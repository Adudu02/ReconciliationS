using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using Reconciliation.Core.Tenancy;

namespace Reconciliation.Api;

internal static class ApiDescription
{
    internal const string Version = "1.0.0";

    internal static IServiceCollection AddVersionedApiDescription(this IServiceCollection services) => services.AddOpenApi(options =>
    {
        options.AddOperationTransformer((operation, context, _) =>
        {
            var metadata = context.Description.ActionDescriptor.EndpointMetadata;
            var anonymous = metadata.OfType<IAllowAnonymous>().Any();
            var policy = metadata.OfType<IAuthorizeData>().Select(item => item.Policy).FirstOrDefault(value => value is not null);
            var role = anonymous ? "anonymous (public endpoint)" : policy switch
            {
                Policies.Administrator => "administrator",
                Policies.Accountant => "accountant or administrator",
                Policies.PlatformOperator => "platform operator",
                Policies.CompanyMember => "viewer, accountant or administrator",
                _ => "authenticated user"
            };
            operation.Summary ??= $"{context.Description.HttpMethod} {context.Description.RelativePath}";
            operation.Description = $"{operation.Description} Required role: {role}. Company routes require a matching X-Company-Id header. Archived companies are read-only.".Trim();
            operation.Responses ??= new OpenApiResponses();
            operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Authentication required or token invalid." });
            operation.Responses.TryAdd("403", new OpenApiResponse { Description = "Company membership or required role missing." });
            operation.Responses.TryAdd("404", new OpenApiResponse { Description = "Resource not found in this company." });
            operation.Responses.TryAdd("409", new OpenApiResponse { Description = "State, capacity or archived-company conflict." });
            return Task.CompletedTask;
        });
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Version = Version;
            document.Info.Description = "Demonstration API: results have no fiscal or legal validity. The current API version is conveyed by info.version and the x-api-version response header. A future breaking contract will introduce a /v2 path prefix.";
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" };
            document.Tags ??= new HashSet<OpenApiTag>();
            var tags = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (path, item) in document.Paths)
            {
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var tag = parts.Length > 2 && parts[0] == "companies" ? parts[2] : parts.FirstOrDefault() ?? "API";
                if (tags.Add(tag)) document.Tags.Add(new OpenApiTag { Name = tag });
                if (item.Operations is null) continue;
                foreach (var operation in item.Operations.Values)
                {
                    operation.Tags = new HashSet<OpenApiTagReference> { new(tag, document) };
                    operation.Security = operation.Description?.Contains("Required role: anonymous", StringComparison.Ordinal) == true
                        ? [] : [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] }];
                }
            }
            return Task.CompletedTask;
        });
    });
}
