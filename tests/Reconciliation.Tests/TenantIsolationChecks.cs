using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Reconciliation.Tests;

internal static class TenantIsolationChecks
{
    public static async Task<IEnumerable<string>> TenantTablesAsync(NpgsqlConnection owner) =>
        await owner.QueryAsync<string>(new CommandDefinition("""
            SELECT DISTINCT table_name FROM information_schema.columns
            WHERE table_schema = 'public' AND (column_name = 'company_id' OR table_name = 'company')
            ORDER BY table_name
            """, cancellationToken: TestContext.Current.CancellationToken));

    public static async Task VerifyTablesAsync(NpgsqlConnection owner)
    {
        foreach (var table in await TenantTablesAsync(owner))
        {
            var state = await owner.QuerySingleAsync<(bool Enabled, bool Forced, long Policies)>(new CommandDefinition("""
                SELECT c.relrowsecurity, c.relforcerowsecurity, (SELECT count(*) FROM pg_policy p WHERE p.polrelid = c.oid)
                FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public' AND c.relname = @table
                """, new { table }, cancellationToken: TestContext.Current.CancellationToken));
            Assert.True(state.Enabled && state.Forced && state.Policies > 0, $"Missing forced RLS or policy: {table}");
        }
    }

    public static async Task VerifyRoutesAsync(ApiFixture fixture)
    {
        var user = await fixture.SeedUserAsync();
        var other = await fixture.SeedUserAsync();
        var a = await fixture.SeedCompanyAsync(user.Id);
        var b = await fixture.SeedCompanyAsync(other.Id);
        using var client = await fixture.LoginAsync(user.Email, a);
        using var anonymous = fixture.App.CreateClient();
        var source = fixture.App.Services.GetRequiredService<EndpointDataSource>();
        var policies = fixture.App.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var exercised = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in source.Endpoints.OfType<RouteEndpoint>())
        {
            var route = endpoint.RoutePattern.RawText!;
            var methods = endpoint.Metadata.GetRequiredMetadata<IHttpMethodMetadata>().HttpMethods;
            if (route is "/health" or "/openapi.json" or "/auth/tokens")
            {
                Assert.NotNull(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
                continue;
            }
            Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
            var policy = await AuthorizationPolicy.CombineAsync(policies, endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.NotNull(policy);
            Assert.Contains(policy.Requirements, requirement => requirement is DenyAnonymousAuthorizationRequirement);
            foreach (var method in methods)
            {
                var key = $"{method} {route}";
                using var baseline = Request(method, route, a, other.Id);
                using var success = await client.SendAsync(baseline, TestContext.Current.CancellationToken);
                Assert.True(success.IsSuccessStatusCode, $"Baseline must exercise real endpoint: {key}, {success.StatusCode}");
                using var noCredentials = Request(method, route, b, other.Id);
                using var unauthorized = await anonymous.SendAsync(noCredentials, TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
                foreach (var selected in new[] { a, b })
                {
                    client.DefaultRequestHeaders.Remove("X-Company-Id");
                    client.DefaultRequestHeaders.Add("X-Company-Id", selected.ToString());
                    // A test-only route has no resource id; choose B to exercise membership refusal.
                    if (route == "/test/decisions" && selected == a) continue;
                    using var attempt = Request(method, route, b, other.Id);
                    using var response = await client.SendAsync(attempt, TestContext.Current.CancellationToken);
                    Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, $"Cross-company access: {key}, {response.StatusCode}");
                }
                client.DefaultRequestHeaders.Remove("X-Company-Id");
                client.DefaultRequestHeaders.Add("X-Company-Id", a.ToString());
                exercised.Add(key);
            }
        }
        Assert.Contains("GET /companies/{id:guid}", exercised);
        Assert.Contains("POST /companies/{id:guid}/members", exercised);
    }

    private static HttpRequestMessage Request(string method, string route, Guid company, Guid user)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), route.Replace("{id:guid}", company.ToString(), StringComparison.Ordinal));
        // A new endpoint must get a real positive baseline and a cross-company request here.
        switch ($"{method} {route}")
        {
            case "GET /companies/{id:guid}":
            case "GET /companies/{id:guid}/members":
            case "POST /test/decisions":
                break;
            case "POST /companies/{id:guid}/members":
                request.Content = JsonContent.Create(new { userId = user, role = "viewer" });
                break;
            default:
                request.Dispose();
                Assert.Fail($"Add cross-company coverage for {method} {route}");
                break;
        }
        return request;
    }
}
