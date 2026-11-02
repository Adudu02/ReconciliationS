using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Reconciliation.Core.Jobs;
using Reconciliation.Core.Tenancy;

namespace Reconciliation.Tests;

public sealed class JobQueueTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private readonly ManualTimeProvider clock = new();

    [Fact]
    public async Task Enqueue_shares_callers_transaction_and_rollback_leaves_no_job()
    {
        var seeded = await SeedAsync();
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var scope = new CompanyScope(source, NullLogger<CompanyScope>.Instance);
        await Assert.ThrowsAsync<InvalidDataException>(() => scope.ExecuteWriteAsync<int>(seeded.CompanyId, async (connection, transaction, cancellationToken) =>
        {
            await JobStore.EnqueueAsync(connection, transaction, seeded.CompanyId, seeded.UserId, "status_check", "{}", null, 5, clock.GetUtcNow(), cancellationToken);
            throw new InvalidDataException("Synthetic rollback");
        }, TestContext.Current.CancellationToken));
        Assert.Equal(0, await database.ScalarAsync<int>("SELECT count(*) FROM job WHERE company_id=@CompanyId", new { seeded.CompanyId }));
    }

    [Fact(DisplayName = "Two workers")]
    public async Task Concurrent_workers_claim_each_job_once_and_company_scope_restricts_each_handler()
    {
        var seeded = await SeedAsync();
        var otherCompany = await database.ScalarAsync<Guid>("INSERT INTO company(rfc,name) VALUES('BBB010101BBB','Synthetic second') RETURNING id");
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var scope = new CompanyScope(source, NullLogger<CompanyScope>.Instance);
        var queue = new JobStore(source, clock);
        var expected = new List<Guid>();
        foreach (var companyId in new[] { seeded.CompanyId, otherCompany })
            for (var index = 0; index < 12; index++)
                expected.Add(await scope.ExecuteWriteAsync(companyId, (connection, transaction, cancellationToken) =>
                    JobStore.EnqueueAsync(connection, transaction, companyId, seeded.UserId, "status_check", "{}", null, 5, clock.GetUtcNow(), cancellationToken), TestContext.Current.CancellationToken));
        var claimed = await Task.WhenAll(Enumerable.Range(0, 24).Select(async worker =>
        {
            var job = await queue.ClaimAsync($"synthetic-worker-{worker}", 60, TestContext.Current.CancellationToken);
            Assert.NotNull(job);
            var visible = await scope.ExecuteAsync(job.CompanyId, (connection, transaction, cancellationToken) =>
                connection.QueryAsync<Guid>(new CommandDefinition("SELECT id FROM company", transaction: transaction, cancellationToken: cancellationToken)), TestContext.Current.CancellationToken);
            Assert.Equal([job.CompanyId], visible);
            return job.Id;
        }));
        Assert.Equal(expected.Order(), claimed.Order());
        Assert.Equal(24, claimed.Distinct().Count());
        Assert.Null(await queue.ClaimAsync("synthetic-empty", 60, TestContext.Current.CancellationToken));
        Assert.Equal(24, await database.ScalarAsync<int>("SELECT count(*) FROM job_attempt WHERE attempt_no=1 AND outcome='running'"));
    }

    [Fact]
    public async Task Due_jobs_follow_run_after_then_created_at_and_future_jobs_wait()
    {
        var seeded = await SeedAsync();
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var scope = new CompanyScope(source, NullLogger<CompanyScope>.Instance);
        var now = clock.GetUtcNow();
        var first = await scope.ExecuteWriteAsync(seeded.CompanyId, (connection, transaction, cancellationToken) =>
            JobStore.EnqueueAsync(connection, transaction, seeded.CompanyId, seeded.UserId, "status_check", "{}", null, 5, now, cancellationToken), TestContext.Current.CancellationToken);
        var second = await scope.ExecuteWriteAsync(seeded.CompanyId, (connection, transaction, cancellationToken) =>
            JobStore.EnqueueAsync(connection, transaction, seeded.CompanyId, seeded.UserId, "status_check", "{}", null, 5, now, cancellationToken), TestContext.Current.CancellationToken);
        await using var owner = await database.OpenOwnerAsync();
        await owner.ExecuteAsync(new CommandDefinition("UPDATE job SET created_at=@created WHERE id=@first; UPDATE job SET run_after=@future WHERE id=@second", new { first, second, created = now.AddSeconds(-1), future = now.AddMinutes(1) }, cancellationToken: TestContext.Current.CancellationToken));
        var queue = new JobStore(source, clock);
        Assert.Equal(first, (await queue.ClaimAsync("synthetic-first", 60, TestContext.Current.CancellationToken))!.Id);
        Assert.Null(await queue.ClaimAsync("synthetic-not-due", 60, TestContext.Current.CancellationToken));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(second, (await queue.ClaimAsync("synthetic-due", 60, TestContext.Current.CancellationToken))!.Id);
    }

    [Fact(DisplayName = "Transient failure then success")]
    public async Task Transient_failure_backs_off_then_records_both_attempts()
    {
        var seeded = await SeedAsync();
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var scope = new CompanyScope(source, NullLogger<CompanyScope>.Instance);
        var jobId = await scope.ExecuteWriteAsync(seeded.CompanyId, (connection, transaction, cancellationToken) =>
            JobStore.EnqueueAsync(connection, transaction, seeded.CompanyId, seeded.UserId, "status_check", "{}", null, 3, clock.GetUtcNow(), cancellationToken), TestContext.Current.CancellationToken);
        var store = new JobStore(source, clock);
        var options = new JobOptions { BackoffBaseSeconds = 2, BackoffCapSeconds = 5 };
        var backoff = new JobBackoff(options, _ => 0);
        Assert.Equal(TimeSpan.FromSeconds(2), backoff.Delay(1));
        Assert.Equal(TimeSpan.FromSeconds(4), backoff.Delay(2));
        Assert.Equal(TimeSpan.FromSeconds(5), backoff.Delay(10));
        var first = (await store.ClaimAsync("synthetic-first", 60, TestContext.Current.CancellationToken))!;
        Assert.True(await store.FinishAsync(first, "synthetic-first", "transient", "Synthetic timeout", backoff.Delay(first.Attempts), TestContext.Current.CancellationToken));
        Assert.Null(await store.ClaimAsync("synthetic-too-early", 60, TestContext.Current.CancellationToken));
        clock.Advance(TimeSpan.FromSeconds(2));
        var second = (await store.ClaimAsync("synthetic-second", 60, TestContext.Current.CancellationToken))!;
        Assert.Equal(jobId, second.Id);
        Assert.Equal(2, second.Attempts);
        Assert.True(await store.FinishAsync(second, "synthetic-second", null, null, TimeSpan.Zero, TestContext.Current.CancellationToken));
        Assert.Equal("succeeded", await database.ScalarAsync<string>("SELECT state FROM job WHERE id=@jobId", new { jobId }));
        await using var owner = await database.OpenOwnerAsync();
        Assert.Equal(["failed", "succeeded"], await owner.QueryAsync<string>(new CommandDefinition("SELECT outcome FROM job_attempt WHERE job_id=@jobId ORDER BY attempt_no", new { jobId }, cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("permanent", 5)]
    [InlineData("transient", 1)]
    public async Task Permanent_or_exhausted_failure_retains_input_and_reason_without_retry(string errorKind, int maxAttempts)
    {
        var seeded = await SeedAsync();
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var scope = new CompanyScope(source, NullLogger<CompanyScope>.Instance);
        var jobId = await scope.ExecuteWriteAsync(seeded.CompanyId, (connection, transaction, cancellationToken) =>
            JobStore.EnqueueAsync(connection, transaction, seeded.CompanyId, seeded.UserId, "status_check", "{\"synthetic\":true}", null, maxAttempts, clock.GetUtcNow(), cancellationToken), TestContext.Current.CancellationToken);
        var store = new JobStore(source, clock);
        var claimed = (await store.ClaimAsync("synthetic-failure", 60, TestContext.Current.CancellationToken))!;
        Assert.True(await store.FinishAsync(claimed, "synthetic-failure", errorKind, "Synthetic invalid input", TimeSpan.Zero, TestContext.Current.CancellationToken));
        Assert.Null(await store.ClaimAsync("synthetic-no-retry", 60, TestContext.Current.CancellationToken));
        Assert.Equal("failed", await database.ScalarAsync<string>("SELECT state FROM job WHERE id=@jobId", new { jobId }));
        Assert.Equal("Synthetic invalid input", await database.ScalarAsync<string>("SELECT last_error FROM job WHERE id=@jobId", new { jobId }));
        Assert.True(await database.ScalarAsync<bool>("SELECT payload->>'synthetic'='true' FROM job WHERE id=@jobId", new { jobId }));
    }

    private async Task<(Guid CompanyId, Guid UserId)> SeedAsync()
    {
        await using var owner = await database.OpenOwnerAsync();
        await owner.ExecuteAsync(new CommandDefinition("TRUNCATE job_attempt,job", cancellationToken: TestContext.Current.CancellationToken));
        var companyId = await owner.QuerySingleAsync<Guid>(new CommandDefinition("INSERT INTO company(rfc,name) VALUES('AAA010101AAA','Synthetic queue') RETURNING id", cancellationToken: TestContext.Current.CancellationToken));
        var userId = await owner.QuerySingleAsync<Guid>(new CommandDefinition("INSERT INTO app_user(email,password_hash) VALUES(@email,'synthetic-not-a-login-hash') RETURNING id", new { email = $"synthetic-{Guid.NewGuid():N}@example.invalid" }, cancellationToken: TestContext.Current.CancellationToken));
        return (companyId, userId);
    }
}
