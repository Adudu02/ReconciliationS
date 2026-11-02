namespace Reconciliation.Tests;

public sealed class ManualTimeProvider : TimeProvider
{
    private long utcTicks = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero).UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref utcTicks), TimeSpan.Zero);
    public void Advance(TimeSpan elapsed) => Interlocked.Add(ref utcTicks, elapsed.Ticks);
}
