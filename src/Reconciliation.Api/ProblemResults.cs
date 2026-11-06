using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.WebUtilities;
using Reconciliation.Core.Matching;

namespace Reconciliation.Api;

internal sealed class ProblemResults : IEndpointFilter
{
    internal static bool Requested(HttpContext context) => context.Request.GetTypedHeaders().Accept?.Any(value => value.MediaType == "application/problem+json" && value.Quality != 0) == true;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = await next(context);
        if (!Requested(context.HttpContext) || result is ProblemHttpResult || result is not IStatusCodeHttpResult status || status.StatusCode is not >= 400) return result;
        var extensions = new Dictionary<string, object?>();
        string? title = null;
        if (result is IValueHttpResult { Value: { } value })
        {
            var json = JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
            if (json.ValueKind == JsonValueKind.Object)
                foreach (var property in json.EnumerateObject())
                    if (property.Name is "error" or "reason" or "state")
                    {
                        extensions[property.Name] = property.Value;
                        if (property.Name is "error" or "reason" && property.Value.ValueKind == JsonValueKind.String) title = property.Value.GetString();
                    }
        }
        if (status.StatusCode == 400 && context.Arguments.OfType<LinkDecisionRequest>().Any())
            title = "Invalid decision. Use confirm, reject or change; amounts must be positive with at most two decimals, notes at most 500 characters, and payment identifiers provided together. Only change accepts a new invoice or amount.";
        return Results.Problem(statusCode: status.StatusCode, title: title ?? ReasonPhrases.GetReasonPhrase(status.StatusCode.Value), extensions: extensions);
    }
}
