using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class AuditHttpTests(ApiFixture fixture)
{
    private async Task<(Guid Company, Guid Actor, string Email, string Item)> SeedAsync(string role = "administrator")
    {
        var user = await fixture.SeedUserAsync();
        var company = await fixture.SeedCompanyAsync(user.Id, role);
        var item = Guid.NewGuid().ToString();
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await using var tx = await owner.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await AuditDatabaseTests.SetContextAsync(owner, tx, company);
        await AuditDatabaseTests.AppendAsync(owner, tx, company, user.Id, "synthetic.accepted", JsonSerializer.Serialize(new[] { new { kind = "cfdi", id = item } }));
        await owner.ExecuteAsync(new CommandDefinition("SELECT append_audit_entry(@company,'system',NULL,'synthetic.changed','[]','{\"source\":\"synthetic-worker\"}')", new { company }, tx, cancellationToken: TestContext.Current.CancellationToken));
        await AuditDatabaseTests.AppendAsync(owner, tx, company, user.Id, "synthetic.accepted", JsonSerializer.Serialize(new[] { new { kind = "cfdi", id = item }, new { kind = "movement", id = "Synthetic-Movement-1" } }));
        await tx.CommitAsync(TestContext.Current.CancellationToken);
        return (company, user.Id, user.Email, item);
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    [Fact(DisplayName = "Filter by item")]
    public async Task Filter_by_item_returns_only_that_CFDI_in_chronological_sequence_order()
    {
        var seed = await SeedAsync("viewer");
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        var page = await GetAsync(client, $"/companies/{seed.Company}/audit-entries?item={seed.Item}");
        Assert.Equal([1L, 3L], page.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("seq").GetInt64()));
        var entries = page.GetProperty("entries").EnumerateArray().ToArray();
        Assert.True(entries[0].GetProperty("occurredAt").GetDateTimeOffset() <= entries[1].GetProperty("occurredAt").GetDateTimeOffset());
        Assert.Equal(seed.Actor.ToString(), entries[0].GetProperty("actor").GetString());
        Assert.Equal(JsonValueKind.Array, entries[0].GetProperty("items").ValueKind);
        Assert.Equal(JsonValueKind.Object, entries[0].GetProperty("detail").ValueKind);
        var none = await GetAsync(client, $"/companies/{seed.Company}/audit-entries?item={Guid.NewGuid()}");
        Assert.Empty(none.GetProperty("entries").EnumerateArray());
        var movement = await GetAsync(client, $"/companies/{seed.Company}/audit-entries?item=Synthetic-Movement-1&itemKind=movement");
        Assert.Equal(3, Assert.Single(movement.GetProperty("entries").EnumerateArray()).GetProperty("seq").GetInt64());
        var wrongKind = await GetAsync(client, $"/companies/{seed.Company}/audit-entries?item={seed.Item}&itemKind=movement");
        Assert.Empty(wrongKind.GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public async Task Date_actor_and_action_filters_compose_and_system_actor_is_literal()
    {
        var seed = await SeedAsync();
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        var all = await GetAsync(client, $"/companies/{seed.Company}/audit-entries");
        var first = Uri.EscapeDataString(all.GetProperty("entries")[0].GetProperty("occurredAt").GetString()!);
        var last = Uri.EscapeDataString(all.GetProperty("entries")[2].GetProperty("occurredAt").GetString()!);
        var system = await GetAsync(client, $"/companies/{seed.Company}/audit-entries?actor=system&action=synthetic.changed");
        Assert.Equal("system", Assert.Single(system.GetProperty("entries").EnumerateArray()).GetProperty("actor").GetString());
        var filtered = await GetAsync(client, $"/companies/{seed.Company}/audit-entries?from={last}&to={last}&actor={seed.Actor}&action=synthetic.accepted&item={seed.Item}");
        Assert.Equal(3, Assert.Single(filtered.GetProperty("entries").EnumerateArray()).GetProperty("seq").GetInt64());
        var before = await GetAsync(client, $"/companies/{seed.Company}/audit-entries?to={first}");
        Assert.Equal(1, Assert.Single(before.GetProperty("entries").EnumerateArray()).GetProperty("seq").GetInt64());
    }

    [Fact]
    public async Task Keyset_pages_are_stable_with_appends_between_pages()
    {
        var seed = await SeedAsync();
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        var first = await GetAsync(client, $"/companies/{seed.Company}/audit-entries?limit=1");
        Assert.Equal(1, first.GetProperty("nextAfterSeq").GetInt64());
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await using (var tx = await owner.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await AuditDatabaseTests.SetContextAsync(owner, tx, seed.Company);
            await AuditDatabaseTests.AppendAsync(owner, tx, seed.Company, seed.Actor);
            await tx.CommitAsync(TestContext.Current.CancellationToken);
        }
        var second = await GetAsync(client, $"/companies/{seed.Company}/audit-entries?limit=2&afterSeq=1");
        Assert.Equal([2L, 3L], second.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("seq").GetInt64()));
        Assert.Equal(3, second.GetProperty("nextAfterSeq").GetInt64());
        var last = await GetAsync(client, $"/companies/{seed.Company}/audit-entries?limit=2&afterSeq=3");
        Assert.Equal(4, Assert.Single(last.GetProperty("entries").EnumerateArray()).GetProperty("seq").GetInt64());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("nextAfterSeq").ValueKind);
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=201")]
    [InlineData("afterSeq=-1")]
    [InlineData("actor=invalid")]
    [InlineData("from=2026-02-01T00:00:00Z&to=2026-01-01T00:00:00Z")]
    [InlineData("from=not-a-date")]
    [InlineData("itemKind=cfdi")]
    public async Task Invalid_filters_are_refused(string query)
    {
        var seed = await SeedAsync();
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        using var response = await client.GetAsync($"/companies/{seed.Company}/audit-entries?{query}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("accountant")]
    [InlineData("administrator")]
    public async Task Every_member_role_can_read_archived_audit_and_verify_but_no_route_allows_changes(string role)
    {
        var seed = await SeedAsync(role);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync(new CommandDefinition("UPDATE company SET archived_at=now() WHERE id=@Company", new { seed.Company }, cancellationToken: TestContext.Current.CancellationToken));
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        var page = await GetAsync(client, $"/companies/{seed.Company}/audit-entries");
        Assert.Equal(3, page.GetProperty("entries").GetArrayLength());
        var verification = await GetAsync(client, $"/companies/{seed.Company}/audit/verification");
        Assert.True(verification.GetProperty("consistent").GetBoolean());
        Assert.Equal(3, verification.GetProperty("entries").GetInt64());
        foreach (var route in new[] { "audit-entries", "audit/verification" })
            foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
            {
                using var request = new HttpRequestMessage(method, $"/companies/{seed.Company}/{route}");
                using var refused = await client.SendAsync(request, TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.MethodNotAllowed, refused.StatusCode);
            }
        Assert.Equal(3, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM audit_entry WHERE company_id=@Company", new { seed.Company }));
    }

    [Fact(DisplayName = "Other company's entries")]
    public async Task Nonmembers_get_the_same_not_found_for_existing_and_unknown_companies_and_verification()
    {
        var own = await SeedAsync();
        var foreign = await SeedAsync();
        using var client = await fixture.LoginAsync(own.Email);
        foreach (var company in new[] { foreign.Company, Guid.NewGuid() })
        {
            client.DefaultRequestHeaders.Remove("X-Company-Id");
            client.DefaultRequestHeaders.Add("X-Company-Id", company.ToString());
            foreach (var route in new[] { "audit-entries", "audit/verification" })
            {
                using var response = await client.GetAsync($"/companies/{company}/{route}", TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                Assert.Equal("", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            }
        }
    }

    [Fact]
    public async Task Maximum_page_is_200_and_item_filter_uses_a_company_index_across_20000_plus_rows()
    {
        var seed = await SeedAsync();
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await using (var tx = await owner.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await AuditDatabaseTests.SetContextAsync(owner, tx, seed.Company);
            await owner.ExecuteAsync(new CommandDefinition("""
                SELECT append_audit_entry(@company,'user',@actor,'synthetic.bulk',
                  jsonb_build_array(jsonb_build_object('kind','movement','id',n::text)),'{}') FROM generate_series(1,20000) n
                """, new { company = seed.Company, actor = seed.Actor }, tx, commandTimeout: 60, cancellationToken: TestContext.Current.CancellationToken));
            await tx.CommitAsync(TestContext.Current.CancellationToken);
        }
        foreach (var other in new[] { await fixture.SeedCompanyAsync(seed.Actor), await fixture.SeedCompanyAsync(seed.Actor) })
        {
            await using var tx = await owner.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await AuditDatabaseTests.SetContextAsync(owner, tx, other);
            await owner.ExecuteAsync(new CommandDefinition("SELECT append_audit_entry(@other,'user',@actor,'synthetic.other','[]','{}') FROM generate_series(1,1000)",
                new { other, actor = seed.Actor }, tx, cancellationToken: TestContext.Current.CancellationToken));
            await tx.CommitAsync(TestContext.Current.CancellationToken);
        }
        await owner.ExecuteAsync(new CommandDefinition("ANALYZE audit_entry", cancellationToken: TestContext.Current.CancellationToken));
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        var page = await GetAsync(client, $"/companies/{seed.Company}/audit-entries?limit=200");
        Assert.Equal(200, page.GetProperty("entries").GetArrayLength());
        Assert.Equal(200, page.GetProperty("nextAfterSeq").GetInt64());
        await using var app = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await app.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await app.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await AuditDatabaseTests.SetContextAsync(app, transaction, seed.Company);
        await app.ExecuteAsync(new CommandDefinition("SET LOCAL enable_seqscan=off", transaction: transaction, cancellationToken: TestContext.Current.CancellationToken));
        var plan = await app.QueryAsync<string>(new CommandDefinition("EXPLAIN SELECT id FROM audit_entry WHERE items @> '[{\"id\":\"19999\"}]'", transaction: transaction, cancellationToken: TestContext.Current.CancellationToken));
        var text = string.Join('\n', plan);
        Assert.True(text.Contains("audit_company_sequence", StringComparison.Ordinal) || text.Contains("audit_chronological_idx", StringComparison.Ordinal), text);
        Assert.DoesNotContain("Seq Scan", text, StringComparison.Ordinal);
        Assert.Contains("Filter:", text, StringComparison.Ordinal);
        Assert.True((await AuditChainTests.VerifyAsync(app, transaction, seed.Company)).Consistent);
        Assert.False(await fixture.Database.ScalarAsync<bool>("SELECT to_regclass('audit_items_idx') IS NOT NULL"), "Do not maintain an unused GIN index under FORCE RLS.");
    }
}
