namespace Reconciliation.Core.Matching;

public static class ComplementDeadline
{
    public static DateOnly Calculate(DateOnly paymentDate, bool nextBusinessDay)
    {
        var fifth = new DateOnly(paymentDate.Year, paymentDate.Month, 5).AddMonths(1);
        return nextBusinessDay ? fifth.DayOfWeek switch
        {
            DayOfWeek.Saturday => fifth.AddDays(2),
            DayOfWeek.Sunday => fifth.AddDays(1),
            _ => fifth
        } : fifth;
    }
}
