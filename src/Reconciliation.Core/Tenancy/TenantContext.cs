using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Reconciliation.Core.Tenancy;

public sealed partial class TenantContext(NpgsqlDataSource dataSource, ILogger<TenantContext> logger)
{
    public async Task<T> ExecuteAsync<T>(Guid? companyId,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (companyId is null) MissingContext(logger);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT set_config('app.company_id', @company, true)", new { company = companyId?.ToString() ?? "" },
            transaction, cancellationToken: cancellationToken));
        var result = await action(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public Task<T> ExecuteWriteAsync<T>(Guid companyId,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(companyId, async (connection, transaction, token) =>
        {
            var archived = await connection.QuerySingleAsync<bool>(new CommandDefinition(
                "SELECT archived_at IS NOT NULL FROM company FOR UPDATE", transaction: transaction, cancellationToken: token));
            if (archived) throw new CompanyReadOnlyException();
            return await action(connection, transaction, token);
        }, cancellationToken);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database access attempted without company context")]
    private static partial void MissingContext(ILogger logger);
}
