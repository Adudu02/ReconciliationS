using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using Reconciliation.Core.Tenancy;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Audit;

public sealed record AuditVerification(bool Consistent, long Entries, long? FirstInconsistentSeq, string? Reason);

public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAudit(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/companies/{companyId:guid}/audit-entries", ReadAsync).RequireAuthorization();
        routes.MapGet("/companies/{companyId:guid}/audit/verification", (Guid companyId, HttpContext httpContext, CompanyScope scope, CancellationToken cancellationToken) =>
            ReadAuthorizedAsync(companyId, httpContext, scope, async (connection, transaction, token) =>
                Results.Ok(await connection.QuerySingleAsync<AuditVerification>(Command("""
                    SELECT consistent,entries,first_inconsistent_seq AS FirstInconsistentSeq,reason FROM verify_audit_chain(@companyId)
                    """, new { companyId }, transaction, cancellationToken: token))), cancellationToken)).RequireAuthorization();
        return routes;
    }

    private static Task<IResult> ReadAsync(Guid companyId, DateTimeOffset? from, DateTimeOffset? to, string? actor, string? action,
        string? item, string? itemKind, long? afterSeq, int? limit, HttpContext httpContext, CompanyScope scope, CancellationToken cancellationToken) =>
        ReadAuthorizedAsync(companyId, httpContext, scope, async (connection, transaction, token) =>
        {
            var pageSize = limit ?? 50;
            if (pageSize is < 1 or > 200 || afterSeq < 0 || from > to || (itemKind is not null && item is null) ||
                (actor is not null && actor != "system" && !Guid.TryParse(actor, out _)))
                return Results.BadRequest(new { error = "Invalid audit filters. Limit must be 1–200; afterSeq nonnegative; actor a user UUID or system; from <= to; itemKind requires item." });
            var normalizedActor = Guid.TryParse(actor, out var userId) ? userId.ToString() : actor;
            var items = item is null ? null : JsonSerializer.Serialize(itemKind is null
                ? [new Dictionary<string, string> { ["id"] = item }]
                : new[] { new Dictionary<string, string> { ["kind"] = itemKind, ["id"] = item } });
            var rows = (await connection.QueryAsync<AuditRow>(Command("""
                SELECT id,company_seq AS Seq,occurred_at AS OccurredAt,actor_type AS ActorType,coalesce(actor_id::text,'system') AS Actor,
                  action,items::text AS Items,detail::text AS Detail,encode(prev_hash,'hex') AS PrevHash,encode(hash,'hex') AS Hash
                FROM audit_entry WHERE company_seq>@after
                  AND (@from::timestamptz IS NULL OR occurred_at>=@from::timestamptz)
                  AND (@to::timestamptz IS NULL OR occurred_at<=@to::timestamptz)
                  AND (@actor::text IS NULL OR coalesce(actor_id::text,'system')=@actor)
                  AND (@action::text IS NULL OR action=@action)
                  AND (@items::jsonb IS NULL OR items @> @items::jsonb)
                ORDER BY company_seq LIMIT @take
                """, new { after = afterSeq ?? 0, from = from?.ToUniversalTime(), to = to?.ToUniversalTime(), actor = normalizedActor, action, items, take = pageSize + 1 }, transaction, cancellationToken: token))).ToArray();
            return Results.Ok(new
            {
                entries = rows.Take(pageSize).Select(row => new
                {
                    row.Id,
                    row.Seq,
                    row.OccurredAt,
                    row.ActorType,
                    row.Actor,
                    row.Action,
                    items = JsonSerializer.Deserialize<JsonElement>(row.Items),
                    detail = JsonSerializer.Deserialize<JsonElement>(row.Detail),
                    row.PrevHash,
                    row.Hash
                }).ToArray(),
                nextAfterSeq = rows.Length > pageSize ? rows[pageSize - 1].Seq : (long?)null
            });
        }, cancellationToken);

    private static Task<IResult> ReadAuthorizedAsync(Guid companyId, HttpContext httpContext, CompanyScope scope,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<IResult>> read, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(httpContext.Request.Headers[CompanyHeader.Name], out var selected) || selected != companyId || httpContext.User.UserId() is not Guid user)
            return Task.FromResult<IResult>(Results.NotFound());
        return scope.ExecuteResultAsync(companyId, async (connection, transaction, token) =>
        {
            // Check membership inside the same RLS transaction so unknown and inaccessible companies return the same 404.
            if (!await connection.ExecuteScalarAsync<bool>(Command("SELECT EXISTS(SELECT FROM membership WHERE user_id=@user)", new { user }, transaction, cancellationToken: token)))
                return Results.NotFound();
            return await read(connection, transaction, token);
        }, cancellationToken);
    }

    private sealed record AuditRow(Guid Id, long Seq, DateTime OccurredAt, string ActorType, string Actor, string Action, string Items, string Detail, string PrevHash, string Hash);
}
