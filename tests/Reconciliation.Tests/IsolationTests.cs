using Dapper;
using Npgsql;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class IsolationTests(ApiFixture fixture)
{
    [Fact]
    public async Task Every_tenant_table_has_forced_RLS_and_a_policy()
    {
        await using var owner = new NpgsqlConnection(fixture.Database.OwnerConnectionString);
        await TenantIsolationChecks.VerifyTablesAsync(owner);
    }

    [Fact]
    public async Task Every_data_endpoint_requires_authentication_and_is_checked_for_cross_company_access()
    {
        await TenantIsolationChecks.VerifyRoutesAsync(fixture);
    }

    [Fact]
    public async Task App_role_cannot_read_any_tenant_table_without_context()
    {
        var user = await fixture.SeedUserAsync();
        await fixture.SeedCompanyAsync(user.Id);
        await using var owner = new NpgsqlConnection(fixture.Database.OwnerConnectionString);
        var tables = await TenantIsolationChecks.TenantTablesAsync(owner);
        await using var app = new NpgsqlConnection(fixture.Database.AppConnectionString);
        foreach (var table in tables)
        {
            // Identifiers come only from PostgreSQL's catalog and are quoted as identifiers.
            using var builder = new NpgsqlCommandBuilder();
            var quoted = builder.QuoteIdentifier(table);
            Assert.Equal(0, await app.ExecuteScalarAsync<int>(new CommandDefinition($"SELECT count(*) FROM {quoted}", cancellationToken: TestContext.Current.CancellationToken)));
        }
    }
}
