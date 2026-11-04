using System.IO.Compression;

namespace Reconciliation.Tests;

public sealed class AuditDocumentationTests
{
    [Fact]
    public async Task ADR_006_documents_canonical_vector_locking_owner_limits_cost_and_alternatives()
    {
        var path = RepoPaths.Doc("adr/ADR-006-append-only-audit-log.md");
        Assert.True(File.Exists(path), "ADR-006 must document the audit contract.");
        var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        foreach (var required in new[] { "canonical", "UTF-8", "microseconds", "prev_hash", "sha256", "FOR UPDATE", "cfdi_audit_owner", "NOLOGIN", "NOBYPASSRLS",
            "SECURITY DEFINER", "pg_catalog", "PUBLIC", "superuser", "external", "triggers-only", "app-level hashing", "external ledger", "O(n)",
            "LEAKPROOF", "jsonb_path_ops", "viewer", "auth_event", "member removal", "_audit_v8", "1767225600123456" })
            Assert.Contains(required, text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("BUILD-LOG.md")]
    [InlineData("BUILD-LOG-ES.md")]
    public async Task PR_8_logs_preserve_prior_tokens_and_explain_current_scope_and_verification(string filename)
    {
        var text = await File.ReadAllTextAsync(RepoPaths.Doc("build-log/" + filename), TestContext.Current.CancellationToken);
        foreach (var required in new[] { "PR-8: Audit log", "ADR-006", "M0008", "append_audit_entry", "verify_audit_chain", "audit_chain_head", "company_seq", "canonical",
            "audit.modification_refused", "auth_event", "viewer", "20,000", "100,000", "O(n)", "LEAKPROOF", "5.3", "_audit_v8", "PR-7:", "PR-6:" })
            Assert.Contains(required, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task README_and_as_built_diagrams_describe_real_audit_endpoints_and_tables()
    {
        var readme = await File.ReadAllTextAsync(Path.Combine(RepoPaths.Root, "README.md"), TestContext.Current.CancellationToken);
        foreach (var required in new[] { "audit-entries", "audit/verification", "afterSeq", "nextAfterSeq", "itemKind", "curl", "200", "ADR-006" })
            Assert.Contains(required, readme, StringComparison.Ordinal);
        var diagram = await File.ReadAllTextAsync(Path.Combine(RepoPaths.Root, "tools/db_diagrams.py"), TestContext.Current.CancellationToken);
        foreach (var required in new[] { "M0008", "'audit_chain_head':", "company_seq", "canonical", "append_audit_entry" })
            Assert.Contains(required, diagram, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("BUILD-LOG")]
    [InlineData("BUILD-LOG-ES")]
    public async Task Word_exports_include_the_current_PR_8_entry(string stem)
    {
        using var document = ZipFile.OpenRead(RepoPaths.Doc($"build-log/exports/{stem}.docx"));
        using var reader = new StreamReader(document.GetEntry("word/document.xml")!.Open());
        var text = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Contains("PR-8: Audit log", text, StringComparison.Ordinal);
    }
}
