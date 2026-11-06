using System.Text;
using Dapper;
using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Reconciliation.Core.Ingestion;
using Reconciliation.Migrator.Migrations;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class MatchingModelTests(ApiFixture fixture)
{
    internal async Task<(Guid Company, Guid Actor, Guid Invoice, Guid Movement, Guid OtherMovement)> SeedAsync()
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        var invoice = await fixture.App.Services.GetRequiredService<CfdiImporter>().ImportAsync(company, user.Id,
            [UploadedFile.FromBytes("synthetic.xml", SyntheticCfdi.Invoice())], TestContext.Current.CancellationToken);
        await fixture.App.Services.GetRequiredService<BankStatementImporter>().ImportAsync(company, user.Id, "reference", "Synthetic-Matching",
            [UploadedFile.FromBytes("synthetic.csv", Encoding.UTF8.GetBytes("fecha,concepto,abono,cargo,saldo\n2026-10-10,SYNTHETIC-ONE,5800,0,5800\n2026-10-20,SYNTHETIC-TWO,5800,0,11600\n"))], TestContext.Current.CancellationToken);
        var id = invoice.Files[0].CfdiId!.Value;
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync(new CommandDefinition("UPDATE cfdi SET subtotal=10000,discount=0,transferred_tax=1600,retained_tax=0,local_transferred_tax=0,local_retained_tax=0,total=11600 WHERE id=@id", new { id }, cancellationToken: TestContext.Current.CancellationToken));
        var movements = (await owner.QueryAsync<Guid>(new CommandDefinition("SELECT id FROM bank_movement WHERE company_id=@company ORDER BY movement_date", new { company }, cancellationToken: TestContext.Current.CancellationToken))).ToArray();
        return (company, user.Id, id, movements[0], movements[1]);
    }

    internal static Task<int> LinkAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid company, Guid movement, Guid invoice, decimal amount, string state) =>
        connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO link(company_id,movement_id,cfdi_id,amount_applied,state,origin,criteria,explanation)
            VALUES(@company,@movement,@invoice,@amount,@state,'automatic','[]','Synthetic test allocation')
            """, new { company, movement, invoice, amount, state }, transaction, cancellationToken: TestContext.Current.CancellationToken));

    [Fact(DisplayName = "Partial payments on one invoice / Invoice partially paid")]
    public async Task Invoice_state_and_balances_are_derived_only_from_active_links()
    {
        var seed = await SeedAsync();
        await using var app = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await app.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = await app.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await AuditDatabaseTests.SetContextAsync(app, tx, seed.Company);
        Assert.Equal("pending", await app.ExecuteScalarAsync<string>(new CommandDefinition("SELECT state FROM cfdi_state() WHERE id=@id", new { id = seed.Invoice }, tx, cancellationToken: TestContext.Current.CancellationToken)));
        await LinkAsync(app, tx, seed.Company, seed.Movement, seed.Invoice, 5800, "reconciled");
        await LinkAsync(app, tx, seed.Company, seed.OtherMovement, seed.Invoice, 5800, "suggested");
        var partial = await app.QuerySingleAsync<(string State, decimal Paid, decimal Pending, decimal Uncovered, decimal Percentage)>(new CommandDefinition(
            "SELECT state,reconciled_amount,pending_amount,uncovered_amount,paid_percentage FROM cfdi_state() WHERE id=@id", new { id = seed.Invoice }, tx, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(("suggested", 5800m, 5800m, 0m, 50m), partial);
        await app.ExecuteAsync(new CommandDefinition("UPDATE link SET state='reconciled' WHERE company_id=@company AND movement_id=@movement", new { company = seed.Company, movement = seed.OtherMovement }, tx, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("reconciled", await app.ExecuteScalarAsync<string>(new CommandDefinition("SELECT state FROM cfdi_state() WHERE id=@id", new { id = seed.Invoice }, tx, cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Equal(["reconciled", "reconciled"], await app.QueryAsync<string>(new CommandDefinition("SELECT state FROM movement_state() ORDER BY id", transaction: tx, cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Fact(DisplayName = "One payment covering two invoices / Movement with no candidate")]
    public async Task Movement_state_sums_multiple_invoices_and_ignores_closed_and_rejected_links()
    {
        var seed = await SeedAsync();
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await using var tx = await owner.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await AuditDatabaseTests.SetContextAsync(owner, tx, seed.Company);
        await owner.ExecuteAsync(new CommandDefinition("UPDATE bank_movement SET credit=10000 WHERE id=@id", new { id = seed.Movement }, tx, cancellationToken: TestContext.Current.CancellationToken));
        var second = await owner.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO cfdi(company_id,import_file_id,uuid,type,direction,issuer_rfc,receiver_rfc,issued_at,stamped_at,currency,subtotal,total)
            SELECT company_id,import_file_id,gen_random_uuid(),type,direction,issuer_rfc,receiver_rfc,issued_at,stamped_at,currency,4000,4000 FROM cfdi WHERE id=@id RETURNING id
            """, new { id = seed.Invoice }, tx, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("pending", await owner.ExecuteScalarAsync<string>(new CommandDefinition("SELECT state FROM movement_state() WHERE id=@id", new { id = seed.Movement }, tx, cancellationToken: TestContext.Current.CancellationToken)));
        await LinkAsync(owner, tx, seed.Company, seed.Movement, seed.Invoice, 6000, "reconciled");
        await LinkAsync(owner, tx, seed.Company, seed.Movement, second, 4000, "reconciled");
        Assert.Equal("reconciled", await owner.ExecuteScalarAsync<string>(new CommandDefinition("SELECT state FROM movement_state() WHERE id=@id", new { id = seed.Movement }, tx, cancellationToken: TestContext.Current.CancellationToken)));
        await owner.ExecuteAsync(new CommandDefinition("UPDATE link SET state='closed' WHERE cfdi_id=@id", new { id = second }, tx, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("pending", await owner.ExecuteScalarAsync<string>(new CommandDefinition("SELECT state FROM movement_state() WHERE id=@id", new { id = seed.Movement }, tx, cancellationToken: TestContext.Current.CancellationToken)));
        await owner.ExecuteAsync(new CommandDefinition("UPDATE link SET state='rejected' WHERE cfdi_id=@id", new { id = seed.Invoice }, tx, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0m, await owner.ExecuteScalarAsync<decimal>(new CommandDefinition("SELECT reconciled_amount+suggested_amount FROM movement_state() WHERE id=@id", new { id = seed.Movement }, tx, cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Link_has_forced_RLS_composite_FKs_null_safe_uniqueness_and_no_delete_grant()
    {
        var seed = await SeedAsync();
        var other = await SeedAsync();
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await TenantIsolationChecks.VerifyTablesAsync(owner);
        Assert.False(await fixture.Database.ScalarAsync<bool>("SELECT has_table_privilege('cfdi_app','link','DELETE')"));
        await using var app = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await app.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Empty(await app.QueryAsync<Guid>(new CommandDefinition("SELECT id FROM link", cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Empty(await app.QueryAsync<Guid>(new CommandDefinition("SELECT id FROM cfdi_state()", cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Empty(await app.QueryAsync<Guid>(new CommandDefinition("SELECT id FROM movement_state()", cancellationToken: TestContext.Current.CancellationToken)));
        await using var tx = await app.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await AuditDatabaseTests.SetContextAsync(app, tx, seed.Company);
        await LinkAsync(app, tx, seed.Company, seed.Movement, seed.Invoice, 1, "rejected");
        Assert.Single(await app.QueryAsync<Guid>(new CommandDefinition("SELECT id FROM link", transaction: tx, cancellationToken: TestContext.Current.CancellationToken)));
        await LinkAsync(app, tx, seed.Company, seed.Movement, seed.Invoice, 1, "suggested");
        Assert.Equal(2, await app.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM link", transaction: tx, cancellationToken: TestContext.Current.CancellationToken)));
        await tx.SaveAsync("before_failure", TestContext.Current.CancellationToken);
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => LinkAsync(app, tx, seed.Company, seed.Movement, seed.Invoice, 1, "suggested"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        await tx.RollbackAsync("before_failure", TestContext.Current.CancellationToken);
        var foreign = await Assert.ThrowsAsync<PostgresException>(() => LinkAsync(app, tx, seed.Company, other.Movement, seed.Invoice, 1, "suggested"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, foreign.SqlState);
        await tx.RollbackAsync("before_failure", TestContext.Current.CancellationToken);
        var crossCompany = await Assert.ThrowsAsync<PostgresException>(() => LinkAsync(app, tx, other.Company, other.Movement, other.Invoice, 1, "suggested"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, crossCompany.SqlState);
    }

    [Fact]
    public async Task M0009_up_down_up_preserves_ingestion_and_restores_the_link_schema()
    {
        Assert.True(await fixture.Database.ScalarAsync<bool>("SELECT to_regclass('link') IS NOT NULL"));
        await using var database = new PostgresFixture();
        await database.InitializeAsync();
        using var services = new ServiceCollection().AddFluentMigratorCore().ConfigureRunner(r => r.AddPostgres()
            .WithGlobalConnectionString(database.OwnerConnectionString).ScanIn(typeof(M0001CompanyUserMembership).Assembly).For.Migrations()).AddLogging().BuildServiceProvider(validateScopes: false);
        using var scope = services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        await using var owner = await database.OpenOwnerAsync();
        var company = await owner.ExecuteScalarAsync<Guid>(new CommandDefinition("INSERT INTO company(rfc,name) VALUES('AAA010101AAA','Synthetic migration') RETURNING id", cancellationToken: TestContext.Current.CancellationToken));
        await owner.ExecuteAsync(new CommandDefinition("""
            INSERT INTO import_file(company_id,upload_id,filename,kind,status,reason,sha256)
            VALUES(@company,gen_random_uuid(),'synthetic.xml','cfdi','rejected','Synthetic invalid input',sha256(convert_to('synthetic','UTF8')))
            """, new { company }, cancellationToken: TestContext.Current.CancellationToken));
        runner.MigrateDown(8);
        Assert.False(await database.ScalarAsync<bool>("SELECT to_regclass('link') IS NOT NULL"));
        runner.MigrateUp();
        Assert.True(await database.ScalarAsync<bool>("SELECT to_regclass('link') IS NOT NULL"));
        Assert.Equal(1, await database.ScalarAsync<int>("SELECT count(*) FROM import_file WHERE company_id=@company", new { company }));
    }
}
