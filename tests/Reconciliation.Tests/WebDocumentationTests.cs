namespace Reconciliation.Tests;

public sealed class WebDocumentationTests
{
    [Fact]
    public void ADR010_records_the_browser_boundary_security_contract_and_rejected_alternatives()
    {
        var path = RepoPaths.Doc("adr/ADR-010-browser-ui-and-api-contract.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        foreach (var token in new[] { "Blazor WebAssembly", "static shell", "anonymous", "Reconciliation.Contracts", "in memory", "HttpOnly", "SameSite=Strict", "X-Refresh-Transport", "Session.BrowserRefreshCookie", "Origin", "Content-Security-Policy", "wasm-unsafe-eval", "application/problem+json", "localStorage", "sessionStorage", "M0013", "user_companies", "app.user_id", "cfdi_directory_owner", "BYPASSRLS", "STABLE", "React", "Blazor Server", "MAUI", "plain JavaScript", "SSE", "English" })
            Assert.Contains(token, text, StringComparison.Ordinal);
        var project = File.ReadAllText(Path.Combine(RepoPaths.Root, "src/Reconciliation.Web/Reconciliation.Web.csproj"));
        Assert.DoesNotContain("Reconciliation.Core", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Reconciliation.Migrator", project, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("BUILD-LOG.md")]
    [InlineData("BUILD-LOG-ES.md")]
    public void PR13_logs_delivery_verification_payload_and_limits_in_both_languages(string file)
    {
        var text = File.ReadAllText(RepoPaths.Doc("build-log/" + file));
        foreach (var token in new[] { "PR-13: Minimal web UI", "ADR-010", "Reconciliation.Web", "M0013", "user_companies", "cfdi_directory_owner", "app.user_id", "Session__BrowserRefreshCookie", "X-Refresh-Transport", "application/problem+json", "17 operations", "50%", "5,800.00", "7,319,437", "12,544,724", "docx@9.5.1", "pdf2image==1.17.0", "SSE", "pr13-login.png", "pr13-overview.png", "pr13-review-queue.png" })
            Assert.Contains(token, text, StringComparison.Ordinal);
    }

    [Fact]
    public void UI_completion_and_run_instructions_match_the_served_application()
    {
        var tasks = File.ReadAllText(Path.Combine(RepoPaths.Root, "openspec/changes/mvp-reconciliation-core/tasks.md"));
        Assert.Contains("- [x] 8.5 ", tasks, StringComparison.Ordinal);
        var readme = File.ReadAllText(Path.Combine(RepoPaths.Root, "README.md"));
        foreach (var token in new[] { "## Web UI", "http://localhost:8080", "Session__BrowserRefreshCookie", "Origin", "in memory", "application/problem+json", "results have no fiscal or legal validity", "tools/ui_smoke.mjs", "English" })
            Assert.Contains(token, readme, StringComparison.Ordinal);
        var architecture = File.ReadAllText(RepoPaths.Doc("diagrams/architecture/system-as-built.architecture.json"));
        Assert.Contains("Reconciliation.Web", architecture, StringComparison.Ordinal);
        Assert.Contains("static shell", architecture, StringComparison.Ordinal);
        var database = File.ReadAllText(Path.Combine(RepoPaths.Root, "tools/db_diagrams.py"));
        Assert.Contains("M0013", database, StringComparison.Ordinal);
        Assert.Contains("cfdi_directory_owner", database, StringComparison.Ordinal);
    }
}
