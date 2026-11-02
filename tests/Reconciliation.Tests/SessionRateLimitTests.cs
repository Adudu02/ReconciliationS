using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class SessionRateLimitTests(ApiFixture fixture)
{
    [Theory]
    [InlineData("/auth/refresh")]
    [InlineData("/auth/logout")]
    public async Task Session_routes_limit_the_remote_ip_and_return_retry_after(string path)
    {
        var user = await fixture.SeedUserAsync();
        using var host = new SessionTestHost(fixture, builder => builder.UseSetting("RefreshRateLimit:IpPermitLimit", "2"));
        var pair = await host.LoginAsync(user.Email);
        if (path == "/auth/logout") host.Authorize(pair);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var allowed = await PostAsync(host, path);
            Assert.Equal(path == "/auth/logout" ? HttpStatusCode.NoContent : HttpStatusCode.Unauthorized, allowed.StatusCode);
        }
        using var denied = await PostAsync(host, path);
        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
        Assert.True(denied.Headers.RetryAfter?.Delta is { TotalSeconds: >= 1 and <= 60 });
    }

    [Fact]
    public async Task Anonymous_logout_attempts_are_also_rate_limited()
    {
        using var host = new SessionTestHost(fixture, builder => builder.UseSetting("RefreshRateLimit:IpPermitLimit", "1"));
        using var first = await PostAsync(host, "/auth/logout");
        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        using var rejected = await PostAsync(host, "/auth/logout");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.NotNull(rejected.Headers.RetryAfter);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void Invalid_refresh_rate_limits_stop_startup(string limit)
    {
        using var app = fixture.App.WithWebHostBuilder(builder => builder.UseSetting("RefreshRateLimit:IpPermitLimit", limit));
        var error = Assert.Throws<InvalidOperationException>(() => app.CreateClient());
        Assert.Contains("RefreshRateLimit", error.Message, StringComparison.Ordinal);
    }

    private static Task<HttpResponseMessage> PostAsync(SessionTestHost host, string path) => path == "/auth/refresh"
        ? host.Client.PostAsJsonAsync(path, new { refreshToken = "synthetic-unknown-refresh-credential" }, TestContext.Current.CancellationToken)
        : host.Client.PostAsync(path, null, TestContext.Current.CancellationToken);
}
