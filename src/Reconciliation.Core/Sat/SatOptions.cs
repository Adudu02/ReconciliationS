namespace Reconciliation.Core.Sat;

public sealed class SatOptions
{
    public string Mode { get; init; } = "stub";
    public string? Endpoint { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan JobBudget { get; init; } = TimeSpan.FromSeconds(60);
    public int MaxFollowUpGenerations { get; init; } = 20;
    public Uri EndpointUri => new(Endpoint ?? (Mode == "real" ? SatContract.PublicEndpoint : "http://sat-stub:8081/ConsultaCFDIService.svc"));
    public TimeSpan CurrentCacheTtl { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan NotFoundCacheTtl { get; init; } = TimeSpan.FromHours(1);
    public int MaxAttempts { get; init; } = 3;
    public int MaxRequestsPerSecond { get; init; } = 2;
}
