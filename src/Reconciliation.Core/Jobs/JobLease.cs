using Dapper;
using Npgsql;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Jobs;

public sealed class JobLease(JobRecord job, TimeProvider clock)
{
    public Guid JobId => job.Id;
    public string? WorkerId => job.LockedBy;
    public DateTimeOffset UtcNow => clock.GetUtcNow();
    public async Task CheckAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var current = await connection.QuerySingleOrDefaultAsync<Guid?>(Command("""
            SELECT id FROM job WHERE id=@Id AND state='running' AND locked_by=@LockedBy AND attempts=@Attempts
              AND locked_until>@now FOR SHARE
            """, new { job.Id, job.LockedBy, job.Attempts, now = clock.GetUtcNow() }, transaction, cancellationToken: cancellationToken));
        if (current is null) throw new OperationCanceledException("Job lease lost.");
    }

    public async Task ProgressAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int done, int total, CancellationToken cancellationToken)
    {
        await CheckAsync(connection, transaction, cancellationToken);
        await connection.ExecuteAsync(Command("UPDATE job SET progress_done=@done,progress_total=@total WHERE id=@Id",
            new { done, total, job.Id }, transaction, cancellationToken: cancellationToken));
    }
}
