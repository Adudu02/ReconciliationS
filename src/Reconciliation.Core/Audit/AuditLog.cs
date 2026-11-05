using System.Text.Json;
using Dapper;
using Npgsql;
using Reconciliation.Core.Tenancy;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Audit;

public sealed record AuditItem(string Kind, string Id);

public static class AuditLog
{
    internal static Task RecordModificationRefusedAsync(CompanyScope scope, Guid companyId, Guid actorId, long targetSeq,
        PostgresException refusal, CancellationToken cancellationToken)
    {
        if (!((refusal.SqlState == PostgresErrorCodes.RaiseException && refusal.MessageText == "Audit entries are append-only") ||
            (refusal.SqlState == PostgresErrorCodes.InsufficientPrivilege && refusal.MessageText == "permission denied for table audit_entry")))
            throw new ArgumentException("Only a refused audit mutation may be recorded.", nameof(refusal));
        return scope.ExecuteAsync(companyId, async (connection, transaction, token) =>
        {
            if (!await connection.ExecuteScalarAsync<bool>(Command("SELECT EXISTS(SELECT FROM audit_entry WHERE company_seq=@targetSeq)",
                new { targetSeq }, transaction, cancellationToken: token)))
                throw new ArgumentException("Target audit entry is not visible in this company.", nameof(targetSeq));
            return await WriteAsync(connection, transaction, companyId, actorId, "audit.modification_refused", null, new { targetSeq }, token);
        }, cancellationToken);
    }

    internal static Task<int> WriteSystemAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, Guid jobId,
        string source, string auditAction, object? previousValues, object currentValues, IReadOnlyList<AuditItem> items, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(Command("""
            SELECT append_audit_entry(@companyId,'system',NULL,@auditAction,@items::jsonb,@detail::jsonb)
            """, new
        {
            companyId,
            auditAction,
            items = JsonSerializer.Serialize(items, JsonSerializerOptions.Web),
            detail = JsonSerializer.Serialize(new { jobId, source, old = previousValues, @new = currentValues }, JsonSerializerOptions.Web)
        }, transaction, cancellationToken: cancellationToken));

    public static Task<int> WriteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, Guid actorId,
        string auditAction, object? previousValues, object currentValues, CancellationToken cancellationToken, IReadOnlyList<AuditItem>? items = null) =>
        connection.ExecuteAsync(Command("""
            SELECT append_audit_entry(@companyId,'user',@actorId,@auditAction,@items::jsonb,
              jsonb_build_object('old',@oldValues::jsonb,'new',@newValues::jsonb))
            """, new
        {
            companyId,
            actorId,
            auditAction,
            items = JsonSerializer.Serialize(items ?? [], JsonSerializerOptions.Web),
            oldValues = previousValues is null ? null : JsonSerializer.Serialize(previousValues, JsonSerializerOptions.Web),
            newValues = JsonSerializer.Serialize(currentValues, JsonSerializerOptions.Web)
        }, transaction, cancellationToken: cancellationToken));
}
