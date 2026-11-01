using System.Text;
using System.Xml.Linq;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Reconciliation.Core.Ingestion;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class CfdiImportTests(ApiFixture fixture)
{
    [Fact]
    public async Task Income_invoice_accepted_with_sample_retention_and_declared_header_amounts()
    {
        var user = await fixture.SeedUserAsync();
        var company = await fixture.SeedCompanyAsync(user.Id);
        var path = Path.GetFullPath("../../../../../samples/synthetic-income-ppd-retention.xml", AppContext.BaseDirectory);
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var report = await ImportAsync(company, user.Id, bytes);
        Assert.Equal("imported", Assert.Single(report.Files).Status);
        await using var owner = new NpgsqlConnection(fixture.Database.OwnerConnectionString);
        var row = await owner.QuerySingleAsync<(decimal Subtotal, decimal Discount, decimal Transferred, decimal Retained, decimal Total, string Direction)>(new CommandDefinition(
            "SELECT subtotal, discount, transferred_tax, retained_tax, total, direction FROM cfdi WHERE company_id = @company", new { company }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal((1690m, 0m, 270.40m, 21.13m, 1939.27m, "issued"), row);
    }

    [Fact]
    public async Task Invoice_with_discount_and_local_taxes_uses_only_declared_header_values()
    {
        var user = await fixture.SeedUserAsync();
        var company = await fixture.SeedCompanyAsync(user.Id);
        var bytes = SyntheticCfdi.Invoice(root =>
        {
            root.SetAttributeValue("SubTotal", "1000.00"); root.SetAttributeValue("Descuento", "100.00"); root.SetAttributeValue("Total", "1044.00");
            root.Element(SyntheticCfdi.Cfdi + "Impuestos")!.SetAttributeValue("TotalImpuestosTrasladados", "144.00");
            root.Element(SyntheticCfdi.Cfdi + "Impuestos")!.SetAttributeValue("TotalImpuestosRetenidos", "0");
            // Concept detail deliberately differs: the importer must not recompute header taxes from rates.
        });
        Assert.Equal("imported", Assert.Single((await ImportAsync(company, user.Id, bytes)).Files).Status);
        var local = SyntheticCfdi.Invoice(root =>
        {
            XNamespace ns = "http://www.sat.gob.mx/implocal";
            root.Element(SyntheticCfdi.Cfdi + "Complemento")!.Add(new XElement(ns + "ImpuestosLocales", new XAttribute("TotaldeTraslados", "10.00"), new XAttribute("TotaldeRetenciones", "5.00")));
            root.SetAttributeValue("Total", "1944.27");
        });
        Assert.Equal("imported", Assert.Single((await ImportAsync(company, user.Id, local)).Files).Status);
        await using var owner = new NpgsqlConnection(fixture.Database.OwnerConnectionString);
        Assert.Equal(5m, await owner.ExecuteScalarAsync<decimal>(new CommandDefinition("SELECT local_transferred_tax - local_retained_tax FROM cfdi WHERE company_id = @company AND local_transferred_tax > 0", new { company }, cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("Total", "1900", "total")]
    [InlineData("Descuento", "2000", "discount")]
    [InlineData("Version", "3.3", "version")]
    [InlineData("TipoDeComprobante", "T", "type")]
    [InlineData("Total", "1939.271", "decimals")]
    public async Task Invalid_header_is_rejected_without_partial_domain_rows(string attribute, string value, string reason)
    {
        var user = await fixture.SeedUserAsync();
        var company = await fixture.SeedCompanyAsync(user.Id);
        var result = Assert.Single((await ImportAsync(company, user.Id, SyntheticCfdi.Invoice(root => root.SetAttributeValue(attribute, value)))).Files);
        Assert.Equal("rejected", result.Status);
        Assert.Contains(reason, result.Reason!, StringComparison.OrdinalIgnoreCase);
        await using var owner = new NpgsqlConnection(fixture.Database.OwnerConnectionString);
        Assert.Equal(0, await owner.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM cfdi WHERE company_id = @company", new { company }, cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Equal(1, await owner.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM import_file WHERE company_id = @company AND original IS NULL AND octet_length(sha256) = 32", new { company }, cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Unrelated_CFDI_is_rejected_and_receiver_company_is_received()
    {
        var user = await fixture.SeedUserAsync();
        var company = await fixture.SeedCompanyAsync(user.Id);
        var unrelated = SyntheticCfdi.Invoice(root => root.Element(SyntheticCfdi.Cfdi + "Emisor")!.SetAttributeValue("Rfc", "CCC010101CCC"));
        var refused = Assert.Single((await ImportAsync(company, user.Id, unrelated)).Files);
        Assert.Equal("rejected", refused.Status);
        Assert.Equal("does not involve this company", refused.Reason);
        var received = SyntheticCfdi.Invoice(root =>
        {
            root.Element(SyntheticCfdi.Cfdi + "Emisor")!.SetAttributeValue("Rfc", "BBB010101BBB");
            root.Element(SyntheticCfdi.Cfdi + "Receptor")!.SetAttributeValue("Rfc", "AAA010101AAA");
        });
        Assert.Equal("imported", Assert.Single((await ImportAsync(company, user.Id, received)).Files).Status);
        await using var owner = new NpgsqlConnection(fixture.Database.OwnerConnectionString);
        Assert.Equal("received", await owner.QuerySingleAsync<string>(new CommandDefinition("SELECT direction FROM cfdi WHERE company_id = @company", new { company }, cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("5800.00", false)]
    [InlineData("4000.00", true)]
    public async Task Payment_complement_accepted_and_inconsistent_balance_is_flagged(string remaining, bool flagged)
    {
        var user = await fixture.SeedUserAsync();
        var company = await fixture.SeedCompanyAsync(user.Id);
        var invoice = Guid.NewGuid();
        var result = Assert.Single((await ImportAsync(company, user.Id, SyntheticCfdi.Payment(invoice, remaining))).Files);
        Assert.Equal("imported", result.Status);
        await using var owner = new NpgsqlConnection(fixture.Database.OwnerConnectionString);
        var row = await owner.QuerySingleAsync<(Guid Invoice, int Installment, decimal Previous, decimal Paid, decimal Remaining, bool Flagged)>(new CommandDefinition(
            "SELECT invoice_uuid, installment, previous_balance, paid, remaining_balance, inconsistent_balance FROM cfdi_payment_doc WHERE company_id = @company", new { company }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal((invoice, 1, 11600m, 5800m, decimal.Parse(remaining, System.Globalization.CultureInfo.InvariantCulture), flagged), row);
        var payment = await owner.QuerySingleAsync<(decimal Amount, decimal Rate, string Operation)>(new CommandDefinition("SELECT amount, exchange_rate, operation_number FROM cfdi_payment WHERE company_id = @company", new { company }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal((5800m, 1m, "ABC123"), payment);
        if (flagged) Assert.Equal("inconsistent balance", result.Reason);
    }

    [Fact]
    public async Task Balance_continuity_is_checked_within_upload_without_requiring_prior_invoice()
    {
        var user = await fixture.SeedUserAsync();
        var company = await fixture.SeedCompanyAsync(user.Id);
        var invoice = Guid.NewGuid();
        var result = await ImportAsync(company, user.Id, SyntheticCfdi.Payment(invoice),
            SyntheticCfdi.Payment(invoice, remaining: "0", installment: 2, previous: "5000", paid: "5000"));
        Assert.All(result.Files, file => Assert.Equal("imported", file.Status));
        Assert.Contains(result.Files, file => file.Reason == "inconsistent balance");
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1.1")]
    public async Task Old_complement_version_is_rejected(string version)
    {
        var user = await fixture.SeedUserAsync();
        var company = await fixture.SeedCompanyAsync(user.Id);
        var result = Assert.Single((await ImportAsync(company, user.Id, SyntheticCfdi.Payment(Guid.NewGuid(), version: version))).Files);
        Assert.Equal("rejected", result.Status);
        Assert.Contains("version", result.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("01", "E")]
    [InlineData("03", "E")]
    [InlineData("04", "I")]
    [InlineData("07", "I")]
    [InlineData("99", "I")]
    public async Task Credit_note_and_substitution_relations_keep_declared_code(string code, string type)
    {
        var user = await fixture.SeedUserAsync();
        var company = await fixture.SeedCompanyAsync(user.Id);
        var related = Guid.NewGuid();
        var bytes = SyntheticCfdi.Invoice(root =>
        {
            root.SetAttributeValue("TipoDeComprobante", type);
            root.Add(new XElement(SyntheticCfdi.Cfdi + "CfdiRelacionados", new XAttribute("TipoRelacion", code), new XElement(SyntheticCfdi.Cfdi + "CfdiRelacionado", new XAttribute("UUID", related))));
        });
        Assert.Equal("imported", Assert.Single((await ImportAsync(company, user.Id, bytes)).Files).Status);
        await using var owner = new NpgsqlConnection(fixture.Database.OwnerConnectionString);
        Assert.Equal((related, code), await owner.QuerySingleAsync<(Guid Related, string Code)>(new CommandDefinition("SELECT related_uuid, relation_code FROM cfdi_relation WHERE company_id = @company", new { company }, cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("XAXX010101000", true)]
    [InlineData("XEXX010101000", false)]
    public async Task Global_generic_and_foreign_receiver_and_midnight_dates_are_preserved(string receiver, bool global)
    {
        var user = await fixture.SeedUserAsync();
        var company = await fixture.SeedCompanyAsync(user.Id);
        var bytes = SyntheticCfdi.Invoice(root =>
        {
            root.Element(SyntheticCfdi.Cfdi + "Receptor")!.SetAttributeValue("Rfc", receiver);
            root.SetAttributeValue("Fecha", "2026-09-30T23:50:00");
            root.Descendants(SyntheticCfdi.Stamp + "TimbreFiscalDigital").Single().SetAttributeValue("FechaTimbrado", "2026-10-01T00:05:00");
        });
        Assert.Equal("imported", Assert.Single((await ImportAsync(company, user.Id, bytes)).Files).Status);
        await using var owner = new NpgsqlConnection(fixture.Database.OwnerConnectionString);
        var row = await owner.QuerySingleAsync<(DateTime Issue, DateTime Stamp, bool Global)>(new CommandDefinition("SELECT issued_at, stamped_at, is_global FROM cfdi WHERE company_id = @company", new { company }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(new DateTime(2026, 9, 30, 23, 50, 0, DateTimeKind.Unspecified), row.Issue);
        Assert.Equal(new DateTime(2026, 10, 1, 0, 5, 0, DateTimeKind.Unspecified), row.Stamp);
        Assert.Equal(global, row.Global);
    }

    [Theory]
    [InlineData("<not-cfdi/>")]
    [InlineData("<!DOCTYPE x [<!ENTITY a SYSTEM 'file:///synthetic'>]><x>&a;</x>")]
    public async Task Hostile_or_non_CFDI_file_records_rejection_without_bytes(string xml)
    {
        var user = await fixture.SeedUserAsync();
        var company = await fixture.SeedCompanyAsync(user.Id);
        var result = Assert.Single((await ImportAsync(company, user.Id, Encoding.UTF8.GetBytes(xml))).Files);
        Assert.Equal("rejected", result.Status);
        await using var owner = new NpgsqlConnection(fixture.Database.OwnerConnectionString);
        Assert.Equal(1, await owner.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM import_file WHERE company_id = @company AND status = 'rejected' AND original IS NULL", new { company }, cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Fact]
    public void Retention_shorter_than_five_years_fails_at_startup()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            using var app = fixture.App.WithWebHostBuilder(builder => builder.UseSetting("Ingestion:RetentionYears", "4"));
            using var client = app.CreateClient();
        });
        Assert.Contains("RetentionYears", error.Message, StringComparison.Ordinal);
    }

    private Task<ImportReport> ImportAsync(Guid company, Guid actor, params byte[][] files) =>
        fixture.App.Services.GetRequiredService<CfdiImporter>().ImportAsync(company, actor,
            files.Select((bytes, index) => UploadedFile.FromBytes($"synthetic-{index}.xml", bytes)).ToArray(), TestContext.Current.CancellationToken);
}
