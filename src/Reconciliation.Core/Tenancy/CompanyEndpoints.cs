using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Tenancy;

public sealed record CompanyResponse(Guid Id, string Rfc, string Name, DateTime? ArchivedAt);
public sealed record MemberResponse(Guid UserId, string Role);
public sealed record MemberRequest(Guid UserId, string Role);

public static class CompanyEndpoints
{
    public static IEndpointRouteBuilder MapCompanies(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/companies/{companyId:guid}", (Guid companyId, TenantContext companyScope, CancellationToken cancellationToken) =>
            companyScope.ExecuteResultAsync(companyId, async (connection, transaction, transactionCancellationToken) =>
            {
                var workspace = await connection.QuerySingleOrDefaultAsync<CompanyResponse>(Command(
                    "SELECT id, rfc, name, archived_at AS ArchivedAt FROM company", transaction: transaction, cancellationToken: transactionCancellationToken));
                return TenantContext.OkOrNotFound(workspace);
            }, cancellationToken)).RequireAuthorization(Policies.CompanyMember);

        routes.MapGet("/companies/{companyId:guid}/members", (Guid companyId, TenantContext companyScope, CancellationToken cancellationToken) =>
            companyScope.ExecuteResultAsync(companyId, async (connection, transaction, transactionCancellationToken) =>
                Results.Ok(await connection.QueryAsync<MemberResponse>(Command(
                    "SELECT user_id AS UserId, role::text AS Role FROM membership", transaction: transaction, cancellationToken: transactionCancellationToken))), cancellationToken))
            .RequireAuthorization(Policies.CompanyMember);

        routes.MapPost("/companies/{companyId:guid}/members", (Guid companyId, MemberRequest membershipChange, TenantContext companyScope, CancellationToken cancellationToken) =>
            companyScope.ExecuteWriteResultAsync(companyId, async (connection, transaction, transactionCancellationToken) =>
            {
                if (membershipChange.Role is not (MembershipRoles.Administrator or MembershipRoles.Accountant or MembershipRoles.Viewer))
                    return Results.BadRequest(new { error = "Role must be administrator, accountant or viewer." });
                var userExists = await connection.ExecuteScalarAsync<bool>(Command(
                    "SELECT EXISTS(SELECT FROM app_user WHERE id = @UserId)", membershipChange, transaction, cancellationToken: transactionCancellationToken));
                if (!userExists) return Results.BadRequest(new { error = "Unknown user." });
                if (membershipChange.Role != MembershipRoles.Administrator)
                {
                    var wouldRemoveLastAdministrator = await connection.ExecuteScalarAsync<bool>(Command("""
                        SELECT EXISTS(SELECT FROM membership WHERE user_id=@UserId AND role='administrator')
                          AND (SELECT count(*) FROM membership WHERE role='administrator') <= 1
                        """, membershipChange, transaction, cancellationToken: transactionCancellationToken));
                    if (wouldRemoveLastAdministrator) return Results.Conflict(new { error = "The company must retain at least one administrator." });
                }
                await connection.ExecuteAsync(Command("""
                    INSERT INTO membership(company_id, user_id, role) VALUES (@id, @UserId, @Role::membership_role)
                    ON CONFLICT (company_id, user_id) DO UPDATE SET role = excluded.role
                    """, new { id = companyId, membershipChange.UserId, membershipChange.Role }, transaction, cancellationToken: transactionCancellationToken));
                return Results.NoContent();
            }, cancellationToken)).RequireAuthorization(Policies.Administrator);
        return routes;
    }
}
