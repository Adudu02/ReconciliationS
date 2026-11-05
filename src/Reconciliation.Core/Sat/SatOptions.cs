namespace Reconciliation.Core.Sat;

public sealed class SatOptions
{
    public string Mode { get; init; } = "stub";
    public string? Endpoint { get; init; }
    public bool RecordResponses { get; init; }
    public string RecordingDirectory { get; init; } = ".local/sat-recordings";
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan JobBudget { get; init; } = TimeSpan.FromSeconds(60);
    public int MaxFollowUpGenerations { get; init; } = 20;
    public Uri EndpointUri => new(string.IsNullOrEmpty(Endpoint) ? (Mode == "real" ? SatContract.PublicEndpoint : "http://sat-stub:8081/ConsultaCFDIService.svc") : Endpoint);
    public TimeSpan CurrentCacheTtl { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan NotFoundCacheTtl { get; init; } = TimeSpan.FromHours(1);
    public int MaxAttempts { get; init; } = 3;
    public int MaxRequestsPerSecond { get; init; } = 2;
    internal bool UsesAnonymizedKey => Mode == "replay" || RecordResponses;

    internal void Validate()
    {
        if (Mode is not ("stub" or "real" or "replay")) throw new ArgumentException("Sat:Mode must be stub, real or replay.");
        if (!Uri.TryCreate(string.IsNullOrEmpty(Endpoint) ? EndpointUri.AbsoluteUri : Endpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length != 0 || (Mode == "real" && endpoint.Scheme != "https"))
            throw new ArgumentException("Sat:Endpoint must be an absolute HTTP(S) URL without credentials; real mode requires HTTPS.");
        if (CurrentCacheTtl <= TimeSpan.Zero || NotFoundCacheTtl <= TimeSpan.Zero || RequestTimeout <= TimeSpan.Zero || JobBudget <= TimeSpan.Zero ||
            RequestTimeout.TotalMilliseconds > int.MaxValue || JobBudget.TotalMilliseconds > int.MaxValue ||
            MaxAttempts <= 0 || MaxRequestsPerSecond <= 0 || MaxFollowUpGenerations <= 0)
            throw new ArgumentException("Sat: TTLs, rates, attempts, budget and follow-up generations must be positive; timeout/budget must fit a timer.");
        if (string.IsNullOrWhiteSpace(RecordingDirectory)) throw new ArgumentException("Sat:RecordingDirectory must be nonempty.");
        if (Mode == "replay" && RecordResponses) throw new ArgumentException("Sat:RecordResponses cannot be enabled in replay mode.");
    }
}
