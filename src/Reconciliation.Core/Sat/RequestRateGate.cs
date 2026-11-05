namespace Reconciliation.Core.Sat;

public interface IRequestRateGate
{
    Task WaitForPermitAsync(CancellationToken cancellationToken);
}

public sealed class RequestRateGate : IRequestRateGate
{
    private readonly TimeProvider clock;
    private readonly long spacing;
    private readonly object gate = new();
    private long? next;

    public RequestRateGate(TimeProvider clock, int requestsPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestsPerSecond);
        this.clock = clock;
        spacing = Math.Max(1, (clock.TimestampFrequency + requestsPerSecond - 1) / requestsPerSecond);
    }

    public async Task WaitForPermitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan delay;
            lock (gate)
            {
                var now = clock.GetTimestamp();
                if (next is null || now >= next.Value)
                {
                    next = checked(now + spacing);
                    return;
                }
                delay = clock.GetElapsedTime(now, next.Value);
            }
            // Recheck after waking: clock jumps or concurrent waiters must not release a catch-up burst.
            await Task.Delay(delay < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : delay, clock, cancellationToken);
        }
    }
}
