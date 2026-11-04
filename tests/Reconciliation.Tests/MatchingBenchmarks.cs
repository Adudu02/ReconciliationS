using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Reconciliation.Core.Matching;

namespace Reconciliation.Tests;

public sealed class MatchingBenchmarks
{
    public static bool Enabled => Environment.GetEnvironmentVariable("RUN_BENCHMARKS") == "1";

    [Fact(Skip = "Opt-in: set RUN_BENCHMARKS=1", SkipUnless = nameof(Enabled), Timeout = 600_000)]
    public async Task Measure_20000_unique_amounts_and_5000_dense_equal_amounts()
    {
        var results = new List<object>();
        foreach (var (count, dense) in new[] { (20_000, false), (5_000, true) })
        {
            var invoices = Enumerable.Range(1, count).Select(i => SyntheticMatching.Invoice(dense ? 100 : 100 + 2 * i) with
            { Id = new Guid(i, 0, 0, new byte[8]), Uuid = new Guid(i, 0, 0, new byte[8]) }).ToArray();
            var movements = invoices.Select((invoice, i) => SyntheticMatching.FirstMovement(invoice.Total) with
            { Id = new Guid(i + count + 1, 0, 0, new byte[8]), Description = "synthetic transfer", Reference = null }).ToArray();
            var times = new List<double>();
            long comparisons = 0;
            for (var sample = 0; sample < 4; sample++)
            {
                var watch = Stopwatch.StartNew();
                var result = MatchingEngine.Run(movements, invoices, [], [], new(3, .01m));
                watch.Stop();
                Assert.Equal(count, result.Changes.Count);
                Assert.True(result.CandidateComparisons <= count * 3 * 2 * MatchingEngine.MaxCandidatesPerMovement);
                comparisons = result.CandidateComparisons;
                if (sample > 0) times.Add(watch.Elapsed.TotalMilliseconds);
            }
            results.Add(new { Movements = count, Invoices = count, Dense = dense, Samples = times, MedianMilliseconds = times.Order().ElementAt(1), Comparisons = comparisons });
        }
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "matching-benchmark.json"), JsonSerializer.Serialize(new
        { Runtime = RuntimeInformation.FrameworkDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Warmups = 1, MeasuredRuns = 3, Results = results }), TestContext.Current.CancellationToken);
    }
}
