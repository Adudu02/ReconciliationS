using System.Data;
using Dapper;
using Npgsql;
using NpgsqlTypes;

namespace Reconciliation.Core.Ingestion;

internal static class ImportStorage
{
    public static Task<Guid> InsertFileAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, Guid uploadId,
        UploadedFile uploadedFile, string fileKind, string importStatus, string? rejectionReason, int retentionYears, CancellationToken cancellationToken) =>
        connection.QuerySingleAsync<Guid>(new CommandDefinition("""
            INSERT INTO import_file(company_id, upload_id, filename, kind, status, reason, original, sha256, retained_until)
            VALUES (@companyId, @uploadId, @Name, @kind, @status, @reason, @original, @Hash,
              CASE WHEN @status = 'imported' THEN now() + make_interval(years => @retentionYears) ELSE NULL END) RETURNING id
            """, new { companyId, uploadId, uploadedFile.Name, kind = fileKind, status = importStatus, reason = rejectionReason, original = new Bytea(importStatus == "imported" ? uploadedFile.ContentMemory : null), uploadedFile.Hash, retentionYears }, transaction, cancellationToken: cancellationToken));

    public static Task<int> FinalizeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid fileId, UploadedFile uploadedFile,
        int retentionYears, int newMovements, int duplicateMovements, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition("""
            UPDATE import_file SET status='imported', original=@original,
              retained_until=created_at+make_interval(years => @retentionYears), imported_movements=@imported, duplicate_movements=@duplicates
            WHERE id=@fileId AND status='importing'
            """, new { fileId, original = new Bytea(uploadedFile.ContentMemory), retentionYears, imported = newMovements, duplicates = duplicateMovements }, transaction, cancellationToken: cancellationToken));

    public static Task<int> AuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, Guid actorId, string auditAction, Guid fileId, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO audit_entry(company_id, actor_id, action, new_values)
            VALUES (@companyId, @actorId, @action, jsonb_build_object('fileId', @fileId::text))
            """, new { companyId, actorId, action = auditAction, fileId }, transaction, cancellationToken: cancellationToken));

    private sealed class Bytea(ReadOnlyMemory<byte>? bytes) : SqlMapper.ICustomQueryParameter
    {
        public void AddParameter(IDbCommand command, string name) => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Bytea)
        { Value = bytes.HasValue ? bytes.Value : DBNull.Value });
    }
}
