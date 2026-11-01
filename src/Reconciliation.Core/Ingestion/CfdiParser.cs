using System.Globalization;
using System.Xml.Linq;

namespace Reconciliation.Core.Ingestion;

internal sealed record CfdiRelation(Guid RelatedUuid, string RelationCode);
internal sealed record PaymentDocument(Guid InvoiceUuid, int Installment, string Currency, decimal PreviousBalance, decimal Paid, decimal RemainingBalance)
{
    public bool InconsistentBalance { get; set; }
}
internal sealed record CfdiPayment(DateTime PaidAt, string PaymentForm, string Currency, decimal? ExchangeRate,
    decimal Amount, string? OperationNumber, IReadOnlyList<PaymentDocument> Documents);
internal sealed record ParsedCfdi(Guid Uuid, string Type, string IssuerRfc, string ReceiverRfc, DateTime IssuedAt,
    DateTime StampedAt, string Currency, string? PaymentMethod, decimal Subtotal, decimal Discount, decimal TransferredTax,
    decimal RetainedTax, decimal LocalTransferredTax, decimal LocalRetainedTax, decimal Total, bool IsGlobal,
    IReadOnlyList<CfdiRelation> Relations, IReadOnlyList<CfdiPayment> Payments)
{
    public string? ReviewReason => Payments.SelectMany(payment => payment.Documents).Any(doc => doc.InconsistentBalance) ? "inconsistent balance" : null;
}

internal static class CfdiParser
{
    private static readonly XNamespace Cfdi = "http://www.sat.gob.mx/cfd/4";
    private static readonly XNamespace Stamp = "http://www.sat.gob.mx/TimbreFiscalDigital";
    private static readonly XNamespace Payments = "http://www.sat.gob.mx/Pagos20";
    private static readonly XNamespace LocalTaxes = "http://www.sat.gob.mx/implocal";

    public static ParsedCfdi Parse(XDocument xml)
    {
        var root = xml.Root;
        if (root?.Name != Cfdi + "Comprobante") throw new InvalidDataException("File is not a CFDI.");
        if (Required(root, "Version") != "4.0") throw new InvalidDataException("Unsupported CFDI version; expected 4.0.");
        var type = Required(root, "TipoDeComprobante");
        if (type is not ("I" or "P" or "E")) throw new InvalidDataException("Unsupported CFDI type.");
        var issuer = Required(One(root, Cfdi + "Emisor"), "Rfc");
        var receiver = Required(One(root, Cfdi + "Receptor"), "Rfc");
        var complement = One(root, Cfdi + "Complemento");
        var stamp = One(complement, Stamp + "TimbreFiscalDigital");
        var subtotal = Money(root, "SubTotal");
        var discount = Money(root, "Descuento", optional: true);
        if (discount > subtotal) throw new InvalidDataException("Discount exceeds subtotal.");
        var taxes = root.Element(Cfdi + "Impuestos");
        var local = complement.Element(LocalTaxes + "ImpuestosLocales");
        var transferred = Money(taxes, "TotalImpuestosTrasladados", optional: true);
        var retained = Money(taxes, "TotalImpuestosRetenidos", optional: true);
        var localTransferred = Money(local, "TotaldeTraslados", optional: true);
        var localRetained = Money(local, "TotaldeRetenciones", optional: true);
        var total = Money(root, "Total");
        if (total != subtotal - discount + transferred - retained + localTransferred - localRetained)
            throw new InvalidDataException("Inconsistent total from declared header amounts.");
        var relations = root.Elements(Cfdi + "CfdiRelacionados").SelectMany(group =>
            group.Elements(Cfdi + "CfdiRelacionado").Select(item => new CfdiRelation(Uuid(item, "UUID"), Required(group, "TipoRelacion")))).Distinct().ToArray();
        var payments = new List<CfdiPayment>();
        if (type == "P")
        {
            var paymentGroups = complement.Elements().Where(element => element.Name.LocalName == "Pagos").ToArray();
            if (paymentGroups.Length != 1 || paymentGroups[0].Name != Payments + "Pagos" || Required(paymentGroups[0], "Version") != "2.0")
                throw new InvalidDataException("Unsupported payment complement version; expected 2.0.");
            foreach (var payment in paymentGroups[0].Elements(Payments + "Pago"))
            {
                var documents = payment.Elements(Payments + "DoctoRelacionado").Select(item =>
                {
                    if (!int.TryParse(Required(item, "NumParcialidad"), NumberStyles.None, CultureInfo.InvariantCulture, out var installment) || installment <= 0)
                        throw new InvalidDataException("Invalid installment number.");
                    var doc = new PaymentDocument(Uuid(item, "IdDocumento"), installment, Required(item, "MonedaDR"),
                        Money(item, "ImpSaldoAnt"), Money(item, "ImpPagado"), Money(item, "ImpSaldoInsoluto"));
                    doc.InconsistentBalance = doc.RemainingBalance != doc.PreviousBalance - doc.Paid;
                    return doc;
                }).ToArray();
                if (documents.Length == 0) throw new InvalidDataException("Payment has no related documents.");
                decimal? rate = null;
                if (payment.Attribute("TipoCambioP") is not null)
                {
                    var declared = Required(payment, "TipoCambioP");
                    if (!decimal.TryParse(declared, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0 || parsed >= 10000000000m || decimal.Round(parsed, 10) != parsed)
                        throw new InvalidDataException("Invalid payment exchange rate.");
                    rate = parsed;
                }
                payments.Add(new CfdiPayment(Date(payment, "FechaPago"), Required(payment, "FormaDePagoP"), Required(payment, "MonedaP"),
                    rate, Money(payment, "Monto"), (string?)payment.Attribute("NumOperacion"), documents));
            }
            if (payments.Count == 0) throw new InvalidDataException("Payment complement has no payments.");
        }
        var method = (string?)root.Attribute("MetodoPago");
        if (method is not (null or "PUE" or "PPD")) throw new InvalidDataException("Invalid payment method.");
        return new ParsedCfdi(Uuid(stamp, "UUID"), type, issuer, receiver, Date(root, "Fecha"), Date(stamp, "FechaTimbrado"),
            Required(root, "Moneda"), method, subtotal, discount, transferred, retained, localTransferred, localRetained, total,
            receiver == "XAXX010101000" || root.Element(Cfdi + "InformacionGlobal") is not null, relations, payments);
    }

    private static XElement One(XElement element, XName name)
    {
        var children = element.Elements(name).ToArray();
        return children.Length == 1 ? children[0] : throw new InvalidDataException($"Expected one {name.LocalName} node.");
    }

    private static string Required(XElement element, string name) =>
        !string.IsNullOrWhiteSpace((string?)element.Attribute(name)) ? element.Attribute(name)!.Value : throw new InvalidDataException($"Missing {name}.");

    private static Guid Uuid(XElement element, string name) =>
        Guid.TryParse(Required(element, name), out var id) && id != Guid.Empty ? id : throw new InvalidDataException($"Invalid {name} UUID.");

    private static DateTime Date(XElement element, string name) =>
        DateTime.TryParseExact(Required(element, name), ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value : throw new InvalidDataException($"Invalid local date {name}.");

    private static decimal Money(XElement? element, string name, bool optional = false)
    {
        var text = (string?)element?.Attribute(name);
        if (text is null && optional) return 0;
        if (text is null || !decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ||
            value < 0 || value > 9999999999999999.99m || decimal.Round(value, 2) != value)
            throw new InvalidDataException($"Invalid {name}: monetary values require at most two decimals and must fit numeric(18,2).");
        return value;
    }
}
