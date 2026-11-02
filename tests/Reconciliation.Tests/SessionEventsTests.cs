using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class SessionEventsTests(ApiFixture fixture)
{
    private static readonly int[] EventOffsets = [0, 1, 12, 13, 14, 15, 16];
    private sealed record AuthEventRow(Guid UserId, Guid SessionId, string Event, DateTime CreatedAt, string RemoteIp);

    [Fact]
    public async Task All_session_events_record_user_family_real_remote_ip_and_injected_time_without_credentials()
    {
        var user = await fixture.SeedUserAsync();
        using var host = new SessionTestHost(fixture, builder => builder.ConfigureServices(services => services.AddTransient<IStartupFilter, EventRemoteIpStartup>()));
        host.Client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.19");
        var initialTime = host.Clock.GetUtcNow().UtcDateTime;
        var original = await host.LoginAsync(user.Email);
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        using var refreshed = await host.RefreshAsync(original.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var renewed = (await refreshed.Content.ReadFromJsonAsync<SessionPair>(TestContext.Current.CancellationToken))!;
        host.Clock.Advance(TimeSpan.FromSeconds(11));
        using var reuse = await host.RefreshAsync(original.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        var second = await host.LoginAsync(user.Email);
        host.Authorize(second);
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        using var logout = await host.Client.PostAsync("/auth/logout", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        var third = await host.LoginAsync(user.Email);
        host.Authorize(third);
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        const string newPassword = "synthetic-event-password-456";
        using var changed = await host.Client.PostAsJsonAsync("/auth/password", new { currentPassword = ApiFixture.Password, newPassword }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        var events = (await owner.QueryAsync<AuthEventRow>(new CommandDefinition("""
            SELECT user_id AS UserId,session_id AS SessionId,event,created_at AS CreatedAt,host(remote_ip) AS RemoteIp
            FROM auth_event WHERE user_id=@Id ORDER BY created_at,id
            """, new { user.Id }, cancellationToken: TestContext.Current.CancellationToken))).ToArray();
        Assert.Equal(["login", "refresh", "reuse_detected", "login", "logout", "login", "password_changed"], events.Select(entry => entry.Event));
        Assert.All(events, entry => Assert.Equal(user.Id, entry.UserId));
        Assert.All(events, entry => Assert.Equal("192.0.2.10", entry.RemoteIp));
        Assert.Equal(EventOffsets.Select(seconds => initialTime.AddSeconds(seconds)), events.Select(entry => entry.CreatedAt));
        Assert.Equal([original.SessionId, original.SessionId, original.SessionId, second.SessionId, second.SessionId, third.SessionId, third.SessionId], events.Select(entry => entry.SessionId));
        var stored = await fixture.Database.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(e))::text FROM auth_event e WHERE user_id=@Id", new { user.Id });
        foreach (var secret in new[] { original.AccessToken, original.RefreshToken!, renewed.AccessToken, renewed.RefreshToken!, second.AccessToken, second.RefreshToken!, third.AccessToken, third.RefreshToken!, ApiFixture.Password, newPassword })
            Assert.False(stored!.Contains(secret, StringComparison.Ordinal), "Events must never contain credentials.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Event_failure_rolls_back_login_or_rotation(bool login)
    {
        var user = await fixture.SeedUserAsync();
        using var host = new SessionTestHost(fixture);
        var pair = login ? null : await host.LoginAsync(user.Email);
        var before = await SnapshotAsync(user.Id);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync(new CommandDefinition("REVOKE INSERT ON auth_event FROM cfdi_app", cancellationToken: TestContext.Current.CancellationToken));
        try
        {
            using var failed = login
                ? await host.Client.PostAsJsonAsync("/auth/tokens", new { email = user.Email, password = ApiFixture.Password }, TestContext.Current.CancellationToken)
                : await host.RefreshAsync(pair!.RefreshToken);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.True(before == await SnapshotAsync(user.Id), "An event failure must roll back the complete session operation.");
        }
        finally { await owner.ExecuteAsync(new CommandDefinition("GRANT INSERT ON auth_event TO cfdi_app", cancellationToken: TestContext.Current.CancellationToken)); }
        if (pair is not null)
        {
            using var active = await host.RefreshAsync(pair.RefreshToken);
            Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        }
    }

    private Task<string?> SnapshotAsync(Guid userId) => fixture.Database.ScalarAsync<string>("""
        SELECT jsonb_build_object('sessions',(SELECT jsonb_agg(to_jsonb(s) ORDER BY id) FROM auth_session s WHERE user_id=@userId),
          'tokens',(SELECT jsonb_agg(to_jsonb(t) ORDER BY t.id) FROM refresh_token t JOIN auth_session s ON s.id=t.session_id WHERE s.user_id=@userId),
          'events',(SELECT jsonb_agg(to_jsonb(e) ORDER BY id) FROM auth_event e WHERE user_id=@userId))::text
        """, new { userId });

    private sealed class EventRemoteIpStartup : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (HttpContext httpContext, RequestDelegate nextRequest) =>
            {
                httpContext.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
                await nextRequest(httpContext);
            });
            next(app);
        };
    }
}
