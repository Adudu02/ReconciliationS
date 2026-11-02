using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Sdk;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class SessionIsolationTests(ApiFixture fixture)
{
    [Fact]
    public async Task Every_new_auth_route_must_have_an_explicit_guard_case()
    {
        var original = fixture.App;
        using var mutant = original.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddTransient<IStartupFilter, UncoveredAuthStartup>()));
        fixture.App = mutant;
        try
        {
            var error = await Assert.ThrowsAnyAsync<XunitException>(() => TenantIsolationChecks.VerifyRoutesAsync(fixture));
            Assert.Contains("Add session coverage for POST /auth/uncovered", error.Message, StringComparison.Ordinal);
        }
        finally { fixture.App = original; }
    }

    [Fact]
    public async Task The_guard_detects_refresh_that_ignores_its_credential()
    {
        var original = fixture.App;
        using var mutant = original.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddTransient<IStartupFilter, BypassedRefreshStartup>()));
        fixture.App = mutant;
        try { await Assert.ThrowsAnyAsync<XunitException>(() => TenantIsolationChecks.VerifyRoutesAsync(fixture)); }
        finally { fixture.App = original; }
    }

    [Fact]
    public async Task Legacy_bearer_without_sid_keeps_working_until_its_original_expiry()
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        using var host = new SessionTestHost(fixture);
        host.AuthorizeAs(user.Id, null, accessMinutes: 60);
        host.Client.DefaultRequestHeaders.Add("X-Company-Id", company.ToString());
        host.Clock.Advance(TimeSpan.FromMinutes(16));
        using var unchanged = await host.Client.GetAsync($"/companies/{company}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
        using var noSession = await host.Client.PostAsync("/auth/logout", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, noSession.StatusCode);
        host.Clock.Advance(TimeSpan.FromMinutes(44));
        using var expired = await host.Client.GetAsync($"/companies/{company}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    private sealed class UncoveredAuthStartup : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            // Test-only probe: a future auth route must fail until its real guard case is registered.
            app.UseEndpoints(routes => routes.MapPost("/auth/uncovered", () => Results.NoContent()).AllowAnonymous());
        };
    }

    private sealed class BypassedRefreshStartup : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (HttpContext httpContext, RequestDelegate nextRequest) =>
            {
                if (httpContext.Request.Path == "/auth/refresh")
                    httpContext.Response.StatusCode = StatusCodes.Status200OK;
                else await nextRequest(httpContext);
            });
            next(app);
        };
    }
}
