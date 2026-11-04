using Npgsql;

namespace Reconciliation.Core.Jobs;

public sealed class StatusCheckHandler : IJobHandler
{
    public string Type => "status_check";
    // shortcut: group 7 implements SAT checks; this placeholder makes no network call or status change.
    public Task<string?> HandleAsync(JobRecord job, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken) =>
        Task.FromResult<string?>("not implemented until group 7");
}
