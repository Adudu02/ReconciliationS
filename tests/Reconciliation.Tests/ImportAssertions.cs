using Reconciliation.Core.Ingestion;

namespace Reconciliation.Tests;

internal static class ImportAssertions
{
    public static FileImportOutcome AssertSingleStatus(ImportReport report, string expectedStatus)
    {
        var outcome = Assert.Single(report.Files);
        Assert.Equal(expectedStatus, outcome.Status);
        return outcome;
    }
}
