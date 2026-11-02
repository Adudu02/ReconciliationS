using System.Text;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Reconciliation.Core.Common;
using Reconciliation.Core.Ingestion;
using Reconciliation.Core.Jobs;
using Reconciliation.Core.Tenancy;

namespace Reconciliation.Tests;

public sealed class QueuedImportTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private readonly ManualTimeProvider clock = new();

    [Fact(DisplayName = "Worker killed mid-job")]
    public Task Worker_killed_mid_job() => AssertImportOnceAsync(true);

    [Fact(DisplayName = "Redelivered job")]
    public Task Redelivered_job() => AssertImportOnceAsync(false);

    private async Task AssertImportOnceAsync(bool workerKilled)
    {
        await using var services = CreateServices();
        var (companyId, actorId) = await SeedAsync();
        var bytes = SyntheticCfdi.Invoice();
        var queued = await services.GetRequiredService<ImportJobService>().EnqueueAsync(companyId, actorId, "import_cfdi", [UploadedFile.FromBytes("synthetic.xml", bytes)], null, null, TestContext.Current.CancellationToken);
        var store = services.GetRequiredService<JobStore>();
        var first = (await store.ClaimAsync("synthetic-original", 9, TestContext.Current.CancellationToken))!;
        Assert.Equal(queued.JobId, first.Id);
        var handler = services.GetServices<IJobHandler>().Single(handler => handler.Type == "import_cfdi");
        await RunHandlerAsync(services, handler, first);
        var delivered = first;
        if (workerKilled)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            delivered = (await store.ClaimAsync("synthetic-replacement", 9, TestContext.Current.CancellationToken))!;
            Assert.Equal(first.Id, delivered.Id);
            Assert.Equal(2, delivered.Attempts);
        }
        await RunHandlerAsync(services, handler, delivered);
        Assert.True(await store.FinishAsync(delivered, delivered.LockedBy!, null, null, TimeSpan.Zero, TestContext.Current.CancellationToken));
        Assert.Equal(1, await database.ScalarAsync<int>("SELECT count(*) FROM cfdi WHERE company_id=@companyId", new { companyId }));
        Assert.Equal(1, await database.ScalarAsync<int>("SELECT count(*) FROM import_file WHERE upload_id=@UploadId", new { queued.UploadId }));
        Assert.Equal(1, await database.ScalarAsync<int>("SELECT count(*) FROM audit_entry WHERE company_id=@companyId AND action='cfdi.imported'", new { companyId }));
        Assert.Equal(0, await database.ScalarAsync<int>("SELECT count(*) FROM import_file WHERE upload_id=@UploadId AND pending_original IS NOT NULL", new { queued.UploadId }));
        Assert.Equal(bytes, await database.ScalarAsync<byte[]>("SELECT original FROM import_file WHERE upload_id=@UploadId", new { queued.UploadId }));
        Assert.Equal("succeeded", await database.ScalarAsync<string>("SELECT state FROM job WHERE id=@JobId", new { queued.JobId }));
        await using var owner = await database.OpenOwnerAsync();
        await Assert.ThrowsAsync<PostgresException>(() => owner.ExecuteAsync(new CommandDefinition("UPDATE import_file SET original='\\x01' WHERE upload_id=@UploadId", new { queued.UploadId }, cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Worker_rejects_bad_files_independently_clears_pending_bytes_and_records_progress()
    {
        await using var services = CreateServices();
        var (companyId, actorId) = await SeedAsync();
        var queued = await services.GetRequiredService<ImportJobService>().EnqueueAsync(companyId, actorId, "import_cfdi",
            [UploadedFile.FromBytes("valid.xml", SyntheticCfdi.Invoice()), UploadedFile.FromBytes("hostile.xml", Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY a SYSTEM 'http://127.0.0.1:1/synthetic'>]><x>&a;</x>")), UploadedFile.FromBytes("invalid.xml", Encoding.UTF8.GetBytes("<invalid/>"))], null, null, TestContext.Current.CancellationToken);
        Assert.Equal(3, await database.ScalarAsync<int>("SELECT count(*) FROM import_file WHERE upload_id=@UploadId AND status='queued' AND pending_original IS NOT NULL AND original IS NULL", new { queued.UploadId }));
        Assert.True(await services.GetRequiredService<JobDispatcher>().RunOneAsync("synthetic-importer", TestContext.Current.CancellationToken));
        Assert.Equal(2, await database.ScalarAsync<int>("SELECT count(*) FROM import_file WHERE upload_id=@UploadId AND status='rejected' AND pending_original IS NULL AND original IS NULL", new { queued.UploadId }));
        Assert.True(await database.ScalarAsync<bool>("SELECT state='succeeded' AND progress_done=3 AND progress_total=3 FROM job WHERE id=@JobId", new { queued.JobId }));
    }

    [Fact]
    public async Task Failed_job_keeps_queued_input_bytes_and_domain_changes_roll_back()
    {
        await using var services = CreateServices();
        var (companyId, actorId) = await SeedAsync();
        var queued = await services.GetRequiredService<ImportJobService>().EnqueueAsync(companyId, actorId, "import_statement",
            [UploadedFile.FromBytes("synthetic.csv", Encoding.UTF8.GetBytes("fecha,concepto,abono,cargo,saldo\n2026-10-10,SYNTHETIC,1,0,1\n"))], "reference", "Synthetic-1234", TestContext.Current.CancellationToken);
        await using var owner = await database.OpenOwnerAsync();
        await owner.ExecuteAsync(new CommandDefinition("REVOKE INSERT ON audit_entry FROM cfdi_app", cancellationToken: TestContext.Current.CancellationToken));
        try { Assert.True(await services.GetRequiredService<JobDispatcher>().RunOneAsync("synthetic-failure", TestContext.Current.CancellationToken)); }
        finally { await owner.ExecuteAsync(new CommandDefinition("GRANT INSERT ON audit_entry TO cfdi_app", cancellationToken: CancellationToken.None)); }
        Assert.Equal("failed", await database.ScalarAsync<string>("SELECT state FROM job WHERE id=@JobId", new { queued.JobId }));
        Assert.True(await database.ScalarAsync<bool>("SELECT status='queued' AND pending_original IS NOT NULL AND original IS NULL FROM import_file WHERE upload_id=@UploadId", new { queued.UploadId }));
        Assert.Equal(0, await database.ScalarAsync<int>("SELECT count(*) FROM bank_movement WHERE company_id=@companyId", new { companyId }));
    }

    private static Task<string?> RunHandlerAsync(IServiceProvider services, IJobHandler handler, JobRecord job) =>
        services.GetRequiredService<CompanyScope>().ExecuteAsync(job.CompanyId, (connection, transaction, cancellationToken) => handler.HandleAsync(job, connection, transaction, cancellationToken), TestContext.Current.CancellationToken);

    private ServiceProvider CreateServices()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:Default"] = database.AppConnectionString, ["Jobs:LeaseSeconds"] = "9" }).Build();
        return new ServiceCollection().AddLogging().AddReconciliationDatabase(configuration).AddIngestion(configuration).AddJobs(configuration)
            .AddSingleton<TimeProvider>(clock).BuildServiceProvider();
    }

    private async Task<(Guid CompanyId, Guid ActorId)> SeedAsync()
    {
        await using var owner = await database.OpenOwnerAsync();
        await owner.ExecuteAsync(new CommandDefinition("TRUNCATE job_attempt,job", cancellationToken: TestContext.Current.CancellationToken));
        var companyId = await owner.QuerySingleAsync<Guid>(new CommandDefinition("INSERT INTO company(rfc,name) VALUES('AAA010101AAA','Synthetic queued') RETURNING id", cancellationToken: TestContext.Current.CancellationToken));
        var actorId = await owner.QuerySingleAsync<Guid>(new CommandDefinition("INSERT INTO app_user(email,password_hash) VALUES(@email,'synthetic-not-a-login-hash') RETURNING id", new { email = $"queued-{Guid.NewGuid():N}@example.invalid" }, cancellationToken: TestContext.Current.CancellationToken));
        return (companyId, actorId);
    }
}
