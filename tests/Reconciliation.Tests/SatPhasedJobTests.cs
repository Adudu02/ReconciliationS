using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Reconciliation.Core.Ingestion;
using Reconciliation.Core.Jobs;
using Reconciliation.Core.Matching;
using Reconciliation.Core.Sat;
using Reconciliation.Core.Tenancy;

namespace Reconciliation.Tests;

public sealed class SatPhasedJobTests : IAsyncLifetime
{
    private readonly ApiFixture fixture = new() { JobLeaseSeconds = 600 };
    public async ValueTask InitializeAsync() { await fixture.InitializeAsync(); fixture.Sat.DefaultCurrent = true; }
    public ValueTask DisposeAsync() => fixture.DisposeAsync();

    [Fact(DisplayName = "Service down during a reconciliation run")]
    public async Task Blocked_SAT_holds_no_company_lock_or_pool_connection_while_import_and_matching_finish()
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        var release = new TaskCompletionSource<SatStatusResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Sat.Outcomes[seed.Uuid] = token => release.Task.WaitAsync(token);
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        await SatJobTests.EnqueueAsync(client, seed); var running = RunAsync(); await fixture.Sat.WaitForCallsAsync(seed.Uuid, 1);
        await MatchingRunScenario.RunAsync(fixture, client, seed.Company);
        using (var body = IngestionHttpTests.CfdiBody(MatchingFiscalScenario.Invoice(200)))
        using (var upload = await client.PostAsync($"/companies/{seed.Company}/imports/cfdi", body, TestContext.Current.CancellationToken)) await fixture.CompleteImportAsync(client, upload);
        Assert.False(running.IsCompleted);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        Assert.Equal(0, await owner.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM pg_stat_activity WHERE usename='cfdi_app' AND state='idle in transaction'", cancellationToken: TestContext.Current.CancellationToken)));
        release.SetResult(SatJobTests.Answer("current")); await running;
        Assert.Equal("current", await fixture.Database.ScalarAsync<string>("SELECT sat_status FROM cfdi WHERE id=@id", new { id = seed.Invoice }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Newer_cache_or_permanent_cancelled_answer_wins_over_an_inflight_reply(bool cancelled)
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        var release = new TaskCompletionSource<SatStatusResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Sat.Outcomes[seed.Uuid] = token => release.Task.WaitAsync(token);
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        await SatJobTests.EnqueueAsync(client, seed); var running = RunAsync(); await fixture.Sat.WaitForCallsAsync(seed.Uuid, 1);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        var observed = fixture.Clock.GetUtcNow().UtcDateTime;
        await fixture.App.Services.GetRequiredService<CompanyScope>().ExecuteWriteAsync(seed.Company, async (c, t, ct) =>
        {
            if (cancelled) await SatStatusChanges.ApplySatStatusChangeAsync(c, t, seed.Invoice, "cancelled", "cancelled without acceptance", fixture.Clock.GetUtcNow(), ct);
            else await c.ExecuteAsync(new CommandDefinition("UPDATE cfdi SET sat_status='current' WHERE id=@id", new { id = seed.Invoice }, t, cancellationToken: ct));
            await SatStatusStore.WriteAsync(c, t, seed.Company, new(seed.Uuid, cancelled ? "cancelled" : "current", "without acceptance",
                cancelled ? "cancelled without acceptance" : null, "200", observed, cancelled ? null : observed.AddHours(24), null, 1, "stub", SatContractTests.Success), ct);
            return true;
        }, TestContext.Current.CancellationToken);
        // The older request completes after the newer observation; completion time is not observation ordering.
        release.SetResult(SatJobTests.Answer(cancelled ? "current" : "cancelled")); await running;
        Assert.Equal(cancelled ? "cancelled" : "current", await fixture.Database.ScalarAsync<string>("SELECT sat_status FROM cfdi WHERE id=@id", new { id = seed.Invoice }));
        Assert.Equal(observed, await fixture.Database.ScalarAsync<DateTime>("SELECT checked_at FROM sat_status_cache WHERE company_id=@company", new { company = seed.Company }));
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM audit_entry WHERE company_id=@company AND action='sat.status_changed'", new { company = seed.Company }));
    }

    [Fact]
    public async Task Stale_lease_rolls_back_the_entire_apply_phase()
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        var release = new TaskCompletionSource<SatStatusResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Sat.Outcomes[seed.Uuid] = token => release.Task.WaitAsync(token);
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        var job = await SatJobTests.EnqueueAsync(client, seed); var running = RunAsync(); await fixture.Sat.WaitForCallsAsync(seed.Uuid, 1);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync(new CommandDefinition("UPDATE job SET locked_until=@expired WHERE id=@job", new { job, expired = fixture.Clock.GetUtcNow().UtcDateTime.AddSeconds(-1) }, cancellationToken: TestContext.Current.CancellationToken));
        release.SetResult(SatJobTests.Answer("cancelled")); await running;
        Assert.Equal("unknown", await fixture.Database.ScalarAsync<string>("SELECT sat_status FROM cfdi WHERE id=@id", new { id = seed.Invoice }));
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM sat_status_cache WHERE company_id=@company", new { company = seed.Company }));
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM audit_entry WHERE company_id=@company AND action IN ('sat.status_changed','cfdi.sat_status_changed')", new { company = seed.Company }));
    }

    [Fact]
    public async Task Job_budget_commits_completed_answers_and_follows_up_only_unchecked_remainder()
    {
        var (actor, company) = await fixture.SeedCompanyWithAdminAsync();
        var report = await fixture.App.Services.GetRequiredService<CfdiImporter>().ImportAsync(company, actor.Id,
            [UploadedFile.FromBytes("a.xml", MatchingFiscalScenario.Invoice(100)), UploadedFile.FromBytes("b.xml", MatchingFiscalScenario.Invoice(200)), UploadedFile.FromBytes("c.xml", MatchingFiscalScenario.Invoice(300))], TestContext.Current.CancellationToken);
        var ids = report.Files.Select(f => f.CfdiId!.Value).Order().ToArray();
        await using var owner = await fixture.Database.OpenOwnerAsync();
        var uuids = (await owner.QueryAsync<(Guid Id, Guid Uuid)>(new CommandDefinition("SELECT id,uuid FROM cfdi WHERE company_id=@company ORDER BY id", new { company }, cancellationToken: TestContext.Current.CancellationToken))).ToArray();
        fixture.Sat.Outcomes[uuids[1].Uuid] = async token => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return SatJobTests.Answer("current"); };
        var job = await fixture.App.Services.GetRequiredService<CompanyScope>().ExecuteWriteAsync(company, (c, t, ct) =>
            JobStore.EnqueueAsync(c, t, company, actor.Id, "status_check", JsonSerializer.Serialize(new { cfdiIds = ids }), null, 5, fixture.Clock.GetUtcNow(), ct), TestContext.Current.CancellationToken);
        var running = RunAsync(); await fixture.Sat.WaitForCallsAsync(uuids[1].Uuid, 1);
        fixture.Clock.Advance(TimeSpan.FromSeconds(61)); await running;
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM sat_status_cache WHERE company_id=@company", new { company }));
        Assert.Equal(0, fixture.Sat.CallsFor(uuids[2].Uuid));
        Assert.Equal("succeeded", await fixture.Database.ScalarAsync<string>("SELECT state FROM job WHERE id=@job", new { job }));
        var followUp = await fixture.Database.ScalarAsync<Guid>("SELECT id FROM job WHERE company_id=@company AND id<>@job", new { company, job });
        var remaining = JsonSerializer.Deserialize<JsonElement>((await owner.ExecuteScalarAsync<string>(new CommandDefinition("SELECT payload::text FROM job WHERE id=@job", new { job = followUp }, cancellationToken: TestContext.Current.CancellationToken)))!).GetProperty("cfdiIds").EnumerateArray().Select(v => v.GetGuid()).ToArray();
        Assert.Equal(ids.Skip(1), remaining);
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>("SELECT (payload->>'followUpGeneration')::integer FROM job WHERE id=@followUp", new { followUp }));
        Assert.Equal(2, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM job WHERE company_id=@company", new { company }));
        fixture.Sat.Outcomes.TryRemove(uuids[1].Uuid, out _); fixture.Clock.Advance(TimeSpan.FromSeconds(10)); await RunAsync();
        Assert.Equal("succeeded", await fixture.Database.ScalarAsync<string>("SELECT state FROM job WHERE id=@job", new { job = followUp }));
        Assert.Equal(1, fixture.Sat.CallsFor(uuids[0].Uuid));
    }

    [Fact]
    public async Task Mixed_failures_keep_original_payload_retry_and_skip_completed_cache_hits()
    {
        var (company, ids, uuids, job) = await BatchAsync(4);
        fixture.Sat.Outcomes[uuids[1]] = _ => throw new SatTransientException("Synthetic outage");
        fixture.Sat.Outcomes[uuids[2]] = async token => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return SatJobTests.Answer("current"); };
        var running = RunAsync(); await fixture.Sat.WaitForCallsAsync(uuids[1], 1);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2)); await fixture.Sat.WaitForCallsAsync(uuids[1], 2);
        fixture.Clock.Advance(TimeSpan.FromSeconds(3)); await fixture.Sat.WaitForCallsAsync(uuids[2], 1);
        fixture.Clock.Advance(TimeSpan.FromSeconds(56)); await running;
        Assert.Equal("queued", await fixture.Database.ScalarAsync<string>("SELECT state FROM job WHERE id=@job", new { job }));
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM job WHERE company_id=@company", new { company }));
        var payload = await fixture.Database.ScalarAsync<string>("SELECT payload::text FROM job WHERE id=@job", new { job });
        Assert.Equal(ids, JsonSerializer.Deserialize<JsonElement>(payload!).GetProperty("cfdiIds").EnumerateArray().Select(v => v.GetGuid()));
        fixture.Sat.Outcomes.TryRemove(uuids[1], out _); fixture.Sat.Outcomes.TryRemove(uuids[2], out _);
        fixture.Clock.Advance(TimeSpan.FromSeconds(10)); await RunAsync();
        Assert.Equal("succeeded", await fixture.Database.ScalarAsync<string>("SELECT state FROM job WHERE id=@job", new { job }));
        Assert.Equal(1, fixture.Sat.CallsFor(uuids[0])); Assert.Equal(4, fixture.Sat.CallsFor(uuids[1]));
    }

    [Fact]
    public async Task Follow_up_generation_cap_uses_bounded_job_attempts()
    {
        var (company, ids, uuids, job) = await BatchAsync(2, 20);
        fixture.Sat.Outcomes[uuids[0]] = async token => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return SatJobTests.Answer("current"); };
        var running = RunAsync(); await fixture.Sat.WaitForCallsAsync(uuids[0], 1); fixture.Clock.Advance(TimeSpan.FromSeconds(61)); await running;
        Assert.Equal("queued", await fixture.Database.ScalarAsync<string>("SELECT state FROM job WHERE id=@job", new { job }));
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM job WHERE company_id=@company", new { company }));
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM sat_status_cache WHERE company_id=@company", new { company }));
        Assert.Equal(0, fixture.Sat.CallsFor(uuids[1]));
        Assert.Equal(2, ids.Length);
    }

    private async Task<(Guid Company, Guid[] Ids, Guid[] Uuids, Guid Job)> BatchAsync(int count, int generation = 0)
    {
        var (actor, company) = await fixture.SeedCompanyWithAdminAsync();
        await fixture.App.Services.GetRequiredService<CfdiImporter>().ImportAsync(company, actor.Id,
            Enumerable.Range(1, count).Select(i => UploadedFile.FromBytes($"synthetic-{i}.xml", MatchingFiscalScenario.Invoice(i * 100))).ToArray(), TestContext.Current.CancellationToken);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        var rows = (await owner.QueryAsync<(Guid Id, Guid Uuid)>(new CommandDefinition("SELECT id,uuid FROM cfdi WHERE company_id=@company ORDER BY id", new { company }, cancellationToken: TestContext.Current.CancellationToken))).ToArray();
        var ids = rows.Select(row => row.Id).ToArray();
        var job = await fixture.App.Services.GetRequiredService<CompanyScope>().ExecuteWriteAsync(company, (c, t, ct) =>
            JobStore.EnqueueAsync(c, t, company, actor.Id, "status_check", JsonSerializer.Serialize(new { cfdiIds = ids, followUpGeneration = generation }), null, 5, fixture.Clock.GetUtcNow(), ct), TestContext.Current.CancellationToken);
        return (company, ids, rows.Select(row => row.Uuid).ToArray(), job);
    }

    private Task<bool> RunAsync() => fixture.App.Services.GetRequiredService<JobDispatcher>().RunOneAsync("synthetic-phased-worker", TestContext.Current.CancellationToken);
}
