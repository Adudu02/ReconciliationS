extern alias stub;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc.Testing;
using Reconciliation.Core.Sat;
using StubProgram = stub::Program;

namespace Reconciliation.Tests;

public sealed class SatStubTests
{
    [Theory]
    [InlineData("current", "SatAnswered", SatState.Current, SatCancellationStatus.None, "200")]
    [InlineData("cancelled", "SatAnswered", SatState.Cancelled, SatCancellationStatus.CancelledWithoutAcceptance, "200")]
    [InlineData("not_found", "SatNotFound", SatState.NotFound, SatCancellationStatus.None, "200")]
    [InlineData("invalid_expression", "SatInvalidExpression", SatState.Unknown, SatCancellationStatus.None, "200")]
    [InlineData("efos_flagged", "SatAnswered", SatState.Current, SatCancellationStatus.None, "100")]
    [InlineData("cancel_in_process", "SatAnswered", SatState.Current, SatCancellationStatus.InProcess, "200")]
    [InlineData("deadline_expired", "SatAnswered", SatState.Current, SatCancellationStatus.DeadlineExpired, "200")]
    public async Task Each_forced_outcome_speaks_the_real_SOAP_contract(string outcome, string type, SatState state, SatCancellationStatus cancellation, string efos)
    {
        await using var app = new WebApplicationFactory<StubProgram>();
        using var http = app.CreateClient();
        using var control = await http.PostAsJsonAsync("/control/outcomes", new { uuid = SatContractTests.SyntheticUuid, outcome }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, control.StatusCode);
        var result = await Client(http).CheckAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal(type, result.GetType().Name);
        Assert.Equal(state, result.Reply.State.Value);
        Assert.Equal(cancellation, result.Reply.CancellationStatus.Value);
        Assert.Equal(efos, result.Reply.EfosRaw);
        Assert.Contains("ConsultaResult", result.Reply.RawText, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "Forced timeout")]
    [InlineData("timeout")]
    [InlineData("slow")]
    [InlineData("server_error")]
    public async Task Unavailable_stub_is_a_typed_transient_failure(string outcome)
    {
        await using var app = new WebApplicationFactory<StubProgram>();
        using var http = app.CreateClient();
        using var control = await http.PostAsJsonAsync("/control/outcomes", new { uuid = SatContractTests.SyntheticUuid, outcome, delayMs = 1000 }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, control.StatusCode);
        await Assert.ThrowsAsync<SatTransientException>(() => Client(http, TimeSpan.FromMilliseconds(100)).CheckAsync(Request(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Default_is_current_and_control_reset_restores_default()
    {
        await using var app = new WebApplicationFactory<StubProgram>(); using var http = app.CreateClient();
        Assert.Equal(SatState.Current, (await Client(http).CheckAsync(Request(), TestContext.Current.CancellationToken)).Reply.State.Value);
        using var control = await http.PostAsJsonAsync("/control/outcomes", new { uuid = SatContractTests.SyntheticUuid, outcome = "cancelled" }, TestContext.Current.CancellationToken);
        using var reset = await http.DeleteAsync("/control/outcomes", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(SatState.Current, (await Client(http).CheckAsync(Request(), TestContext.Current.CancellationToken)).Reply.State.Value);
        using var health = await http.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Theory]
    [InlineData("nonsense", 1)]
    [InlineData("slow", -1)]
    [InlineData("slow", 60001)]
    public async Task Invalid_control_input_is_rejected(string outcome, int delayMs)
    {
        await using var app = new WebApplicationFactory<StubProgram>(); using var http = app.CreateClient();
        using var response = await http.PostAsJsonAsync("/control/outcomes", new { uuid = SatContractTests.SyntheticUuid, outcome, delayMs }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Printed_expression_is_parsed_and_invalid_expression_returns_601()
    {
        await using var app = new WebApplicationFactory<StubProgram>(); using var http = app.CreateClient();
        using var body = new StringContent(new XDocument(new XElement(SatContract.Soap + "Envelope", new XElement(SatContract.Soap + "Body",
            new XElement(SatContract.Service + "Consulta", new XElement(SatContract.Service + "expresionImpresa", "https://example.invalid/?id=not-a-uuid"))))).ToString(), Encoding.UTF8, "text/xml");
        using var response = await http.PostAsync("/ConsultaCFDIService.svc", body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.IsType<SatInvalidExpression>(SatContract.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task Non_transient_HTTP_failures_are_permanent(HttpStatusCode status)
    {
        using var http = new HttpClient(new ResponseHandler(status));
        await Assert.ThrowsAsync<SatPermanentException>(() => Client(http).CheckAsync(Request(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reclassified_as_service_failure()
    {
        await using var app = new WebApplicationFactory<StubProgram>(); using var http = app.CreateClient();
        using var cts = new CancellationTokenSource(); await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(http).CheckAsync(Request(), cts.Token));
    }

    internal static SatStatusRequest Request() => new(SatContractTests.SyntheticUuid, "AAA010101AAA", "BBB010101BBB", 1939.27m);
    private static ISatClient Client(HttpClient http, TimeSpan? timeout = null)
    {
        var type = typeof(ISatClient).Assembly.GetType("Reconciliation.Core.Sat.SoapSatClient"); Assert.NotNull(type);
        return (ISatClient)Activator.CreateInstance(type, http, new Uri("http://localhost/ConsultaCFDIService.svc"), timeout ?? TimeSpan.FromSeconds(10), TimeProvider.System)!;
    }
    private sealed class ResponseHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(status));
    }
}
