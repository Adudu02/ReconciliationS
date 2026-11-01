using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Reconciliation.Core.Tenancy;

public sealed record PlatformOperatorRequirement : IAuthorizationRequirement;

public sealed class PlatformAuthorization(NpgsqlDataSource dataSource) : AuthorizationHandler<PlatformOperatorRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PlatformOperatorRequirement requirement) => AuthorizeOperatorAsync(context, requirement);

    private async Task AuthorizeOperatorAsync(AuthorizationHandlerContext authorizationContext, PlatformOperatorRequirement requirement)
    {
        if (authorizationContext.Resource is not HttpContext httpContext || !Guid.TryParse(authorizationContext.User.FindFirstValue(JwtRegisteredClaimNames.Sub), out var operatorUserId)) return;
        await using var connection = await dataSource.OpenConnectionAsync(httpContext.RequestAborted);
        if (await connection.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT is_operator FROM app_user WHERE id = @userId", new { userId = operatorUserId }, cancellationToken: httpContext.RequestAborted)))
            authorizationContext.Succeed(requirement);
    }
}
