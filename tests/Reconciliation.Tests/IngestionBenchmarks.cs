using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Reconciliation.Core.Ingestion;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class IngestionBenchmarks(ApiFixture fixture)
{
    public static bool Enabled => Environment.GetEnvironmentVariable("RUN_BENCHMARKS") == "1";

    [Fact(Skip = "Opt-in: set RUN_BENCHMARKS=1", SkipUnless = nameof(Enabled), Timeout = 600_000)]
    public async Task Measure_50000_statement_lines_and_payment_with_300_related_documents()
    {
        var statement = new StringBuilder("fecha,concepto,abono,cargo,saldo\n");
        for (var index = 1; index <= 50_000; index++)
            statement.Append(CultureInfo.InvariantCulture, $"2026-10-10,SYNTHETIC-{index},1.00,0,{index}.00\n");
        var csvBytes = Encoding.UTF8.GetBytes(statement.ToString());
        var bankTimes = new List<double>();
        var paymentTimes = new List<double>();
        var user = await fixture.SeedUserAsync();
        for (var sample = 0; sample < 4; sample++)
        {
            var company = await fixture.SeedCompanyAsync(user.Id);
            var watch = Stopwatch.StartNew();
            var bank = await fixture.App.Services.GetRequiredService<BankStatementImporter>().ImportAsync(company, user.Id,
                "reference", "Synthetic-1234", [UploadedFile.FromBytes("benchmark.csv", csvBytes)], TestContext.Current.CancellationToken);
            watch.Stop();
            Assert.Equal(50_000, Assert.Single(bank.Files).ImportedMovements);
            if (sample > 0) bankTimes.Add(watch.Elapsed.TotalMilliseconds);
            var payment = XDocument.Parse(Encoding.UTF8.GetString(SyntheticCfdi.Payment(Guid.NewGuid())));
            XNamespace pagos = "http://www.sat.gob.mx/Pagos20";
            var pago = payment.Descendants(pagos + "Pago").Single();
            var template = new XElement(pago.Element(pagos + "DoctoRelacionado")!);
            pago.RemoveNodes();
            for (var index = 0; index < 300; index++)
            {
                var line = new XElement(template);
                line.SetAttributeValue("IdDocumento", Guid.NewGuid());
                pago.Add(line);
            }
            watch.Restart();
            var report = await fixture.App.Services.GetRequiredService<CfdiImporter>().ImportAsync(company, user.Id,
                [UploadedFile.FromBytes("benchmark.xml", Encoding.UTF8.GetBytes(payment.ToString()))], TestContext.Current.CancellationToken);
            watch.Stop();
            Assert.Equal("imported", Assert.Single(report.Files).Status);
            await using var owner = new NpgsqlConnection(fixture.Database.OwnerConnectionString);
            Assert.Equal(300, await owner.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM cfdi_payment_doc WHERE company_id = @company", new { company }, cancellationToken: TestContext.Current.CancellationToken)));
            if (sample > 0) paymentTimes.Add(watch.Elapsed.TotalMilliseconds);
        }
        var measurements = new
        {
            StatementLines = 50_000,
            RelatedDocuments = 300,
            Warmups = 1,
            Samples = 3,
            BankMilliseconds = bankTimes,
            BankMedianMilliseconds = bankTimes.Order().ElementAt(1),
            PaymentMilliseconds = paymentTimes,
            PaymentMedianMilliseconds = paymentTimes.Order().ElementAt(1)
        };
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "ingestion-benchmark.json"),
            JsonSerializer.Serialize(measurements), TestContext.Current.CancellationToken);
    }
}
