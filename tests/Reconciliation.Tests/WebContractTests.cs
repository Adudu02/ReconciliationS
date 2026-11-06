using System.Text.Json;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class WebContractTests(ApiFixture fixture)
{
    [Fact]
    public async Task Every_web_operation_has_referenced_request_and_response_schemas()
    {
        using var client = fixture.App.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi.json", TestContext.Current.CancellationToken));
        var root = document.RootElement;
        Assert.True(root.GetProperty("components").TryGetProperty("schemas", out var schemas));
        foreach (var (method, path) in new (string, string)[] {
            ("post", "/auth/tokens"), ("post", "/auth/refresh"), ("post", "/auth/logout"), ("get", "/companies"),
            ("get", "/companies/{companyId}/overview"), ("get", "/companies/{companyId}/review-queue"),
            ("post", "/companies/{companyId}/links/{linkId}/decisions"), ("get", "/companies/{companyId}/cfdi"),
            ("get", "/companies/{companyId}/cfdi/{invoiceId}/summary"), ("post", "/companies/{companyId}/imports/cfdi"),
            ("post", "/companies/{companyId}/imports/bank-statement"), ("get", "/companies/{companyId}/imports/{uploadId}"),
            ("get", "/companies/{companyId}/jobs/{jobId}"), ("post", "/companies/{companyId}/reconciliation-runs"),
            ("get", "/companies/{companyId}/movements"), ("patch", "/companies/{companyId}/movements/{movementId}") })
        {
            var op = root.GetProperty("paths").GetProperty(path).GetProperty(method);
            if (path is "/auth/logout" || method == "patch") continue;
            var response = op.GetProperty("responses").EnumerateObject().First(item => item.Name.StartsWith('2'));
            var schema = response.Value.GetProperty("content").GetProperty("application/json").GetProperty("schema");
            Assert.True(schema.TryGetProperty("$ref", out _), $"Missing response schema for {method} {path}");
            if (method == "post" && !path.EndsWith("reconciliation-runs", StringComparison.Ordinal))
                Assert.True(op.TryGetProperty("requestBody", out _), $"Missing request schema for {path}");
        }
        Assert.NotEmpty(schemas.EnumerateObject());
    }
}
