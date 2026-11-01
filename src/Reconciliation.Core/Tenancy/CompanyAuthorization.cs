using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Tenancy;

public sealed record CompanyRoleRequirement(string[] Roles) : IAuthorizationRequirement;

public sealed class CompanyAuthorization(TenantContext companyScope) : AuthorizationHandler<CompanyRoleRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, CompanyRoleRequirement requirement) => AuthorizeCompanyAsync(context, requirement);

    private async Task AuthorizeCompanyAsync(AuthorizationHandlerContext authorizationContext, CompanyRoleRequirement requirement)
    {
        if (authorizationContext.Resource is not HttpContext httpContext ||
            !Guid.TryParse(httpContext.Request.Headers[CompanyHeader.Name], out var companyId) ||
            authorizationContext.User.UserId() is not Guid memberUserId) return;
        if (httpContext.Request.RouteValues.TryGetValue("companyId", out var routeId) &&
            (!Guid.TryParse(routeId?.ToString(), out var routeCompanyId) || routeCompanyId != companyId)) return;
        var memberRole = await companyScope.ExecuteAsync(companyId, (connection, transaction, transactionCancellationToken) =>
            connection.QuerySingleOrDefaultAsync<string>(Command(
                "SELECT role::text FROM membership WHERE user_id = @userId", new { userId = memberUserId }, transaction, cancellationToken: transactionCancellationToken)), httpContext.RequestAborted);
        if (memberRole is not null && requirement.Roles.Contains(memberRole, StringComparer.Ordinal)) authorizationContext.Succeed(requirement);
    }

    public static IServiceCollection AddCompanyAuthorization(IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationHandler, CompanyAuthorization>();
        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.CompanyMember, policy => policy.RequireAuthenticatedUser().AddRequirements(new CompanyRoleRequirement([MembershipRoles.Administrator, MembershipRoles.Accountant, MembershipRoles.Viewer])))
            .AddPolicy(Policies.Administrator, policy => policy.RequireAuthenticatedUser().AddRequirements(new CompanyRoleRequirement([MembershipRoles.Administrator])))
            .AddPolicy(Policies.Accountant, policy => policy.RequireAuthenticatedUser().AddRequirements(new CompanyRoleRequirement([MembershipRoles.Administrator, MembershipRoles.Accountant])));
        return services;
    }
}
