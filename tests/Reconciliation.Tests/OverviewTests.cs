using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;

namespace Reconciliation.Tests;

public sealed class OverviewTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Overview_state_totals_include_classified_movements_and_zero_payable_invoices()
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        await MatchingFiscalScenario.ImportNoteAsync(fixture, seed, 100);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync("UPDATE bank_movement SET classification='loan' WHERE id=@Movement", seed);
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        using var response = await client.GetAsync($"/companies/{seed.Company}/overview", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        foreach (var name in new[] { "invoices", "movements" })
        {
            var resource = body.GetProperty(name);
            Assert.Equal(1, resource.GetProperty("count").GetInt64());
            Assert.Equal(100, resource.GetProperty("amount").GetDecimal());
            Assert.Equal(resource.GetProperty("count").GetInt64(), resource.GetProperty("states").EnumerateArray().Sum(row => row.GetProperty("count").GetInt64()));
            Assert.Equal(resource.GetProperty("amount").GetDecimal(), resource.GetProperty("states").EnumerateArray().Sum(row => row.GetProperty("amount").GetDecimal()));
        }
        Assert.Contains("results have no fiscal or legal validity", body.GetProperty("disclaimer").GetString(), StringComparison.Ordinal);
        Assert.Contains("Classified", body.GetProperty("totalsNote").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overview_counts_overdue_cancellation_EFOS_and_global_alerts()
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 200);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync("""
            UPDATE bank_movement SET movement_date='2026-09-10' WHERE id=@Movement;
            WITH other AS (
              INSERT INTO cfdi(company_id,import_file_id,uuid,type,direction,issuer_rfc,receiver_rfc,issued_at,stamped_at,currency,subtotal,total,payment_method,cancellation_status,is_global)
              SELECT company_id,import_file_id,gen_random_uuid(),type,direction,issuer_rfc,receiver_rfc,issued_at,stamped_at,currency,subtotal,total,'PPD','in process',true FROM cfdi WHERE id=@Invoice RETURNING id
            ) INSERT INTO link(company_id,movement_id,cfdi_id,amount_applied,state,origin,criteria,explanation)
              SELECT @Company,@Movement,id,100,'reconciled','user','[]','Synthetic overdue' FROM other;
            INSERT INTO link(company_id,movement_id,cfdi_id,amount_applied,state,origin,criteria,explanation)
              VALUES(@Company,@Movement,@Invoice,100,'reconciled','user','[]','Synthetic overdue');
            INSERT INTO sat_status_cache(company_id,uuid,state,checked_at,expires_at,attempts,source,code_raw,efos_raw)
              VALUES(@Company,@Uuid,'current','2026-11-02','2026-11-03',1,'stub','S','100')
            """, seed);
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        var body = await client.GetFromJsonAsync<JsonElement>($"/companies/{seed.Company}/overview", TestContext.Current.CancellationToken);
        var alerts = body.GetProperty("alerts");
        Assert.Equal(2, alerts.GetProperty("complementOverdue").GetInt64());
        Assert.Equal(1, alerts.GetProperty("cancellationInProcess").GetInt64());
        Assert.Equal(1, alerts.GetProperty("efosWarning").GetInt64());
        Assert.Equal(1, alerts.GetProperty("global").GetInt64());
    }
}
