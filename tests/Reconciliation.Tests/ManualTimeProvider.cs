namespace Reconciliation.Tests;

public sealed class ManualTimeProvider : TimeProvider
{
    private long utcTicks = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero).UtcTicks;
    private readonly object gate = new();
    private readonly List<ClockTimer> timers = [];
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref utcTicks), TimeSpan.Zero);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref utcTicks);
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ClockTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }
    public void Advance(TimeSpan elapsed)
    {
        ClockTimer[] due;
        lock (gate)
        {
            utcTicks += elapsed.Ticks;
            due = timers.Where(timer => timer.Due <= utcTicks).ToArray();
            foreach (var timer in due) timer.Due = timer.Period == Timeout.InfiniteTimeSpan ? long.MaxValue : utcTicks + timer.Period.Ticks;
        }
        foreach (var timer in due) timer.Fire();
    }
    private sealed class ClockTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
    {
        public long Due { get; set; } = long.MaxValue;
        public TimeSpan Period { get; private set; }
        public void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock.gate)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.utcTicks + dueTime.Ticks;
                Period = period;
                if (!clock.timers.Contains(this)) clock.timers.Add(this);
            }
            return true;
        }
        public void Dispose() { lock (clock.gate) clock.timers.Remove(this); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
