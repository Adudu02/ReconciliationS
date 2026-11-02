using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Reconciliation.Core.Tenancy;

public static class ClaimsExtensions
{
    public static Guid? SessionId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue("sid"), out var sessionId) ? sessionId : null;

    public static Guid? UserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId) ? userId : null;
}
