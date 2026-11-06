using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class ReviewQueueTests(ApiFixture fixture)
{
    [Fact]
    public async Task Queue_shows_suggestions_and_reasons_but_excludes_reconciled_and_informational_items()
    {
        var seed = await MatchingRunScenario.SeedAsync(fixture);
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        await MatchingRunScenario.RunAsync(fixture, client, seed.Company);
        using var response = await client.GetAsync($"/companies/{seed.Company}/review-queue", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var item = Assert.Single(body.GetProperty("entries").EnumerateArray());
        Assert.Equal("link", item.GetProperty("kind").GetString());
        Assert.NotEmpty(item.GetProperty("criteria").EnumerateArray());
        Assert.True(item.GetProperty("confidence").GetString() is "high" or "medium" or "low");
        Assert.False(string.IsNullOrEmpty(item.GetProperty("explanation").GetString()));
    }

    [Fact]
    public async Task Queue_includes_overpaid_invoice_and_not_informational_global_invoice()
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 120);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync("""
            INSERT INTO link(company_id,movement_id,cfdi_id,amount_applied,state,origin,criteria,explanation)
            VALUES(@Company,@Movement,@Invoice,120,'reconciled','user','[]','Synthetic overpayment');
            UPDATE cfdi SET is_global=true WHERE id=@Invoice
            """, seed);
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        var body = await client.GetFromJsonAsync<JsonElement>($"/companies/{seed.Company}/review-queue", TestContext.Current.CancellationToken);
        var item = Assert.Single(body.GetProperty("entries").EnumerateArray());
        Assert.Equal("invoice", item.GetProperty("kind").GetString());
        Assert.Contains(item.GetProperty("reasons").EnumerateArray(), reason => reason.GetProperty("code").GetString() == "overpaid" && reason.GetProperty("requiresReview").GetBoolean());
        await owner.ExecuteAsync("UPDATE link SET amount_applied=100 WHERE company_id=@Company", seed);
        var clear = await client.GetFromJsonAsync<JsonElement>($"/companies/{seed.Company}/review-queue", TestContext.Current.CancellationToken);
        Assert.Empty(clear.GetProperty("entries").EnumerateArray());
    }
    [Fact]
    public async Task Queue_keyset_orders_confidence_and_validates_page_limit()
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync("""
            WITH movements AS (
              INSERT INTO bank_movement(company_id,bank_account_id,movement_date,description,credit,debit,balance)
              SELECT @Company,bank_account_id,'2026-10-10',label,10,0,10 FROM bank_movement,
                (VALUES('high'),('medium'),('low')) v(label) WHERE id=@Movement RETURNING id,description
            ) INSERT INTO link(company_id,movement_id,cfdi_id,amount_applied,state,origin,criteria,confidence,explanation)
            SELECT @Company,id,@Invoice,10,'suggested','automatic','[]',description,'Synthetic ranking' FROM movements
            """, seed);
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        var url = $"/companies/{seed.Company}/review-queue?limit=1";
        var seen = new HashSet<Guid>();
        foreach (var confidence in new[] { "high", "medium", "low" })
        {
            var body = await client.GetFromJsonAsync<JsonElement>(url, TestContext.Current.CancellationToken);
            var item = Assert.Single(body.GetProperty("entries").EnumerateArray());
            Assert.Equal(confidence, item.GetProperty("confidence").GetString());
            Assert.True(seen.Add(item.GetProperty("id").GetGuid()));
            if (confidence != "low") url = $"/companies/{seed.Company}/review-queue?limit=1&afterRank={body.GetProperty("nextAfterRank")}&afterDate={body.GetProperty("nextAfterDate").GetString()}&afterId={body.GetProperty("nextAfterId").GetString()}";
            else Assert.Equal(JsonValueKind.Null, body.GetProperty("nextAfterId").ValueKind);
        }
        using var invalid = await client.GetAsync($"/companies/{seed.Company}/review-queue?limit=201", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
}
