using System.Text;
using System.Xml.Linq;

namespace Reconciliation.Tests;

internal static class SyntheticCfdi
{
    public static readonly XNamespace Cfdi = "http://www.sat.gob.mx/cfd/4";
    public static readonly XNamespace Stamp = "http://www.sat.gob.mx/TimbreFiscalDigital";

    public static byte[] Invoice(Action<XElement>? change = null)
    {
        var path = Path.GetFullPath("../../../../../samples/synthetic-income-ppd-retention.xml", AppContext.BaseDirectory);
        var document = XDocument.Load(path);
        document.Descendants(Stamp + "TimbreFiscalDigital").Single().SetAttributeValue("UUID", Guid.NewGuid());
        change?.Invoke(document.Root!);
        return Encoding.UTF8.GetBytes(document.ToString());
    }

    public static byte[] Payment(Guid invoice, string remaining = "5800.00", string version = "2.0", int installment = 1, string previous = "11600.00", string paid = "5800.00") => Encoding.UTF8.GetBytes($$"""
        <cfdi:Comprobante xmlns:cfdi="http://www.sat.gob.mx/cfd/4" xmlns:p="http://www.sat.gob.mx/Pagos20" xmlns:t="http://www.sat.gob.mx/TimbreFiscalDigital"
          Version="4.0" TipoDeComprobante="P" Fecha="2026-10-10T12:00:00" Moneda="XXX" SubTotal="0" Total="0">
          <cfdi:Emisor Rfc="AAA010101AAA"/><cfdi:Receptor Rfc="BBB010101BBB"/>
          <cfdi:Complemento><p:Pagos Version="{{version}}"><p:Pago FechaPago="2026-10-10T11:00:00" FormaDePagoP="03" MonedaP="MXN" TipoCambioP="1.0000000000" Monto="{{paid}}" NumOperacion="ABC123">
          <p:DoctoRelacionado IdDocumento="{{invoice}}" NumParcialidad="{{installment}}" ImpSaldoAnt="{{previous}}" ImpPagado="{{paid}}" ImpSaldoInsoluto="{{remaining}}" MonedaDR="MXN"/>
          </p:Pago></p:Pagos><t:TimbreFiscalDigital UUID="{{Guid.NewGuid()}}" FechaTimbrado="2026-10-10T12:01:00"/></cfdi:Complemento>
        </cfdi:Comprobante>
        """);
}
