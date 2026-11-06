using System.Net;
using System.Net.Http.Json;
using Dapper;
using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Reconciliation.Migrator.Migrations;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class LinkDecisionTests(ApiFixture fixture)
{
    [Fact]
    public async Task Decision_table_has_forced_RLS_and_minimal_append_only_grants()
    {
        await using var owner = await fixture.Database.OpenOwnerAsync();
        Assert.True(await owner.ExecuteScalarAsync<bool>("SELECT coalesce((SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid=to_regclass('link_decision')),false)"));
        Assert.True(await owner.ExecuteScalarAsync<bool>("SELECT has_table_privilege('cfdi_app','link_decision','SELECT,INSERT')"));
        Assert.False(await owner.ExecuteScalarAsync<bool>("SELECT has_table_privilege('cfdi_app','link_decision','UPDATE') OR has_table_privilege('cfdi_app','link_decision','DELETE')"));
    }

    [Fact]
    public async Task M0012_up_down_up_preserves_imported_data_and_audit()
    {
        await using var database = new PostgresFixture();
        await database.InitializeAsync();
        using var services = new ServiceCollection().AddFluentMigratorCore().ConfigureRunner(r => r.AddPostgres()
            .WithGlobalConnectionString(database.OwnerConnectionString).ScanIn(typeof(M0001CompanyUserMembership).Assembly).For.Migrations()).AddLogging().BuildServiceProvider(validateScopes: false);
        using var scope = services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        await using var owner = await database.OpenOwnerAsync();
        var company = await owner.ExecuteScalarAsync<Guid>("INSERT INTO company(rfc,name) VALUES('AAA010101AAA','Synthetic review migration') RETURNING id");
        await owner.ExecuteAsync("SELECT set_config('app.company_id',@value,false); SELECT append_audit_entry(@company,'system',NULL,'synthetic.migration','[]','{\"source\":\"migration-test\"}')", new { value = company.ToString(), company });
        await owner.ExecuteAsync("""
            WITH file AS (INSERT INTO import_file(company_id,upload_id,filename,kind,status,reason,sha256)
              VALUES(@company,gen_random_uuid(),'synthetic.xml','cfdi','rejected','Migration fixture',sha256('synthetic'::bytea)) RETURNING id)
            INSERT INTO cfdi(company_id,import_file_id,uuid,type,direction,issuer_rfc,receiver_rfc,issued_at,stamped_at,currency,subtotal,total)
            SELECT @company,id,gen_random_uuid(),'I','issued','AAA010101AAA','BBB010101BBB','2026-10-10','2026-10-10','MXN',100,100 FROM file
            """, new { company });
        var hash = await owner.ExecuteScalarAsync<string>("SELECT encode(hash,'hex') FROM audit_entry WHERE company_id=@company", new { company });
        runner.MigrateDown(11);
        Assert.Null(await owner.ExecuteScalarAsync<string>("SELECT to_regclass('link_decision')::text"));
        runner.MigrateUp();
        Assert.Equal(hash, await owner.ExecuteScalarAsync<string>("SELECT encode(hash,'hex') FROM audit_entry WHERE company_id=@company", new { company }));
        Assert.Equal(1, await owner.ExecuteScalarAsync<int>("SELECT count(*) FROM cfdi WHERE company_id=@company", new { company }));
    }

    [Theory]
    [InlineData("confirm", "reconciled")]
    [InlineData("reject", "rejected")]
    public async Task Decision_records_actor_and_one_audit_atomically_and_refuses_repeat(string type, string expected)
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        await MatchingRunScenario.RunAsync(fixture, client, seed.Company);
        var link = await fixture.Database.ScalarAsync<Guid>("SELECT id FROM link WHERE company_id=@company", new { company = seed.Company });
        var before = await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM audit_entry WHERE company_id=@company", new { company = seed.Company });
        var path = $"/companies/{seed.Company}/links/{link}/decisions";
        using var response = await client.PostAsJsonAsync(path, new { type, note = "synthetic private note" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await fixture.Database.ScalarAsync<string>("SELECT state FROM link WHERE id=@link", new { link }));
        Assert.Equal(seed.Actor, await fixture.Database.ScalarAsync<Guid>("SELECT decided_by FROM link_decision WHERE link_id=@link", new { link }));
        Assert.Equal(before + 1, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM audit_entry WHERE company_id=@company", new { company = seed.Company }));
        using var repeat = await client.PostAsJsonAsync(path, new { type }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);
    }

    [Fact]
    public async Task Change_same_invoice_amount_creates_new_user_link_and_does_not_flap()
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        await MatchingRunScenario.RunAsync(fixture, client, seed.Company);
        var link = await fixture.Database.ScalarAsync<Guid>("SELECT id FROM link WHERE company_id=@company", new { company = seed.Company });
        using var response = await client.PostAsJsonAsync($"/companies/{seed.Company}/links/{link}/decisions", new { type = "change", amountApplied = 60m }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("closed", await fixture.Database.ScalarAsync<string>("SELECT state FROM link WHERE id=@link", new { link }));
        Assert.Equal(60m, await fixture.Database.ScalarAsync<decimal>("SELECT amount_applied FROM link WHERE company_id=@company AND origin='user' AND state='reconciled'", new { company = seed.Company }));
        await MatchingRunScenario.RunAsync(fixture, client, seed.Company);
        Assert.Equal(2, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM link WHERE company_id=@company", new { company = seed.Company }));
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM link_decision WHERE link_id=@link", new { link }));
    }

    [Theory]
    [InlineData("reconciled")]
    [InlineData("rejected")]
    [InlineData("closed")]
    public async Task Decisions_on_non_suggestions_return_current_state(string state)
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        await MatchingRunScenario.RunAsync(fixture, client, seed.Company);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        var link = await owner.ExecuteScalarAsync<Guid>("UPDATE link SET state=@state WHERE company_id=@company RETURNING id", new { state, company = seed.Company });
        using var response = await client.PostAsJsonAsync($"/companies/{seed.Company}/links/{link}/decisions", new { type = "change", amountApplied = 60m }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(state, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_decisions_have_exactly_one_winner()
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        using var first = await fixture.LoginAsync(seed.Email, seed.Company);
        using var second = await fixture.LoginAsync(seed.Email, seed.Company);
        await MatchingRunScenario.RunAsync(fixture, first, seed.Company);
        var link = await fixture.Database.ScalarAsync<Guid>("SELECT id FROM link WHERE company_id=@company", new { company = seed.Company });
        var path = $"/companies/{seed.Company}/links/{link}/decisions";
        var responses = await Task.WhenAll(first.PostAsJsonAsync(path, new { type = "confirm" }, TestContext.Current.CancellationToken), second.PostAsJsonAsync(path, new { type = "reject" }, TestContext.Current.CancellationToken));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }
}
