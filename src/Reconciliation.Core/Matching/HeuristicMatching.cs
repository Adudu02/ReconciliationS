namespace Reconciliation.Core.Matching;

internal static class HeuristicMatching
{
    private sealed record Stream(IEnumerable<MatchingInvoice> Values, int Points);
    private sealed record Cursor(IEnumerator<MatchingInvoice> Values, int Points);
    private sealed class AmountIndexes
    {
        public AmountDateIndex<MatchingInvoice> Totals { get; } = new();
        public AmountDateIndex<MatchingInvoice> Gross { get; } = new();
        public AmountDateIndex<MatchingInvoice> Residual { get; } = new();
    }
    private sealed record Scored(MatchingInvoice Invoice, MatchingCriterion[] Criteria, int Points, int Distance);

    public static long Apply(IReadOnlyList<MatchingMovement> movements, IReadOnlyCollection<MatchingInvoice> invoices,
        IReadOnlyList<MatchingPayment> payments, IReadOnlyList<MatchingLink> existing, MatchingSettings settings, int cap,
        List<MatchingLink> changes, Dictionary<Guid, decimal> reservedInvoices, Dictionary<Guid, decimal> reservedMovements)
    {
        var remaining = invoices.ToDictionary(invoice => invoice.Id, invoice => Math.Max(0, invoice.Total - reservedInvoices.GetValueOrDefault(invoice.Id)));
        var all = new AmountIndexes();
        var invoiceRfcs = invoices.ToDictionary(invoice => invoice.Id, invoice => new[]
        {
            MatchingText.Normalize(invoice.IssuerRfc), MatchingText.Normalize(invoice.ReceiverRfc)
        }.Distinct(StringComparer.Ordinal).ToArray());
        var byRfc = invoiceRfcs.Values.SelectMany(values => values).Distinct(StringComparer.Ordinal)
            .ToDictionary(rfc => rfc, _ => new AmountIndexes(), StringComparer.Ordinal);
        var byId = invoices.ToDictionary(invoice => invoice.Id);
        var byUuid = invoices.ToDictionary(invoice => invoice.Uuid);
        var references = invoices.ToDictionary(invoice => invoice.Id, invoice => MatchingText.References(invoice).ToHashSet(StringComparer.Ordinal));
        var postings = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
        foreach (var invoice in invoices)
        {
            foreach (var term in references[invoice.Id])
            {
                if (!postings.TryGetValue(term, out var ids)) postings.Add(term, ids = []);
                ids.Add(invoice.Id);
            }
            Add(invoice);
        }
        var operations = payments.Where(payment => payment.Currency == "MXN" && payment.Direction == "issued" && payment.Version == "2.0" && !payment.Inconsistent && !string.IsNullOrEmpty(payment.OperationNumber))
            .GroupBy(payment => payment.OperationNumber!, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var blocked = existing.Where(link => !link.Reopened).Select(link => (link.MovementId, link.InvoiceId)).ToHashSet();
        var blockCounts = blocked.GroupBy(pair => pair.MovementId).ToDictionary(group => group.Key, group => group.Count());
        long comparisons = 0;
        foreach (var movement in movements.OrderBy(movement => movement.Date).ThenBy(movement => movement.Id))
        {
            var available = movement.Credit - reservedMovements.GetValueOrDefault(movement.Id);
            if (available <= 0 || movement.Debit != 0 || movement.Classification is not null) continue;
            var text = new MatchingText(movement);
            var direct = new HashSet<Guid>();
            foreach (var term in text.Terms) if (postings.TryGetValue(term, out var ids)) direct.UnionWith(ids);
            var operationMatches = new HashSet<Guid>();
            if (movement.Reference is not null && operations.TryGetValue(movement.Reference, out var matchingPayments))
                foreach (var payment in matchingPayments)
                {
                    if (payment.Amount != movement.Credit) continue;
                    foreach (var doc in payment.Documents)
                        if (!doc.Inconsistent && doc.Currency == "MXN" && byUuid.TryGetValue(doc.InvoiceUuid, out var invoice)) operationMatches.Add(invoice.Id);
                }
            direct.UnionWith(operationMatches);
            var lookupLimit = cap + blockCounts.GetValueOrDefault(movement.Id) + 1;
            var streams = new List<Stream>();
            var cents = (int)(Math.Clamp(settings.AmountTolerance, 0, 1.00m) * 100);
            AddStreams(all, 0);
            foreach (var rfc in text.RfcTerms)
                if (byRfc.TryGetValue(rfc, out var indexes)) AddStreams(indexes, 15);

            void AddStreams(AmountIndexes indexes, int rfcPoints)
            {
                streams.Add(new(indexes.Gross.Nearest(movement.Credit, movement.Date, lookupLimit), 35 + rfcPoints));
                streams.Add(new(indexes.Residual.Nearest(movement.Credit, movement.Date, lookupLimit), 30 + rfcPoints));
                for (var delta = -cents; delta <= cents; delta++)
                {
                    var target = movement.Credit + delta / 100m;
                    streams.Add(new(indexes.Totals.Nearest(target, movement.Date, lookupLimit), (delta == 0 ? 40 : 25) + rfcPoints));
                    if (delta == 0) continue;
                    streams.Add(new(indexes.Gross.Nearest(target, movement.Date, lookupLimit), 25 + rfcPoints));
                    streams.Add(new(indexes.Residual.Nearest(target, movement.Date, lookupLimit), 25 + rfcPoints));
                }
            }
            bool Allowed(MatchingInvoice invoice) => remaining[invoice.Id] > 0 && !blocked.Contains((movement.Id, invoice.Id));
            var (candidates, truncated) = Merge(streams, movement.Date, settings.DateToleranceDays, cap, Allowed);
            foreach (var id in direct) if (Allowed(byId[id])) candidates.TryAdd(id, byId[id]);
            var scored = new List<Scored>();
            foreach (var invoice in candidates.Values)
            {
                comparisons++;
                var criteria = Score(movement, invoice, remaining[invoice.Id], settings, text, references[invoice.Id], operationMatches.Contains(invoice.Id));
                var hasAmount = criteria.Any(IsAmount);
                if (!hasAmount && !criteria.Any(criterion => criterion.Code is "invoice-reference" or "operation-number")) continue;
                if (!hasAmount) criteria = [.. criteria, new("identifier-without-amount", 0, "identifier without amount match; allocation is limited by both remaining capacities")];
                var points = criteria.Sum(criterion => criterion.Points);
                if (points >= 25) scored.Add(new(invoice, criteria, points, Math.Abs(movement.Date.DayNumber - invoice.Date.DayNumber)));
            }
            var best = scored.OrderByDescending(candidate => candidate.Points).ThenBy(candidate => candidate.Distance).ThenBy(candidate => candidate.Invoice.Id).FirstOrDefault();
            if (best is null) continue;
            var amount = Math.Min(available, remaining[best.Invoice.Id]);
            var equal = scored.Count(candidate => candidate.Points == best.Points) - 1;
            var explanation = string.Join("; ", best.Criteria.Select(criterion => criterion.Detail));
            if (truncated) explanation += $"; {cap}+ others may have matched equally";
            else if (equal > 0) explanation += $"; {equal} other invoices matched equally";
            changes.Add(new(movement.Id, best.Invoice.Id, null, null, amount, "suggested", "automatic", best.Criteria,
                best.Points >= 70 ? "high" : best.Points >= 45 ? "medium" : "low", explanation));
            Remove(best.Invoice.Id);
            remaining[best.Invoice.Id] -= amount;
            reservedInvoices[best.Invoice.Id] = reservedInvoices.GetValueOrDefault(best.Invoice.Id) + amount;
            reservedMovements[movement.Id] = reservedMovements.GetValueOrDefault(movement.Id) + amount;
            Add(best.Invoice);
        }
        return comparisons;

        void Add(MatchingInvoice invoice)
        {
            if (remaining[invoice.Id] <= 0) return;
            AddTo(all, invoice);
            foreach (var rfc in invoiceRfcs[invoice.Id]) AddTo(byRfc[rfc], invoice);
        }
        void AddTo(AmountIndexes indexes, MatchingInvoice invoice)
        {
            indexes.Totals.Add(invoice.Id, invoice.Total, invoice.Date, invoice);
            if (invoice.Retained > 0) indexes.Gross.Add(invoice.Id, invoice.Total + invoice.Retained, invoice.Date, invoice);
            if (remaining[invoice.Id] < invoice.Total) indexes.Residual.Add(invoice.Id, remaining[invoice.Id], invoice.Date, invoice);
        }
        void Remove(Guid id)
        {
            RemoveFrom(all, id);
            foreach (var rfc in invoiceRfcs[id]) RemoveFrom(byRfc[rfc], id);
        }
        static void RemoveFrom(AmountIndexes indexes, Guid id)
        {
            indexes.Totals.Remove(id); indexes.Gross.Remove(id); indexes.Residual.Remove(id);
        }
    }

    private static MatchingCriterion[] Score(MatchingMovement movement, MatchingInvoice invoice, decimal remaining, MatchingSettings settings,
        MatchingText text, HashSet<string> references, bool operation)
    {
        var criteria = new List<MatchingCriterion>();
        var amount = AmountCriterion(movement.Credit, invoice, remaining, settings.AmountTolerance);
        if (amount is not null) criteria.Add(amount);
        var distance = Math.Abs(movement.Date.DayNumber - invoice.Date.DayNumber);
        if (distance <= settings.DateToleranceDays)
        {
            var points = DatePoints(distance, settings.DateToleranceDays);
            criteria.Add(new("date-proximity", points, "invoice date is within tolerance; proximity bonus " + (points - 20)));
        }
        if (text.Description.Contains(MatchingText.Normalize(invoice.IssuerRfc), StringComparison.Ordinal) || text.Description.Contains(MatchingText.Normalize(invoice.ReceiverRfc), StringComparison.Ordinal))
            criteria.Add(new("rfc", 15, "issuer or receiver RFC found in the description"));
        if (references.Overlaps(text.Terms)) criteria.Add(new("invoice-reference", 30, "invoice serie/folio or UUID fragment found in text"));
        if (operation) criteria.Add(new("operation-number", 30, "reference equals operation number"));
        return criteria.ToArray();
    }

    private static int DatePoints(int distance, int tolerance) => distance > tolerance ? 0
        : tolerance == 0 ? 30 : 20 + (int)((10L * (tolerance - distance) + tolerance / 2) / tolerance);

    private static bool IsAmount(MatchingCriterion criterion) => criterion.Code is "amount-equals-total" or "gross-payment" or "amount-equals-remaining" or "amount-within-tolerance";

    private static MatchingCriterion? AmountCriterion(decimal credit, MatchingInvoice invoice, decimal remaining, decimal tolerance)
    {
        if (credit == invoice.Total) return new("amount-equals-total", 40, "amount equals CFDI net total");
        if (invoice.Retained > 0 && credit == invoice.Total + invoice.Retained) return new("gross-payment", 35, "amount equals total before retentions");
        if (remaining < invoice.Total && credit == remaining) return new("amount-equals-remaining", 30, "amount equals remaining payable");
        var targets = new List<(decimal Amount, string Name)> { (invoice.Total, "total") };
        if (invoice.Retained > 0) targets.Add((invoice.Total + invoice.Retained, "gross (amount equals total before retentions)"));
        if (remaining < invoice.Total) targets.Add((remaining, "remaining payable"));
        var effective = Math.Clamp(tolerance, 0, 1.00m);
        var closest = targets.Select(target => (target.Name, Difference: Math.Abs(credit - target.Amount))).OrderBy(target => target.Difference).First();
        return closest.Difference <= effective ? new("amount-within-tolerance", 25,
            $"amount within tolerance of {closest.Name} target; difference {closest.Difference * 100:0.##} cents") : null;
    }

    private static (Dictionary<Guid, MatchingInvoice> Candidates, bool Truncated) Merge(List<Stream> streams, DateOnly date, int tolerance, int cap, Func<MatchingInvoice, bool> allowed)
    {
        var queue = new PriorityQueue<Cursor, (int Score, int Distance, Guid Id)>();
        var cursors = new List<Cursor>();
        var seen = new HashSet<Guid>();
        var result = new Dictionary<Guid, MatchingInvoice>();
        try
        {
            foreach (var stream in streams)
            {
                var cursor = new Cursor(stream.Values.GetEnumerator(), stream.Points);
                cursors.Add(cursor);
                if (cursor.Values.MoveNext()) Enqueue(cursor);
            }
            while (queue.TryDequeue(out var cursor, out _))
            {
                var invoice = cursor.Values.Current;
                if (seen.Add(invoice.Id) && allowed(invoice))
                {
                    if (result.Count == cap) return (result, true);
                    result.Add(invoice.Id, invoice);
                }
                if (cursor.Values.MoveNext()) Enqueue(cursor);
            }
            return (result, false);
        }
        finally { foreach (var cursor in cursors) cursor.Values.Dispose(); }

        void Enqueue(Cursor cursor)
        {
            var invoice = cursor.Values.Current;
            var distance = Math.Abs(invoice.Date.DayNumber - date.DayNumber);
            var bonus = DatePoints(distance, tolerance);
            queue.Enqueue(cursor, (-(cursor.Points + bonus), distance, invoice.Id));
        }
    }
}
