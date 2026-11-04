using System.Net;
using System.Net.Http.Json;
using Dapper;
using Npgsql;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class MatchingToleranceTests(ApiFixture fixture)
{
    [Theory]
    [InlineData(1.00, HttpStatusCode.OK)]
    [InlineData(1.01, HttpStatusCode.BadRequest)]
    public async Task Company_tolerance_is_limited_to_one_currency_unit(decimal amountTolerance, HttpStatusCode expected)
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        using var client = await fixture.LoginAsync(user.Email, company);
        using var response = await client.PatchAsJsonAsync($"/companies/{company}/settings", new { amountTolerance }, TestContext.Current.CancellationToken);
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.BadRequest) Assert.Contains("1.00", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Existing_settings_fit_the_cap_and_direct_database_writes_cannot_exceed_it()
    {
        var (_, company) = await fixture.SeedCompanyWithAdminAsync();
        await using var owner = await fixture.Database.OpenOwnerAsync();
        Assert.Equal(0, await owner.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM company WHERE amount_tolerance>1.00", cancellationToken: TestContext.Current.CancellationToken)));
        var error = await Assert.ThrowsAsync<PostgresException>(() => owner.ExecuteAsync(new CommandDefinition("UPDATE company SET amount_tolerance=1.01 WHERE id=@company", new { company }, cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }
}
