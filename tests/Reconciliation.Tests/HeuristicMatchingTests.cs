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
    [Theory]
    [InlineData(100.01, 0, 0.01, 35, "gross-payment")]
    [InlineData(99.99, 0.01, 0.01, 30, "amount-equals-remaining")]
    [InlineData(100, 0, 0.01, 40, "amount-equals-total")]
    [InlineData(100.99, 0, 1, 25, "amount-within-tolerance")]
    [InlineData(49.99, 50, 0, 25, "amount-within-tolerance")]
    public void Overlapping_amount_targets_never_double_count(decimal credit, decimal alreadyPaid, decimal retained, int points, string code)
    {
        var invoice = SyntheticMatching.Invoice(100) with { Retained = retained };
        MatchingLink[] existing = alreadyPaid == 0 ? [] : [new(SyntheticMatching.SecondMovementId, invoice.Id, null, null, alreadyPaid,
            "reconciled", "user", [], null, "Synthetic confirmed allocation")];
        var movement = SyntheticMatching.FirstMovement(credit) with { Description = "SYNTHETIC" };
        var link = Assert.Single(MatchingEngine.Run([movement], [invoice], [], existing, Settings).Changes);
        var amount = Assert.Single(link.Criteria, criterion => criterion.Code.StartsWith("amount-", StringComparison.Ordinal) || criterion.Code == "gross-payment");
        Assert.Equal(code, amount.Code);
        Assert.Equal(points, amount.Points);
        if (credit == 100.99m)
        {
            Assert.Contains("gross", amount.Detail, StringComparison.Ordinal);
            Assert.Contains("1 cents", amount.Detail, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("SYNTHETIC")]
    [InlineData("AAA010101AAA")]
    public void Date_or_RFC_without_an_amount_or_strong_identifier_never_makes_a_candidate(string description)
    {
        var invoice = SyntheticMatching.Invoice(100) with { Date = new(2026, 10, 10) };
        Assert.Empty(MatchingEngine.Run([SyntheticMatching.FirstMovement(50) with { Description = description }], [invoice], [], [], Settings).Changes);
    }

    [Fact]
    public void A_strong_identifier_can_propose_a_partial_payment_and_RFC_adds_points_after_eligibility()
    {
        var invoice = SyntheticMatching.Invoice(100) with { Date = new(2026, 10, 10), Serie = "FC", Folio = "488" };
        var movement = SyntheticMatching.FirstMovement(50) with { Description = "FC488 AAA010101AAA" };
        var link = Assert.Single(MatchingEngine.Run([movement], [invoice], [], [], Settings).Changes);
        Assert.Equal(50m, link.Amount);
        Assert.Equal("suggested", link.State);
        Assert.Contains(link.Criteria, criterion => criterion.Code == "rfc" && criterion.Points == 15);
        Assert.Contains("identifier without amount match", link.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Lazy_deletion_removes_stale_payments_and_continues_beyond_a_dense_dead_prefix()
    {
        var invoice = SyntheticMatching.Invoice(100);
        var other = invoice with { Id = SyntheticMatching.PaymentCfdiId, Uuid = SyntheticMatching.PaymentCfdiId };
        var payments = Enumerable.Range(1, 80).Select(i => SyntheticMatching.Payment(100) with
        {
            Id = Guid.Parse($"00000000-0000-0000-0001-{i:D12}"),
            CfdiId = Guid.Parse($"00000000-0000-0000-0002-{i:D12}")
        }).Append(SyntheticMatching.Payment(100) with
        {
            Id = Guid.Parse("00000000-0000-0000-0003-000000000001"), Date = new(2026, 10, 12),
            Documents = [new(other.Uuid, 100)]
        }).ToArray();
        var second = SyntheticMatching.SecondMovement() with { Date = new(2026, 10, 11), Credit = 100 };
        var result = MatchingEngine.Run([SyntheticMatching.FirstMovement(100), second], [invoice, other], payments, [], Settings);
        Assert.Equal(2, result.Changes.Count(link => link.State == "reconciled"));
        Assert.True(result.CandidateComparisons <= 2 * MatchingEngine.MaxCandidatesPerMovement + payments.Length);
    }

    [Fact]
    public void Amount_RFC_sub_index_reaches_the_true_best_beyond_K_nearer_candidates()
    {
        var invoice = SyntheticMatching.Invoice(100) with { Date = new(2026, 10, 9), ReceiverRfc = "CCC010101CCC" };
        var nearer = Enumerable.Range(1, 60).Select(i => invoice with
        {
            Id = Guid.Parse($"00000000-0000-0000-0001-{i:D12}"), Uuid = Guid.Parse($"00000000-0000-0000-0001-{i:D12}")
        }).ToArray();
        var far = invoice with { Id = SyntheticMatching.InvoiceId, Date = new(2026, 10, 7), ReceiverRfc = "BBB010101BBB" };
        var movement = SyntheticMatching.FirstMovement(100) with { Description = "BBB010101BBB" };
        var result = MatchingEngine.Run([movement], [.. nearer, far], [], [], Settings, maxCandidates: 2);
        Assert.Equal(far.Id, Assert.Single(result.Changes).InvoiceId);
        Assert.True(result.CandidateComparisons <= 3 * 2 * 2);
    }

    [Theory]
    [InlineData(1, 27)]
    [InlineData(2, 23)]
    [InlineData(3, 20)]
    public void Date_proximity_bonus_rounds_to_integer_points(int distance, int expected)
    {
        var invoice = SyntheticMatching.Invoice(100) with { Date = new DateOnly(2026, 10, 10).AddDays(-distance) };
        var link = Assert.Single(MatchingEngine.Run([SyntheticMatching.FirstMovement(100)], [invoice], [], [], Settings).Changes);
        Assert.Equal(expected, Assert.Single(link.Criteria, criterion => criterion.Code == "date-proximity").Points);
    }

}
