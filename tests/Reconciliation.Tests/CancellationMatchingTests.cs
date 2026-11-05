using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Reconciliation.Core.Ingestion;
using Reconciliation.Core.Matching;
using Reconciliation.Core.Tenancy;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class CancellationMatchingTests(ApiFixture fixture)
{
    [Theory(DisplayName = "Invoice found cancelled / Payment complement found cancelled")]
    [InlineData(false, "cancelled invoice")]
    [InlineData(true, "cancelled payment complement")]
    public async Task Cancellation_moves_links_to_review_preserves_decisions_and_audits_atomically(bool payment, string reason)
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        await InvoiceReviewReasonTests.InsertManualLinkAsync(fixture, seed, 100, "reconciled");
        var target = seed.Invoice;
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync(new CommandDefinition("UPDATE link SET decided_by=@actor,decided_at='2026-10-11' WHERE company_id=@company", new { actor = seed.Actor, company = seed.Company }, cancellationToken: TestContext.Current.CancellationToken));
        if (payment)
        {
            var report = await fixture.App.Services.GetRequiredService<CfdiImporter>().ImportAsync(seed.Company, seed.Actor,
                [UploadedFile.FromBytes("synthetic-payment.xml", SyntheticCfdi.Payment(seed.Uuid, "0", previous: "100", paid: "100"))], TestContext.Current.CancellationToken);
            target = report.Files[0].CfdiId!.Value;
            await owner.ExecuteAsync(new CommandDefinition("UPDATE link SET payment_cfdi_id=@target,payment_ref=(SELECT id FROM cfdi_payment WHERE cfdi_id=@target) WHERE company_id=@company", new { target, company = seed.Company }, cancellationToken: TestContext.Current.CancellationToken));
        }
        await ApplyAsync(seed.Company, target, "cancelled");
        var link = await owner.QuerySingleAsync<(string, string, string, Guid, decimal)>(new CommandDefinition(
            "SELECT state,review_reason,origin,decided_by,amount_applied FROM link WHERE company_id=@company", new { company = seed.Company }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(("suggested", reason, "user", seed.Actor, 100m), link);
        var amounts = await owner.QuerySingleAsync<(string, decimal, decimal, decimal)>(new CommandDefinition(
            "SELECT state,reconciled_amount,suggested_amount,uncovered_amount FROM movement_state() WHERE id=@movement", new { movement = seed.Movement }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(("suggested", 0m, 0m, 100m), amounts);
        var audit = JsonSerializer.Deserialize<JsonElement>((await owner.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT detail::text FROM audit_entry WHERE company_id=@company AND action='link.review_required'", new { company = seed.Company }, cancellationToken: TestContext.Current.CancellationToken)))!);
        Assert.Equal(seed.Actor.ToString(), audit.GetProperty("old").GetProperty("decided_by").GetString());
        Assert.Equal("sat_status_change", audit.GetProperty("source").GetString());
        var before = await AuditCountAsync(seed.Company);
        await ApplyAsync(seed.Company, target, "cancelled");
        Assert.Equal(before, await AuditCountAsync(seed.Company));
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        await MatchingRunScenario.RunAsync(fixture, client, seed.Company);
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM link WHERE company_id=@company", new { company = seed.Company }));
        var matching = (await client.GetFromJsonAsync<JsonElement>($"/companies/{seed.Company}/cfdi/{seed.Invoice}", TestContext.Current.CancellationToken)).GetProperty("matching");
        Assert.Contains(matching.GetProperty("reasons").EnumerateArray(), r => r.GetProperty("code").GetString() == reason && r.GetProperty("requiresReview").GetBoolean());
        Assert.Equal(payment ? 100 : 0, matching.GetProperty("pendingAmount").GetDecimal());
        Assert.False(matching.GetProperty("complementOverdue").GetBoolean());
    }

    [Fact(DisplayName = "Cancellation in process")]
    public async Task In_process_keeps_links_and_exposes_cancellation_status()
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        await InvoiceReviewReasonTests.InsertManualLinkAsync(fixture, seed, 100, "reconciled");
        await ApplyAsync(seed.Company, seed.Invoice, "current", "in process");
        Assert.Equal("reconciled", await fixture.Database.ScalarAsync<string>("SELECT state FROM link WHERE company_id=@company", new { company = seed.Company }));
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        var matching = (await client.GetFromJsonAsync<JsonElement>($"/companies/{seed.Company}/cfdi/{seed.Invoice}", TestContext.Current.CancellationToken)).GetProperty("matching");
        Assert.Equal("current", matching.GetProperty("satStatus").GetString());
        Assert.Equal("in process", matching.GetProperty("cancellationStatus").GetString());
        Assert.DoesNotContain(matching.GetProperty("reasons").EnumerateArray(), r => r.GetProperty("code").GetString() == "cancelled invoice");
    }

    [Fact]
    public async Task Cancelled_credit_note_only_restores_payable_and_audits_status()
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture);
        var note = await MatchingFiscalScenario.ImportNoteAsync(fixture, seed, 1160);
        await InvoiceReviewReasonTests.InsertManualLinkAsync(fixture, seed, 10440, "reconciled");
        await ApplyAsync(seed.Company, note, "cancelled");
        Assert.Equal("reconciled", await fixture.Database.ScalarAsync<string>("SELECT state FROM link WHERE company_id=@company", new { company = seed.Company }));
        Assert.Equal(11600, await fixture.Database.ScalarAsync<decimal>("SELECT payable FROM cfdi_payable() WHERE id=@invoice", new { invoice = seed.Invoice }));
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM audit_entry WHERE company_id=@company AND action='cfdi.sat_status_changed'", new { company = seed.Company }));
    }

    [Theory(DisplayName = "Replacement found")]
    [InlineData("valid", true)]
    [InlineData("receiver", false)]
    [InlineData("currency", false)]
    [InlineData("received", false)]
    [InlineData("global", false)]
    [InlineData("cancelled", false)]
    [InlineData("code03", false)]
    [InlineData("zero", false)]
    [InlineData("rejected", false)]
    public async Task Replacement_uses_direct_valid_same_customer_relation_and_free_capacity(string variant, bool expected)
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        await InvoiceReviewReasonTests.InsertManualLinkAsync(fixture, seed, 100, "reconciled");
        var replacement = await ImportReplacementAsync(seed, variant);
        if (variant == "rejected")
        {
            await using var owner = await fixture.Database.OpenOwnerAsync();
            await owner.ExecuteAsync(new CommandDefinition("INSERT INTO link(company_id,movement_id,cfdi_id,amount_applied,state,origin,criteria,explanation) VALUES(@company,@movement,@replacement,50,'rejected','user','[]','Synthetic rejection')", new { company = seed.Company, movement = seed.Movement, replacement }, cancellationToken: TestContext.Current.CancellationToken));
        }
        await ApplyAsync(seed.Company, seed.Invoice, "cancelled");
        await using var read = await fixture.Database.OpenOwnerAsync();
        var proposed = (await read.QueryAsync<(string, decimal, string)>(new CommandDefinition("SELECT state,amount_applied,criteria::text FROM link WHERE cfdi_id=@replacement AND state='suggested'", new { replacement }, cancellationToken: TestContext.Current.CancellationToken))).ToArray();
        if (expected)
        {
            var link = Assert.Single(proposed);
            Assert.Equal("suggested", link.Item1); Assert.Equal(50, link.Item2);
            Assert.Contains("replacement of cancelled invoice", link.Item3, StringComparison.Ordinal);
        }
        else Assert.Empty(proposed);
        var before = await AuditCountAsync(seed.Company);
        await ApplyAsync(seed.Company, seed.Invoice, "cancelled");
        Assert.Equal(before, await AuditCountAsync(seed.Company));
    }

    [Fact]
    public async Task Worker_discovers_replacement_imported_after_cancellation_once()
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        await InvoiceReviewReasonTests.InsertManualLinkAsync(fixture, seed, 100, "reconciled");
        await ApplyAsync(seed.Company, seed.Invoice, "cancelled");
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        await MatchingRunScenario.RunAsync(fixture, client, seed.Company);
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM link WHERE company_id=@company", new { company = seed.Company }));
        var replacement = await ImportReplacementAsync(seed);
        await MatchingRunScenario.RunAsync(fixture, client, seed.Company);
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>("SELECT count(*) FROM link WHERE cfdi_id=@replacement AND state='suggested'", new { replacement }));
        var before = await AuditCountAsync(seed.Company);
        await MatchingRunScenario.RunAsync(fixture, client, seed.Company);
        Assert.Equal(before, await AuditCountAsync(seed.Company));
    }

    [Fact]
    public async Task Failure_after_status_change_rolls_back_status_links_and_audit()
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        await InvoiceReviewReasonTests.InsertManualLinkAsync(fixture, seed, 100, "reconciled");
        var before = await AuditCountAsync(seed.Company);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Scope.ExecuteWriteAsync<int>(seed.Company, async (c, t, token) =>
        {
            await InvokeAsync(c, t, seed.Invoice, "cancelled", null, token);
            throw new InvalidOperationException("Synthetic rollback after cascade");
        }, TestContext.Current.CancellationToken));
        Assert.Equal("unknown", await fixture.Database.ScalarAsync<string>("SELECT sat_status FROM cfdi WHERE id=@invoice", new { invoice = seed.Invoice }));
        Assert.Equal("reconciled", await fixture.Database.ScalarAsync<string>("SELECT state FROM link WHERE company_id=@company", new { company = seed.Company }));
        Assert.Equal(before, await AuditCountAsync(seed.Company));
    }

    [Fact]
    public async Task Domain_operation_is_tenant_guarded_and_archive_read_only()
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        var (_, other) = await fixture.SeedCompanyWithAdminAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync(other, seed.Invoice, "cancelled"));
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync(new CommandDefinition("UPDATE company SET archived_at=now() WHERE id=@company", new { company = seed.Company }, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CompanyReadOnlyException>(() => ApplyAsync(seed.Company, seed.Invoice, "cancelled"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Engine_excludes_cancelled_invoice_or_complement(bool payment)
    {
        var invoice = SyntheticMatching.Invoice(); var complement = SyntheticMatching.Payment();
        if (payment) typeof(MatchingPayment).GetProperty("SatStatus")!.SetValue(complement, "cancelled");
        else typeof(MatchingInvoice).GetProperty("SatStatus")!.SetValue(invoice, "cancelled");
        var result = MatchingEngine.Run([SyntheticMatching.FirstMovement()], [invoice], [complement], [], new(3, .01m));
        if (payment) Assert.DoesNotContain(result.Changes, link => link.State == "reconciled");
        else Assert.Empty(result.Changes);
    }

    [Fact]
    public void Cancellation_marked_links_release_capacity_but_hold_their_movement()
    {
        var invoice = SyntheticMatching.Invoice(100); var held = SyntheticMatching.FirstMovement(100); var free = held with { Id = Guid.NewGuid() };
        var link = new MatchingLink(held.Id, invoice.Id, null, null, 100, "suggested", "automatic", [], "high", "Cancelled history");
        typeof(MatchingLink).GetProperty("ReviewReason")!.SetValue(link, "cancelled payment complement");
        var result = MatchingEngine.Run([held, free], [invoice], [], [link], new(3, .01m));
        Assert.Equal(free.Id, Assert.Single(result.Changes).MovementId);
        Assert.Empty(result.Updates);
        Assert.Equal(100, Assert.Single(result.Invoices).SuggestedAmount);
    }

    private CompanyScope Scope => fixture.App.Services.GetRequiredService<CompanyScope>();
    private Task<int> ApplyAsync(Guid company, Guid cfdi, string status, string? cancellation = null) => Scope.ExecuteWriteAsync(company,
        (c, t, token) => InvokeAsync(c, t, cfdi, status, cancellation, token), TestContext.Current.CancellationToken);
    private Task<int> InvokeAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid cfdi, string status, string? cancellation, CancellationToken token) =>
        (Task<int>)typeof(MatchingEngine).Assembly.GetType("Reconciliation.Core.Matching.SatStatusChanges", true)!
            .GetMethod("ApplySatStatusChangeAsync")!.Invoke(null, [c, t, cfdi, status, cancellation, fixture.Clock.GetUtcNow(), token])!;
    private Task<int> AuditCountAsync(Guid company) => fixture.Database.ScalarAsync<int>("SELECT count(*) FROM audit_entry WHERE company_id=@company", new { company });

    private async Task<Guid> ImportReplacementAsync(MatchingFiscalScenario.Seed seed, string variant = "valid")
    {
        var bytes = MatchingFiscalScenario.Invoice(variant == "zero" ? 0 : 50, root =>
        {
            root.SetAttributeValue("Fecha", "2027-01-01T12:00:00");
            root.Add(new XElement(SyntheticCfdi.Cfdi + "CfdiRelacionados", new XAttribute("TipoRelacion", variant == "code03" ? "03" : "04"),
                new XElement(SyntheticCfdi.Cfdi + "CfdiRelacionado", new XAttribute("UUID", seed.Uuid))));
            if (variant == "currency") root.SetAttributeValue("Moneda", "USD");
            if (variant == "receiver") root.Element(SyntheticCfdi.Cfdi + "Receptor")!.SetAttributeValue("Rfc", "CCC010101CCC");
            if (variant == "global") root.Element(SyntheticCfdi.Cfdi + "Receptor")!.SetAttributeValue("Rfc", "XAXX010101000");
            if (variant == "received")
            {
                root.Element(SyntheticCfdi.Cfdi + "Emisor")!.SetAttributeValue("Rfc", SyntheticCfdi.CounterpartyRfc);
                root.Element(SyntheticCfdi.Cfdi + "Receptor")!.SetAttributeValue("Rfc", SyntheticCfdi.CompanyRfc);
            }
        });
        var report = await fixture.App.Services.GetRequiredService<CfdiImporter>().ImportAsync(seed.Company, seed.Actor,
            [UploadedFile.FromBytes("synthetic-replacement.xml", bytes)], TestContext.Current.CancellationToken);
        ImportAssertions.AssertSingleStatus(report, "imported");
        var id = report.Files[0].CfdiId!.Value;
        if (variant == "cancelled")
        {
            await using var owner = await fixture.Database.OpenOwnerAsync();
            await owner.ExecuteAsync(new CommandDefinition("UPDATE cfdi SET sat_status='cancelled' WHERE id=@id", new { id }, cancellationToken: TestContext.Current.CancellationToken));
        }
        return id;
    }
}
