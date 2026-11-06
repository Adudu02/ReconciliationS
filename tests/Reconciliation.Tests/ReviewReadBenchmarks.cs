using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace Reconciliation.Tests;

public sealed class ReviewReadBenchmarks(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    public static bool Enabled => Environment.GetEnvironmentVariable("RUN_REVIEW_BENCHMARKS") == "1";

    [Fact(Skip = "Opt-in: set RUN_REVIEW_BENCHMARKS=1", SkipUnless = nameof(Enabled), Timeout = 600_000)]
    public async Task Measure_queue_and_overview_with_20000_invoices_and_movements()
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        await using (var owner = await fixture.Database.OpenOwnerAsync())
        {
            await owner.ExecuteAsync(new CommandDefinition("""
                INSERT INTO cfdi(company_id,import_file_id,uuid,type,direction,issuer_rfc,receiver_rfc,issued_at,stamped_at,currency,subtotal,total,payment_method)
                SELECT company_id,import_file_id,gen_random_uuid(),type,direction,issuer_rfc,receiver_rfc,issued_at,stamped_at,currency,subtotal,total,payment_method
                  FROM cfdi CROSS JOIN generate_series(1,19999) WHERE id=@Invoice;
                INSERT INTO bank_movement(company_id,bank_account_id,movement_date,description,credit,debit,balance)
                SELECT company_id,bank_account_id,movement_date,'Synthetic benchmark '||n,credit,debit,balance
                  FROM bank_movement CROSS JOIN generate_series(1,19999) n WHERE id=@Movement;
                WITH invoices AS (SELECT id,row_number() OVER(ORDER BY id) n FROM cfdi WHERE company_id=@Company),
                  movements AS (SELECT id,row_number() OVER(ORDER BY id) n FROM bank_movement WHERE company_id=@Company)
                INSERT INTO link(company_id,movement_id,cfdi_id,amount_applied,state,origin,criteria,confidence,explanation)
                  SELECT @Company,m.id,c.id,100,'suggested','automatic','[]','low','Synthetic benchmark suggestion' FROM invoices c JOIN movements m USING(n);
                ANALYZE cfdi; ANALYZE link; ANALYZE bank_movement;
                """, seed, cancellationToken: TestContext.Current.CancellationToken));
        }
        var results = new List<object>();
        var plans = new List<string>();
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        foreach (var route in new[] { "review-queue?limit=200", "overview" })
        {
            var samples = new List<double>();
            for (var sample = 0; sample < 4; sample++)
            {
                var watch = Stopwatch.StartNew();
                using var response = await client.GetAsync($"/companies/{seed.Company}/{route}", TestContext.Current.CancellationToken);
                response.EnsureSuccessStatusCode();
                var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
                watch.Stop();
                using var body = JsonDocument.Parse(content);
                if (route == "overview")
                {
                    Assert.Equal(20000, body.RootElement.GetProperty("invoices").GetProperty("count").GetInt64());
                    Assert.Equal(20000, body.RootElement.GetProperty("movements").GetProperty("count").GetInt64());
                }
                else Assert.Equal(200, body.RootElement.GetProperty("entries").GetArrayLength());
                if (sample > 0) samples.Add(watch.Elapsed.TotalMilliseconds);
            }
            results.Add(new { route, invoices = 20000, movements = 20000, samples, medianMilliseconds = samples.Order().ElementAt(1) });
        }
        await using var app = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await app.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await app.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await AuditDatabaseTests.SetContextAsync(app, transaction, seed.Company);
        foreach (var file in new[] { "ReviewEndpoints.cs", "OverviewEndpoints.cs" })
        {
            var source = await File.ReadAllTextAsync(Path.Combine(RepoPaths.Root, "src", "Reconciliation.Core", "Matching", file), TestContext.Current.CancellationToken);
            foreach (var command in source.Split("Command(\"\"\"", StringSplitOptions.None).Skip(1))
            {
                var sql = command.Split("\"\"\"", StringSplitOptions.None)[0];
                var plan = await app.QueryAsync<string>(new CommandDefinition("EXPLAIN (ANALYZE,BUFFERS,FORMAT TEXT) " + sql,
                    new { companyId = seed.Company, today = DateOnly.FromDateTime(fixture.Clock.GetUtcNow().UtcDateTime), afterRank = (int?)null, afterDate = (DateOnly?)null, afterId = (Guid?)null, take = 201 }, transaction, cancellationToken: TestContext.Current.CancellationToken));
                plans.Add(file + " cfdi_app EXPLAIN:\n" + string.Join('\n', plan));
            }
        }
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "review-benchmark.json"), JsonSerializer.Serialize(new { warmups = 1, measuredRuns = 3, results, plans }), TestContext.Current.CancellationToken);
    }
}
