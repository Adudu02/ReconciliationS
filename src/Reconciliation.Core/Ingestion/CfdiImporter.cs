using System.Xml;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using Reconciliation.Core.Tenancy;

namespace Reconciliation.Core.Ingestion;

public sealed class CfdiImporter(TenantContext companyScope, SafeCfdiXml xml, IngestionOptions options, IMemoryCache companyRfcs)
{
    private sealed record ExistingInvoice(Guid Id, Guid ImportFileId);
    public async Task<ImportReport> ImportAsync(Guid companyId, Guid actorId, IReadOnlyList<UploadedFile> files, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count > options.MaxFiles || files.Sum(uploadedFile => (long)uploadedFile.ContentLength) > options.MaxUploadBytes)
            throw new InvalidDataException("Upload exceeds file-count or total-size limit.");
        var companyRfc = await companyRfcs.GetOrCreateAsync("company-rfc:" + companyId, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return await companyScope.ExecuteAsync(companyId, (connection, transaction, transactionCancellationToken) =>
                connection.QuerySingleAsync<string>(new CommandDefinition("SELECT rfc FROM company", transaction: transaction, cancellationToken: transactionCancellationToken)), cancellationToken);
        });
        var uploadId = Guid.NewGuid();
        var parsed = new List<(UploadedFile File, ParsedCfdi? Cfdi, string? Error)>();
        foreach (var uploadedFile in files)
        {
            try
            {
                if (uploadedFile.Error is not null || uploadedFile.Content is null) throw new InvalidDataException(uploadedFile.Error ?? "Missing file bytes.");
                var parsedCfdi = CfdiParser.Parse(xml.Read(uploadedFile.Content, uploadedFile.ContentLength));
                parsed.Add(parsedCfdi.IssuerRfc != companyRfc && parsedCfdi.ReceiverRfc != companyRfc
                    ? (uploadedFile, null, "does not involve this company") : (uploadedFile, parsedCfdi, null));
            }
            catch (Exception error) when (error is XmlException or InvalidDataException)
            {
                parsed.Add((uploadedFile, null, error.Message));
            }
        }
        foreach (var paymentsByInvoiceUuid in parsed.Where(upload => upload.Cfdi is not null).SelectMany(upload => upload.Cfdi!.Payments)
            .SelectMany(payment => payment.Documents).GroupBy(parsedCfdi => parsedCfdi.InvoiceUuid))
        {
            PaymentDocument? previousInstallment = null;
            foreach (var paidInvoice in paymentsByInvoiceUuid.OrderBy(parsedCfdi => parsedCfdi.Installment))
            {
                if (previousInstallment is not null && paidInvoice.Installment == previousInstallment.Installment + 1 && paidInvoice.PreviousBalance != previousInstallment.RemainingBalance)
                    paidInvoice.InconsistentBalance = true;
                previousInstallment = paidInvoice;
            }
        }
        var fileOutcomes = new List<FileImportResult>();
        try
        {
            foreach (var upload in parsed)
            {
                fileOutcomes.Add(await companyScope.ExecuteWriteAsync(companyId, async (connection, transaction, transactionCancellationToken) =>
                {
                    var rejectionReason = upload.Error;
                    var parsedCfdi = upload.Cfdi;
                    var importStatus = rejectionReason is null ? "imported" : "rejected";
                    rejectionReason ??= parsedCfdi?.ReviewReason;
                    var fileId = await ImportStorage.InsertFileAsync(connection, transaction, companyId, uploadId, upload.File, "cfdi",
                        importStatus == "imported" ? "importing" : importStatus, rejectionReason, options.RetentionYears, transactionCancellationToken);
                    Guid? cfdiId = null;
                    if (importStatus == "imported" && parsedCfdi is not null)
                    {
                        cfdiId = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition("""
                        INSERT INTO cfdi(company_id, import_file_id, uuid, type, direction, issuer_rfc, receiver_rfc, issued_at, stamped_at,
                          currency, payment_method, subtotal, discount, transferred_tax, retained_tax, local_transferred_tax, local_retained_tax, total, is_global, review_reason)
                        VALUES (@companyId, @fileId, @Uuid, @Type, @direction, @IssuerRfc, @ReceiverRfc, @IssuedAt, @StampedAt,
                          @Currency, @PaymentMethod, @Subtotal, @Discount, @TransferredTax, @RetainedTax, @LocalTransferredTax, @LocalRetainedTax, @Total, @IsGlobal, @ReviewReason)
                        ON CONFLICT (company_id, uuid) DO NOTHING RETURNING id
                        """, new
                        {
                            companyId,
                            fileId,
                            parsedCfdi.Uuid,
                            parsedCfdi.Type,
                            direction = parsedCfdi.IssuerRfc == companyRfc ? "issued" : "received",
                            parsedCfdi.IssuerRfc,
                            parsedCfdi.ReceiverRfc,
                            parsedCfdi.IssuedAt,
                            parsedCfdi.StampedAt,
                            parsedCfdi.Currency,
                            parsedCfdi.PaymentMethod,
                            parsedCfdi.Subtotal,
                            parsedCfdi.Discount,
                            parsedCfdi.TransferredTax,
                            parsedCfdi.RetainedTax,
                            parsedCfdi.LocalTransferredTax,
                            parsedCfdi.LocalRetainedTax,
                            parsedCfdi.Total,
                            parsedCfdi.IsGlobal,
                            parsedCfdi.ReviewReason
                        }, transaction, cancellationToken: transactionCancellationToken));
                        if (cfdiId is null)
                        {
                            var alreadyStored = await connection.QuerySingleAsync<ExistingInvoice>(new CommandDefinition(
                                "SELECT id, import_file_id AS ImportFileId FROM cfdi WHERE uuid=@Uuid", parsedCfdi, transaction, cancellationToken: transactionCancellationToken));
                            await connection.ExecuteAsync(new CommandDefinition("UPDATE import_file SET status='already_imported', reason=NULL, original_file_id=@ImportFileId WHERE id=@fileId",
                                new { alreadyStored.ImportFileId, fileId }, transaction, cancellationToken: transactionCancellationToken));
                            await ImportStorage.AuditAsync(connection, transaction, companyId, actorId, "cfdi.already_imported", fileId, transactionCancellationToken);
                            return new FileImportResult(fileId, upload.File.Name, "already_imported", null, alreadyStored.Id);
                        }
                        if (parsedCfdi.Relations.Count > 0)
                            await connection.ExecuteAsync(new CommandDefinition("""
                                INSERT INTO cfdi_relation(company_id, cfdi_id, related_uuid, relation_code)
                                SELECT @companyId, @invoiceId, * FROM unnest(@related::uuid[], @codes::text[])
                                """, new { companyId, invoiceId = cfdiId, related = parsedCfdi.Relations.Select(relation => relation.RelatedUuid).ToArray(), codes = parsedCfdi.Relations.Select(relation => relation.RelationCode).ToArray() }, transaction, cancellationToken: transactionCancellationToken));
                        if (parsedCfdi.Payments.Count > 0)
                        {
                            var payments = parsedCfdi.Payments.Select((payment, index) => new { Payment = payment, Index = index, Id = Guid.NewGuid() }).ToArray();
                            await connection.ExecuteAsync(new CommandDefinition("""
                                INSERT INTO cfdi_payment(company_id, cfdi_id, id, payment_index, paid_at, payment_form, currency, exchange_rate, amount, operation_number)
                                SELECT @companyId, @invoiceId, * FROM unnest(@ids::uuid[], @indexes::integer[], @dates::timestamp[],
                                  @forms::text[], @currencies::text[], @rates::numeric[], @amounts::numeric[], @operations::text[])
                                """, new
                            {
                                companyId,
                                invoiceId = cfdiId,
                                ids = payments.Select(payment => payment.Id).ToArray(),
                                indexes = payments.Select(payment => payment.Index).ToArray(),
                                dates = payments.Select(payment => payment.Payment.PaidAt).ToArray(),
                                forms = payments.Select(payment => payment.Payment.PaymentForm).ToArray(),
                                currencies = payments.Select(payment => payment.Payment.Currency).ToArray(),
                                rates = payments.Select(payment => payment.Payment.ExchangeRate).ToArray(),
                                amounts = payments.Select(payment => payment.Payment.Amount).ToArray(),
                                operations = payments.Select(payment => payment.Payment.OperationNumber).ToArray()
                            }, transaction, cancellationToken: transactionCancellationToken));
                            var documents = payments.SelectMany(payment => payment.Payment.Documents.Select(paidInvoice => new { PaymentId = payment.Id, Line = paidInvoice })).ToArray();
                            await connection.ExecuteAsync(new CommandDefinition("""
                                INSERT INTO cfdi_payment_doc(company_id, payment_id, invoice_uuid, installment, currency, previous_balance, paid, remaining_balance, inconsistent_balance)
                                SELECT @companyId, * FROM unnest(@paymentIds::uuid[], @invoices::uuid[], @installments::integer[], @currencies::text[],
                                  @previous::numeric[], @paid::numeric[], @remaining::numeric[], @inconsistent::boolean[])
                                """, new
                            {
                                companyId,
                                paymentIds = documents.Select(paidInvoice => paidInvoice.PaymentId).ToArray(),
                                invoices = documents.Select(paidInvoice => paidInvoice.Line.InvoiceUuid).ToArray(),
                                installments = documents.Select(paidInvoice => paidInvoice.Line.Installment).ToArray(),
                                currencies = documents.Select(paidInvoice => paidInvoice.Line.Currency).ToArray(),
                                previous = documents.Select(paidInvoice => paidInvoice.Line.PreviousBalance).ToArray(),
                                paid = documents.Select(paidInvoice => paidInvoice.Line.Paid).ToArray(),
                                remaining = documents.Select(paidInvoice => paidInvoice.Line.RemainingBalance).ToArray(),
                                inconsistent = documents.Select(paidInvoice => paidInvoice.Line.InconsistentBalance).ToArray()
                            }, transaction, cancellationToken: transactionCancellationToken));
                        }
                        await ImportStorage.FinalizeAsync(connection, transaction, fileId, upload.File, options.RetentionYears, 0, 0, transactionCancellationToken);
                    }
                    await ImportStorage.AuditAsync(connection, transaction, companyId, actorId, "cfdi." + importStatus, fileId, transactionCancellationToken);
                    return new FileImportResult(fileId, upload.File.Name, importStatus, rejectionReason ?? parsedCfdi?.ReviewReason, cfdiId);
                }, cancellationToken));
            }
        }
        catch (Exception error) when (error is NpgsqlException or CompanyReadOnlyException)
        {
            throw new UploadFailedException(uploadId, error);
        }
        return new ImportReport(uploadId, fileOutcomes);
    }
}
