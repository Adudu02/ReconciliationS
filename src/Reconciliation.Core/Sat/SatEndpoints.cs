using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Reconciliation.Core.Jobs;
using Reconciliation.Core.Tenancy;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Sat;

public sealed record SatCheckRequest(Guid? AnonymizedUuid);
public sealed record SatStatusView(string State, string? Cancellability, string? CancellationStatus, string? EfosRaw,
    DateTime? CheckedAt, DateTime? ExpiresAt, string? LastError, int? Attempts, string? Source, bool EfosFlagged,
    string? CodeRaw, string? StateRaw, string? CancellabilityRaw, string? CancellationStatusRaw);

public static class SatEndpoints
{
    public static IEndpointRouteBuilder MapSat(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/companies/{companyId:guid}/cfdi/{uuid:guid}/sat-check", async (Guid companyId, Guid uuid, HttpContext http,
            CompanyScope scope, JobOptions jobs, SatOptions options, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            SatCheckRequest? request = null;
            if (http.Request.ContentLength is > 0 || http.Request.Headers.ContainsKey("Transfer-Encoding"))
            {
                try { request = await http.Request.ReadFromJsonAsync<SatCheckRequest>(cancellationToken); }
                catch (JsonException) { return Results.BadRequest(new { error = "Invalid anonymizedUuid." }); }
            }
            if (request?.AnonymizedUuid == Guid.Empty || request?.AnonymizedUuid == uuid)
                return Results.BadRequest(new { error = "Anonymized UUID must be nonempty and different from the CFDI UUID." });
            return await scope.ExecuteWriteResultAsync(companyId, async (connection, transaction, token) =>
            {
                if (request?.AnonymizedUuid is not null && !await connection.ExecuteScalarAsync<bool>(Command(
                    "SELECT EXISTS(SELECT FROM membership WHERE user_id=@actor AND role='administrator')", new { actor = http.User.UserId() }, transaction, token)))
                    return Results.Forbid();
                var id = await connection.QuerySingleOrDefaultAsync<Guid?>(Command("SELECT id FROM cfdi WHERE uuid=@uuid", new { uuid }, transaction, token));
                if (id is null) return Results.NotFound();
                var jobId = await JobStore.EnqueueAsync(connection, transaction, companyId, http.User.UserId()!.Value, "status_check",
                    JsonSerializer.Serialize(new SatCheckPayload([id.Value], request?.AnonymizedUuid), JsonSerializerOptions.Web),
                    null, jobs.MaxAttempts, clock.GetUtcNow(), token, 1);
                return Results.Accepted($"/companies/{companyId}/jobs/{jobId}", new { jobId });
            }, cancellationToken);
        }).RequireAuthorization(Policies.Accountant);
        return routes;
    }
}
