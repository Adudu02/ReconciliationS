namespace Reconciliation.Core.Matching;

public static class MatchingEngine
{
    public const int MaxCandidatesPerMovement = 50;

    public static MatchingResult Run(IReadOnlyList<MatchingMovement> movements, IReadOnlyList<MatchingInvoice> invoices,
        IReadOnlyList<MatchingPayment> payments, IReadOnlyList<MatchingLink> existing, MatchingSettings settings,
        int maxCandidates = MaxCandidatesPerMovement)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCandidates, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxCandidates, MaxCandidatesPerMovement);
        ArgumentOutOfRangeException.ThrowIfNegative(settings.DateToleranceDays);
        var eligible = invoices.Where(invoice => invoice.Type == "I" && invoice.Direction == "issued" && invoice.Currency == "MXN").ToDictionary(invoice => invoice.Uuid);
        var active = existing.Where(link => link.State is "reconciled" or "suggested").ToArray();
        var current = active.ToDictionary(Key);
        var updates = new List<MatchingLinkUpdate>();
        var provisional = active.Where(link => link.Origin == "automatic" && link.State == "suggested")
            .GroupBy(link => link.MovementId).ToDictionary(group => group.Key, group => group.OrderBy(link => link.InvoiceId).ThenBy(link => link.Id).ToArray());
        // Suggestions are provisional: only firm links reserve capacity against deterministic evidence.
        var firm = active.Where(link => link.Origin != "automatic" || link.State != "suggested").ToArray();
        var reservedInvoices = firm.GroupBy(link => link.InvoiceId).ToDictionary(group => group.Key, group => group.Sum(link => link.Amount));
        var reservedMovements = firm.GroupBy(link => link.MovementId).ToDictionary(group => group.Key, group => group.Sum(link => link.Amount));
        var usedPayments = firm.Where(link => link.PaymentRef.HasValue).Select(link => link.PaymentRef!.Value).ToHashSet();
        var rejected = existing.Where(link => link.State == "rejected" && link.Origin == "user" && !link.Reopened)
            .Select(link => (link.MovementId, link.InvoiceId)).ToHashSet();
        var blocked = existing.Where(link => !link.Reopened).Select(link => (link.MovementId, link.InvoiceId, link.PaymentCfdiId)).ToHashSet();
        var tombstoneCounts = existing.Where(link => !link.Reopened).GroupBy(link => link.MovementId).ToDictionary(group => group.Key, group => group.Count());
        var index = new AmountDateIndex<MatchingPayment>();
        var documents = new Dictionary<Guid, MatchingPaymentDocument[]>();
        foreach (var payment in payments)
        {
            if (payment.Direction != "issued" || payment.Currency != "MXN" || payment.Version != "2.0" || payment.Inconsistent || usedPayments.Contains(payment.Id) ||
                payment.Documents.Count == 0 || payment.Documents.Any(doc => doc.Currency != "MXN" || doc.Inconsistent || doc.Paid <= 0 || !eligible.ContainsKey(doc.InvoiceUuid)) ||
                payment.Documents.Sum(doc => doc.Paid) != payment.Amount) continue;
            documents.Add(payment.Id, payment.Documents.GroupBy(doc => doc.InvoiceUuid)
                .Select(group => new MatchingPaymentDocument(group.Key, group.Sum(doc => doc.Paid))).OrderBy(doc => eligible[doc.InvoiceUuid].Id).ToArray());
            index.Add(payment.Id, payment.Amount, payment.Date, payment);
        }
        var changes = new List<MatchingLink>();
        long comparisons = 0;
        foreach (var movement in movements.OrderBy(movement => movement.Date).ThenBy(movement => movement.Id))
        {
            if (movement.Credit <= 0 || movement.Debit != 0 || movement.Classification is not null || reservedMovements.GetValueOrDefault(movement.Id) > 0) continue;
            MatchingPayment? selected = null;
            var tombstones = tombstoneCounts.GetValueOrDefault(movement.Id);
            while (selected is null)
            {
                var exhausted = new List<Guid>();
                foreach (var payment in index.Nearest(movement.Credit, movement.Date, maxCandidates + tombstones))
                {
                    if (Math.Abs(payment.Date.DayNumber - movement.Date.DayNumber) > settings.DateToleranceDays) break;
                    var docs = documents[payment.Id];
                    if (docs.Any(doc => doc.Paid > eligible[doc.InvoiceUuid].Total - reservedInvoices.GetValueOrDefault(eligible[doc.InvoiceUuid].Id)))
                    {
                        comparisons++;
                        exhausted.Add(payment.Id);
                        continue;
                    }
                    if (docs.Any(doc => rejected.Contains((movement.Id, eligible[doc.InvoiceUuid].Id)) ||
                        blocked.Contains((movement.Id, eligible[doc.InvoiceUuid].Id, (Guid?)payment.CfdiId)))) continue;
                    comparisons++;
                    selected = payment;
                    break;
                }
                // Capacity only shrinks: each invalid payment is removed once, after disposing its lookup enumerators.
                foreach (var id in exhausted) index.Remove(id);
                if (selected is not null || exhausted.Count == 0) break;
            }
            if (selected is null) continue;
            index.Remove(selected.Id);
            var superseded = provisional.GetValueOrDefault(movement.Id) ?? [];
            var upgraded = new HashSet<MatchingLink>();
            foreach (var document in documents[selected.Id])
            {
                var invoice = eligible[document.InvoiceUuid];
                var proposed = new MatchingLink(movement.Id, invoice.Id, selected.CfdiId, selected.Id, document.Paid, "reconciled", "automatic",
                    [new("payment-complement", 0, "issued payment complement references invoice UUID; exact movement amount and payment date within tolerance")],
                    null, "Payment complement references the invoice; payment equals the credit movement within the date tolerance.");
                var previous = superseded.FirstOrDefault(link => link.InvoiceId == invoice.Id);
                if (previous is not null)
                {
                    Update(previous, proposed with { Id = previous.Id, CreatedAt = previous.CreatedAt }, "link.upgraded");
                    upgraded.Add(previous);
                }
                else { changes.Add(proposed); current.Add(Key(proposed), proposed); }
                reservedInvoices[invoice.Id] = reservedInvoices.GetValueOrDefault(invoice.Id) + document.Paid;
                reservedMovements[movement.Id] = reservedMovements.GetValueOrDefault(movement.Id) + document.Paid;
            }
            foreach (var previous in superseded)
                if (!upgraded.Contains(previous)) Close(previous, "superseded by deterministic link");
        }
        var invoiceCapacity = eligible.Values.ToDictionary(invoice => invoice.Id, invoice => invoice.Total);
        var movementCapacity = movements.ToDictionary(movement => movement.Id, movement => movement.Credit);
        var invoiceTotals = current.Values.GroupBy(link => link.InvoiceId).ToDictionary(group => group.Key, group => group.Sum(link => link.Amount));
        var movementTotals = current.Values.GroupBy(link => link.MovementId).ToDictionary(group => group.Key, group => group.Sum(link => link.Amount));
        // A total order makes cleanup independent of input order; only automatic suggestions may close.
        var cleanup = current.Values.Where(link => link.State == "suggested" && link.Origin == "automatic")
            .OrderBy(link => link.Confidence == "high" ? 2 : link.Confidence == "medium" ? 1 : 0)
            .ThenByDescending(link => link.CreatedAt).ThenBy(link => link.Id ?? link.MovementId)
            .ThenBy(link => link.InvoiceId).ThenBy(link => link.PaymentCfdiId).ToArray();
        foreach (var link in cleanup)
        {
            if ((!invoiceCapacity.TryGetValue(link.InvoiceId, out var invoiceLimit) || invoiceTotals[link.InvoiceId] <= invoiceLimit) &&
                (!movementCapacity.TryGetValue(link.MovementId, out var movementLimit) || movementTotals[link.MovementId] <= movementLimit)) continue;
            Close(link, "capacity taken by deterministic link");
            invoiceTotals[link.InvoiceId] -= link.Amount;
            movementTotals[link.MovementId] -= link.Amount;
        }
        reservedInvoices = current.Values.GroupBy(link => link.InvoiceId).ToDictionary(group => group.Key, group => group.Sum(link => link.Amount));
        reservedMovements = current.Values.GroupBy(link => link.MovementId).ToDictionary(group => group.Key, group => group.Sum(link => link.Amount));
        var stageTwoExisting = current.Values.Concat(existing.Where(link => link.State is not ("reconciled" or "suggested")))
            .Concat(updates.Where(update => update.After.State == "closed").Select(update => update.After)).ToArray();
        var deterministicCount = changes.Count;
        comparisons += HeuristicMatching.Apply(movements, eligible.Values.ToArray(), payments, stageTwoExisting, settings, maxCandidates,
            changes, reservedInvoices, reservedMovements);
        foreach (var link in changes.Skip(deterministicCount)) current.Add(Key(link), link);
        var allLinks = current.Values.ToArray();
        var reconciled = allLinks.Where(link => link.State == "reconciled").GroupBy(link => link.InvoiceId).ToDictionary(group => group.Key, group => group.Sum(link => link.Amount));
        var suggested = allLinks.Where(link => link.State == "suggested").GroupBy(link => link.InvoiceId).ToDictionary(group => group.Key, group => group.Sum(link => link.Amount));
        var balances = eligible.Values.OrderBy(invoice => invoice.Id).Select(invoice => new MatchingBalance(invoice.Id,
            reconciled.GetValueOrDefault(invoice.Id), suggested.GetValueOrDefault(invoice.Id),
            Math.Max(0, invoice.Total - reconciled.GetValueOrDefault(invoice.Id)),
            Math.Max(0, invoice.Total - reconciled.GetValueOrDefault(invoice.Id) - suggested.GetValueOrDefault(invoice.Id)))).ToArray();
        return new(changes, comparisons, balances, updates);

        void Update(MatchingLink before, MatchingLink after, string action)
        {
            current.Remove(Key(before));
            if (after.State is "reconciled" or "suggested") current.Add(Key(after), after);
            updates.Add(new(before, after, action));
        }
        void Close(MatchingLink link, string reason) => Update(link, link with { State = "closed", Explanation = link.Explanation + "; " + reason }, "link.closed");
    }

    private static (Guid Movement, Guid Invoice, Guid? PaymentCfdi) Key(MatchingLink link) => (link.MovementId, link.InvoiceId, link.PaymentCfdiId);
}
