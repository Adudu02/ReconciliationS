namespace Reconciliation.Core.Tenancy;

public static class JwtTokenPolicy
{
    public const string TokenIssuer = "reconciliation";
    public const string TokenAudience = "reconciliation";
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(1);
}
