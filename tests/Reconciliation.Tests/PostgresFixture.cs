using Dapper;
using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Reconciliation.Migrator.Migrations;
using Testcontainers.PostgreSql;

namespace Reconciliation.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public string OwnerConnectionString => container.GetConnectionString();
    public string AppConnectionString => new NpgsqlConnectionStringBuilder(OwnerConnectionString)
    {
        Username = "cfdi_app",
        Password = "synthetic-test-password"
    }.ConnectionString;

    public async Task<NpgsqlConnection> OpenOwnerAsync()
    {
        var connection = new NpgsqlConnection(OwnerConnectionString);
        try { await connection.OpenAsync(TestContext.Current.CancellationToken); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task<T?> ScalarAsync<T>(string sql, object? parameters = null)
    {
        await using var connection = await OpenOwnerAsync();
        return await connection.ExecuteScalarAsync<T>(new CommandDefinition(sql, parameters, cancellationToken: TestContext.Current.CancellationToken));
    }

    public async ValueTask InitializeAsync()
    {
        await container.StartAsync(TestContext.Current.CancellationToken);
        using var provider = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(runner => runner.AddPostgres().WithGlobalConnectionString(OwnerConnectionString)
                .ScanIn(typeof(M0001CompanyUserMembership).Assembly).For.Migrations())
            .AddLogging()
            .BuildServiceProvider(validateScopes: false);
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
        await using var connection = new NpgsqlConnection(OwnerConnectionString);
        await connection.ExecuteAsync(new CommandDefinition("""
            ALTER ROLE cfdi_app LOGIN PASSWORD 'synthetic-test-password' NOSUPERUSER NOBYPASSRLS;
            """, cancellationToken: TestContext.Current.CancellationToken));
    }

    public ValueTask DisposeAsync() => container.DisposeAsync();
}
