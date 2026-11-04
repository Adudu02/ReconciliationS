using System.Data;
using Dapper;
using Npgsql;
using NpgsqlTypes;
using Reconciliation.Core.Audit;
using Reconciliation.Core.Jobs;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Ingestion;

internal static class ImportFileStore
{
    public static async Task<Guid> InsertFileAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, Guid uploadId,
        UploadedFile uploadedFile, ImportFileKind fileKind, ImportStatus importStatus, string? rejectionReason, int retentionYears, CancellationToken cancellationToken)
    {
        if (uploadedFile.StoredFileId is Guid storedFileId)
        {
            await connection.ExecuteAsync(Command("""
                UPDATE import_file SET status=@status,reason=@rejectionReason,
                  pending_original=CASE WHEN @status='rejected' THEN NULL ELSE pending_original END
                WHERE id=@storedFileId AND status IN ('queued','processing')
                """, new { storedFileId, status = importStatus.ToDatabaseValue(), rejectionReason }, transaction, cancellationToken: cancellationToken));
            return storedFileId;
        }
        return await connection.QuerySingleAsync<Guid>(Command("""
            INSERT INTO import_file(company_id, upload_id, filename, kind, status, reason, original, sha256, retained_until)
            VALUES (@companyId, @uploadId, @Name, @kind, @status, @reason, @original, @Hash,
              CASE WHEN @status = 'imported' THEN now() + make_interval(years => @retentionYears) ELSE NULL END) RETURNING id
            """, new { companyId, uploadId, uploadedFile.Name, kind = fileKind.ToDatabaseValue(), status = importStatus.ToDatabaseValue(), reason = rejectionReason, original = new Bytea(importStatus == ImportStatus.Imported ? uploadedFile.ContentMemory : null), uploadedFile.Hash, retentionYears }, transaction, cancellationToken: cancellationToken));
    }

    public static Task<int> FinalizeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid fileId, UploadedFile uploadedFile,
        int retentionYears, int newMovements, int duplicateMovements, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(Command("""
            UPDATE import_file SET status='imported', original=coalesce(pending_original,@original),pending_original=NULL,
              retained_until=coalesce(@acceptedAt::timestamptz,created_at)+make_interval(years => @retentionYears), imported_movements=@imported, duplicate_movements=@duplicates
            WHERE id=@fileId AND status='processing'
            """, new { fileId, original = new Bytea(uploadedFile.ContentMemory), retentionYears, acceptedAt = uploadedFile.Lease?.UtcNow, imported = newMovements, duplicates = duplicateMovements }, transaction, cancellationToken: cancellationToken));

    public static async Task<FileImportOutcome?> CompletedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        UploadedFile file, CancellationToken cancellationToken)
    {
        if (file.StoredFileId is null) return null;
        if (file.Lease is not null) await file.Lease.CheckAsync(connection, transaction, cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<FileImportOutcome>(Command("""
            SELECT f.id AS FileId,f.filename AS Name,f.status,f.reason,c.id AS CfdiId,
              f.imported_movements AS ImportedMovements,f.duplicate_movements AS DuplicateMovements
            FROM import_file f LEFT JOIN cfdi c ON c.import_file_id=coalesce(f.original_file_id,f.id)
            WHERE f.id=@StoredFileId AND f.status NOT IN ('queued','processing')
            """, new { file.StoredFileId }, transaction, cancellationToken: cancellationToken));
    }

    public static Task<Guid> QueueAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId,
        Guid uploadId, UploadedFile file, string kind, int fileIndex, int maxFileBytes, DateTimeOffset now, CancellationToken cancellationToken) =>
        connection.QuerySingleAsync<Guid>(Command("""
            INSERT INTO import_file(company_id,upload_id,filename,kind,status,reason,pending_original,sha256,file_index,created_at)
            VALUES(@companyId,@uploadId,@Name,@kind,'queued',@error,@bytes,@Hash,@fileIndex,@now) RETURNING id
            """, new
        {
            companyId,
            uploadId,
            file.Name,
            kind,
            file.Hash,
            fileIndex,
            now,
            error = file.Error ?? (file.ContentLength > maxFileBytes ? UploadErrors.FileLimit : null),
            bytes = new Bytea(file.Error is null && file.ContentLength <= maxFileBytes ? file.ContentMemory : null)
        }, transaction, cancellationToken: cancellationToken));

    public static Task<int> AuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, Guid actorId, string auditAction, Guid fileId, CancellationToken cancellationToken,
        Guid? cfdiUuid = null, IReadOnlyList<Guid>? movementIds = null, JobLease? lease = null)
    {
        var items = new List<AuditItem> { new("import_file", fileId.ToString()) };
        if (cfdiUuid.HasValue) items.Add(new("cfdi", cfdiUuid.Value.ToString()));
        if (movementIds is not null) items.AddRange(movementIds.Select(id => new AuditItem("movement", id.ToString())));
        if (lease is not null) items.Add(new("job", lease.JobId.ToString()));
        return AuditLog.WriteAsync(connection, transaction, companyId, actorId, auditAction, null,
            new { fileId, jobId = lease?.JobId, source = lease?.WorkerId }, cancellationToken, items);
    }

    private sealed class Bytea(ReadOnlyMemory<byte>? bytes) : SqlMapper.ICustomQueryParameter
    {
        public void AddParameter(IDbCommand command, string name) => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Bytea)
        { Value = bytes.HasValue ? bytes.Value : DBNull.Value });
    }
}
