using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Reconciliation.Core.Tenancy;

namespace Reconciliation.Core.Ingestion;

public sealed record InvoiceResponse(Guid Id, Guid Uuid, string Type, string Direction, string IssuerRfc, string ReceiverRfc,
    DateTime IssuedAt, DateTime StampedAt, string Currency, string? PaymentMethod, decimal Subtotal, decimal Discount,
    decimal TransferredTax, decimal RetainedTax, decimal LocalTransferredTax, decimal LocalRetainedTax, decimal Total, bool IsGlobal, string? ReviewReason);

public static class IngestionEndpoints
{
    public static IEndpointRouteBuilder MapIngestion(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/companies/{companyId:guid}/imports/cfdi", async (Guid companyId, HttpContext httpContext, CfdiImporter cfdiImporter, IngestionOptions ingestionOptions, CancellationToken cancellationToken) =>
        {
            try
            {
                var multipartUpload = await MultipartUpload.ReadAsync(httpContext.Request, ingestionOptions, cancellationToken);
                return Results.Ok(await cfdiImporter.ImportAsync(companyId, Actor(httpContext), multipartUpload.Files, cancellationToken));
            }
            catch (BadHttpRequestException error) { return Results.Json(new { error = error.Message }, statusCode: error.StatusCode); }
            catch (InvalidDataException error) { return Results.BadRequest(new { error = error.Message }); }
        }).RequireAuthorization(Policies.Accountant);
        routes.MapPost("/companies/{companyId:guid}/imports/bank-statement", async (Guid companyId, HttpContext httpContext, BankStatementImporter statementImporter, IngestionOptions ingestionOptions, CancellationToken cancellationToken) =>
        {
            try
            {
                var multipartUpload = await MultipartUpload.ReadAsync(httpContext.Request, ingestionOptions, cancellationToken);
                return Results.Ok(await statementImporter.ImportAsync(companyId, Actor(httpContext), multipartUpload.Fields.GetValueOrDefault("bank") ?? "",
                    multipartUpload.Fields.GetValueOrDefault("accountReference") ?? "", multipartUpload.Files, cancellationToken));
            }
            catch (BadHttpRequestException error) { return Results.Json(new { error = error.Message }, statusCode: error.StatusCode); }
            catch (InvalidDataException error) { return Results.BadRequest(new { error = error.Message }); }
        }).RequireAuthorization(Policies.Accountant);
        routes.MapGet("/companies/{companyId:guid}/banks", (BankStatementImporter statementImporter) => Results.Ok(statementImporter.SupportedBanks))
            .RequireAuthorization(Policies.CompanyMember);
        routes.MapGet("/companies/{companyId:guid}/imports/{uploadId:guid}", (Guid companyId, Guid uploadId, TenantContext companyScope, CancellationToken cancellationToken) =>
            companyScope.ExecuteAsync<IResult>(companyId, async (connection, transaction, transactionCancellationToken) =>
            {
                var fileOutcomes = (await connection.QueryAsync<FileImportResult>(new CommandDefinition("""
                    SELECT f.id AS FileId, f.filename AS Name, f.status, f.reason, c.id AS CfdiId,
                      f.imported_movements AS ImportedMovements, f.duplicate_movements AS DuplicateMovements
                    FROM import_file f LEFT JOIN cfdi c ON c.import_file_id = coalesce(f.original_file_id, f.id)
                    WHERE f.upload_id = @uploadId ORDER BY f.created_at, f.id
                    """, new { uploadId }, transaction, cancellationToken: transactionCancellationToken))).ToArray();
                return fileOutcomes.Length == 0 ? Results.NotFound() : Results.Ok(new ImportReport(uploadId, fileOutcomes));
            }, cancellationToken)).RequireAuthorization(Policies.CompanyMember);
        routes.MapGet("/companies/{companyId:guid}/cfdi/{invoiceId:guid}", (Guid companyId, [FromRoute(Name = "invoiceId")] Guid cfdiId, TenantContext companyScope, CancellationToken cancellationToken) =>
            companyScope.ExecuteAsync<IResult>(companyId, async (connection, transaction, transactionCancellationToken) =>
            {
                var storedCfdi = await connection.QuerySingleOrDefaultAsync<InvoiceResponse>(new CommandDefinition("""
                    SELECT id, uuid, type, direction, issuer_rfc AS IssuerRfc, receiver_rfc AS ReceiverRfc,
                      issued_at AS IssuedAt, stamped_at AS StampedAt, currency, payment_method AS PaymentMethod,
                      subtotal, discount, transferred_tax AS TransferredTax, retained_tax AS RetainedTax,
                      local_transferred_tax AS LocalTransferredTax, local_retained_tax AS LocalRetainedTax,
                      total, is_global AS IsGlobal, review_reason AS ReviewReason FROM cfdi WHERE id = @invoiceId
                    """, new { invoiceId = cfdiId }, transaction, cancellationToken: transactionCancellationToken));
                return storedCfdi is null ? Results.NotFound() : Results.Ok(storedCfdi);
            }, cancellationToken)).RequireAuthorization(Policies.CompanyMember);
        routes.MapGet("/companies/{companyId:guid}/cfdi/{invoiceId:guid}/xml", (Guid companyId, [FromRoute(Name = "invoiceId")] Guid cfdiId, TenantContext companyScope) =>
            (IResult)new StoredOriginalResult(companyScope, companyId, cfdiId,
                "SELECT f.original FROM import_file f JOIN cfdi c ON c.import_file_id=f.id WHERE c.id=@resourceId", "application/xml", "original.xml"))
            .RequireAuthorization(Policies.CompanyMember);
        routes.MapGet("/companies/{companyId:guid}/imports/files/{fileId:guid}/original", (Guid companyId, Guid fileId, TenantContext companyScope) =>
            (IResult)new StoredOriginalResult(companyScope, companyId, fileId, """
                SELECT original.original FROM import_file requested
                JOIN import_file original ON original.id=coalesce(requested.original_file_id,requested.id)
                WHERE requested.id=@resourceId AND original.original IS NOT NULL
                """, "application/octet-stream", "original")).RequireAuthorization(Policies.CompanyMember);
        return routes;
    }

    private static Guid Actor(HttpContext httpContext) => Guid.Parse(httpContext.User.FindFirstValue(JwtRegisteredClaimNames.Sub)!);
}
