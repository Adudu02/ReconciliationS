using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using Reconciliation.Core.Sat;

namespace Reconciliation.Tests;

public sealed class SatRateAndLookupTests
{
    [Theory(DisplayName = "Large batch")]
    [InlineData(2, 1000)]
    [InlineData(3, 40)]
    public async Task Concurrent_callers_use_one_fake_time_gate_and_never_exceed_rate_in_a_second(int rate, int count)
    {
        var clock = new ManualTimeProvider(); var gate = Gate(clock, rate);
        var permits = Channel.CreateUnbounded<DateTimeOffset>();
        var tasks = Enumerable.Range(0, count).Select(async _ => { await Permit(gate, TestContext.Current.CancellationToken); await permits.Writer.WriteAsync(clock.GetUtcNow(), TestContext.Current.CancellationToken); }).ToArray();
        var actual = new List<DateTimeOffset> { await permits.Reader.ReadAsync(TestContext.Current.CancellationToken) };
        var spacing = TimeSpan.FromTicks((TimeSpan.TicksPerSecond + rate - 1) / rate);
        for (var i = 1; i < count; i++)
        {
            clock.Advance(spacing);
            actual.Add(await permits.Reader.ReadAsync(TestContext.Current.CancellationToken));
        }
        await Task.WhenAll(tasks);
        for (var i = 1; i < actual.Count; i++) Assert.Equal(spacing, actual[i] - actual[i - 1]);
        foreach (var start in actual) Assert.True(actual.Count(at => at >= start && at < start.AddSeconds(1)) <= rate);
    }

    [Fact]
    public async Task Cancellation_does_not_consume_a_future_slot_and_clock_jumps_do_not_release_a_burst()
    {
        var clock = new ManualTimeProvider(); var gate = Gate(clock, 2);
        await Permit(gate, TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource(); var waiting = Permit(gate, cts.Token);
        await cts.CancelAsync(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        var first = Permit(gate, TestContext.Current.CancellationToken); var second = Permit(gate, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(10));
        await Task.WhenAny(first, second);
        Assert.False(first.IsCompleted && second.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(500)); await Task.WhenAll(first, second);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Nonpositive_rate_is_rejected(int rate)
    {
        var error = Assert.Throws<TargetInvocationException>(() => Gate(new ManualTimeProvider(), rate));
        Assert.IsType<ArgumentOutOfRangeException>(error.InnerException);
    }

    [Fact]
    public async Task Transient_attempts_use_increasing_fake_time_backoff_and_then_succeed()
    {
        var clock = new ManualTimeProvider(); var calls = Channel.CreateUnbounded<DateTimeOffset>();
        var client = new FakeSatClient(async (attempt, _) =>
        {
            await calls.Writer.WriteAsync(clock.GetUtcNow(), TestContext.Current.CancellationToken);
            if (attempt < 3) throw new SatTransientException("Synthetic unavailable");
            return SatContract.Parse(SatContractTests.Envelope());
        });
        var lookup = Lookup(client, clock); var check = Check(lookup, null);
        var start = await calls.Reader.ReadAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(1)); Assert.Equal(start.AddSeconds(1), await calls.Reader.ReadAsync(TestContext.Current.CancellationToken));
        clock.Advance(TimeSpan.FromSeconds(2)); Assert.Equal(start.AddSeconds(3), await calls.Reader.ReadAsync(TestContext.Current.CancellationToken));
        var result = await check;
        Assert.Equal(3, Property(result, "Attempts")); Assert.False((bool)Property(result, "Retry"));
        Assert.IsType<SatAnswered>(Property(result, "Response"));
    }

    [Theory]
    [InlineData("transient", 3, true)]
    [InlineData("permanent", 1, false)]
    [InlineData("601", 1, false)]
    public async Task Bounded_failures_and_601_are_explicit_results(string kind, int expectedCalls, bool retry)
    {
        var clock = new ManualTimeProvider(); var calls = Channel.CreateUnbounded<int>();
        var client = new FakeSatClient(async (attempt, _) =>
        {
            await calls.Writer.WriteAsync(attempt, TestContext.Current.CancellationToken);
            if (kind == "transient") throw new SatTransientException("Synthetic unavailable");
            if (kind == "permanent") throw new SatPermanentException("Synthetic bad response");
            return SatContract.Parse(SatContractTests.Envelope(SatContractTests.Invalid));
        });
        var check = Check(Lookup(client, clock), null);
        Assert.Equal(1, await calls.Reader.ReadAsync(TestContext.Current.CancellationToken));
        if (kind == "transient")
        {
            clock.Advance(TimeSpan.FromSeconds(1)); Assert.Equal(2, await calls.Reader.ReadAsync(TestContext.Current.CancellationToken));
            clock.Advance(TimeSpan.FromSeconds(2)); Assert.Equal(3, await calls.Reader.ReadAsync(TestContext.Current.CancellationToken));
        }
        var result = await check; Assert.Equal(expectedCalls, client.Calls); Assert.Equal(retry, Property(result, "Retry"));
        Assert.Equal(expectedCalls, Property(result, "Attempts")); Assert.NotNull(Property(result, "Error"));
    }

    [Theory(DisplayName = "Cached current status / Cancelled status cached")]
    [InlineData("current", 23, true)]
    [InlineData("current", 24, false)]
    [InlineData("not_found", 0, true)]
    [InlineData("not_found", 1, false)]
    [InlineData("cancelled", 10000, true)]
    [InlineData("unknown", 0, false)]
    public async Task Cache_expiry_is_exact_and_unknown_is_always_rechecked(string state, int hours, bool hit)
    {
        var clock = new ManualTimeProvider(); var now = clock.GetUtcNow();
        var cache = Cache(state, now.UtcDateTime, state == "cancelled" ? null : now.AddHours(state == "not_found" ? 1 : 24).UtcDateTime);
        clock.Advance(TimeSpan.FromHours(hours));
        var client = new FakeSatClient((_, _) => Task.FromResult(SatContract.Parse(SatContractTests.Envelope())));
        var result = await Check(Lookup(client, clock), cache);
        Assert.Equal(hit, Property(result, "FromCache")); Assert.Equal(hit ? 0 : 1, client.Calls);
    }

    [Theory]
    [InlineData(SatContractTests.Success, "200", false)]
    [InlineData(SatContractTests.Success, "100", true)]
    [InlineData(SatContractTests.Success, "future", true)]
    [InlineData(SatContractTests.Success, "", false)]
    [InlineData(SatContractTests.Success, null, false)]
    [InlineData(SatContractTests.Invalid, "100", false)]
    [InlineData("Future code", "100", false)]
    public void Only_an_actual_success_with_present_nonclear_EFOS_warns(string code, string? efos, bool flagged) =>
        Assert.Equal(flagged, SatContract.Parse(SatContractTests.Envelope(code, efos: efos)).Reply.EfosFlagged);

    internal static object Cache(string state, DateTime checkedAt, DateTime? expiresAt) => JsonSerializer.Deserialize(JsonSerializer.Serialize(new
    { uuid = SatContractTests.SyntheticUuid, state, checkedAt, expiresAt, attempts = 1, source = state == "cancelled" ? "backfill" : "stub" }), Type("SatCacheEntry"), JsonSerializerOptions.Web)!;
    private static object Gate(TimeProvider clock, int rate) => Activator.CreateInstance(Type("RequestRateGate"), clock, rate)!;
    private static Task Permit(object gate, CancellationToken token) => (Task)gate.GetType().GetMethod("WaitForPermitAsync")!.Invoke(gate, [token])!;
    internal static object Lookup(ISatClient client, TimeProvider clock) => Activator.CreateInstance(Type("SatStatusLookup"), client, Gate(clock, 2), Activator.CreateInstance(Type("SatOptions")), clock, (Func<int, int>)(_ => 0))!;
    internal static async Task<object> Check(object lookup, object? cache, SatStatusRequest? request = null)
    {
        var task = (Task)lookup.GetType().GetMethod("CheckAsync")!.Invoke(lookup, [request ?? SatStubTests.Request(), cache, TestContext.Current.CancellationToken])!;
        await task; return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }
    private static Type Type(string name) { var type = typeof(ISatClient).Assembly.GetType("Reconciliation.Core.Sat." + name); Assert.NotNull(type); return type; }
    private static object Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value)!;
}

internal sealed class FakeSatClient(Func<int, CancellationToken, Task<SatStatusResponse>> check) : ISatClient
{
    private int calls;
    public int Calls => Volatile.Read(ref calls);
    public Task<SatStatusResponse> CheckAsync(SatStatusRequest request, CancellationToken cancellationToken) => check(Interlocked.Increment(ref calls), cancellationToken);
}
