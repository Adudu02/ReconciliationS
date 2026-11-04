using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class AuditBenchmarks(ApiFixture fixture)
{
    private static readonly JsonSerializerOptions OutputOptions = new() { WriteIndented = true };
    public static bool Enabled => Environment.GetEnvironmentVariable("RUN_AUDIT_BENCHMARKS") == "1";

    [Fact(Skip = "Opt-in: set RUN_AUDIT_BENCHMARKS=1", SkipUnless = nameof(Enabled), Timeout = 600_000)]
    public async Task Measure_item_filter_and_verification_at_20000_and_100000_company_entries()
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        using var client = await fixture.LoginAsync(user.Email, company);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        var results = new List<object>();
        foreach (var count in new[] { 20_000, 100_000 })
        {
            await using (var tx = await owner.BeginTransactionAsync(TestContext.Current.CancellationToken))
            {
                await AuditDatabaseTests.SetContextAsync(owner, tx, company);
                await owner.ExecuteAsync(new CommandDefinition("""
                    SELECT append_audit_entry(@company,'user',@actor,'synthetic.benchmark',
                      jsonb_build_array(jsonb_build_object('kind','movement','id',n::text)),'{}')
                    FROM generate_series(@start,@count) n
                    """, new { company, actor = user.Id, start = count == 20_000 ? 1 : 20_001, count }, tx, commandTimeout: 300, cancellationToken: TestContext.Current.CancellationToken));
                await tx.CommitAsync(TestContext.Current.CancellationToken);
            }
            await owner.ExecuteAsync(new CommandDefinition("ANALYZE audit_entry", cancellationToken: TestContext.Current.CancellationToken));
            var times = new List<double>();
            for (var sample = 0; sample < 4; sample++)
            {
                var watch = Stopwatch.StartNew();
                using var response = await client.GetAsync($"/companies/{company}/audit-entries?item={count}", TestContext.Current.CancellationToken);
                response.EnsureSuccessStatusCode();
                var page = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
                watch.Stop();
                Assert.Equal(1, page.GetProperty("entries").GetArrayLength());
                if (sample > 0) times.Add(watch.Elapsed.TotalMilliseconds);
            }
            var timer = Stopwatch.StartNew();
            using var verified = await client.GetAsync($"/companies/{company}/audit/verification", TestContext.Current.CancellationToken);
            verified.EnsureSuccessStatusCode();
            var verification = await verified.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            timer.Stop();
            Assert.True(verification.GetProperty("consistent").GetBoolean());
            Assert.Equal(count, verification.GetProperty("entries").GetInt64());
            results.Add(new { CompanyEntries = count, ItemFilterMilliseconds = times, MedianMilliseconds = times.Order().ElementAt(1), VerificationMilliseconds = timer.Elapsed.TotalMilliseconds });
        }
        await using var demo = await owner.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await owner.ExecuteAsync(new CommandDefinition("CREATE INDEX audit_items_demo_idx ON audit_entry USING gin(items jsonb_path_ops); SET LOCAL enable_seqscan=off", transaction: demo, cancellationToken: TestContext.Current.CancellationToken));
        var ownerPlan = await owner.QueryAsync<string>(new CommandDefinition("EXPLAIN SELECT id FROM audit_entry WHERE items @> '[{\"id\":\"100000\"}]'", transaction: demo, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("audit_items_demo_idx", string.Join('\n', ownerPlan), StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "audit-benchmark.json"), JsonSerializer.Serialize(new { Warmups = 1, Samples = 3, Results = results, OwnerGinDemoPlan = ownerPlan }, OutputOptions), TestContext.Current.CancellationToken);
    }
}
