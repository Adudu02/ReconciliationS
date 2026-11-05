using Dapper;
using Npgsql;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Sat;

public static class SatStatusStore
{
    public static async Task<Dictionary<Guid, SatCacheEntry>> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid[] uuids, CancellationToken cancellationToken) =>
        (await connection.QueryAsync<SatCacheEntry>(Command("""
            SELECT uuid,state,cancellability,cancellation_status AS CancellationStatus,efos_raw AS EfosRaw,checked_at AS CheckedAt,
              expires_at AS ExpiresAt,last_error AS LastError,attempts,source,code_raw AS CodeRaw,state_raw AS StateRaw,
              cancellability_raw AS CancellabilityRaw,cancellation_status_raw AS CancellationStatusRaw,last_job_id AS LastJobId,last_error_kind AS LastErrorKind
            FROM sat_status_cache WHERE uuid=ANY(@uuids)
            """, new { uuids }, transaction, cancellationToken))).ToDictionary(entry => entry.Uuid);

    public static Task<int> WriteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, SatCacheEntry entry, CancellationToken cancellationToken) =>
        WriteBatchAsync(connection, transaction, companyId, [entry], cancellationToken);

    public static Task<int> WriteBatchAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, IReadOnlyList<SatCacheEntry> entries, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(Command("""
            INSERT INTO sat_status_cache(company_id,uuid,state,cancellability,cancellation_status,efos_raw,checked_at,expires_at,last_error,attempts,source,
              code_raw,state_raw,cancellability_raw,cancellation_status_raw,last_job_id,last_error_kind)
            SELECT @companyId,u.* FROM unnest(@uuids::uuid[],@states::text[],@cancellabilities::text[],@cancellations::text[],@efos::text[],
              @checked::timestamptz[],@expires::timestamptz[],@errors::text[],@attempts::integer[],@sources::text[],
              @codes::text[],@rawStates::text[],@rawCancellable::text[],@rawCancellation::text[],@lastJobs::uuid[],@errorKinds::text[]) AS u
            ON CONFLICT(company_id,uuid) DO UPDATE SET state=excluded.state,cancellability=excluded.cancellability,
              cancellation_status=excluded.cancellation_status,efos_raw=excluded.efos_raw,checked_at=excluded.checked_at,expires_at=excluded.expires_at,
              last_error=excluded.last_error,attempts=excluded.attempts,source=excluded.source,code_raw=excluded.code_raw,state_raw=excluded.state_raw,
              cancellability_raw=excluded.cancellability_raw,cancellation_status_raw=excluded.cancellation_status_raw,
              last_job_id=excluded.last_job_id,last_error_kind=excluded.last_error_kind
            """, new
        {
            companyId,
            uuids = entries.Select(e => e.Uuid).ToArray(),
            states = entries.Select(e => e.State).ToArray(),
            cancellabilities = entries.Select(e => e.Cancellability).ToArray(),
            cancellations = entries.Select(e => e.CancellationStatus).ToArray(),
            efos = entries.Select(e => e.EfosRaw).ToArray(),
            @checked = entries.Select(e => e.CheckedAt).ToArray(),
            expires = entries.Select(e => e.ExpiresAt).ToArray(),
            errors = entries.Select(e => e.LastError).ToArray(),
            attempts = entries.Select(e => e.Attempts).ToArray(),
            sources = entries.Select(e => e.Source).ToArray(),
            codes = entries.Select(e => e.CodeRaw).ToArray(),
            rawStates = entries.Select(e => e.StateRaw).ToArray(),
            rawCancellable = entries.Select(e => e.CancellabilityRaw).ToArray(),
            rawCancellation = entries.Select(e => e.CancellationStatusRaw).ToArray(),
            lastJobs = entries.Select(e => e.LastJobId).ToArray(),
            errorKinds = entries.Select(e => e.LastErrorKind).ToArray()
        }, transaction, cancellationToken));
}
