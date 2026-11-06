namespace Reconciliation.Tests;

public sealed class ReviewDocumentationTests
{
    [Fact]
    public void ADR009_records_decision_and_derived_read_contracts_and_alternatives()
    {
        var path = RepoPaths.Doc("adr/ADR-009-review-decisions-and-read-model.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        foreach (var token in new[] { "link_decision", "FOR UPDATE", "409", "company_id", "RLS", "requiresReview", "pendingAmount", "uncoveredAmount", "payment_cfdi_id", "payment_ref", "500", "M0012", "NULLS NOT DISTINCT", "manual export/cleanup", "two audit", "stored states", "materialized views", "1.0.0", "/v2", "reopen", "manual N:M", "8.5" })
            Assert.Contains(token, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("BUILD-LOG.md")]
    [InlineData("BUILD-LOG-ES.md")]
    public void Build_log_records_PR12_delivery_verification_and_limits(string file)
    {
        var text = File.ReadAllText(RepoPaths.Doc("build-log/" + file));
        foreach (var token in new[] { "PR-12: Review, summaries and API", "ADR-009", "M0012", "link_decision", "requiresReview", "pendingAmount", "uncoveredAmount", "payment_cfdi_id", "payment_ref", "20,000", "525.0735", "463.6808", "EXPLAIN", "reopen", "manual N:M", "8.5", "manual export/cleanup" })
            Assert.Contains(token, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Completed_API_tasks_and_examples_leave_UI_for_8B()
    {
        var tasks = File.ReadAllText(Path.Combine(RepoPaths.Root, "openspec/changes/mvp-reconciliation-core/tasks.md"));
        foreach (var id in new[] { "8.1", "8.2", "8.3", "8.3b", "8.3c", "8.3d", "8.4" }) Assert.Contains("- [x] " + id + " ", tasks, StringComparison.Ordinal);
        Assert.Contains("- [ ] 8.5 ", tasks, StringComparison.Ordinal);
        var readme = File.ReadAllText(Path.Combine(RepoPaths.Root, "README.md"));
        foreach (var token in new[] { "/review-queue", "/decisions", "/summary", "/overview", "x-api-version" }) Assert.Contains(token, readme, StringComparison.Ordinal);
        Assert.Contains("'link_decision':", File.ReadAllText(Path.Combine(RepoPaths.Root, "tools/db_diagrams.py")), StringComparison.Ordinal);
    }
}
