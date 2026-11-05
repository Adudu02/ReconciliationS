namespace Reconciliation.Core.Sat;

public sealed class SatOptions
{
    public TimeSpan CurrentCacheTtl { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan NotFoundCacheTtl { get; init; } = TimeSpan.FromHours(1);
    public int MaxAttempts { get; init; } = 3;
    public int MaxRequestsPerSecond { get; init; } = 2;
}
