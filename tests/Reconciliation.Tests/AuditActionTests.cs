using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Reconciliation.Core.Audit;
using Reconciliation.Core.Ingestion;
using Reconciliation.Core.Jobs;
using Reconciliation.Core.Tenancy;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class AuditActionTests(ApiFixture fixture)
{
    [Fact]
    public async Task Accepted_duplicate_and_rejected_CFDI_files_each_record_actor_and_typed_items()
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        var uuid = Guid.NewGuid();
        var importer = fixture.App.Services.GetRequiredService<CfdiImporter>();
        var files = new[] { UploadedFile.FromBytes("first.xml", SyntheticCfdi.Invoice(root => root.Descendants(SyntheticCfdi.Stamp + "TimbreFiscalDigital").Single().SetAttributeValue("UUID", uuid))), UploadedFile.FromBytes("duplicate.xml", SyntheticCfdi.Invoice(root => root.Descendants(SyntheticCfdi.Stamp + "TimbreFiscalDigital").Single().SetAttributeValue("UUID", uuid))), UploadedFile.FromBytes("invalid.xml", Encoding.UTF8.GetBytes("<invalid/>")) };
        var report = await importer.ImportAsync(company, user.Id, files, TestContext.Current.CancellationToken);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        var entries = (await owner.QueryAsync<(string Action, Guid Actor, string Items)>(new CommandDefinition(
            "SELECT action,actor_id,items::text FROM audit_entry WHERE company_id=@company ORDER BY company_seq", new { company }, cancellationToken: TestContext.Current.CancellationToken))).ToArray();
        Assert.Equal(["cfdi.imported", "cfdi.already_imported", "cfdi.rejected"], entries.Select(e => e.Action));
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(user.Id, entries[i].Actor);
            var items = JsonSerializer.Deserialize<JsonElement>(entries[i].Items);
            Assert.Equal(JsonValueKind.Array, items.ValueKind);
            Assert.Contains(items.EnumerateArray(), item => item.GetProperty("kind").GetString() == "import_file" && item.GetProperty("id").GetGuid() == report.Files[i].FileId);
            if (i < 2) Assert.Contains(items.EnumerateArray(), item => item.GetProperty("kind").GetString() == "cfdi" && item.GetProperty("id").GetGuid() == uuid);
        }
    }

    [Fact]
    public async Task Statement_files_include_the_affected_movement_ids_even_for_duplicate_overlap()
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        var file = UploadedFile.FromBytes("synthetic.csv", Encoding.UTF8.GetBytes("fecha,concepto,abono,cargo,saldo\n2026-10-10,SYNTHETIC,1,0,1\n"));
        var importer = fixture.App.Services.GetRequiredService<BankStatementImporter>();
        await importer.ImportAsync(company, user.Id, "reference", "Synthetic-1234", [file, file], TestContext.Current.CancellationToken);
        await importer.ImportAsync(company, user.Id, "reference", "Synthetic-1234", [UploadedFile.FromBytes("bad.csv", Encoding.UTF8.GetBytes("invalid"))], TestContext.Current.CancellationToken);
        var movement = await fixture.Database.ScalarAsync<Guid>("SELECT id FROM bank_movement WHERE company_id=@company", new { company });
        Assert.Equal(2, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM audit_entry WHERE company_id=@company AND items @> @item::jsonb", new { company, item = JsonSerializer.Serialize(new[] { new { kind = "movement", id = movement.ToString() } }) }));
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM audit_entry WHERE company_id=@company AND action='bank-statement.rejected'", new { company }));
    }

    [Fact]
    public async Task Member_added_and_changed_are_audited_but_unchanged_and_refused_memberships_are_not()
    {
        var (admin, company) = await fixture.SeedCompanyWithAdminAsync();
        var member = await fixture.SeedUserAsync();
        using var client = await fixture.LoginAsync(admin.Email, company);
        foreach (var role in new[] { "viewer", "accountant", "accountant" })
        {
            using var response = await client.PostAsJsonAsync($"/companies/{company}/members", new { userId = member.Id, role }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        using var refused = await client.PostAsJsonAsync($"/companies/{company}/members", new { userId = admin.Id, role = "viewer" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        var changes = (await owner.QueryAsync<(string Action, string? Before, string After, Guid Actor)>(new CommandDefinition("""
            SELECT action,detail->'old'->>'role',detail->'new'->>'role',actor_id FROM audit_entry
            WHERE company_id=@company AND items @> @item::jsonb ORDER BY company_seq
            """, new { company, item = JsonSerializer.Serialize(new[] { new { kind = "user", id = member.Id.ToString() } }) }, cancellationToken: TestContext.Current.CancellationToken))).ToArray();
        Assert.Equal(2, changes.Length);
        Assert.Equal(("member.added", null, "viewer", admin.Id), changes[0]);
        Assert.Equal(("member.changed", "viewer", "accountant", admin.Id), changes[1]);
    }

    [Fact]
    public async Task Failure_after_action_and_audit_rolls_back_both_entry_and_chain_head()
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        var scope = fixture.App.Services.GetRequiredService<CompanyScope>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ExecuteWriteAsync<int>(company, async (connection, transaction, token) =>
        {
            await connection.ExecuteAsync(new CommandDefinition("UPDATE company SET name='rolled back'", transaction: transaction, cancellationToken: token));
            await AuditLog.WriteAsync(connection, transaction, company, user.Id, "synthetic.changed", null, new { name = "rolled back" }, token);
            throw new InvalidOperationException("Synthetic failure after audit");
        }, TestContext.Current.CancellationToken));
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM audit_entry WHERE company_id=@company", new { company }));
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM audit_chain_head WHERE company_id=@company", new { company }));
        Assert.Equal("Synthetic company", await fixture.Database.ScalarAsync<string>("SELECT name FROM company WHERE id=@company", new { company }));
    }

    [Fact(DisplayName = "User confirms a link")]
    public void User_confirms_a_link() => Assert.Fail("Replace with real user decision audit scenario.");

    [Fact(DisplayName = "Automatic link")]
    public async Task Automatic_link()
    {
        var seed = await MatchingRunScenario.SeedAsync(fixture);
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        var job = await MatchingRunScenario.RunAsync(fixture, client, seed.Company);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        var entries = (await owner.QueryAsync<(string ActorType, Guid? ActorId, string Items, string Detail)>(new CommandDefinition(
            "SELECT actor_type,actor_id,items::text,detail::text FROM audit_entry WHERE company_id=@company AND action='link.created' ORDER BY company_seq",
            new { company = seed.Company }, cancellationToken: TestContext.Current.CancellationToken))).ToArray();
        Assert.Equal(2, entries.Length);
        foreach (var entry in entries)
        {
            Assert.Equal("system", entry.ActorType);
            Assert.Null(entry.ActorId);
            var detail = JsonSerializer.Deserialize<JsonElement>(entry.Detail);
            Assert.Equal(job.GetProperty("id").GetGuid(), detail.GetProperty("jobId").GetGuid());
            Assert.Equal("reconciliation_run", detail.GetProperty("source").GetString());
            Assert.NotEmpty(detail.GetProperty("new").GetProperty("criteria").EnumerateArray());
            var items = JsonSerializer.Deserialize<JsonElement>(entry.Items).EnumerateArray().ToArray();
            Assert.Equal(["cfdi", "link", "movement"], items.Select(item => item.GetProperty("kind").GetString()).Order());
            Assert.Contains(items, item => item.GetProperty("kind").GetString() == "cfdi" && item.GetProperty("id").GetGuid() == seed.InvoiceUuid);
        }
    }

    [Fact(DisplayName = "SAT status change")]
    public async Task SAT_status_change()
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        await InvoiceReviewReasonTests.InsertManualLinkAsync(fixture, seed, 100, "reconciled");
        fixture.Sat.Outcomes[seed.Uuid] = _ => Task.FromResult(SatJobTests.Answer("cancelled"));
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        var job = await SatJobTests.EnqueueAsync(client, seed);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (!await fixture.Database.ScalarAsync<bool>("SELECT state='succeeded' FROM job WHERE id=@job", new { job }))
        { deadline.Token.ThrowIfCancellationRequested(); await fixture.App.Services.GetRequiredService<JobDispatcher>().RunOneAsync("synthetic-sat-audit", deadline.Token); }
        await using var owner = await fixture.Database.OpenOwnerAsync();
        var row = await owner.QuerySingleAsync<(string Actor, Guid? ActorId, string Items, string Detail)>(new CommandDefinition(
            "SELECT actor_type,actor_id,items::text,detail::text FROM audit_entry WHERE company_id=@company AND action='sat.status_changed'", new { company = seed.Company }, cancellationToken: deadline.Token));
        Assert.Equal("system", row.Actor); Assert.Null(row.ActorId);
        var detail = JsonSerializer.Deserialize<JsonElement>(row.Detail);
        Assert.Equal(job, detail.GetProperty("jobId").GetGuid());
        Assert.Equal("cancelled", detail.GetProperty("new").GetProperty("state").GetString());
        Assert.Equal("200", detail.GetProperty("new").GetProperty("efosRaw").GetString());
        Assert.DoesNotContain("issuerRfc", row.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("total", row.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(JsonSerializer.Deserialize<JsonElement>(row.Items).EnumerateArray(), item => item.GetProperty("kind").GetString() == "cfdi" && item.GetProperty("id").GetGuid() == seed.Uuid);
        Assert.Equal("suggested", await fixture.Database.ScalarAsync<string>("SELECT state FROM link WHERE company_id=@company", new { company = seed.Company }));
    }
}
