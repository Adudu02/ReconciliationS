using Reconciliation.Core.Matching;

namespace Reconciliation.Tests;

public sealed class DeterministicMatchingTests
{
    private static readonly MatchingSettings Settings = new(3, 0.01m);

    [Fact(DisplayName = "Payment complement matches movement")]
    public void Complement_matches_the_first_worked_example_movement_only()
    {
        var result = MatchingEngine.Run([SyntheticMatching.SecondMovement(), SyntheticMatching.FirstMovement()],
            [SyntheticMatching.Invoice()], [SyntheticMatching.Payment()], [], Settings);
        var link = Assert.Single(result.Changes.Where(link => link.State == "reconciled"));
        Assert.Equal(SyntheticMatching.FirstMovementId, link.MovementId);
        Assert.Equal(SyntheticMatching.InvoiceId, link.InvoiceId);
        Assert.Equal(SyntheticMatching.PaymentCfdiId, link.PaymentCfdiId);
        Assert.Equal(SyntheticMatching.PaymentId, link.PaymentRef);
        Assert.Equal(5800m, link.Amount);
        Assert.Equal("reconciled", link.State);
        Assert.Equal("automatic", link.Origin);
        Assert.Null(link.Confidence);
        Assert.Contains(link.Criteria, criterion => criterion.Code == "payment-complement");
        Assert.NotEmpty(link.Explanation);
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(-3, true)]
    [InlineData(-4, false)]
    public void Payment_date_window_is_inclusive(int days, bool matched)
    {
        var movement = SyntheticMatching.FirstMovement() with { Date = new DateOnly(2026, 10, 10).AddDays(days) };
        var result = MatchingEngine.Run([movement], [SyntheticMatching.Invoice()], [SyntheticMatching.Payment()], [], Settings);
        Assert.Equal(matched ? 1 : 0, result.Changes.Count);
    }

    [Theory(DisplayName = "Debit movement / issued income direction")]
    [InlineData("I", "issued", "issued", false, true)]
    [InlineData("I", "issued", "issued", true, false)]
    [InlineData("I", "received", "issued", false, false)]
    [InlineData("E", "issued", "issued", false, false)]
    [InlineData("I", "issued", "received", false, false)]
    public void Only_credit_movements_issued_income_and_issued_complements_can_match(string type, string direction, string paymentDirection, bool debit, bool matched)
    {
        var invoice = SyntheticMatching.Invoice() with { Type = type, Direction = direction };
        var movement = SyntheticMatching.FirstMovement() with { Credit = debit ? 0 : 5800, Debit = debit ? 5800 : 0 };
        var payment = SyntheticMatching.Payment() with { Direction = paymentDirection };
        Assert.Equal(matched ? 1 : 0, MatchingEngine.Run([movement], [invoice], [payment], [], Settings).Changes.Count);
    }

    [Fact]
    public void Net_total_is_used_for_a_deterministic_retention_payment()
    {
        var invoice = SyntheticMatching.Invoice(1939.27m) with { Retained = 21.13m };
        var result = MatchingEngine.Run([SyntheticMatching.FirstMovement(1939.27m)], [invoice], [SyntheticMatching.Payment(1939.27m)], [], Settings);
        Assert.Equal(1939.27m, Assert.Single(result.Changes).Amount);
        Assert.DoesNotContain(MatchingEngine.Run([SyntheticMatching.FirstMovement(1960.40m)], [invoice], [SyntheticMatching.Payment(1939.27m)], [], Settings).Changes, link => link.State == "reconciled");
    }

    [Theory]
    [InlineData("MXN", "2.0", false, true)]
    [InlineData("USD", "2.0", false, false)]
    [InlineData("MXN", "1.0", false, false)]
    [InlineData("MXN", "2.0", true, false)]
    public void Unsupported_currency_version_or_inconsistent_payment_is_not_automatically_confirmed(string currency, string version, bool inconsistent, bool matched)
    {
        var payment = SyntheticMatching.Payment() with { Currency = currency, Version = version, Inconsistent = inconsistent };
        Assert.Equal(matched ? 1 : 0, MatchingEngine.Run([SyntheticMatching.FirstMovement()], [SyntheticMatching.Invoice()], [payment], [], Settings).Changes.Count);
    }

    [Fact]
    public void A_payment_with_several_invoice_documents_is_allocated_atomically_without_exceeding_capacity()
    {
        var first = SyntheticMatching.Invoice(3000);
        var second = first with { Id = Guid.Parse("00000000-0000-0000-0000-000000000010"), Uuid = Guid.Parse("00000000-0000-4000-8000-000000000010"), Total = 2800 };
        var payment = SyntheticMatching.Payment() with { Documents = [new(first.Uuid, 3000), new(second.Uuid, 2800)] };
        var result = MatchingEngine.Run([SyntheticMatching.FirstMovement()], [first, second], [payment], [], Settings);
        Assert.Equal(2, result.Changes.Count);
        Assert.Equal(5800m, result.Changes.Sum(link => link.Amount));
        Assert.Empty(MatchingEngine.Run([SyntheticMatching.FirstMovement()], [first with { Total = 2999 }, second], [payment], [], Settings).Changes);
        Assert.Empty(MatchingEngine.Run([SyntheticMatching.FirstMovement()], [first], [payment], [], Settings).Changes);
    }

    [Fact]
    public void Existing_allocations_and_a_used_payment_are_not_allocated_again()
    {
        var result = MatchingEngine.Run([SyntheticMatching.FirstMovement()], [SyntheticMatching.Invoice()], [SyntheticMatching.Payment()], [], Settings);
        Assert.Empty(MatchingEngine.Run([SyntheticMatching.FirstMovement()], [SyntheticMatching.Invoice()], [SyntheticMatching.Payment()], result.Changes, Settings).Changes);
        var later = SyntheticMatching.FirstMovement() with { Id = SyntheticMatching.SecondMovementId };
        Assert.DoesNotContain(MatchingEngine.Run([later], [SyntheticMatching.Invoice()], [SyntheticMatching.Payment()], result.Changes, Settings).Changes, link => link.State == "reconciled");
    }
}
