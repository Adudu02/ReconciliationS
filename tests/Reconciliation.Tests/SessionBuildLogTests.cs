namespace Reconciliation.Tests;

public sealed class SessionBuildLogTests
{
    [Theory]
    [InlineData("BUILD-LOG.md")]
    [InlineData("BUILD-LOG-ES.md")]
    public async Task Each_build_log_records_session_architecture_dictionary_and_the_six_question_PR_entry(string file)
    {
        var text = await File.ReadAllTextAsync(RepoPaths.Doc("build-log/" + file), TestContext.Current.CancellationToken);
        foreach (var required in new[] { "PR-6:", "SessionOptions", "TimeProvider", "auth_session", "refresh_token", "auth_event", "token_hash", "absolute_expires_at", "Session__AccessTokenMinutes", "Session__AllowedOrigins__0", "RefreshRateLimit__IpPermitLimit", "reuse_detected", "ADR-004", "409", "OIDC" })
            Assert.Contains(required, text, StringComparison.Ordinal);
    }
}
