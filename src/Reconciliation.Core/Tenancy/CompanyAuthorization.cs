using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Reconciliation.Core.Tenancy;

public sealed record CompanyRoleRequirement(string[] Roles) : IAuthorizationRequirement;

public sealed class CompanyAuthorization(TenantContext tenant) : AuthorizationHandler<CompanyRoleRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, CompanyRoleRequirement requirement)
    {
        if (context.Resource is not HttpContext http ||
            !Guid.TryParse(http.Request.Headers["X-Company-Id"], out var companyId) ||
            !Guid.TryParse(context.User.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId)) return;
        if (http.Request.RouteValues.TryGetValue("id", out var routeId) &&
            (!Guid.TryParse(routeId?.ToString(), out var id) || id != companyId)) return;
        var role = await tenant.ExecuteAsync(companyId, (connection, transaction, ct) =>
            connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                "SELECT role::text FROM membership WHERE user_id = @userId", new { userId }, transaction, cancellationToken: ct)), http.RequestAborted);
        if (role is not null && requirement.Roles.Contains(role, StringComparer.Ordinal)) context.Succeed(requirement);
    }

    public static IServiceCollection AddCompanyAuthorization(IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationHandler, CompanyAuthorization>();
        services.AddAuthorizationBuilder()
            .AddPolicy("company-member", policy => policy.RequireAuthenticatedUser().AddRequirements(new CompanyRoleRequirement(["administrator", "accountant", "viewer"])))
            .AddPolicy("administrator", policy => policy.RequireAuthenticatedUser().AddRequirements(new CompanyRoleRequirement(["administrator"])))
            .AddPolicy("accountant", policy => policy.RequireAuthenticatedUser().AddRequirements(new CompanyRoleRequirement(["administrator", "accountant"])));
        return services;
    }
}
