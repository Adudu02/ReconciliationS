using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Reconciliation.Core.Jobs;
using Reconciliation.Core.Sat;
using Reconciliation.Core.Tenancy;

namespace Reconciliation.Tests;

[CollectionDefinition("sat-api")]
public sealed class SatApiTestGroup : ICollectionFixture<ApiFixture>;

[Collection("sat-api")]
public sealed class SatJobTests(ApiFixture fixture)
{
    [Theory(DisplayName = "Current invoice / Cancelled invoice / Not found / Invalid expression / Flagged issuer / Cancellation in process / Deadline expired")]
    [InlineData("current", "current", null, "200")]
    [InlineData("cancelled", "cancelled", "cancelled without acceptance", "200")]
    [InlineData("not_found", "not_found", null, "200")]
    [InlineData("invalid", "current", null, null)]
    [InlineData("efos", "current", null, "100")]
    [InlineData("in_process", "current", "in process", "200")]
    [InlineData("expired", "current", "deadline expired", "200")]
    [InlineData("unknown", "unknown", "unknown", "future")]
    public async Task On_demand_job_stores_contract_metadata_and_cascades_only_cancelled(string outcome, string expected, string? cancellation, string? efos)
    {
        await ResetAsync(); var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        await InvoiceReviewReasonTests.InsertManualLinkAsync(fixture, seed, 100, "reconciled");
        if (outcome == "invalid")
        {
            await using var owner = await fixture.Database.OpenOwnerAsync();
            await owner.ExecuteAsync(new CommandDefinition("UPDATE cfdi SET sat_status='current' WHERE id=@id", new { id = seed.Invoice }, cancellationToken: TestContext.Current.CancellationToken));
        }
        fixture.Sat.Outcomes[seed.Uuid] = _ => Task.FromResult(Answer(outcome));
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        var jobId = await EnqueueAsync(client, seed);
        await RunAsync(); var job = await JobAsync(client, seed.Company, jobId);
        Assert.Equal("succeeded", job.GetProperty("state").GetString());
        Assert.Equal(1, fixture.Sat.CallsFor(seed.Uuid));
        var document = await client.GetFromJsonAsync<JsonElement>($"/companies/{seed.Company}/cfdi/{seed.Invoice}", TestContext.Current.CancellationToken);
        var status = document.GetProperty("sat");
        Assert.Equal(expected, status.GetProperty("state").GetString());
        Assert.Equal(cancellation, status.GetProperty("cancellationStatus").GetString());
        Assert.Equal(efos, status.GetProperty("efosRaw").GetString());
        Assert.NotEqual(JsonValueKind.Null, status.GetProperty("checkedAt").ValueKind);
        Assert.Equal("stub", status.GetProperty("source").GetString());
        if (outcome == "invalid")
        {
            Assert.Contains("601", status.GetProperty("lastError").GetString()!, StringComparison.Ordinal);
            Assert.Equal("unknown", await fixture.Database.ScalarAsync<string>("SELECT state FROM sat_status_cache WHERE company_id=@company", new { company = seed.Company }));
        }
        else
        {
            Assert.Equal("without acceptance", status.GetProperty("cancellability").GetString());
            Assert.Equal(outcome == "efos" || outcome == "unknown", status.GetProperty("efosFlagged").GetBoolean());
        }
        Assert.Equal(outcome == "cancelled" ? "suggested" : "reconciled", await fixture.Database.ScalarAsync<string>("SELECT state FROM link WHERE company_id=@company", new { company = seed.Company }));
        if (outcome == "cancelled")
        {
            Assert.Equal("cancelled invoice", await fixture.Database.ScalarAsync<string>("SELECT review_reason FROM link WHERE company_id=@company", new { company = seed.Company }));
            Assert.Equal(JsonValueKind.Null, status.GetProperty("expiresAt").ValueKind);
            Assert.Equal(1, await CountAuditAsync(seed.Company, "link.review_required"));
        }
        if (outcome == "efos")
            Assert.Contains(document.GetProperty("matching").GetProperty("reasons").EnumerateArray(), r => r.GetProperty("code").GetString() == "efos warning" && r.GetProperty("requiresReview").GetBoolean());
    }

    [Fact(DisplayName = "Cached current status / Cancelled status cached")]
    public async Task TTL_refresh_of_same_answer_has_no_audit_noise_and_cancelled_cache_never_calls_again()
    {
        await ResetAsync(); var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        await EnqueueAsync(client, seed); await RunAsync();
        var before = await CountAuditAsync(seed.Company, "sat.status_changed");
        await EnqueueAsync(client, seed); await RunAsync(); Assert.Equal(1, fixture.Sat.CallsFor(seed.Uuid));
        fixture.Clock.Advance(TimeSpan.FromHours(24));
        using var fresh = await fixture.LoginAsync(seed.Email, seed.Company);
        await EnqueueAsync(fresh, seed); await RunAsync(); Assert.Equal(2, fixture.Sat.CallsFor(seed.Uuid)); Assert.Equal(before, await CountAuditAsync(seed.Company, "sat.status_changed"));
        fixture.Clock.Advance(TimeSpan.FromHours(24));
        fixture.Sat.Outcomes[seed.Uuid] = _ => Task.FromResult(Answer("cancelled"));
        using var next = await fixture.LoginAsync(seed.Email, seed.Company); await EnqueueAsync(next, seed); await RunAsync();
        Assert.Equal(3, fixture.Sat.CallsFor(seed.Uuid));
        fixture.Clock.Advance(TimeSpan.FromDays(365));
        using var later = await fixture.LoginAsync(seed.Email, seed.Company); await EnqueueAsync(later, seed); await RunAsync();
        Assert.Equal(3, fixture.Sat.CallsFor(seed.Uuid));
    }

    [Fact(DisplayName = "Unknown status rechecked")]
    public async Task Transient_exhaustion_commits_unknown_error_attempts_before_job_backoff_then_recovers_once()
    {
        await ResetAsync(); var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        fixture.Sat.Outcomes[seed.Uuid] = _ => throw new SatTransientException("Synthetic unavailable");
        using var client = await fixture.LoginAsync(seed.Email, seed.Company); var jobId = await EnqueueAsync(client, seed);
        var run = RunAsync(); await fixture.Sat.WaitForCallsAsync(seed.Uuid, 1);
        await fixture.Clock.WaitForTimerAsync(TimeSpan.FromSeconds(2)); fixture.Clock.Advance(TimeSpan.FromSeconds(2)); await fixture.Sat.WaitForCallsAsync(seed.Uuid, 2);
        await fixture.Clock.WaitForTimerAsync(TimeSpan.FromSeconds(3)); fixture.Clock.Advance(TimeSpan.FromSeconds(3)); await run;
        var failed = await JobAsync(client, seed.Company, jobId); Assert.Equal("queued", failed.GetProperty("state").GetString());
        Assert.NotNull(failed.GetProperty("lastError").GetString());
        Assert.Equal(3, await fixture.Database.ScalarAsync<int>("SELECT attempts FROM sat_status_cache WHERE company_id=@company", new { company = seed.Company }));
        Assert.Equal("unknown", await fixture.Database.ScalarAsync<string>("SELECT sat_status FROM cfdi WHERE id=@id", new { id = seed.Invoice }));
        Assert.NotNull(await fixture.Database.ScalarAsync<string>("SELECT last_error FROM sat_status_cache WHERE company_id=@company", new { company = seed.Company }));
        fixture.Sat.Outcomes[seed.Uuid] = _ => Task.FromResult(Answer("current")); fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await RunAsync(); Assert.Equal("succeeded", (await JobAsync(client, seed.Company, jobId)).GetProperty("state").GetString());
        Assert.Equal("current", await fixture.Database.ScalarAsync<string>("SELECT sat_status FROM cfdi WHERE id=@id", new { id = seed.Invoice }));
        Assert.Equal(1, await CountAuditAsync(seed.Company, "sat.status_changed"));
    }

    [Fact]
    public async Task Repeated_EFOS_value_warns_and_audits_only_once_per_change_and_empty_does_not_warn()
    {
        await ResetAsync(); var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        fixture.Sat.Outcomes[seed.Uuid] = _ => Task.FromResult(Answer("efos")); await EnqueueAsync(client, seed); await RunAsync();
        fixture.Clock.Advance(TimeSpan.FromHours(24)); using var fresh = await fixture.LoginAsync(seed.Email, seed.Company);
        await EnqueueAsync(fresh, seed); await RunAsync(); Assert.Equal(1, await CountAuditAsync(seed.Company, "sat.efos_flagged"));
        fixture.Clock.Advance(TimeSpan.FromHours(24)); fixture.Sat.Outcomes[seed.Uuid] = _ => Task.FromResult(SatContract.Parse(SatContractTests.Envelope(efos: "")));
        using var later = await fixture.LoginAsync(seed.Email, seed.Company); await EnqueueAsync(later, seed); await RunAsync();
        var value = await later.GetFromJsonAsync<JsonElement>($"/companies/{seed.Company}/cfdi/{seed.Invoice}", TestContext.Current.CancellationToken);
        Assert.False(value.GetProperty("sat").GetProperty("efosFlagged").GetBoolean());
        Assert.DoesNotContain(value.GetProperty("matching").GetProperty("reasons").EnumerateArray(), r => r.GetProperty("code").GetString() == "efos warning");
    }

    [Theory]
    [InlineData("viewer", HttpStatusCode.Forbidden)]
    [InlineData("accountant", HttpStatusCode.Accepted)]
    [InlineData("administrator", HttpStatusCode.Accepted)]
    public async Task Roles_and_UUID_company_lookup_are_guarded(string role, HttpStatusCode expected)
    {
        await ResetAsync(); var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync(new CommandDefinition("UPDATE membership SET role=@role::membership_role WHERE company_id=@company", new { role, company = seed.Company }, cancellationToken: TestContext.Current.CancellationToken));
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        using var response = await client.PostAsync($"/companies/{seed.Company}/cfdi/{seed.Uuid}/sat-check", null, TestContext.Current.CancellationToken); Assert.Equal(expected, response.StatusCode);
        if (role != "viewer")
        {
            using var unknown = await client.PostAsync($"/companies/{seed.Company}/cfdi/{Guid.NewGuid()}/sat-check", null, TestContext.Current.CancellationToken); Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            await owner.ExecuteAsync(new CommandDefinition("UPDATE company SET archived_at=now() WHERE id=@company", new { company = seed.Company }, cancellationToken: TestContext.Current.CancellationToken));
            using var archived = await client.PostAsync($"/companies/{seed.Company}/cfdi/{seed.Uuid}/sat-check", null, TestContext.Current.CancellationToken); Assert.Equal(HttpStatusCode.Conflict, archived.StatusCode);
        }
    }

    [Fact]
    public async Task Import_enqueues_checks_in_document_transaction_and_duplicate_enqueues_nothing()
    {
        await ResetAsync(); var (actor, company) = await fixture.SeedCompanyWithAdminAsync();
        using var client = await fixture.LoginAsync(actor.Email, company); var bytes = MatchingFiscalScenario.Invoice(100);
        using (var body = IngestionHttpTests.CfdiBody(bytes))
        using (var uploaded = await client.PostAsync($"/companies/{company}/imports/cfdi", body, TestContext.Current.CancellationToken)) await fixture.CompleteImportAsync(client, uploaded);
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM job WHERE company_id=@company AND type='status_check'", new { company }));
        Assert.Equal(0, fixture.Sat.TotalCalls);
        await RunAsync(); Assert.Equal(1, fixture.Sat.TotalCalls);
        using (var body = IngestionHttpTests.CfdiBody(bytes))
        using (var uploaded = await client.PostAsync($"/companies/{company}/imports/cfdi", body, TestContext.Current.CancellationToken)) await fixture.CompleteImportAsync(client, uploaded);
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM job WHERE company_id=@company AND type='status_check'", new { company }));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"cfdiIds\":[]}")]
    [InlineData("{\"cfdiIds\":[\"00000000-0000-0000-0000-000000000000\"]}")]
    public async Task Invalid_job_payload_fails_without_HTTP(string payload)
    {
        await ResetAsync(); var (actor, company) = await fixture.SeedCompanyWithAdminAsync();
        var job = await fixture.App.Services.GetRequiredService<CompanyScope>().ExecuteWriteAsync(company, (c, t, ct) =>
            JobStore.EnqueueAsync(c, t, company, actor.Id, "status_check", payload, null, 3, fixture.Clock.GetUtcNow(), ct), TestContext.Current.CancellationToken);
        await RunAsync(); Assert.Equal("failed", await fixture.Database.ScalarAsync<string>("SELECT state FROM job WHERE id=@job", new { job })); Assert.Equal(0, fixture.Sat.TotalCalls);
    }

    [Fact]
    public async Task Oversized_batch_is_rejected_before_any_service_call()
    {
        await ResetAsync(); var (actor, company) = await fixture.SeedCompanyWithAdminAsync();
        var job = await fixture.App.Services.GetRequiredService<CompanyScope>().ExecuteWriteAsync(company, (c, t, ct) =>
            JobStore.EnqueueAsync(c, t, company, actor.Id, "status_check", JsonSerializer.Serialize(new { cfdiIds = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray() }), null, 3, fixture.Clock.GetUtcNow(), ct), TestContext.Current.CancellationToken);
        await RunAsync(); Assert.Equal("failed", await fixture.Database.ScalarAsync<string>("SELECT state FROM job WHERE id=@job", new { job })); Assert.Equal(0, fixture.Sat.TotalCalls);
    }

    [Fact]
    public async Task Invalid_expression_preserves_an_existing_cache_state_and_only_an_explicit_request_rechecks()
    {
        await ResetAsync(); var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        using var client = await fixture.LoginAsync(seed.Email, seed.Company); await EnqueueAsync(client, seed); await RunAsync();
        fixture.Clock.Advance(TimeSpan.FromHours(24)); fixture.Sat.Outcomes[seed.Uuid] = _ => Task.FromResult(Answer("invalid"));
        using var fresh = await fixture.LoginAsync(seed.Email, seed.Company); var job = await EnqueueAsync(fresh, seed); await RunAsync();
        Assert.Equal("succeeded", (await JobAsync(fresh, seed.Company, job)).GetProperty("state").GetString());
        Assert.Equal("current", await fixture.Database.ScalarAsync<string>("SELECT state FROM sat_status_cache WHERE company_id=@company", new { company = seed.Company }));
        Assert.Equal(2, fixture.Sat.CallsFor(seed.Uuid)); Assert.False(await RunAsync());
        await EnqueueAsync(fresh, seed); await RunAsync(); Assert.Equal(3, fixture.Sat.CallsFor(seed.Uuid));
    }

    internal static SatStatusResponse Answer(string kind) => kind switch
    {
        "cancelled" => SatContract.Parse(SatContractTests.Envelope(state: "Cancelado", cancellation: "Cancelado sin aceptación")),
        "not_found" => SatContract.Parse(SatContractTests.Envelope(SatContractTests.Missing, "No Encontrado")),
        "invalid" => SatContract.Parse(SatContractTests.Envelope(SatContractTests.Invalid)),
        "efos" => SatContract.Parse(SatContractTests.Envelope(efos: "100")),
        "in_process" => SatContract.Parse(SatContractTests.Envelope(cancellation: "En proceso")),
        "expired" => SatContract.Parse(SatContractTests.Envelope(cancellation: "Plazo vencido")),
        "unknown" => SatContract.Parse(SatContractTests.Envelope(state: "future", cancellation: "future", efos: "future")),
        _ => SatContract.Parse(SatContractTests.Envelope())
    };
    private async Task ResetAsync()
    {
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync(new CommandDefinition("TRUNCATE job_attempt,job", cancellationToken: TestContext.Current.CancellationToken)); fixture.Sat.Reset(); fixture.Sat.DefaultCurrent = true;
    }
    internal static async Task<Guid> EnqueueAsync(HttpClient client, MatchingFiscalScenario.Seed seed)
    {
        using var response = await client.PostAsync($"/companies/{seed.Company}/cfdi/{seed.Uuid}/sat-check", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("jobId").GetGuid();
    }
    private Task<bool> RunAsync() => fixture.App.Services.GetRequiredService<JobDispatcher>().RunOneAsync("synthetic-sat-worker", TestContext.Current.CancellationToken);
    private static Task<JsonElement> JobAsync(HttpClient client, Guid company, Guid job) => client.GetFromJsonAsync<JsonElement>($"/companies/{company}/jobs/{job}", TestContext.Current.CancellationToken);
    private Task<int> CountAuditAsync(Guid company, string action) => fixture.Database.ScalarAsync<int>("SELECT count(*) FROM audit_entry WHERE company_id=@company AND action=@action", new { company, action });
}

public sealed class SatTestClient : ISatClient
{
    private readonly ConcurrentDictionary<Guid, int> calls = new();
    public ConcurrentDictionary<Guid, Func<CancellationToken, Task<SatStatusResponse>>> Outcomes { get; } = new();
    public bool DefaultCurrent { get; set; }
    public int TotalCalls => calls.Values.Sum();
    public int CallsFor(Guid uuid) => calls.GetValueOrDefault(uuid);
    public Task<SatStatusResponse> CheckAsync(SatStatusRequest request, CancellationToken cancellationToken)
    {
        calls.AddOrUpdate(request.Uuid, 1, (_, count) => count + 1);
        if (Outcomes.TryGetValue(request.Uuid, out var response)) return response(cancellationToken);
        return DefaultCurrent ? Task.FromResult(SatJobTests.Answer("current")) : throw new SatPermanentException("Synthetic offline SAT fixture.");
    }
    public async Task WaitForCallsAsync(Guid uuid, int count)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (CallsFor(uuid) < count) { timeout.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
        for (var i = 0; i < 20; i++) await Task.Yield();
    }
    public void Reset() { calls.Clear(); Outcomes.Clear(); }
}

internal sealed class ImmediateSatRateGate : IRequestRateGate
{
    public Task WaitForPermitAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
}
