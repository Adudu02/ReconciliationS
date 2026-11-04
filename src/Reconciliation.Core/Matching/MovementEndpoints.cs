using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Reconciliation.Core.Audit;
using Reconciliation.Core.Tenancy;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Matching;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ClassificationChange(JsonElement Classification, string? Note);

public static class MovementEndpoints
{
    public static IEndpointRouteBuilder MapMovements(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/companies/{companyId:guid}/movements", ReadAsync).RequireAuthorization(Policies.CompanyMember);
        routes.MapPatch("/companies/{companyId:guid}/movements/{movementId:guid}", ClassifyAsync).RequireAuthorization(Policies.Accountant);
        return routes;
    }

    private static bool IsClassification(string? value) => value is "loan" or "partner_contribution" or "tax_payment" or "late_invoicing" or "other";

    private static Task<IResult> ClassifyAsync(Guid companyId, Guid movementId, ClassificationChange change, HttpContext context,
        CompanyScope scope, CancellationToken cancellationToken) => scope.ExecuteWriteResultAsync(companyId, async (connection, transaction, token) =>
    {
        if (change.Classification.ValueKind is not (JsonValueKind.Null or JsonValueKind.String) ||
            (change.Classification.ValueKind == JsonValueKind.String && !IsClassification(change.Classification.GetString())) || change.Note?.Length > 2000)
            return Results.BadRequest(new { error = "Classification must be loan, partner_contribution, tax_payment, late_invoicing, other or explicit null; note is limited to 2000 characters." });
        var classification = change.Classification.ValueKind == JsonValueKind.Null ? null : change.Classification.GetString();
        var note = classification is null || string.IsNullOrWhiteSpace(change.Note) ? null : change.Note.Trim();
        var before = await connection.QuerySingleOrDefaultAsync<ClassificationRow>(Command("""
            SELECT classification,classification_note AS Note FROM bank_movement WHERE id=@movementId FOR UPDATE
            """, new { movementId }, transaction, cancellationToken: token));
        if (before is null) return Results.NotFound();
        var after = new ClassificationRow(classification, note);
        if (before != after)
        {
            await connection.ExecuteAsync(Command("UPDATE bank_movement SET classification=@Classification,classification_note=@Note WHERE id=@movementId",
                new { after.Classification, after.Note, movementId }, transaction, cancellationToken: token));
            await AuditLog.WriteAsync(connection, transaction, companyId, context.User.UserId()!.Value, "movement.classification_changed", before, after, token,
                [new("movement", movementId.ToString())]);
        }
        return Results.NoContent();
    }, cancellationToken);

    private static Task<IResult> ReadAsync(Guid companyId, string? state, string? classification, DateOnly? afterDate, Guid? afterId, int? limit,
        CompanyScope scope, CancellationToken cancellationToken) => scope.ExecuteResultAsync(companyId, async (connection, transaction, token) =>
    {
        var pageSize = limit ?? 50;
        if (pageSize is < 1 or > 200 || (state is not null && state is not ("pending" or "suggested" or "reconciled")) ||
            (classification is not null && !IsClassification(classification)) || afterDate.HasValue != afterId.HasValue)
            return Results.BadRequest(new { error = "Invalid movement filters. Limit must be 1–200; state pending/suggested/reconciled; supported classification; afterDate and afterId must be supplied together." });
        var rows = (await connection.QueryAsync<MovementRow>(Command("""
            SELECT m.id,m.movement_date AS Date,m.description,m.credit,m.debit,m.reference,s.state,m.classification,m.classification_note AS Note,
              coalesce(m.classification_note,m.classification,reasons.reason,'No active link') AS Reason,
              s.reconciled_amount AS ReconciledAmount,s.suggested_amount AS SuggestedAmount,s.pending_amount AS PendingAmount,s.uncovered_amount AS UncoveredAmount
            FROM bank_movement m JOIN movement_state() s ON s.company_id=m.company_id AND s.id=m.id
            LEFT JOIN (
              SELECT company_id,movement_id,string_agg(DISTINCT explanation,'; ' ORDER BY explanation) AS reason
              FROM link WHERE state IN ('reconciled','suggested') GROUP BY company_id,movement_id
            ) reasons ON reasons.company_id=m.company_id AND reasons.movement_id=m.id
            WHERE (@state::text IS NULL OR s.state=@state) AND (@state::text IS DISTINCT FROM 'pending' OR m.classification IS NULL)
              AND (@classification::text IS NULL OR m.classification=@classification)
              AND (@afterDate::date IS NULL OR (m.movement_date,m.id)>(@afterDate::date,@afterId::uuid))
            ORDER BY m.movement_date,m.id LIMIT @take
            """, new { state, classification, afterDate, afterId, take = pageSize + 1 }, transaction, cancellationToken: token))).ToArray();
        return Results.Ok(new
        {
            movements = rows.Take(pageSize).ToArray(),
            nextAfterDate = rows.Length > pageSize ? rows[pageSize - 1].Date : (DateOnly?)null,
            nextAfterId = rows.Length > pageSize ? rows[pageSize - 1].Id : (Guid?)null
        });
    }, cancellationToken);

    private sealed record ClassificationRow(string? Classification, string? Note);
    private sealed record MovementRow(Guid Id, DateOnly Date, string Description, decimal Credit, decimal Debit, string? Reference,
        string State, string? Classification, string? Note, string Reason, decimal ReconciledAmount, decimal SuggestedAmount, decimal PendingAmount, decimal UncoveredAmount);
}
