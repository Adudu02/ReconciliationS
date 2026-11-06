using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Reconciliation.Core.Tenancy;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Matching;

public static class SummaryEndpoints
{
    public static IEndpointRouteBuilder MapSummaries(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/companies/{companyId:guid}/cfdi/{invoiceId:guid}/summary", SummaryAsync).RequireAuthorization(Policies.CompanyMember);
        routes.MapGet("/companies/{companyId:guid}/cfdi", ListAsync).RequireAuthorization(Policies.CompanyMember);
        return routes;
    }

    private static Task<IResult> SummaryAsync(Guid companyId, Guid invoiceId, CompanyScope scope, TimeProvider clock, CancellationToken cancellationToken) =>
        scope.ExecuteResultAsync(companyId, async (connection, transaction, token) =>
        {
            var json = await connection.ExecuteScalarAsync<string>(Command("""
                WITH claims AS (
                  SELECT DISTINCT ON(company_id,payment_ref) company_id,payment_ref,movement_id FROM link
                  WHERE state='reconciled' AND payment_ref IS NOT NULL ORDER BY company_id,payment_ref,created_at,id
                ), payments AS (
                  SELECT l.*,m.movement_date,m.credit,m.debit,m.description,p.amount AS payment_amount,p.payment_form,
                    p.paid_at,CASE WHEN c.payment_method='PPD' AND l.state='reconciled' AND c.sat_status<>'cancelled' THEN
                      f.fifth+CASE WHEN company.complement_next_biz_day AND extract(isodow FROM f.fifth) IN (6,7)
                        THEN 8-extract(isodow FROM f.fifth)::integer ELSE 0 END END AS due,
                    EXISTS(SELECT FROM claims claim JOIN cfdi_payment payment ON payment.company_id=claim.company_id AND payment.id=claim.payment_ref
                      JOIN cfdi complement ON complement.company_id=payment.company_id AND complement.id=payment.cfdi_id
                      JOIN cfdi_payment_doc doc ON doc.company_id=payment.company_id AND doc.payment_id=payment.id
                      WHERE claim.company_id=l.company_id AND claim.payment_ref=l.payment_ref AND claim.movement_id=l.movement_id
                        AND payment.cfdi_id=l.payment_cfdi_id AND complement.type='P' AND complement.sat_status<>'cancelled' AND doc.invoice_uuid=c.uuid) AS covered
                  FROM link l JOIN cfdi c ON c.company_id=l.company_id AND c.id=l.cfdi_id
                  JOIN bank_movement m ON m.company_id=l.company_id AND m.id=l.movement_id JOIN company ON company.id=l.company_id
                  LEFT JOIN cfdi_payment p ON p.company_id=l.company_id AND p.id=l.payment_ref AND p.cfdi_id=l.payment_cfdi_id
                  CROSS JOIN LATERAL (SELECT (date_trunc('month',m.movement_date::timestamp)+interval '1 month 4 days')::date AS fifth) f
                  WHERE l.company_id=@companyId AND l.cfdi_id=@invoiceId AND l.state IN ('suggested','reconciled')
                ), linked AS (
                  SELECT coalesce(jsonb_agg(jsonb_build_object('id',id,'movementId',movement_id,'date',movement_date,'description',description,
                    'amount',credit+debit,'amountApplied',amount_applied,'state',state,'confidence',confidence,'paymentCfdiId',payment_cfdi_id,'paymentRef',payment_ref,
                    'complementStatus',CASE WHEN covered THEN 'received' WHEN due<@today THEN 'overdue' ELSE 'missing' END,
                    'dueDate',CASE WHEN NOT covered THEN due END,'paymentDate',paid_at,'paymentForm',payment_form,
                    'cashPaymentWarning',CASE WHEN payment_form='01' AND payment_amount>2000 THEN
                      'Cash payment above 2,000.00: results have no fiscal validity and deductibility must be checked' END) ORDER BY movement_date,id),'[]') AS links FROM payments
                )
                SELECT jsonb_build_object('id',c.id,'uuid',c.uuid,'total',c.total,'creditNotes',p.credit_notes,'netPayable',p.payable,
                  'paid',s.reconciled_amount,'paidPercentage',s.paid_percentage,'pending',s.pending_amount,'uncovered',s.uncovered_amount,
                  'state',s.state,'direction',c.direction,'paymentMethod',c.payment_method,'satStatus',c.sat_status,'cancellationStatus',c.cancellation_status,
                  'efosWarning',r.reasons @> '[{"code":"efos warning","requiresReview":true}]','reasons',r.reasons,'requiresReview',r.requires_review,'links',linked.links)::text
                FROM cfdi c JOIN cfdi_state() s ON s.company_id=c.company_id AND s.id=c.id
                JOIN cfdi_payable() p ON p.company_id=c.company_id AND p.id=c.id
                JOIN cfdi_review_reasons(@today) r ON r.company_id=c.company_id AND r.id=c.id CROSS JOIN linked
                WHERE c.company_id=@companyId AND c.id=@invoiceId
                """, new { companyId, invoiceId, today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime) }, transaction, cancellationToken: token));
            return json is null ? Results.NotFound() : Results.Ok(JsonSerializer.Deserialize<JsonElement>(json));
        }, cancellationToken);

    private static Task<IResult> ListAsync(Guid companyId, string? type, string? direction, string? state, string? complementStatus,
        DateTime? afterIssuedAt, Guid? afterId, int? limit, CompanyScope scope, TimeProvider clock, CancellationToken cancellationToken) =>
        scope.ExecuteResultAsync(companyId, async (connection, transaction, token) =>
        {
            var take = limit ?? 50;
            if (take is < 1 or > 200 || type is not (null or "I" or "E" or "P") || direction is not (null or "issued" or "received") ||
                state is not (null or "pending" or "suggested" or "reconciled") || complementStatus is not (null or "missing" or "overdue") || afterIssuedAt.HasValue != afterId.HasValue)
                return Results.BadRequest(new { error = "Invalid invoice filters, cursor or page limit (1–200)." });
            var rows = (await connection.QueryAsync<InvoiceListRow>(Command("""
                SELECT c.id,c.issued_at AS IssuedAt,jsonb_build_object('id',c.id,'uuid',c.uuid,'type',c.type,'direction',c.direction,
                  'issuedAt',c.issued_at,'total',c.total,'state',s.state,'pendingAmount',s.pending_amount,'dueDate',r.complement_due_date,
                  'complementOverdue',r.complement_overdue)::text AS entry
                FROM cfdi c LEFT JOIN cfdi_state() s ON s.company_id=c.company_id AND s.id=c.id
                LEFT JOIN cfdi_review_reasons(@today) r ON r.company_id=c.company_id AND r.id=c.id
                WHERE c.company_id=@companyId AND (@type::text IS NULL OR c.type=@type) AND (@direction::text IS NULL OR c.direction=@direction)
                  AND (@state::text IS NULL OR s.state=@state) AND (@state::text IS DISTINCT FROM 'pending' OR s.pending_amount>0)
                  AND (@complementStatus::text IS NULL OR (@complementStatus='missing' AND r.complement_due_date IS NOT NULL) OR (@complementStatus='overdue' AND r.complement_overdue))
                  AND (@afterIssuedAt::timestamp IS NULL OR (c.issued_at,c.id)>(@afterIssuedAt::timestamp,@afterId::uuid))
                ORDER BY c.issued_at,c.id LIMIT @take
                """, new { companyId, type, direction, state, complementStatus, afterIssuedAt, afterId, take = take + 1, today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime) }, transaction, cancellationToken: token))).ToArray();
            var last = rows.Length > take ? rows[take - 1] : null;
            return Results.Ok(new { invoices = rows.Take(take).Select(row => JsonSerializer.Deserialize<JsonElement>(row.Entry)).ToArray(), nextAfterIssuedAt = last?.IssuedAt, nextAfterId = last?.Id });
        }, cancellationToken);

    private sealed record InvoiceListRow(Guid Id, DateTime IssuedAt, string Entry);
}
