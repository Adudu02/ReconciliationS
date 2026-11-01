using System.Data;
using Dapper;
using Npgsql;
using NpgsqlTypes;
using Reconciliation.Core.Audit;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Ingestion;

internal static class ImportStorage
{
    public static Task<Guid> InsertFileAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, Guid uploadId,
        UploadedFile uploadedFile, ImportFileKind fileKind, ImportStatus importStatus, string? rejectionReason, int retentionYears, CancellationToken cancellationToken) =>
        connection.QuerySingleAsync<Guid>(Command("""
            INSERT INTO import_file(company_id, upload_id, filename, kind, status, reason, original, sha256, retained_until)
            VALUES (@companyId, @uploadId, @Name, @kind, @status, @reason, @original, @Hash,
              CASE WHEN @status = 'imported' THEN now() + make_interval(years => @retentionYears) ELSE NULL END) RETURNING id
            """, new { companyId, uploadId, uploadedFile.Name, kind = fileKind.ToDatabaseValue(), status = importStatus.ToDatabaseValue(), reason = rejectionReason, original = new Bytea(importStatus == ImportStatus.Imported ? uploadedFile.ContentMemory : null), uploadedFile.Hash, retentionYears }, transaction, cancellationToken: cancellationToken));

    public static Task<int> FinalizeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid fileId, UploadedFile uploadedFile,
        int retentionYears, int newMovements, int duplicateMovements, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(Command("""
            UPDATE import_file SET status='imported', original=@original,
              retained_until=created_at+make_interval(years => @retentionYears), imported_movements=@imported, duplicate_movements=@duplicates
            WHERE id=@fileId AND status='importing'
            """, new { fileId, original = new Bytea(uploadedFile.ContentMemory), retentionYears, imported = newMovements, duplicates = duplicateMovements }, transaction, cancellationToken: cancellationToken));

    public static Task<int> AuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, Guid actorId, string auditAction, Guid fileId, CancellationToken cancellationToken) =>
        AuditLog.WriteAsync(connection, transaction, companyId, actorId, auditAction, null, new { fileId }, cancellationToken);

    private sealed class Bytea(ReadOnlyMemory<byte>? bytes) : SqlMapper.ICustomQueryParameter
    {
        public void AddParameter(IDbCommand command, string name) => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Bytea)
        { Value = bytes.HasValue ? bytes.Value : DBNull.Value });
    }
}
