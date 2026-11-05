using Reconciliation.Core.Matching;

namespace Reconciliation.Tests;

public sealed class MatchingDecisionTests
{
    private static readonly MatchingSettings Settings = new(3, .01m);

    [Fact]
    public void Second_run_does_not_allocate_a_suggested_movements_leftover_credit()
    {
        var invoice = SyntheticMatching.Invoice(60) with { Serie = "FC", Folio = "0001" };
        var other = invoice with { Id = Guid.NewGuid(), Uuid = Guid.NewGuid(), Total = 40, Serie = null, Folio = null };
        var movement = SyntheticMatching.FirstMovement(100) with { Description = "FC0001" };
        var first = MatchingEngine.Run([movement], [invoice, other], [], [], Settings);
        Assert.Equal(60, Assert.Single(first.Changes).Amount);
        var second = MatchingEngine.Run([movement], [invoice, other], [], first.Changes, Settings);
        Assert.Empty(second.Changes);
        Assert.Empty(second.Updates);
    }

    [Fact]
    public void User_rejection_blocks_a_complement_imported_later_unless_reopened()
    {
        var invoice = SyntheticMatching.Invoice(100);
        var movement = SyntheticMatching.FirstMovement(100);
        var rejected = Suggestion(movement, invoice) with { State = "rejected", Origin = "user" };
        var payment = SyntheticMatching.Payment(100);
        Assert.Empty(MatchingEngine.Run([movement], [invoice], [payment], [rejected], Settings).Changes);
        Assert.Single(MatchingEngine.Run([movement], [invoice], [payment], [rejected with { Reopened = true }], Settings).Changes);
    }

    [Theory]
    [InlineData("user", "suggested")]
    [InlineData("user", "reconciled")]
    [InlineData("automatic", "reconciled")]
    public void Deterministic_evidence_never_changes_a_user_decision_or_reconciled_link(string origin, string state)
    {
        var invoice = SyntheticMatching.Invoice(100);
        var movement = SyntheticMatching.FirstMovement(100);
        var existing = Suggestion(movement, invoice) with { Amount = 50, Origin = origin, State = state };
        var result = MatchingEngine.Run([movement], [invoice], [SyntheticMatching.Payment(100)], [existing], Settings);
        Assert.Empty(result.Changes);
        Assert.Empty(result.Updates);
    }

    [Fact]
    public void Suggestion_then_complement_upgrades_in_place_and_third_run_changes_nothing()
    {
        var invoice = SyntheticMatching.Invoice(100);
        var movement = SyntheticMatching.FirstMovement(100);
        var first = MatchingEngine.Run([movement], [invoice], [], [], Settings);
        var original = Assert.Single(first.Changes);
        var payment = SyntheticMatching.Payment(100);
        var second = MatchingEngine.Run([movement], [invoice], [payment], first.Changes, Settings);
        Assert.Empty(second.Changes);
        var update = Assert.Single(second.Updates);
        Assert.Equal(original, update.Before);
        Assert.Equal("link.upgraded", update.Action);
        Assert.Equal("reconciled", update.After.State);
        Assert.Equal(payment.Id, update.After.PaymentRef);
        Assert.Null(update.After.Confidence);
        Assert.Single(update.After.Criteria, criterion => criterion.Code == "payment-complement");
        var third = MatchingEngine.Run([movement], [invoice], [payment], Apply(first.Changes, second), Settings);
        Assert.Empty(third.Changes);
        Assert.Empty(third.Updates);
    }

    [Fact]
    public void Complement_for_a_different_invoice_closes_the_superseded_suggestion()
    {
        var invoice = SyntheticMatching.Invoice(100);
        var other = invoice with { Id = Guid.NewGuid(), Uuid = Guid.NewGuid() };
        var movement = SyntheticMatching.FirstMovement(100);
        var suggestion = Suggestion(movement, other);
        var result = MatchingEngine.Run([movement], [invoice, other], [SyntheticMatching.Payment(100)], [suggestion], Settings);
        Assert.Equal(invoice.Id, Assert.Single(result.Changes).InvoiceId);
        var closed = Assert.Single(result.Updates);
        Assert.Equal("link.closed", closed.Action);
        Assert.Equal("closed", closed.After.State);
        Assert.Equal(suggestion, closed.Before);
    }

    [Fact]
    public void Provisional_suggestion_reserves_nothing_against_another_movements_complement()
    {
        var invoice = SyntheticMatching.Invoice(100);
        var movement = SyntheticMatching.FirstMovement(100);
        var provisionalMovement = movement with { Id = Guid.NewGuid() };
        var suggestion = Suggestion(provisionalMovement, invoice);
        var result = MatchingEngine.Run([movement, provisionalMovement], [invoice], [SyntheticMatching.Payment(100)], [suggestion], Settings);
        Assert.Equal(movement.Id, Assert.Single(result.Changes).MovementId);
        var closed = Assert.Single(result.Updates);
        Assert.Equal("closed", closed.After.State);
        Assert.Contains("capacity taken by deterministic link", closed.After.Explanation);
        var balance = Assert.Single(result.Invoices);
        Assert.Equal(100, balance.ReconciledAmount);
        Assert.Equal(0, balance.SuggestedAmount);
        var rerun = MatchingEngine.Run([movement, provisionalMovement], [invoice], [SyntheticMatching.Payment(100)], Apply([suggestion], result), Settings);
        Assert.Empty(rerun.Changes);
        Assert.Empty(rerun.Updates);
    }

    [Fact]
    public void Capacity_cleanup_closes_lowest_confidence_then_newest_then_stable_id()
    {
        var invoice = SyntheticMatching.Invoice(150);
        var movement = SyntheticMatching.FirstMovement(75);
        var others = Enumerable.Range(1, 3).Select(i => movement with { Id = new Guid(i + 100, 0, 0, new byte[8]), Credit = 50 }).ToArray();
        var suggestions = others.Select(other => Suggestion(other, invoice)).ToArray();
        suggestions[0] = suggestions[0] with { Confidence = "low", CreatedAt = DateTimeOffset.Parse("2026-10-01T00:00:00Z") };
        suggestions[1] = suggestions[1] with { Confidence = "medium", CreatedAt = DateTimeOffset.Parse("2026-10-02T00:00:00Z") };
        suggestions[2] = suggestions[2] with { Confidence = "medium", CreatedAt = DateTimeOffset.Parse("2026-10-01T00:00:00Z") };
        var result = MatchingEngine.Run([movement, .. others], [invoice], [SyntheticMatching.Payment(75)], suggestions, Settings);
        Assert.Equal(75, Assert.Single(result.Changes).Amount);
        Assert.Equal(new[] { others[0].Id, others[1].Id }, result.Updates.Select(update => update.Before.MovementId));
        Assert.Equal(50, Assert.Single(result.Invoices).SuggestedAmount);
        var reverse = MatchingEngine.Run([.. others.Reverse(), movement], [invoice], [SyntheticMatching.Payment(75)], [.. suggestions.Reverse()], Settings);
        Assert.Equal(result.Updates.Select(update => update.Before.MovementId), reverse.Updates.Select(update => update.Before.MovementId));
    }

    internal static MatchingLink Suggestion(MatchingMovement movement, MatchingInvoice invoice) => new(movement.Id, invoice.Id,
        null, null, Math.Min(movement.Credit, invoice.Total), "suggested", "automatic", [new("amount-equals-total", 40, "amount equals total")], "low", "amount equals total");

    internal static MatchingLink[] Apply(IReadOnlyList<MatchingLink> existing, MatchingResult result)
    {
        var links = existing.ToList();
        foreach (var update in result.Updates) { links.Remove(update.Before); links.Add(update.After); }
        links.AddRange(result.Changes);
        return links.ToArray();
    }
}
