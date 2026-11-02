namespace Reconciliation.Tests;

public sealed class SessionFoundationsTests
{
    [Fact]
    public async Task Session_ADR_records_the_chosen_renewal_model_and_production_alternative()
    {
        var path = RepoPaths.Doc("adr/ADR-004-session-renewal.md");
        Assert.True(File.Exists(path), "ADR-004 must record session renewal decisions.");
        var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        foreach (var required in new[] { "rotating refresh", "sliding", "cookie sessions", "Bearer", "OIDC", "production", "grace", "15 minutes", "30 days", "SHA-256" })
            Assert.Contains(required, text, StringComparison.OrdinalIgnoreCase);
    }
}
