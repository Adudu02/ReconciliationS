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
        var reservedInvoices = active.GroupBy(link => link.InvoiceId).ToDictionary(group => group.Key, group => group.Sum(link => link.Amount));
        var reservedMovements = active.GroupBy(link => link.MovementId).ToDictionary(group => group.Key, group => group.Sum(link => link.Amount));
        var usedPayments = active.Where(link => link.PaymentRef.HasValue).Select(link => link.PaymentRef!.Value).ToHashSet();
        var blocked = existing.Where(link => !link.Reopened).Select(link => (link.MovementId, link.InvoiceId, link.PaymentCfdiId)).ToHashSet();
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
            var exhausted = new List<Guid>();
            foreach (var payment in index.Nearest(movement.Credit, movement.Date, maxCandidates))
            {
                comparisons++;
                if (Math.Abs(payment.Date.DayNumber - movement.Date.DayNumber) > settings.DateToleranceDays) break;
                var docs = documents[payment.Id];
                if (docs.Any(doc => doc.Paid > eligible[doc.InvoiceUuid].Total - reservedInvoices.GetValueOrDefault(eligible[doc.InvoiceUuid].Id)))
                {
                    exhausted.Add(payment.Id);
                    continue;
                }
                if (docs.Any(doc => blocked.Contains((movement.Id, eligible[doc.InvoiceUuid].Id, (Guid?)payment.CfdiId)))) continue;
                selected = payment;
                break;
            }
            // Do not mutate a sorted bucket until its enumerators have been disposed.
            foreach (var id in exhausted) index.Remove(id);
            if (selected is null) continue;
            index.Remove(selected.Id);
            foreach (var document in documents[selected.Id])
            {
                var invoice = eligible[document.InvoiceUuid];
                changes.Add(new(movement.Id, invoice.Id, selected.CfdiId, selected.Id, document.Paid, "reconciled", "automatic",
                    [new("payment-complement", 0, "issued payment complement references invoice UUID; exact movement amount and payment date within tolerance")],
                    null, "Payment complement references the invoice; payment equals the credit movement within the date tolerance."));
                reservedInvoices[invoice.Id] = reservedInvoices.GetValueOrDefault(invoice.Id) + document.Paid;
                reservedMovements[movement.Id] = reservedMovements.GetValueOrDefault(movement.Id) + document.Paid;
            }
        }
        return new(changes, comparisons);
    }
}
