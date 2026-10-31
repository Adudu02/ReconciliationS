using Dapper;
using Npgsql;

namespace Reconciliation.Tests;

public sealed class PostgresFixtureTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Fixture_runs_migrations_and_connects_as_restricted_app_role()
    {
        await using var connection = new NpgsqlConnection(database.AppConnectionString);
        var tables = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'",
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("company", tables);
        Assert.Contains("membership", tables);
        Assert.Contains("app_user", tables);
        var role = await connection.QuerySingleAsync<(string Name, bool Superuser, bool Bypass)>(new CommandDefinition(
            "SELECT rolname, rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user",
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("cfdi_app", role.Name);
        Assert.False(role.Superuser);
        Assert.False(role.Bypass);
    }
}
