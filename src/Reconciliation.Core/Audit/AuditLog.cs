using System.Text.Json;
using Dapper;
using Npgsql;
using Reconciliation.Core.Tenancy;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Audit;

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

    public static Task<int> WriteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, Guid actorId,
        string auditAction, object? previousValues, object currentValues, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(Command("""
            SELECT append_audit_entry(@companyId,'user',@actorId,@auditAction,'{}'::jsonb,
              jsonb_build_object('old',@oldValues::jsonb,'new',@newValues::jsonb))
            """, new
        {
            companyId,
            actorId,
            auditAction,
            oldValues = previousValues is null ? null : JsonSerializer.Serialize(previousValues, JsonSerializerOptions.Web),
            newValues = JsonSerializer.Serialize(currentValues, JsonSerializerOptions.Web)
        }, transaction, cancellationToken: cancellationToken));
}
