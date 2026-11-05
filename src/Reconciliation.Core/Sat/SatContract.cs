using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Reconciliation.Core.Ingestion;

namespace Reconciliation.Core.Sat;

public static partial class SatContract
{
    public const string PublicEndpoint = "https://consultaqr.facturaelectronica.sat.gob.mx/ConsultaCFDIService.svc";
    public const string SoapAction = "http://tempuri.org/IConsultaCFDIService/Consulta";
    public static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    public static readonly XNamespace Service = "http://tempuri.org/";
    public static readonly XNamespace Acuse = "http://schemas.datacontract.org/2004/07/Sat.Cfdi.Negocio.ConsultaCfdi.Servicio";
    public const int MaxResponseBytes = 65536;
    private static readonly SafeCfdiXmlReader Reader = new(new IngestionOptions { MaxFileBytes = MaxResponseBytes, MaxXmlDepth = 16, MaxXmlNodes = 1024 });
    private static readonly Dictionary<string, SatCode> Codes = new(StringComparer.Ordinal)
    {
        ["S - Comprobante obtenido satisfactoriamente"] = SatCode.Success,
        ["N - 601: La expresión impresa proporcionada no es válida"] = SatCode.InvalidExpression,
        ["N - 602: Comprobante no encontrado"] = SatCode.NotFound
    };
    private static readonly Dictionary<string, SatState> States = new(StringComparer.Ordinal)
    { ["Vigente"] = SatState.Current, ["Cancelado"] = SatState.Cancelled, ["No Encontrado"] = SatState.NotFound };
    private static readonly Dictionary<string, SatCancellability> Cancellabilities = new(StringComparer.Ordinal)
    { ["Cancelable sin aceptación"] = SatCancellability.WithoutAcceptance, ["Cancelable con aceptación"] = SatCancellability.WithAcceptance, ["No cancelable"] = SatCancellability.NotCancellable };
    private static readonly Dictionary<string, SatCancellationStatus> Cancellations = new(StringComparer.Ordinal)
    {
        [""] = SatCancellationStatus.None,
        ["En proceso"] = SatCancellationStatus.InProcess,
        ["Plazo vencido"] = SatCancellationStatus.DeadlineExpired,
        ["Cancelado sin aceptación"] = SatCancellationStatus.CancelledWithoutAcceptance,
        ["Cancelado con aceptación"] = SatCancellationStatus.CancelledWithAcceptance,
        ["Solicitud rechazada"] = SatCancellationStatus.Rejected
    };

    public static string PrintedExpression(Guid uuid, string issuerRfc, string receiverRfc, decimal total)
    {
        if (uuid == Guid.Empty || issuerRfc is null || receiverRfc is null || !RfcFormat().IsMatch(issuerRfc) || !RfcFormat().IsMatch(receiverRfc))
            throw new ArgumentException("SAT request requires a UUID and valid RFCs.");
        if (total < 0 || total > 9999999999999999.99m || decimal.Round(total, 6) != total)
            throw new ArgumentOutOfRangeException(nameof(total), "SAT total must be nonnegative, within numeric(18,2) magnitude and have at most six fractional digits.");
        return $"https://verificacfdi.facturaelectronica.sat.gob.mx/default.aspx?id={uuid:D}&re={Uri.EscapeDataString(issuerRfc)}&rr={Uri.EscapeDataString(receiverRfc)}&tt={total.ToString("0000000000.000000", CultureInfo.InvariantCulture)}";
    }

    public static XDocument ReadXml(string xml)
    {
        try
        {
            if (xml.Length > MaxResponseBytes) throw new InvalidDataException("SAT XML size limit.");
            return Reader.Read(Encoding.UTF8.GetBytes(xml));
        }
        catch (Exception error) when (error is XmlException or InvalidDataException)
        { throw new SatPermanentException("Invalid or oversized SAT SOAP document.", error); }
    }

    public static SatStatusResponse Parse(string xml)
    {
        var document = ReadXml(xml);
        var result = document.Root?.Name == Soap + "Envelope"
            ? document.Root.Element(Soap + "Body")?.Element(Service + "ConsultaResponse")?.Element(Service + "ConsultaResult") : null;
        if (result is null) throw new SatPermanentException("SAT SOAP response is missing ConsultaResult.");
        string Value(string name) => result.Element(Acuse + name)?.Value ?? "";
        var code = Map(Codes, Value("CodigoEstatus"));
        var reply = new SatReply(code, Map(States, Value("Estado")), Map(Cancellabilities, Value("EsCancelable")),
            Map(Cancellations, Value("EstatusCancelacion")), result.Element(Acuse + "ValidacionEFOS")?.Value, xml);
        return code.Value switch
        {
            SatCode.InvalidExpression => new SatInvalidExpression(reply),
            SatCode.NotFound => new SatNotFound(reply),
            _ => new SatAnswered(reply)
        };
    }

    public static async Task<string> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[MaxResponseBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length), cancellationToken);
            if (read == 0) return Encoding.UTF8.GetString(bytes, 0, length);
            length += read;
        }
        throw new SatPermanentException("SAT SOAP size limit exceeded.");
    }

    public static string ResponseXml(SatCode code, SatState state, SatCancellability cancellability, SatCancellationStatus cancellation, string? efos) =>
        new XDocument(new XElement(Soap + "Envelope", new XElement(Soap + "Body", new XElement(Service + "ConsultaResponse",
            new XElement(Service + "ConsultaResult", new XElement(Acuse + "CodigoEstatus", Raw(Codes, code)), new XElement(Acuse + "Estado", Raw(States, state)),
                new XElement(Acuse + "EsCancelable", Raw(Cancellabilities, cancellability)), new XElement(Acuse + "EstatusCancelacion", Raw(Cancellations, cancellation)),
                efos is null ? null : new XElement(Acuse + "ValidacionEFOS", efos)))))).ToString();

    private static string Raw<T>(Dictionary<string, T> mappings, T value) where T : struct, Enum =>
        mappings.FirstOrDefault(pair => EqualityComparer<T>.Default.Equals(pair.Value, value)).Key ?? "";

    public static bool IsEfosFlagged(SatReply reply) => reply.Code.Value == SatCode.Success && !string.IsNullOrEmpty(reply.EfosRaw) && reply.EfosRaw != "200";

    private static SatValue<T> Map<T>(Dictionary<string, T> mappings, string raw) where T : struct, Enum => new(mappings.GetValueOrDefault(raw), raw);

    [GeneratedRegex(@"\A[A-ZÑ&]{3,4}[0-9]{6}[A-Z0-9]{3}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RfcFormat();
}
