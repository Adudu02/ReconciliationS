using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Reconciliation.Core.Tenancy;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Matching;

public static class ReviewEndpoints
{
    public static IEndpointRouteBuilder MapReview(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/companies/{companyId:guid}/review-queue", QueueAsync).RequireAuthorization(Policies.CompanyMember);
        return routes;
    }

    private static Task<IResult> QueueAsync(Guid companyId, int? limit, int? afterRank, DateOnly? afterDate, Guid? afterId,
        CompanyScope scope, TimeProvider clock, CancellationToken cancellationToken) => scope.ExecuteResultAsync(companyId, async (connection, transaction, token) =>
    {
        var take = limit ?? 50;
        if (take is < 1 or > 200 || afterRank.HasValue != afterDate.HasValue || afterDate.HasValue != afterId.HasValue || afterRank is < 0 or > 3)
            return Results.BadRequest(new { error = "Invalid queue cursor or limit (1–200)." });
        var rows = (await connection.QueryAsync<QueueRow>(Command("""
            WITH reasons AS MATERIALIZED (SELECT * FROM cfdi_review_reasons(@today)), queue AS (
              SELECT CASE l.confidence WHEN 'high' THEN 0 WHEN 'medium' THEN 1 ELSE 2 END AS rank,m.movement_date AS date,l.id,
                jsonb_build_object('kind','link','id',l.id,'linkId',l.id,'cfdiId',c.id,'movementId',m.id,
                  'movement',jsonb_build_object('date',m.movement_date,'amount',m.credit+m.debit,'description',m.description),
                  'invoice',jsonb_build_object('date',c.issued_at,'amount',c.total,'issuerRfc',c.issuer_rfc,'receiverRfc',c.receiver_rfc),
                  'criteria',l.criteria,'confidence',l.confidence,'explanation',l.explanation,'reasons',r.reasons) AS entry
              FROM link l JOIN bank_movement m ON m.company_id=l.company_id AND m.id=l.movement_id
              JOIN cfdi c ON c.company_id=l.company_id AND c.id=l.cfdi_id JOIN reasons r ON r.company_id=c.company_id AND r.id=c.id
              WHERE l.company_id=@companyId AND l.state='suggested'
              UNION ALL
              SELECT 3,c.issued_at::date,c.id,jsonb_build_object('kind','invoice','id',c.id,'cfdiId',c.id,
                'invoice',jsonb_build_object('date',c.issued_at,'amount',c.total,'issuerRfc',c.issuer_rfc,'receiverRfc',c.receiver_rfc),
                'criteria','[]'::jsonb,'confidence',NULL,'explanation','Invoice requires review','reasons',r.reasons)
              FROM cfdi c JOIN reasons r ON r.company_id=c.company_id AND r.id=c.id
              WHERE c.company_id=@companyId AND r.requires_review
            ) SELECT rank,date,id,entry::text FROM queue
            WHERE @afterRank::integer IS NULL OR (rank,date,id)>(@afterRank::integer,@afterDate::date,@afterId::uuid)
            ORDER BY rank,date,id LIMIT @take
            """, new { companyId, today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), afterRank, afterDate, afterId, take = take + 1 }, transaction, cancellationToken: token))).ToArray();
        var last = rows.Length > take ? rows[take - 1] : null;
        return Results.Ok(new
        {
            entries = rows.Take(take).Select(row => JsonSerializer.Deserialize<JsonElement>(row.Entry)).ToArray(),
            nextAfterRank = last?.Rank,
            nextAfterDate = last?.Date,
            nextAfterId = last?.Id
        });
    }, cancellationToken);

    private sealed record QueueRow(int Rank, DateOnly Date, Guid Id, string Entry);
}
