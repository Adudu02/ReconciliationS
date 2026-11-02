using Dapper;
using Npgsql;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Jobs;

public sealed record JobRecord(Guid Id, Guid CompanyId, string Type, string State, string Payload, Guid? InputRef,
    int Attempts, int MaxAttempts, DateTime RunAfter, string? LockedBy, DateTime? LockedUntil, int ProgressDone,
    int ProgressTotal, string? LastError, Guid CreatedBy, DateTime CreatedAt, DateTime? FinishedAt)
{
    public IReadOnlyList<JobAttemptRecord> History { get; init; } = [];
}

public sealed class JobStore(NpgsqlDataSource dataSource, TimeProvider clock)
{
    internal const string Columns = """
        id, company_id AS CompanyId, type, state, payload::text AS Payload, input_ref AS InputRef,
        attempts, max_attempts AS MaxAttempts, run_after AS RunAfter, locked_by AS LockedBy, locked_until AS LockedUntil,
        progress_done AS ProgressDone, progress_total AS ProgressTotal, last_error AS LastError,
        created_by AS CreatedBy, created_at AS CreatedAt, finished_at AS FinishedAt
        """;

    public static Task<Guid> EnqueueAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid companyId, Guid actorId, string jobType, string payload, Guid? inputRef, int maxAttempts,
        DateTimeOffset now, CancellationToken cancellationToken) => connection.QuerySingleAsync<Guid>(Command("""
            INSERT INTO job(company_id,type,payload,input_ref,max_attempts,created_by,created_at,run_after)
            VALUES(@companyId,@jobType,@payload::jsonb,@inputRef,@maxAttempts,@actorId,@now,@now) RETURNING id
            """, new { companyId, jobType, payload, inputRef, maxAttempts, actorId, now }, transaction, cancellationToken: cancellationToken));

    public async Task<JobRecord?> ClaimAsync(string workerId, int leaseSeconds, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<JobRecord>(Command($"SELECT {Columns} FROM claim_next_job(@workerId,@leaseSeconds,@now)",
            new { workerId, leaseSeconds, now = clock.GetUtcNow() }, cancellationToken: cancellationToken));
    }
    public async Task<bool> FinishAsync(JobRecord job, string workerId, string? errorKind, string? error,
        TimeSpan retryDelay, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var now = clock.GetUtcNow();
        return await connection.QuerySingleAsync<bool>(Command("SELECT finish_job(@id,@workerId,@attempt,@errorKind,@error,@retryAt,@now)",
            new { id = job.Id, workerId, attempt = job.Attempts, errorKind, error, retryAt = now + retryDelay, now }, cancellationToken: cancellationToken));
    }

    public async Task<bool> RenewAsync(JobRecord job, string workerId, int leaseSeconds, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleAsync<bool>(Command("SELECT renew_job(@id,@workerId,@attempt,@leaseSeconds,@now)",
            new { id = job.Id, workerId, attempt = job.Attempts, leaseSeconds, now = clock.GetUtcNow() }, cancellationToken: cancellationToken));
    }

}
