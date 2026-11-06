using System.Collections;
using System.Text.Json;
using Dapper;
using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Reconciliation.Core.Sat;
using Reconciliation.Migrator.Migrations;

namespace Reconciliation.Tests;

public sealed class SatCacheTests
{
    [Fact]
    public async Task M0011_is_tenant_scoped_and_replays_cancelled_backfill_without_a_service_call()
    {
        await using var database = new PostgresFixture(); await database.InitializeAsync();
        using var services = new ServiceCollection().AddFluentMigratorCore().ConfigureRunner(r => r.AddPostgres()
            .WithGlobalConnectionString(database.OwnerConnectionString).ScanIn(typeof(M0001CompanyUserMembership).Assembly).For.Migrations()).AddLogging().BuildServiceProvider(validateScopes: false);
        using var scope = services.CreateScope(); var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        Assert.Equal(13, await database.ScalarAsync<long>("SELECT max(\"Version\") FROM \"VersionInfo\""));
        runner.MigrateDown(10);
        Assert.Contains("run_after=p_now", await database.ScalarAsync<string>("SELECT pg_get_functiondef('claim_next_job(text,integer,timestamptz)'::regprocedure)"), StringComparison.Ordinal);
        Assert.False(await database.ScalarAsync<bool>("SELECT EXISTS(SELECT FROM information_schema.columns WHERE table_name='job' AND column_name='seq')"));
        await using var owner = await database.OpenOwnerAsync();
        var company = await owner.ExecuteScalarAsync<Guid>(new CommandDefinition("INSERT INTO company(rfc,name) VALUES('AAA010101AAA','Synthetic SAT cache') RETURNING id", cancellationToken: TestContext.Current.CancellationToken));
        var uuid = Guid.Parse("00000000-0000-4000-8000-000000000711");
        await owner.ExecuteAsync(new CommandDefinition("""
            WITH f AS (INSERT INTO import_file(company_id,upload_id,filename,kind,status,reason,sha256)
              VALUES(@company,gen_random_uuid(),'synthetic.xml','cfdi','rejected','Synthetic fixture',sha256('synthetic'::bytea)) RETURNING id)
            INSERT INTO cfdi(company_id,import_file_id,uuid,type,direction,issuer_rfc,receiver_rfc,issued_at,stamped_at,currency,subtotal,total,sat_status,cancellation_status)
            SELECT @company,id,@uuid,'I','issued','AAA010101AAA','BBB010101BBB','2026-10-10','2026-10-10','MXN',100,100,'cancelled','cancelled without acceptance' FROM f
            """, new { company, uuid }, cancellationToken: TestContext.Current.CancellationToken));
        runner.MigrateUp();
        Assert.Contains("LEAST(run_after,p_now)", await database.ScalarAsync<string>("SELECT pg_get_functiondef('claim_next_job(text,integer,timestamptz)'::regprocedure)"), StringComparison.Ordinal);
        var row = await owner.QuerySingleAsync<(string State, string Source, DateTime? Expires, string? Code, string? Efos)>(new CommandDefinition(
            "SELECT state,source,expires_at,code_raw,efos_raw FROM sat_status_cache WHERE uuid=@uuid", new { uuid }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(("cancelled", "backfill", null, null, null), row);
        Assert.Equal("cancelled without acceptance", await database.ScalarAsync<string>("SELECT cancellation_status FROM sat_status_cache WHERE uuid=@uuid", new { uuid }));
        runner.MigrateDown(10); Assert.False(await database.ScalarAsync<bool>("SELECT to_regclass('sat_status_cache') IS NOT NULL")); runner.MigrateUp();
        Assert.Equal(1, await database.ScalarAsync<int>("SELECT count(*) FROM sat_status_cache WHERE uuid=@uuid", new { uuid }));
        await using (var readTransaction = await owner.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            var store = typeof(ISatClient).Assembly.GetType("Reconciliation.Core.Sat.SatStatusStore"); Assert.NotNull(store);
            var read = (Task)store.GetMethod("ReadAsync")!.Invoke(null, [owner, readTransaction, new[] { uuid }, TestContext.Current.CancellationToken])!;
            await read;
            var cache = ((IDictionary)read.GetType().GetProperty("Result")!.GetValue(read)!)[uuid]; Assert.NotNull(cache);
            var client = new FakeSatClient((_, _) => throw new InvalidOperationException("A migrated cancellation must not call SAT"));
            await SatRateAndLookupTests.Check(SatRateAndLookupTests.Lookup(client, new ManualTimeProvider()), cache,
                new(uuid, "AAA010101AAA", "BBB010101BBB", 100));
            Assert.Equal(0, client.Calls);
            await readTransaction.CommitAsync(TestContext.Current.CancellationToken);
        }
        await TenantIsolationChecks.VerifyTablesAsync(owner);
        await using var app = new NpgsqlConnection(database.AppConnectionString); await app.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, await app.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM sat_status_cache", cancellationToken: TestContext.Current.CancellationToken)));
        await using var transaction = await app.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await app.ExecuteAsync(new CommandDefinition("SELECT set_config('app.company_id',@company,true)", new { company = company.ToString() }, transaction, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await app.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM sat_status_cache", transaction: transaction, cancellationToken: TestContext.Current.CancellationToken)));
        Assert.False(await app.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT has_table_privilege(current_user,'sat_status_cache','DELETE')", transaction: transaction, cancellationToken: TestContext.Current.CancellationToken)));
        var entryType = typeof(ISatClient).Assembly.GetType("Reconciliation.Core.Sat.SatCacheEntry"); Assert.NotNull(entryType);
        var checkedAt = new DateTime(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc);
        var entry = JsonSerializer.Deserialize(JsonSerializer.Serialize(new
        {
            uuid,
            state = "current",
            cancellability = "with acceptance",
            cancellationStatus = "in process",
            efosRaw = "100",
            checkedAt,
            expiresAt = checkedAt.AddHours(24),
            attempts = 2,
            source = "stub",
            codeRaw = SatContractTests.Success,
            stateRaw = "Vigente",
            cancellabilityRaw = "Cancelable con aceptación",
            cancellationStatusRaw = "En proceso"
        }), entryType, JsonSerializerOptions.Web)!;
        var write = (Task)typeof(ISatClient).Assembly.GetType("Reconciliation.Core.Sat.SatStatusStore")!.GetMethod("WriteAsync")!
            .Invoke(null, [app, transaction, company, entry, TestContext.Current.CancellationToken])!; await write;
        Assert.Equal(("current", "with acceptance", "in process", "100", 2), await app.QuerySingleAsync<(string, string, string, string, int)>(new CommandDefinition(
            "SELECT state,cancellability,cancellation_status,efos_raw,attempts FROM sat_status_cache", transaction: transaction, cancellationToken: TestContext.Current.CancellationToken)));
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<PostgresException>(() => app.ExecuteAsync(new CommandDefinition("""
            INSERT INTO sat_status_cache(company_id,uuid,state,source,checked_at,attempts) VALUES(@company,@uuid,'unknown','stub',now(),1)
            """, new { company, uuid }, cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }
}
