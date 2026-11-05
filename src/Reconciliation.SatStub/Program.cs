using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;
using Reconciliation.Core.Sat;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = SatContract.MaxResponseBytes);
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<StubOutcomes>();
var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "sat-stub" }));
app.MapPost("/control/outcomes", (StubControl request, StubOutcomes outcomes) =>
{
    if (request.Uuid == Guid.Empty || !StubOutcomes.Names.Contains(request.Outcome) || request.DelayMs is < 0 or > 60000) return Results.BadRequest();
    outcomes.Values[request.Uuid] = request;
    return Results.NoContent();
});
app.MapDelete("/control/outcomes", (StubOutcomes outcomes) => { outcomes.Values.Clear(); return Results.NoContent(); });
app.MapPost("/ConsultaCFDIService.svc", async (HttpRequest request, StubOutcomes outcomes, TimeProvider clock, CancellationToken token) =>
{
    string xml;
    try { xml = await SatContract.ReadBoundedAsync(request.Body, token); }
    catch (SatPermanentException) { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
    Guid uuid;
    try
    {
        var document = SatContract.ReadXml(xml);
        var expression = document.Root?.Name == SatContract.Soap + "Envelope"
            ? document.Root.Element(SatContract.Soap + "Body")?.Element(SatContract.Service + "Consulta")?.Element(SatContract.Service + "expresionImpresa")?.Value : null;
        if (!Uri.TryCreate(expression, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "verificacfdi.facturaelectronica.sat.gob.mx" || uri.AbsolutePath != "/default.aspx")
            return Reply(SatCode.InvalidExpression, SatState.Unknown);
        var query = QueryHelpers.ParseQuery(uri.Query);
        if (query.Count != 4 || !Guid.TryParseExact(query.GetValueOrDefault("id"), "D", out uuid) || uuid == Guid.Empty ||
            !decimal.TryParse(query.GetValueOrDefault("tt"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var total))
            return Reply(SatCode.InvalidExpression, SatState.Unknown);
        _ = SatContract.PrintedExpression(uuid, query.GetValueOrDefault("re").ToString(), query.GetValueOrDefault("rr").ToString(), total);
    }
    catch (ArgumentException) { return Reply(SatCode.InvalidExpression, SatState.Unknown); }
    catch (SatPermanentException) { return Results.BadRequest(); }
    var control = outcomes.Values.GetValueOrDefault(uuid);
    if (control?.Outcome is "timeout" or "slow")
        await Task.Delay(TimeSpan.FromMilliseconds(control.DelayMs ?? (control.Outcome == "timeout" ? 60000 : 15000)), clock, token);
    return control?.Outcome switch
    {
        "server_error" => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
        "not_found" => Reply(SatCode.NotFound, SatState.NotFound),
        "invalid_expression" => Reply(SatCode.InvalidExpression, SatState.Unknown),
        "cancelled" => Reply(SatCode.Success, SatState.Cancelled, SatCancellationStatus.CancelledWithoutAcceptance),
        "cancel_in_process" => Reply(SatCode.Success, SatState.Current, SatCancellationStatus.InProcess),
        "deadline_expired" => Reply(SatCode.Success, SatState.Current, SatCancellationStatus.DeadlineExpired),
        "efos_flagged" => Reply(SatCode.Success, SatState.Current, efos: "100"),
        _ => Reply(SatCode.Success, SatState.Current)
    };
});
app.Run();

static IResult Reply(SatCode code, SatState state, SatCancellationStatus cancellation = SatCancellationStatus.None, string efos = "200") =>
    Results.Text(SatContract.ResponseXml(code, state, SatCancellability.WithoutAcceptance, cancellation, efos), "text/xml; charset=utf-8");

internal sealed record StubControl(Guid Uuid, string Outcome, int? DelayMs);
internal sealed class StubOutcomes
{
    internal static readonly HashSet<string> Names = ["current", "cancelled", "not_found", "invalid_expression", "efos_flagged", "cancel_in_process", "deadline_expired", "timeout", "server_error", "slow"];
    internal ConcurrentDictionary<Guid, StubControl> Values { get; } = new();
}
