using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Reconciliation.Contracts;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class WebErrorNegotiationTests(ApiFixture fixture)
{
    private static readonly string?[] Accepts = [null, "*/*", "application/json, application/problem+json"];

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    public async Task Web_errors_are_negotiated_and_legacy_bodies_stay_byte_for_byte(int status)
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        using var admin = await fixture.LoginAsync(seed.Email, seed.Company);
        await MatchingRunScenario.RunAsync(fixture, admin, seed.Company);
        var page = await admin.GetFromJsonAsync<ReviewPage>($"/companies/{seed.Company}/review-queue", TestContext.Current.CancellationToken);
        var link = Assert.Single(page!.Entries, entry => entry.Kind == "link").LinkId;
        var viewer = await fixture.SeedUserAsync();
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync(new CommandDefinition("INSERT INTO membership(company_id,user_id,role) VALUES(@Company,@Id,'viewer')", new { seed.Company, viewer.Id }, cancellationToken: TestContext.Current.CancellationToken));
        using var readOnly = await fixture.LoginAsync(viewer.Email, seed.Company);
        using var anonymous = fixture.App.CreateClient();
        if (status == 409)
        {
            using var rejected = await admin.PostAsJsonAsync($"/companies/{seed.Company}/links/{link}/decisions", new { type = "reject" }, TestContext.Current.CancellationToken);
            rejected.EnsureSuccessStatusCode();
        }
        foreach (var accept in Accepts)
        {
            var client = status == 401 ? anonymous : status == 403 ? readOnly : admin;
            client.DefaultRequestHeaders.Accept.Clear();
            if (accept is not null) client.DefaultRequestHeaders.Accept.ParseAdd(accept);
            using var response = status == 404
                ? await client.GetAsync($"/companies/{seed.Company}/cfdi/{Guid.NewGuid()}/summary", TestContext.Current.CancellationToken)
                : await client.PostAsJsonAsync($"/companies/{seed.Company}/links/{link}/decisions", new { type = status == 400 ? "invalid" : "confirm" }, TestContext.Current.CancellationToken);
            Assert.Equal((HttpStatusCode)status, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            if (accept?.Contains("application/problem+json", StringComparison.Ordinal) == true)
            {
                Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
                using var json = JsonDocument.Parse(body);
                Assert.Equal(status, json.RootElement.GetProperty("status").GetInt32());
                if (status == 409) Assert.Equal("rejected", json.RootElement.GetProperty("state").GetString());
            }
            else Assert.Equal(status == 409 ? "{\"state\":\"rejected\"}" : "", body);
        }
    }

    [Fact]
    public async Task OpenAPI_lists_legacy_JSON_and_problem_JSON_error_media_types()
    {
        using var client = fixture.App.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi.json", TestContext.Current.CancellationToken));
        var responses = document.RootElement.GetProperty("paths").GetProperty("/companies/{companyId}/links/{linkId}/decisions").GetProperty("post").GetProperty("responses");
        foreach (var status in new[] { "400", "401", "403", "404", "409" })
        {
            var content = responses.GetProperty(status).GetProperty("content");
            Assert.True(content.TryGetProperty("application/json", out _));
            Assert.True(content.TryGetProperty("application/problem+json", out _));
        }
    }
}
