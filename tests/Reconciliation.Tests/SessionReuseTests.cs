using System.Net;
using System.Net.Http.Json;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class SessionReuseTests(ApiFixture fixture)
{
    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public async Task Reuse_inside_the_inclusive_grace_window_returns_conflict_and_changes_nothing(int seconds)
    {
        var user = await fixture.SeedUserAsync();
        using var host = new SessionTestHost(fixture);
        var pair = await host.LoginAsync(user.Email);
        using var renewed = await host.RefreshAsync(pair.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        var winner = (await renewed.Content.ReadFromJsonAsync<SessionPair>(TestContext.Current.CancellationToken))!;
        var before = await SnapshotAsync(pair.SessionId);
        host.Clock.Advance(TimeSpan.FromSeconds(seconds));
        using var replay = await host.RefreshAsync(pair.RefreshToken);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Equal(before, await SnapshotAsync(pair.SessionId));
        using var active = await host.RefreshAsync(winner.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, active.StatusCode);
    }

    [Fact]
    public async Task Reuse_outside_grace_revokes_only_that_family_and_records_the_event()
    {
        var user = await fixture.SeedUserAsync();
        using var host = new SessionTestHost(fixture);
        var pair = await host.LoginAsync(user.Email);
        var otherDevice = await host.LoginAsync(user.Email);
        using var renewed = await host.RefreshAsync(pair.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        var current = (await renewed.Content.ReadFromJsonAsync<SessionPair>(TestContext.Current.CancellationToken))!;
        host.Clock.Advance(TimeSpan.FromSeconds(11));
        using var replay = await host.RefreshAsync(pair.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("reuse_detected", await fixture.Database.ScalarAsync<string>("SELECT revoked_reason FROM auth_session WHERE id=@sessionId", new { sessionId = pair.SessionId }));
        Assert.Equal(1, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM auth_event WHERE session_id=@sessionId AND user_id=@Id AND event='reuse_detected'", new { sessionId = pair.SessionId, user.Id }));
        using var denied = await host.RefreshAsync(current.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var unaffected = await host.RefreshAsync(otherDevice.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, unaffected.StatusCode);
    }

    [Fact]
    public async Task Replaying_an_expired_rotated_credential_still_detects_theft_of_an_active_family()
    {
        var user = await fixture.SeedUserAsync();
        using var host = new SessionTestHost(fixture);
        var original = await host.LoginAsync(user.Email);
        using var first = await host.RefreshAsync(original.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var replacement = (await first.Content.ReadFromJsonAsync<SessionPair>(TestContext.Current.CancellationToken))!;
        host.Clock.Advance(TimeSpan.FromMinutes(479));
        using var keepAlive = await host.RefreshAsync(replacement.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, keepAlive.StatusCode);
        var current = (await keepAlive.Content.ReadFromJsonAsync<SessionPair>(TestContext.Current.CancellationToken))!;
        host.Clock.Advance(TimeSpan.FromMinutes(2));
        using var replay = await host.RefreshAsync(original.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("reuse_detected", await fixture.Database.ScalarAsync<string>("SELECT revoked_reason FROM auth_session WHERE id=@sessionId", new { sessionId = original.SessionId }));
        using var revoked = await host.RefreshAsync(current.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    private Task<string?> SnapshotAsync(Guid sessionId) => fixture.Database.ScalarAsync<string>("""
        SELECT jsonb_build_object('session',to_jsonb(s),'tokens',(SELECT jsonb_agg(to_jsonb(t) ORDER BY id) FROM refresh_token t WHERE session_id=s.id),
          'events',(SELECT jsonb_agg(to_jsonb(e) ORDER BY id) FROM auth_event e WHERE session_id=s.id))::text
        FROM auth_session s WHERE id=@sessionId
        """, new { sessionId });
}
