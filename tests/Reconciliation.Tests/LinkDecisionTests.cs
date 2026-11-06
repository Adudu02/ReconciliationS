using System.Net;
using System.Net.Http.Json;
using Dapper;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class LinkDecisionTests(ApiFixture fixture)
{
    [Fact]
    public async Task Decision_table_has_forced_RLS_and_minimal_append_only_grants()
    {
        await using var owner = await fixture.Database.OpenOwnerAsync();
        Assert.True(await owner.ExecuteScalarAsync<bool>("SELECT coalesce((SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid=to_regclass('link_decision')),false)"));
        Assert.True(await owner.ExecuteScalarAsync<bool>("SELECT has_table_privilege('cfdi_app','link_decision','SELECT,INSERT')"));
        Assert.False(await owner.ExecuteScalarAsync<bool>("SELECT has_table_privilege('cfdi_app','link_decision','UPDATE') OR has_table_privilege('cfdi_app','link_decision','DELETE')"));
    }

    [Theory]
    [InlineData("confirm", "reconciled")]
    [InlineData("reject", "rejected")]
    public async Task Decision_records_actor_and_one_audit_atomically_and_refuses_repeat(string type, string expected)
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        await MatchingRunScenario.RunAsync(fixture, client, seed.Company);
        var link = await fixture.Database.ScalarAsync<Guid>("SELECT id FROM link WHERE company_id=@company", new { company = seed.Company });
        var before = await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM audit_entry WHERE company_id=@company", new { company = seed.Company });
        var path = $"/companies/{seed.Company}/links/{link}/decisions";
        using var response = await client.PostAsJsonAsync(path, new { type, note = "synthetic private note" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await fixture.Database.ScalarAsync<string>("SELECT state FROM link WHERE id=@link", new { link }));
        Assert.Equal(seed.Actor, await fixture.Database.ScalarAsync<Guid>("SELECT decided_by FROM link_decision WHERE link_id=@link", new { link }));
        Assert.Equal(before + 1, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM audit_entry WHERE company_id=@company", new { company = seed.Company }));
        using var repeat = await client.PostAsJsonAsync(path, new { type }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);
    }

    [Fact]
    public async Task Concurrent_decisions_have_exactly_one_winner()
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        using var first = await fixture.LoginAsync(seed.Email, seed.Company);
        using var second = await fixture.LoginAsync(seed.Email, seed.Company);
        await MatchingRunScenario.RunAsync(fixture, first, seed.Company);
        var link = await fixture.Database.ScalarAsync<Guid>("SELECT id FROM link WHERE company_id=@company", new { company = seed.Company });
        var path = $"/companies/{seed.Company}/links/{link}/decisions";
        var responses = await Task.WhenAll(first.PostAsJsonAsync(path, new { type = "confirm" }, TestContext.Current.CancellationToken), second.PostAsJsonAsync(path, new { type = "reject" }, TestContext.Current.CancellationToken));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }
}
