using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Reconciliation.Core.Ingestion;
using Reconciliation.Migrator;

namespace Reconciliation.Tests;

internal static class TenantIsolationChecks
{
    private sealed record Resources(Guid InvoiceId, Guid UploadId, Guid FileId, Guid JobId);
    public static async Task<IEnumerable<string>> TenantTablesAsync(NpgsqlConnection owner) =>
        await owner.QueryAsync<string>(new CommandDefinition("""
            SELECT DISTINCT table_name FROM information_schema.columns
            WHERE table_schema = 'public' AND (column_name = 'company_id' OR table_name = 'company')
            ORDER BY table_name
            """, cancellationToken: TestContext.Current.CancellationToken));

    public static async Task VerifyTablesAsync(NpgsqlConnection owner)
    {
        await JobsDatabaseTests.AssertQueueRoleBoundaryAsync(owner);
        await AuditDatabaseTests.AssertAuditRoleBoundaryAsync(owner);
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
        await AuthIsolationChecks.VerifyAsync(fixture);
        var user = await fixture.SeedUserAsync();
        var other = await fixture.SeedUserAsync();
        var a = await fixture.SeedCompanyAsync(user.Id);
        var b = await fixture.SeedCompanyAsync(other.Id);
        using var client = await fixture.LoginAsync(user.Email, a);
        using var anonymous = fixture.App.CreateClient();
        var operatorEmail = $"isolation-operator-{Guid.NewGuid():N}@example.invalid";
        await OperatorBootstrap.SeedAsync(fixture.Database.OwnerConnectionString, operatorEmail, ApiFixture.Password, TestContext.Current.CancellationToken);
        using var platform = await fixture.LoginAsync(operatorEmail);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        var importer = fixture.App.Services.GetRequiredService<CfdiImporter>();
        var resourceA = await SeedResourcesAsync(importer, owner, a, user.Id);
        var resourceB = await SeedResourcesAsync(importer, owner, b, other.Id);
        var before = await SnapshotAsync(owner, b);
        var source = fixture.App.Services.GetRequiredService<EndpointDataSource>();
        var policies = fixture.App.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var exercised = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in source.Endpoints.OfType<RouteEndpoint>().OrderBy(endpoint => endpoint.RoutePattern.RawText?.EndsWith("/archive", StringComparison.Ordinal) == true ? 1 : 0))
        {
            var route = endpoint.RoutePattern.RawText!;
            var methods = endpoint.Metadata.GetRequiredMetadata<IHttpMethodMetadata>().HttpMethods;
            if (AuthIsolationChecks.IsAuthenticationRoute(route)) continue;
            if (route is "/health" or "/openapi.json")
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
                using var baseline = Request(method, route, a, other.Id, resourceA);
                using var success = await (route == "/companies" ? platform : client).SendAsync(baseline, TestContext.Current.CancellationToken);
                Assert.True(success.IsSuccessStatusCode, $"Baseline must exercise real endpoint: {key}, {success.StatusCode}");
                using var noCredentials = Request(method, route, b, other.Id, resourceB);
                using var unauthorized = await anonymous.SendAsync(noCredentials, TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
                foreach (var selected in new[] { a, b })
                {
                    client.DefaultRequestHeaders.Remove("X-Company-Id");
                    client.DefaultRequestHeaders.Add("X-Company-Id", selected.ToString());
                    // A test-only route has no resource id; choose B to exercise membership refusal.
                    if (route == "/test/decisions" && selected == a) continue;
                    using var attempt = Request(method, route, b, other.Id, resourceB);
                    using var response = await client.SendAsync(attempt, TestContext.Current.CancellationToken);
                    Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, $"Cross-company access: {key}, {response.StatusCode}");
                }
                client.DefaultRequestHeaders.Remove("X-Company-Id");
                client.DefaultRequestHeaders.Add("X-Company-Id", a.ToString());
                if (route.Contains("{invoiceId:", StringComparison.Ordinal) || route.Contains("{uploadId:", StringComparison.Ordinal) || route.Contains("{fileId:", StringComparison.Ordinal) || route.Contains("{jobId:", StringComparison.Ordinal))
                {
                    using var foreignResource = Request(method, route, a, other.Id, resourceB);
                    using var hidden = await client.SendAsync(foreignResource, TestContext.Current.CancellationToken);
                    Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
                }
                exercised.Add(key);
            }
        }
        Assert.Equal(before, await SnapshotAsync(owner, b));
        Assert.Contains("GET /companies/{companyId:guid}", exercised);
        Assert.Contains("POST /companies/{companyId:guid}/members", exercised);
    }

    private static async Task<Resources> SeedResourcesAsync(CfdiImporter importer, NpgsqlConnection owner, Guid company, Guid actor)
    {
        var report = await importer.ImportAsync(company, actor, [UploadedFile.FromBytes("synthetic.xml", SyntheticCfdi.Invoice())], TestContext.Current.CancellationToken);
        var jobId = await owner.QuerySingleAsync<Guid>(new CommandDefinition("INSERT INTO job(company_id,type,state,created_by) VALUES(@company,'status_check','failed',@actor) RETURNING id", new { company, actor }, cancellationToken: TestContext.Current.CancellationToken));
        return new Resources(report.Files[0].CfdiId!.Value, report.UploadId, report.Files[0].FileId, jobId);
    }

    private static async Task<string> SnapshotAsync(NpgsqlConnection owner, Guid company)
    {
        var rows = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in await TenantTablesAsync(owner))
        {
            using var builder = new NpgsqlCommandBuilder();
            var identifier = builder.QuoteIdentifier(table);
            var key = table == "company" ? "id" : "company_id";
            rows[table] = await owner.QuerySingleAsync<string>(new CommandDefinition(
                $"SELECT coalesce(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text), '[]')::text FROM {identifier} t WHERE {key} = @company",
                new { company }, cancellationToken: TestContext.Current.CancellationToken));
        }
        return JsonSerializer.Serialize(rows);
    }

    private static HttpRequestMessage Request(string method, string route, Guid company, Guid user, Resources resources)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), route.Replace("{companyId:guid}", company.ToString(), StringComparison.Ordinal)
            .Replace("{invoiceId:guid}", resources.InvoiceId.ToString(), StringComparison.Ordinal)
            .Replace("{uploadId:guid}", resources.UploadId.ToString(), StringComparison.Ordinal)
            .Replace("{fileId:guid}", resources.FileId.ToString(), StringComparison.Ordinal)
            .Replace("{jobId:guid}", resources.JobId.ToString(), StringComparison.Ordinal));
        // A new endpoint must get a real positive baseline and a cross-company request here.
        switch ($"{method} {route}")
        {
            case "GET /companies/{companyId:guid}/audit-entries":
            case "GET /companies/{companyId:guid}/audit/verification":
            case "GET /companies/{companyId:guid}/jobs/{jobId:guid}":
            case "POST /companies/{companyId:guid}/jobs/{jobId:guid}/retry":
            case "GET /companies/{companyId:guid}":
            case "GET /companies/{companyId:guid}/members":
            case "GET /companies/{companyId:guid}/settings":
            case "POST /companies/{companyId:guid}/archive":
            case "POST /companies/{companyId:guid}/reconciliation-runs":
            case "GET /companies/{companyId:guid}/banks":
            case "GET /companies/{companyId:guid}/imports/{uploadId:guid}":
            case "GET /companies/{companyId:guid}/cfdi/{invoiceId:guid}":
            case "GET /companies/{companyId:guid}/cfdi/{invoiceId:guid}/xml":
            case "GET /companies/{companyId:guid}/imports/files/{fileId:guid}/original":
            case "POST /test/decisions":
                break;
            case "POST /companies/{companyId:guid}/imports/cfdi":
                request.Content = IngestionHttpTests.CfdiBody(SyntheticCfdi.Invoice());
                break;
            case "POST /companies/{companyId:guid}/imports/bank-statement":
                request.Content = new MultipartFormDataContent
                {
                    { new StringContent("reference"), "bank" }, { new StringContent("Synthetic-1234"), "accountReference" },
                    { new ByteArrayContent(Encoding.UTF8.GetBytes("fecha,concepto,abono,cargo,saldo\n2026-10-10,SYNTHETIC,1,0,1\n")), "files", "synthetic.csv" }
                };
                break;
            case "GET /companies/{companyId:guid}/jobs":
                request.RequestUri = new Uri(request.RequestUri + "?status=failed", UriKind.Relative);
                break;
            case "POST /companies":
                request.Content = JsonContent.Create(new { rfc = "AAA010101AAA", name = "Synthetic isolated", adminEmail = $"isolation-admin-{Guid.NewGuid():N}@example.invalid", adminPassword = ApiFixture.Password });
                break;
            case "PATCH /companies/{companyId:guid}/settings":
                request.Content = JsonContent.Create(new { amountTolerance = 0.02m });
                break;
            case "POST /companies/{companyId:guid}/members":
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
