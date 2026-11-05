using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Reconciliation.Core.Jobs;
using Reconciliation.Core.Tenancy;

namespace Reconciliation.Tests;

public sealed class ReconciliationJobsTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact(DisplayName = "Run requested twice")]
    public async Task Concurrent_requests_and_a_running_request_return_the_single_active_run()
    {
        var seeded = await fixture.SeedCompanyWithAdminAsync();
        using var client = await fixture.LoginAsync(seeded.User.Email, seeded.CompanyId);
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.PostAsync($"/companies/{seeded.CompanyId}/reconciliation-runs", null, TestContext.Current.CancellationToken)));
        Guid jobId;
        try
        {
            Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Accepted));
            Assert.Equal(7, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
            var receipts = await Task.WhenAll(responses.Select(response => response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)));
            jobId = Assert.Single(receipts.Select(receipt => receipt.GetProperty("jobId").GetGuid()).Distinct());
        }
        finally { foreach (var response in responses) response.Dispose(); }
        var store = fixture.App.Services.GetRequiredService<JobStore>();
        var claimed = (await store.ClaimAsync("synthetic-run", 60, TestContext.Current.CancellationToken))!;
        Assert.Equal(jobId, claimed.Id);
        using var repeated = await client.PostAsync($"/companies/{seeded.CompanyId}/reconciliation-runs", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        var running = await repeated.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(jobId, running.GetProperty("jobId").GetGuid());
        Assert.Equal("running", running.GetProperty("state").GetString());
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM job WHERE company_id=@CompanyId AND type='reconciliation_run'", new { seeded.CompanyId }));
        await using var owner = await fixture.Database.OpenOwnerAsync();
        var duplicate = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => owner.ExecuteAsync(new CommandDefinition("INSERT INTO job(company_id,type,created_by) VALUES(@CompanyId,'reconciliation_run',@Id)", new { seeded.CompanyId, seeded.User.Id }, cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Equal(Npgsql.PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        Assert.True(await store.FinishAsync(claimed, "synthetic-run", null, null, TimeSpan.Zero, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Four_types_are_registered_and_only_status_check_is_still_a_placeholder()
    {
        Assert.Equal(["import_cfdi", "import_statement", "reconciliation_run", "status_check"], fixture.App.Services.GetServices<IJobHandler>().Select(handler => handler.Type).Order());
        var seeded = await fixture.SeedCompanyWithAdminAsync();
        using var client = await fixture.LoginAsync(seeded.User.Email, seeded.CompanyId);
        foreach (var (jobType, group) in new[] { ("reconciliation_run", 6), ("status_check", 7) })
        {
            var jobId = await fixture.App.Services.GetRequiredService<CompanyScope>().ExecuteWriteAsync(seeded.CompanyId, (connection, transaction, cancellationToken) =>
                JobStore.EnqueueAsync(connection, transaction, seeded.CompanyId, seeded.User.Id, jobType, "{}", null, 5, fixture.Clock.GetUtcNow(), cancellationToken), TestContext.Current.CancellationToken);
            Assert.True(await fixture.App.Services.GetRequiredService<JobDispatcher>().RunOneAsync("synthetic-placeholder", TestContext.Current.CancellationToken));
            var job = await client.GetFromJsonAsync<JsonElement>($"/companies/{seeded.CompanyId}/jobs/{jobId}", TestContext.Current.CancellationToken);
            Assert.Equal("succeeded", job.GetProperty("state").GetString());
            if (jobType == "status_check") Assert.Contains($"not implemented until group {group}", job.GetProperty("payload").GetString()!, StringComparison.Ordinal);
            else Assert.DoesNotContain("not implemented", job.GetProperty("payload").GetString()!, StringComparison.Ordinal);
            Assert.Equal(JsonValueKind.Null, job.GetProperty("lastError").ValueKind);
        }
    }

    [Fact]
    public async Task Viewer_cannot_start_accountant_can_and_archive_refuses_new_runs()
    {
        var user = await fixture.SeedUserAsync();
        var companyId = await fixture.SeedCompanyAsync(user.Id, "viewer");
        using var client = await fixture.LoginAsync(user.Email, companyId);
        using var denied = await client.PostAsync($"/companies/{companyId}/reconciliation-runs", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync(new CommandDefinition("UPDATE membership SET role='accountant' WHERE company_id=@companyId", new { companyId }, cancellationToken: TestContext.Current.CancellationToken));
        using var started = await client.PostAsync($"/companies/{companyId}/reconciliation-runs", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        Assert.True(await fixture.App.Services.GetRequiredService<JobDispatcher>().RunOneAsync("synthetic-role-run", TestContext.Current.CancellationToken));
        await owner.ExecuteAsync(new CommandDefinition("UPDATE company SET archived_at=now() WHERE id=@companyId", new { companyId }, cancellationToken: TestContext.Current.CancellationToken));
        using var archived = await client.PostAsync($"/companies/{companyId}/reconciliation-runs", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, archived.StatusCode);
    }
}
