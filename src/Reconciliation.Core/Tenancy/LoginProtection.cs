using System.Globalization;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Reconciliation.Core.Tenancy;

public sealed record DummyPasswordHash(string Value);

public static class LoginProtection
{
    public static IServiceCollection AddLoginProtection(this IServiceCollection services, IConfiguration configuration)
    {
        var emailLimit = configuration.GetValue("LoginRateLimit:EmailPermitLimit", 10);
        var ipLimit = configuration.GetValue("LoginRateLimit:IpPermitLimit", 30);
        var windowSeconds = configuration.GetValue("LoginRateLimit:WindowSeconds", 60);
        if (emailLimit <= 0 || ipLimit <= 0 || windowSeconds <= 0)
            throw new InvalidOperationException("LoginRateLimit permit limits and WindowSeconds must be positive.");
        services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(http => IsLogin(http)
                    ? RateLimitPartition.GetFixedWindowLimiter(http.Items["LoginEmail"]?.ToString() ?? "", _ => Window(emailLimit, windowSeconds))
                    : RateLimitPartition.GetNoLimiter("not-login")),
                PartitionedRateLimiter.Create<HttpContext, string>(http => IsLogin(http)
                    ? RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => Window(ipLimit, windowSeconds))
                    : RateLimitPartition.GetNoLimiter("not-login")));
            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) ? Math.Ceiling(retry.TotalSeconds) : windowSeconds;
                context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                await context.HttpContext.Response.WriteAsJsonAsync(new { error = "Too many login attempts; retry later." }, cancellationToken);
            };
        });
        return services;
    }

    public static WebApplication UseLoginProtection(this WebApplication app)
    {
        app.Use(async (http, next) =>
        {
            if (IsLogin(http))
            {
                if (http.Request.ContentLength > 8192) { http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge; return; }
                http.Request.EnableBuffering(8192, 8192);
                try
                {
                    var credentials = await http.Request.ReadFromJsonAsync<LoginRequest>(http.RequestAborted);
                    http.Items["LoginEmail"] = credentials?.Email?.Trim().ToUpperInvariant() ?? "";
                }
                catch (JsonException) { http.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
                catch (IOException) { http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge; return; }
                finally { http.Request.Body.Position = 0; }
            }
            await next(http);
        });
        app.UseRateLimiter();
        return app;
    }

    private static bool IsLogin(HttpContext http) => HttpMethods.IsPost(http.Request.Method) && http.Request.Path == "/auth/tokens";
    private static FixedWindowRateLimiterOptions Window(int permits, int seconds) => new()
    { PermitLimit = permits, Window = TimeSpan.FromSeconds(seconds), QueueLimit = 0, AutoReplenishment = true };
}
