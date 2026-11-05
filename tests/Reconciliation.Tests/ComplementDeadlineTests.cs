using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FsCheck.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Reconciliation.Core.Ingestion;
using Reconciliation.Core.Matching;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class ComplementDeadlineTests(ApiFixture fixture)
{
    [Theory(DisplayName = "Complement overdue")]
    [InlineData("2026-10-10", false, "2026-11-05")]
    [InlineData("2026-08-10", false, "2026-09-05")]
    [InlineData("2026-08-10", true, "2026-09-07")]
    [InlineData("2026-06-10", true, "2026-07-06")]
    [InlineData("2026-12-31", true, "2027-01-05")]
    public void Fifth_of_next_month_and_optional_weekend_adjustment(string payment, bool businessDay, string due) =>
        Assert.Equal(DateOnly.Parse(due), Calculate(DateOnly.Parse(payment), businessDay));

    [Property(MaxTest = 150)]
    public void Deadline_is_fifth_or_following_Monday_only_when_enabled(int day, bool businessDay)
    {
        var payment = new DateOnly(2020, 1, 1).AddDays((int)(Math.Abs((long)day) % 36500));
        var fifth = new DateOnly(payment.Year, payment.Month, 5).AddMonths(1);
        var actual = Calculate(payment, businessDay);
        var weekend = fifth.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        Assert.Equal(businessDay && weekend ? fifth.AddDays(fifth.DayOfWeek == DayOfWeek.Saturday ? 2 : 1) : fifth, actual);
    }

    [Theory]
    [InlineData("valid", false)]
    [InlineData("no-payment-ref", true)]
    [InlineData("no-complement-id", true)]
    [InlineData("cancelled", true)]
    [InlineData("wrong-type", true)]
    [InlineData("wrong-payment", true)]
    [InlineData("wrong-invoice", true)]
    [InlineData("unlinked", true)]
    [InlineData("pue", false)]
    public async Task Coverage_requires_explicit_valid_payment_and_invoice_document(string scenario, bool overdue)
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        var (complement, payment) = await ImportPaymentAsync(seed);
        await InvoiceReviewReasonTests.InsertManualLinkAsync(fixture, seed, 100, "reconciled");
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync(new CommandDefinition("UPDATE link SET payment_cfdi_id=@complement,payment_ref=@payment WHERE company_id=@company", new { complement, payment, company = seed.Company }, cancellationToken: TestContext.Current.CancellationToken));
        var mutation = scenario switch
        {
            "no-payment-ref" => "UPDATE link SET payment_ref=NULL WHERE company_id=@company",
            "no-complement-id" => "UPDATE link SET payment_cfdi_id=NULL,payment_ref=NULL WHERE company_id=@company",
            "cancelled" => "UPDATE cfdi SET sat_status='cancelled' WHERE id=@complement",
            "wrong-type" => "UPDATE cfdi SET type='E' WHERE id=@complement",
            "wrong-payment" => "UPDATE link SET payment_cfdi_id=@invoice WHERE company_id=@company",
            "wrong-invoice" => "UPDATE cfdi_payment_doc SET invoice_uuid=gen_random_uuid() WHERE payment_id=@payment",
            "unlinked" => "UPDATE link SET payment_cfdi_id=NULL,payment_ref=NULL WHERE company_id=@company",
            "pue" => "UPDATE cfdi SET payment_method='PUE' WHERE id=@invoice; UPDATE link SET payment_cfdi_id=NULL,payment_ref=NULL WHERE company_id=@company",
            _ => "SELECT 1"
        };
        await owner.ExecuteAsync(new CommandDefinition(mutation, new { complement, payment, company = seed.Company, invoice = seed.Invoice }, cancellationToken: TestContext.Current.CancellationToken));
        var result = await ReasonsAsync(seed.Invoice);
        Assert.Equal(overdue, result.GetProperty("complement_overdue").GetBoolean());
        Assert.Equal(overdue ? "2026-11-05" : null, result.GetProperty("complement_due_date").GetString());
        Assert.Equal(overdue, result.GetProperty("reasons").EnumerateArray().Any(r => r.GetProperty("code").GetString() == "complement overdue"));
        var onDueDate = await ReasonsAsync(seed.Invoice, "2026-11-05");
        Assert.False(onDueDate.GetProperty("complement_overdue").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Payment_is_claimed_by_one_movement_but_covers_all_its_document_allocations(bool anotherMovement)
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 6000, 10000);
        var second = await fixture.App.Services.GetRequiredService<CfdiImporter>().ImportAsync(seed.Company, seed.Actor,
            [UploadedFile.FromBytes("synthetic-second.xml", MatchingFiscalScenario.Invoice(4000))], TestContext.Current.CancellationToken);
        var invoiceB = second.Files[0].CfdiId!.Value;
        var (complement, payment) = await ImportPaymentAsync(seed);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        var uuidB = await owner.ExecuteScalarAsync<Guid>(new CommandDefinition("SELECT uuid FROM cfdi WHERE id=@invoiceB", new { invoiceB }, cancellationToken: TestContext.Current.CancellationToken));
        await owner.ExecuteAsync(new CommandDefinition("""
            INSERT INTO cfdi_payment_doc(company_id,payment_id,invoice_uuid,installment,currency,previous_balance,paid,remaining_balance)
            VALUES(@company,@payment,@uuidB,1,'MXN',4000,4000,0);
            UPDATE cfdi_payment_doc SET previous_balance=6000,paid=6000,remaining_balance=0 WHERE payment_id=@payment AND invoice_uuid=@uuid;
            UPDATE cfdi_payment SET amount=10000 WHERE id=@payment;
            INSERT INTO link(company_id,movement_id,cfdi_id,payment_cfdi_id,payment_ref,amount_applied,state,origin,criteria,explanation,created_at)
            VALUES(@company,@movement,@invoice,@complement,@payment,6000,'reconciled','user','[]','First claim','2026-10-10');
            """, new { company = seed.Company, payment, uuidB, uuid = seed.Uuid, movement = seed.Movement, invoice = seed.Invoice, complement }, cancellationToken: TestContext.Current.CancellationToken));
        var movementB = seed.Movement;
        if (anotherMovement)
            movementB = await owner.ExecuteScalarAsync<Guid>(new CommandDefinition("""
                INSERT INTO bank_movement(company_id,bank_account_id,movement_date,description,credit,debit,balance)
                SELECT company_id,bank_account_id,movement_date,'Synthetic second payment',4000,0,14000 FROM bank_movement WHERE id=@movement RETURNING id
                """, new { movement = seed.Movement }, cancellationToken: TestContext.Current.CancellationToken));
        await owner.ExecuteAsync(new CommandDefinition("""
            INSERT INTO link(company_id,movement_id,cfdi_id,payment_cfdi_id,payment_ref,amount_applied,state,origin,criteria,explanation,created_at)
            VALUES(@company,@movementB,@invoiceB,@complement,@payment,4000,'reconciled','user','[]','Later allocation','2026-10-11')
            """, new { company = seed.Company, movementB, invoiceB, complement, payment }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False((await ReasonsAsync(seed.Invoice)).GetProperty("complement_overdue").GetBoolean());
        Assert.Equal(anotherMovement, (await ReasonsAsync(invoiceB)).GetProperty("complement_overdue").GetBoolean());
    }

    [Fact]
    public async Task Read_API_uses_injected_UTC_today_and_exposes_due_date()
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        await InvoiceReviewReasonTests.InsertManualLinkAsync(fixture, seed, 100, "reconciled");
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync(new CommandDefinition("UPDATE bank_movement SET movement_date='2026-09-10' WHERE id=@movement", new { movement = seed.Movement }, cancellationToken: TestContext.Current.CancellationToken));
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        var matching = (await client.GetFromJsonAsync<JsonElement>($"/companies/{seed.Company}/cfdi/{seed.Invoice}", TestContext.Current.CancellationToken)).GetProperty("matching");
        Assert.Equal("2026-10-05", matching.GetProperty("complementDueDate").GetString());
        Assert.True(matching.GetProperty("complementOverdue").GetBoolean());
    }

    private static DateOnly Calculate(DateOnly payment, bool businessDay) =>
        (DateOnly)typeof(MatchingEngine).Assembly.GetType("Reconciliation.Core.Matching.ComplementDeadline", true)!
            .GetMethod("Calculate")!.Invoke(null, [payment, businessDay])!;

    private async Task<(Guid Complement, Guid Payment)> ImportPaymentAsync(MatchingFiscalScenario.Seed seed)
    {
        var report = await fixture.App.Services.GetRequiredService<CfdiImporter>().ImportAsync(seed.Company, seed.Actor,
            [UploadedFile.FromBytes("synthetic-payment.xml", SyntheticCfdi.Payment(seed.Uuid, "0.00", previous: "100.00", paid: "100.00"))], TestContext.Current.CancellationToken);
        var complement = report.Files[0].CfdiId!.Value;
        var payment = await fixture.Database.ScalarAsync<Guid>("SELECT id FROM cfdi_payment WHERE cfdi_id=@complement", new { complement });
        return (complement, payment);
    }

    private async Task<JsonElement> ReasonsAsync(Guid invoice, string today = "2026-11-06")
    {
        await using var owner = await fixture.Database.OpenOwnerAsync();
        return JsonSerializer.Deserialize<JsonElement>((await owner.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT row_to_json(r)::text FROM cfdi_review_reasons(@today::date) r WHERE id=@invoice", new { invoice, today }, cancellationToken: TestContext.Current.CancellationToken)))!);
    }
}
