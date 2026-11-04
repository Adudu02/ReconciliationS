using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Reconciliation.Core.Jobs;
using Reconciliation.Core.Tenancy;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class MatchingRunTests(ApiFixture fixture)
{
    [Fact]
    public async Task Worked_example_uploaded_through_API_runs_in_worker_with_derived_states_and_progress()
    {
        var seed = await MatchingRunScenario.SeedAsync(fixture);
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        var job = await MatchingRunScenario.RunAsync(fixture, client, seed.Company);
        Assert.Equal("succeeded", job.GetProperty("state").GetString());
        Assert.Equal(2, job.GetProperty("progressDone").GetInt32());
        Assert.Equal(2, job.GetProperty("progressTotal").GetInt32());
        await using var owner = await fixture.Database.OpenOwnerAsync();
        var links = (await owner.QueryAsync<(string State, decimal Amount)>(new CommandDefinition("""
            SELECT l.state,l.amount_applied FROM link l JOIN bank_movement m ON m.id=l.movement_id
            WHERE l.company_id=@company ORDER BY m.movement_date
            """, new { company = seed.Company }, cancellationToken: TestContext.Current.CancellationToken))).ToArray();
        Assert.Equal(new[] { ("reconciled", 5800m), ("suggested", 5800m) }, links);
        await fixture.App.Services.GetRequiredService<CompanyScope>().ExecuteAsync(seed.Company, async (connection, transaction, token) =>
        {
            Assert.Equal(["reconciled", "suggested"], await connection.QueryAsync<string>(new CommandDefinition("SELECT s.state FROM movement_state() s JOIN bank_movement m ON m.id=s.id ORDER BY m.movement_date", transaction: transaction, cancellationToken: token)));
            Assert.Equal(("suggested", 50m, 5800m, 0m), await connection.QuerySingleAsync<(string, decimal, decimal, decimal)>(new CommandDefinition("SELECT state,paid_percentage,pending_amount,uncovered_amount FROM cfdi_state()", transaction: transaction, cancellationToken: token)));
            return true;
        }, TestContext.Current.CancellationToken);
        var beforeAudit = await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM audit_entry WHERE company_id=@company", new { company = seed.Company });
        await MatchingRunScenario.RunAsync(fixture, client, seed.Company);
        Assert.Equal(2, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM link WHERE company_id=@company", new { company = seed.Company }));
        Assert.Equal(beforeAudit, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM audit_entry WHERE company_id=@company", new { company = seed.Company }));
    }

    [Fact]
    public async Task Later_complement_upgrades_same_link_id_audits_previous_values_and_rerun_is_unchanged()
    {
        var seed = await MatchingRunScenario.SeedAsync(fixture, complement: false, second: false, firstAmount: "11600.00");
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        await MatchingRunScenario.RunAsync(fixture, client, seed.Company);
        var link = await fixture.Database.ScalarAsync<Guid>("SELECT id FROM link WHERE company_id=@company", new { company = seed.Company });
        using var body = IngestionHttpTests.CfdiBody(SyntheticCfdi.Payment(seed.InvoiceUuid, remaining: "0.00", paid: "11600.00"));
        using var uploaded = await client.PostAsync($"/companies/{seed.Company}/imports/cfdi", body, TestContext.Current.CancellationToken);
        await fixture.CompleteImportAsync(client, uploaded);
        await MatchingRunScenario.RunAsync(fixture, client, seed.Company);
        Assert.Equal(link, await fixture.Database.ScalarAsync<Guid>("SELECT id FROM link WHERE company_id=@company AND state='reconciled' AND payment_ref IS NOT NULL", new { company = seed.Company }));
        Assert.Equal("suggested", await fixture.Database.ScalarAsync<string>("SELECT detail->'old'->>'state' FROM audit_entry WHERE company_id=@company AND action='link.upgraded'", new { company = seed.Company }));
        await MatchingRunScenario.RunAsync(fixture, client, seed.Company);
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM link WHERE company_id=@company", new { company = seed.Company }));
        Assert.Equal(2, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM audit_entry WHERE company_id=@company AND action LIKE 'link.%'", new { company = seed.Company }));
    }

    [Fact]
    public async Task Failure_after_handler_rolls_back_links_audit_and_chain_head_together()
    {
        var seed = await MatchingRunScenario.SeedAsync(fixture);
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        using var response = await client.PostAsync($"/companies/{seed.Company}/reconciliation-runs", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var store = fixture.App.Services.GetRequiredService<JobStore>();
        var job = (await store.ClaimAsync("synthetic-rollback", 60, TestContext.Current.CancellationToken))!;
        Assert.Equal(seed.Company, job.CompanyId);
        var before = await fixture.Database.ScalarAsync<long>("SELECT last_seq FROM audit_chain_head WHERE company_id=@company", new { company = seed.Company });
        var handler = fixture.App.Services.GetServices<IJobHandler>().Single(handler => handler.Type == "reconciliation_run");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.App.Services.GetRequiredService<CompanyScope>().ExecuteAsync<int>(seed.Company, async (connection, transaction, token) =>
        {
            await handler.HandleAsync(job, connection, transaction, token);
            Assert.Equal(2, await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM link", transaction: transaction, cancellationToken: token)));
            throw new InvalidOperationException("Synthetic failure after matching and audit");
        }, TestContext.Current.CancellationToken));
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM link WHERE company_id=@company", new { company = seed.Company }));
        Assert.Equal(before, await fixture.Database.ScalarAsync<long>("SELECT last_seq FROM audit_chain_head WHERE company_id=@company", new { company = seed.Company }));
        Assert.True(await store.FinishAsync(job, "synthetic-rollback", "permanent", "Synthetic rollback", TimeSpan.Zero, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Company_archived_after_enqueue_is_read_only_to_matching_worker()
    {
        var seed = await MatchingRunScenario.SeedAsync(fixture);
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        using var response = await client.PostAsync($"/companies/{seed.Company}/reconciliation-runs", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync(new CommandDefinition("UPDATE company SET archived_at=now() WHERE id=@company", new { company = seed.Company }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await fixture.App.Services.GetRequiredService<JobDispatcher>().RunOneAsync("synthetic-archived-matching", TestContext.Current.CancellationToken));
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var job = await client.GetFromJsonAsync<JsonElement>($"/companies/{seed.Company}/jobs/{receipt.GetProperty("jobId").GetGuid()}", TestContext.Current.CancellationToken);
        Assert.Equal("failed", job.GetProperty("state").GetString());
        Assert.Contains("read-only", job.GetProperty("lastError").GetString()!, StringComparison.Ordinal);
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM link WHERE company_id=@company", new { company = seed.Company }));
    }
}

internal static class MatchingRunScenario
{
    internal sealed record Seed(Guid Company, Guid Actor, string Email, Guid InvoiceUuid);

    internal static async Task<Seed> SeedAsync(ApiFixture fixture, bool complement = true, bool second = true, string firstAmount = "5800.00")
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        using var client = await fixture.LoginAsync(user.Email, company);
        var uuid = Guid.NewGuid();
        var invoice = SyntheticCfdi.Invoice(root =>
        {
            root.SetAttributeValue("SubTotal", "10000.00"); root.SetAttributeValue("Descuento", "0.00"); root.SetAttributeValue("Total", "11600.00");
            root.SetAttributeValue("Fecha", "2026-10-02T12:00:00"); root.SetAttributeValue("Serie", "FC"); root.SetAttributeValue("Folio", "0001");
            root.Element(SyntheticCfdi.Cfdi + "Receptor")!.SetAttributeValue("Rfc", "XEXX010101000");
            var taxes = root.Element(SyntheticCfdi.Cfdi + "Impuestos")!;
            taxes.SetAttributeValue("TotalImpuestosTrasladados", "1600.00"); taxes.SetAttributeValue("TotalImpuestosRetenidos", "0.00");
            root.Descendants(SyntheticCfdi.Stamp + "TimbreFiscalDigital").Single().SetAttributeValue("UUID", uuid);
        });
        using var cfdiBody = IngestionHttpTests.CfdiBody(complement ? [invoice, SyntheticCfdi.Payment(uuid)] : [invoice]);
        using var cfdiUploaded = await client.PostAsync($"/companies/{company}/imports/cfdi", cfdiBody, TestContext.Current.CancellationToken);
        var report = await fixture.CompleteImportAsync(client, cfdiUploaded);
        Assert.All(report.GetProperty("files").EnumerateArray(), file => Assert.Equal("imported", file.GetProperty("status").GetString()));
        var csv = "fecha,concepto,abono,cargo,saldo\n2026-10-10,SPEI REC 0034 PAGO FACT 0001," + firstAmount + ",0," + firstAmount + "\n";
        if (second) csv += "2026-10-20,SPEI REC 0071 XEXX COMERCIAL,5800.00,0,11600.00\n";
        using var bankBody = new MultipartFormDataContent
        {
            { new StringContent("reference"), "bank" }, { new StringContent("Synthetic-Matching"), "accountReference" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "files", "synthetic.csv" }
        };
        using var bankUploaded = await client.PostAsync($"/companies/{company}/imports/bank-statement", bankBody, TestContext.Current.CancellationToken);
        var bankReport = await fixture.CompleteImportAsync(client, bankUploaded);
        Assert.Equal(second ? 2 : 1, bankReport.GetProperty("files")[0].GetProperty("importedMovements").GetInt32());
        return new(company, user.Id, user.Email, uuid);
    }

    internal static async Task<JsonElement> RunAsync(ApiFixture fixture, HttpClient client, Guid company)
    {
        using var response = await client.PostAsync($"/companies/{company}/reconciliation-runs", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var jobId = receipt.GetProperty("jobId").GetGuid();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var job = await client.GetFromJsonAsync<JsonElement>($"/companies/{company}/jobs/{jobId}", deadline.Token);
            Assert.True(job.GetProperty("state").GetString() != "failed", job.GetRawText());
            if (job.GetProperty("state").GetString() == "succeeded") return job;
            if (!await fixture.App.Services.GetRequiredService<JobDispatcher>().RunOneAsync("synthetic-matching-worker", deadline.Token)) await Task.Yield();
        }
    }
}
