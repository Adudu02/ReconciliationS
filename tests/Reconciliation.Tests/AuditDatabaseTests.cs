using Dapper;
using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Reconciliation.Migrator.Migrations;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class AuditDatabaseTests(ApiFixture fixture)
{
    internal const string AppendSignature = "append_audit_entry(uuid,text,uuid,text,jsonb,jsonb)";
    internal static Task<long> AppendAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid company, Guid actor,
        string action = "synthetic.action", string items = "{}", string detail = "{}") => connection.QuerySingleAsync<long>(new CommandDefinition(
            "SELECT company_seq FROM append_audit_entry(@company,'user',@actor,@action,@items::jsonb,@detail::jsonb)",
            new { company, actor, action, items, detail }, transaction, cancellationToken: TestContext.Current.CancellationToken));

    [Theory]
    [InlineData(false, "UPDATE audit_entry SET action='changed'")]
    [InlineData(false, "DELETE FROM audit_entry")]
    [InlineData(false, "TRUNCATE audit_entry")]
    [InlineData(true, "UPDATE audit_entry SET action='changed'")]
    [InlineData(true, "DELETE FROM audit_entry")]
    [InlineData(true, "TRUNCATE audit_entry")]
    public async Task Application_and_table_owner_cannot_modify_entries(bool tableOwner, string sql)
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await using (var tx = await owner.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await SetContextAsync(owner, tx, company);
            await AppendAsync(owner, tx, company, user.Id);
            await tx.CommitAsync(TestContext.Current.CancellationToken);
        }
        await using var connection = new NpgsqlConnection(tableOwner ? fixture.Database.OwnerConnectionString : fixture.Database.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await SetContextAsync(connection, transaction, company);
        var error = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(new CommandDefinition(sql, transaction: transaction, cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Equal(tableOwner ? PostgresErrorCodes.RaiseException : PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        if (tableOwner) Assert.Contains("append-only", error.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Append_requires_matching_context_and_application_has_only_select_on_audit_tables()
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        var other = await fixture.SeedCompanyAsync(user.Id);
        await using var app = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await app.OpenAsync(TestContext.Current.CancellationToken);
        foreach (var context in new Guid?[] { null, other, company })
        {
            await using var transaction = await app.BeginTransactionAsync(TestContext.Current.CancellationToken);
            if (context.HasValue) await SetContextAsync(app, transaction, context.Value);
            if (context == company) Assert.Equal(1, await AppendAsync(app, transaction, company, user.Id));
            else
            {
                var error = await Assert.ThrowsAsync<PostgresException>(() => AppendAsync(app, transaction, company, user.Id));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
            }
        }
        foreach (var table in new[] { "audit_entry", "audit_chain_head" })
        {
            Assert.True(await fixture.Database.ScalarAsync<bool>("SELECT has_table_privilege('cfdi_app',@table,'SELECT')", new { table }));
            Assert.False(await fixture.Database.ScalarAsync<bool>("SELECT has_table_privilege('cfdi_app',@table,'INSERT,UPDATE,DELETE,TRUNCATE')", new { table }));
        }
        await using (var transaction = await app.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await SetContextAsync(app, transaction, company);
            await Assert.ThrowsAsync<PostgresException>(() => app.ExecuteAsync(new CommandDefinition(
                "INSERT INTO audit_entry(company_id,actor_id,action) VALUES(@company,@actor,'direct')", new { company, actor = user.Id }, transaction, cancellationToken: TestContext.Current.CancellationToken)));
        }
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await AssertAuditRoleBoundaryAsync(owner);
        await TenantIsolationChecks.VerifyTablesAsync(owner);
    }

    internal static async Task AssertAuditRoleBoundaryAsync(NpgsqlConnection owner)
    {
        Assert.True(await owner.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT NOT rolcanlogin AND NOT rolsuper AND NOT rolbypassrls AND NOT rolcreatedb AND NOT rolcreaterole FROM pg_roles WHERE rolname='cfdi_audit_owner'", cancellationToken: TestContext.Current.CancellationToken)));
        Assert.False(await owner.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT pg_has_role('cfdi_app','cfdi_audit_owner','MEMBER')", cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Empty(await owner.QueryAsync<string>(new CommandDefinition("""
            SELECT tablename FROM pg_tables WHERE schemaname='public' AND tablename NOT IN ('audit_entry','audit_chain_head')
              AND has_table_privilege('cfdi_audit_owner',format('%I.%I',schemaname,tablename),'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
            """, cancellationToken: TestContext.Current.CancellationToken)));
        var metadata = await owner.QuerySingleAsync<(bool Definer, string Owner, string[] Settings, bool AppExecute, bool PublicExecute)>(new CommandDefinition("""
            SELECT p.prosecdef,r.rolname,p.proconfig,has_function_privilege('cfdi_app',p.oid,'EXECUTE'),
              EXISTS(SELECT FROM aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) a WHERE a.grantee=0 AND a.privilege_type='EXECUTE')
            FROM pg_proc p JOIN pg_roles r ON r.oid=p.proowner WHERE p.proname='append_audit_entry'
            """, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(metadata.Definer && metadata.AppExecute && !metadata.PublicExecute);
        Assert.Equal("cfdi_audit_owner", metadata.Owner);
        Assert.Contains("search_path=pg_catalog, public, pg_temp", metadata.Settings);
    }

    [Fact]
    public async Task M0008_up_down_up_preserves_and_chains_existing_rows_in_created_at_id_order()
    {
        await using var database = new PostgresFixture();
        await database.InitializeAsync();
        using var services = new ServiceCollection().AddFluentMigratorCore().ConfigureRunner(runner => runner.AddPostgres()
            .WithGlobalConnectionString(database.OwnerConnectionString).ScanIn(typeof(M0001CompanyUserMembership).Assembly).For.Migrations())
            .AddLogging().BuildServiceProvider(validateScopes: false);
        using var scope = services.CreateScope();
        var migrations = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        migrations.MigrateDown(7);
        await using var owner = await database.OpenOwnerAsync();
        var company = await owner.ExecuteScalarAsync<Guid>(new CommandDefinition("INSERT INTO company(rfc,name) VALUES('AAA010101AAA','Synthetic backfill') RETURNING id", cancellationToken: TestContext.Current.CancellationToken));
        var actor = await owner.ExecuteScalarAsync<Guid>(new CommandDefinition("INSERT INTO app_user(email,password_hash) VALUES('backfill@example.invalid','synthetic') RETURNING id", cancellationToken: TestContext.Current.CancellationToken));
        await owner.ExecuteAsync(new CommandDefinition("""
            INSERT INTO audit_entry(id,company_id,actor_id,action,old_values,new_values,created_at)
            VALUES ('00000000-0000-0000-0000-000000000002',@company,@actor,'second',NULL,'{"n":2}','2026-01-01Z'),
                   ('00000000-0000-0000-0000-000000000001',@company,@actor,'first','{"n":0}','{"n":1}','2026-01-01Z');
            """, new { company, actor }, cancellationToken: TestContext.Current.CancellationToken));
        for (var pass = 0; pass < 2; pass++)
        {
            migrations.MigrateUp();
            Assert.Equal(["first", "second"], await owner.QueryAsync<string>(new CommandDefinition("SELECT action FROM audit_entry ORDER BY company_seq", cancellationToken: TestContext.Current.CancellationToken)));
            Assert.Equal([1L, 2L], await owner.QueryAsync<long>(new CommandDefinition("SELECT company_seq FROM audit_entry ORDER BY company_seq", cancellationToken: TestContext.Current.CancellationToken)));
            Assert.True(await owner.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT bool_and(hash=sha256(convert_to(canonical,'UTF8') || prev_hash)) FROM audit_entry
                """, cancellationToken: TestContext.Current.CancellationToken)));
            Assert.True(await owner.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT h.last_seq=2 AND h.last_hash=e.hash FROM audit_chain_head h JOIN audit_entry e ON e.company_id=h.company_id AND e.company_seq=2", cancellationToken: TestContext.Current.CancellationToken)));
            Assert.True(await owner.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT detail='{" + "\"old\":{\"n\":0},\"new\":{\"n\":1}}'::jsonb AND old_values=detail->'old' AND new_values=detail->'new' FROM audit_entry WHERE company_seq=1", cancellationToken: TestContext.Current.CancellationToken)));
            await AssertAuditRoleBoundaryAsync(owner);
            if (pass == 0) migrations.MigrateDown(7);
        }
    }

    internal static Task SetContextAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid company) => connection.ExecuteAsync(new CommandDefinition(
        "SELECT set_config('app.company_id',@company,true)", new { company = company.ToString() }, transaction, cancellationToken: TestContext.Current.CancellationToken));
}
