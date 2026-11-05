using System.IO.Compression;

namespace Reconciliation.Tests;

public sealed class MatchingDocumentationTests
{
    [Fact]
    public async Task ADR_007_documents_explainable_pipeline_bounded_indexes_decisions_and_alternatives()
    {
        var path = RepoPaths.Doc("adr/ADR-007-explainable-rule-pipeline.md");
        Assert.True(File.Exists(path), "ADR-007 must explain the matching contract.");
        var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        foreach (var required in new[] { "pure", "40", "35", "30", "25", "20", "15", "70", "45", "high", "medium", "low",
            "remaining payable", "amount equals total before retentions", "201", "MaxCandidatesPerMovement", "50", "RFC", "nearest", "lazy deletion",
            "O(L log L)", "linearithmic", "brute-force", "identifier", "no name", "link.upgraded", "link.closed", "pendingAmount", "uncoveredAmount",
            "single opaque score", "learned model", "ADR-006", "typed", "NULL", "6B" })
            Assert.Contains(required, text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("BUILD-LOG.md")]
    [InlineData("BUILD-LOG-ES.md")]
    public async Task PR_9_records_measured_cost_decisions_limits_and_preserves_prior_entries(string filename)
    {
        var text = await File.ReadAllTextAsync(RepoPaths.Doc("build-log/" + filename), TestContext.Current.CancellationToken);
        foreach (var required in new[] { "PR-9: Matching core", "ADR-007", "M0009", "MatchingEngine", "reconciliation_run", "20,000", "5,000",
            "RUN_BENCHMARKS=1", "median", "O(L log L)", "pendingAmount", "uncoveredAmount", "spec tension", "1,939.27", "link.upgraded", "link.closed",
            "5.3", "6B", "PR-8: Audit log", "PR-7:", "PR-6:" })
            Assert.Contains(required, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task README_and_as_built_schema_cover_matching_reads_and_classification()
    {
        var readme = await File.ReadAllTextAsync(Path.Combine(RepoPaths.Root, "README.md"), TestContext.Current.CancellationToken);
        foreach (var required in new[] { "ADR-007", "movements?state=pending", "classification", "afterDate", "afterId", "nextAfterId", "1.00", "11600", "5800" })
            Assert.Contains(required, readme, StringComparison.Ordinal);
        Assert.DoesNotContain("not implemented until group 6", readme, StringComparison.Ordinal);
        var diagrams = await File.ReadAllTextAsync(Path.Combine(RepoPaths.Root, "tools/db_diagrams.py"), TestContext.Current.CancellationToken);
        foreach (var required in new[] { "M0009", "'link':", "amount_applied", "payment_cfdi_id", "classification_note", "serie", "folio", "cfdi_state", "movement_state" })
            Assert.Contains(required, diagrams, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("BUILD-LOG")]
    [InlineData("BUILD-LOG-ES")]
    public async Task Word_exports_include_current_matching_entry(string stem)
    {
        using var document = ZipFile.OpenRead(RepoPaths.Doc($"build-log/exports/{stem}.docx"));
        using var reader = new StreamReader(document.GetEntry("word/document.xml")!.Open());
        Assert.Contains("PR-9: Matching core", await reader.ReadToEndAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }
}
