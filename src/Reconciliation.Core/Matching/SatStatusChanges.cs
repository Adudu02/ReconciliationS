using System.Text.Json;
using Dapper;
using Npgsql;
using Reconciliation.Core.Tenancy;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Matching;

public static class SatStatusChanges
{
    public static async Task<int> ApplySatStatusChangeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid cfdiId, string status, string? cancellationStatus, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (status is not ("unknown" or "current" or "cancelled" or "not_found")) throw new ArgumentException("Unsupported SAT status.", nameof(status));
        var archived = await connection.QuerySingleOrDefaultAsync<bool?>(Command("SELECT archived_at IS NOT NULL FROM company FOR UPDATE",
            transaction: transaction, cancellationToken: cancellationToken));
        if (archived is null) throw new InvalidOperationException("Company not found.");
        if (archived.Value) throw new CompanyReadOnlyException();
        if (!await connection.ExecuteScalarAsync<bool>(Command("SELECT EXISTS(SELECT FROM cfdi WHERE id=@cfdiId)", new { cfdiId }, transaction, cancellationToken)))
            throw new InvalidOperationException("CFDI not found.");
        var changes = await connection.ExecuteScalarAsync<int>(Command("""
            WITH previous AS MATERIALIZED (SELECT * FROM cfdi WHERE id=@cfdiId FOR UPDATE), changed AS (
              UPDATE cfdi c SET sat_status=@status,cancellation_status=@cancellationStatus FROM previous p
              WHERE c.company_id=p.company_id AND c.id=p.id AND (p.sat_status IS DISTINCT FROM @status OR p.cancellation_status IS DISTINCT FROM @cancellationStatus)
              RETURNING c.company_id,c.uuid,to_jsonb(p) AS old,to_jsonb(c) AS new
            ), audited AS (
              SELECT append_audit_entry(company_id,'system',NULL,'cfdi.sat_status_changed',
                jsonb_build_array(jsonb_build_object('kind','cfdi','id',uuid::text)),
                jsonb_build_object('source','sat_status_change','changedAt',@now,'old',old,'new',new)) FROM changed
            ) SELECT count(*)::integer FROM audited
            """, new { cfdiId, status, cancellationStatus, now }, transaction, cancellationToken));
        if (status == "cancelled")
            changes += await connection.ExecuteScalarAsync<int>(Command("""
                WITH previous AS MATERIALIZED (
                  SELECT l.*,invoice.uuid AS invoice_uuid,
                    CASE WHEN l.cfdi_id=@cfdiId THEN 'cancelled invoice' ELSE 'cancelled payment complement' END AS reason
                  FROM link l JOIN cfdi invoice ON invoice.company_id=l.company_id AND invoice.id=l.cfdi_id
                  WHERE l.state='reconciled' AND (l.cfdi_id=@cfdiId OR l.payment_cfdi_id=@cfdiId) FOR UPDATE OF l
                ), changed AS (
                  UPDATE link l SET state='suggested',review_reason=p.reason FROM previous p
                  WHERE l.company_id=p.company_id AND l.id=p.id
                  RETURNING l.company_id,l.id,l.movement_id,p.invoice_uuid,to_jsonb(p) AS old,to_jsonb(l) AS new
                ), audited AS (
                  SELECT append_audit_entry(company_id,'system',NULL,'link.review_required',jsonb_build_array(
                    jsonb_build_object('kind','cfdi','id',invoice_uuid::text),jsonb_build_object('kind','link','id',id::text),
                    jsonb_build_object('kind','movement','id',movement_id::text)),
                    jsonb_build_object('source','sat_status_change','changedAt',@now,'old',old,'new',new)) FROM changed ORDER BY id
                ) SELECT count(*)::integer FROM audited
                """, new { cfdiId, now }, transaction, cancellationToken));
        return changes + await DiscoverReplacementsAsync(connection, transaction, now, cancellationToken);
    }

    internal static async Task<int> DiscoverReplacementsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        DateTimeOffset now, CancellationToken cancellationToken, Guid? jobId = null)
    {
        // Only direct relation postings are expanded; capacity totals and decisions are loaded once.
        var candidates = (await connection.QueryAsync<ReplacementCandidate>(Command("""
            WITH reserved AS (
              SELECT * FROM link WHERE state IN ('suggested','reconciled')
                AND coalesce(review_reason,'') NOT IN ('cancelled invoice','cancelled payment complement')
            ), invoice_used AS (SELECT company_id,cfdi_id,sum(amount_applied) AS amount FROM reserved GROUP BY company_id,cfdi_id),
            movement_used AS (SELECT company_id,movement_id,sum(amount_applied) AS amount FROM reserved GROUP BY company_id,movement_id)
            SELECT DISTINCT old.company_id AS CompanyId,m.id AS MovementId,replacement.id AS InvoiceId,replacement.uuid AS InvoiceUuid,
              m.movement_date AS MovementDate,replacement.issued_at::date AS InvoiceDate,
              greatest(m.credit-coalesce(mu.amount,0),0) AS MovementFree,greatest(p.payable-coalesce(iu.amount,0),0) AS InvoiceFree,
              prior.id AS PreviousId,to_jsonb(prior)::text AS Previous,held.id AS CancelledLinkId
            FROM link held JOIN cfdi old ON old.company_id=held.company_id AND old.id=held.cfdi_id
            JOIN cfdi_relation r ON r.company_id=old.company_id AND r.related_uuid=old.uuid AND r.relation_code='04'
            JOIN cfdi replacement ON replacement.company_id=r.company_id AND replacement.id=r.cfdi_id
            JOIN cfdi_payable() p ON p.company_id=replacement.company_id AND p.id=replacement.id
            JOIN bank_movement m ON m.company_id=held.company_id AND m.id=held.movement_id
            LEFT JOIN invoice_used iu ON iu.company_id=replacement.company_id AND iu.cfdi_id=replacement.id
            LEFT JOIN movement_used mu ON mu.company_id=m.company_id AND mu.movement_id=m.id
            LEFT JOIN link prior ON prior.company_id=m.company_id AND prior.movement_id=m.id AND prior.cfdi_id=replacement.id
              AND prior.payment_cfdi_id IS NULL AND prior.state IN ('rejected','closed') AND prior.reopened_at IS NOT NULL
            WHERE old.sat_status='cancelled' AND held.state='suggested' AND held.review_reason IN ('cancelled invoice','cancelled payment complement')
              AND replacement.type='I' AND replacement.direction='issued' AND replacement.sat_status<>'cancelled'
              AND NOT replacement.is_global AND upper(replacement.receiver_rfc)<>'XAXX010101000'
              AND replacement.currency=old.currency AND replacement.receiver_rfc=old.receiver_rfc AND p.payable>0
              AND m.credit>0 AND m.debit=0 AND m.classification IS NULL
              AND NOT EXISTS(SELECT FROM reserved active WHERE active.company_id=m.company_id AND active.movement_id=m.id)
              AND NOT EXISTS(SELECT FROM link decision WHERE decision.company_id=m.company_id AND decision.movement_id=m.id
                AND decision.cfdi_id=replacement.id AND decision.reopened_at IS NULL)
            """, transaction: transaction, cancellationToken: cancellationToken))).ToArray();
        var movementFree = candidates.GroupBy(row => row.MovementId).ToDictionary(group => group.Key, group => group.First().MovementFree);
        var invoiceFree = candidates.GroupBy(row => row.InvoiceId).ToDictionary(group => group.Key, group => group.First().InvoiceFree);
        var selected = new HashSet<Guid>();
        var rows = new List<(ReplacementCandidate Candidate, Guid Id, decimal Amount)>();
        foreach (var candidate in candidates.OrderBy(row => row.MovementDate).ThenBy(row => row.MovementId)
            .ThenBy(row => Math.Abs(row.InvoiceDate.DayNumber - row.MovementDate.DayNumber)).ThenBy(row => row.InvoiceId).ThenBy(row => row.CancelledLinkId))
        {
            var amount = Math.Min(movementFree[candidate.MovementId], invoiceFree[candidate.InvoiceId]);
            if (selected.Contains(candidate.MovementId) || amount <= 0) continue;
            selected.Add(candidate.MovementId);
            rows.Add((candidate, candidate.PreviousId ?? Guid.NewGuid(), amount));
            movementFree[candidate.MovementId] -= amount;
            invoiceFree[candidate.InvoiceId] -= amount;
        }
        var criteria = JsonSerializer.Serialize(new[] { new MatchingCriterion("replacement-of-cancelled-invoice", 30, "replacement of cancelled invoice") }, JsonSerializerOptions.Web);
        foreach (var batch in rows.Chunk(500))
            await connection.ExecuteAsync(Command("""
                WITH input AS (
                  SELECT * FROM unnest(@companies::uuid[],@ids::uuid[],@movements::uuid[],@invoices::uuid[],@uuids::uuid[],@amounts::numeric[],@previous::text[],@cancelled::uuid[])
                    AS u(company,id,movement,invoice,uuid,amount,previous,cancelled)
                ), changed AS (
                  INSERT INTO link(company_id,id,movement_id,cfdi_id,amount_applied,state,origin,criteria,confidence,explanation,created_at)
                  SELECT company,id,movement,invoice,amount,'suggested','automatic',@criteria::jsonb,'low','replacement of cancelled invoice',@now FROM input
                  ON CONFLICT(company_id,id) DO UPDATE SET state='suggested',origin='automatic',amount_applied=excluded.amount_applied,
                    criteria=excluded.criteria,confidence=excluded.confidence,explanation=excluded.explanation,review_reason=NULL,
                    decided_by=NULL,decided_at=NULL,reopened_at=NULL
                  WHERE link.state IN ('rejected','closed') AND link.reopened_at IS NOT NULL RETURNING *
                )
                SELECT append_audit_entry(c.company_id,'system',NULL,'link.created',jsonb_build_array(
                    jsonb_build_object('kind','cfdi','id',i.uuid::text),jsonb_build_object('kind','movement','id',c.movement_id::text),
                    jsonb_build_object('kind','link','id',c.id::text)),
                  jsonb_build_object('source','cancelled_replacement','jobId',@jobId::uuid,'cancelledLinkId',i.cancelled,
                    'old',i.previous::jsonb,'new',to_jsonb(c)))
                FROM changed c JOIN input i ON i.company=c.company_id AND i.id=c.id ORDER BY c.movement_id,c.cfdi_id,c.id
                """, new
            {
                companies = batch.Select(row => row.Candidate.CompanyId).ToArray(),
                ids = batch.Select(row => row.Id).ToArray(),
                movements = batch.Select(row => row.Candidate.MovementId).ToArray(),
                invoices = batch.Select(row => row.Candidate.InvoiceId).ToArray(),
                uuids = batch.Select(row => row.Candidate.InvoiceUuid).ToArray(),
                amounts = batch.Select(row => row.Amount).ToArray(),
                previous = batch.Select(row => row.Candidate.Previous).ToArray(),
                cancelled = batch.Select(row => row.Candidate.CancelledLinkId).ToArray(),
                criteria,
                now,
                jobId
            }, transaction, cancellationToken));
        return rows.Count;
    }

    private sealed record ReplacementCandidate(Guid CompanyId, Guid MovementId, Guid InvoiceId, Guid InvoiceUuid,
        DateOnly MovementDate, DateOnly InvoiceDate, decimal MovementFree, decimal InvoiceFree, Guid? PreviousId, string? Previous, Guid CancelledLinkId);
}
