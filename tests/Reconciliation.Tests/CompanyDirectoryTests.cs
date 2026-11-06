using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Reconciliation.Migrator.Migrations;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class CompanyDirectoryTests(ApiFixture fixture)
{
    [Fact]
    public async Task Directory_lists_only_own_memberships_including_archived_and_no_operator_bypass()
    {
        var user = await fixture.SeedUserAsync();
        var other = await fixture.SeedUserAsync();
        var a = await fixture.SeedCompanyAsync(user.Id, "accountant");
        var archived = await fixture.SeedCompanyAsync(user.Id, "viewer");
        await fixture.SeedCompanyAsync(other.Id);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync(new CommandDefinition("UPDATE company SET archived_at=now() WHERE id=@archived", new { archived }, cancellationToken: TestContext.Current.CancellationToken));
        using var client = await fixture.LoginAsync(user.Email);
        using var response = await client.GetAsync("/companies", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal("accountant", Assert.Single(rows, row => row.GetProperty("id").GetGuid() == a).GetProperty("role").GetString());
        Assert.NotEqual(JsonValueKind.Null, Assert.Single(rows, row => row.GetProperty("id").GetGuid() == archived).GetProperty("archivedAt").ValueKind);
        var empty = await fixture.SeedUserAsync();
        await owner.ExecuteAsync(new CommandDefinition("UPDATE app_user SET is_operator=true WHERE id=@Id", empty, cancellationToken: TestContext.Current.CancellationToken));
        using var platform = await fixture.LoginAsync(empty.Email);
        Assert.Empty((await platform.GetFromJsonAsync<JsonElement>("/companies", TestContext.Current.CancellationToken)).EnumerateArray());
        using var anonymous = fixture.App.CreateClient();
        using var denied = await anonymous.GetAsync("/companies", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Directory_function_requires_matching_LOCAL_user_context(bool mismatched)
    {
        await using var app = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await app.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = await app.BeginTransactionAsync(TestContext.Current.CancellationToken);
        if (mismatched) await app.ExecuteAsync(new CommandDefinition("SELECT set_config('app.user_id',@value,true)", new { value = Guid.NewGuid().ToString() }, tx, cancellationToken: TestContext.Current.CancellationToken));
        var error = await Assert.ThrowsAsync<PostgresException>(() => app.QueryAsync<Guid>(new CommandDefinition("SELECT id FROM user_companies(@user)", new { user = Guid.NewGuid() }, tx, cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task Directory_owner_has_only_pinned_function_and_column_reads_and_up_down_up_is_clean()
    {
        await using var database = new PostgresFixture();
        await database.InitializeAsync();
        await using var owner = await database.OpenOwnerAsync();
        Assert.True(await owner.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT FROM pg_roles WHERE rolname='cfdi_directory_owner' AND rolbypassrls AND NOT rolcanlogin AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole)", cancellationToken: TestContext.Current.CancellationToken)));
        Assert.True(await owner.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT prosecdef AND provolatile='s' AND proconfig=ARRAY['search_path=pg_catalog, public, pg_temp'] FROM pg_proc WHERE proname='user_companies'", cancellationToken: TestContext.Current.CancellationToken)));
        Assert.False(await owner.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT FROM pg_proc p,aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) a WHERE p.proname='user_companies' AND a.grantee=0 AND a.privilege_type='EXECUTE')", cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Empty(await owner.QueryAsync<string>(new CommandDefinition("SELECT tablename FROM pg_tables WHERE schemaname='public' AND has_table_privilege('cfdi_directory_owner',format('%I.%I',schemaname,tablename),'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')", cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Equal(7, await owner.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM information_schema.column_privileges WHERE grantee='cfdi_directory_owner' AND privilege_type='SELECT'", cancellationToken: TestContext.Current.CancellationToken)));
        using var services = new ServiceCollection().AddFluentMigratorCore().ConfigureRunner(runner => runner.AddPostgres().WithGlobalConnectionString(database.OwnerConnectionString).ScanIn(typeof(M0001CompanyUserMembership).Assembly).For.Migrations()).AddLogging().BuildServiceProvider(validateScopes: false);
        using var scope = services.CreateScope();
        var migrator = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        migrator.MigrateDown(12);
        Assert.False(await database.ScalarAsync<bool>("SELECT EXISTS(SELECT FROM pg_proc WHERE proname='user_companies') OR EXISTS(SELECT FROM pg_roles WHERE rolname='cfdi_directory_owner')"));
        migrator.MigrateUp();
        Assert.True(await database.ScalarAsync<bool>("SELECT to_regprocedure('user_companies(uuid)') IS NOT NULL"));
    }
}
