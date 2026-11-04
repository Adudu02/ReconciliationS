using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class MovementEndpointsTests(ApiFixture fixture)
{
    [Fact(DisplayName = "Loan deposit classified")]
    public async Task Classification_leaves_pending_list_keeps_derived_state_and_clear_reopens_with_user_audit()
    {
        var seed = await new MatchingModelTests(fixture).SeedAsync();
        await using var owner = await fixture.Database.OpenOwnerAsync();
        var email = await owner.ExecuteScalarAsync<string>(new CommandDefinition("SELECT email FROM app_user WHERE id=@actor", new { actor = seed.Actor }, cancellationToken: TestContext.Current.CancellationToken));
        using var client = await fixture.LoginAsync(email!, seed.Company);
        using var classified = await client.PatchAsJsonAsync($"/companies/{seed.Company}/movements/{seed.Movement}", new { classification = "loan", note = "Synthetic loan from partner" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, classified.StatusCode);
        var pending = await client.GetFromJsonAsync<JsonElement>($"/companies/{seed.Company}/movements?state=pending", TestContext.Current.CancellationToken);
        Assert.Equal(seed.OtherMovement, Assert.Single(pending.GetProperty("movements").EnumerateArray()).GetProperty("id").GetGuid());
        var loans = await client.GetFromJsonAsync<JsonElement>($"/companies/{seed.Company}/movements?classification=loan", TestContext.Current.CancellationToken);
        var loan = Assert.Single(loans.GetProperty("movements").EnumerateArray());
        Assert.Equal("pending", loan.GetProperty("state").GetString());
        Assert.Equal("loan", loan.GetProperty("classification").GetString());
        Assert.Equal("Synthetic loan from partner", loan.GetProperty("reason").GetString());
        using var cleared = await client.PatchAsJsonAsync($"/companies/{seed.Company}/movements/{seed.Movement}", new { classification = (string?)null }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        var reopened = await client.GetFromJsonAsync<JsonElement>($"/companies/{seed.Company}/movements?state=pending", TestContext.Current.CancellationToken);
        Assert.Equal(2, reopened.GetProperty("movements").GetArrayLength());
        var entries = (await owner.QueryAsync<(Guid Actor, string Items, string Detail)>(new CommandDefinition("SELECT actor_id,items::text,detail::text FROM audit_entry WHERE company_id=@company AND action='movement.classification_changed' ORDER BY company_seq", new { company = seed.Company }, cancellationToken: TestContext.Current.CancellationToken))).ToArray();
        Assert.Equal(2, entries.Length);
        Assert.All(entries, entry =>
        {
            Assert.Equal(seed.Actor, entry.Actor);
            Assert.Contains(JsonSerializer.Deserialize<JsonElement>(entry.Items).EnumerateArray(), item => item.GetProperty("kind").GetString() == "movement" && item.GetProperty("id").GetGuid() == seed.Movement);
        });
        Assert.Equal("loan", JsonSerializer.Deserialize<JsonElement>(entries[1].Detail).GetProperty("old").GetProperty("classification").GetString());
        Assert.Equal(JsonValueKind.Null, JsonSerializer.Deserialize<JsonElement>(entries[1].Detail).GetProperty("new").GetProperty("classification").ValueKind);
    }

    [Theory]
    [InlineData("viewer", HttpStatusCode.Forbidden)]
    [InlineData("accountant", HttpStatusCode.NoContent)]
    [InlineData("administrator", HttpStatusCode.NoContent)]
    public async Task Every_role_reads_and_only_accountant_or_admin_classifies(string role, HttpStatusCode expected)
    {
        var seed = await new MatchingModelTests(fixture).SeedAsync();
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync(new CommandDefinition("UPDATE membership SET role=@role::membership_role WHERE company_id=@company", new { role, company = seed.Company }, cancellationToken: TestContext.Current.CancellationToken));
        var email = await owner.ExecuteScalarAsync<string>(new CommandDefinition("SELECT email FROM app_user WHERE id=@actor", new { actor = seed.Actor }, cancellationToken: TestContext.Current.CancellationToken));
        using var client = await fixture.LoginAsync(email!, seed.Company);
        using var read = await client.GetAsync($"/companies/{seed.Company}/movements", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var patch = await client.PatchAsJsonAsync($"/companies/{seed.Company}/movements/{seed.Movement}", new { classification = "partner_contribution" }, TestContext.Current.CancellationToken);
        Assert.Equal(expected, patch.StatusCode);
        Assert.Equal(expected == HttpStatusCode.NoContent ? 1 : 0, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM audit_entry WHERE company_id=@company AND action='movement.classification_changed'", new { company = seed.Company }));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"classification\":\"invalid\"}")]
    [InlineData("{\"classification\":1}")]
    [InlineData("{\"classification\":\"loan\",\"unexpected\":true}")]
    public async Task Classification_requires_an_explicit_supported_value_or_null(string json)
    {
        var seed = await new MatchingModelTests(fixture).SeedAsync();
        var email = await fixture.Database.ScalarAsync<string>("SELECT email FROM app_user WHERE id=@actor", new { actor = seed.Actor });
        using var client = await fixture.LoginAsync(email!, seed.Company);
        using var body = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PatchAsync($"/companies/{seed.Company}/movements/{seed.Movement}", body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM audit_entry WHERE company_id=@company AND action='movement.classification_changed'", new { company = seed.Company }));
    }

    [Fact]
    public async Task Archived_company_remains_readable_but_classification_is_refused()
    {
        var seed = await new MatchingModelTests(fixture).SeedAsync();
        var email = await fixture.Database.ScalarAsync<string>("SELECT email FROM app_user WHERE id=@actor", new { actor = seed.Actor });
        using var client = await fixture.LoginAsync(email!, seed.Company);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync(new CommandDefinition("UPDATE company SET archived_at=now() WHERE id=@company", new { company = seed.Company }, cancellationToken: TestContext.Current.CancellationToken));
        using var read = await client.GetAsync($"/companies/{seed.Company}/movements", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var patch = await client.PatchAsJsonAsync($"/companies/{seed.Company}/movements/{seed.Movement}", new { classification = "other" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, patch.StatusCode);
    }

    [Fact]
    public async Task Keyset_paging_is_chronological_stable_and_never_reveals_another_companys_movements()
    {
        var seed = await new MatchingModelTests(fixture).SeedAsync();
        var foreign = await new MatchingModelTests(fixture).SeedAsync();
        var email = await fixture.Database.ScalarAsync<string>("SELECT email FROM app_user WHERE id=@actor", new { actor = seed.Actor });
        using var client = await fixture.LoginAsync(email!, seed.Company);
        var ids = new List<Guid>();
        var path = $"/companies/{seed.Company}/movements?limit=1";
        while (true)
        {
            var page = await client.GetFromJsonAsync<JsonElement>(path, TestContext.Current.CancellationToken);
            ids.Add(Assert.Single(page.GetProperty("movements").EnumerateArray()).GetProperty("id").GetGuid());
            if (page.GetProperty("nextAfterId").ValueKind == JsonValueKind.Null) break;
            path = $"/companies/{seed.Company}/movements?limit=1&afterDate={page.GetProperty("nextAfterDate").GetString()}&afterId={page.GetProperty("nextAfterId").GetGuid()}";
        }
        Assert.Equal(new[] { seed.Movement, seed.OtherMovement }, ids);
        using var denied = await client.PatchAsJsonAsync($"/companies/{seed.Company}/movements/{foreign.Movement}", new { classification = "tax_payment" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Null(await fixture.Database.ScalarAsync<string>("SELECT classification FROM bank_movement WHERE id=@id", new { id = foreign.Movement }));
        foreach (var query in new[] { "limit=201", "state=invalid", "classification=invalid", "afterDate=2026-10-10" })
        {
            using var invalid = await client.GetAsync($"/companies/{seed.Company}/movements?{query}", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
    }
}
