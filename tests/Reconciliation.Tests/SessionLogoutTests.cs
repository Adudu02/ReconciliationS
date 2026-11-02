using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class SessionLogoutTests(ApiFixture fixture)
{
    [Fact]
    public async Task Logout_revokes_the_current_family_only_and_records_the_event()
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        using var host = new SessionTestHost(fixture);
        var current = await host.LoginAsync(user.Email);
        var otherDevice = await host.LoginAsync(user.Email);
        host.Authorize(current, company);
        using var loggedOut = await host.Client.PostAsync("/auth/logout", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, loggedOut.StatusCode);
        using var revoked = await host.RefreshAsync(current.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        using var unaffected = await host.RefreshAsync(otherDevice.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, unaffected.StatusCode);
        Assert.Equal("logout", await fixture.Database.ScalarAsync<string>("SELECT revoked_reason FROM auth_session WHERE id=@sessionId", new { sessionId = current.SessionId }));
        Assert.Equal(1, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM auth_event WHERE session_id=@sessionId AND user_id=@Id AND event='logout'", new { sessionId = current.SessionId, user.Id }));
        // Access remains stateless until expiry, exactly as the design documents.
        using var stillValidAccess = await host.Client.GetAsync($"/companies/{company}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, stillValidAccess.StatusCode);
    }

    [Fact]
    public async Task Anonymous_logout_is_refused()
    {
        using var host = new SessionTestHost(fixture);
        using var denied = await host.Client.PostAsync("/auth/logout", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [Fact]
    public async Task A_signed_access_token_cannot_logout_a_session_owned_by_another_user()
    {
        var user = await fixture.SeedUserAsync();
        var other = await fixture.SeedUserAsync();
        using var host = new SessionTestHost(fixture);
        var otherPair = await host.LoginAsync(other.Email);
        var jwt = new JwtSecurityToken("reconciliation", "reconciliation",
            [new Claim("sub", user.Id.ToString()), new Claim("sid", otherPair.SessionId.ToString())],
            notBefore: host.Clock.GetUtcNow().UtcDateTime, expires: host.Clock.GetUtcNow().UtcDateTime.AddMinutes(15),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes("synthetic-test-signing-key-at-least-32-bytes")), SecurityAlgorithms.HmacSha256));
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
        using var denied = await host.Client.PostAsync("/auth/logout", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.False(await fixture.Database.ScalarAsync<bool>("SELECT revoked_at IS NOT NULL FROM auth_session WHERE id=@sessionId", new { sessionId = otherPair.SessionId }));
    }
}
