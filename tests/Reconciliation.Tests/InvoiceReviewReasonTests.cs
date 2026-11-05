using System.Net.Http.Json;
using System.Text.Json;
using Dapper;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class InvoiceReviewReasonTests(ApiFixture fixture)
{
    [Theory(DisplayName = "Overpayment")]
    [InlineData(0, 12000, "reconciled", true)]
    [InlineData(1160, 11000, "reconciled", true)]
    [InlineData(0, 12000, "suggested", false)]
    [InlineData(0, 11600, "reconciled", false)]
    public async Task Overpaid_uses_only_reconciled_coverage_against_net_payable(decimal notes, decimal applied, string state, bool overpaid)
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, credit: applied);
        if (notes > 0) await MatchingFiscalScenario.ImportNoteAsync(fixture, seed, notes);
        await InsertManualLinkAsync(fixture, seed, applied, state);
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        var matching = (await client.GetFromJsonAsync<JsonElement>($"/companies/{seed.Company}/cfdi/{seed.Invoice}", TestContext.Current.CancellationToken)).GetProperty("matching");
        Assert.Equal(11600 - notes, matching.GetProperty("payable").GetDecimal());
        Assert.Equal(state, matching.GetProperty("state").GetString());
        Assert.Equal(overpaid, matching.GetProperty("requiresReview").GetBoolean());
        var reasons = matching.GetProperty("reasons").EnumerateArray().Where(reason => reason.GetProperty("code").GetString() == "overpaid").ToArray();
        if (overpaid) Assert.True(Assert.Single(reasons).GetProperty("requiresReview").GetBoolean());
        else Assert.Empty(reasons);
    }

    [Fact]
    public async Task Informational_global_reason_does_not_hide_an_overpaid_problem()
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 120,
            root => root.Element(SyntheticCfdi.Cfdi + "Receptor")!.SetAttributeValue("Rfc", "XAXX010101000"));
        await InsertManualLinkAsync(fixture, seed, 120, "reconciled");
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        var matching = (await client.GetFromJsonAsync<JsonElement>($"/companies/{seed.Company}/cfdi/{seed.Invoice}", TestContext.Current.CancellationToken)).GetProperty("matching");
        var reasons = matching.GetProperty("reasons").EnumerateArray().ToArray();
        Assert.Equal("reconciled", matching.GetProperty("state").GetString());
        Assert.False(Assert.Single(reasons, reason => reason.GetProperty("code").GetString() == "global invoice").GetProperty("requiresReview").GetBoolean());
        Assert.True(Assert.Single(reasons, reason => reason.GetProperty("code").GetString() == "overpaid").GetProperty("requiresReview").GetBoolean());
        Assert.True(matching.GetProperty("requiresReview").GetBoolean());
    }

    [Fact]
    public async Task Cancelled_credit_note_restores_payable_and_adds_reason_without_downgrading_valid_payments()
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture);
        var note = await MatchingFiscalScenario.ImportNoteAsync(fixture, seed, 1160);
        await InsertManualLinkAsync(fixture, seed, 10440, "reconciled");
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        var before = (await client.GetFromJsonAsync<JsonElement>($"/companies/{seed.Company}/cfdi/{seed.Invoice}", TestContext.Current.CancellationToken)).GetProperty("matching");
        Assert.Equal("reconciled", before.GetProperty("state").GetString());
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync(new CommandDefinition("UPDATE cfdi SET sat_status='cancelled' WHERE id=@note", new { note }, cancellationToken: TestContext.Current.CancellationToken));
        var after = (await client.GetFromJsonAsync<JsonElement>($"/companies/{seed.Company}/cfdi/{seed.Invoice}", TestContext.Current.CancellationToken)).GetProperty("matching");
        Assert.Equal(11600, after.GetProperty("payable").GetDecimal());
        Assert.Equal(1160, after.GetProperty("pendingAmount").GetDecimal());
        Assert.Equal("pending", after.GetProperty("state").GetString());
        Assert.True(Assert.Single(after.GetProperty("reasons").EnumerateArray(), reason => reason.GetProperty("code").GetString() == "cancelled credit note").GetProperty("requiresReview").GetBoolean());
        Assert.Equal("reconciled", await fixture.Database.ScalarAsync<string>("SELECT state FROM link WHERE company_id=@company", new { company = seed.Company }));
    }

    [Fact]
    public async Task Different_currency_credit_note_is_not_netted_and_is_a_review_problem()
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture);
        await MatchingFiscalScenario.ImportNoteAsync(fixture, seed, 1160, currency: "USD");
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        var matching = (await client.GetFromJsonAsync<JsonElement>($"/companies/{seed.Company}/cfdi/{seed.Invoice}", TestContext.Current.CancellationToken)).GetProperty("matching");
        Assert.Equal(0, matching.GetProperty("creditNotes").GetDecimal());
        Assert.Equal(11600, matching.GetProperty("payable").GetDecimal());
        Assert.True(Assert.Single(matching.GetProperty("reasons").EnumerateArray(), reason => reason.GetProperty("code").GetString() == "credit note in a different currency").GetProperty("requiresReview").GetBoolean());
        Assert.True(matching.GetProperty("requiresReview").GetBoolean());
    }

    internal static async Task InsertManualLinkAsync(ApiFixture fixture, MatchingFiscalScenario.Seed seed, decimal applied, string state)
    {
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync(new CommandDefinition("""
            INSERT INTO link(company_id,movement_id,cfdi_id,amount_applied,state,origin,criteria,explanation)
            VALUES(@company,@movement,@invoice,@applied,@state,'user','[]','Synthetic user-confirmed fiscal fixture')
            """, new { company = seed.Company, movement = seed.Movement, invoice = seed.Invoice, applied, state }, cancellationToken: TestContext.Current.CancellationToken));
    }
}
