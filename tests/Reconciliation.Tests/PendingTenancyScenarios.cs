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
    [Fact(DisplayName = "Viewer cannot decide")]
    public async Task Viewer_cannot_decide_on_real_link()
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        using var admin = await fixture.LoginAsync(seed.Email, seed.Company);
        await MatchingRunScenario.RunAsync(fixture, admin, seed.Company);
        var viewer = await fixture.SeedUserAsync();
        using var membership = await admin.PostAsJsonAsync($"/companies/{seed.Company}/members", new { userId = viewer.Id, role = "viewer" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, membership.StatusCode);
        var link = await fixture.Database.ScalarAsync<Guid>("SELECT id FROM link WHERE company_id=@company", new { company = seed.Company });
        var before = await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM audit_entry WHERE company_id=@company", new { company = seed.Company });
        using var client = await fixture.LoginAsync(viewer.Email, seed.Company);
        using var response = await client.PostAsJsonAsync($"/companies/{seed.Company}/links/{link}/decisions", new { type = "confirm" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("suggested", await fixture.Database.ScalarAsync<string>("SELECT state FROM link WHERE id=@link", new { link }));
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM link_decision WHERE link_id=@link", new { link }));
        Assert.Equal(before, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM audit_entry WHERE company_id=@company", new { company = seed.Company }));
    }

    [Fact(DisplayName = "Different roles")]
    public async Task Different_roles_on_real_links()
    {
        var a = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        var b = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        using var adminA = await fixture.LoginAsync(a.Email, a.Company);
        using var adminB = await fixture.LoginAsync(b.Email, b.Company);
        await MatchingRunScenario.RunAsync(fixture, adminA, a.Company);
        await MatchingRunScenario.RunAsync(fixture, adminB, b.Company);
        var user = await fixture.SeedUserAsync();
        using var memberA = await adminA.PostAsJsonAsync($"/companies/{a.Company}/members", new { userId = user.Id, role = "accountant" }, TestContext.Current.CancellationToken);
        using var memberB = await adminB.PostAsJsonAsync($"/companies/{b.Company}/members", new { userId = user.Id, role = "viewer" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, memberA.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, memberB.StatusCode);
        var linkA = await fixture.Database.ScalarAsync<Guid>("SELECT id FROM link WHERE company_id=@company", new { company = a.Company });
        var linkB = await fixture.Database.ScalarAsync<Guid>("SELECT id FROM link WHERE company_id=@company", new { company = b.Company });
        using var clientA = await fixture.LoginAsync(user.Email, a.Company);
        using var clientB = await fixture.LoginAsync(user.Email, b.Company);
        using var accepted = await clientA.PostAsJsonAsync($"/companies/{a.Company}/links/{linkA}/decisions", new { type = "confirm" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var read = await clientB.GetAsync($"/companies/{b.Company}/cfdi/{b.Invoice}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var refused = await clientB.PostAsJsonAsync($"/companies/{b.Company}/links/{linkB}/decisions", new { type = "confirm" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }
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
