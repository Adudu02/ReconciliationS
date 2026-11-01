using System.Xml;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using Reconciliation.Core.Tenancy;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Ingestion;

public sealed class CfdiImporter(TenantContext companyScope, SafeCfdiXml xml, IngestionOptions options, IMemoryCache companyRfcs)
{
    private sealed record ParsedUpload(UploadedFile File, ParsedCfdi? Cfdi, string? Error);
    private sealed record ExistingInvoice(Guid Id, Guid ImportFileId);
    public async Task<ImportReport> ImportAsync(Guid companyId, Guid actorId, IReadOnlyList<UploadedFile> files, CancellationToken cancellationToken = default)
    {
        options.EnsureWithinLimits(files);
        var companyRfc = await companyRfcs.GetOrCreateAsync("company-rfc:" + companyId, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return await companyScope.ExecuteAsync(companyId, (connection, transaction, transactionCancellationToken) =>
                connection.QuerySingleAsync<string>(Command("SELECT rfc FROM company", transaction: transaction, cancellationToken: transactionCancellationToken)), cancellationToken);
        });
        var uploadId = Guid.NewGuid();
        var parsed = FlagBrokenInstallmentChains(ParseUploads(files, companyRfc));
        var fileOutcomes = new List<FileImportResult>();
        try
        {
            foreach (var upload in parsed)
            {
                fileOutcomes.Add(await PersistCfdiAsync(companyId, actorId, uploadId, companyRfc, upload, cancellationToken));
            }
        }
        catch (Exception error) when (error is NpgsqlException or CompanyReadOnlyException)
        {
            throw new UploadFailedException(uploadId, error);
        }
        return new ImportReport(uploadId, fileOutcomes);
    }

    private ParsedUpload[] ParseUploads(IReadOnlyList<UploadedFile> files, string? companyRfc)
    {
        var parsed = new List<ParsedUpload>();
        foreach (var uploadedFile in files)
        {
            try
            {
                var fileBytes = uploadedFile.RequireContent();
                var parsedCfdi = CfdiParser.Parse(xml.Read(fileBytes, uploadedFile.ContentLength));
                parsed.Add(parsedCfdi.IssuerRfc != companyRfc && parsedCfdi.ReceiverRfc != companyRfc
                    ? new ParsedUpload(uploadedFile, null, "does not involve this company") : new ParsedUpload(uploadedFile, parsedCfdi, null));
            }
            catch (Exception error) when (error is XmlException or InvalidDataException)
            {
                parsed.Add(new ParsedUpload(uploadedFile, null, error.Message));
            }
        }
        return parsed.ToArray();
    }

    private static ParsedUpload[] FlagBrokenInstallmentChains(ParsedUpload[] parsed)
    {
        var brokenChains = new HashSet<PaymentDocument>(ReferenceEqualityComparer.Instance);
        foreach (var paymentsByInvoiceUuid in parsed.Where(upload => upload.Cfdi is not null).SelectMany(upload => upload.Cfdi!.Payments)
            .SelectMany(payment => payment.Documents).GroupBy(parsedCfdi => parsedCfdi.InvoiceUuid))
        {
            PaymentDocument? previousInstallment = null;
            foreach (var paidInvoice in paymentsByInvoiceUuid.OrderBy(parsedCfdi => parsedCfdi.Installment))
            {
                if (previousInstallment is not null && paidInvoice.Installment == previousInstallment.Installment + 1 && paidInvoice.PreviousBalance != previousInstallment.RemainingBalance)
                    brokenChains.Add(paidInvoice);
                previousInstallment = paidInvoice;
            }
        }
        if (brokenChains.Count == 0) return parsed;
        return parsed.Select(upload => upload.Cfdi is null ? upload : upload with
        {
            Cfdi = upload.Cfdi with
            {
                Payments = upload.Cfdi.Payments.Select(payment => payment with
                {
                    Documents = payment.Documents.Select(paidInvoice => brokenChains.Contains(paidInvoice)
                        ? paidInvoice with { InconsistentBalance = true } : paidInvoice).ToArray()
                }).ToArray()
            }
        }).ToArray();
    }

    private Task<FileImportResult> PersistCfdiAsync(Guid companyId, Guid actorId, Guid uploadId, string? companyRfc, ParsedUpload upload, CancellationToken cancellationToken) =>
        companyScope.ExecuteWriteAsync(companyId, async (connection, transaction, transactionCancellationToken) =>
        {
            var rejectionReason = upload.Error;
            var parsedCfdi = upload.Cfdi;
            var importStatus = rejectionReason is null ? ImportStatus.Imported : ImportStatus.Rejected;
            rejectionReason ??= parsedCfdi?.ReviewReason;
            var fileId = await ImportStorage.InsertFileAsync(connection, transaction, companyId, uploadId, upload.File, ImportFileKind.Cfdi,
                importStatus == ImportStatus.Imported ? ImportStatus.Importing : importStatus, rejectionReason, options.RetentionYears, transactionCancellationToken);
            Guid? cfdiId = null;
            if (importStatus == ImportStatus.Imported && parsedCfdi is not null)
            {
                cfdiId = await connection.QuerySingleOrDefaultAsync<Guid?>(Command("""
                INSERT INTO cfdi(company_id, import_file_id, uuid, type, direction, issuer_rfc, receiver_rfc, issued_at, stamped_at,
                  currency, payment_method, subtotal, discount, transferred_tax, retained_tax, local_transferred_tax, local_retained_tax, total, is_global, review_reason)
                VALUES (@companyId, @fileId, @Uuid, @Type, @direction, @IssuerRfc, @ReceiverRfc, @IssuedAt, @StampedAt,
                  @Currency, @PaymentMethod, @Subtotal, @Discount, @TransferredTax, @RetainedTax, @LocalTransferredTax, @LocalRetainedTax, @Total, @IsGlobal, @ReviewReason)
                ON CONFLICT (company_id, uuid) DO NOTHING RETURNING id
                """, new CfdiInsertRow(companyId, fileId, parsedCfdi.Uuid, parsedCfdi.Type,
                    parsedCfdi.IssuerRfc == companyRfc ? "issued" : "received", parsedCfdi.IssuerRfc, parsedCfdi.ReceiverRfc,
                    parsedCfdi.IssuedAt, parsedCfdi.StampedAt, parsedCfdi.Currency, parsedCfdi.PaymentMethod,
                    parsedCfdi.Subtotal, parsedCfdi.Discount, parsedCfdi.TransferredTax, parsedCfdi.RetainedTax,
                    parsedCfdi.LocalTransferredTax, parsedCfdi.LocalRetainedTax, parsedCfdi.Total, parsedCfdi.IsGlobal, parsedCfdi.ReviewReason), transaction, cancellationToken: transactionCancellationToken));
                if (cfdiId is null)
                {
                    return await RecordDuplicateAsync(connection, transaction, companyId, actorId, fileId, upload.File, parsedCfdi, transactionCancellationToken);
                }
                if (parsedCfdi.Relations.Count > 0)
                    await connection.ExecuteAsync(Command("""
                        INSERT INTO cfdi_relation(company_id, cfdi_id, related_uuid, relation_code)
                        SELECT @companyId, @invoiceId, * FROM unnest(@related::uuid[], @codes::text[])
                        """, new { companyId, invoiceId = cfdiId, related = parsedCfdi.Relations.Select(relation => relation.RelatedUuid).ToArray(), codes = parsedCfdi.Relations.Select(relation => relation.RelationCode).ToArray() }, transaction, cancellationToken: transactionCancellationToken));
                if (parsedCfdi.Payments.Count > 0)
                {
                    var payments = parsedCfdi.Payments.Select((payment, index) => new { Payment = payment, Index = index, Id = Guid.NewGuid() }).ToArray();
                    await connection.ExecuteAsync(Command("""
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
                    await connection.ExecuteAsync(Command("""
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
            await ImportStorage.AuditAsync(connection, transaction, companyId, actorId, ImportFileKind.Cfdi.ToDatabaseValue() + "." + importStatus.ToDatabaseValue(), fileId, transactionCancellationToken);
            return new FileImportResult(fileId, upload.File.Name, importStatus.ToDatabaseValue(), rejectionReason ?? parsedCfdi?.ReviewReason, cfdiId);
        }, cancellationToken);

    private static async Task<FileImportResult> RecordDuplicateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid companyId, Guid actorId, Guid fileId, UploadedFile file, ParsedCfdi parsedCfdi, CancellationToken cancellationToken)
    {
        var alreadyStored = await connection.QuerySingleAsync<ExistingInvoice>(Command(
            "SELECT id, import_file_id AS ImportFileId FROM cfdi WHERE uuid=@Uuid", parsedCfdi, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(Command("UPDATE import_file SET status='already_imported', reason=NULL, original_file_id=@ImportFileId WHERE id=@fileId",
            new { alreadyStored.ImportFileId, fileId }, transaction, cancellationToken: cancellationToken));
        await ImportStorage.AuditAsync(connection, transaction, companyId, actorId, ImportFileKind.Cfdi.ToDatabaseValue() + "." + ImportStatus.AlreadyImported.ToDatabaseValue(), fileId, cancellationToken);
        return new FileImportResult(fileId, file.Name, ImportStatus.AlreadyImported.ToDatabaseValue(), null, alreadyStored.Id);
    }

    private sealed record CfdiInsertRow(Guid CompanyId, Guid FileId, Guid Uuid, string Type, string Direction, string IssuerRfc,
        string ReceiverRfc, DateTime IssuedAt, DateTime StampedAt, string Currency, string? PaymentMethod, decimal Subtotal,
        decimal Discount, decimal TransferredTax, decimal RetainedTax, decimal LocalTransferredTax, decimal LocalRetainedTax,
        decimal Total, bool IsGlobal, string? ReviewReason);
}
