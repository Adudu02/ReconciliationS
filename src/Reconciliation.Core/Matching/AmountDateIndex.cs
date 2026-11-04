namespace Reconciliation.Core.Matching;

internal sealed class AmountDateIndex<T>
{
    private sealed class Bucket
    {
        public SortedSet<DateOnly> Dates { get; } = [];
        public Dictionary<DateOnly, SortedSet<Guid>> Ids { get; } = [];
    }

    private readonly Dictionary<decimal, Bucket> buckets = [];
    private readonly Dictionary<Guid, (T Value, decimal Amount, DateOnly Date)> entries = [];

    public void Add(Guid id, decimal amount, DateOnly date, T value)
    {
        entries.Add(id, (value, amount, date));
        if (!buckets.TryGetValue(amount, out var bucket)) buckets.Add(amount, bucket = new());
        bucket.Dates.Add(date);
        if (!bucket.Ids.TryGetValue(date, out var ids)) bucket.Ids.Add(date, ids = []);
        ids.Add(id);
    }

    public void Remove(Guid id)
    {
        if (!entries.Remove(id, out var entry)) return;
        var bucket = buckets[entry.Amount];
        var ids = bucket.Ids[entry.Date];
        ids.Remove(id);
        if (ids.Count > 0) return;
        bucket.Ids.Remove(entry.Date);
        bucket.Dates.Remove(entry.Date);
        if (bucket.Dates.Count == 0) buckets.Remove(entry.Amount);
    }

    public IEnumerable<T> Nearest(decimal amount, DateOnly date, int limit)
    {
        if (!buckets.TryGetValue(amount, out var bucket)) yield break;
        using var forward = Ordered(bucket, date, false).GetEnumerator();
        using var backward = Ordered(bucket, date, true).GetEnumerator();
        var hasForward = forward.MoveNext();
        var hasBackward = backward.MoveNext();
        for (var count = 0; count < limit && (hasForward || hasBackward); count++)
        {
            var useForward = !hasBackward || (hasForward && Compare(forward.Current, backward.Current, date) <= 0);
            var id = useForward ? forward.Current.Id : backward.Current.Id;
            yield return entries[id].Value;
            if (useForward) hasForward = forward.MoveNext();
            else hasBackward = backward.MoveNext();
        }
    }

    private static IEnumerable<(DateOnly Date, Guid Id)> Ordered(Bucket bucket, DateOnly date, bool reverse)
    {
        var dates = reverse ? bucket.Dates.GetViewBetween(DateOnly.MinValue, date).Reverse()
            : bucket.Dates.GetViewBetween(date, DateOnly.MaxValue);
        foreach (var candidateDate in dates)
        {
            if (reverse && candidateDate == date) continue;
            foreach (var id in bucket.Ids[candidateDate]) yield return (candidateDate, id);
        }
    }

    private static int Compare((DateOnly Date, Guid Id) a, (DateOnly Date, Guid Id) b, DateOnly date)
    {
        var distance = Math.Abs(a.Date.DayNumber - date.DayNumber).CompareTo(Math.Abs(b.Date.DayNumber - date.DayNumber));
        return distance != 0 ? distance : a.Id.CompareTo(b.Id);
    }
}
