using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Reconciliation.Core.Ingestion;
using Reconciliation.Core.Matching;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class MatchingRetentionTests(ApiFixture fixture)
{
    [Theory]
    [InlineData(0, 10)]
    [InlineData(5, 10)]
    public async Task Gross_payment_includes_federal_and_local_retention_and_records_both_components(decimal federal, decimal local)
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        var total = 100 - federal - local;
        var bytes = SyntheticCfdi.Invoice(root =>
        {
            root.SetAttributeValue("SubTotal", "100.00"); root.SetAttributeValue("Descuento", "0.00");
            root.SetAttributeValue("Total", total.ToString("0.00", CultureInfo.InvariantCulture)); root.SetAttributeValue("Fecha", "2026-10-10T12:00:00");
            var taxes = root.Element(SyntheticCfdi.Cfdi + "Impuestos")!;
            taxes.SetAttributeValue("TotalImpuestosTrasladados", "0.00"); taxes.SetAttributeValue("TotalImpuestosRetenidos", federal.ToString("0.00", CultureInfo.InvariantCulture));
            XNamespace localTaxes = "http://www.sat.gob.mx/implocal";
            root.Element(SyntheticCfdi.Cfdi + "Complemento")!.Add(new XElement(localTaxes + "ImpuestosLocales", new XAttribute("version", "1.0"),
                new XAttribute("TotaldeTraslados", "0.00"), new XAttribute("TotaldeRetenciones", local.ToString("0.00", CultureInfo.InvariantCulture))));
        });
        var report = await fixture.App.Services.GetRequiredService<CfdiImporter>().ImportAsync(company, user.Id,
            [UploadedFile.FromBytes("synthetic-local-retention.xml", bytes)], TestContext.Current.CancellationToken);
        ImportAssertions.AssertSingleStatus(report, "imported");
        await fixture.App.Services.GetRequiredService<BankStatementImporter>().ImportAsync(company, user.Id, "reference", "Synthetic-Retention",
            [UploadedFile.FromBytes("synthetic.csv", Encoding.UTF8.GetBytes("fecha,concepto,abono,cargo,saldo\n2026-10-10,synthetic transfer,100.00,0,100.00\n"))], TestContext.Current.CancellationToken);
        using var client = await fixture.LoginAsync(user.Email, company);
        await MatchingRunScenario.RunAsync(fixture, client, company);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        var links = (await owner.QueryAsync<(string State, decimal Amount, string Criteria, string Explanation)>(new CommandDefinition(
            "SELECT state,amount_applied,criteria::text,explanation FROM link WHERE company_id=@company", new { company }, cancellationToken: TestContext.Current.CancellationToken))).ToArray();
        var link = Assert.Single(links);
        Assert.Equal("suggested", link.State);
        Assert.Equal(total, link.Amount);
        var criterion = Assert.Single(JsonSerializer.Deserialize<JsonElement>(link.Criteria).EnumerateArray(), criterion => criterion.GetProperty("code").GetString() == "gross-payment");
        Assert.Equal(35, criterion.GetProperty("points").GetInt32());
        var components = FormattableString.Invariant($"federal {federal:0.00}, local {local:0.00}");
        Assert.Contains(components, criterion.GetProperty("detail").GetString()!, StringComparison.Ordinal);
        Assert.Contains(components, link.Explanation, StringComparison.Ordinal);
        Assert.Contains("amount equals total before retentions", link.Explanation, StringComparison.Ordinal);
        var audit = await owner.ExecuteScalarAsync<string>(new CommandDefinition("SELECT detail::text FROM audit_entry WHERE company_id=@company AND action='link.created'", new { company }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains(components, audit!, StringComparison.Ordinal);
    }

    [Fact]
    public void Local_only_retention_is_a_gross_target_when_the_single_input_is_the_declared_sum()
    {
        const decimal federal = 0, local = 10;
        var invoice = SyntheticMatching.Invoice(90) with { Retained = federal + local };
        var result = MatchingEngine.Run([SyntheticMatching.FirstMovement(100)], [invoice], [], [], new(3, .01m));
        var link = Assert.Single(result.Changes);
        Assert.Equal("suggested", link.State);
        Assert.Contains(link.Criteria, criterion => criterion.Code == "gross-payment" && criterion.Points == 35);
        Assert.Contains("amount equals total before retentions", link.Explanation, StringComparison.Ordinal);
    }
}
