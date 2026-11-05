using Dapper;
using Npgsql;
using Reconciliation.Core.Tenancy;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Jobs;

public sealed record JobHandlerResult(string? Note = null, bool Retry = false, bool CompleteInTransaction = false);

public interface IPhasedJobHandler : IJobHandler
{
    Task<JobHandlerResult> HandlePhasedAsync(JobRecord job, JobHandlerContext context, CancellationToken cancellationToken);
}

public sealed class JobHandlerContext(CompanyScope scope, JobRecord job, TimeProvider clock)
{
    public Task<T> ReadAsync<T>(Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> work, CancellationToken cancellationToken) =>
        scope.ExecuteAsync(job.CompanyId, async (connection, transaction, token) =>
        {
            var result = await work(connection, transaction, token);
            await new JobLease(job, clock).CheckAsync(connection, transaction, token);
            return result;
        }, cancellationToken);

    public Task<JobHandlerResult> WriteAsync(Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<JobHandlerResult>> work,
        CancellationToken cancellationToken) => scope.ExecuteWriteAsync(job.CompanyId, async (connection, transaction, token) =>
        {
            var result = await work(connection, transaction, token);
            await new JobLease(job, clock).CheckAsync(connection, transaction, token);
            if (result.CompleteInTransaction)
            {
                var accepted = await connection.ExecuteScalarAsync<bool>(Command("SELECT finish_job(@Id,@LockedBy,@Attempts,NULL,NULL,@now,@now,@Note)",
                    new { job.Id, job.LockedBy, job.Attempts, now = clock.GetUtcNow(), result.Note }, transaction, token));
                if (!accepted) throw new OperationCanceledException("Job lease lost before completion.");
            }
            return result;
        }, cancellationToken);
}
