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
              cancellability_raw AS CancellabilityRaw,cancellation_status_raw AS CancellationStatusRaw
            FROM sat_status_cache WHERE uuid=ANY(@uuids)
            """, new { uuids }, transaction, cancellationToken))).ToDictionary(entry => entry.Uuid);

    public static Task<int> WriteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, SatCacheEntry entry, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(Command("""
            INSERT INTO sat_status_cache(company_id,uuid,state,cancellability,cancellation_status,efos_raw,checked_at,expires_at,last_error,attempts,source,
              code_raw,state_raw,cancellability_raw,cancellation_status_raw)
            VALUES(@companyId,@Uuid,@State,@Cancellability,@CancellationStatus,@EfosRaw,@CheckedAt,@ExpiresAt,@LastError,@Attempts,@Source,
              @CodeRaw,@StateRaw,@CancellabilityRaw,@CancellationStatusRaw)
            ON CONFLICT(company_id,uuid) DO UPDATE SET state=excluded.state,cancellability=excluded.cancellability,
              cancellation_status=excluded.cancellation_status,efos_raw=excluded.efos_raw,checked_at=excluded.checked_at,expires_at=excluded.expires_at,
              last_error=excluded.last_error,attempts=excluded.attempts,source=excluded.source,code_raw=excluded.code_raw,state_raw=excluded.state_raw,
              cancellability_raw=excluded.cancellability_raw,cancellation_status_raw=excluded.cancellation_status_raw
            """, new
        {
            companyId,
            entry.Uuid,
            entry.State,
            entry.Cancellability,
            entry.CancellationStatus,
            entry.EfosRaw,
            entry.CheckedAt,
            entry.ExpiresAt,
            entry.LastError,
            entry.Attempts,
            entry.Source,
            entry.CodeRaw,
            entry.StateRaw,
            entry.CancellabilityRaw,
            entry.CancellationStatusRaw
        }, transaction, cancellationToken));
}
