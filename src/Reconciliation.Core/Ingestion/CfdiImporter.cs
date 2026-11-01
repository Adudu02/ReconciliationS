using System.Xml;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using Reconciliation.Core.Tenancy;

namespace Reconciliation.Core.Ingestion;

public sealed class CfdiImporter(TenantContext tenant, SafeCfdiXml xml, IngestionOptions options, IMemoryCache companyRfcs)
{
    private sealed record ExistingInvoice(Guid Id, Guid ImportFileId);
    public async Task<ImportReport> ImportAsync(Guid companyId, Guid actorId, IReadOnlyList<UploadedFile> files, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count > options.MaxFiles || files.Sum(file => (long)file.ContentLength) > options.MaxUploadBytes)
            throw new InvalidDataException("Upload exceeds file-count or total-size limit.");
        var rfc = await companyRfcs.GetOrCreateAsync("company-rfc:" + companyId, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return await tenant.ExecuteAsync(companyId, (connection, transaction, ct) =>
                connection.QuerySingleAsync<string>(new CommandDefinition("SELECT rfc FROM company", transaction: transaction, cancellationToken: ct)), cancellationToken);
        });
        var uploadId = Guid.NewGuid();
        var parsed = new List<(UploadedFile File, ParsedCfdi? Cfdi, string? Error)>();
        foreach (var file in files)
        {
            try
            {
                if (file.Error is not null || file.Content is null) throw new InvalidDataException(file.Error ?? "Missing file bytes.");
                var doc = CfdiParser.Parse(xml.Read(file.Content, file.ContentLength));
                parsed.Add(doc.IssuerRfc != rfc && doc.ReceiverRfc != rfc
                    ? (file, null, "does not involve this company") : (file, doc, null));
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
        try
        {
            foreach (var item in parsed)
            {
                results.Add(await tenant.ExecuteWriteAsync(companyId, async (connection, transaction, ct) =>
                {
                    var reason = item.Error;
                    var doc = item.Cfdi;
                    var status = reason is null ? "imported" : "rejected";
                    reason ??= doc?.ReviewReason;
                    var fileId = await ImportStorage.InsertFileAsync(connection, transaction, companyId, uploadId, item.File, "cfdi",
                        status == "imported" ? "importing" : status, reason, options.RetentionYears, ct);
                    Guid? invoiceId = null;
                    if (status == "imported" && doc is not null)
                    {
                        invoiceId = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition("""
                        INSERT INTO cfdi(company_id, import_file_id, uuid, type, direction, issuer_rfc, receiver_rfc, issued_at, stamped_at,
                          currency, payment_method, subtotal, discount, transferred_tax, retained_tax, local_transferred_tax, local_retained_tax, total, is_global, review_reason)
                        VALUES (@companyId, @fileId, @Uuid, @Type, @direction, @IssuerRfc, @ReceiverRfc, @IssuedAt, @StampedAt,
                          @Currency, @PaymentMethod, @Subtotal, @Discount, @TransferredTax, @RetainedTax, @LocalTransferredTax, @LocalRetainedTax, @Total, @IsGlobal, @ReviewReason)
                        ON CONFLICT (company_id, uuid) DO NOTHING RETURNING id
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
                        if (invoiceId is null)
                        {
                            var existing = await connection.QuerySingleAsync<ExistingInvoice>(new CommandDefinition(
                                "SELECT id, import_file_id AS ImportFileId FROM cfdi WHERE uuid=@Uuid", doc, transaction, cancellationToken: ct));
                            await connection.ExecuteAsync(new CommandDefinition("UPDATE import_file SET status='already_imported', reason=NULL, original_file_id=@ImportFileId WHERE id=@fileId",
                                new { existing.ImportFileId, fileId }, transaction, cancellationToken: ct));
                            await ImportStorage.AuditAsync(connection, transaction, companyId, actorId, "cfdi.already_imported", fileId, ct);
                            return new FileImportResult(fileId, item.File.Name, "already_imported", null, existing.Id);
                        }
                        if (doc.Relations.Count > 0)
                            await connection.ExecuteAsync(new CommandDefinition("""
                                INSERT INTO cfdi_relation(company_id, cfdi_id, related_uuid, relation_code)
                                SELECT @companyId, @invoiceId, * FROM unnest(@related::uuid[], @codes::text[])
                                """, new { companyId, invoiceId, related = doc.Relations.Select(relation => relation.RelatedUuid).ToArray(), codes = doc.Relations.Select(relation => relation.RelationCode).ToArray() }, transaction, cancellationToken: ct));
                        if (doc.Payments.Count > 0)
                        {
                            var payments = doc.Payments.Select((payment, index) => new { Payment = payment, Index = index, Id = Guid.NewGuid() }).ToArray();
                            await connection.ExecuteAsync(new CommandDefinition("""
                                INSERT INTO cfdi_payment(company_id, cfdi_id, id, payment_index, paid_at, payment_form, currency, exchange_rate, amount, operation_number)
                                SELECT @companyId, @invoiceId, * FROM unnest(@ids::uuid[], @indexes::integer[], @dates::timestamp[],
                                  @forms::text[], @currencies::text[], @rates::numeric[], @amounts::numeric[], @operations::text[])
                                """, new
                            {
                                companyId,
                                invoiceId,
                                ids = payments.Select(payment => payment.Id).ToArray(),
                                indexes = payments.Select(payment => payment.Index).ToArray(),
                                dates = payments.Select(payment => payment.Payment.PaidAt).ToArray(),
                                forms = payments.Select(payment => payment.Payment.PaymentForm).ToArray(),
                                currencies = payments.Select(payment => payment.Payment.Currency).ToArray(),
                                rates = payments.Select(payment => payment.Payment.ExchangeRate).ToArray(),
                                amounts = payments.Select(payment => payment.Payment.Amount).ToArray(),
                                operations = payments.Select(payment => payment.Payment.OperationNumber).ToArray()
                            }, transaction, cancellationToken: ct));
                            var documents = payments.SelectMany(payment => payment.Payment.Documents.Select(line => new { PaymentId = payment.Id, Line = line })).ToArray();
                            await connection.ExecuteAsync(new CommandDefinition("""
                                INSERT INTO cfdi_payment_doc(company_id, payment_id, invoice_uuid, installment, currency, previous_balance, paid, remaining_balance, inconsistent_balance)
                                SELECT @companyId, * FROM unnest(@paymentIds::uuid[], @invoices::uuid[], @installments::integer[], @currencies::text[],
                                  @previous::numeric[], @paid::numeric[], @remaining::numeric[], @inconsistent::boolean[])
                                """, new
                            {
                                companyId,
                                paymentIds = documents.Select(line => line.PaymentId).ToArray(),
                                invoices = documents.Select(line => line.Line.InvoiceUuid).ToArray(),
                                installments = documents.Select(line => line.Line.Installment).ToArray(),
                                currencies = documents.Select(line => line.Line.Currency).ToArray(),
                                previous = documents.Select(line => line.Line.PreviousBalance).ToArray(),
                                paid = documents.Select(line => line.Line.Paid).ToArray(),
                                remaining = documents.Select(line => line.Line.RemainingBalance).ToArray(),
                                inconsistent = documents.Select(line => line.Line.InconsistentBalance).ToArray()
                            }, transaction, cancellationToken: ct));
                        }
                        await ImportStorage.FinalizeAsync(connection, transaction, fileId, item.File, options.RetentionYears, 0, 0, ct);
                    }
                    await ImportStorage.AuditAsync(connection, transaction, companyId, actorId, "cfdi." + status, fileId, ct);
                    return new FileImportResult(fileId, item.File.Name, status, reason ?? doc?.ReviewReason, invoiceId);
                }, cancellationToken));
            }
        }
        catch (Exception error) when (error is NpgsqlException or CompanyReadOnlyException)
        {
            throw new UploadFailedException(uploadId, error);
        }
        return new ImportReport(uploadId, results);
    }
}
