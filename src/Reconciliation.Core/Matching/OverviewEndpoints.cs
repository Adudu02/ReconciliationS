using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Reconciliation.Core.Tenancy;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Matching;

public static class OverviewEndpoints
{
    public static IEndpointRouteBuilder MapOverview(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/companies/{companyId:guid}/overview", ReadAsync).RequireAuthorization(Policies.CompanyMember);
        return routes;
    }

    private static Task<IResult> ReadAsync(Guid companyId, CompanyScope scope, TimeProvider clock, CancellationToken cancellationToken) =>
        scope.ExecuteResultAsync(companyId, async (connection, transaction, token) =>
        {
            var invoices = (await connection.QueryAsync<InvoiceStateRow>(Command("""
                SELECT s.state,count(*) AS Count,coalesce(sum(c.total),0) AS Amount,
                  count(*) FILTER(WHERE r.complement_overdue) AS ComplementOverdue,
                  count(*) FILTER(WHERE c.cancellation_status='in process') AS CancellationInProcess,
                  count(*) FILTER(WHERE r.reasons @> '[{"code":"efos warning","requiresReview":true}]') AS EfosWarning,
                  count(*) FILTER(WHERE c.is_global OR upper(c.receiver_rfc)='XAXX010101000') AS Global
                FROM cfdi c JOIN cfdi_state() s ON s.company_id=c.company_id AND s.id=c.id
                JOIN cfdi_review_reasons(@today) r ON r.company_id=c.company_id AND r.id=c.id
                WHERE c.company_id=@companyId AND c.type='I' GROUP BY GROUPING SETS((s.state),())
                """, new { companyId, today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime) }, transaction, cancellationToken: token))).ToArray();
            var movements = (await connection.QueryAsync<MovementStateRow>(Command("""
                SELECT s.state,count(*) AS Count,coalesce(sum(m.credit+m.debit),0) AS Amount
                FROM bank_movement m JOIN movement_state() s ON s.company_id=m.company_id AND s.id=m.id
                WHERE m.company_id=@companyId GROUP BY GROUPING SETS((s.state),())
                """, new { companyId }, transaction, cancellationToken: token))).ToArray();
            var invoiceTotal = invoices.Single(row => row.State is null);
            var movementTotal = movements.Single(row => row.State is null);
            return Results.Ok(new
            {
                invoices = new { invoiceTotal.Count, invoiceTotal.Amount, states = invoices.Where(row => row.State is not null).Select(row => new { row.State, row.Count, row.Amount }).OrderBy(row => row.State, StringComparer.Ordinal) },
                movements = new { movementTotal.Count, movementTotal.Amount, states = movements.Where(row => row.State is not null).OrderBy(row => row.State, StringComparer.Ordinal) },
                alerts = new { invoiceTotal.ComplementOverdue, invoiceTotal.CancellationInProcess, invoiceTotal.EfosWarning, invoiceTotal.Global },
                disclaimer = "Demo: results have no fiscal or legal validity",
                totalsNote = "Classified movements and zero-payable invoices remain in their derived states and are included in totals. Invoice amounts use imported type I gross totals; movement amounts use credit plus debit. Pending lists exclude classified movements and zero pending amounts."
            });
        }, cancellationToken);

    private sealed record InvoiceStateRow(string? State, long Count, decimal Amount, long ComplementOverdue, long CancellationInProcess, long EfosWarning, long Global);
    private sealed record MovementStateRow(string? State, long Count, decimal Amount);
}
