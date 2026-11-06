using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Reconciliation.Core.Ingestion;

namespace Reconciliation.Tests;

public sealed class SummaryTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Worked_example_summary_has_half_paid_both_movements_and_explicit_payment_CFDI()
    {
        var seed = await MatchingRunScenario.SeedAsync(fixture);
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        await MatchingRunScenario.RunAsync(fixture, client, seed.Company);
        var id = await fixture.Database.ScalarAsync<Guid>("SELECT id FROM cfdi WHERE company_id=@company AND type='I'", new { company = seed.Company });
        using var response = await client.GetAsync($"/companies/{seed.Company}/cfdi/{id}/summary", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(11600, body.GetProperty("total").GetDecimal());
        Assert.Equal(5800, body.GetProperty("paid").GetDecimal());
        Assert.Equal(50, body.GetProperty("paidPercentage").GetDecimal());
        Assert.Equal(5800, body.GetProperty("pending").GetDecimal());
        Assert.Equal(0, body.GetProperty("uncovered").GetDecimal());
        Assert.Equal("issued", body.GetProperty("direction").GetString());
        Assert.Equal("PPD", body.GetProperty("paymentMethod").GetString());
        var links = body.GetProperty("links").EnumerateArray().ToArray();
        Assert.Equal(2, links.Length);
        var paid = Assert.Single(links, link => link.GetProperty("state").GetString() == "reconciled");
        Assert.NotEqual(JsonValueKind.Null, paid.GetProperty("paymentCfdiId").ValueKind);
        Assert.Equal("received", paid.GetProperty("complementStatus").GetString());
    }

    [Fact]
    public async Task Confirmed_PPD_without_complement_has_due_date_and_is_in_missing_filter()
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        await MatchingRunScenario.RunAsync(fixture, client, seed.Company);
        var link = await fixture.Database.ScalarAsync<Guid>("SELECT id FROM link WHERE company_id=@company", new { company = seed.Company });
        using var confirmed = await client.PostAsJsonAsync($"/companies/{seed.Company}/links/{link}/decisions", new { type = "confirm" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var body = await client.GetFromJsonAsync<JsonElement>($"/companies/{seed.Company}/cfdi/{seed.Invoice}/summary", TestContext.Current.CancellationToken);
        var payment = Assert.Single(body.GetProperty("links").EnumerateArray());
        Assert.Equal("missing", payment.GetProperty("complementStatus").GetString());
        Assert.Equal("2026-11-05", payment.GetProperty("dueDate").GetString());
        var list = await client.GetFromJsonAsync<JsonElement>($"/companies/{seed.Company}/cfdi?complementStatus=missing", TestContext.Current.CancellationToken);
        Assert.Equal(seed.Invoice, Assert.Single(list.GetProperty("invoices").EnumerateArray()).GetProperty("id").GetGuid());
        var overdue = await client.GetFromJsonAsync<JsonElement>($"/companies/{seed.Company}/cfdi?complementStatus=overdue", TestContext.Current.CancellationToken);
        Assert.Empty(overdue.GetProperty("invoices").EnumerateArray());
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync("UPDATE bank_movement SET movement_date='2026-09-10' WHERE id=@Movement", seed);
        var pastDue = await client.GetFromJsonAsync<JsonElement>($"/companies/{seed.Company}/cfdi?complementStatus=overdue", TestContext.Current.CancellationToken);
        var overdueInvoice = Assert.Single(pastDue.GetProperty("invoices").EnumerateArray());
        Assert.Equal(seed.Invoice, overdueInvoice.GetProperty("id").GetGuid());
        Assert.Equal("2026-10-05", overdueInvoice.GetProperty("dueDate").GetString());
    }

    [Fact]
    public async Task Cash_warning_uses_payment_form_and_amount_without_changing_link_state()
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 3000, 3000);
        var xml = Encoding.UTF8.GetString(SyntheticCfdi.Payment(seed.Uuid, remaining: "0.00", previous: "3000.00", paid: "3000.00")).Replace("FormaDePagoP=\"03\"", "FormaDePagoP=\"01\"", StringComparison.Ordinal);
        await fixture.App.Services.GetRequiredService<CfdiImporter>().ImportAsync(seed.Company, seed.Actor,
            [UploadedFile.FromBytes("synthetic-cash.xml", Encoding.UTF8.GetBytes(xml))], TestContext.Current.CancellationToken);
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        await MatchingRunScenario.RunAsync(fixture, client, seed.Company);
        var body = await client.GetFromJsonAsync<JsonElement>($"/companies/{seed.Company}/cfdi/{seed.Invoice}/summary", TestContext.Current.CancellationToken);
        var link = Assert.Single(body.GetProperty("links").EnumerateArray());
        Assert.Equal("reconciled", link.GetProperty("state").GetString());
        Assert.Equal("Cash payment above 2,000.00: results have no fiscal validity and deductibility must be checked", link.GetProperty("cashPaymentWarning").GetString());
    }
    [Fact]
    public async Task Pending_list_excludes_zero_payable_and_validates_filters()
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture);
        await MatchingFiscalScenario.ImportNoteAsync(fixture, seed, 11600);
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        var pending = await client.GetFromJsonAsync<JsonElement>($"/companies/{seed.Company}/cfdi?type=I&direction=issued&state=pending", TestContext.Current.CancellationToken);
        Assert.Empty(pending.GetProperty("invoices").EnumerateArray());
        using var invalid = await client.GetAsync($"/companies/{seed.Company}/cfdi?complementStatus=unknown", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var unknown = await client.GetAsync($"/companies/{seed.Company}/cfdi/{Guid.NewGuid()}/summary", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }
}
