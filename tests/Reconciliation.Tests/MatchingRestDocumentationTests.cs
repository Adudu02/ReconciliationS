using System.IO.Compression;

namespace Reconciliation.Tests;

public sealed class MatchingRestDocumentationTests
{
    [Fact]
    public async Task ADR_007_amends_net_payable_global_and_cancellation_contracts_and_honest_limits()
    {
        var text = await File.ReadAllTextAsync(RepoPaths.Doc("adr/ADR-007-explainable-rule-pipeline.md"), TestContext.Current.CancellationToken);
        foreach (var token in new[] { "Amendments in 6B", "M0010", "same currency", "original", "approximation", "fully credited", "requiresReview",
            "global invoice", "UTC", "holidays", "first-claim", "cancelled invoice", "cancelled payment complement", "relation 04", "same receiver",
            "direct", "EquivalenciaDR", "6.11", "O(L log L)", "201", "50" })
            Assert.Contains(token, text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("BUILD-LOG")]
    [InlineData("BUILD-LOG-ES")]
    public async Task Logs_and_exports_record_matching_rest_decisions_measured_deltas_and_group8_contract(string stem)
    {
        var text = await File.ReadAllTextAsync(RepoPaths.Doc($"build-log/{stem}.md"), TestContext.Current.CancellationToken);
        foreach (var token in new[] { "PR-10: Matching rest", "M0010", "cfdi_review_reasons", "requiresReview", "payment_ref", "payment_cfdi_id",
            "20,000", "5,000", "before", "after", "median", "RUN_BENCHMARKS=1", "UTC", "EquivalenciaDR", "6.11", "group 8",
            "pendingAmount > 0", "PR-9: Matching core", "PR-8: Audit log" })
            Assert.Contains(token, text, StringComparison.Ordinal);
        using var document = ZipFile.OpenRead(RepoPaths.Doc($"build-log/exports/{stem}.docx"));
        using var reader = new StreamReader(document.GetEntry("word/document.xml")!.Open());
        Assert.Contains("PR-10: Matching rest", await reader.ReadToEndAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task README_and_diagram_sources_expose_matching_rest_derived_contract()
    {
        var readme = await File.ReadAllTextAsync(Path.Combine(RepoPaths.Root, "README.md"), TestContext.Current.CancellationToken);
        foreach (var token in new[] { "creditNotes", "payable", "complementDueDate", "complementOverdue", "requiresReview", "cancellationStatus", "fully credited", "6.11" })
            Assert.Contains(token, readme, StringComparison.Ordinal);
        var diagrams = await File.ReadAllTextAsync(Path.Combine(RepoPaths.Root, "tools/db_diagrams.py"), TestContext.Current.CancellationToken);
        foreach (var token in new[] { "M0010", "cancellation_status", "cfdi_review_reasons", "cfdi_payable", "review_reason" })
            Assert.Contains(token, diagrams, StringComparison.Ordinal);
    }
}
