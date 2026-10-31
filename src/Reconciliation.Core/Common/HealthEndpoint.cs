using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Reconciliation.Core.Common;

public sealed record HealthResponse(string Status, string Database);

public static class HealthEndpoint
{
    /// <summary>GET /health: 200 when the database answers, 503 otherwise. Used by the API and the worker.</summary>
    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/health", async (IDatabaseHealth database, CancellationToken cancellationToken) =>
            await database.IsReachableAsync(cancellationToken)
                ? Results.Ok(new HealthResponse("ok", "up"))
                : Results.Json(new HealthResponse("unavailable", "down"), statusCode: StatusCodes.Status503ServiceUnavailable)).AllowAnonymous();
        return routes;
    }
}
