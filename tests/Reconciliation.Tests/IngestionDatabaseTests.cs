using Dapper;
using Npgsql;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class IngestionDatabaseTests(ApiFixture fixture)
{
    [Fact]
    public async Task Domain_tables_exist_and_invoice_UUID_is_unique_within_company()
    {
        var user = await fixture.SeedUserAsync();
        var a = await fixture.SeedCompanyAsync(user.Id);
        var b = await fixture.SeedCompanyAsync(user.Id);
        await using var owner = new NpgsqlConnection(fixture.Database.OwnerConnectionString);
        foreach (var table in new[] { "import_file", "cfdi", "cfdi_relation", "cfdi_payment", "cfdi_payment_doc", "bank_account", "bank_movement" })
            Assert.True(await owner.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT to_regclass(@table) IS NOT NULL", new { table }, cancellationToken: TestContext.Current.CancellationToken)), table);
        var uuid = Guid.NewGuid();
        await InsertInvoiceAsync(owner, a, uuid);
        var error = await Assert.ThrowsAsync<PostgresException>(() => InsertInvoiceAsync(owner, a, uuid));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        await InsertInvoiceAsync(owner, b, uuid);
        await TenantIsolationChecks.VerifyTablesAsync(owner);
    }

    [Fact]
    public async Task Movement_uniqueness_preserves_different_balances_and_money_is_cent_exact()
    {
        var user = await fixture.SeedUserAsync();
        var company = await fixture.SeedCompanyAsync(user.Id);
        await using var owner = new NpgsqlConnection(fixture.Database.OwnerConnectionString);
        var account = await owner.QuerySingleAsync<Guid>(new CommandDefinition("INSERT INTO bank_account(company_id, bank, account_reference) VALUES (@company, 'reference', 'Synthetic-1234') RETURNING id", new { company }, cancellationToken: TestContext.Current.CancellationToken));
        const string insert = "INSERT INTO bank_movement(company_id, bank_account_id, movement_date, description, credit, debit, balance) VALUES (@company, @account, '2026-10-10', 'Synthetic', @amount, 0, @balance)";
        await owner.ExecuteAsync(new CommandDefinition(insert, new { company, account, amount = 0.10m, balance = 0.10m }, cancellationToken: TestContext.Current.CancellationToken));
        var error = await Assert.ThrowsAsync<PostgresException>(() => owner.ExecuteAsync(new CommandDefinition(insert, new { company, account, amount = 0.10m, balance = 0.10m }, cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        await owner.ExecuteAsync(new CommandDefinition(insert, new { company, account, amount = 0.20m, balance = 0.30m }, cancellationToken: TestContext.Current.CancellationToken));
        var amounts = (await owner.QueryAsync<decimal>(new CommandDefinition("SELECT credit FROM bank_movement WHERE company_id = @company", new { company }, cancellationToken: TestContext.Current.CancellationToken))).ToArray();
        Assert.Equal(0.30m, amounts.Sum());
        Assert.Equal(0.30m, await owner.ExecuteScalarAsync<decimal>(new CommandDefinition("SELECT sum(credit) FROM bank_movement WHERE company_id = @company", new { company }, cancellationToken: TestContext.Current.CancellationToken)));
        await owner.ExecuteAsync(new CommandDefinition(insert, new { company, account, amount = 0.10m, balance = 0.40m }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(3, await owner.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM bank_movement WHERE company_id = @company", new { company }, cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Cross_company_foreign_keys_and_imported_data_deletion_are_refused()
    {
        var user = await fixture.SeedUserAsync();
        var a = await fixture.SeedCompanyAsync(user.Id);
        var b = await fixture.SeedCompanyAsync(user.Id);
        await using var owner = new NpgsqlConnection(fixture.Database.OwnerConnectionString);
        var invoice = await InsertInvoiceAsync(owner, b, Guid.NewGuid());
        var error = await Assert.ThrowsAsync<PostgresException>(() => owner.ExecuteAsync(new CommandDefinition("INSERT INTO cfdi_relation(company_id, cfdi_id, related_uuid, relation_code) VALUES (@a, @invoice, gen_random_uuid(), '04')", new { a, invoice }, cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        await using var app = new NpgsqlConnection(fixture.Database.AppConnectionString);
        foreach (var table in new[] { "cfdi", "import_file", "bank_movement" })
            Assert.False(await app.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT has_table_privilege(current_user, @table, 'DELETE')", new { table }, cancellationToken: TestContext.Current.CancellationToken)));
    }

    private static async Task<Guid> InsertInvoiceAsync(NpgsqlConnection owner, Guid company, Guid uuid)
    {
        var file = await owner.QuerySingleAsync<Guid>(new CommandDefinition("""
            INSERT INTO import_file(company_id, upload_id, filename, kind, status, original, sha256, retained_until)
            VALUES (@company, gen_random_uuid(), 'synthetic.xml', 'cfdi', 'imported', '\x0102', sha256('\x0102'::bytea), now() + interval '10 years') RETURNING id
            """, new { company }, cancellationToken: TestContext.Current.CancellationToken));
        return await owner.QuerySingleAsync<Guid>(new CommandDefinition("""
            INSERT INTO cfdi(company_id, import_file_id, uuid, type, direction, issuer_rfc, receiver_rfc, issued_at, stamped_at, currency, subtotal, total)
            VALUES (@company, @file, @uuid, 'I', 'issued', 'AAA010101AAA', 'BBB010101BBB', '2026-10-10T00:00:00', '2026-10-10T00:01:00', 'MXN', 100, 100) RETURNING id
            """, new { company, file, uuid }, cancellationToken: TestContext.Current.CancellationToken));
    }
}
