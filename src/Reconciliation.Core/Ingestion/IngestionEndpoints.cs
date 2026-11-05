using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Reconciliation.Core.Matching;
using Reconciliation.Core.Tenancy;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Ingestion;

public sealed record CfdiResponse(Guid Id, Guid Uuid, string Type, string Direction, string IssuerRfc, string ReceiverRfc,
    DateTime IssuedAt, DateTime StampedAt, string Currency, string? PaymentMethod, decimal Subtotal, decimal Discount,
    decimal TransferredTax, decimal RetainedTax, decimal LocalTransferredTax, decimal LocalRetainedTax, decimal Total, bool IsGlobal, string? ReviewReason)
{
    public InvoiceMatchingState? Matching { get; init; }
}

public static class IngestionEndpoints
{
    public static IEndpointRouteBuilder MapIngestion(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/companies/{companyId:guid}/imports/cfdi", (Guid companyId, HttpContext httpContext, ImportJobService importJobs, IngestionOptions ingestionOptions, CancellationToken cancellationToken) =>
            HandleUploadAsync(httpContext.Request, ingestionOptions, upload => importJobs.EnqueueAsync(companyId, httpContext.User.UserId()!.Value, "import_cfdi", upload.Files, null, null, cancellationToken), cancellationToken))
            .RequireAuthorization(Policies.Accountant);
        routes.MapPost("/companies/{companyId:guid}/imports/bank-statement", (Guid companyId, HttpContext httpContext, ImportJobService importJobs, IngestionOptions ingestionOptions, CancellationToken cancellationToken) =>
            HandleUploadAsync(httpContext.Request, ingestionOptions, upload => importJobs.EnqueueAsync(companyId, httpContext.User.UserId()!.Value, "import_statement", upload.Files,
                upload.Fields.GetValueOrDefault("bank") ?? "", upload.Fields.GetValueOrDefault("accountReference") ?? "", cancellationToken), cancellationToken))
            .RequireAuthorization(Policies.Accountant);
        routes.MapGet("/companies/{companyId:guid}/banks", (BankStatementImporter statementImporter) => Results.Ok(statementImporter.SupportedBanks))
            .RequireAuthorization(Policies.CompanyMember);
        routes.MapGet("/companies/{companyId:guid}/imports/{uploadId:guid}", (Guid companyId, Guid uploadId, CompanyScope companyScope, CancellationToken cancellationToken) =>
            companyScope.ExecuteResultAsync(companyId, async (connection, transaction, transactionCancellationToken) =>
            {
                var fileOutcomes = (await connection.QueryAsync<FileImportOutcome>(Command("""
                    SELECT f.id AS FileId, f.filename AS Name, f.status, f.reason, c.id AS CfdiId,
                      f.imported_movements AS ImportedMovements, f.duplicate_movements AS DuplicateMovements
                    FROM import_file f LEFT JOIN cfdi c ON c.import_file_id = coalesce(f.original_file_id, f.id)
                    WHERE f.upload_id = @uploadId ORDER BY f.created_at, f.file_index, f.id
                    """, new { uploadId }, transaction, cancellationToken: transactionCancellationToken))).ToArray();
                return fileOutcomes.Length == 0 ? Results.NotFound() : Results.Ok(new ImportReport(uploadId, fileOutcomes));
            }, cancellationToken)).RequireAuthorization(Policies.CompanyMember);
        routes.MapGet("/companies/{companyId:guid}/cfdi/{invoiceId:guid}", (Guid companyId, [FromRoute(Name = "invoiceId")] Guid cfdiId, CompanyScope companyScope, TimeProvider clock, CancellationToken cancellationToken) =>
            companyScope.ExecuteResultAsync(companyId, async (connection, transaction, transactionCancellationToken) =>
            {
                var storedCfdi = await connection.QuerySingleOrDefaultAsync<CfdiResponse>(Command("""
                    SELECT id, uuid, type, direction, issuer_rfc AS IssuerRfc, receiver_rfc AS ReceiverRfc,
                      issued_at AS IssuedAt, stamped_at AS StampedAt, currency, payment_method AS PaymentMethod,
                      subtotal, discount, transferred_tax AS TransferredTax, retained_tax AS RetainedTax,
                      local_transferred_tax AS LocalTransferredTax, local_retained_tax AS LocalRetainedTax,
                      total, is_global AS IsGlobal, review_reason AS ReviewReason FROM cfdi WHERE id = @invoiceId
                    """, new { invoiceId = cfdiId }, transaction, cancellationToken: transactionCancellationToken));
                if (storedCfdi is { Type: "I" })
                {
                    var matchingJson = await connection.ExecuteScalarAsync<string>(Command("""
                        SELECT row_to_json(matching)::text FROM (
                          SELECT s.state,p.credit_notes AS CreditNotes,p.payable,s.reconciled_amount AS ReconciledAmount,
                            s.suggested_amount AS SuggestedAmount,s.pending_amount AS PendingAmount,s.uncovered_amount AS UncoveredAmount,
                            s.paid_percentage AS PaidPercentage,r.reasons,r.requires_review AS RequiresReview,
                            r.complement_due_date AS ComplementDueDate,r.complement_overdue AS ComplementOverdue,c.sat_status AS SatStatus,c.cancellation_status AS CancellationStatus
                          FROM cfdi_state() s JOIN cfdi c ON c.company_id=s.company_id AND c.id=s.id JOIN cfdi_payable() p ON p.company_id=s.company_id AND p.id=s.id
                          JOIN cfdi_review_reasons(@today) r ON r.company_id=s.company_id AND r.id=s.id WHERE s.id=@cfdiId
                        ) matching
                        """, new { cfdiId, today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime) }, transaction, cancellationToken: transactionCancellationToken));
                    storedCfdi = storedCfdi with { Matching = JsonSerializer.Deserialize<InvoiceMatchingState>(matchingJson!, JsonSerializerOptions.Web) };
                }
                return CompanyScope.OkOrNotFound(storedCfdi);
            }, cancellationToken)).RequireAuthorization(Policies.CompanyMember);
        routes.MapGet("/companies/{companyId:guid}/cfdi/{invoiceId:guid}/xml", (Guid companyId, [FromRoute(Name = "invoiceId")] Guid cfdiId, CompanyScope companyScope) =>
            (IResult)new StoredOriginalResult(companyScope, companyId, cfdiId,
                "SELECT f.original FROM import_file f JOIN cfdi c ON c.import_file_id=f.id WHERE c.id=@resourceId", "application/xml", "original.xml"))
            .RequireAuthorization(Policies.CompanyMember);
        routes.MapGet("/companies/{companyId:guid}/imports/files/{fileId:guid}/original", (Guid companyId, Guid fileId, CompanyScope companyScope) =>
            (IResult)new StoredOriginalResult(companyScope, companyId, fileId, """
                SELECT original.original FROM import_file requested
                JOIN import_file original ON original.id=coalesce(requested.original_file_id,requested.id)
                WHERE requested.id=@resourceId AND original.original IS NOT NULL
                """, "application/octet-stream", "original")).RequireAuthorization(Policies.CompanyMember);
        return routes;
    }


    private static async Task<IResult> HandleUploadAsync(HttpRequest request, IngestionOptions options,
        Func<MultipartUpload, Task<QueuedImport>> enqueue, CancellationToken cancellationToken)
    {
        try
        {
            var queued = await enqueue(await MultipartUpload.ReadAsync(request, options, cancellationToken));
            return Results.Accepted($"/companies/{request.RouteValues["companyId"]}/jobs/{queued.JobId}", queued);
        }
        catch (BadHttpRequestException error) { return Results.Json(new { error = error.Message }, statusCode: error.StatusCode); }
        catch (InvalidDataException error) { return Results.BadRequest(new { error = error.Message }); }
    }
}
