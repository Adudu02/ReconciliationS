using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Reconciliation.Contracts;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class WebProblemTests(ApiFixture fixture)
{
    [Fact]
    public async Task Decision_validation_and_conflict_are_RFC7807_with_current_state_preserved()
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, application/problem+json");
        await MatchingRunScenario.RunAsync(fixture, client, seed.Company);
        var queue = await client.GetFromJsonAsync<ReviewPage>($"/companies/{seed.Company}/review-queue", TestContext.Current.CancellationToken);
        var link = Assert.Single(queue!.Entries, row => row.Kind == "link").LinkId;
        using var invalid = await client.PostAsJsonAsync($"/companies/{seed.Company}/links/{link}/decisions", new { type = "wrong" }, TestContext.Current.CancellationToken);
        Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var rejected = await client.PostAsJsonAsync($"/companies/{seed.Company}/links/{link}/decisions", new { type = "reject" }, TestContext.Current.CancellationToken);
        rejected.EnsureSuccessStatusCode();
        using var conflict = await client.PostAsJsonAsync($"/companies/{seed.Company}/links/{link}/decisions", new { type = "confirm" }, TestContext.Current.CancellationToken);
        Assert.Equal("application/problem+json", conflict.Content.Headers.ContentType?.MediaType);
        var body = await conflict.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(409, body.GetProperty("status").GetInt32());
        Assert.Equal("rejected", body.GetProperty("state").GetString());
    }
}
