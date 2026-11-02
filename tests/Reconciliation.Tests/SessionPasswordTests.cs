using System.Net;
using System.Net.Http.Json;
using Dapper;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class SessionPasswordTests(ApiFixture fixture)
{
    private const string NewPassword = "synthetic-updated-password-456";

    [Fact]
    public async Task Password_change_revokes_all_own_sessions_and_old_password_without_affecting_other_users()
    {
        var user = await fixture.SeedUserAsync();
        var other = await fixture.SeedUserAsync();
        using var host = new SessionTestHost(fixture);
        var first = await host.LoginAsync(user.Email);
        var second = await host.LoginAsync(user.Email);
        var otherPair = await host.LoginAsync(other.Email);
        host.Authorize(first);
        using var changed = await ChangeAsync(host, ApiFixture.Password, NewPassword);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(2, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM auth_session WHERE user_id=@Id AND revoked_at IS NOT NULL AND revoked_reason='password_changed'", new { user.Id }));
        Assert.Equal(1, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM auth_event WHERE user_id=@Id AND session_id=@sessionId AND event='password_changed'", new { user.Id, sessionId = first.SessionId }));
        foreach (var pair in new[] { first, second })
        {
            using var denied = await host.RefreshAsync(pair.RefreshToken);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        using var otherStillActive = await host.RefreshAsync(otherPair.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, otherStillActive.StatusCode);
        using var oldPassword = await host.Client.PostAsJsonAsync("/auth/tokens", new { email = user.Email, password = ApiFixture.Password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
        var newLogin = await host.LoginAsync(user.Email, NewPassword);
        Assert.NotNull(newLogin.RefreshToken);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.Unauthorized)]
    [InlineData(true, HttpStatusCode.BadRequest)]
    public async Task Rejected_password_changes_leave_credentials_sessions_and_events_unchanged(bool weakNewPassword, HttpStatusCode expected)
    {
        var user = await fixture.SeedUserAsync();
        using var host = new SessionTestHost(fixture);
        var pair = await host.LoginAsync(user.Email);
        host.Authorize(pair);
        var before = await SnapshotAsync(user.Id);
        using var denied = await ChangeAsync(host, weakNewPassword ? ApiFixture.Password : "synthetic-wrong-password", weakNewPassword ? "short" : NewPassword);
        Assert.Equal(expected, denied.StatusCode);
        Assert.True(before == await SnapshotAsync(user.Id), "A refused password change must not change stored credentials or session state.");
        using var stillActive = await host.RefreshAsync(pair.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, stillActive.StatusCode);
    }

    [Fact]
    public async Task Anonymous_password_change_is_refused()
    {
        using var host = new SessionTestHost(fixture);
        using var denied = await ChangeAsync(host, ApiFixture.Password, NewPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [Fact]
    public async Task A_signed_token_cannot_associate_password_change_with_another_users_session()
    {
        var user = await fixture.SeedUserAsync();
        var other = await fixture.SeedUserAsync();
        using var host = new SessionTestHost(fixture);
        var otherPair = await host.LoginAsync(other.Email);
        host.AuthorizeAs(user.Id, otherPair.SessionId);
        var before = await SnapshotAsync(user.Id);
        using var denied = await ChangeAsync(host, ApiFixture.Password, NewPassword);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.True(before == await SnapshotAsync(user.Id));
    }

    [Fact]
    public async Task Event_failure_rolls_back_password_and_revocation_together()
    {
        var user = await fixture.SeedUserAsync();
        using var host = new SessionTestHost(fixture);
        var pair = await host.LoginAsync(user.Email);
        host.Authorize(pair);
        var before = await SnapshotAsync(user.Id);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync(new CommandDefinition("REVOKE INSERT ON auth_event FROM cfdi_app", cancellationToken: TestContext.Current.CancellationToken));
        try
        {
            using var failed = await ChangeAsync(host, ApiFixture.Password, NewPassword);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.True(before == await SnapshotAsync(user.Id), "Event failure must roll back the whole password change.");
        }
        finally { await owner.ExecuteAsync(new CommandDefinition("GRANT INSERT ON auth_event TO cfdi_app", cancellationToken: TestContext.Current.CancellationToken)); }
        using var stillActive = await host.RefreshAsync(pair.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, stillActive.StatusCode);
    }

    private static Task<HttpResponseMessage> ChangeAsync(SessionTestHost host, string currentPassword, string newPassword) =>
        host.Client.PostAsJsonAsync("/auth/password", new { currentPassword, newPassword }, TestContext.Current.CancellationToken);

    private Task<string?> SnapshotAsync(Guid userId) => fixture.Database.ScalarAsync<string>("""
        SELECT jsonb_build_object('account',to_jsonb(a),'sessions',(SELECT jsonb_agg(to_jsonb(s) ORDER BY id) FROM auth_session s WHERE user_id=a.id),
          'events',(SELECT jsonb_agg(to_jsonb(e) ORDER BY id) FROM auth_event e WHERE user_id=a.id))::text FROM app_user a WHERE id=@userId
        """, new { userId });
}
