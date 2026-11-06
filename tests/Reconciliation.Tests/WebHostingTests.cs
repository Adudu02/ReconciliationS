using System.Net;
using System.Net.Http.Headers;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class WebHostingTests(ApiFixture fixture)
{
    [Theory]
    [InlineData("/")]
    [InlineData("/ui/review")]
    public async Task Anonymous_shell_is_static_safe_and_contains_no_tenant_data(string path)
    {
        using var client = fixture.App.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("_framework/blazor.webassembly.js", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("style=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Synthetic company", html, StringComparison.Ordinal);
        Assert.DoesNotContain("accessToken", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/companies/unknown", "text/html", HttpStatusCode.Unauthorized)]
    [InlineData("/auth/unknown", "text/html", HttpStatusCode.Unauthorized)]
    [InlineData("/openapi.json/missing", "text/html", HttpStatusCode.Unauthorized)]
    [InlineData("/health/missing", "text/html", HttpStatusCode.Unauthorized)]
    [InlineData("/ui/review", "application/json", HttpStatusCode.NotFound)]
    public async Task Fallback_never_returns_html_for_api_or_non_html_requests(string path, string accept, HttpStatusCode expected)
    {
        using var client = fixture.App.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.ParseAdd(accept);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(expected, response.StatusCode);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }
}
