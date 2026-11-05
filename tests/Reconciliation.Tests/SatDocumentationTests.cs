using System.IO.Compression;
using System.Text.Json;

namespace Reconciliation.Tests;

public sealed class SatDocumentationTests
{
    [Fact]
    public async Task ADR_008_documents_contract_shared_policy_privacy_phases_queue_and_phase2_limits()
    {
        var path = RepoPaths.Doc("adr/ADR-008-sat-client-port-and-adapters.md"); Assert.True(File.Exists(path));
        var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        foreach (var token in new[] { "ISatClient", "SOAP", "stub", "real", "replay", "0000001939.270000", "0000000000.000000", "601", "602", "EFOS", "200", "empty",
            "24", "1 hour", "TimeProvider", "IRequestRateGate", "TokenBucketRateLimiter", "per process", "no connection", "phase 3", "lease fence", "60", "100", "20",
            "follow-up", "UNNEST", "backfill", "seq", "original due time", "privacy", "anonymised", "fallback", "7.6", "Phase 2", "Alternatives", "N workers" }) Assert.Contains(token, text, StringComparison.OrdinalIgnoreCase);
        var queue = await File.ReadAllTextAsync(RepoPaths.Doc("adr/ADR-005-postgresql-job-queue.md"), TestContext.Current.CancellationToken);
        Assert.Contains("original due time", queue, StringComparison.OrdinalIgnoreCase); Assert.Contains("seq", queue, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("BUILD-LOG")]
    [InlineData("BUILD-LOG-ES")]
    public async Task Bilingual_logs_and_exports_include_the_current_SAT_entry(string stem)
    {
        var text = await File.ReadAllTextAsync(RepoPaths.Doc($"build-log/{stem}.md"), TestContext.Current.CancellationToken);
        foreach (var token in new[] { "PR-11: SAT status check", "ADR-008", "M0011", "sat_status_cache", "TimeProvider", "IRequestRateGate", "status_check", "601", "EFOS", "7.6", "Phase 2", "seq", "follow-up", "1,000", "499.5", ".local/sat-recordings", "PR-10: Matching rest" }) Assert.Contains(token, text, StringComparison.Ordinal);
        using var doc = ZipFile.OpenRead(RepoPaths.Doc($"build-log/exports/{stem}.docx"));
        using var reader = new StreamReader(doc.GetEntry("word/document.xml")!.Open());
        Assert.Contains("PR-11: SAT status check", await reader.ReadToEndAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task README_explains_SAT_configuration_synthetic_commands_and_stored_read_values()
    {
        var readme = await File.ReadAllTextAsync(Path.Combine(RepoPaths.Root, "README.md"), TestContext.Current.CancellationToken);
        foreach (var token in new[] { "ADR-008", "/sat-check", "SAT_MODE", "Sat:Mode", "replay", "anonymizedUuid", "cancellability", "efosRaw", "checkedAt", "lastError", "Phase 2", "7.6", "curl", "control/outcomes" }) Assert.Contains(token, readme, StringComparison.Ordinal);
        Assert.DoesNotContain("not implemented until group 7", readme, StringComparison.Ordinal);
        var environment = await File.ReadAllTextAsync(Path.Combine(RepoPaths.Root, ".env.example"), TestContext.Current.CancellationToken);
        foreach (var token in new[] { "SAT_MODE=stub", "SAT_MAX_REQUESTS_PER_SECOND=2", "SAT_JOB_BUDGET=00:01:00", "SAT_RECORD_RESPONSES=false" }) Assert.Contains(token, environment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task As_built_database_sources_include_the_SAT_cache_and_FIFO_sequence()
    {
        var text = await File.ReadAllTextAsync(Path.Combine(RepoPaths.Root, "tools/db_diagrams.py"), TestContext.Current.CancellationToken);
        foreach (var token in new[] { "M0011", "'sat_status_cache':", "'seq'", "checked_at", "expires_at", "efos_raw", "last_error_kind" }) Assert.Contains(token, text, StringComparison.Ordinal);
        foreach (var suffix in new[] { "", "-es" })
        {
            var svg = await File.ReadAllTextAsync(RepoPaths.Doc($"diagrams/database/database-erd-implemented{suffix}.svg"), TestContext.Current.CancellationToken);
            Assert.Contains("sat_status_cache", svg, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Architecture_records_a_real_worker_SOAP_path_to_the_internal_stub()
    {
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(RepoPaths.Doc("diagrams/architecture/system-as-built.architecture.json"), TestContext.Current.CancellationToken));
        var sat = doc.RootElement.GetProperty("components").EnumerateArray().Single(item => item.GetProperty("id").GetString() == "sat");
        Assert.Contains("SOAP", sat.GetProperty("sublabel").GetString()!, StringComparison.Ordinal);
        var connection = doc.RootElement.GetProperty("connections").EnumerateArray().Single(item => item.GetProperty("id").GetString() == "worker-to-sat");
        Assert.Contains("SOAP", connection.GetProperty("label").GetString()!, StringComparison.Ordinal);
        Assert.False(connection.TryGetProperty("variant", out var variant) && variant.GetString() == "dashed");
    }
}
