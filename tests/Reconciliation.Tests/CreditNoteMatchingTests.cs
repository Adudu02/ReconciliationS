using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentMigrator.Runner;
using FsCheck.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Reconciliation.Core.Ingestion;
using Reconciliation.Core.Matching;
using Reconciliation.Core.Tenancy;
using Reconciliation.Migrator.Migrations;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class CreditNoteMatchingTests(ApiFixture fixture)
{
    [Fact(DisplayName = "Credit note applied")]
    public async Task Credit_note_nets_payable_for_worker_capacity_derived_state_and_read_API()
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture);
        await MatchingFiscalScenario.ImportNoteAsync(fixture, seed, 1160);
        var payment = SyntheticCfdi.Payment(seed.Uuid, remaining: "0.00", previous: "10440.00", paid: "10440.00");
        await fixture.App.Services.GetRequiredService<CfdiImporter>().ImportAsync(seed.Company, seed.Actor,
            [UploadedFile.FromBytes("synthetic-payment.xml", payment)], TestContext.Current.CancellationToken);
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        await MatchingRunScenario.RunAsync(fixture, client, seed.Company);
        var invoice = await client.GetFromJsonAsync<JsonElement>($"/companies/{seed.Company}/cfdi/{seed.Invoice}", TestContext.Current.CancellationToken);
        var matching = invoice.GetProperty("matching");
        Assert.Equal(11600, invoice.GetProperty("total").GetDecimal());
        Assert.Equal(1160, matching.GetProperty("creditNotes").GetDecimal());
        Assert.Equal(10440, matching.GetProperty("payable").GetDecimal());
        Assert.Equal("reconciled", matching.GetProperty("state").GetString());
        Assert.Equal(0, matching.GetProperty("pendingAmount").GetDecimal());
        Assert.Equal(100, matching.GetProperty("paidPercentage").GetDecimal());
    }

    [Fact]
    public void Engine_uses_net_payable_for_total_gross_remaining_and_capacity()
    {
        var invoice = SyntheticMatching.Invoice(11600) with { CreditNotes = 1160 };
        var movement = SyntheticMatching.FirstMovement(10440);
        var reconciled = MatchingEngine.Run([movement], [invoice], [SyntheticMatching.Payment(10440)], [], new(3, .01m));
        Assert.Equal(10440, Assert.Single(reconciled.Changes).Amount);
        Assert.Equal(0, Assert.Single(reconciled.Invoices).PendingAmount);
        var heuristic = Assert.Single(MatchingEngine.Run([movement], [invoice], [], [], new(3, .01m)).Changes);
        Assert.Contains(heuristic.Criteria, criterion => criterion.Code == "amount-equals-total" && criterion.Points == 40);
        var grossInvoice = invoice with { Retained = 100 };
        var gross = Assert.Single(MatchingEngine.Run([movement with { Credit = 10540 }], [grossInvoice], [], [], new(3, .01m)).Changes);
        Assert.Equal(10440, gross.Amount);
        Assert.Contains(gross.Criteria, criterion => criterion.Code == "gross-payment" && criterion.Points == 35);
        var first = new MatchingLink(Guid.NewGuid(), invoice.Id, null, null, 5000, "reconciled", "user", [], null, "Synthetic confirmed payment");
        var remainder = Assert.Single(MatchingEngine.Run([movement with { Credit = 5440 }], [invoice], [], [first], new(3, .01m)).Changes);
        Assert.Contains(remainder.Criteria, criterion => criterion.Code == "amount-equals-remaining");
        Assert.Equal(5440, remainder.Amount);
    }

    [Theory]
    [InlineData("01", "unknown", "MXN", true, 1160)]
    [InlineData("03", "unknown", "MXN", true, 0)]
    [InlineData("04", "unknown", "MXN", true, 0)]
    [InlineData("01", "cancelled", "MXN", true, 0)]
    [InlineData("01", "unknown", "USD", true, 0)]
    [InlineData("01", "unknown", "MXN", false, 0)]
    public async Task Only_valid_same_currency_issued_code01_notes_are_netted(string code, string status, string currency, bool issued, decimal expected)
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture);
        var note = await MatchingFiscalScenario.ImportNoteAsync(fixture, seed, 1160, code, currency, issued);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync(new CommandDefinition("UPDATE cfdi SET sat_status=@status WHERE id=@note", new { status, note }, cancellationToken: TestContext.Current.CancellationToken));
        var values = await owner.QuerySingleAsync<(decimal Notes, decimal Payable)>(new CommandDefinition("SELECT credit_notes,payable FROM cfdi_payable() WHERE id=@invoice", new { invoice = seed.Invoice }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal((expected, 11600m - expected), values);
    }

    [Fact]
    public async Task Fully_credited_invoice_has_no_amount_due_and_only_an_informational_reason()
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture);
        await MatchingFiscalScenario.ImportNoteAsync(fixture, seed, 11600);
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        await MatchingRunScenario.RunAsync(fixture, client, seed.Company);
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM link WHERE company_id=@company", new { company = seed.Company }));
        var invoice = await client.GetFromJsonAsync<JsonElement>($"/companies/{seed.Company}/cfdi/{seed.Invoice}", TestContext.Current.CancellationToken);
        var matching = invoice.GetProperty("matching");
        Assert.Equal("pending", matching.GetProperty("state").GetString());
        Assert.Equal(0, matching.GetProperty("payable").GetDecimal());
        Assert.Equal(0, matching.GetProperty("pendingAmount").GetDecimal());
        Assert.Equal(0, matching.GetProperty("uncoveredAmount").GetDecimal());
        Assert.False(Assert.Single(matching.GetProperty("reasons").EnumerateArray(), reason => reason.GetProperty("code").GetString() == "fully credited").GetProperty("requiresReview").GetBoolean());
        Assert.False(matching.GetProperty("requiresReview").GetBoolean());
    }

    [Property(MaxTest = 150)]
    public void Net_payable_is_never_negative(int totalCents, int noteCents)
    {
        var total = Math.Abs((long)totalCents) / 100m;
        var notes = Math.Abs((long)noteCents) / 100m;
        var invoice = SyntheticMatching.Invoice(total) with { CreditNotes = notes };
        Assert.InRange(invoice.Payable, 0, total);
        if (notes >= total) Assert.Equal(0, invoice.Payable);
    }

    [Fact]
    public async Task M0010_up_down_up_preserves_populated_links_and_audit_and_guards_derived_reads()
    {
        await using var database = new PostgresFixture();
        await database.InitializeAsync();
        using var services = new ServiceCollection().AddFluentMigratorCore().ConfigureRunner(r => r.AddPostgres()
            .WithGlobalConnectionString(database.OwnerConnectionString).ScanIn(typeof(M0001CompanyUserMembership).Assembly).For.Migrations()).AddLogging().BuildServiceProvider(validateScopes: false);
        using var serviceScope = services.CreateScope();
        var runner = serviceScope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        await using var owner = await database.OpenOwnerAsync();
        Assert.Equal(13, await owner.ExecuteScalarAsync<long>(new CommandDefinition("SELECT max(\"Version\") FROM \"VersionInfo\"", cancellationToken: TestContext.Current.CancellationToken)));
        var company = await owner.ExecuteScalarAsync<Guid>(new CommandDefinition("INSERT INTO company(rfc,name) VALUES('AAA010101AAA','Synthetic fiscal migration') RETURNING id", cancellationToken: TestContext.Current.CancellationToken));
        await owner.ExecuteAsync(new CommandDefinition("SELECT set_config('app.company_id',@company,false); SELECT append_audit_entry(@id,'system',NULL,'synthetic.migration','[]','{\"source\":\"migration-test\"}')", new { company = company.ToString(), id = company }, cancellationToken: TestContext.Current.CancellationToken));
        var link = await owner.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            WITH file AS (
              INSERT INTO import_file(company_id,upload_id,filename,kind,status,reason,sha256)
              VALUES(@company,gen_random_uuid(),'synthetic.xml','cfdi','rejected','Synthetic migration fixture',sha256('synthetic'::bytea)) RETURNING id
            ), invoice AS (
              INSERT INTO cfdi(company_id,import_file_id,uuid,type,direction,issuer_rfc,receiver_rfc,issued_at,stamped_at,currency,subtotal,total)
              SELECT @company,id,gen_random_uuid(),'I','issued','AAA010101AAA','BBB010101BBB','2026-10-10','2026-10-10','MXN',11600,11600 FROM file RETURNING id
            ), account AS (
              INSERT INTO bank_account(company_id,bank,account_reference) VALUES(@company,'reference','Synthetic-Migration') RETURNING id
            ), movement AS (
              INSERT INTO bank_movement(company_id,bank_account_id,movement_date,description,credit,debit,balance)
              SELECT @company,id,'2026-10-10','Synthetic migration movement',10440,0,10440 FROM account RETURNING id
            )
            INSERT INTO link(company_id,movement_id,cfdi_id,amount_applied,state,origin,criteria,explanation)
            SELECT @company,movement.id,invoice.id,10440,'reconciled','user','[]','Synthetic migration link' FROM invoice,movement RETURNING id
            """, new { company }, cancellationToken: TestContext.Current.CancellationToken));
        var hash = await owner.ExecuteScalarAsync<string>(new CommandDefinition("SELECT encode(hash,'hex') FROM audit_entry WHERE company_id=@company", new { company }, cancellationToken: TestContext.Current.CancellationToken));
        runner.MigrateDown(9);
        Assert.False(await database.ScalarAsync<bool>("SELECT to_regprocedure('cfdi_payable()') IS NOT NULL"));
        runner.MigrateUp();
        Assert.Equal((10440m, "reconciled"), await owner.QuerySingleAsync<(decimal, string)>(new CommandDefinition("SELECT amount_applied,state FROM link WHERE company_id=@company AND id=@link", new { company, link }, cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Equal(hash, await owner.ExecuteScalarAsync<string>(new CommandDefinition("SELECT encode(hash,'hex') FROM audit_entry WHERE company_id=@company", new { company }, cancellationToken: TestContext.Current.CancellationToken)));
        await owner.ExecuteAsync(new CommandDefinition("UPDATE cfdi SET sat_status='cancelled' WHERE company_id=@company; UPDATE link SET state='suggested',review_reason='cancelled invoice' WHERE id=@link", new { company, link }, cancellationToken: TestContext.Current.CancellationToken));
        runner.MigrateDown(9);
        runner.MigrateUp();
        Assert.Equal("cancelled invoice", await owner.ExecuteScalarAsync<string>(new CommandDefinition("SELECT review_reason FROM link WHERE id=@link", new { link }, cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Equal(hash, await owner.ExecuteScalarAsync<string>(new CommandDefinition("SELECT encode(hash,'hex') FROM audit_entry WHERE company_id=@company", new { company }, cancellationToken: TestContext.Current.CancellationToken)));
        await using var app = new NpgsqlConnection(database.AppConnectionString);
        await app.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Empty(await app.QueryAsync<Guid>(new CommandDefinition("SELECT id FROM cfdi_payable()", cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Empty(await app.QueryAsync<Guid>(new CommandDefinition("SELECT id FROM cfdi_review_reasons('2026-11-06')", cancellationToken: TestContext.Current.CancellationToken)));
        await TenantIsolationChecks.VerifyTablesAsync(owner);
    }
}
