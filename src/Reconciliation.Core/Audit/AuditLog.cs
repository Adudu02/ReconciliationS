using System.Text.Json;
using Dapper;
using Npgsql;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Audit;

public static class AuditLog
{
    // shortcut: transactional audit only; group 5 adds hash chaining and rejection triggers.
    public static Task<int> WriteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, Guid actorId,
        string auditAction, object? previousValues, object currentValues, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(Command("""
            INSERT INTO audit_entry(company_id, actor_id, action, old_values, new_values)
            VALUES (@companyId, @actorId, @auditAction, @oldValues::jsonb, @newValues::jsonb)
            """, new
        {
            companyId,
            actorId,
            auditAction,
            oldValues = previousValues is null ? null : JsonSerializer.Serialize(previousValues, JsonSerializerOptions.Web),
            newValues = JsonSerializer.Serialize(currentValues, JsonSerializerOptions.Web)
        }, transaction, cancellationToken: cancellationToken));
}
