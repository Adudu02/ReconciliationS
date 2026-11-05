using System.Net;
using System.Net.Http.Json;
using System.Text;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Reconciliation.Core.Ingestion;
using Reconciliation.Core.Jobs;

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

    [Fact(DisplayName = "Tolerance changed")]
    public async Task Settings_apply_to_next_reconciliation_run()
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        await fixture.App.Services.GetRequiredService<CfdiImporter>().ImportAsync(company, user.Id,
            [UploadedFile.FromBytes("synthetic.xml", SyntheticCfdi.Invoice(root => root.SetAttributeValue("Fecha", "2026-10-10T12:00:00")))], TestContext.Current.CancellationToken);
        await fixture.App.Services.GetRequiredService<BankStatementImporter>().ImportAsync(company, user.Id, "reference", "Synthetic-Tolerance",
            [UploadedFile.FromBytes("synthetic.csv", Encoding.UTF8.GetBytes("fecha,concepto,abono,cargo,saldo\n2026-10-10,transfer,1939.29,0,1939.29\n"))], TestContext.Current.CancellationToken);
        using var client = await fixture.LoginAsync(user.Email, company);
        await MatchingRunScenario.RunAsync(fixture, client, company);
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM link WHERE company_id=@company", new { company }));
        using var changed = await client.PatchAsJsonAsync($"/companies/{company}/settings", new { amountTolerance = .02m }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        await MatchingRunScenario.RunAsync(fixture, client, company);
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM link WHERE company_id=@company AND state='suggested' AND criteria @> '[{\"code\":\"amount-within-tolerance\"}]'", new { company }));
    }

    [Fact(DisplayName = "Delete attempt")]
    public async Task Imported_CFDI_retained_on_delete_attempt()
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
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
