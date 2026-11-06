using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Reconciliation.Contracts;

namespace Reconciliation.Api;

internal static class WebApiContract
{
    private sealed record UploadContract(IFormFile[] Files, string? Bank, string? AccountReference);
    private sealed record Operation(Type? Response, Type? Request = null, string Status = "200", string ContentType = "application/json");
    private static readonly Dictionary<string, Operation> Operations = new(StringComparer.Ordinal)
    {
        ["POST auth/tokens"] = new(typeof(SessionTokens), typeof(LoginRequest)),
        ["POST auth/refresh"] = new(typeof(SessionTokens), typeof(RefreshBody)),
        ["POST auth/logout"] = new(null, Status: "204"),
        ["GET companies"] = new(typeof(CompanyItem[])),
        ["GET companies/{companyId}/overview"] = new(typeof(OverviewResponse)),
        ["GET companies/{companyId}/review-queue"] = new(typeof(ReviewPage)),
        ["POST companies/{companyId}/links/{linkId}/decisions"] = new(typeof(DecisionResponse), typeof(DecisionRequest)),
        ["GET companies/{companyId}/cfdi"] = new(typeof(InvoicePage)),
        ["GET companies/{companyId}/cfdi/{invoiceId}/summary"] = new(typeof(InvoiceSummary)),
        ["POST companies/{companyId}/imports/cfdi"] = new(typeof(ImportReceipt), typeof(UploadContract), "202", "multipart/form-data"),
        ["POST companies/{companyId}/imports/bank-statement"] = new(typeof(ImportReceipt), typeof(UploadContract), "202", "multipart/form-data"),
        ["GET companies/{companyId}/imports/{uploadId}"] = new(typeof(ImportReportResponse)),
        ["GET companies/{companyId}/jobs/{jobId}"] = new(typeof(JobResponse)),
        ["POST companies/{companyId}/reconciliation-runs"] = new(typeof(RunReceipt), Status: "202"),
        ["GET companies/{companyId}/movements"] = new(typeof(MovementPage)),
        ["PATCH companies/{companyId}/movements/{movementId}"] = new(null, typeof(ClassificationRequest), "204")
    };

    internal static async Task DescribeAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken token)
    {
        var path = context.Description.RelativePath!.Replace(":guid", "", StringComparison.Ordinal);
        if (!Operations.TryGetValue($"{context.Description.HttpMethod} {path}", out var contract)) return;
        var document = context.Document!;
        async Task<IOpenApiSchema> ReferenceAsync(Type type)
        {
            var name = "Web" + type.Name.Replace("[]", "List", StringComparison.Ordinal);
            var schema = await context.GetOrCreateSchemaAsync(type, cancellationToken: token);
            document.AddComponent(name, schema);
            return new OpenApiSchemaReference(name, document);
        }
        if (contract.Response is not null)
        {
            operation.Responses ??= new OpenApiResponses();
            if (contract.Status == "202") operation.Responses.Remove("200");
            operation.Responses[contract.Status] = new OpenApiResponse
            {
                Description = "Successful response.",
                Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = await ReferenceAsync(contract.Response) } }
            };
            if (contract.Status == "202" && context.Description.RelativePath!.EndsWith("reconciliation-runs", StringComparison.Ordinal))
                operation.Responses["200"] = operation.Responses[contract.Status];
        }
        if (contract.Request is not null)
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = context.Description.RelativePath != "auth/refresh",
                Content = new Dictionary<string, OpenApiMediaType> { [contract.ContentType] = new() { Schema = await ReferenceAsync(contract.Request) } }
            };
        if (context.Description.RelativePath!.StartsWith("auth/", StringComparison.Ordinal))
            operation.Description += " Browser clients opt in using X-Refresh-Transport: cookie and an allowed Origin when Session.BrowserRefreshCookie is enabled; refreshToken is then omitted. Legacy Session.RefreshCookie takes precedence. JSON clients without the header retain body-delivered refresh credentials.";
    }
}
