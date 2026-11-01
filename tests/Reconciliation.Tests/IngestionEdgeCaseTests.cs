using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Reconciliation.Core.Ingestion;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class IngestionEdgeCaseTests(ApiFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Monetary_fields_reject_excess_declared_decimal_places_even_when_the_extra_digits_are_zero(bool payment)
    {
        var user = await fixture.SeedUserAsync();
        var company = await fixture.SeedCompanyAsync(user.Id);
        var bytes = payment ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(SyntheticCfdi.Payment(Guid.NewGuid())).Replace("Monto=\"5800.00\"", "Monto=\"5800.000\"", StringComparison.Ordinal)) :
            SyntheticCfdi.Invoice(root => root.SetAttributeValue("Total", "1939.2700"));
        var result = await fixture.App.Services.GetRequiredService<CfdiImporter>().ImportAsync(company, user.Id, [UploadedFile.FromBytes("synthetic.xml", bytes)], TestContext.Current.CancellationToken);
        Assert.Equal("rejected", Assert.Single(result.Files).Status);
    }

    [Fact]
    public async Task Rejected_unrelated_complement_cannot_flag_a_valid_payment_in_the_same_upload()
    {
        var user = await fixture.SeedUserAsync();
        var company = await fixture.SeedCompanyAsync(user.Id);
        var invoice = Guid.NewGuid();
        var unrelated = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(SyntheticCfdi.Payment(invoice, "6600", paid: "5000"))
            .Replace("AAA010101AAA", "CCC010101CCC", StringComparison.Ordinal));
        var valid = SyntheticCfdi.Payment(invoice, "0", installment: 2, previous: "5800", paid: "5800");
        var report = await fixture.App.Services.GetRequiredService<CfdiImporter>().ImportAsync(company, user.Id,
            [UploadedFile.FromBytes("unrelated.xml", unrelated), UploadedFile.FromBytes("valid.xml", valid)], TestContext.Current.CancellationToken);
        Assert.Equal("rejected", report.Files[0].Status);
        Assert.Equal("imported", report.Files[1].Status);
        Assert.Null(report.Files[1].Reason);
    }

    [Fact]
    public async Task Dates_finer_than_PostgreSQL_microseconds_are_rejected_instead_of_silently_changed()
    {
        var user = await fixture.SeedUserAsync();
        var company = await fixture.SeedCompanyAsync(user.Id);
        var bytes = SyntheticCfdi.Invoice(root => root.SetAttributeValue("Fecha", "2026-09-30T23:59:59.9999999"));
        var report = await fixture.App.Services.GetRequiredService<CfdiImporter>().ImportAsync(company, user.Id,
            [UploadedFile.FromBytes("synthetic.xml", bytes)], TestContext.Current.CancellationToken);
        Assert.Equal("rejected", Assert.Single(report.Files).Status);
    }

    [Fact]
    public async Task Concept_amounts_with_six_decimals_are_not_recomputed_or_rejected()
    {
        var user = await fixture.SeedUserAsync();
        var company = await fixture.SeedCompanyAsync(user.Id);
        var bytes = SyntheticCfdi.Invoice(root => root.Descendants(SyntheticCfdi.Cfdi + "Concepto").Single().SetAttributeValue("ValorUnitario", "1690.000001"));
        var report = await fixture.App.Services.GetRequiredService<CfdiImporter>().ImportAsync(company, user.Id, [UploadedFile.FromBytes("synthetic.xml", bytes)], TestContext.Current.CancellationToken);
        Assert.Equal("imported", Assert.Single(report.Files).Status);
    }
}
