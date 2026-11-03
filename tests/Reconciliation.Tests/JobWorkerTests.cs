extern alias worker;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Reconciliation.Core.Common;
using Reconciliation.Core.Ingestion;
using Reconciliation.Core.Jobs;
using Reconciliation.Core.Tenancy;
using WorkerLoop = worker::Reconciliation.Worker.JobLoop;

namespace Reconciliation.Tests;

public sealed class JobWorkerTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private readonly ManualTimeProvider clock = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispatcher_classifies_real_handler_errors_and_preserves_transactional_attempts(bool permanent)
    {
        var handler = new SyntheticHandler(async (job, connection, transaction, cancellationToken) =>
        {
            Assert.Equal([job.CompanyId], await connection.QueryAsync<Guid>(new CommandDefinition("SELECT id FROM company", transaction: transaction, cancellationToken: cancellationToken)));
            if (permanent) throw new InvalidDataException("Synthetic malformed input");
            if (job.Attempts == 1) throw new TimeoutException("Synthetic timeout");
            await connection.ExecuteAsync(new CommandDefinition("UPDATE company SET name='Synthetic completed'", transaction: transaction, cancellationToken: cancellationToken));
            return null;
        });
        await using var services = CreateServices(handler);
        var jobId = await EnqueueAsync(services);
        var dispatcher = services.GetRequiredService<JobDispatcher>();
        Assert.True(await dispatcher.RunOneAsync("synthetic-dispatch", TestContext.Current.CancellationToken));
        Assert.Equal(permanent ? "failed" : "queued", await database.ScalarAsync<string>("SELECT state FROM job WHERE id=@jobId", new { jobId }));
        if (!permanent)
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.True(await dispatcher.RunOneAsync("synthetic-dispatch", TestContext.Current.CancellationToken));
            Assert.Equal("succeeded", await database.ScalarAsync<string>("SELECT state FROM job WHERE id=@jobId", new { jobId }));
        }
        Assert.Equal(permanent ? "permanent" : "transient", await database.ScalarAsync<string>("SELECT error_kind FROM job_attempt WHERE job_id=@jobId AND attempt_no=1", new { jobId }));
        Assert.Equal(permanent ? 1 : 2, await database.ScalarAsync<int>("SELECT count(*) FROM job_attempt WHERE job_id=@jobId", new { jobId }));
    }

    [Fact]
    public async Task Wrapped_import_database_timeout_remains_transient()
    {
        var handler = new SyntheticHandler((_, _, _, _) => throw new UploadFailedException(Guid.NewGuid(), new TimeoutException("Synthetic wrapped timeout")));
        await using var services = CreateServices(handler);
        var jobId = await EnqueueAsync(services);
        Assert.True(await services.GetRequiredService<JobDispatcher>().RunOneAsync("synthetic-wrapped", TestContext.Current.CancellationToken));
        Assert.Equal("queued", await database.ScalarAsync<string>("SELECT state FROM job WHERE id=@jobId", new { jobId }));
        Assert.Equal("transient", await database.ScalarAsync<string>("SELECT error_kind FROM job_attempt WHERE job_id=@jobId", new { jobId }));
    }

    [Fact]
    public async Task Dispatcher_heartbeats_while_handler_is_running()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new SyntheticHandler(async (_, _, _, cancellationToken) =>
        { started.SetResult(); await release.Task.WaitAsync(cancellationToken); return null; });
        await using var services = CreateServices(handler);
        var jobId = await EnqueueAsync(services);
        var execution = services.GetRequiredService<JobDispatcher>().RunOneAsync("synthetic-long", TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var originalLease = clock.GetUtcNow().AddSeconds(9).UtcDateTime;
        clock.Advance(TimeSpan.FromSeconds(6));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await database.ScalarAsync<bool>("SELECT locked_until>@originalLease FROM job WHERE id=@jobId", new { jobId, originalLease }))
        { deadline.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Null(await services.GetRequiredService<JobStore>().ClaimAsync("synthetic-cannot-steal", 9, TestContext.Current.CancellationToken));
        release.SetResult();
        Assert.True(await execution);
        Assert.Equal("succeeded", await database.ScalarAsync<string>("SELECT state FROM job WHERE id=@jobId", new { jobId }));
    }

    [Fact]
    public async Task Shutdown_cancels_handler_and_leaves_lease_for_recovery()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new SyntheticHandler(async (_, _, _, cancellationToken) =>
        { started.SetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); return null; });
        await using var services = CreateServices(handler);
        var jobId = await EnqueueAsync(services);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var execution = services.GetRequiredService<JobDispatcher>().RunOneAsync("synthetic-stopping", stopping.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        stopping.Cancel();
        Assert.True(await execution);
        Assert.Equal("running", await database.ScalarAsync<string>("SELECT state FROM job WHERE id=@jobId", new { jobId }));
        clock.Advance(TimeSpan.FromSeconds(10));
        var recovered = await services.GetRequiredService<JobStore>().ClaimAsync("synthetic-next", 9, TestContext.Current.CancellationToken);
        Assert.NotNull(recovered);
        Assert.Equal(jobId, recovered.Id);
    }

    [Fact]
    public async Task Polling_host_runs_two_handlers_concurrently_and_stops_cleanly()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var handler = new SyntheticHandler(async (_, _, _, cancellationToken) =>
        {
            if (Interlocked.Increment(ref active) == 2) started.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            return null;
        });
        await using var services = CreateServices(handler);
        var first = await EnqueueAsync(services);
        var second = await EnqueueAsync(services, false);
        using var loop = new WorkerLoop(services.GetRequiredService<JobDispatcher>(), services.GetRequiredService<JobOptions>(), clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkerLoop>.Instance);
        await loop.StartAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        release.SetResult();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await database.ScalarAsync<bool>("SELECT count(*)=2 FROM job WHERE id=ANY(@ids) AND state='succeeded'", new { ids = new[] { first, second } }))
        { deadline.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
        await loop.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, active);
    }

    [Theory]
    [InlineData("PollIntervalMilliseconds", "0")]
    [InlineData("Concurrency", "0")]
    [InlineData("LeaseSeconds", "0")]
    [InlineData("MaxAttempts", "0")]
    [InlineData("BackoffCapSeconds", "1")]
    public void Invalid_worker_configuration_fails_at_startup(string setting, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Jobs:" + setting] = value }).Build();
        var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddJobs(configuration));
        Assert.Contains("Jobs:", error.Message, StringComparison.Ordinal);
    }

    private ServiceProvider CreateServices(IJobHandler handler)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = database.AppConnectionString,
            ["Jobs:LeaseSeconds"] = "9"
        }).Build();
        var services = new ServiceCollection().AddLogging().AddReconciliationDatabase(configuration).AddIngestion(configuration).AddJobs(configuration);
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(new JobBackoff(new JobOptions(), _ => 0));
        services.RemoveAll<IJobHandler>();
        services.AddSingleton(handler);
        return services.BuildServiceProvider();
    }

    private async Task<Guid> EnqueueAsync(IServiceProvider services, bool resetQueue = true)
    {
        await using var owner = await database.OpenOwnerAsync();
        if (resetQueue) await owner.ExecuteAsync(new CommandDefinition("TRUNCATE job_attempt,job", cancellationToken: TestContext.Current.CancellationToken));
        var companyId = await owner.QuerySingleAsync<Guid>(new CommandDefinition("INSERT INTO company(rfc,name) VALUES('AAA010101AAA','Synthetic worker') RETURNING id", cancellationToken: TestContext.Current.CancellationToken));
        var actorId = await owner.QuerySingleAsync<Guid>(new CommandDefinition("INSERT INTO app_user(email,password_hash) VALUES(@email,'synthetic-not-a-login-hash') RETURNING id", new { email = $"worker-{Guid.NewGuid():N}@example.invalid" }, cancellationToken: TestContext.Current.CancellationToken));
        return await services.GetRequiredService<CompanyScope>().ExecuteWriteAsync(companyId, (connection, transaction, cancellationToken) =>
            JobStore.EnqueueAsync(connection, transaction, companyId, actorId, "status_check", "{}", null, 3, clock.GetUtcNow(), cancellationToken), TestContext.Current.CancellationToken);
    }

    private sealed class SyntheticHandler(Func<JobRecord, NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<string?>> work) : IJobHandler
    {
        public string Type => "status_check";
        public Task<string?> HandleAsync(JobRecord job, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken) => work(job, connection, transaction, cancellationToken);
    }
}
