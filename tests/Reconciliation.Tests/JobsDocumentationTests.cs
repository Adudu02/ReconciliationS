namespace Reconciliation.Tests;

public sealed class JobsDocumentationTests
{
    [Theory]
    [InlineData("BUILD-LOG.md")]
    [InlineData("BUILD-LOG-ES.md")]
    public async Task Jobs_logs_explain_the_queue_boundary_operations_and_current_exports(string filename)
    {
        var text = await File.ReadAllTextAsync(RepoPaths.Doc("build-log/" + filename), TestContext.Current.CancellationToken);
        foreach (var required in new[] { "PR-7:", "ADR-005", "M0007", "JobLoop", "JobDispatcher", "job_attempt", "pending_original", "file_index",
            "claim_next_job", "NOLOGIN", "BYPASSRLS", "p_now", "TimeProvider", "100 jobs/minute", "docker compose kill worker", "202", "backoff" })
            Assert.Contains(required, text, StringComparison.Ordinal);
        var readme = await File.ReadAllTextAsync(Path.Combine(RepoPaths.Root, "README.md"), TestContext.Current.CancellationToken);
        foreach (var required in new[] { "202", "jobId", "uploadId", "reconciliation-runs", "Jobs__LeaseSeconds", "not implemented until group 6" })
            Assert.Contains(required, readme, StringComparison.Ordinal);
        var environment = await File.ReadAllTextAsync(Path.Combine(RepoPaths.Root, ".env.example"), TestContext.Current.CancellationToken);
        Assert.Contains("JOBS_LEASE_SECONDS=60", environment, StringComparison.Ordinal);
        var diagrams = await File.ReadAllTextAsync(Path.Combine(RepoPaths.Root, "tools/db_diagrams.py"), TestContext.Current.CancellationToken);
        foreach (var required in new[] { "M0007", "'job':", "'job_attempt':", "pending_original", "locked_until" })
            Assert.Contains(required, diagrams, StringComparison.Ordinal);
    }
}
