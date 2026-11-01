using System.Globalization;
using System.Xml.Linq;
using MoneyRules = Reconciliation.Core.Common.Money;

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
    public string? ReviewReason => Payments.SelectMany(payment => payment.Documents).Any(paidInvoice => paidInvoice.InconsistentBalance) ? "inconsistent balance" : null;
}

internal static class CfdiParser
{
    private static readonly XNamespace Cfdi = "http://www.sat.gob.mx/cfd/4";
    private static readonly XNamespace Stamp = "http://www.sat.gob.mx/TimbreFiscalDigital";
    private static readonly XNamespace Payments = "http://www.sat.gob.mx/Pagos20";
    private static readonly XNamespace LocalTaxes = "http://www.sat.gob.mx/implocal";

    public static ParsedCfdi Parse(XDocument cfdiXml)
    {
        var comprobante = cfdiXml.Root;
        if (comprobante?.Name != Cfdi + "Comprobante") throw new InvalidDataException("File is not a CFDI.");
        if (Required(comprobante, "Version") != CfdiStandards.SupportedVersion) throw new InvalidDataException("Unsupported CFDI version; expected 4.0.");
        var type = Required(comprobante, "TipoDeComprobante");
        if (type is not ("I" or "P" or "E")) throw new InvalidDataException("Unsupported CFDI type.");
        var issuerRfc = Required(One(comprobante, Cfdi + "Emisor"), "Rfc");
        var receiverRfc = Required(One(comprobante, Cfdi + "Receptor"), "Rfc");
        var complement = One(comprobante, Cfdi + "Complemento");
        var stamp = One(complement, Stamp + "TimbreFiscalDigital");
        var subtotal = Money(comprobante, "SubTotal");
        var discount = Money(comprobante, "Descuento", optional: true);
        if (discount > subtotal) throw new InvalidDataException("Discount exceeds subtotal.");
        var taxesNode = comprobante.Element(Cfdi + "Impuestos");
        var localTaxesNode = complement.Element(LocalTaxes + "ImpuestosLocales");
        var transferredTax = Money(taxesNode, "TotalImpuestosTrasladados", optional: true);
        var retainedTax = Money(taxesNode, "TotalImpuestosRetenidos", optional: true);
        var localTransferredTax = Money(localTaxesNode, "TotaldeTraslados", optional: true);
        var localRetainedTax = Money(localTaxesNode, "TotaldeRetenciones", optional: true);
        var total = Money(comprobante, "Total");
        if (total != subtotal - discount + transferredTax - retainedTax + localTransferredTax - localRetainedTax)
            throw new InvalidDataException("Inconsistent total from declared header amounts.");
        var relations = comprobante.Elements(Cfdi + "CfdiRelacionados").SelectMany(relationGroup =>
            relationGroup.Elements(Cfdi + "CfdiRelacionado").Select(relatedDocument => new CfdiRelation(Uuid(relatedDocument, "UUID"), Required(relationGroup, "TipoRelacion")))).Distinct().ToArray();
        var payments = new List<CfdiPayment>();
        if (type == "P")
        {
            var paymentGroups = complement.Elements().Where(xmlNode => xmlNode.Name.LocalName == "Pagos").ToArray();
            if (paymentGroups.Length != 1 || paymentGroups[0].Name != Payments + "Pagos" || Required(paymentGroups[0], "Version") != CfdiStandards.PaymentComplementVersion)
                throw new InvalidDataException("Unsupported payment complement version; expected 2.0.");
            foreach (var payment in paymentGroups[0].Elements(Payments + "Pago"))
            {
                var documents = payment.Elements(Payments + "DoctoRelacionado").Select(relatedDocument =>
                {
                    if (!int.TryParse(Required(relatedDocument, "NumParcialidad"), NumberStyles.None, CultureInfo.InvariantCulture, out var installment) || installment <= 0)
                        throw new InvalidDataException("Invalid installment number.");
                    var paidInvoice = new PaymentDocument(Uuid(relatedDocument, "IdDocumento"), installment, Required(relatedDocument, "MonedaDR"),
                        Money(relatedDocument, "ImpSaldoAnt"), Money(relatedDocument, "ImpPagado"), Money(relatedDocument, "ImpSaldoInsoluto"));
                    paidInvoice.InconsistentBalance = paidInvoice.RemainingBalance != paidInvoice.PreviousBalance - paidInvoice.Paid;
                    return paidInvoice;
                }).ToArray();
                if (documents.Length == 0) throw new InvalidDataException("Payment has no related documents.");
                decimal? exchangeRate = null;
                if (payment.Attribute("TipoCambioP") is not null)
                {
                    var declaredRate = Required(payment, "TipoCambioP");
                    if (!decimal.TryParse(declaredRate, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsedRate) || parsedRate <= 0 || parsedRate >= 10000000000m || decimal.Round(parsedRate, 10) != parsedRate)
                        throw new InvalidDataException("Invalid payment exchange rate.");
                    exchangeRate = parsedRate;
                }
                payments.Add(new CfdiPayment(Date(payment, "FechaPago"), Required(payment, "FormaDePagoP"), Required(payment, "MonedaP"),
                    exchangeRate, Money(payment, "Monto"), (string?)payment.Attribute("NumOperacion"), documents));
            }
            if (payments.Count == 0) throw new InvalidDataException("Payment complement has no payments.");
        }
        var paymentMethod = (string?)comprobante.Attribute("MetodoPago");
        if (paymentMethod is not (null or "PUE" or "PPD")) throw new InvalidDataException("Invalid payment method.");
        return new ParsedCfdi(Uuid(stamp, "UUID"), type, issuerRfc, receiverRfc, Date(comprobante, "Fecha"), Date(stamp, "FechaTimbrado"),
            Required(comprobante, "Moneda"), paymentMethod, subtotal, discount, transferredTax, retainedTax, localTransferredTax, localRetainedTax, total,
            receiverRfc == CfdiStandards.PublicGeneralRfc || comprobante.Element(Cfdi + "InformacionGlobal") is not null, relations, payments);
    }

    private static XElement One(XElement xmlNode, XName attributeName)
    {
        var children = xmlNode.Elements(attributeName).ToArray();
        return children.Length == 1 ? children[0] : throw new InvalidDataException($"Expected one {attributeName.LocalName} node.");
    }

    private static string Required(XElement xmlNode, string attributeName) =>
        !string.IsNullOrWhiteSpace((string?)xmlNode.Attribute(attributeName)) ? xmlNode.Attribute(attributeName)!.Value : throw new InvalidDataException($"Missing {attributeName}.");

    private static Guid Uuid(XElement xmlNode, string attributeName) =>
        Guid.TryParse(Required(xmlNode, attributeName), out var documentUuid) && documentUuid != Guid.Empty ? documentUuid : throw new InvalidDataException($"Invalid {attributeName} UUID.");

    private static DateTime Date(XElement xmlNode, string attributeName) =>
        DateTime.TryParseExact(Required(xmlNode, attributeName), ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFF"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var amount) ? amount : throw new InvalidDataException($"Invalid local date {attributeName}.");

    private static decimal Money(XElement? xmlNode, string attributeName, bool optional = false)
    {
        var declaredAmount = (string?)xmlNode?.Attribute(attributeName);
        if (declaredAmount is null && optional) return 0;
        if (declaredAmount is null || !MoneyRules.HasAtMostTwoDecimals(declaredAmount) || !decimal.TryParse(declaredAmount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) ||
            amount < 0 || amount > MoneyRules.MaxAmount || !MoneyRules.HasAtMostTwoDecimals(amount))
            throw new InvalidDataException($"Invalid {attributeName}: monetary values require at most two decimals and must fit numeric(18,2).");
        return amount;
    }
}
