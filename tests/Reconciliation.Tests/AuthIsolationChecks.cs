using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Reconciliation.Core.Tenancy;

namespace Reconciliation.Tests;

internal static class AuthIsolationChecks
{
    private enum AuthCase { Login, Refresh, Logout, Password }
    private static readonly Dictionary<string, AuthCase> Cases = new(StringComparer.Ordinal)
    {
        ["POST /auth/tokens"] = AuthCase.Login,
        ["POST /auth/refresh"] = AuthCase.Refresh,
        ["POST /auth/logout"] = AuthCase.Logout,
        ["POST /auth/password"] = AuthCase.Password
    };

    public static bool IsAuthenticationRoute(string route) => route == "/auth" || route.StartsWith("/auth/", StringComparison.Ordinal);

    public static async Task VerifyAsync(ApiFixture fixture)
    {
        var routes = fixture.App.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => IsAuthenticationRoute(endpoint.RoutePattern.RawText!)).ToArray();
        var discovered = routes.SelectMany(endpoint => endpoint.Metadata.GetRequiredMetadata<IHttpMethodMetadata>().HttpMethods
            .Select(method => method + " " + endpoint.RoutePattern.RawText)).ToArray();
        foreach (var key in discovered) Assert.True(Cases.ContainsKey(key), "Add session coverage for " + key);
        Assert.Equal(Cases.Keys.Order(StringComparer.Ordinal), discovered.Order(StringComparer.Ordinal));
        var policies = fixture.App.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        foreach (var endpoint in routes)
        {
            var route = endpoint.RoutePattern.RawText!;
            foreach (var method in endpoint.Metadata.GetRequiredMetadata<IHttpMethodMetadata>().HttpMethods)
            {
                var authCase = Cases[method + " " + route];
                if (authCase is AuthCase.Login or AuthCase.Refresh) Assert.NotNull(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
                else
                {
                    Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
                    var policy = await AuthorizationPolicy.CombineAsync(policies, endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
                    Assert.NotNull(policy);
                    Assert.Contains(policy.Requirements, requirement => requirement is DenyAnonymousAuthorizationRequirement);
                }
                var expectedLimiter = authCase is AuthCase.Refresh or AuthCase.Logout ? LoginProtection.SessionIpPolicy : null;
                Assert.Equal(expectedLimiter, endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName);
                await VerifyUserBoundaryAsync(fixture, route, authCase);
                if (authCase != AuthCase.Password) await VerifyRateLimitAsync(fixture, route, authCase);
            }
        }
    }

    private static async Task VerifyUserBoundaryAsync(ApiFixture fixture, string route, AuthCase authCase)
    {
        var user = await fixture.SeedUserAsync();
        var other = await fixture.SeedUserAsync();
        using var host = new SessionTestHost(fixture);
        var own = await host.LoginAsync(user.Email);
        var foreign = await host.LoginAsync(other.Email);
        var before = await SnapshotAsync(fixture, other.Id);
        using (var anonymous = await SendUnknownAsync(host, route)) Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        switch (authCase)
        {
            case AuthCase.Login:
                using (var response = await host.Client.PostAsJsonAsync(route, new { email = user.Email, password = ApiFixture.Password, userId = other.Id }, TestContext.Current.CancellationToken))
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    var pair = (await response.Content.ReadFromJsonAsync<SessionPair>(TestContext.Current.CancellationToken))!;
                    Assert.Equal(user.Id.ToString(), pair.Jwt.Subject);
                }
                break;
            case AuthCase.Refresh:
                // Possession of the refresh credential identifies the user; a Bearer/body hint cannot redirect it.
                host.Authorize(foreign);
                using (var response = await host.Client.PostAsJsonAsync(route, new { refreshToken = own.RefreshToken, userId = other.Id }, TestContext.Current.CancellationToken))
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    var pair = (await response.Content.ReadFromJsonAsync<SessionPair>(TestContext.Current.CancellationToken))!;
                    Assert.Equal(user.Id.ToString(), pair.Jwt.Subject);
                    Assert.Equal(own.SessionId, pair.SessionId);
                }
                break;
            case AuthCase.Logout:
            case AuthCase.Password:
                host.AuthorizeAs(user.Id, foreign.SessionId);
                using (var denied = await SendOwnedAsync(host, route, authCase)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
                host.Authorize(own);
                using (var allowed = await SendOwnedAsync(host, route, authCase)) Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
                break;
            default: throw new InvalidOperationException("Unhandled authentication guard case.");
        }
        Assert.True(before == await SnapshotAsync(fixture, other.Id), "An auth operation must not change another user's sessions, credentials or events.");
        using var stillActive = await host.RefreshAsync(foreign.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, stillActive.StatusCode);
    }

    private static async Task VerifyRateLimitAsync(ApiFixture fixture, string route, AuthCase authCase)
    {
        using var host = new SessionTestHost(fixture, builder => builder.UseSetting(
            authCase == AuthCase.Login ? "LoginRateLimit:IpPermitLimit" : "RefreshRateLimit:IpPermitLimit", "1"));
        using var first = await SendUnknownAsync(host, route);
        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        using var denied = await SendUnknownAsync(host, route);
        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
        Assert.True(denied.Headers.RetryAfter?.Delta is { TotalSeconds: > 0 });
    }

    private static Task<HttpResponseMessage> SendUnknownAsync(SessionTestHost host, string route) =>
        host.Client.PostAsJsonAsync(route, new { email = "missing@example.invalid", password = "synthetic-invalid-password", refreshToken = "synthetic-unknown", currentPassword = "synthetic-invalid-password", newPassword = "synthetic-new-password" }, TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> SendOwnedAsync(SessionTestHost host, string route, AuthCase authCase) => authCase == AuthCase.Logout
        ? host.Client.PostAsync(route, null, TestContext.Current.CancellationToken)
        : host.Client.PostAsJsonAsync(route, new { currentPassword = ApiFixture.Password, newPassword = "synthetic-guard-password-456" }, TestContext.Current.CancellationToken);

    private static Task<string?> SnapshotAsync(ApiFixture fixture, Guid userId) => fixture.Database.ScalarAsync<string>("""
        SELECT jsonb_build_object('account',to_jsonb(a),'sessions',(SELECT jsonb_agg(to_jsonb(s) ORDER BY id) FROM auth_session s WHERE user_id=a.id),
          'tokens',(SELECT jsonb_agg(to_jsonb(t) ORDER BY t.id) FROM refresh_token t JOIN auth_session s ON s.id=t.session_id WHERE s.user_id=a.id),
          'events',(SELECT jsonb_agg(to_jsonb(e) ORDER BY id) FROM auth_event e WHERE user_id=a.id))::text FROM app_user a WHERE id=@userId
        """, new { userId });
}
