using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class SessionExpiryTests(ApiFixture fixture)
{
    [Fact]
    public async Task Inactivity_refuses_renewal_at_the_idle_deadline()
    {
        var user = await fixture.SeedUserAsync();
        using var host = new SessionTestHost(fixture);
        var pair = await host.LoginAsync(user.Email);
        host.Clock.Advance(TimeSpan.FromHours(8));
        using var expired = await host.RefreshAsync(pair.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Equal(1, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM refresh_token WHERE session_id=@sessionId", new { sessionId = pair.SessionId }));
    }

    [Fact]
    public async Task Absolute_cap_refuses_renewal_even_when_active_a_minute_earlier()
    {
        var user = await fixture.SeedUserAsync();
        using var host = new SessionTestHost(fixture, builder => builder.UseSetting("Session:IdleMinutes", "44640"));
        var pair = await host.LoginAsync(user.Email);
        host.Clock.Advance(TimeSpan.FromDays(30) - TimeSpan.FromMinutes(1));
        using var active = await host.RefreshAsync(pair.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        var latest = (await active.Content.ReadFromJsonAsync<SessionPair>(TestContext.Current.CancellationToken))!;
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        using var expired = await host.RefreshAsync(latest.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    [Fact]
    public async Task Renewal_near_the_cap_does_not_extend_either_deadline()
    {
        var user = await fixture.SeedUserAsync();
        using var host = new SessionTestHost(fixture, builder => builder.UseSetting("Session:AbsoluteDays", "1").UseSetting("Session:IdleMinutes", "1440"));
        var expectedCap = host.Clock.GetUtcNow().UtcDateTime.AddDays(1);
        var pair = await host.LoginAsync(user.Email);
        host.Clock.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(55));
        using var renewed = await host.RefreshAsync(pair.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        Assert.Equal(expectedCap, await fixture.Database.ScalarAsync<DateTime>("SELECT absolute_expires_at FROM auth_session WHERE id=@sessionId", new { sessionId = pair.SessionId }));
        Assert.Equal(expectedCap, await fixture.Database.ScalarAsync<DateTime>("SELECT expires_at FROM refresh_token WHERE session_id=@sessionId AND rotated_at IS NULL", new { sessionId = pair.SessionId }));
    }
}
