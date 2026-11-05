using System.Text.Json;
using Dapper;
using Npgsql;
using Reconciliation.Core.Audit;
using Reconciliation.Core.Jobs;
using Reconciliation.Core.Tenancy;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Matching;

public sealed class ReconciliationRunHandler(CompanyScope scope, TimeProvider clock) : IJobHandler
{
    public string Type => "reconciliation_run";

    public async Task<string?> HandleAsync(JobRecord job, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var settings = await connection.QuerySingleAsync<SettingsRow>(Command("""
            SELECT archived_at IS NOT NULL AS Archived,date_tolerance_days AS DateToleranceDays,amount_tolerance AS AmountTolerance
            FROM company WHERE id=@companyId FOR UPDATE
            """, new { companyId = job.CompanyId }, transaction, cancellationToken: cancellationToken));
        if (settings.Archived) throw new CompanyReadOnlyException();
        MatchingMovement[] movements;
        InvoiceRow[] invoiceRows;
        PaymentRow[] paymentRows;
        DocumentRow[] documents;
        LinkRow[] linkRows;
        using (var data = await connection.QueryMultipleAsync(Command("""
            SELECT id,movement_date AS Date,credit,debit,description,reference,classification FROM bank_movement;
            SELECT c.id,c.uuid,c.issued_at::date AS Date,c.total,coalesce(retained_tax,0)+coalesce(local_retained_tax,0) AS Retained,
              issuer_rfc AS IssuerRfc,receiver_rfc AS ReceiverRfc,type,direction,currency,serie,folio,
              coalesce(retained_tax,0) AS FederalRetained,coalesce(local_retained_tax,0) AS LocalRetained,p.credit_notes AS CreditNotes
              FROM cfdi c JOIN cfdi_payable() p ON p.company_id=c.company_id AND p.id=c.id WHERE c.type='I' AND c.direction='issued';
            SELECT p.id,p.cfdi_id AS CfdiId,p.paid_at::date AS Date,p.amount,p.operation_number AS OperationNumber,c.direction,p.currency
              FROM cfdi_payment p JOIN cfdi c ON c.company_id=p.company_id AND c.id=p.cfdi_id WHERE c.type='P';
            SELECT payment_id AS PaymentId,invoice_uuid AS InvoiceUuid,paid,currency,inconsistent_balance AS Inconsistent FROM cfdi_payment_doc;
            SELECT id,movement_id AS MovementId,cfdi_id AS InvoiceId,payment_cfdi_id AS PaymentCfdiId,payment_ref AS PaymentRef,
              amount_applied AS Amount,state,origin,criteria::text AS Criteria,confidence,explanation,reopened_at IS NOT NULL AS Reopened,created_at AS CreatedAt FROM link;
            """, transaction: transaction, cancellationToken: cancellationToken)))
        {
            movements = (await data.ReadAsync<MatchingMovement>()).ToArray();
            invoiceRows = (await data.ReadAsync<InvoiceRow>()).ToArray();
            paymentRows = (await data.ReadAsync<PaymentRow>()).ToArray();
            documents = (await data.ReadAsync<DocumentRow>()).ToArray();
            linkRows = (await data.ReadAsync<LinkRow>()).ToArray();
        }
        var invoices = invoiceRows.Select(row => row.Invoice).ToArray();
        var invoicesById = invoiceRows.ToDictionary(row => row.Id);
        var lease = new JobLease(job, clock);
        await ReportProgressAsync(0);
        var paymentDocuments = documents.GroupBy(doc => doc.PaymentId).ToDictionary(group => group.Key,
            group => group.Select(doc => new MatchingPaymentDocument(doc.InvoiceUuid, doc.Paid, doc.Currency, doc.Inconsistent)).ToArray());
        var payments = paymentRows.Select(payment => new MatchingPayment(payment.Id, payment.CfdiId, payment.Date, payment.Amount,
            payment.OperationNumber, paymentDocuments.GetValueOrDefault(payment.Id) ?? [], payment.Direction, payment.Currency)).ToArray();
        var existing = linkRows.Select(link => new MatchingLink(link.MovementId, link.InvoiceId, link.PaymentCfdiId, link.PaymentRef,
            link.Amount, link.State, link.Origin, JsonSerializer.Deserialize<MatchingCriterion[]>(link.Criteria, JsonSerializerOptions.Web)!,
            link.Confidence, link.Explanation, link.Reopened, link.Id, new DateTimeOffset(link.CreatedAt))).ToArray();
        var result = MatchingEngine.Run(movements, invoices, payments, existing, new(settings.DateToleranceDays, settings.AmountTolerance));
        var reopened = existing.Where(link => link.Reopened && link.State is "rejected" or "closed")
            .ToDictionary(link => (link.MovementId, link.InvoiceId, link.PaymentCfdiId));
        var created = result.Changes.Select(ExplainRetentions).Select(link => reopened.TryGetValue((link.MovementId, link.InvoiceId, link.PaymentCfdiId), out var previous)
            ? link with { Id = previous.Id, CreatedAt = previous.CreatedAt }
            : link with { Id = Guid.NewGuid(), CreatedAt = clock.GetUtcNow() }).ToArray();
        var writes = result.Updates.Select(update => update.After).Concat(created).ToArray();
        foreach (var batch in writes.Chunk(500)) await PersistAsync(batch);
        foreach (var update in result.Updates) await AuditAsync(update.After, update.Action, update.Before);
        foreach (var link in created) await AuditAsync(link, "link.created", null);
        await ReportProgressAsync(movements.Length);
        // The final fence rolls back stale work; progress/heartbeats use short separate transactions until here.
        await lease.CheckAsync(connection, transaction, cancellationToken);
        return $"Created {created.Length} links; upgraded {result.Updates.Count(update => update.Action == "link.upgraded")}; closed {result.Updates.Count(update => update.Action == "link.closed")}.";

        Task<bool> ReportProgressAsync(int done) => scope.ExecuteAsync(job.CompanyId, async (progressConnection, progressTransaction, token) =>
        {
            await lease.ProgressAsync(progressConnection, progressTransaction, done, movements.Length, token);
            return true;
        }, cancellationToken);

        Task<int> AuditAsync(MatchingLink link, string action, MatchingLink? previous) => AuditLog.WriteSystemAsync(connection, transaction,
            job.CompanyId, job.Id, Type, action, previous, link,
            [new("movement", link.MovementId.ToString()), new("cfdi", invoicesById[link.InvoiceId].Uuid.ToString()), new("link", link.Id!.Value.ToString())], cancellationToken);

        MatchingLink ExplainRetentions(MatchingLink link)
        {
            const string grossReason = "amount equals total before retentions";
            if (!link.Criteria.Any(criterion => criterion.Detail.Contains(grossReason, StringComparison.Ordinal))) return link;
            var invoice = invoicesById[link.InvoiceId];
            var components = FormattableString.Invariant($"retentions: federal {invoice.FederalRetained:0.00}, local {invoice.LocalRetained:0.00}");
            // Provenance belongs to the SQL boundary; the pure engine keeps one exact retained total.
            return link with
            {
                Criteria = link.Criteria.Select(criterion => criterion.Detail.Contains(grossReason, StringComparison.Ordinal)
                    ? criterion with { Detail = criterion.Detail + "; " + components } : criterion).ToArray(),
                Explanation = link.Explanation + "; " + components
            };
        }

        async Task PersistAsync(MatchingLink[] rows)
        {
            var affected = await connection.ExecuteAsync(Command("""
                INSERT INTO link(company_id,id,movement_id,cfdi_id,payment_cfdi_id,payment_ref,amount_applied,state,origin,criteria,confidence,explanation,created_at)
                SELECT @companyId,u.id,u.movement,u.invoice,u.payment_cfdi,u.payment,u.amount,u.state,u.origin,u.criteria::jsonb,u.confidence,u.explanation,u.created_at
                FROM unnest(@ids::uuid[],@movements::uuid[],@invoices::uuid[],@paymentCfdis::uuid[],@payments::uuid[],@amounts::numeric[],
                  @states::text[],@origins::text[],@criteria::text[],@confidences::text[],@explanations::text[],@createdAt::timestamptz[])
                  AS u(id,movement,invoice,payment_cfdi,payment,amount,state,origin,criteria,confidence,explanation,created_at)
                ON CONFLICT(company_id,id) DO UPDATE SET payment_cfdi_id=EXCLUDED.payment_cfdi_id,payment_ref=EXCLUDED.payment_ref,
                  amount_applied=EXCLUDED.amount_applied,state=EXCLUDED.state,origin=EXCLUDED.origin,criteria=EXCLUDED.criteria,
                  confidence=EXCLUDED.confidence,explanation=EXCLUDED.explanation,decided_by=NULL,decided_at=NULL,reopened_at=NULL
                WHERE (link.origin='automatic' AND link.state='suggested') OR (link.state IN ('rejected','closed') AND link.reopened_at IS NOT NULL)
                """, new
            {
                companyId = job.CompanyId,
                ids = rows.Select(link => link.Id!.Value).ToArray(),
                movements = rows.Select(link => link.MovementId).ToArray(),
                invoices = rows.Select(link => link.InvoiceId).ToArray(),
                paymentCfdis = rows.Select(link => link.PaymentCfdiId).ToArray(),
                payments = rows.Select(link => link.PaymentRef).ToArray(),
                amounts = rows.Select(link => link.Amount).ToArray(),
                states = rows.Select(link => link.State).ToArray(),
                origins = rows.Select(link => link.Origin).ToArray(),
                criteria = rows.Select(link => JsonSerializer.Serialize(link.Criteria, JsonSerializerOptions.Web)).ToArray(),
                confidences = rows.Select(link => link.Confidence).ToArray(),
                explanations = rows.Select(link => link.Explanation).ToArray(),
                createdAt = rows.Select(link => link.CreatedAt!.Value).ToArray()
            }, transaction, cancellationToken: cancellationToken));
            if (affected != rows.Length) throw new InvalidDataException("Matching decision changed before it could be saved.");
        }
    }

    private sealed record SettingsRow(bool Archived, int DateToleranceDays, decimal AmountTolerance);
    private sealed record InvoiceRow(Guid Id, Guid Uuid, DateOnly Date, decimal Total, decimal Retained, string IssuerRfc, string ReceiverRfc,
        string Type, string Direction, string Currency, string? Serie, string? Folio, decimal FederalRetained, decimal LocalRetained, decimal CreditNotes)
    {
        public MatchingInvoice Invoice => new(Id, Uuid, Date, Total, Retained, IssuerRfc, ReceiverRfc, Type, Direction, Currency, Serie, Folio, CreditNotes);
    }
    private sealed record PaymentRow(Guid Id, Guid CfdiId, DateOnly Date, decimal Amount, string? OperationNumber, string Direction, string Currency);
    private sealed record DocumentRow(Guid PaymentId, Guid InvoiceUuid, decimal Paid, string Currency, bool Inconsistent);
    private sealed record LinkRow(Guid Id, Guid MovementId, Guid InvoiceId, Guid? PaymentCfdiId, Guid? PaymentRef, decimal Amount,
        string State, string Origin, string Criteria, string? Confidence, string Explanation, bool Reopened, DateTime CreatedAt);
}
