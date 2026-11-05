using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Xml.Linq;
using Reconciliation.Core.Matching;

namespace Reconciliation.Tests;

public sealed class SatContractTests
{
    internal const string Success = "S - Comprobante obtenido satisfactoriamente";
    internal const string Invalid = "N - 601: La expresión impresa proporcionada no es válida";
    internal const string Missing = "N - 602: Comprobante no encontrado";
    internal static readonly Guid SyntheticUuid = Guid.Parse("00000000-0000-4000-8000-000000000701");

    [Fact]
    public void Every_documented_code_state_cancellability_cancellation_and_EFOS_combination_preserves_raw_values()
    {
        var codes = new[] { (Success, "SatAnswered", "Success"), (Invalid, "SatInvalidExpression", "InvalidExpression"), (Missing, "SatNotFound", "NotFound"), ("Future code", "SatAnswered", "Unknown") };
        var states = new[] { ("Vigente", "Current"), ("Cancelado", "Cancelled"), ("No Encontrado", "NotFound"), ("Future state", "Unknown") };
        var cancellabilities = new[] { ("Cancelable sin aceptación", "WithoutAcceptance"), ("Cancelable con aceptación", "WithAcceptance"), ("No cancelable", "NotCancellable"), ("Future cancellability", "Unknown") };
        var cancellations = new[] { ("", "None"), ("En proceso", "InProcess"), ("Plazo vencido", "DeadlineExpired"), ("Cancelado sin aceptación", "CancelledWithoutAcceptance"), ("Cancelado con aceptación", "CancelledWithAcceptance"), ("Solicitud rechazada", "Rejected"), ("Future cancellation", "Unknown") };
        foreach (var (code, outcome, codeValue) in codes)
            foreach (var (state, stateValue) in states)
                foreach (var (cancellable, cancellableValue) in cancellabilities)
                    foreach (var (cancellation, cancellationValue) in cancellations)
                        foreach (var efos in new[] { "200", "100", "future" })
                        {
                            var xml = Envelope(code, state, cancellable, cancellation, efos);
                            var response = Invoke("Parse", xml);
                            Assert.Equal(outcome, response.GetType().Name);
                            var reply = Property(response, "Reply");
                            AssertMapped(reply, "Code", codeValue, code);
                            AssertMapped(reply, "State", stateValue, state);
                            AssertMapped(reply, "Cancellability", cancellableValue, cancellable);
                            AssertMapped(reply, "CancellationStatus", cancellationValue, cancellation);
                            Assert.Equal(efos, Property(reply, "EfosRaw"));
                            Assert.Equal(code == Success && efos != "200", Property(reply, "EfosFlagged"));
                            Assert.Equal(xml, Property(reply, "RawText"));
                        }
    }

    [Theory]
    [InlineData("0", "0000000000.000000")]
    [InlineData("1939.27", "0000001939.270000")]
    [InlineData("1.123456", "0000000001.123456")]
    [InlineData("9999999999999999.99", "9999999999999999.990000")]
    public void Printed_expression_uses_exact_invariant_padded_decimal_and_escaped_RFC(string amount, string total)
    {
        var expression = (string)Invoke("PrintedExpression", SyntheticUuid, "A&A010101AAA", "BBB010101BBB", decimal.Parse(amount, CultureInfo.InvariantCulture));
        Assert.Equal($"https://verificacfdi.facturaelectronica.sat.gob.mx/default.aspx?id={SyntheticUuid:D}&re=A%26A010101AAA&rr=BBB010101BBB&tt={total}", expression);
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("0.1234567")]
    [InlineData("10000000000000000")]
    public void Invalid_total_is_rejected_without_rounding(string total) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Invoke("PrintedExpression", SyntheticUuid, "AAA010101AAA", "BBB010101BBB", decimal.Parse(total, CultureInfo.InvariantCulture)));

    [Fact]
    public void Empty_UUID_or_invalid_RFC_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Invoke("PrintedExpression", Guid.Empty, "AAA010101AAA", "BBB010101BBB", 0m));
        Assert.Throws<ArgumentException>(() => Invoke("PrintedExpression", SyntheticUuid, "AAA&rr=injected", "BBB010101BBB", 0m));
    }

    [Theory]
    [InlineData("<!DOCTYPE a [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><a>&x;</a>")]
    [InlineData("<broken>")]
    [InlineData("<Envelope><ConsultaResult><Estado>Cancelado</Estado></ConsultaResult></Envelope>")]
    public void Hostile_or_non_contract_XML_is_a_permanent_failure(string xml)
    {
        var error = Assert.ThrowsAny<Exception>(() => Invoke("Parse", xml));
        Assert.Equal("SatPermanentException", error.GetType().Name);
    }

    [Fact]
    public void SOAP_size_depth_node_limits_and_faults_are_bounded()
    {
        var huge = new string('x', 65537);
        var deep = string.Concat(Enumerable.Repeat("<a>", 40)) + string.Concat(Enumerable.Repeat("</a>", 40));
        var many = "<a>" + string.Concat(Enumerable.Repeat("<b/>", 2000)) + "</a>";
        foreach (var xml in new[] { huge, deep, many, Envelope(Success, "Vigente").Replace("ConsultaResult", "Fault", StringComparison.Ordinal) })
            Assert.Equal("SatPermanentException", Assert.ThrowsAny<Exception>(() => Invoke("Parse", xml)).GetType().Name);
    }

    internal static string Envelope(string code = Success, string state = "Vigente", string cancellable = "Cancelable sin aceptación", string cancellation = "", string? efos = "200")
    {
        XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
        XNamespace service = "http://tempuri.org/";
        XNamespace contract = "http://schemas.datacontract.org/2004/07/Sat.Cfdi.Negocio.ConsultaCfdi.Servicio";
        return new XDocument(new XElement(soap + "Envelope", new XElement(soap + "Body", new XElement(service + "ConsultaResponse",
            new XElement(service + "ConsultaResult", new XElement(contract + "CodigoEstatus", code), new XElement(contract + "Estado", state),
                new XElement(contract + "EsCancelable", cancellable), new XElement(contract + "EstatusCancelacion", cancellation),
                efos is null ? null : new XElement(contract + "ValidacionEFOS", efos)))))).ToString();
    }

    private static object Invoke(string name, params object[] values)
    {
        var contract = typeof(MatchingEngine).Assembly.GetType("Reconciliation.Core.Sat.SatContract");
        Assert.NotNull(contract);
        var method = contract.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        try { return method.Invoke(null, values)!; }
        catch (TargetInvocationException error) when (error.InnerException is not null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static object Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value)!;
    private static void AssertMapped(object reply, string field, string expected, string raw)
    {
        var mapped = Property(reply, field); Assert.Equal(expected, Property(mapped, "Value").ToString()); Assert.Equal(raw, Property(mapped, "Raw"));
    }
}
