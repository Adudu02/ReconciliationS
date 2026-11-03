using Dapper;
using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Reconciliation.Migrator.Migrations;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class SessionDatabaseTests(ApiFixture fixture)
{
    [Fact]
    public async Task Session_tables_are_user_scoped_and_application_grants_are_narrow()
    {
        await using var owner = await fixture.Database.OpenOwnerAsync();
        foreach (var table in new[] { "auth_session", "refresh_token", "auth_event" })
        {
            Assert.True(await owner.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT to_regclass(@table) IS NOT NULL", new { table }, cancellationToken: TestContext.Current.CancellationToken)), $"Missing session table {table}");
            var state = await owner.QuerySingleAsync<(bool Rls, bool Forced, bool Select, bool Insert, bool Update, bool Delete)>(new CommandDefinition("""
                SELECT relrowsecurity, relforcerowsecurity,
                  has_table_privilege('cfdi_app', @table, 'SELECT'), has_table_privilege('cfdi_app', @table, 'INSERT'),
                  has_table_privilege('cfdi_app', @table, 'UPDATE'), has_table_privilege('cfdi_app', @table, 'DELETE')
                FROM pg_class WHERE oid=to_regclass(@table)
                """, new { table }, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal((false, false, true, true, false, false), state);
            var actual = (await owner.QueryAsync<string>(new CommandDefinition("""
                SELECT column_name FROM information_schema.columns WHERE table_schema='public' AND table_name=@table
                  AND has_column_privilege('cfdi_app', @table, column_name, 'UPDATE') ORDER BY column_name
                """, new { table }, cancellationToken: TestContext.Current.CancellationToken))).ToArray();
            string[] expected = table switch
            {
                "auth_session" => ["last_used_at", "revoked_at", "revoked_reason"],
                "refresh_token" => ["replaced_by", "rotated_at"],
                _ => []
            };
            Assert.Equal(expected, actual);
            Assert.False(await owner.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT FROM information_schema.columns WHERE table_schema='public' AND table_name=@table AND column_name='company_id')", new { table }, cancellationToken: TestContext.Current.CancellationToken)));
        }
        var indexes = string.Join('\n', await owner.QueryAsync<string>(new CommandDefinition("SELECT indexdef FROM pg_indexes WHERE tablename IN ('auth_session','refresh_token')", cancellationToken: TestContext.Current.CancellationToken)));
        foreach (var expected in new[] { "(token_hash)", "(session_id)", "(user_id)" }) Assert.Contains(expected, indexes, StringComparison.Ordinal);
        var mutableUserColumns = await owner.QueryAsync<string>(new CommandDefinition("""
            SELECT column_name FROM information_schema.columns WHERE table_name='app_user' AND table_schema='public'
              AND has_column_privilege('cfdi_app','app_user',column_name,'UPDATE') ORDER BY column_name
            """, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(["password_hash"], mutableUserColumns);
    }

    [Fact]
    public async Task Application_role_cannot_delete_sessions_or_alter_events_or_immutable_columns()
    {
        Assert.True(await fixture.Database.ScalarAsync<bool>("SELECT to_regclass('auth_session') IS NOT NULL"));
        var user = await fixture.SeedUserAsync();
        await using var app = new NpgsqlConnection(fixture.Database.AppConnectionString);
        var now = fixture.Clock.GetUtcNow();
        var sessionId = await app.QuerySingleAsync<Guid>(new CommandDefinition("""
            INSERT INTO auth_session(user_id,created_at,last_used_at,absolute_expires_at) VALUES(@Id,@now,@now,@expires) RETURNING id
            """, new { user.Id, now, expires = now.AddDays(30) }, cancellationToken: TestContext.Current.CancellationToken));
        await app.ExecuteAsync(new CommandDefinition("""
            INSERT INTO auth_event(user_id,session_id,event,created_at) VALUES(@Id,@sessionId,'login',@now)
            """, new { user.Id, sessionId, now }, cancellationToken: TestContext.Current.CancellationToken));
        foreach (var sql in new[]
        {
            "DELETE FROM auth_session", "DELETE FROM refresh_token", "DELETE FROM auth_event",
            "UPDATE auth_event SET event='logout'", "UPDATE auth_session SET user_id=user_id",
            "UPDATE auth_session SET absolute_expires_at=absolute_expires_at", "UPDATE refresh_token SET token_hash=token_hash",
            "UPDATE app_user SET is_operator=true", "UPDATE app_user SET email=email"
        })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => app.ExecuteAsync(new CommandDefinition(sql, cancellationToken: TestContext.Current.CancellationToken)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
    }

    [Fact]
    public async Task M0006_applies_reverts_and_reapplies_on_a_disposable_database()
    {
        await using var database = new PostgresFixture();
        await database.InitializeAsync();
        Assert.True(6 <= await database.ScalarAsync<long>("SELECT max(\"Version\") FROM \"VersionInfo\""));
        using var services = new ServiceCollection().AddFluentMigratorCore()
            .ConfigureRunner(runner => runner.AddPostgres().WithGlobalConnectionString(database.OwnerConnectionString)
                .ScanIn(typeof(M0001CompanyUserMembership).Assembly).For.Migrations())
            .AddLogging().BuildServiceProvider(validateScopes: false);
        using var scope = services.CreateScope();
        var migrations = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        migrations.MigrateDown(5);
        Assert.False(await database.ScalarAsync<bool>("SELECT to_regclass('auth_session') IS NOT NULL"));
        Assert.False(await database.ScalarAsync<bool>("SELECT has_column_privilege('cfdi_app','app_user','password_hash','UPDATE')"));
        migrations.MigrateUp();
        Assert.True(await database.ScalarAsync<bool>("SELECT to_regclass('auth_session') IS NOT NULL"));
        Assert.True(6 <= await database.ScalarAsync<long>("SELECT max(\"Version\") FROM \"VersionInfo\""));
    }
}
