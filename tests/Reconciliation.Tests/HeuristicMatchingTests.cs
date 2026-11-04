using Reconciliation.Core.Matching;

namespace Reconciliation.Tests;

public sealed class HeuristicMatchingTests
{
    private static readonly MatchingSettings Settings = new(3, 0.01m);

    [Fact(DisplayName = "Movement without complement / Reasons visible")]
    public void Worked_example_gets_a_residual_suggestion_after_its_deterministic_payment()
    {
        var result = MatchingEngine.Run([SyntheticMatching.SecondMovement(), SyntheticMatching.FirstMovement()], [SyntheticMatching.Invoice()], [SyntheticMatching.Payment()], [], Settings);
        Assert.Equal(2, result.Changes.Count);
        var suggestion = Assert.Single(result.Changes, link => link.State == "suggested");
        Assert.Equal(SyntheticMatching.SecondMovementId, suggestion.MovementId);
        Assert.Equal(5800m, suggestion.Amount);
        Assert.Equal("low", suggestion.Confidence);
        Assert.Contains(suggestion.Criteria, criterion => criterion.Code == "amount-equals-remaining" && criterion.Points == 30);
        Assert.Contains("remaining payable", suggestion.Explanation, StringComparison.OrdinalIgnoreCase);
        var balance = Assert.Single(result.Invoices);
        Assert.Equal((5800m, 5800m, 5800m, 0m), (balance.ReconciledAmount, balance.SuggestedAmount, balance.PendingAmount, balance.UncoveredAmount));
    }

    [Fact(DisplayName = "Retention reduces the payment (suggestion fully covers net total)")]
    public void Reference_only_retention_payment_covers_net_but_does_not_claim_confirmation()
    {
        var invoice = SyntheticMatching.Invoice(1939.27m) with { Retained = 21.13m, Serie = "FC", Folio = "488" };
        var movement = SyntheticMatching.FirstMovement(1939.27m) with { Description = "SYNTHETIC FC488" };
        var result = MatchingEngine.Run([movement], [invoice], [], [], Settings);
        var link = Assert.Single(result.Changes);
        Assert.Equal("suggested", link.State);
        Assert.Equal(1939.27m, link.Amount);
        Assert.Contains(link.Criteria, criterion => criterion.Code == "amount-equals-total" && criterion.Points == 40);
        var balance = Assert.Single(result.Invoices);
        Assert.Equal(1939.27m, balance.PendingAmount);
        Assert.Equal(0m, balance.UncoveredAmount);
    }

    [Theory]
    [InlineData(100, 0, "high", 40, 30)]
    [InlineData(99.99, 0, "medium", 25, 30)]
    [InlineData(100, 10, "low", 40, 0)]
    public void Amount_and_date_points_determine_fixed_confidence(decimal amount, int days, string confidence, int amountPoints, int datePoints)
    {
        var invoice = SyntheticMatching.Invoice(100) with { Date = new(2026, 10, 10) };
        var movement = SyntheticMatching.FirstMovement(amount) with { Date = invoice.Date.AddDays(days), Description = "SYNTHETIC" };
        var link = Assert.Single(MatchingEngine.Run([movement], [invoice], [], [], Settings).Changes);
        Assert.Equal(confidence, link.Confidence);
        Assert.Equal(amountPoints, link.Criteria.Single(c => c.Code.StartsWith("amount-", StringComparison.Ordinal)).Points);
        Assert.Equal(datePoints, link.Criteria.Where(c => c.Code == "date-proximity").Sum(c => c.Points));
        Assert.Equal("suggested", link.State);
    }

    [Fact(DisplayName = "Gross payment")]
    public void Gross_payments_are_always_suggested_with_the_spec_reason_even_at_high_confidence()
    {
        var invoice = SyntheticMatching.Invoice(1939.27m) with { Date = new(2026, 10, 10), Retained = 21.13m, Serie = "FC", Folio = "488" };
        var movement = SyntheticMatching.FirstMovement(1960.40m) with { Description = "FC488 AAA010101AAA" };
        var link = Assert.Single(MatchingEngine.Run([movement], [invoice], [], [], Settings).Changes);
        Assert.Equal("suggested", link.State);
        Assert.Equal("high", link.Confidence);
        Assert.Equal(1939.27m, link.Amount);
        Assert.Contains(link.Criteria, c => c.Code == "gross-payment" && c.Points == 35);
        Assert.Contains("amount equals total before retentions", link.Explanation, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Tracking key equals operation number")]
    public void Operation_reference_is_evidence_even_outside_the_deterministic_payment_date_window()
    {
        var movement = SyntheticMatching.SecondMovement() with { Reference = "ABC123" };
        var link = Assert.Single(MatchingEngine.Run([movement], [SyntheticMatching.Invoice()], [SyntheticMatching.Payment()], [], Settings).Changes);
        Assert.Equal("suggested", link.State);
        Assert.Contains(link.Criteria, c => c.Code == "operation-number" && c.Points == 30);
        Assert.Contains("reference equals operation number", link.Explanation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("FC-488", "invoice-reference", 30)]
    [InlineData("0000000000004000", "invoice-reference", 30)]
    [InlineData("AAA010101AAA", "rfc", 15)]
    public void Text_postings_reach_candidates_beyond_the_nearest_amount_bucket(string text, string code, int points)
    {
        var invoice = SyntheticMatching.Invoice(100) with { Date = new(2026, 10, 10), Serie = "FC", Folio = "488" };
        var movement = SyntheticMatching.FirstMovement(50) with { Description = text };
        var link = Assert.Single(MatchingEngine.Run([movement], [invoice], [], [], Settings).Changes);
        Assert.Contains(link.Criteria, c => c.Code == code && c.Points == points);
    }

    [Fact]
    public void A_short_folio_alone_and_an_RFC_alone_below_threshold_do_not_create_suggestions()
    {
        var invoice = SyntheticMatching.Invoice(100) with { Folio = "488" };
        foreach (var text in new[] { "488", "AAA010101AAA" })
            Assert.Empty(MatchingEngine.Run([SyntheticMatching.FirstMovement(50) with { Description = text }], [invoice], [], [], Settings).Changes);
    }

    [Fact]
    public void Equally_scored_candidates_create_only_one_suggestion_and_explain_the_tie()
    {
        var invoice = SyntheticMatching.Invoice(5800);
        var other = invoice with { Id = SyntheticMatching.PaymentCfdiId, Uuid = SyntheticMatching.PaymentCfdiId };
        var result = MatchingEngine.Run([SyntheticMatching.FirstMovement()], [other, invoice], [], [], Settings);
        var link = Assert.Single(result.Changes);
        Assert.Equal(invoice.Id, link.InvoiceId);
        Assert.Contains("1 other invoices matched equally", link.Explanation, StringComparison.Ordinal);
    }
}
