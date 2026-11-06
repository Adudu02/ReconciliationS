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
        if (request.Type is not ("confirm" or "reject") || request.Note?.Length > 500 || request.PaymentCfdiId.HasValue != request.PaymentRef.HasValue ||
            request.NewCfdiId.HasValue || request.AmountApplied.HasValue || request.PaymentCfdiId.HasValue)
            return Results.BadRequest();
        var link = await connection.QuerySingleOrDefaultAsync<DecisionLink>(Command("""
            SELECT id,movement_id AS MovementId,cfdi_id AS CfdiId,amount_applied AS Amount,state,origin,review_reason AS ReviewReason
            FROM link WHERE company_id=@companyId AND id=@linkId FOR UPDATE
            """, new { companyId, linkId }, transaction, cancellationToken: token));
        if (link is null) return Results.NotFound();
        if (link.State != "suggested" || (link.Origin == "user" && link.ReviewReason is null)) return Results.Conflict(new { state = link.State });
        if (request.Type == "confirm")
        {
            if (link.ReviewReason is not null) return Results.Conflict(new { reason = link.ReviewReason });
            await connection.ExecuteAsync(Command("SELECT id FROM bank_movement WHERE id=@MovementId FOR UPDATE; SELECT id FROM cfdi WHERE id=@CfdiId FOR UPDATE", link, transaction, cancellationToken: token));
            var capacity = await connection.ExecuteScalarAsync<bool>(Command("""
                SELECT m.credit+m.debit >= @Amount+coalesce((SELECT sum(amount_applied) FROM link WHERE movement_id=m.id AND state='reconciled'),0)
                  AND p.payable >= @Amount+coalesce((SELECT sum(amount_applied) FROM link WHERE cfdi_id=c.id AND state='reconciled'),0)
                  AND c.sat_status<>'cancelled'
                FROM bank_movement m,cfdi c JOIN cfdi_payable() p ON p.company_id=c.company_id AND p.id=c.id
                WHERE m.id=@MovementId AND c.id=@CfdiId
                """, link, transaction, cancellationToken: token));
            if (!capacity) return Results.Conflict(new { reason = "Capacity exceeded or invoice cancelled" });
        }
        var actor = context.User.UserId()!.Value;
        var state = request.Type == "confirm" ? "reconciled" : "rejected";
        await connection.ExecuteAsync(Command("""
            UPDATE link SET state=@state,origin=CASE WHEN @state='rejected' THEN 'user' ELSE origin END,decided_by=@actor,decided_at=clock_timestamp() WHERE id=@linkId;
            """, new { state, actor, linkId }, transaction, cancellationToken: token));
        var detail = JsonSerializer.Serialize(new { request.Note }, JsonSerializerOptions.Web);
        var decision = await connection.QuerySingleAsync<DecisionRow>(Command("""
            INSERT INTO link_decision(company_id,link_id,type,decided_by,detail) VALUES(@companyId,@linkId,@Type,@actor,@detail::jsonb)
            RETURNING id,type,decided_by AS DecidedBy,decided_at AS DecidedAt
            """, new { companyId, linkId, request.Type, actor, detail }, transaction, cancellationToken: token));
        var uuid = await connection.ExecuteScalarAsync<Guid>(Command("SELECT uuid FROM cfdi WHERE id=@CfdiId", link, transaction, cancellationToken: token));
        await AuditLog.WriteAsync(connection, transaction, companyId, actor, "link." + (request.Type == "confirm" ? "confirmed" : "rejected"),
            new { link.State }, new { state, decision.Id }, token, [new("link", linkId.ToString()), new("movement", link.MovementId.ToString()), new("cfdi", uuid.ToString())]);
        var states = await connection.QuerySingleAsync<DecisionStates>(Command("""
            SELECT m.state AS MovementState,c.state AS InvoiceState FROM movement_state() m,cfdi_state() c WHERE m.id=@MovementId AND c.id=@CfdiId
            """, link, transaction, cancellationToken: token));
        return Results.Ok(new { decision, links = new[] { new { link.Id, state, link.Amount, states.MovementState, states.InvoiceState } } });
    }, cancellationToken);

    private sealed record DecisionLink(Guid Id, Guid MovementId, Guid CfdiId, decimal Amount, string State, string Origin, string? ReviewReason);
    private sealed record DecisionRow(Guid Id, string Type, Guid DecidedBy, DateTime DecidedAt);
    private sealed record DecisionStates(string MovementState, string InvoiceState);
}
