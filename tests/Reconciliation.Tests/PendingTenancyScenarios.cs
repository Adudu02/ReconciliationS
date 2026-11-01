using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Reconciliation.Core.Ingestion;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class PendingTenancyScenarios(ApiFixture fixture)
{
    [Fact(DisplayName = "Viewer cannot decide", Skip = "pending group 8: confirm a real suggested link and verify nothing changes")]
    public void Viewer_cannot_decide_on_real_link() => Assert.Fail("Replace with real link-decision scenario.");

    [Fact(DisplayName = "Different roles", Skip = "pending group 8: successful link decision in A and read-only access in B")]
    public void Different_roles_on_real_links() => Assert.Fail("Replace with real link-decision scenario.");
    [Fact(DisplayName = "Cross-company attempt in the test suite")]
    public async Task Real_invoice_cross_company_read()
    {
        var userA = await fixture.SeedUserAsync();
        var userB = await fixture.SeedUserAsync();
        var a = await fixture.SeedCompanyAsync(userA.Id);
        var b = await fixture.SeedCompanyAsync(userB.Id);
        var report = await fixture.App.Services.GetRequiredService<CfdiImporter>().ImportAsync(b, userB.Id,
            [UploadedFile.FromBytes("synthetic.xml", SyntheticCfdi.Invoice())], TestContext.Current.CancellationToken);
        using var owner = await fixture.LoginAsync(userB.Email, b);
        using var baseline = await owner.GetAsync($"/companies/{b}/cfdi/{report.Files[0].CfdiId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, baseline.StatusCode);
        using var client = await fixture.LoginAsync(userA.Email, a);
        using var response = await client.GetAsync($"/companies/{a}/cfdi/{report.Files[0].CfdiId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact(DisplayName = "Tolerance changed", Skip = "pending group 6: persisted settings apply to the next reconciliation run")]
    public void Settings_apply_to_next_reconciliation_run() => Assert.Fail("Replace with real reconciliation-run scenario.");

    [Fact(DisplayName = "Delete attempt")]
    public async Task Imported_CFDI_retained_on_delete_attempt()
    {
        var user = await fixture.SeedUserAsync();
        var company = await fixture.SeedCompanyAsync(user.Id);
        var bytes = SyntheticCfdi.Invoice();
        var report = await fixture.App.Services.GetRequiredService<CfdiImporter>().ImportAsync(company, user.Id,
            [UploadedFile.FromBytes("synthetic.xml", bytes)], TestContext.Current.CancellationToken);
        var path = $"/companies/{company}/cfdi/{report.Files[0].CfdiId}";
        using var client = await fixture.LoginAsync(user.Email, company);
        using var before = await client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        var originalInvoice = await before.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var deleted = await client.DeleteAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, deleted.StatusCode);
        using var after = await client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(originalInvoice, await after.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var original = await client.GetAsync(path + "/xml", TestContext.Current.CancellationToken);
        Assert.Equal(bytes, await original.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }
}
