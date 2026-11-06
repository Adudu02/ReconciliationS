using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class ReviewOpenApiTests(ApiFixture fixture)
{
    [Fact]
    public async Task Anonymous_versioned_document_describes_every_endpoint_security_roles_and_errors()
    {
        using var client = fixture.App.CreateClient();
        using var response = await client.GetAsync("/openapi.json", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("1.0.0", Assert.Single(response.Headers.GetValues("x-api-version")));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = document.RootElement;
        Assert.Equal("1.0.0", root.GetProperty("info").GetProperty("version").GetString());
        Assert.Contains("results have no fiscal or legal validity", root.GetProperty("info").GetProperty("description").GetString(), StringComparison.Ordinal);
        var bearer = root.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");
        Assert.Equal("http", bearer.GetProperty("type").GetString());
        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());
        var paths = root.GetProperty("paths");
        foreach (var endpoint in fixture.App.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var raw = endpoint.RoutePattern.RawText!;
            if (raw is "/health" or "/openapi.json") continue;
            var path = raw.Replace(":guid", "", StringComparison.Ordinal).Replace(":long", "", StringComparison.Ordinal);
            foreach (var method in endpoint.Metadata.GetRequiredMetadata<IHttpMethodMetadata>().HttpMethods)
            {
                Assert.True(paths.TryGetProperty(path, out var described), $"Missing {method} {path}");
                var operation = described.GetProperty(method.ToLowerInvariant());
                Assert.False(string.IsNullOrWhiteSpace(operation.GetProperty("summary").GetString()));
                Assert.NotEmpty(operation.GetProperty("tags").EnumerateArray());
                Assert.Contains("Required role:", operation.GetProperty("description").GetString(), StringComparison.Ordinal);
                if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null) Assert.NotEmpty(operation.GetProperty("security").EnumerateArray());
                foreach (var status in new[] { "401", "403", "404", "409" }) Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _), $"{method} {path} lacks {status}");
            }
        }
        foreach (var path in new[] { "/companies/{companyId}/banks", "/companies/{companyId}/movements/{movementId}", "/companies/{companyId}/settings", "/companies/{companyId}/cfdi/{invoiceId}/xml", "/companies/{companyId}/imports/files/{fileId}/original" })
            Assert.True(paths.TryGetProperty(path, out _), $"Missing catalogue route {path}");
        Assert.True(paths.GetProperty("/companies/{companyId}/settings").TryGetProperty("get", out _));
        Assert.True(paths.GetProperty("/companies/{companyId}/settings").TryGetProperty("patch", out _));
        Assert.True(paths.GetProperty("/companies/{companyId}/movements/{movementId}").TryGetProperty("patch", out _));
    }
}
