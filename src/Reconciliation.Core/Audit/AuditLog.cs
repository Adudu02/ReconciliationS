using System.Text.Json;
using Dapper;
using Npgsql;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Audit;

public static class AuditLog
{
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
