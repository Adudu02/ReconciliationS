using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Reconciliation.Contracts;
using Reconciliation.Migrator.Migrations;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class MemberDirectoryTests(ApiFixture fixture)
{
    [Theory]
    [InlineData("administrator")]
    [InlineData("accountant")]
    [InlineData("viewer")]
    public async Task Members_can_read_only_their_company_member_emails(string role)
    {
        var user = await fixture.SeedUserAsync();
        var other = await fixture.SeedUserAsync();
        var a = await fixture.SeedCompanyAsync(user.Id, role);
        var b = await fixture.SeedCompanyAsync(other.Id);
        using var client = await fixture.LoginAsync(user.Email, a);
        var members = await client.GetFromJsonAsync<JsonElement>($"/companies/{a}/members", TestContext.Current.CancellationToken);
        var member = Assert.Single(members.EnumerateArray());
        Assert.Equal(user.Email, member.GetProperty("email").GetString());
        Assert.Equal(user.Id, member.GetProperty("userId").GetGuid());
        Assert.Equal(role, member.GetProperty("role").GetString());
        var typed = JsonSerializer.Deserialize(members.GetRawText(), WebJsonContext.Default.MemberItemArray)!;
        Assert.Equal(new MemberItem(user.Id, user.Email, role), Assert.Single(typed));
        Assert.DoesNotContain(other.Email, members.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("password", members.GetRawText(), StringComparison.OrdinalIgnoreCase);
        using var mismatch = await client.GetAsync($"/companies/{b}/members", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, mismatch.StatusCode);
        client.DefaultRequestHeaders.Remove("X-Company-Id");
        client.DefaultRequestHeaders.Add("X-Company-Id", b.ToString());
        using var denied = await client.GetAsync($"/companies/{b}/members", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.DoesNotContain(other.Email, await denied.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-company")]
    [InlineData("missing-user")]
    [InlineData("mismatched-company")]
    [InlineData("mismatched-user")]
    [InlineData("nonmember")]
    public async Task Member_directory_function_requires_matching_transaction_context_and_membership(string scenario)
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        var other = await fixture.SeedUserAsync();
        var otherCompany = await fixture.SeedCompanyAsync(other.Id);
        var requestedCompany = scenario == "nonmember" ? otherCompany : company;
        await using var app = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await app.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = await app.BeginTransactionAsync(TestContext.Current.CancellationToken);
        if (scenario != "missing-company")
            await app.ExecuteAsync(new CommandDefinition("SELECT set_config('app.company_id',@value,true)", new { value = (scenario == "mismatched-company" ? otherCompany : requestedCompany).ToString() }, tx, cancellationToken: TestContext.Current.CancellationToken));
        if (scenario != "missing-user")
            await app.ExecuteAsync(new CommandDefinition("SELECT set_config('app.user_id',@value,true)", new { value = (scenario == "mismatched-user" ? other.Id : user.Id).ToString() }, tx, cancellationToken: TestContext.Current.CancellationToken));
        var error = await Assert.ThrowsAsync<PostgresException>(() => app.QueryAsync<string>(new CommandDefinition(
            "SELECT email FROM company_members(@company,@user)", new { company = requestedCompany, user = user.Id }, tx, cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task Directory_migration_has_narrow_grants_and_reverses_without_changing_accounts_or_app_grants()
    {
        await using var database = new PostgresFixture();
        await database.InitializeAsync();
        await using var owner = await database.OpenOwnerAsync();
        const string grantsQuery = "SELECT array_agg(privilege_type ORDER BY privilege_type) FROM information_schema.role_table_grants WHERE grantee='cfdi_app' AND table_name='app_user'";
        var grants = await owner.ExecuteScalarAsync<string[]>(new CommandDefinition(grantsQuery, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await owner.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT rolbypassrls AND NOT rolcanlogin AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole FROM pg_roles WHERE rolname='cfdi_member_directory_owner'", cancellationToken: TestContext.Current.CancellationToken)));
        Assert.True(await owner.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT prosecdef AND provolatile='s' AND proconfig=ARRAY['search_path=pg_catalog, public, pg_temp'] FROM pg_proc WHERE proname='company_members'", cancellationToken: TestContext.Current.CancellationToken)));
        Assert.False(await owner.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT has_table_privilege('cfdi_member_directory_owner','app_user','SELECT') OR has_column_privilege('cfdi_member_directory_owner','app_user','password_hash','SELECT')", cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Equal(5, await owner.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM information_schema.column_privileges WHERE grantee='cfdi_member_directory_owner' AND privilege_type='SELECT'", cancellationToken: TestContext.Current.CancellationToken)));
        Assert.False(await owner.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT FROM pg_proc p,aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) a WHERE p.proname='company_members' AND a.grantee=0 AND a.privilege_type='EXECUTE')", cancellationToken: TestContext.Current.CancellationToken)));
        var user = await owner.ExecuteScalarAsync<Guid>(new CommandDefinition("INSERT INTO app_user(email,password_hash) VALUES('retained@example.invalid','synthetic-hash') RETURNING id", cancellationToken: TestContext.Current.CancellationToken));
        using var services = new ServiceCollection().AddFluentMigratorCore().ConfigureRunner(runner => runner.AddPostgres().WithGlobalConnectionString(database.OwnerConnectionString).ScanIn(typeof(M0001CompanyUserMembership).Assembly).For.Migrations()).AddLogging().BuildServiceProvider(validateScopes: false);
        using var scope = services.CreateScope();
        var migrator = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        migrator.MigrateDown(13);
        Assert.False(await database.ScalarAsync<bool>("SELECT EXISTS(SELECT FROM pg_proc WHERE proname='company_members') OR EXISTS(SELECT FROM pg_roles WHERE rolname='cfdi_member_directory_owner')"));
        Assert.Equal(grants, await owner.ExecuteScalarAsync<string[]>(new CommandDefinition(grantsQuery, cancellationToken: TestContext.Current.CancellationToken)));
        migrator.MigrateUp();
        Assert.True(await database.ScalarAsync<bool>("SELECT to_regprocedure('company_members(uuid,uuid)') IS NOT NULL"));
        Assert.Equal(user, await database.ScalarAsync<Guid>("SELECT id FROM app_user WHERE email='retained@example.invalid'"));
    }

    [Fact]
    public async Task Openapi_describes_team_and_account_contracts_without_passwords_in_member_responses()
    {
        using var client = fixture.App.CreateClient();
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/openapi.json", TestContext.Current.CancellationToken));
        var root = doc.RootElement;
        var members = root.GetProperty("paths").GetProperty("/companies/{companyId}/members");
        var response = Resolve(root, members.GetProperty("get").GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        var item = Resolve(root, response.GetProperty("items"));
        Assert.Equal("email,role,userId", string.Join(",", item.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order()));
        var request = Resolve(root, members.GetProperty("post").GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        Assert.Equal("email,initialPassword,role,userId", string.Join(",", request.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order()));
        Assert.True(members.GetProperty("post").GetProperty("responses").TryGetProperty("204", out _));
        Assert.Contains("exactly one", members.GetProperty("post").GetProperty("description").GetString(), StringComparison.Ordinal);
        var password = root.GetProperty("paths").GetProperty("/auth/password").GetProperty("post");
        var body = Resolve(root, password.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        Assert.Equal("currentPassword,newPassword", string.Join(",", body.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order()));
        Assert.True(password.GetProperty("responses").TryGetProperty("204", out _));
    }

    private static JsonElement Resolve(JsonElement root, JsonElement schema)
    {
        for (var depth = 0; depth < 20 && schema.TryGetProperty("$ref", out var reference); depth++)
            schema = root.GetProperty("components").GetProperty("schemas").GetProperty(reference.GetString()!.Split('/')[^1]);
        return schema;
    }
}
