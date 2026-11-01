using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class MembershipTests(ApiFixture fixture)
{
    [Fact]
    public async Task Viewer_cannot_decide_policy_probe()
    {
        var user = await fixture.SeedUserAsync();
        var company = await fixture.SeedCompanyAsync(user.Id, "viewer");
        using var client = await fixture.LoginAsync(user.Email, company);
        using var response = await client.PostAsync("/test/decisions", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Accountant_cannot_manage_users_but_administrator_can()
    {
        var user = await fixture.SeedUserAsync();
        var added = await fixture.SeedUserAsync();
        var company = await fixture.SeedCompanyAsync(user.Id, "accountant");
        using var client = await fixture.LoginAsync(user.Email, company);
        using var denied = await client.PostAsJsonAsync($"/companies/{company}/members", new { userId = added.Id, role = "viewer" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        Assert.Equal(0, await owner.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM membership WHERE company_id = @company AND user_id = @Id", new { company, added.Id }, cancellationToken: TestContext.Current.CancellationToken)));
        await owner.ExecuteAsync(new CommandDefinition("UPDATE membership SET role = 'administrator' WHERE company_id = @company AND user_id = @Id", new { company, user.Id }, cancellationToken: TestContext.Current.CancellationToken));
        using var accepted = await client.PostAsJsonAsync($"/companies/{company}/members", new { userId = added.Id, role = "viewer" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        using var members = await client.GetAsync($"/companies/{company}/members", TestContext.Current.CancellationToken);
        Assert.Contains(added.Id.ToString(), await members.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Different_roles_are_selected_per_request_with_same_token()
    {
        var user = await fixture.SeedUserAsync();
        var a = await fixture.SeedCompanyAsync(user.Id, "accountant");
        var b = await fixture.SeedCompanyAsync(user.Id, "viewer");
        using var client = await fixture.LoginAsync(user.Email, a);
        using var allowed = await client.PostAsync("/test/decisions", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        client.DefaultRequestHeaders.Remove("X-Company-Id");
        client.DefaultRequestHeaders.Add("X-Company-Id", b.ToString());
        using var denied = await client.PostAsync("/test/decisions", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var read = await client.GetAsync($"/companies/{b}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var json = await read.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(b, json.GetProperty("id").GetGuid());
        using var mismatch = await client.GetAsync($"/companies/{a}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, mismatch.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("malformed")]
    [InlineData("00000000-0000-0000-0000-000000000001")]
    public async Task Missing_invalid_or_nonmember_company_context_is_refused(string? selected)
    {
        var (user, a) = await fixture.SeedCompanyWithAdminAsync();
        using var client = await fixture.LoginAsync(user.Email);
        if (selected is not null) client.DefaultRequestHeaders.Add("X-Company-Id", selected);
        using var denied = await client.GetAsync($"/companies/{a}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }
}
