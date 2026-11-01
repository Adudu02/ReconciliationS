namespace Reconciliation.Tests;

public sealed class IngestionBuildLogTests
{
    [Theory]
    [InlineData("BUILD-LOG.md")]
    [InlineData("BUILD-LOG-ES.md")]
    public async Task PR_3_records_ingestion_decisions_and_limits_in_each_language(string file)
    {
        var path = RepoPaths.Doc("build-log/" + file);
        var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        foreach (var required in new[] { "PR-3:", "upload_id", "MaxFileBytes", "MaxUploadBytes", "MaxFiles", "MaxXmlNodes", "MaxXmlDepth", "RetentionYears", "accountReference", "numeric(20,10)" })
            Assert.Contains(required, text, StringComparison.Ordinal);
    }
}
