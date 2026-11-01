using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
        routes.MapPost("/companies/{id:guid}/imports/cfdi", async (Guid id, HttpContext http, CfdiImporter importer, IngestionOptions options, CancellationToken ct) =>
        {
            try
            {
                var upload = await MultipartUpload.ReadAsync(http.Request, options, ct);
                return Results.Ok(await importer.ImportAsync(id, Actor(http), upload.Files, ct));
            }
            catch (BadHttpRequestException error) { return Results.Json(new { error = error.Message }, statusCode: error.StatusCode); }
            catch (InvalidDataException error) { return Results.BadRequest(new { error = error.Message }); }
        }).RequireAuthorization("accountant");
        routes.MapPost("/companies/{id:guid}/imports/bank-statement", async (Guid id, HttpContext http, BankStatementImporter importer, IngestionOptions options, CancellationToken ct) =>
        {
            try
            {
                var upload = await MultipartUpload.ReadAsync(http.Request, options, ct);
                return Results.Ok(await importer.ImportAsync(id, Actor(http), upload.Fields.GetValueOrDefault("bank") ?? "",
                    upload.Fields.GetValueOrDefault("accountReference") ?? "", upload.Files, ct));
            }
            catch (BadHttpRequestException error) { return Results.Json(new { error = error.Message }, statusCode: error.StatusCode); }
            catch (InvalidDataException error) { return Results.BadRequest(new { error = error.Message }); }
        }).RequireAuthorization("accountant");
        routes.MapGet("/companies/{id:guid}/banks", (BankStatementImporter importer) => Results.Ok(importer.SupportedBanks))
            .RequireAuthorization("company-member");
        routes.MapGet("/companies/{id:guid}/imports/{uploadId:guid}", (Guid id, Guid uploadId, TenantContext tenant, CancellationToken ct) =>
            tenant.ExecuteAsync<IResult>(id, async (connection, transaction, token) =>
            {
                var files = (await connection.QueryAsync<FileImportResult>(new CommandDefinition("""
                    SELECT f.id AS FileId, f.filename AS Name, f.status, f.reason, c.id AS CfdiId,
                      f.imported_movements AS ImportedMovements, f.duplicate_movements AS DuplicateMovements
                    FROM import_file f LEFT JOIN cfdi c ON c.import_file_id = coalesce(f.original_file_id, f.id)
                    WHERE f.upload_id = @uploadId ORDER BY f.created_at, f.id
                    """, new { uploadId }, transaction, cancellationToken: token))).ToArray();
                return files.Length == 0 ? Results.NotFound() : Results.Ok(new ImportReport(uploadId, files));
            }, ct)).RequireAuthorization("company-member");
        routes.MapGet("/companies/{id:guid}/cfdi/{invoiceId:guid}", (Guid id, Guid invoiceId, TenantContext tenant, CancellationToken ct) =>
            tenant.ExecuteAsync<IResult>(id, async (connection, transaction, token) =>
            {
                var invoice = await connection.QuerySingleOrDefaultAsync<InvoiceResponse>(new CommandDefinition("""
                    SELECT id, uuid, type, direction, issuer_rfc AS IssuerRfc, receiver_rfc AS ReceiverRfc,
                      issued_at AS IssuedAt, stamped_at AS StampedAt, currency, payment_method AS PaymentMethod,
                      subtotal, discount, transferred_tax AS TransferredTax, retained_tax AS RetainedTax,
                      local_transferred_tax AS LocalTransferredTax, local_retained_tax AS LocalRetainedTax,
                      total, is_global AS IsGlobal, review_reason AS ReviewReason FROM cfdi WHERE id = @invoiceId
                    """, new { invoiceId }, transaction, cancellationToken: token));
                return invoice is null ? Results.NotFound() : Results.Ok(invoice);
            }, ct)).RequireAuthorization("company-member");
        routes.MapGet("/companies/{id:guid}/cfdi/{invoiceId:guid}/xml", (Guid id, Guid invoiceId, TenantContext tenant) =>
            (IResult)new StoredOriginalResult(tenant, id, invoiceId,
                "SELECT f.original FROM import_file f JOIN cfdi c ON c.import_file_id=f.id WHERE c.id=@resourceId", "application/xml", "original.xml"))
            .RequireAuthorization("company-member");
        routes.MapGet("/companies/{id:guid}/imports/files/{fileId:guid}/original", (Guid id, Guid fileId, TenantContext tenant) =>
            (IResult)new StoredOriginalResult(tenant, id, fileId, """
                SELECT original.original FROM import_file requested
                JOIN import_file original ON original.id=coalesce(requested.original_file_id,requested.id)
                WHERE requested.id=@resourceId AND original.original IS NOT NULL
                """, "application/octet-stream", "original")).RequireAuthorization("company-member");
        return routes;
    }

    private static Guid Actor(HttpContext http) => Guid.Parse(http.User.FindFirstValue(JwtRegisteredClaimNames.Sub)!);
}
