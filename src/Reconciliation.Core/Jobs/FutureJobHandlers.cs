using Npgsql;

namespace Reconciliation.Core.Jobs;

public sealed class ReconciliationRunHandler : IJobHandler
{
    public string Type => "reconciliation_run";
    // shortcut: group 6 implements matching; this placeholder records its limit without touching domain data.
    public Task<string?> HandleAsync(JobRecord job, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken) =>
        Task.FromResult<string?>("not implemented until group 6");
}

public sealed class StatusCheckHandler : IJobHandler
{
    public string Type => "status_check";
    // shortcut: group 7 implements SAT checks; this placeholder makes no network call or status change.
    public Task<string?> HandleAsync(JobRecord job, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken) =>
        Task.FromResult<string?>("not implemented until group 7");
}
