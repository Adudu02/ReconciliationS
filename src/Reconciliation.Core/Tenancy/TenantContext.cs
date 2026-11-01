using Microsoft.Extensions.Logging;
using Npgsql;

namespace Reconciliation.Core.Tenancy;

public sealed partial class TenantContext(NpgsqlDataSource dataSource, ILogger<TenantContext> logger)
{
    public Task<T> ExecuteAsync<T>(Guid? companyId,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default) => ExecuteTransactionAsync(companyId, action, false, cancellationToken);

    public Task<T> ExecuteWriteAsync<T>(Guid companyId,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default) => ExecuteTransactionAsync(companyId, action, true, cancellationToken);

    private async Task<T> ExecuteTransactionAsync<T>(Guid? companyId,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> action, bool writing,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (companyId is null) MissingContext(logger);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var batch = new NpgsqlBatch(connection, transaction))
        {
            var context = new NpgsqlBatchCommand("SELECT set_config('app.company_id', @company, true)");
            context.Parameters.AddWithValue("company", companyId?.ToString() ?? "");
            batch.BatchCommands.Add(context);
            if (writing) batch.BatchCommands.Add(new NpgsqlBatchCommand("SELECT archived_at IS NOT NULL FROM company FOR UPDATE"));
            await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
            if (writing)
            {
                await reader.NextResultAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Company not found.");
                if (reader.GetBoolean(0)) throw new CompanyReadOnlyException();
            }
        }
        var result = await action(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database access attempted without company context")]
    private static partial void MissingContext(ILogger logger);
}
