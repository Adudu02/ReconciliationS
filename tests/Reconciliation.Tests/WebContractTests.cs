using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Reconciliation.Contracts;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class WebContractTests(ApiFixture fixture)
{
    private static readonly JsonSerializerOptions StrictJson = new(JsonSerializerOptions.Web) { RespectRequiredConstructorParameters = true };
    [Fact]
    public async Task Every_web_operation_has_referenced_request_and_response_schemas()
    {
        using var client = fixture.App.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi.json", TestContext.Current.CancellationToken));
        var root = document.RootElement;
        Assert.True(root.GetProperty("components").TryGetProperty("schemas", out var schemas));
        foreach (var (method, path) in new (string, string)[] {
            ("post", "/auth/tokens"), ("post", "/auth/refresh"), ("post", "/auth/logout"), ("get", "/companies"),
            ("get", "/companies/{companyId}/banks"), ("get", "/companies/{companyId}/overview"), ("get", "/companies/{companyId}/review-queue"),
            ("post", "/companies/{companyId}/links/{linkId}/decisions"), ("get", "/companies/{companyId}/cfdi"),
            ("get", "/companies/{companyId}/cfdi/{invoiceId}/summary"), ("post", "/companies/{companyId}/imports/cfdi"),
            ("post", "/companies/{companyId}/imports/bank-statement"), ("get", "/companies/{companyId}/imports/{uploadId}"),
            ("get", "/companies/{companyId}/jobs/{jobId}"), ("post", "/companies/{companyId}/reconciliation-runs"),
            ("get", "/companies/{companyId}/movements"), ("patch", "/companies/{companyId}/movements/{movementId}") })
        {
            var op = root.GetProperty("paths").GetProperty(path).GetProperty(method);
            if (path is "/auth/logout" || method == "patch") continue;
            var response = op.GetProperty("responses").EnumerateObject().First(item => item.Name.StartsWith('2'));
            Assert.True(response.Value.TryGetProperty("content", out _), $"Missing content for {method} {path}: {response.Value}");
            var schema = response.Value.GetProperty("content").GetProperty("application/json").GetProperty("schema");
            Assert.True(schema.TryGetProperty("$ref", out _), $"Missing response schema for {method} {path}");
            if (method == "post" && !path.EndsWith("reconciliation-runs", StringComparison.Ordinal))
                Assert.True(op.TryGetProperty("requestBody", out _), $"Missing request schema for {path}");
        }
        Assert.NotEmpty(schemas.EnumerateObject());
    }
    [Fact]
    public async Task Web_DTOs_deserialize_real_domain_responses_with_required_constructor_fields()
    {
        var seed = await MatchingRunScenario.SeedAsync(fixture);
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        var run = await client.PostAsync($"/companies/{seed.Company}/reconciliation-runs", null, TestContext.Current.CancellationToken);
        var receipt = await DecodeAsync<RunReceipt>(run);
        await DrainAsync();
        Assert.Equal("succeeded", (await ReadAsync<JobResponse>(client, $"/companies/{seed.Company}/jobs/{receipt.JobId}")).State);
        var companies = await ReadAsync<CompanyItem[]>(client, "/companies");
        Assert.Contains(companies, item => item.Id == seed.Company);
        var overview = await ReadAsync<OverviewResponse>(client, $"/companies/{seed.Company}/overview");
        Assert.Equal(11600, overview.Invoices.Amount);
        var invoices = await ReadAsync<InvoicePage>(client, $"/companies/{seed.Company}/cfdi?type=I");
        var summary = await ReadAsync<InvoiceSummary>(client, $"/companies/{seed.Company}/cfdi/{Assert.Single(invoices.Invoices).Id}/summary");
        Assert.Equal(50, summary.PaidPercentage);
        Assert.Equal(2, summary.Links.Length);
        Assert.NotNull(Assert.Single(summary.Links, item => item.State == "reconciled").PaymentCfdiId);
        var queue = await ReadAsync<ReviewPage>(client, $"/companies/{seed.Company}/review-queue");
        var suggestion = Assert.Single(queue.Entries, item => item.Kind == "link");
        using var decision = await client.PostAsJsonAsync($"/companies/{seed.Company}/links/{suggestion.LinkId}/decisions", new DecisionRequest("reject"), TestContext.Current.CancellationToken);
        Assert.Equal("reject", (await DecodeAsync<DecisionResponse>(decision)).Decision.Type);
        Assert.Equal(2, (await ReadAsync<MovementPage>(client, $"/companies/{seed.Company}/movements")).Movements.Length);
        using var upload = await client.PostAsync($"/companies/{seed.Company}/imports/cfdi", IngestionHttpTests.CfdiBody(SyntheticCfdi.Invoice()), TestContext.Current.CancellationToken);
        var imported = await DecodeAsync<ImportReceipt>(upload);
        await DrainAsync();
        Assert.NotEmpty((await ReadAsync<ImportReportResponse>(client, $"/companies/{seed.Company}/imports/{imported.UploadId}")).Files);
    }

    private async Task DrainAsync()
    {
        var dispatcher = fixture.App.Services.GetRequiredService<Reconciliation.Core.Jobs.JobDispatcher>();
        while (await dispatcher.RunOneAsync("synthetic-contract", TestContext.Current.CancellationToken)) { }
    }

    private static async Task<T> ReadAsync<T>(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        return await DecodeAsync<T>(response);
    }

    private static async Task<T> DecodeAsync<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<T>(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StrictJson)!;
    }

}
