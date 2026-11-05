using System.Text.Json;
using Dapper;
using Npgsql;
using Reconciliation.Core.Audit;
using Reconciliation.Core.Matching;
using Reconciliation.Core.Sat;
using Reconciliation.Core.Tenancy;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Jobs;

public sealed record SatCheckPayload(Guid[] CfdiIds, Guid? AnonymizedUuid = null, int FollowUpGeneration = 0, Guid? FollowUpParentId = null);

public sealed class StatusCheckHandler(SatStatusLookup lookup, SatOptions options, JobOptions jobs, TimeProvider clock) : IPhasedJobHandler
{
    public string Type => "status_check";
    public Task<string?> HandleAsync(JobRecord job, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("SAT checks require phased dispatcher execution.");

    public async Task<JobHandlerResult> HandlePhasedAsync(JobRecord job, JobHandlerContext context, CancellationToken cancellationToken)
    {
        var payload = ParsePayload(job.Payload);
        var initial = await context.ReadAsync(async (connection, transaction, token) =>
        {
            if (await connection.ExecuteScalarAsync<bool>(Command("SELECT archived_at IS NOT NULL FROM company", transaction: transaction, cancellationToken: token)))
                throw new CompanyReadOnlyException();
            return await LoadAsync(connection, transaction, payload.CfdiIds, token);
        }, cancellationToken);
        var completed = new List<Completed>();
        using (var budget = new CancellationTokenSource(options.JobBudget, clock))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token))
            foreach (var row in initial.Rows)
            {
                if (budget.IsCancellationRequested) break;
                var previous = initial.Cache.GetValueOrDefault(row.Uuid);
                try
                {
                    var request = new SatStatusRequest(row.Uuid, row.IssuerRfc, row.ReceiverRfc, row.Total, payload.AnonymizedUuid);
                    var result = previous is { LastErrorKind: "permanent" } && previous.LastJobId == job.Id
                        ? new SatLookupResult(null, previous, 0, previous.LastError, false)
                        : await lookup.CheckAsync(request, previous, linked.Token);
                    completed.Add(new(row, previous, result, clock.GetUtcNow().UtcDateTime));
                }
                catch (OperationCanceledException) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { break; }
            }
        // No connection or transaction survives the HTTP phase; the context owns both short transactions.
        return await context.WriteAsync(async (connection, transaction, token) =>
        {
            var latest = await LoadAsync(connection, transaction, payload.CfdiIds, token);
            var current = latest.Rows.ToDictionary(row => row.Id);
            var entries = new List<SatCacheEntry>();
            var ordinary = new List<(Guid Id, string State, string? Cancellation)>();
            var changes = new List<(CfdiRow Row, AuditValues Before, AuditValues After, bool EfosChanged)>();
            var newlyCancelled = new List<(Guid Id, string? Cancellation)>();
            var retry = false;
            foreach (var item in completed)
            {
                var row = current[item.Row.Id];
                var cache = latest.Cache.GetValueOrDefault(row.Uuid);
                if (cache?.State == "cancelled" || (cache is not null && cache != item.Previous &&
                    (item.Previous is null || cache.CheckedAt >= item.Previous.CheckedAt))) continue;
                if (row.State == "cancelled")
                {
                    if (cache is null || cache.State != "cancelled")
                        entries.Add(new(row.Uuid, "cancelled", cache?.Cancellability, row.CancellationStatus, cache?.EfosRaw,
                            item.CheckedAt, null, null, 0, "backfill"));
                    continue;
                }
                if (item.Result.FromCache) continue;
                var entry = Entry(row, cache, item.Result, item.CheckedAt, job.Id);
                entries.Add(entry);
                retry |= item.Result.Retry;
                var hasAnswer = item.Result.Response is SatAnswered or SatNotFound;
                var updatesStatus = hasAnswer || item.Result.Retry;
                if (!updatesStatus) continue;
                var before = Values(row.State, row.CancellationStatus, cache);
                var after = Values(entry.State, entry.CancellationStatus, entry);
                if (entry.State == "cancelled") newlyCancelled.Add((row.Id, entry.CancellationStatus));
                else ordinary.Add((row.Id, entry.State, entry.CancellationStatus));
                if (before != after)
                    changes.Add((row, before, after, IsEfosFlagged(entry) && (!IsEfosFlagged(cache) || cache?.EfosRaw != entry.EfosRaw)));
            }
            if (entries.Count > 0) await SatStatusStore.WriteBatchAsync(connection, transaction, job.CompanyId, entries, token);
            if (ordinary.Count > 0)
                await connection.ExecuteAsync(Command("""
                    UPDATE cfdi c SET sat_status=u.state,cancellation_status=u.cancellation
                    FROM unnest(@ids::uuid[],@states::text[],@cancellations::text[]) AS u(id,state,cancellation)
                    WHERE c.id=u.id AND (c.sat_status IS DISTINCT FROM u.state OR c.cancellation_status IS DISTINCT FROM u.cancellation)
                    """, new { ids = ordinary.Select(r => r.Id).ToArray(), states = ordinary.Select(r => r.State).ToArray(), cancellations = ordinary.Select(r => r.Cancellation).ToArray() }, transaction, token));
            foreach (var changed in newlyCancelled)
                await SatStatusChanges.ApplySatStatusChangeAsync(connection, transaction, changed.Id, "cancelled", changed.Cancellation, clock.GetUtcNow(), token);
            foreach (var changed in changes)
            {
                AuditItem[] items = [new("cfdi", changed.Row.Uuid.ToString()), new("job", job.Id.ToString())];
                await AuditLog.WriteSystemAsync(connection, transaction, job.CompanyId, job.Id, Type, "sat.status_changed", changed.Before, changed.After, items, token);
                if (changed.EfosChanged)
                    await AuditLog.WriteSystemAsync(connection, transaction, job.CompanyId, job.Id, Type, "sat.efos_flagged", changed.Before, changed.After, items, token);
            }
            var uncheckedIds = initial.Rows.Skip(completed.Count).Select(row => row.Id).Where(id =>
                current[id].State != "cancelled" && !(latest.Cache.TryGetValue(current[id].Uuid, out var cached) && cached.LastError is null &&
                    cached.State != "unknown" && (cached.ExpiresAt is null || cached.ExpiresAt > clock.GetUtcNow().UtcDateTime))).ToArray();
            var followUp = uncheckedIds.Length > 0 && !retry && payload.FollowUpGeneration < options.MaxFollowUpGenerations;
            if (followUp)
            {
                var exists = await connection.ExecuteScalarAsync<bool>(Command("""
                    SELECT EXISTS(SELECT FROM job WHERE type='status_check' AND state IN ('queued','running') AND payload->>'followUpParentId'=@parent)
                    """, new { parent = job.Id.ToString() }, transaction, token));
                if (!exists)
                    await JobStore.EnqueueAsync(connection, transaction, job.CompanyId, job.CreatedBy, Type,
                        JsonSerializer.Serialize(new SatCheckPayload(uncheckedIds, payload.AnonymizedUuid, payload.FollowUpGeneration + 1, job.Id), JsonSerializerOptions.Web),
                        null, jobs.MaxAttempts, clock.GetUtcNow(), token, uncheckedIds.Length);
            }
            await connection.ExecuteAsync(Command("UPDATE job SET progress_done=@done,progress_total=@total WHERE id=@id",
                new { done = completed.Count, total = initial.Rows.Length, id = job.Id }, transaction, token));
            return new JobHandlerResult($"Checked {completed.Count}; deferred {uncheckedIds.Length}; status changes {changes.Count}.",
                retry || (uncheckedIds.Length > 0 && !followUp), CompleteInTransaction: followUp);
        }, cancellationToken);
    }

    private SatCacheEntry Entry(CfdiRow row, SatCacheEntry? previous, SatLookupResult result, DateTime now, Guid jobId)
    {
        if (result.Response is not (SatAnswered or SatNotFound))
        {
            var retained = previous ?? new SatCacheEntry(row.Uuid, "unknown", null, null, null, now, now, null, 0, options.Mode);
            return retained with { State = result.Retry ? "unknown" : retained.State, CheckedAt = now, ExpiresAt = now, LastError = result.Error, Attempts = result.Attempts, LastJobId = jobId, LastErrorKind = result.Retry ? "transient" : "permanent" };
        }
        var response = result.Response;
        var state = SatContract.StorageState(response);
        var reply = response.Reply;
        return new(row.Uuid, state, SatContract.StorageCancellability(reply.Cancellability.Value), SatContract.StorageCancellation(reply.CancellationStatus.Value),
            reply.EfosRaw, now, state switch { "cancelled" => null, "current" => now + options.CurrentCacheTtl, "not_found" => now + options.NotFoundCacheTtl, _ => now },
            null, result.Attempts, options.Mode, reply.Code.Raw, reply.State.Raw, reply.Cancellability.Raw, reply.CancellationStatus.Raw, jobId);
    }

    private static SatCheckPayload ParsePayload(string json)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<SatCheckPayload>(json, JsonSerializerOptions.Web);
            if (payload?.CfdiIds is not { Length: > 0 and <= 100 } || payload.CfdiIds.Any(id => id == Guid.Empty) ||
                payload.CfdiIds.Distinct().Count() != payload.CfdiIds.Length || payload.FollowUpGeneration < 0 ||
                payload.AnonymizedUuid == Guid.Empty || (payload.AnonymizedUuid is not null && payload.CfdiIds.Length != 1))
                throw new InvalidDataException("SAT job requires 1-100 distinct CFDI ids and a valid optional anonymised key.");
            return payload;
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid SAT job payload.", error); }
    }

    private static async Task<Snapshot> LoadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid[] ids, CancellationToken token)
    {
        var rows = (await connection.QueryAsync<CfdiRow>(Command("""
            SELECT id,uuid,issuer_rfc AS IssuerRfc,receiver_rfc AS ReceiverRfc,total,sat_status AS State,cancellation_status AS CancellationStatus
            FROM cfdi WHERE id=ANY(@ids) ORDER BY id
            """, new { ids }, transaction, token))).ToArray();
        if (rows.Length != ids.Length) throw new InvalidDataException("SAT job input is not visible in this company.");
        return new(rows, await SatStatusStore.ReadAsync(connection, transaction, rows.Select(row => row.Uuid).ToArray(), token));
    }
    private static bool IsEfosFlagged(SatCacheEntry? entry) => entry?.CodeRaw == SatContract.SuccessCode && !string.IsNullOrEmpty(entry.EfosRaw) && entry.EfosRaw != "200";
    private static AuditValues Values(string state, string? cancellation, SatCacheEntry? cache) =>
        new(state, cache?.Cancellability, cancellation, cache?.EfosRaw, cache?.CodeRaw, cache?.StateRaw, cache?.CancellabilityRaw, cache?.CancellationStatusRaw);
    private sealed record AuditValues(string State, string? Cancellability, string? CancellationStatus, string? EfosRaw, string? CodeRaw, string? StateRaw, string? CancellabilityRaw, string? CancellationStatusRaw);
    private sealed record CfdiRow(Guid Id, Guid Uuid, string IssuerRfc, string ReceiverRfc, decimal Total, string State, string? CancellationStatus);
    private sealed record Snapshot(CfdiRow[] Rows, Dictionary<Guid, SatCacheEntry> Cache);
    private sealed record Completed(CfdiRow Row, SatCacheEntry? Previous, SatLookupResult Result, DateTime CheckedAt);
}
