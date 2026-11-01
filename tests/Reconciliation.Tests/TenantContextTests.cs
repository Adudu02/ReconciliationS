using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Reconciliation.Core.Tenancy;

namespace Reconciliation.Tests;

public sealed class TenantContextTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private sealed class ContextLog : ILogger<TenantContext>
    {
        public int Warnings { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings++;
        }
    }

    [Fact]
    public async Task Context_absent_returns_no_rows_and_logs_attempt()
    {
        var a = await SeedCompanyAsync();
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var log = new ContextLog();
        var context = new TenantContext(source, log);
        var rows = await context.ExecuteAsync(null, (connection, transaction, transactionCancellationToken) =>
            connection.QueryAsync<Guid>(new CommandDefinition("SELECT id FROM company", transaction: transaction, cancellationToken: transactionCancellationToken)), TestContext.Current.CancellationToken);
        Assert.Empty(rows);
        Assert.Equal(1, log.Warnings);
        Assert.NotEqual(Guid.Empty, a);
    }

    [Fact]
    public async Task Missing_filter_returns_only_context_company_and_cannot_write_other_company()
    {
        var a = await SeedCompanyAsync();
        var b = await SeedCompanyAsync();
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = new TenantContext(source, new ContextLog());
        var rows = await context.ExecuteAsync(a, (connection, transaction, transactionCancellationToken) =>
            connection.QueryAsync<Guid>(new CommandDefinition("SELECT id FROM company", transaction: transaction, cancellationToken: transactionCancellationToken)), TestContext.Current.CancellationToken);
        Assert.Equal([a], rows);
        var changed = await context.ExecuteAsync(a, (connection, transaction, transactionCancellationToken) => connection.ExecuteAsync(new CommandDefinition(
            "UPDATE company SET name = 'cross-tenant' WHERE id = @b", new { b }, transaction, cancellationToken: transactionCancellationToken)), TestContext.Current.CancellationToken);
        Assert.Equal(0, changed);
        await Assert.ThrowsAsync<PostgresException>(() => context.ExecuteAsync(a, (connection, transaction, transactionCancellationToken) => connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO membership(company_id, user_id, role) VALUES (@b, gen_random_uuid(), 'viewer')", new { b }, transaction, cancellationToken: transactionCancellationToken)), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Context_is_local_to_transaction_after_commit_and_rollback()
    {
        var a = await SeedCompanyAsync();
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString + ";Maximum Pool Size=1");
        var context = new TenantContext(source, new ContextLog());
        await context.ExecuteAsync(a, (connection, transaction, transactionCancellationToken) => connection.ExecuteAsync(new CommandDefinition(
            "UPDATE company SET name = 'committed'", transaction: transaction, cancellationToken: transactionCancellationToken)), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.ExecuteAsync<int>(a, async (connection, transaction, transactionCancellationToken) =>
        {
            await connection.ExecuteAsync(new CommandDefinition("UPDATE company SET name = 'rolled back'", transaction: transaction, cancellationToken: transactionCancellationToken));
            throw new InvalidOperationException("synthetic rollback");
        }, TestContext.Current.CancellationToken));
        await using var connection = await source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM company", cancellationToken: TestContext.Current.CancellationToken)));
        await using var owner = await database.OpenOwnerAsync();
        Assert.Equal("committed", await owner.QuerySingleAsync<string>(new CommandDefinition("SELECT name FROM company WHERE id = @a", new { a }, cancellationToken: TestContext.Current.CancellationToken)));
    }

    private async Task<Guid> SeedCompanyAsync()
    {
        await using var owner = await database.OpenOwnerAsync();
        return await owner.QuerySingleAsync<Guid>(new CommandDefinition(
            "INSERT INTO company(rfc, name) VALUES ('AAA010101AAA', 'Synthetic company') RETURNING id", cancellationToken: TestContext.Current.CancellationToken));
    }
}
