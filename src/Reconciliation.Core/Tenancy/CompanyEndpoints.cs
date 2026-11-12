using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Reconciliation.Core.Audit;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Tenancy;

public sealed record CompanyResponse(Guid Id, string Rfc, string Name, DateTime? ArchivedAt);
public sealed record MemberResponse(Guid UserId, string Role);
public sealed record MemberRequest(Guid? UserId, string Role, string? Email = null, string? InitialPassword = null);

public static class CompanyEndpoints
{
    public static IEndpointRouteBuilder MapCompanies(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/companies", async (HttpContext context, CompanyDirectory directory, CancellationToken token) =>
            Results.Ok(await directory.ListAsync(context.User.UserId()!.Value, token))).RequireAuthorization();
        routes.MapGet("/companies/{companyId:guid}", (Guid companyId, CompanyScope companyScope, CancellationToken cancellationToken) =>
            companyScope.ExecuteResultAsync(companyId, async (connection, transaction, transactionCancellationToken) =>
            {
                var workspace = await connection.QuerySingleOrDefaultAsync<CompanyResponse>(Command(
                    "SELECT id, rfc, name, archived_at AS ArchivedAt FROM company", transaction: transaction, cancellationToken: transactionCancellationToken));
                return CompanyScope.OkOrNotFound(workspace);
            }, cancellationToken)).RequireAuthorization(Policies.CompanyMember);

        routes.MapGet("/companies/{companyId:guid}/members", (Guid companyId, CompanyScope companyScope, CancellationToken cancellationToken) =>
            companyScope.ExecuteResultAsync(companyId, async (connection, transaction, transactionCancellationToken) =>
                Results.Ok(await connection.QueryAsync<MemberResponse>(Command(
                    "SELECT user_id AS UserId, role::text AS Role FROM membership", transaction: transaction, cancellationToken: transactionCancellationToken))), cancellationToken))
            .RequireAuthorization(Policies.CompanyMember);

        routes.MapPost("/companies/{companyId:guid}/members", (Guid companyId, MemberRequest membershipChange, HttpContext httpContext, CompanyScope companyScope, IPasswordHasher<string> passwordHasher, CancellationToken cancellationToken) =>
            companyScope.ExecuteWriteResultAsync(companyId, async (connection, transaction, transactionCancellationToken) =>
            {
                if (membershipChange.Role is not (MembershipRoles.Administrator or MembershipRoles.Accountant or MembershipRoles.Viewer))
                    return Results.BadRequest(new { error = "Role must be administrator, accountant or viewer." });
                if ((membershipChange.UserId is null) == (membershipChange.Email is null))
                    return Results.BadRequest(new { error = "Supply exactly one of userId or email." });
                if (membershipChange.Email is not null && !EmailAddressRules.IsValid(membershipChange.Email))
                    return Results.BadRequest(new { error = "A valid email is required." });
                var userId = membershipChange.UserId;
                if (membershipChange.Email is { } email)
                {
                    userId = await connection.QuerySingleOrDefaultAsync<Guid?>(Command(
                        "SELECT id FROM app_user WHERE lower(email)=lower(@email)", new { email }, transaction, cancellationToken: transactionCancellationToken));
                    if (userId is null)
                    {
                        if (membershipChange.InitialPassword is null || membershipChange.InitialPassword.Length < PasswordPolicy.MinimumLength)
                            return Results.BadRequest(new { error = "Initial password requires at least 12 characters." });
                        var hash = passwordHasher.HashPassword(email, membershipChange.InitialPassword);
                        userId = await connection.QuerySingleOrDefaultAsync<Guid?>(Command("""
                            INSERT INTO app_user(email,password_hash) VALUES (@email,@hash)
                            ON CONFLICT (lower(email)) DO NOTHING RETURNING id
                            """, new { email, hash }, transaction, cancellationToken: transactionCancellationToken));
                        if (userId is not null)
                            await AuditLog.WriteAsync(connection, transaction, companyId, httpContext.User.UserId()!.Value,
                                "account.created", null, new { userId }, transactionCancellationToken, [new("user", userId.Value.ToString())]);
                        else
                            userId = await connection.QuerySingleAsync<Guid>(Command(
                                "SELECT id FROM app_user WHERE lower(email)=lower(@email)", new { email }, transaction, cancellationToken: transactionCancellationToken));
                    }
                }
                else if (!await connection.ExecuteScalarAsync<bool>(Command(
                    "SELECT EXISTS(SELECT FROM app_user WHERE id=@userId)", new { userId }, transaction, cancellationToken: transactionCancellationToken)))
                    return Results.BadRequest(new { error = "Unknown user." });
                var member = new { UserId = userId!.Value, membershipChange.Role };
                if (membershipChange.Role != MembershipRoles.Administrator)
                {
                    var wouldRemoveLastAdministrator = await connection.ExecuteScalarAsync<bool>(Command("""
                        SELECT EXISTS(SELECT FROM membership WHERE user_id=@UserId AND role='administrator')
                          AND (SELECT count(*) FROM membership WHERE role='administrator') <= 1
                        """, member, transaction, cancellationToken: transactionCancellationToken));
                    if (wouldRemoveLastAdministrator) return Results.Conflict(new { error = "The company must retain at least one administrator." });
                }
                var before = await connection.QuerySingleOrDefaultAsync<string>(Command("SELECT role::text FROM membership WHERE user_id=@UserId",
                    member, transaction, cancellationToken: transactionCancellationToken));
                if (before == membershipChange.Role) return Results.NoContent();
                await connection.ExecuteAsync(Command("""
                    INSERT INTO membership(company_id, user_id, role) VALUES (@id, @UserId, @Role::membership_role)
                    ON CONFLICT (company_id, user_id) DO UPDATE SET role = excluded.role
                    """, new { id = companyId, member.UserId, member.Role }, transaction, cancellationToken: transactionCancellationToken));
                await AuditLog.WriteAsync(connection, transaction, companyId, httpContext.User.UserId()!.Value,
                    before is null ? "member.added" : "member.changed", before is null ? null : new { role = before },
                    member, transactionCancellationToken, [new("user", member.UserId.ToString())]);
                return Results.NoContent();
            }, cancellationToken)).RequireAuthorization(Policies.Administrator);
        return routes;
    }
}
