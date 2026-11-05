using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Reconciliation.Core.Ingestion;

namespace Reconciliation.Tests;

internal static class MatchingFiscalScenario
{
    internal sealed record Seed(Guid Company, Guid Actor, string Email, Guid Invoice, Guid Uuid, Guid Movement);

    internal static byte[] Invoice(decimal total, Action<XElement>? change = null) => SyntheticCfdi.Invoice(root =>
    {
        root.SetAttributeValue("SubTotal", total.ToString("0.00", CultureInfo.InvariantCulture));
        root.SetAttributeValue("Descuento", "0.00"); root.SetAttributeValue("Total", total.ToString("0.00", CultureInfo.InvariantCulture));
        root.SetAttributeValue("Fecha", "2026-10-10T12:00:00");
        var taxes = root.Element(SyntheticCfdi.Cfdi + "Impuestos")!;
        taxes.SetAttributeValue("TotalImpuestosTrasladados", "0.00"); taxes.SetAttributeValue("TotalImpuestosRetenidos", "0.00");
        change?.Invoke(root);
    });

    internal static byte[] CreditNote(Guid invoiceUuid, decimal total, string code = "01", string currency = "MXN", bool issued = true) => Invoice(total, root =>
    {
        root.SetAttributeValue("TipoDeComprobante", "E"); root.SetAttributeValue("Moneda", currency);
        root.Add(new XElement(SyntheticCfdi.Cfdi + "CfdiRelacionados", new XAttribute("TipoRelacion", code),
            new XElement(SyntheticCfdi.Cfdi + "CfdiRelacionado", new XAttribute("UUID", invoiceUuid))));
        if (!issued)
        {
            root.Element(SyntheticCfdi.Cfdi + "Emisor")!.SetAttributeValue("Rfc", SyntheticCfdi.CounterpartyRfc);
            root.Element(SyntheticCfdi.Cfdi + "Receptor")!.SetAttributeValue("Rfc", SyntheticCfdi.CompanyRfc);
        }
    });

    internal static async Task<Seed> SeedAsync(ApiFixture fixture, decimal total = 11600m, decimal credit = 10440m, Action<XElement>? change = null)
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        var report = await fixture.App.Services.GetRequiredService<CfdiImporter>().ImportAsync(company, user.Id,
            [UploadedFile.FromBytes("synthetic-invoice.xml", Invoice(total, change))], TestContext.Current.CancellationToken);
        ImportAssertions.AssertSingleStatus(report, "imported");
        var csv = FormattableString.Invariant($"fecha,concepto,abono,cargo,saldo\n2026-10-10,synthetic transfer,{credit:0.00},0,{credit:0.00}\n");
        await fixture.App.Services.GetRequiredService<BankStatementImporter>().ImportAsync(company, user.Id, "reference", "Synthetic-Fiscal",
            [UploadedFile.FromBytes("synthetic.csv", Encoding.UTF8.GetBytes(csv))], TestContext.Current.CancellationToken);
        var invoice = report.Files[0].CfdiId!.Value;
        return new(company, user.Id, user.Email, invoice,
            await fixture.Database.ScalarAsync<Guid>("SELECT uuid FROM cfdi WHERE id=@invoice", new { invoice }),
            await fixture.Database.ScalarAsync<Guid>("SELECT id FROM bank_movement WHERE company_id=@company", new { company }));
    }

    internal static async Task<Guid> ImportNoteAsync(ApiFixture fixture, Seed seed, decimal total, string code = "01", string currency = "MXN", bool issued = true)
    {
        var report = await fixture.App.Services.GetRequiredService<CfdiImporter>().ImportAsync(seed.Company, seed.Actor,
            [UploadedFile.FromBytes("synthetic-credit.xml", CreditNote(seed.Uuid, total, code, currency, issued))], TestContext.Current.CancellationToken);
        ImportAssertions.AssertSingleStatus(report, "imported");
        return report.Files[0].CfdiId!.Value;
    }
}
