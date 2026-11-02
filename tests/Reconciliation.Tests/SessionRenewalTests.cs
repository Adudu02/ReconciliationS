using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class SessionRenewalTests(ApiFixture fixture)
{
    [Fact]
    public async Task Renewal_issues_a_new_pair_in_the_same_session_and_old_refresh_no_longer_renews()
    {
        var user = await fixture.SeedUserAsync();
        using var host = new SessionTestHost(fixture);
        var before = await host.LoginAsync(user.Email);
        using var renewed = await host.RefreshAsync(before.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        var after = (await renewed.Content.ReadFromJsonAsync<SessionPair>(TestContext.Current.CancellationToken))!;
        Assert.Equal(900, after.ExpiresIn);
        Assert.True(before.RefreshToken != after.RefreshToken, "Rotation must issue a different refresh credential.");
        Assert.True(before.AccessToken != after.AccessToken, "Renewal must issue a new access token even at the same clock instant.");
        Assert.Equal(before.SessionId, after.SessionId);
        using var replay = await host.RefreshAsync(before.RefreshToken);
        Assert.True(replay.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Conflict, "A rotated credential must never renew again.");
        using var again = await host.RefreshAsync(after.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM refresh_token WHERE session_id=@sessionId AND rotated_at IS NULL", new { sessionId = before.SessionId }));
    }

    [Fact]
    public async Task Unknown_refresh_is_refused_without_changing_any_session()
    {
        var user = await fixture.SeedUserAsync();
        using var host = new SessionTestHost(fixture);
        var pair = await host.LoginAsync(user.Email);
        var before = await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM refresh_token");
        using var denied = await host.RefreshAsync(WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal(before, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM refresh_token"));
        Assert.False(await fixture.Database.ScalarAsync<bool>("SELECT revoked_at IS NOT NULL FROM auth_session WHERE id=@sessionId", new { sessionId = pair.SessionId }));
    }

    [Fact]
    public async Task Refresh_does_not_need_a_still_valid_access_token()
    {
        var user = await fixture.SeedUserAsync();
        using var host = new SessionTestHost(fixture);
        var pair = await host.LoginAsync(user.Email);
        host.Clock.Advance(TimeSpan.FromMinutes(16));
        using var renewed = await host.RefreshAsync(pair.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
    }
}
