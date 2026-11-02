using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Net.Http.Headers;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class SessionCookieTests(ApiFixture fixture)
{
    private const string AllowedOrigin = "https://app.example.invalid";

    [Fact]
    public async Task Cookie_login_delivers_only_the_refresh_cookie_with_all_required_flags()
    {
        var user = await fixture.SeedUserAsync();
        using var host = CreateCookieHost();
        var (cookie, pair) = await LoginCookieAsync(host, user.Email);
        Assert.Equal(43, cookie.Value.Length);
        Assert.Equal(900, pair.ExpiresIn);
    }

    [Fact]
    public async Task Cookie_renewal_rotates_without_a_json_body_and_logout_clears_the_cookie()
    {
        var user = await fixture.SeedUserAsync();
        using var host = CreateCookieHost();
        var (original, pair) = await LoginCookieAsync(host, user.Email);
        SetCookie(host, original.Value.Value!);
        host.Client.DefaultRequestHeaders.Add("Origin", AllowedOrigin);
        using var refreshed = await host.Client.PostAsync("/auth/refresh", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var replacement = ReadCookie(refreshed);
        var newPair = await ReadCookiePairAsync(refreshed);
        Assert.True(original.Value != replacement.Value, "The cookie credential must rotate.");
        Assert.Equal(pair.SessionId, newPair.SessionId);
        using var lostRace = await host.Client.PostAsync("/auth/refresh", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, lostRace.StatusCode);
        Assert.False(lostRace.Headers.Contains("Set-Cookie"));
        SetCookie(host, replacement.Value.Value!);
        host.Authorize(newPair);
        using var loggedOut = await host.Client.PostAsync("/auth/logout", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, loggedOut.StatusCode);
        var cleared = ReadCookie(loggedOut);
        Assert.True(cleared.Value.Length == 0);
        Assert.True(cleared.MaxAge == TimeSpan.Zero || cleared.Expires < host.Clock.GetUtcNow());
        using var revoked = await host.Client.PostAsync("/auth/refresh", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://evil.example.invalid")]
    public async Task Cookie_renewal_requires_an_allowed_origin_and_does_not_consume_a_rejected_token(string? origin)
    {
        var user = await fixture.SeedUserAsync();
        using var host = CreateCookieHost();
        var (cookie, pair) = await LoginCookieAsync(host, user.Email);
        SetCookie(host, cookie.Value.Value!);
        if (origin is not null) host.Client.DefaultRequestHeaders.Add("Origin", origin);
        using var denied = await host.Client.PostAsync("/auth/refresh", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(1, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM refresh_token WHERE session_id=@sessionId", new { sessionId = pair.SessionId }));
        Assert.False(await fixture.Database.ScalarAsync<bool>("SELECT revoked_at IS NOT NULL FROM auth_session WHERE id=@sessionId", new { sessionId = pair.SessionId }));
        host.Client.DefaultRequestHeaders.Remove("Origin");
        host.Client.DefaultRequestHeaders.Add("Origin", AllowedOrigin);
        using var accepted = await host.Client.PostAsync("/auth/refresh", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task Cookie_mode_does_not_accept_a_body_credential_instead_of_the_cookie()
    {
        var user = await fixture.SeedUserAsync();
        using var host = CreateCookieHost();
        var (cookie, pair) = await LoginCookieAsync(host, user.Email);
        host.Client.DefaultRequestHeaders.Add("Origin", AllowedOrigin);
        using var denied = await host.RefreshAsync(cookie.Value.Value!);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal(1, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM refresh_token WHERE session_id=@sessionId", new { sessionId = pair.SessionId }));
    }

    [Fact]
    public async Task Disabled_cookie_transport_keeps_refresh_in_json_without_requiring_origin()
    {
        var user = await fixture.SeedUserAsync();
        using var host = new SessionTestHost(fixture);
        using var login = await host.Client.PostAsJsonAsync("/auth/tokens", new { email = user.Email, password = ApiFixture.Password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.False(login.Headers.Contains("Set-Cookie"));
        var pair = (await login.Content.ReadFromJsonAsync<SessionPair>(TestContext.Current.CancellationToken))!;
        Assert.NotNull(pair.RefreshToken);
        using var renewed = await host.RefreshAsync(pair.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        Assert.False(renewed.Headers.Contains("Set-Cookie"));
        var body = await renewed.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(body.TryGetProperty("refreshToken", out _));
    }

    [Theory]
    [InlineData("{", "application/json", HttpStatusCode.BadRequest)]
    [InlineData("{}", "text/plain", HttpStatusCode.Unauthorized)]
    public async Task Body_transport_preserves_malformed_request_responses(string body, string contentType, HttpStatusCode expected)
    {
        using var host = new SessionTestHost(fixture);
        using var content = new StringContent(body, System.Text.Encoding.UTF8, contentType);
        using var response = await host.Client.PostAsync("/auth/refresh", content, TestContext.Current.CancellationToken);
        Assert.Equal(expected, response.StatusCode);
    }

    private SessionTestHost CreateCookieHost() => new(fixture, builder => builder.UseSetting("Session:RefreshCookie", "true").UseSetting("Session:AllowedOrigins:0", AllowedOrigin));

    private static async Task<(SetCookieHeaderValue Cookie, SessionPair Pair)> LoginCookieAsync(SessionTestHost host, string email)
    {
        using var response = await host.Client.PostAsJsonAsync("/auth/tokens", new { email, password = ApiFixture.Password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (ReadCookie(response), await ReadCookiePairAsync(response));
    }

    private static SetCookieHeaderValue ReadCookie(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var values), "Cookie mode must deliver its credential through Set-Cookie.");
        var cookie = SetCookieHeaderValue.Parse(Assert.Single(values));
        Assert.Equal("refresh_token", cookie.Name.Value);
        Assert.Equal("/auth", cookie.Path.Value);
        Assert.True(cookie.HttpOnly && cookie.Secure);
        Assert.Equal("Strict", cookie.SameSite.ToString());
        return cookie;
    }

    private static async Task<SessionPair> ReadCookiePairAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.False(body.TryGetProperty("refreshToken", out _));
        return body.Deserialize<SessionPair>(JsonSerializerOptions.Web)!;
    }

    private static void SetCookie(SessionTestHost host, string refreshToken)
    {
        host.Client.DefaultRequestHeaders.Remove("Cookie");
        host.Client.DefaultRequestHeaders.Add("Cookie", "refresh_token=" + refreshToken);
    }
}
