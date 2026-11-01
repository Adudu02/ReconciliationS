using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Reconciliation.Core.Tenancy;

public sealed record CompanyResponse(Guid Id, string Rfc, string Name, DateTime? ArchivedAt);
public sealed record MemberResponse(Guid UserId, string Role);
public sealed record MemberRequest(Guid UserId, string Role);

public static class CompanyEndpoints
{
    public static IEndpointRouteBuilder MapCompanies(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/companies/{id:guid}", (Guid id, TenantContext tenant, CancellationToken ct) =>
            tenant.ExecuteAsync<IResult>(id, async (connection, transaction, token) =>
            {
                var company = await connection.QuerySingleOrDefaultAsync<CompanyResponse>(new CommandDefinition(
                    "SELECT id, rfc, name, archived_at AS ArchivedAt FROM company", transaction: transaction, cancellationToken: token));
                return company is null ? Results.NotFound() : Results.Ok(company);
            }, ct)).RequireAuthorization("company-member");

        routes.MapGet("/companies/{id:guid}/members", (Guid id, TenantContext tenant, CancellationToken ct) =>
            tenant.ExecuteAsync<IResult>(id, async (connection, transaction, token) =>
                Results.Ok(await connection.QueryAsync<MemberResponse>(new CommandDefinition(
                    "SELECT user_id AS UserId, role::text AS Role FROM membership", transaction: transaction, cancellationToken: token))), ct))
            .RequireAuthorization("company-member");

        routes.MapPost("/companies/{id:guid}/members", (Guid id, MemberRequest request, TenantContext tenant, CancellationToken ct) =>
            tenant.ExecuteAsync<IResult>(id, async (connection, transaction, token) =>
            {
                if (request.Role is not ("administrator" or "accountant" or "viewer"))
                    return Results.BadRequest(new { error = "Role must be administrator, accountant or viewer." });
                var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "SELECT EXISTS(SELECT FROM app_user WHERE id = @UserId)", request, transaction, cancellationToken: token));
                if (!exists) return Results.BadRequest(new { error = "Unknown user." });
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO membership(company_id, user_id, role) VALUES (@id, @UserId, @Role::membership_role)
                    ON CONFLICT (company_id, user_id) DO UPDATE SET role = excluded.role
                    """, new { id, request.UserId, request.Role }, transaction, cancellationToken: token));
                return Results.NoContent();
            }, ct)).RequireAuthorization("administrator");
        return routes;
    }
}
