using Microsoft.Extensions.Logging;
using Npgsql;
using Reconciliation.Core.Tenancy;

namespace Reconciliation.Core.Jobs;

public interface IJobHandler
{
    string Type { get; }
    Task<string?> HandleAsync(JobRecord job, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken);
}

public sealed partial class JobDispatcher(JobStore store, CompanyScope companyScope, IEnumerable<IJobHandler> handlers,
    JobOptions options, JobBackoff backoff, TimeProvider clock, ILogger<JobDispatcher> logger)
{
    public async Task<bool> RunOneAsync(string workerId, CancellationToken cancellationToken)
    {
        var job = await store.ClaimAsync(workerId, options.LeaseSeconds, cancellationToken);
        if (job is null) return false;
        Claimed(logger, job.Id, job.CompanyId, job.Type, job.Attempts);
        using var workCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = HeartbeatAsync(job, workerId, workCancellation, heartbeatCancellation.Token);
        try
        {
            string? note = null;
            string? errorKind = null;
            string? reason = null;
            var completedInTransaction = false;
            try
            {
                var handler = handlers.SingleOrDefault(candidate => candidate.Type == job.Type)
                    ?? throw new InvalidDataException("Unsupported job type.");
                if (handler is IPhasedJobHandler phased)
                {
                    var result = await phased.HandlePhasedAsync(job, new JobHandlerContext(companyScope, job, clock), workCancellation.Token);
                    note = result.Note;
                    completedInTransaction = result.CompleteInTransaction;
                    if (result.Retry) { errorKind = "transient"; reason = "SAT checks deferred; retry scheduled."; }
                }
                else
                    note = await companyScope.ExecuteAsync(job.CompanyId, (connection, transaction, transactionCancellationToken) =>
                        handler.HandleAsync(job, connection, transaction, transactionCancellationToken), workCancellation.Token);
            }
            catch (OperationCanceledException) when (workCancellation.IsCancellationRequested) { return true; }
#pragma warning disable CA1031 // Worker boundary: retain unexpected failures without leaking exception details to clients.
            catch (Exception error)
#pragma warning restore CA1031
            {
                var failure = error.GetBaseException();
                var transient = failure is TimeoutException || failure is NpgsqlException { IsTransient: true };
                errorKind = transient ? "transient" : "permanent";
                reason = failure switch
                {
                    InvalidDataException => failure.Message[..Math.Min(failure.Message.Length, 2000)],
                    CompanyReadOnlyException => "Archived company is read-only.",
                    _ => transient ? "Temporary worker failure." : "Job handler failed."
                };
            }
            if (!workCancellation.IsCancellationRequested && !completedInTransaction)
            {
                var finished = await store.FinishAsync(job, workerId, errorKind, reason,
                    errorKind == "transient" ? backoff.Delay(job.Attempts) : TimeSpan.Zero, note, cancellationToken);
                Finished(logger, job.Id, job.CompanyId, job.Type, errorKind ?? "succeeded", finished);
            }
            else if (completedInTransaction) Finished(logger, job.Id, job.CompanyId, job.Type, "succeeded", true);
            return true;
        }
        finally
        {
            await heartbeatCancellation.CancelAsync();
            try { await heartbeat; }
            catch (OperationCanceledException) when (heartbeatCancellation.IsCancellationRequested) { }
        }
    }

    private async Task HeartbeatAsync(JobRecord job, string workerId, CancellationTokenSource workCancellation, CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(options.LeaseSeconds / 3d), clock, cancellationToken);
            try
            {
                if (await store.RenewAsync(job, workerId, options.LeaseSeconds, cancellationToken)) continue;
            }
            catch (NpgsqlException) { }
            await workCancellation.CancelAsync();
            LeaseLost(logger, job.Id, job.CompanyId, job.Type);
            return;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Claimed job {JobId} company {CompanyId} type {Type} attempt {Attempt}")]
    private static partial void Claimed(ILogger logger, Guid jobId, Guid companyId, string type, int attempt);
    [LoggerMessage(Level = LogLevel.Information, Message = "Finished job {JobId} company {CompanyId} type {Type} outcome {Outcome} lease accepted {Accepted}")]
    private static partial void Finished(ILogger logger, Guid jobId, Guid companyId, string type, string outcome, bool accepted);
    [LoggerMessage(Level = LogLevel.Warning, Message = "Lost lease for job {JobId} company {CompanyId} type {Type}; canceling handler")]
    private static partial void LeaseLost(ILogger logger, Guid jobId, Guid companyId, string type);
}
