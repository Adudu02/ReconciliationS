using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using Dapper;
using Reconciliation.Core.Matching;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class GlobalInvoiceMatchingTests(ApiFixture fixture)
{
    [Theory]
    [InlineData(true, "BBB010101BBB")]
    [InlineData(false, "XAXX010101000")]
    [InlineData(false, "xaxx010101000")]
    public void Global_invoice_is_excluded_from_both_stages_even_with_exact_complement_and_identifiers(bool flag, string receiver)
    {
        var invoice = SyntheticMatching.Invoice(100) with { IsGlobal = flag, ReceiverRfc = receiver, Serie = "FC", Folio = "0001" };
        var movement = SyntheticMatching.FirstMovement(100) with { Description = receiver + " FC0001", Reference = "ABC123" };
        Assert.Empty(MatchingEngine.Run([movement], [invoice], [SyntheticMatching.Payment(100)], [], new(3, .01m)).Changes);
        Assert.Empty(MatchingEngine.Run([movement], [invoice], [], [], new(3, .01m)).Changes);
    }

    [Theory(DisplayName = "Global invoice pending")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stored_global_flag_or_generic_receiver_excludes_all_proposals_and_explains_pending(bool flag)
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100, root =>
        {
            if (flag) root.Add(new XElement(SyntheticCfdi.Cfdi + "InformacionGlobal", new XAttribute("Periodicidad", "01"), new XAttribute("Meses", "10"), new XAttribute("Año", "2026")));
            else root.Element(SyntheticCfdi.Cfdi + "Receptor")!.SetAttributeValue("Rfc", "XAXX010101000");
        });
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        await MatchingRunScenario.RunAsync(fixture, client, seed.Company);
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM link WHERE company_id=@company", new { company = seed.Company }));
        var invoice = await client.GetFromJsonAsync<JsonElement>($"/companies/{seed.Company}/cfdi/{seed.Invoice}", TestContext.Current.CancellationToken);
        var matching = invoice.GetProperty("matching");
        Assert.Equal("pending", matching.GetProperty("state").GetString());
        var reason = Assert.Single(matching.GetProperty("reasons").EnumerateArray(), reason => reason.GetProperty("code").GetString() == "global invoice");
        Assert.False(reason.GetProperty("requiresReview").GetBoolean());
        Assert.False(matching.GetProperty("requiresReview").GetBoolean());
    }

    [Fact]
    public async Task Manually_reconciled_global_invoice_keeps_state_and_informational_reason()
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100, root => root.Element(SyntheticCfdi.Cfdi + "Receptor")!.SetAttributeValue("Rfc", "XAXX010101000"));
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync(new CommandDefinition("""
            INSERT INTO link(company_id,movement_id,cfdi_id,amount_applied,state,origin,criteria,explanation)
            VALUES(@company,@movement,@invoice,100,'reconciled','user','[]','Synthetic manual global link')
            """, new { company = seed.Company, movement = seed.Movement, invoice = seed.Invoice }, cancellationToken: TestContext.Current.CancellationToken));
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        await MatchingRunScenario.RunAsync(fixture, client, seed.Company);
        var invoice = await client.GetFromJsonAsync<JsonElement>($"/companies/{seed.Company}/cfdi/{seed.Invoice}", TestContext.Current.CancellationToken);
        var matching = invoice.GetProperty("matching");
        Assert.Equal("reconciled", matching.GetProperty("state").GetString());
        Assert.Equal(0, matching.GetProperty("pendingAmount").GetDecimal());
        Assert.False(Assert.Single(matching.GetProperty("reasons").EnumerateArray(), reason => reason.GetProperty("code").GetString() == "global invoice").GetProperty("requiresReview").GetBoolean());
        Assert.False(matching.GetProperty("requiresReview").GetBoolean());
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM link WHERE company_id=@company AND origin='user' AND state='reconciled'", new { company = seed.Company }));
    }
}
