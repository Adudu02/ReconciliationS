namespace Reconciliation.Core.Common;

/// <summary>Answers whether the database is reachable. Never throws for a database failure.</summary>
public interface IDatabaseHealth
{
    Task<bool> IsReachableAsync(CancellationToken cancellationToken = default);
}
