using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Reconciliation.Core.Tenancy;

public static class ClaimsExtensions
{
    public static Guid? UserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId) ? userId : null;
}
