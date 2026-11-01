using System.Text;
using System.Xml.Linq;

namespace Reconciliation.Tests;

internal static class SyntheticCfdi
{
    public const string CompanyRfc = "AAA010101AAA";
    public const string CounterpartyRfc = "BBB010101BBB";
    public const string CfdiNamespace = "http://www.sat.gob.mx/cfd/4";
    public const string StampNamespace = "http://www.sat.gob.mx/TimbreFiscalDigital";
    public const string PaymentsNamespace = "http://www.sat.gob.mx/Pagos20";
    public static readonly XNamespace Cfdi = CfdiNamespace;
    public static readonly XNamespace Stamp = StampNamespace;

    public static byte[] Invoice(Action<XElement>? change = null)
    {
        var path = RepoPaths.Sample("synthetic-income-ppd-retention.xml");
        var document = XDocument.Load(path);
        document.Descendants(Stamp + "TimbreFiscalDigital").Single().SetAttributeValue("UUID", Guid.NewGuid());
        change?.Invoke(document.Root!);
        return Encoding.UTF8.GetBytes(document.ToString());
    }

    public static byte[] Payment(Guid invoice, string remaining = "5800.00", string version = "2.0", int installment = 1, string previous = "11600.00", string paid = "5800.00") => Encoding.UTF8.GetBytes($$"""
        <cfdi:Comprobante xmlns:cfdi="{{CfdiNamespace}}" xmlns:p="{{PaymentsNamespace}}" xmlns:t="{{StampNamespace}}"
          Version="4.0" TipoDeComprobante="P" Fecha="2026-10-10T12:00:00" Moneda="XXX" SubTotal="0" Total="0">
          <cfdi:Emisor Rfc="{{CompanyRfc}}"/><cfdi:Receptor Rfc="{{CounterpartyRfc}}"/>
          <cfdi:Complemento><p:Pagos Version="{{version}}"><p:Pago FechaPago="2026-10-10T11:00:00" FormaDePagoP="03" MonedaP="MXN" TipoCambioP="1.0000000000" Monto="{{paid}}" NumOperacion="ABC123">
          <p:DoctoRelacionado IdDocumento="{{invoice}}" NumParcialidad="{{installment}}" ImpSaldoAnt="{{previous}}" ImpPagado="{{paid}}" ImpSaldoInsoluto="{{remaining}}" MonedaDR="MXN"/>
          </p:Pago></p:Pagos><t:TimbreFiscalDigital UUID="{{Guid.NewGuid()}}" FechaTimbrado="2026-10-10T12:01:00"/></cfdi:Complemento>
        </cfdi:Comprobante>
        """);
}
