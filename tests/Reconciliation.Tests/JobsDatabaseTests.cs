using Dapper;
using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Reconciliation.Migrator.Migrations;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class JobsDatabaseTests(ApiFixture fixture)
{
    [Fact]
    public async Task Queue_tables_have_forced_RLS_and_no_company_context_reveals_nothing()
    {
        Assert.True(await fixture.Database.ScalarAsync<bool>("SELECT to_regclass('job') IS NOT NULL"), "M0007 must create job");
        var seeded = await fixture.SeedCompanyWithAdminAsync();
        var otherCompany = await fixture.SeedCompanyAsync(seeded.User.Id);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await TenantIsolationChecks.VerifyTablesAsync(owner);
        foreach (var companyId in new[] { seeded.CompanyId, otherCompany })
        {
            var jobId = await owner.QuerySingleAsync<Guid>(new CommandDefinition("INSERT INTO job(company_id,type,state,attempts,payload,created_by) VALUES(@companyId,'status_check','failed',1,'{}',@Id) RETURNING id", new { companyId, seeded.User.Id }, cancellationToken: TestContext.Current.CancellationToken));
            await owner.ExecuteAsync(new CommandDefinition("INSERT INTO job_attempt(company_id,job_id,attempt_no,started_at,outcome,error_kind,error) VALUES(@companyId,@jobId,1,now(),'failed','permanent','Synthetic input')", new { companyId, jobId }, cancellationToken: TestContext.Current.CancellationToken));
        }
        await using var app = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await app.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, await app.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM job", cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Equal(0, await app.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM job_attempt", cancellationToken: TestContext.Current.CancellationToken)));
        await using var transaction = await app.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await app.ExecuteAsync(new CommandDefinition("SELECT set_config('app.company_id',@company,true)", new { company = seeded.CompanyId.ToString() }, transaction, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal([seeded.CompanyId], await app.QueryAsync<Guid>(new CommandDefinition("SELECT company_id FROM job", transaction: transaction, cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Equal([seeded.CompanyId], await app.QueryAsync<Guid>(new CommandDefinition("SELECT company_id FROM job_attempt", transaction: transaction, cancellationToken: TestContext.Current.CancellationToken)));
        var error = await Assert.ThrowsAsync<PostgresException>(() => app.ExecuteAsync(new CommandDefinition("INSERT INTO job(company_id,type,payload,created_by) VALUES(@otherCompany,'status_check','{}',@Id)", new { otherCompany, seeded.User.Id }, transaction, cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task Only_application_can_execute_pinned_claim_and_definer_can_touch_only_queue_tables()
    {
        await using var owner = await fixture.Database.OpenOwnerAsync();
        Assert.True(await owner.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT FROM pg_proc WHERE proname='claim_next_job')", cancellationToken: TestContext.Current.CancellationToken)), "Missing claim function");
        await AssertQueueRoleBoundaryAsync(owner);
        var functions = (await owner.QueryAsync<(bool Definer, string Owner, string[] Settings, bool AppExecute, bool PublicExecute)>(new CommandDefinition("""
            SELECT p.prosecdef, r.rolname, p.proconfig, has_function_privilege('cfdi_app',p.oid,'EXECUTE'),
              EXISTS(SELECT FROM aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) a WHERE a.grantee=0 AND a.privilege_type='EXECUTE')
            FROM pg_proc p JOIN pg_roles r ON r.oid=p.proowner WHERE p.proname IN ('claim_next_job','renew_job','finish_job')
            """, cancellationToken: TestContext.Current.CancellationToken))).ToArray();
        Assert.Equal(3, functions.Length);
        foreach (var metadata in functions)
        {
            Assert.True(metadata.Definer && metadata.AppExecute && !metadata.PublicExecute);
            Assert.Equal("cfdi_queue_owner", metadata.Owner);
            Assert.Contains("search_path=pg_catalog, public, pg_temp", metadata.Settings);
        }
        await using var app = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await app.ExecuteAsync(new CommandDefinition("SELECT * FROM claim_next_job('synthetic-worker',30)", cancellationToken: TestContext.Current.CancellationToken));
    }

    internal static async Task AssertQueueRoleBoundaryAsync(NpgsqlConnection owner)
    {
        Assert.Equal(["cfdi_queue_owner"], await owner.QueryAsync<string>(new CommandDefinition("SELECT rolname FROM pg_roles WHERE rolbypassrls AND NOT rolsuper ORDER BY rolname", cancellationToken: TestContext.Current.CancellationToken)));
        Assert.True(await owner.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT NOT rolsuper AND NOT rolbypassrls FROM pg_roles WHERE rolname='cfdi_app'", cancellationToken: TestContext.Current.CancellationToken)));
        Assert.False(await owner.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT rolcanlogin OR rolsuper OR rolcreatedb OR rolcreaterole FROM pg_roles WHERE rolname='cfdi_queue_owner'", cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Empty(await owner.QueryAsync<string>(new CommandDefinition("""
            SELECT tablename FROM pg_tables WHERE schemaname='public' AND tablename NOT IN ('job','job_attempt')
              AND (has_table_privilege('cfdi_queue_owner',format('%I.%I',schemaname,tablename),'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER'))
            """, cancellationToken: TestContext.Current.CancellationToken)));
        Assert.False(await owner.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT pg_has_role('cfdi_app','cfdi_queue_owner','MEMBER')", cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task M0007_applies_reverts_and_reapplies()
    {
        Assert.True(await fixture.Database.ScalarAsync<bool>("SELECT to_regclass('job') IS NOT NULL"), "Missing queue schema");
        await using var database = new PostgresFixture();
        await database.InitializeAsync();
        using var services = new ServiceCollection().AddFluentMigratorCore().ConfigureRunner(runner => runner.AddPostgres()
            .WithGlobalConnectionString(database.OwnerConnectionString).ScanIn(typeof(M0001CompanyUserMembership).Assembly).For.Migrations())
            .AddLogging().BuildServiceProvider(validateScopes: false);
        using var scope = services.CreateScope();
        var migrations = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        migrations.MigrateDown(6);
        Assert.False(await database.ScalarAsync<bool>("SELECT to_regclass('job') IS NOT NULL OR to_regclass('job_attempt') IS NOT NULL"));
        Assert.False(await database.ScalarAsync<bool>("SELECT EXISTS(SELECT FROM pg_roles WHERE rolname='cfdi_queue_owner')"));
        migrations.MigrateUp();
        Assert.True(7 <= await database.ScalarAsync<long>("SELECT max(\"Version\") FROM \"VersionInfo\""));
        await using var owner = await database.OpenOwnerAsync();
        await AssertQueueRoleBoundaryAsync(owner);
    }
}
