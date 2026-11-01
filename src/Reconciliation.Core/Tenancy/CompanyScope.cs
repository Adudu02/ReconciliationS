using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Reconciliation.Core.Tenancy;

public sealed partial class CompanyScope(NpgsqlDataSource dataSource, ILogger<CompanyScope> logger)
{
    public Task<IResult> ExecuteResultAsync(Guid? companyId,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<IResult>> work,
        CancellationToken cancellationToken = default) => ExecuteAsync(companyId, work, cancellationToken);

    public Task<IResult> ExecuteWriteResultAsync(Guid companyId,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<IResult>> work,
        CancellationToken cancellationToken = default) => ExecuteWriteAsync(companyId, work, cancellationToken);

    public static IResult OkOrNotFound<T>(T? resource) => resource is null ? Results.NotFound() : Results.Ok(resource);

    public Task<T> ExecuteAsync<T>(Guid? companyId,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default) => ExecuteTransactionAsync(companyId, work, false, cancellationToken);

    public Task<T> ExecuteWriteAsync<T>(Guid companyId,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default) => ExecuteTransactionAsync(companyId, work, true, cancellationToken);

    private async Task<T> ExecuteTransactionAsync<T>(Guid? companyId,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> work, bool writing,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
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
        var outcome = await work(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return outcome;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database access attempted without company context")]
    private static partial void MissingContext(ILogger logger);
}
