namespace Reconciliation.Tests;

public sealed class BankCsvDocumentationTests
{
    [Fact]
    public async Task Reference_format_documents_the_contract_and_synthetic_parseable_examples()
    {
        var path = RepoPaths.Doc("reference/bank-csv-reference-format.md");
        var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        foreach (var required in new[] { "fecha,concepto,abono,cargo,saldo", "yyyy-MM-dd", "RASTREO:", "REF:", "reference", "```csv", "5800.00" })
            Assert.Contains(required, text, StringComparison.Ordinal);
    }
}
