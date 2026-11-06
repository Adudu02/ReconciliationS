namespace Reconciliation.Core.Tenancy;

public sealed class SessionOptions
{
    public const string SectionName = "Session";
    public int AccessTokenMinutes { get; init; } = 15;
    public int IdleMinutes { get; init; } = 480;
    public int AbsoluteDays { get; init; } = 30;
    public int ReuseGraceSeconds { get; init; } = 10;
    public bool RefreshCookie { get; init; }
    public bool BrowserRefreshCookie { get; init; }
    public string[] AllowedOrigins { get; init; } = [];

    public void Validate(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (AccessTokenMinutes is < 5 or > 30 || IdleMinutes <= 0 || AbsoluteDays <= 0 ||
            AbsoluteDays > (DateTimeOffset.MaxValue - timeProvider.GetUtcNow()).TotalDays || ReuseGraceSeconds < 0 ||
            AllowedOrigins is null || AllowedOrigins.Any(origin => !IsOrigin(origin)))
            throw new InvalidOperationException("Session requires AccessTokenMinutes between 5 and 30, positive representable idle/absolute lifetimes, nonnegative ReuseGraceSeconds and valid HTTP(S) AllowedOrigins without paths.");
    }

    private static bool IsOrigin(string origin) => Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        origin == uri.GetLeftPart(UriPartial.Authority);
}
