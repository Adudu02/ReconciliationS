namespace Reconciliation.Tests;

public sealed class TenantAdrTests
{
    [Fact]
    public async Task ADR_003_records_RLS_and_compares_both_isolation_alternatives()
    {
        var path = RepoPaths.Doc("adr/ADR-003-tenant-isolation-rls.md");
        var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        foreach (var required in new[] { "row-level security", "schema-per-company", "database-per-company", "FORCE", "cfdi_app", "transaction", "group 5", "invite" })
            Assert.Contains(required, text, StringComparison.OrdinalIgnoreCase);
    }
}
