namespace Reconciliation.Tests;

public sealed class QueueAdrTests
{
    [Fact]
    public async Task Queue_ADR_compares_alternatives_and_states_volume_limit_migration_path_and_privileges()
    {
        var path = RepoPaths.Doc("adr/ADR-005-postgresql-job-queue.md");
        Assert.True(File.Exists(path), "The next free queue ADR is ADR-005; ADR-004 belongs to sessions.");
        var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        foreach (var required in new[] { "PostgreSQL", "Redis", "RabbitMQ", "SQS", "SKIP LOCKED", "transactional enqueue",
            "volume limit", "100 jobs/minute", "migration path", "outbox", "SECURITY DEFINER", "cfdi_queue_owner", "NOLOGIN", "BYPASSRLS", "cfdi_app",
            "search_path", "PUBLIC", "pending_original", "p_now", "TimeProvider", "group 6", "group 7", "idempotent" })
            Assert.Contains(required, text, StringComparison.OrdinalIgnoreCase);
    }
}
