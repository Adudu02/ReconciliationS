namespace Reconciliation.Core.Sat;

public sealed record SatCacheEntry(Guid Uuid, string State, string? Cancellability, string? CancellationStatus, string? EfosRaw,
    DateTime CheckedAt, DateTime? ExpiresAt, string? LastError, int Attempts, string Source,
    string? CodeRaw = null, string? StateRaw = null, string? CancellabilityRaw = null, string? CancellationStatusRaw = null, Guid? LastJobId = null, string? LastErrorKind = null);

public sealed record SatLookupResult(SatStatusResponse? Response, SatCacheEntry? Cache, int Attempts, string? Error, bool Retry)
{
    public bool FromCache => Cache is not null;
}

public sealed class SatStatusLookup(ISatClient client, IRequestRateGate gate, SatOptions options, TimeProvider clock, Func<int, int> jitter)
{
    public async Task<SatLookupResult> CheckAsync(SatStatusRequest request, SatCacheEntry? cache, CancellationToken cancellationToken)
    {
        if (cache is not null && cache.Uuid == request.Uuid && cache.State != "unknown" && cache.LastError is null &&
            (cache.ExpiresAt is null || cache.ExpiresAt > clock.GetUtcNow().UtcDateTime))
            return new(null, cache, 0, null, false);
        for (var attempt = 1; ; attempt++)
        {
            await gate.WaitForPermitAsync(cancellationToken);
            try
            {
                var response = await client.CheckAsync(request, cancellationToken);
                return new(response, null, attempt, response is SatInvalidExpression ? "SAT invalid expression (601)." : null, false);
            }
            catch (SatTransientException)
            {
                if (attempt >= options.MaxAttempts) return new(null, null, attempt, "SAT unavailable after bounded attempts.", true);
                var milliseconds = (int)Math.Min(30000, 1000L << Math.Min(attempt - 1, 5));
                await Task.Delay(TimeSpan.FromMilliseconds(milliseconds + jitter(milliseconds / 5 + 1)), clock, cancellationToken);
            }
            catch (SatPermanentException) { return new(null, null, attempt, "SAT permanently refused the request or response contract.", false); }
        }
    }
}
