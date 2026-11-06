using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class BrowserSessionTests(ApiFixture fixture)
{
    private const string Origin = "https://app.example.invalid";

    [Fact]
    public async Task Browser_opt_in_uses_strict_cookie_while_headerless_JSON_clients_are_unchanged()
    {
        var user = await fixture.SeedUserAsync();
        using var host = new SessionTestHost(fixture, builder => builder.UseSetting("Session:BrowserRefreshCookie", "true").UseSetting("Session:AllowedOrigins:0", Origin));
        using (var json = await host.Client.PostAsJsonAsync("/auth/tokens", new { email = user.Email, password = ApiFixture.Password }, TestContext.Current.CancellationToken))
        {
            Assert.False(json.Headers.Contains("Set-Cookie"));
            Assert.True((await json.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).TryGetProperty("refreshToken", out _));
        }
        host.Client.DefaultRequestHeaders.Add("X-Refresh-Transport", "cookie");
        using (var denied = await host.Client.PostAsJsonAsync("/auth/tokens", new { email = user.Email, password = ApiFixture.Password }, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        host.Client.DefaultRequestHeaders.Add("Origin", Origin);
        using var login = await host.Client.PostAsJsonAsync("/auth/tokens", new { email = user.Email, password = ApiFixture.Password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.False(body.TryGetProperty("refreshToken", out _));
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/auth", cookie, StringComparison.OrdinalIgnoreCase);
        host.Client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        using var refresh = await host.Client.PostAsync("/auth/refresh", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        Assert.False((await refresh.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).TryGetProperty("refreshToken", out _));
        host.Clock.Advance(TimeSpan.FromSeconds(11));
        using var replay = await host.Client.PostAsync("/auth/refresh", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task Disabled_browser_header_is_ignored_and_logout_with_cookie_clears_it()
    {
        var user = await fixture.SeedUserAsync();
        using var disabled = new SessionTestHost(fixture);
        disabled.Client.DefaultRequestHeaders.Add("X-Refresh-Transport", "cookie");
        using var json = await disabled.Client.PostAsJsonAsync("/auth/tokens", new { email = user.Email, password = ApiFixture.Password }, TestContext.Current.CancellationToken);
        Assert.True((await json.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).TryGetProperty("refreshToken", out _));
        using var host = new SessionTestHost(fixture, builder => builder.UseSetting("Session:BrowserRefreshCookie", "true").UseSetting("Session:AllowedOrigins:0", Origin));
        host.Client.DefaultRequestHeaders.Add("X-Refresh-Transport", "cookie");
        host.Client.DefaultRequestHeaders.Add("Origin", Origin);
        using var login = await host.Client.PostAsJsonAsync("/auth/tokens", new { email = user.Email, password = ApiFixture.Password }, TestContext.Current.CancellationToken);
        var pair = (await login.Content.ReadFromJsonAsync<SessionPair>(TestContext.Current.CancellationToken))!;
        host.Authorize(pair);
        using var logout = await host.Client.PostAsync("/auth/logout", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Contains("max-age=0", Assert.Single(logout.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);
    }
}
