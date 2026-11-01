using System.Xml;
using Dapper;
using Reconciliation.Core.Tenancy;

namespace Reconciliation.Core.Ingestion;

public sealed class CfdiImporter(TenantContext tenant, SafeCfdiXml xml, IngestionOptions options)
{
    private sealed record ExistingInvoice(Guid Id, Guid ImportFileId);
    public async Task<ImportReport> ImportAsync(Guid companyId, Guid actorId, IReadOnlyList<UploadedFile> files, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count > options.MaxFiles || files.Sum(file => (long)(file.Content?.Length ?? 0)) > options.MaxUploadBytes)
            throw new InvalidDataException("Upload exceeds file-count or total-size limit.");
        var uploadId = Guid.NewGuid();
        var parsed = new List<(UploadedFile File, ParsedCfdi? Cfdi, string? Error)>();
        foreach (var file in files)
        {
            try
            {
                if (file.Error is not null || file.Content is null) throw new InvalidDataException(file.Error ?? "Missing file bytes.");
                parsed.Add((file, CfdiParser.Parse(xml.Read(file.Content)), null));
            }
            catch (Exception error) when (error is XmlException or InvalidDataException)
            {
                parsed.Add((file, null, error.Message));
            }
        }
        foreach (var group in parsed.Where(item => item.Cfdi is not null).SelectMany(item => item.Cfdi!.Payments)
            .SelectMany(payment => payment.Documents).GroupBy(doc => doc.InvoiceUuid))
        {
            PaymentDocument? previous = null;
            foreach (var doc in group.OrderBy(doc => doc.Installment))
            {
                if (previous is not null && doc.Installment == previous.Installment + 1 && doc.PreviousBalance != previous.RemainingBalance)
                    doc.InconsistentBalance = true;
                previous = doc;
            }
        }
        var results = new List<FileImportResult>();
        foreach (var item in parsed)
        {
            results.Add(await tenant.ExecuteAsync(companyId, async (connection, transaction, ct) =>
            {
                var rfc = await connection.QuerySingleAsync<string>(new CommandDefinition("SELECT rfc FROM company", transaction: transaction, cancellationToken: ct));
                var reason = item.Error;
                var doc = item.Cfdi;
                if (doc is not null && doc.IssuerRfc != rfc && doc.ReceiverRfc != rfc) reason = "does not involve this company";
                var status = reason is null ? "imported" : "rejected";
                if (status == "imported" && doc is not null)
                {
                    // Serialize one company/UUID so a concurrent duplicate never rewrites the accepted original.
                    await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))",
                        new { key = companyId.ToString() + doc.Uuid.ToString() }, transaction, cancellationToken: ct));
                    var existing = await connection.QuerySingleOrDefaultAsync<ExistingInvoice>(new CommandDefinition(
                        "SELECT id, import_file_id AS ImportFileId FROM cfdi WHERE uuid = @Uuid", doc, transaction, cancellationToken: ct));
                    if (existing is not null)
                    {
                        var duplicateFile = await ImportStorage.InsertFileAsync(connection, transaction, companyId, uploadId, item.File,
                            "cfdi", "already_imported", null, options.RetentionYears, ct);
                        await connection.ExecuteAsync(new CommandDefinition("UPDATE import_file SET original_file_id = @ImportFileId WHERE id = @duplicateFile",
                            new { existing.ImportFileId, duplicateFile }, transaction, cancellationToken: ct));
                        await ImportStorage.AuditAsync(connection, transaction, companyId, actorId, "cfdi.already_imported", duplicateFile, ct);
                        return new FileImportResult(duplicateFile, item.File.Name, "already_imported", null, existing.Id);
                    }
                }
                reason ??= doc?.ReviewReason;
                var fileId = await ImportStorage.InsertFileAsync(connection, transaction, companyId, uploadId, item.File, "cfdi", status, reason, options.RetentionYears, ct);
                Guid? invoiceId = null;
                if (status == "imported" && doc is not null)
                {
                    invoiceId = await connection.QuerySingleAsync<Guid>(new CommandDefinition("""
                        INSERT INTO cfdi(company_id, import_file_id, uuid, type, direction, issuer_rfc, receiver_rfc, issued_at, stamped_at,
                          currency, payment_method, subtotal, discount, transferred_tax, retained_tax, local_transferred_tax, local_retained_tax, total, is_global, review_reason)
                        VALUES (@companyId, @fileId, @Uuid, @Type, @direction, @IssuerRfc, @ReceiverRfc, @IssuedAt, @StampedAt,
                          @Currency, @PaymentMethod, @Subtotal, @Discount, @TransferredTax, @RetainedTax, @LocalTransferredTax, @LocalRetainedTax, @Total, @IsGlobal, @ReviewReason)
                        RETURNING id
                        """, new
                    {
                        companyId,
                        fileId,
                        doc.Uuid,
                        doc.Type,
                        direction = doc.IssuerRfc == rfc ? "issued" : "received",
                        doc.IssuerRfc,
                        doc.ReceiverRfc,
                        doc.IssuedAt,
                        doc.StampedAt,
                        doc.Currency,
                        doc.PaymentMethod,
                        doc.Subtotal,
                        doc.Discount,
                        doc.TransferredTax,
                        doc.RetainedTax,
                        doc.LocalTransferredTax,
                        doc.LocalRetainedTax,
                        doc.Total,
                        doc.IsGlobal,
                        doc.ReviewReason
                    }, transaction, cancellationToken: ct));
                    foreach (var relation in doc.Relations)
                        await connection.ExecuteAsync(new CommandDefinition("INSERT INTO cfdi_relation(company_id, cfdi_id, related_uuid, relation_code) VALUES (@companyId, @invoiceId, @RelatedUuid, @RelationCode)",
                            new { companyId, invoiceId, relation.RelatedUuid, relation.RelationCode }, transaction, cancellationToken: ct));
                    for (var index = 0; index < doc.Payments.Count; index++)
                    {
                        var payment = doc.Payments[index];
                        var paymentId = await connection.QuerySingleAsync<Guid>(new CommandDefinition("""
                            INSERT INTO cfdi_payment(company_id, cfdi_id, payment_index, paid_at, payment_form, currency, exchange_rate, amount, operation_number)
                            VALUES (@companyId, @invoiceId, @index, @PaidAt, @PaymentForm, @Currency, @ExchangeRate, @Amount, @OperationNumber) RETURNING id
                            """, new { companyId, invoiceId, index, payment.PaidAt, payment.PaymentForm, payment.Currency, payment.ExchangeRate, payment.Amount, payment.OperationNumber }, transaction, cancellationToken: ct));
                        foreach (var line in payment.Documents)
                            await connection.ExecuteAsync(new CommandDefinition("""
                                INSERT INTO cfdi_payment_doc(company_id, payment_id, invoice_uuid, installment, currency, previous_balance, paid, remaining_balance, inconsistent_balance)
                                VALUES (@companyId, @paymentId, @InvoiceUuid, @Installment, @Currency, @PreviousBalance, @Paid, @RemainingBalance, @InconsistentBalance)
                                """, new { companyId, paymentId, line.InvoiceUuid, line.Installment, line.Currency, line.PreviousBalance, line.Paid, line.RemainingBalance, line.InconsistentBalance }, transaction, cancellationToken: ct));
                    }
                }
                await ImportStorage.AuditAsync(connection, transaction, companyId, actorId, "cfdi." + status, fileId, ct);
                return new FileImportResult(fileId, item.File.Name, status, reason ?? doc?.ReviewReason, invoiceId);
            }, cancellationToken));
        }
        return new ImportReport(uploadId, results);
    }
}
