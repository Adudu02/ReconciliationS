using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Reconciliation.Core.Tenancy;

public sealed record CompanyResponse(Guid Id, string Rfc, string Name, DateTimeOffset? ArchivedAt);

public static class CompanyEndpoints
{
    public static IEndpointRouteBuilder MapCompanies(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/companies/{id:guid}", async (Guid id, HttpContext http, TenantContext tenant, CancellationToken ct) =>
        {
            if (!Guid.TryParse(http.Request.Headers["X-Company-Id"], out var selected) || selected != id)
                return Results.Forbid();
            var user = http.User.FindFirstValue(JwtRegisteredClaimNames.Sub);
            return await tenant.ExecuteAsync<IResult>(id, async (connection, transaction, token) =>
            {
                var company = await connection.QuerySingleOrDefaultAsync<CompanyResponse>(new CommandDefinition("""
                    SELECT id, rfc, name, archived_at AS ArchivedAt FROM company
                    WHERE EXISTS (SELECT 1 FROM membership WHERE user_id = @user::uuid)
                    """, new { user }, transaction, cancellationToken: token));
                return company is null ? Results.NotFound() : Results.Ok(company);
            }, ct);
        }).RequireAuthorization();
        return routes;
    }
}
