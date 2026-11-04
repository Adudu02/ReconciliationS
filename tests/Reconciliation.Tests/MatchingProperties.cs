using System.Text.Json;
using FsCheck.Xunit;
using Reconciliation.Core.Matching;

namespace Reconciliation.Tests;

public sealed class MatchingProperties
{
    private static readonly MatchingSettings Settings = new(3, .01m);

    [Property(MaxTest = 150)]
    public void Rerun_over_own_output_changes_nothing(int seed)
    {
        var (movements, invoices, payments) = Data(seed);
        var first = MatchingEngine.Run(movements, invoices, payments, [], Settings, 2);
        var second = MatchingEngine.Run(movements, invoices, payments, first.Changes, Settings, 2);
        Assert.Empty(second.Changes);
        Assert.Empty(second.Updates);
    }

    [Property(MaxTest = 150)]
    public void Applied_amounts_respect_both_capacities_and_invoice_sums(int seed)
    {
        var (movements, invoices, payments) = Data(seed);
        var result = MatchingEngine.Run(movements, invoices, payments, [], Settings, 2);
        foreach (var movement in movements)
            Assert.InRange(result.Changes.Where(link => link.MovementId == movement.Id).Sum(link => link.Amount), 0, movement.Credit);
        foreach (var invoice in invoices)
        {
            var amount = result.Changes.Where(link => link.InvoiceId == invoice.Id).Sum(link => link.Amount);
            Assert.InRange(amount, 0, invoice.Total);
            var balance = Assert.Single(result.Invoices, balance => balance.InvoiceId == invoice.Id);
            Assert.Equal(amount, balance.ReconciledAmount + balance.SuggestedAmount);
        }
        Assert.All(result.Changes, link => Assert.True(link.Amount > 0));
    }

    [Property(MaxTest = 150)]
    public void Rejected_pair_never_reappears_even_with_deterministic_evidence(int seed)
    {
        var (movements, invoices, payments) = Data(seed);
        var rejected = MatchingDecisionTests.Suggestion(movements[0], invoices[0]) with { State = "rejected", Origin = "user" };
        var result = MatchingEngine.Run(movements, invoices, payments, [rejected], Settings, 2);
        Assert.DoesNotContain(result.Changes, link => link.MovementId == rejected.MovementId && link.InvoiceId == rejected.InvoiceId);
    }

    [Property(MaxTest = 150)]
    public void Input_order_does_not_change_proposals_or_cleanup(int seed)
    {
        var (movements, invoices, payments) = Data(seed);
        var suggestions = movements.Take(3).Select((movement, i) => MatchingDecisionTests.Suggestion(movement, invoices[i % invoices.Length]) with
        { CreatedAt = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero).AddDays(i % 2), Confidence = i % 2 == 0 ? "low" : "medium", Id = movement.Id }).ToArray();
        var first = MatchingEngine.Run(movements, invoices, payments, suggestions, Settings, 2);
        var second = MatchingEngine.Run([.. movements.Reverse()], [.. invoices.Reverse()], [.. payments.Reverse()], [.. suggestions.Reverse()], Settings, 2);
        Assert.Equal(JsonSerializer.Serialize(first.Changes), JsonSerializer.Serialize(second.Changes));
        Assert.Equal(JsonSerializer.Serialize(first.Updates), JsonSerializer.Serialize(second.Updates));
        var rerun = MatchingEngine.Run(movements, invoices, payments, MatchingDecisionTests.Apply(suggestions, first), Settings, 2);
        Assert.Empty(rerun.Changes);
        Assert.Empty(rerun.Updates);
    }

    [Property(MaxTest = 150)]
    public void Candidate_never_has_two_amount_criteria(int seed)
    {
        var (movements, invoices, payments) = Data(seed);
        var result = MatchingEngine.Run(movements, invoices, payments, [], Settings, 2);
        Assert.All(result.Changes, link => Assert.True(link.Criteria.Count(criterion => criterion.Code is
            "amount-equals-total" or "gross-payment" or "amount-equals-remaining" or "amount-within-tolerance") <= 1));
    }

    [Property(MaxTest = 200)]
    public void Small_K_best_equals_brute_force_full_scan(int seed)
    {
        var random = new Random(seed);
        var movement = SyntheticMatching.FirstMovement(100) with { Description = random.Next(2) == 0 ? "BBB010101BBB" : "FC0001", Reference = null };
        var invoices = Enumerable.Range(1, random.Next(3, 13)).Select(i => SyntheticMatching.Invoice(100 + random.Next(-2, 3) / 100m) with
        {
            Id = Id(i),
            Uuid = Id(i),
            Date = movement.Date.AddDays(-random.Next(8)),
            Retained = random.Next(2) / 100m,
            ReceiverRfc = random.Next(2) == 0 ? "BBB010101BBB" : "CCC010101CCC",
            Serie = "FC",
            Folio = i.ToString("D4")
        }).ToArray();
        // Independent full scan: no indexes, cap or production scoring helpers.
        var expected = invoices.Select(invoice =>
        {
            var amount = movement.Credit == invoice.Total ? 40 : invoice.Retained > 0 && movement.Credit == invoice.Total + invoice.Retained ? 35
                : Math.Min(Math.Abs(movement.Credit - invoice.Total), Math.Abs(movement.Credit - invoice.Total - invoice.Retained)) <= .01m ? 25 : 0;
            var identifier = movement.Description.Contains(invoice.Serie + invoice.Folio, StringComparison.Ordinal);
            var distance = Math.Abs(movement.Date.DayNumber - invoice.Date.DayNumber);
            var date = distance > 3 ? 0 : 20 + (10 * (3 - distance) + 1) / 3;
            var points = amount + date + (movement.Description.Contains(invoice.ReceiverRfc, StringComparison.Ordinal) ? 15 : 0) + (identifier ? 30 : 0);
            return new { invoice.Id, Points = amount > 0 || identifier ? points : 0, Distance = distance };
        }).Where(candidate => candidate.Points >= 25).OrderByDescending(candidate => candidate.Points).ThenBy(candidate => candidate.Distance).ThenBy(candidate => candidate.Id).FirstOrDefault();
        var actual = MatchingEngine.Run([movement], invoices, [], [], Settings, 1);
        Assert.Equal(expected?.Id, actual.Changes.SingleOrDefault()?.InvoiceId);
    }

    [Fact]
    public void Dense_equal_amounts_are_bounded_deterministic_capacity_safe_and_idempotent()
    {
        const int count = 2000;
        var movements = Enumerable.Range(1, count).Select(i => SyntheticMatching.FirstMovement(100) with { Id = Id(i + count), Description = "transfer", Reference = null }).ToArray();
        var invoices = Enumerable.Range(1, count).Select(i => SyntheticMatching.Invoice(100) with { Id = Id(i), Uuid = Id(i) }).ToArray();
        var first = MatchingEngine.Run(movements, invoices, [], [], Settings);
        Assert.Equal(count, first.Changes.Count);
        Assert.Equal(count, first.Changes.Select(link => link.MovementId).Distinct().Count());
        Assert.Equal(count, first.Changes.Select(link => link.InvoiceId).Distinct().Count());
        Assert.All(first.Changes, link => Assert.Equal(100, link.Amount));
        Assert.True(first.CandidateComparisons <= count * (3 * 2 * MatchingEngine.MaxCandidatesPerMovement));
        var reversed = MatchingEngine.Run([.. movements.Reverse()], [.. invoices.Reverse()], [], [], Settings);
        Assert.Equal(JsonSerializer.Serialize(first.Changes), JsonSerializer.Serialize(reversed.Changes));
        Assert.Empty(MatchingEngine.Run(movements, invoices, [], first.Changes, Settings).Changes);
    }

    private static (MatchingMovement[] Movements, MatchingInvoice[] Invoices, MatchingPayment[] Payments) Data(int seed)
    {
        var random = new Random(seed);
        var invoices = Enumerable.Range(1, random.Next(2, 10)).Select(i => SyntheticMatching.Invoice(random.Next(1, 5) * 50m) with
        {
            Id = Id(i),
            Uuid = Id(i),
            Date = new DateOnly(2026, 10, 10).AddDays(-random.Next(8)),
            Retained = random.Next(3),
            Serie = "FC",
            Folio = i.ToString("D4")
        }).ToArray();
        var movements = Enumerable.Range(1, random.Next(2, 10)).Select(i =>
        {
            var invoice = invoices[random.Next(invoices.Length)];
            return SyntheticMatching.FirstMovement(invoice.Total + random.Next(-1, 2) * invoice.Retained) with
            { Id = Id(i + 100), Date = new DateOnly(2026, 10, 10).AddDays(random.Next(3)), Description = random.Next(2) == 0 ? "FC" + invoice.Folio : "transfer", Reference = null };
        }).ToArray();
        var first = movements[0];
        var target = invoices[0];
        var paid = Math.Min(first.Credit, target.Total);
        var payments = new[] { new MatchingPayment(Id(500), Id(501), first.Date, paid, null, [new(target.Uuid, paid)]) };
        return (movements, invoices, payments);
    }

    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);
}
