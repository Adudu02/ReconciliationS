// Placeholder for the local SAT status stub. Task 7.2 replaces it with a service that speaks the real
// SOAP contract and lets tests force each outcome.
var app = WebApplication.CreateBuilder(args).Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "sat-stub" }));
app.MapFallback(() => Results.Text("SAT stub: contract not implemented yet", statusCode: StatusCodes.Status501NotImplemented));

app.Run();
