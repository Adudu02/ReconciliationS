using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Reconciliation.Core.Tenancy;

public sealed record PlatformOperatorRequirement : IAuthorizationRequirement;

public sealed class PlatformAuthorization(NpgsqlDataSource source) : AuthorizationHandler<PlatformOperatorRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PlatformOperatorRequirement requirement)
    {
        if (context.Resource is not HttpContext http || !Guid.TryParse(context.User.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId)) return;
        await using var connection = await source.OpenConnectionAsync(http.RequestAborted);
        if (await connection.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT is_operator FROM app_user WHERE id = @userId", new { userId }, cancellationToken: http.RequestAborted)))
            context.Succeed(requirement);
    }
}
