namespace Reconciliation.Tests;

public sealed class SessionDocumentationTests
{
    [Fact]
    public async Task Readme_explains_the_session_contract_and_provides_the_cli_flow()
    {
        var text = await File.ReadAllTextAsync(Path.Combine(RepoPaths.Root, "README.md"), TestContext.Current.CancellationToken);
        foreach (var required in new[] { "refreshToken", "expiresIn", "/auth/refresh", "/auth/logout", "/auth/password", "15-minute", "409", "single-flight", "Session__RefreshCookie", "Origin", "HttpOnly", "SHA-256" })
            Assert.Contains(required, text, StringComparison.Ordinal);
        Assert.DoesNotContain("--solution Reconciliation.slnx --solution", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Environment_example_documents_session_defaults_and_optional_origins()
    {
        var text = await File.ReadAllTextAsync(Path.Combine(RepoPaths.Root, ".env.example"), TestContext.Current.CancellationToken);
        foreach (var required in new[] { "Session__AccessTokenMinutes=15", "Session__IdleMinutes=480", "Session__AbsoluteDays=30", "Session__ReuseGraceSeconds=10", "Session__RefreshCookie=false", "Session__AllowedOrigins__0=", "RefreshRateLimit__IpPermitLimit=60" })
            Assert.Contains(required, text, StringComparison.Ordinal);
    }
}
