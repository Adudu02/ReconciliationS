using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Reconciliation.Core.Audit;
using Reconciliation.Core.Tenancy;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Jobs;

public sealed record JobAttemptRecord(int AttemptNo, DateTime StartedAt, DateTime? FinishedAt, string Outcome, string? ErrorKind, string? Error);

public static class JobEndpoints
{
    public static IEndpointRouteBuilder MapJobs(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/companies/{companyId:guid}/jobs/{jobId:guid}", (Guid companyId, Guid jobId, CompanyScope companyScope, CancellationToken cancellationToken) =>
            companyScope.ExecuteResultAsync(companyId, async (connection, transaction, transactionCancellationToken) =>
            {
                var job = await connection.QuerySingleOrDefaultAsync<JobRecord>(Command($"SELECT {JobStore.Columns} FROM job WHERE id=@jobId", new { jobId }, transaction, cancellationToken: transactionCancellationToken));
                if (job is null) return Results.NotFound();
                var history = await connection.QueryAsync<JobAttemptRecord>(Command("""
                    SELECT attempt_no AS AttemptNo,started_at AS StartedAt,finished_at AS FinishedAt,outcome,error_kind AS ErrorKind,error
                    FROM job_attempt WHERE job_id=@jobId ORDER BY attempt_no
                    """, new { jobId }, transaction, cancellationToken: transactionCancellationToken));
                return Results.Ok(job with { History = history.ToArray() });
            }, cancellationToken)).RequireAuthorization(Policies.CompanyMember);
        routes.MapGet("/companies/{companyId:guid}/jobs", (Guid companyId, string? status, CompanyScope companyScope, CancellationToken cancellationToken) =>
            companyScope.ExecuteResultAsync(companyId, async (connection, transaction, transactionCancellationToken) =>
            {
                if (status != "failed") return Results.BadRequest(new { error = "Only status=failed is supported." });
                var jobs = (await connection.QueryAsync<JobRecord>(Command($"SELECT {JobStore.Columns} FROM job WHERE state='failed' ORDER BY created_at,id", transaction: transaction, cancellationToken: transactionCancellationToken))).ToArray();
                var attempts = (await connection.QueryAsync<AttemptWithJob>(Command("""
                    SELECT a.job_id AS JobId,a.attempt_no AS AttemptNo,a.started_at AS StartedAt,a.finished_at AS FinishedAt,a.outcome,a.error_kind AS ErrorKind,a.error
                    FROM job_attempt a JOIN job j ON j.id=a.job_id WHERE j.state='failed' ORDER BY a.attempt_no
                    """, transaction: transaction, cancellationToken: transactionCancellationToken))).ToLookup(attempt => attempt.JobId);
                return Results.Ok(jobs.Select(job => job with { History = attempts[job.Id].Select(attempt => attempt.ToRecord()).ToArray() }));
            }, cancellationToken)).RequireAuthorization(Policies.Administrator);
        routes.MapPost("/companies/{companyId:guid}/jobs/{jobId:guid}/retry", (Guid companyId, Guid jobId, HttpContext httpContext, CompanyScope companyScope, JobOptions options, TimeProvider clock, CancellationToken cancellationToken) =>
            companyScope.ExecuteWriteResultAsync(companyId, async (connection, transaction, transactionCancellationToken) =>
            {
                var job = await connection.QuerySingleOrDefaultAsync<JobRecord>(Command($"SELECT {JobStore.Columns} FROM job WHERE id=@jobId FOR UPDATE", new { jobId }, transaction, cancellationToken: transactionCancellationToken));
                if (job is null) return Results.NotFound();
                if (job.State != "failed") return Results.Conflict(new { error = "Only failed jobs may be retried." });
                if (job.Type == "reconciliation_run" && await connection.ExecuteScalarAsync<bool>(Command("SELECT EXISTS(SELECT FROM job WHERE type='reconciliation_run' AND state IN ('queued','running'))", transaction: transaction, cancellationToken: transactionCancellationToken)))
                    return Results.Conflict(new { error = "A reconciliation run is already active." });
                await connection.ExecuteAsync(Command("""
                    UPDATE job SET state='queued',max_attempts=attempts+@MaxAttempts,run_after=@now,finished_at=NULL,last_error=NULL WHERE id=@jobId
                    """, new { jobId, options.MaxAttempts, now = clock.GetUtcNow() }, transaction, cancellationToken: transactionCancellationToken));
                await AuditLog.WriteAsync(connection, transaction, companyId, httpContext.User.UserId()!.Value, "job.retried", new { job.State, job.Attempts }, new { jobId }, transactionCancellationToken, [new("job", jobId.ToString())]);
                return Results.Accepted($"/companies/{companyId}/jobs/{jobId}", new { jobId });
            }, cancellationToken)).RequireAuthorization(Policies.Administrator);
        routes.MapPost("/companies/{companyId:guid}/reconciliation-runs", (Guid companyId, HttpContext httpContext, CompanyScope companyScope, JobOptions options, TimeProvider clock, CancellationToken cancellationToken) =>
            companyScope.ExecuteWriteResultAsync(companyId, async (connection, transaction, transactionCancellationToken) =>
            {
                var active = await connection.QuerySingleOrDefaultAsync<JobRecord>(Command($"SELECT {JobStore.Columns} FROM job WHERE type='reconciliation_run' AND state IN ('queued','running')", transaction: transaction, cancellationToken: transactionCancellationToken));
                if (active is not null) return Results.Ok(new { jobId = active.Id, active.State });
                var jobId = await JobStore.EnqueueAsync(connection, transaction, companyId, httpContext.User.UserId()!.Value,
                    "reconciliation_run", "{}", null, options.MaxAttempts, clock.GetUtcNow(), transactionCancellationToken);
                return Results.Accepted($"/companies/{companyId}/jobs/{jobId}", new { jobId, state = "queued" });
            }, cancellationToken)).RequireAuthorization(Policies.Accountant);
        return routes;
    }

    private sealed record AttemptWithJob(Guid JobId, int AttemptNo, DateTime StartedAt, DateTime? FinishedAt, string Outcome, string? ErrorKind, string? Error)
    {
        public JobAttemptRecord ToRecord() => new(AttemptNo, StartedAt, FinishedAt, Outcome, ErrorKind, Error);
    }
}
