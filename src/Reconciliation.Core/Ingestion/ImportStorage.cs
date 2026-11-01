using Dapper;
using Npgsql;

namespace Reconciliation.Core.Ingestion;

internal static class ImportStorage
{
    public static Task<Guid> InsertFileAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, Guid uploadId,
        UploadedFile file, string kind, string status, string? reason, int retentionYears, CancellationToken ct) =>
        connection.QuerySingleAsync<Guid>(new CommandDefinition("""
            INSERT INTO import_file(company_id, upload_id, filename, kind, status, reason, original, sha256, retained_until)
            VALUES (@companyId, @uploadId, @Name, @kind, @status, @reason, @original, @Hash,
              CASE WHEN @status = 'imported' THEN now() + make_interval(years => @retentionYears) ELSE NULL END) RETURNING id
            """, new { companyId, uploadId, file.Name, kind, status, reason, original = status == "imported" ? file.Content : null, file.Hash, retentionYears }, transaction, cancellationToken: ct));

    public static Task<int> AuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, Guid actorId, string action, Guid fileId, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO audit_entry(company_id, actor_id, action, new_values)
            VALUES (@companyId, @actorId, @action, jsonb_build_object('fileId', @fileId::text))
            """, new { companyId, actorId, action, fileId }, transaction, cancellationToken: ct));
}
