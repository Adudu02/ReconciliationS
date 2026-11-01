using System.Text;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Reconciliation.Core.Ingestion;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class BankImportTests(ApiFixture fixture)
{
    private const string Header = "fecha,concepto,abono,cargo,saldo\n";
    private const string First = "2026-10-10,SPEI SINTETICO RASTREO:ABC123 PAGO FACT 0001,5800.00,0,5800.00\n";
    private const string Second = "2026-10-20,COBRO SINTETICO REF:0002,5800.00,0,11600.00\n";

    [Fact]
    public async Task Documented_examples_parse_with_dates_amounts_quoted_descriptions_and_references()
    {
        var path = Path.GetFullPath("../../../../../docs/bank-csv-reference-format.md", AppContext.BaseDirectory);
        var markdown = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        var sample = markdown.Split("```csv\n", StringSplitOptions.None)[1].Split("```", StringSplitOptions.None)[0];
        var parser = fixture.App.Services.GetRequiredService<IEnumerable<IBankStatementParser>>().Single();
        Assert.Equal("reference", parser.Bank);
        var rows = parser.Parse(Encoding.UTF8.GetBytes(sample));
        Assert.Equal(3, rows.Count);
        Assert.Equal(5800m, rows[0].Credit);
        Assert.Equal(new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Unspecified), rows[0].MovementDate);
        Assert.Equal("ABC123", rows[0].Reference);
        Assert.Equal("0002", rows[1].Reference);
        Assert.Null(rows[2].Reference);
        Assert.Equal("COMISION SINTETICA, SERVICIO", rows[2].Description);
        Assert.Equal(10m, rows[2].Debit);
    }

    [Fact]
    public async Task Valid_statement_and_overlapping_statements_import_only_new_movements()
    {
        var user = await fixture.SeedUserAsync();
        var company = await fixture.SeedCompanyAsync(user.Id);
        var importer = fixture.App.Services.GetRequiredService<BankStatementImporter>();
        var first = await importer.ImportAsync(company, user.Id, "reference", "Synthetic-1234", [Csv(Header + First + Second)], TestContext.Current.CancellationToken);
        Assert.Equal(2, Assert.Single(first.Files).ImportedMovements);
        var overlap = await importer.ImportAsync(company, user.Id, "reference", "Synthetic-1234", [Csv(Header + Second + "2026-10-21,NUEVO SINTETICO,10,0,11610.00\n")], TestContext.Current.CancellationToken);
        Assert.Equal(1, Assert.Single(overlap.Files).ImportedMovements);
        Assert.Equal(1, Assert.Single(overlap.Files).DuplicateMovements);
        await using var owner = new NpgsqlConnection(fixture.Database.OwnerConnectionString);
        Assert.Equal(3, await owner.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM bank_movement WHERE company_id = @company", new { company }, cancellationToken: TestContext.Current.CancellationToken)));
        var original = await owner.QuerySingleAsync<byte[]>(new CommandDefinition("SELECT original FROM import_file WHERE id = @FileId", new { overlap.Files[0].FileId }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(Csv(Header + Second + "2026-10-21,NUEVO SINTETICO,10,0,11610.00\n").Content, original);
    }

    [Fact]
    public async Task Two_legitimate_identical_movements_with_different_balances_are_preserved()
    {
        var user = await fixture.SeedUserAsync();
        var company = await fixture.SeedCompanyAsync(user.Id);
        var result = await fixture.App.Services.GetRequiredService<BankStatementImporter>().ImportAsync(company, user.Id, "reference", "Synthetic-1234",
            [Csv(Header + First + First.Replace("0,5800.00", "0,11600.00", StringComparison.Ordinal))], TestContext.Current.CancellationToken);
        Assert.Equal(2, Assert.Single(result.Files).ImportedMovements);
    }

    [Theory]
    [InlineData("2026-99-10,BAD,1,0,1", "2")]
    [InlineData("2026-10-10,BAD,oops,0,1", "2")]
    [InlineData("2026-10-10,BAD,0.001,0,1", "2")]
    [InlineData("2026-10-10,BAD,1,2,1", "2")]
    [InlineData("2026-10-10,\"unterminated,1,0,1", "2")]
    public async Task Malformed_line_rejects_whole_statement_with_line_number_and_no_partial_account_or_movements(string line, string reason)
    {
        var user = await fixture.SeedUserAsync();
        var company = await fixture.SeedCompanyAsync(user.Id);
        var report = await fixture.App.Services.GetRequiredService<BankStatementImporter>().ImportAsync(company, user.Id, "reference", "Synthetic-1234", [Csv(Header + line + "\n" + First)], TestContext.Current.CancellationToken);
        var rejected = Assert.Single(report.Files);
        Assert.Equal("rejected", rejected.Status);
        Assert.Contains(reason, rejected.Reason!, StringComparison.Ordinal);
        await using var owner = new NpgsqlConnection(fixture.Database.OwnerConnectionString);
        Assert.Equal(0, await owner.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM bank_account WHERE company_id = @company", new { company }, cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("unknown", "Synthetic-1234", "reference")]
    [InlineData("reference", "invalid/account", "accountReference")]
    public async Task Unknown_bank_and_invalid_account_label_are_rejected_with_reason(string bank, string account, string reason)
    {
        var user = await fixture.SeedUserAsync();
        var company = await fixture.SeedCompanyAsync(user.Id);
        var result = Assert.Single((await fixture.App.Services.GetRequiredService<BankStatementImporter>().ImportAsync(company, user.Id, bank, account, [Csv(Header + First)], TestContext.Current.CancellationToken)).Files);
        Assert.Equal("rejected", result.Status);
        Assert.Contains(reason, result.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reference_format_handles_BOM_CRLF_empty_credit_and_tracking_precedence()
    {
        var parser = fixture.App.Services.GetRequiredService<IEnumerable<IBankStatementParser>>().Single();
        var row = Assert.Single(parser.Parse(Encoding.UTF8.GetBytes("\uFEFF" + Header.Replace("\n", "\r\n", StringComparison.Ordinal) + "2026-10-10,REF:0002 RASTREO:ABC123,,10.00,-10.00\r\n")));
        Assert.Equal("ABC123", row.Reference);
        Assert.Equal(10m, row.Debit);
        Assert.Equal(-10m, row.Balance);
    }

    private static UploadedFile Csv(string csv) => UploadedFile.FromBytes("synthetic.csv", Encoding.UTF8.GetBytes(csv));
}
