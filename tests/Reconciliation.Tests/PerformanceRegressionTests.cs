using System.Text;
using System.Xml.Linq;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Reconciliation.Core.Ingestion;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class PerformanceRegressionTests(ApiFixture fixture)
{
    [Fact]
    public async Task Statement_inserts_use_bounded_chunks_and_count_duplicates_across_chunks()
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        using var probe = new QueryProbe();
        using var logging = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information).AddProvider(probe));
        await using var source = new NpgsqlDataSourceBuilder(fixture.Database.AppConnectionString).UseLoggerFactory(logging).Build();
        using var app = fixture.App.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        { services.RemoveAll<NpgsqlDataSource>(); services.AddSingleton(source); }));
        var csv = new StringBuilder("fecha,concepto,abono,cargo,saldo\n");
        for (var index = 0; index < 10_001; index++) csv.Append(System.Globalization.CultureInfo.InvariantCulture, $"2026-10-10,SYNTHETIC-{index},1,0,{index}\n");
        csv.Append("2026-10-10,SYNTHETIC-0,1,0,0\n");
        var report = await app.Services.GetRequiredService<BankStatementImporter>().ImportAsync(company, user.Id, "reference", "Synthetic-1234",
            [UploadedFile.FromBytes("chunked.csv", Encoding.UTF8.GetBytes(csv.ToString()))], TestContext.Current.CancellationToken);
        Assert.Equal(10_001, report.Files[0].ImportedMovements);
        Assert.Equal(1, report.Files[0].DuplicateMovements);
        Assert.InRange(probe.MovementInserts, 1, 3);
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM import_file WHERE company_id=@company AND status='processing'", new { company }));
    }

    [Fact]
    public async Task Payment_and_relation_details_are_inserted_in_batches()
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        using var probe = new QueryProbe();
        using var logging = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information).AddProvider(probe));
        await using var source = new NpgsqlDataSourceBuilder(fixture.Database.AppConnectionString).UseLoggerFactory(logging).Build();
        using var app = fixture.App.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        { services.RemoveAll<NpgsqlDataSource>(); services.AddSingleton(source); }));
        var document = XDocument.Parse(Encoding.UTF8.GetString(SyntheticCfdi.Payment(Guid.NewGuid())));
        XNamespace pagos = "http://www.sat.gob.mx/Pagos20";
        var payment = document.Descendants(pagos + "Pago").Single();
        var template = new XElement(payment.Element(pagos + "DoctoRelacionado")!);
        payment.RemoveNodes();
        for (var index = 0; index < 300; index++)
        {
            var paidInvoice = new XElement(template);
            paidInvoice.SetAttributeValue("IdDocumento", Guid.NewGuid());
            payment.Add(paidInvoice);
        }
        for (var index = 0; index < 3; index++) document.Root!.Add(new XElement(SyntheticCfdi.Cfdi + "CfdiRelacionados",
            new XAttribute("TipoRelacion", "04"), new XElement(SyntheticCfdi.Cfdi + "CfdiRelacionado", new XAttribute("UUID", Guid.NewGuid()))));
        var report = await app.Services.GetRequiredService<CfdiImporter>().ImportAsync(company, user.Id,
            [UploadedFile.FromBytes("batched.xml", Encoding.UTF8.GetBytes(document.ToString()))], TestContext.Current.CancellationToken);
        Assert.Equal("imported", report.Files[0].Status);
        Assert.Equal(1, probe.DocumentInserts);
        Assert.Equal(1, probe.RelationInserts);
    }

    [Fact]
    public async Task Failed_statement_rolls_back_placeholder_account_and_movements()
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync(new CommandDefinition("REVOKE EXECUTE ON FUNCTION append_audit_entry(uuid,text,uuid,text,jsonb,jsonb) FROM cfdi_app", cancellationToken: TestContext.Current.CancellationToken));
        try
        {
            await Assert.ThrowsAsync<UploadFailedException>(() => fixture.App.Services.GetRequiredService<BankStatementImporter>().ImportAsync(company, user.Id, "reference", "Synthetic-1234",
                [UploadedFile.FromBytes("rollback.csv", Encoding.UTF8.GetBytes("fecha,concepto,abono,cargo,saldo\n2026-10-10,SYNTHETIC,1,0,1\n"))], TestContext.Current.CancellationToken));
            foreach (var table in new[] { "import_file", "bank_movement", "bank_account" })
                Assert.Equal(0, await owner.ExecuteScalarAsync<int>(new CommandDefinition($"SELECT count(*) FROM {table} WHERE company_id=@company", new { company }, cancellationToken: TestContext.Current.CancellationToken)));
        }
        finally { await owner.ExecuteAsync(new CommandDefinition("GRANT EXECUTE ON FUNCTION append_audit_entry(uuid,text,uuid,text,jsonb,jsonb) TO cfdi_app", cancellationToken: CancellationToken.None)); }
    }

    [Fact]
    public async Task Committed_rejected_original_cannot_be_replaced_and_finalization_still_checks_hash()
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        var report = await fixture.App.Services.GetRequiredService<CfdiImporter>().ImportAsync(company, user.Id,
            [UploadedFile.FromBytes("rejected.xml", Encoding.UTF8.GetBytes("<invalid/>"))], TestContext.Current.CancellationToken);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await Assert.ThrowsAsync<PostgresException>(() => owner.ExecuteAsync(new CommandDefinition("""
            UPDATE import_file SET status='imported', original='\x01', sha256=sha256('\x01'::bytea), retained_until=now()+interval '10 years'
            WHERE id=@FileId
            """, new { report.Files[0].FileId }, cancellationToken: TestContext.Current.CancellationToken)));
        await using var transaction = await owner.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var pending = await owner.QuerySingleAsync<Guid>(new CommandDefinition("""
            INSERT INTO import_file(company_id,upload_id,filename,kind,status,sha256)
            VALUES(@company,gen_random_uuid(),'pending.csv','bank-statement','processing',sha256('\x02'::bytea)) RETURNING id
            """, new { company }, transaction, cancellationToken: TestContext.Current.CancellationToken));
        var error = await Assert.ThrowsAsync<PostgresException>(() => owner.ExecuteAsync(new CommandDefinition("""
            UPDATE import_file SET status='imported', original='\x01', retained_until=now()+interval '10 years' WHERE id=@pending
            """, new { pending }, transaction, cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    private sealed class QueryProbe : ILoggerProvider
    {
        public int MovementInserts { get; private set; }
        public int DocumentInserts { get; private set; }
        public int RelationInserts { get; private set; }
        public ILogger CreateLogger(string categoryName) => new QueryLogger(this, categoryName);
        public void Dispose() { }
        private sealed class QueryLogger(QueryProbe probe, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (category != "Npgsql.Command" || logLevel < LogLevel.Information) return;
                var command = formatter(state, exception);
                if (command.Contains("INSERT INTO bank_movement", StringComparison.Ordinal)) probe.MovementInserts++;
                if (command.Contains("INSERT INTO cfdi_payment_doc", StringComparison.Ordinal)) probe.DocumentInserts++;
                if (command.Contains("INSERT INTO cfdi_relation", StringComparison.Ordinal)) probe.RelationInserts++;
            }
        }
    }
}
