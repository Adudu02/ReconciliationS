using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Reconciliation.Core.Audit;
using Reconciliation.Core.Tenancy;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Matching;

public sealed record LinkDecisionRequest(string Type, Guid? NewCfdiId, decimal? AmountApplied, Guid? PaymentCfdiId, Guid? PaymentRef, string? Note);

public static class DecisionEndpoints
{
    public static IEndpointRouteBuilder MapDecisions(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/companies/{companyId:guid}/links/{linkId:guid}/decisions", DecideAsync).RequireAuthorization(Policies.Accountant);
        return routes;
    }

    private static Task<IResult> DecideAsync(Guid companyId, Guid linkId, LinkDecisionRequest request, HttpContext context,
        CompanyScope scope, CancellationToken cancellationToken) => scope.ExecuteWriteResultAsync(companyId, async (connection, transaction, token) =>
    {
        if (request.Type is not ("confirm" or "reject" or "change") || request.Note?.Length > 500 || request.PaymentCfdiId.HasValue != request.PaymentRef.HasValue ||
            (request.Type != "change" && (request.NewCfdiId.HasValue || request.AmountApplied.HasValue)) ||
            (request.Type == "reject" && request.PaymentRef.HasValue) || request.AmountApplied is <= 0 || (request.AmountApplied.HasValue && decimal.Round(request.AmountApplied.Value, 2) != request.AmountApplied))
            return Results.BadRequest();
        var link = await connection.QuerySingleOrDefaultAsync<DecisionLink>(Command("""
            SELECT id,movement_id AS MovementId,cfdi_id AS CfdiId,amount_applied AS Amount,state,origin,review_reason AS ReviewReason
            FROM link WHERE company_id=@companyId AND id=@linkId FOR UPDATE
            """, new { companyId, linkId }, transaction, cancellationToken: token));
        if (link is null) return Results.NotFound();
        if (link.State != "suggested" || (link.Origin == "user" && link.ReviewReason is null)) return Results.Conflict(new { state = link.State });
        var target = request.NewCfdiId ?? link.CfdiId;
        var amount = request.AmountApplied ?? link.Amount;
        if (request.Type == "confirm" && link.ReviewReason is not null) return Results.Conflict(new { reason = link.ReviewReason });
        if (request.Type != "reject")
        {
            await connection.ExecuteAsync(Command("SELECT id FROM bank_movement WHERE id=@MovementId FOR UPDATE; SELECT id FROM cfdi WHERE id=@target FOR UPDATE",
                new { link.MovementId, target }, transaction, cancellationToken: token));
            var eligible = await connection.QuerySingleOrDefaultAsync<TargetInvoice>(Command("SELECT type,direction,sat_status AS SatStatus,uuid FROM cfdi WHERE id=@target",
                new { target }, transaction, cancellationToken: token));
            if (eligible is null) return Results.NotFound();
            if (eligible.Type != "I" || eligible.Direction != "issued" || eligible.SatStatus == "cancelled") return Results.Conflict(new { reason = "Target must be an issued non-cancelled invoice" });
            var capacity = await connection.ExecuteScalarAsync<bool>(Command("""
                SELECT m.credit+m.debit >= @amount+coalesce((SELECT sum(amount_applied) FROM link WHERE movement_id=m.id AND state='reconciled'),0)
                  AND (@isChange OR p.payable >= @amount+coalesce((SELECT sum(amount_applied) FROM link WHERE cfdi_id=c.id AND state='reconciled'),0))
                FROM bank_movement m,cfdi c JOIN cfdi_payable() p ON p.company_id=c.company_id AND p.id=c.id
                WHERE m.id=@MovementId AND c.id=@target
                """, new { amount, link.MovementId, target, isChange = request.Type == "change" }, transaction, cancellationToken: token));
            if (!capacity) return Results.Conflict(new { reason = "Capacity exceeded" });
            if (request.Type == "change" && await connection.ExecuteScalarAsync<bool>(Command("""
                SELECT EXISTS(SELECT FROM link WHERE movement_id=@MovementId AND cfdi_id=@target AND id<>@linkId
                  AND (state IN ('suggested','reconciled') OR (state='rejected' AND origin='user' AND reopened_at IS NULL)))
                """, new { link.MovementId, target, linkId }, transaction, cancellationToken: token)))
                return Results.Conflict(new { reason = "Target pair already active or rejected" });
            if (request.PaymentRef.HasValue)
            {
                var valid = await connection.ExecuteScalarAsync<bool>(Command("""
                    SELECT EXISTS(SELECT FROM cfdi_payment p JOIN cfdi c ON c.company_id=p.company_id AND c.id=p.cfdi_id
                      JOIN cfdi_payment_doc d ON d.company_id=p.company_id AND d.payment_id=p.id
                      WHERE p.id=@PaymentRef AND p.cfdi_id=@PaymentCfdiId AND c.type='P' AND c.sat_status<>'cancelled' AND d.invoice_uuid=@Uuid)
                    """, new { request.PaymentRef, request.PaymentCfdiId, eligible.Uuid }, transaction, cancellationToken: token));
                if (!valid) return Results.NotFound();
                if (await connection.ExecuteScalarAsync<bool>(Command("SELECT EXISTS(SELECT FROM link WHERE payment_ref=@PaymentRef AND state='reconciled' AND movement_id<>@MovementId)",
                    new { request.PaymentRef, link.MovementId }, transaction, cancellationToken: token))) return Results.Conflict(new { reason = "Payment already claimed by another movement" });
            }
        }
        var actor = context.User.UserId()!.Value;
        var state = request.Type == "confirm" ? "reconciled" : request.Type == "reject" ? "rejected" : "closed";
        await connection.ExecuteAsync(Command("""
            UPDATE link SET state=@state,origin=CASE WHEN @state IN ('rejected','closed') THEN 'user' ELSE origin END,decided_by=@actor,decided_at=clock_timestamp(),payment_cfdi_id=CASE WHEN @state='reconciled' THEN coalesce(@PaymentCfdiId,payment_cfdi_id) ELSE payment_cfdi_id END,payment_ref=CASE WHEN @state='reconciled' THEN coalesce(@PaymentRef,payment_ref) ELSE payment_ref END WHERE id=@linkId;
            """, new { state, actor, linkId, request.PaymentCfdiId, request.PaymentRef }, transaction, cancellationToken: token));
        var detail = JsonSerializer.Serialize(request, JsonSerializerOptions.Web);
        var decision = await connection.QuerySingleAsync<DecisionRow>(Command("""
            INSERT INTO link_decision(company_id,link_id,type,decided_by,detail) VALUES(@companyId,@linkId,@Type,@actor,@detail::jsonb)
            RETURNING id,type,decided_by AS DecidedBy,decided_at AS DecidedAt
            """, new { companyId, linkId, request.Type, actor, detail }, transaction, cancellationToken: token));
        var uuid = await connection.ExecuteScalarAsync<Guid>(Command("SELECT uuid FROM cfdi WHERE id=@CfdiId", link, transaction, cancellationToken: token));
        await AuditLog.WriteAsync(connection, transaction, companyId, actor, "link." + (request.Type == "confirm" ? "confirmed" : request.Type == "reject" ? "rejected" : "closed"),
            new { link.State }, new { state, decision.Id }, token, [new("link", linkId.ToString()), new("movement", link.MovementId.ToString()), new("cfdi", uuid.ToString())]);
        Guid? newLinkId = null;
        if (request.Type == "change")
        {
            newLinkId = await connection.ExecuteScalarAsync<Guid>(Command("""
                INSERT INTO link(company_id,movement_id,cfdi_id,payment_cfdi_id,payment_ref,amount_applied,state,origin,criteria,explanation,decided_by,decided_at)
                VALUES(@companyId,@MovementId,@target,@PaymentCfdiId,@PaymentRef,@amount,'reconciled','user','[]','User changed suggestion',@actor,clock_timestamp()) RETURNING id
                """, new { companyId, link.MovementId, target, request.PaymentCfdiId, request.PaymentRef, amount, actor }, transaction, cancellationToken: token));
            var targetUuid = await connection.ExecuteScalarAsync<Guid>(Command("SELECT uuid FROM cfdi WHERE id=@target", new { target }, transaction, cancellationToken: token));
            await AuditLog.WriteAsync(connection, transaction, companyId, actor, "link.created", null, new { id = newLinkId, state = "reconciled", amount }, token,
                [new("link", newLinkId.ToString()!), new("movement", link.MovementId.ToString()), new("cfdi", targetUuid.ToString())]);
            await connection.ExecuteAsync(Command("""
                UPDATE link SET state='closed',decided_by=@actor,decided_at=clock_timestamp()
                WHERE movement_id=@MovementId AND state='suggested' AND review_reason IN ('cancelled invoice','cancelled payment complement') AND id<>@linkId
                """, new { actor, link.MovementId, linkId }, transaction, cancellationToken: token));
        }
        var affected = await connection.ExecuteScalarAsync<string>(Command("""
            SELECT coalesce(jsonb_agg(jsonb_build_object('id',l.id,'state',l.state,'amountApplied',l.amount_applied,'cfdiId',l.cfdi_id,
              'movementState',m.state,'invoiceState',c.state)),'[]')::text FROM link l
            JOIN movement_state() m ON m.company_id=l.company_id AND m.id=l.movement_id
            JOIN cfdi_state() c ON c.company_id=l.company_id AND c.id=l.cfdi_id WHERE l.id=@linkId OR l.id=@newLinkId
            """, new { linkId, newLinkId }, transaction, cancellationToken: token));
        return Results.Ok(new { decision, links = JsonSerializer.Deserialize<JsonElement>(affected!) });
    }, cancellationToken);

    private sealed record DecisionLink(Guid Id, Guid MovementId, Guid CfdiId, decimal Amount, string State, string Origin, string? ReviewReason);
    private sealed record DecisionRow(Guid Id, string Type, Guid DecidedBy, DateTime DecidedAt);
    private sealed record TargetInvoice(string Type, string Direction, string SatStatus, Guid Uuid);
}
