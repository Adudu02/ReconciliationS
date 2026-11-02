using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Reconciliation.Core.Ingestion;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class SessionRolesTests(ApiFixture fixture)
{
    [Fact]
    public async Task Membership_changes_are_live_during_a_session_and_no_roles_are_stored_in_credentials()
    {
        var (admin, company) = await fixture.SeedCompanyWithAdminAsync();
        var accountant = await fixture.SeedUserAsync();
        using var administrator = await fixture.LoginAsync(admin.Email, company);
        using var added = await administrator.PostAsJsonAsync($"/companies/{company}/members", new { userId = accountant.Id, role = "accountant" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, added.StatusCode);
        using var host = new SessionTestHost(fixture);
        var pair = await host.LoginAsync(accountant.Email);
        host.Authorize(pair, company);
        using var imported = await ImportAsync(host, company);
        Assert.Equal(HttpStatusCode.Accepted, imported.StatusCode);
        using var changed = await administrator.PostAsJsonAsync($"/companies/{company}/members", new { userId = accountant.Id, role = "viewer" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        using var forbidden = await ImportAsync(host, company);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var readable = await host.Client.GetAsync($"/companies/{company}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, readable.StatusCode);
        using var renewed = await host.RefreshAsync(pair.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        var fresh = (await renewed.Content.ReadFromJsonAsync<SessionPair>(TestContext.Current.CancellationToken))!;
        host.Authorize(fresh, company);
        using var stillViewer = await ImportAsync(host, company);
        Assert.Equal(HttpStatusCode.Forbidden, stillViewer.StatusCode);
        foreach (var credential in new[] { pair, fresh })
            Assert.DoesNotContain(credential.Jwt.Claims, claim => claim.Type is "role" or "roles" or ClaimTypes.Role);
        Assert.Equal(0, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM information_schema.columns WHERE table_schema='public' AND table_name IN ('auth_session','refresh_token') AND column_name ILIKE '%role%'"));
    }

    private static async Task<HttpResponseMessage> ImportAsync(SessionTestHost host, Guid companyId)
    {
        using var body = IngestionHttpTests.CfdiBody(SyntheticCfdi.Invoice());
        return await host.Client.PostAsync($"/companies/{companyId}/imports/cfdi", body, TestContext.Current.CancellationToken);
    }
}
