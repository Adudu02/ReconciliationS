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
        var emailPermitLimit = configuration.GetValue("LoginRateLimit:EmailPermitLimit", 10);
        var ipPermitLimit = configuration.GetValue("LoginRateLimit:IpPermitLimit", 30);
        var windowSeconds = configuration.GetValue("LoginRateLimit:WindowSeconds", 60);
        if (emailPermitLimit <= 0 || ipPermitLimit <= 0 || windowSeconds <= 0)
            throw new InvalidOperationException("LoginRateLimit permit limits and WindowSeconds must be positive.");
        services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(httpContext => IsLogin(httpContext)
                    ? RateLimitPartition.GetFixedWindowLimiter(httpContext.Items["LoginEmail"]?.ToString() ?? "", _ => Window(emailPermitLimit, windowSeconds))
                    : RateLimitPartition.GetNoLimiter("not-login")),
                PartitionedRateLimiter.Create<HttpContext, string>(httpContext => IsLogin(httpContext)
                    ? RateLimitPartition.GetFixedWindowLimiter(httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => Window(ipPermitLimit, windowSeconds))
                    : RateLimitPartition.GetNoLimiter("not-login")));
            options.OnRejected = async (rejectionContext, cancellationToken) =>
            {
                rejectionContext.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                var retryAfterSeconds = rejectionContext.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? Math.Ceiling(retryAfter.TotalSeconds) : windowSeconds;
                rejectionContext.HttpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                await rejectionContext.HttpContext.Response.WriteAsJsonAsync(new { error = "Too many login attempts; retry later." }, cancellationToken);
            };
        });
        return services;
    }

    public static WebApplication UseLoginProtection(this WebApplication application)
    {
        application.Use(async (httpContext, next) =>
        {
            if (IsLogin(httpContext))
            {
                if (httpContext.Request.ContentLength > 8192) { httpContext.Response.StatusCode = StatusCodes.Status413PayloadTooLarge; return; }
                httpContext.Request.EnableBuffering(8192, 8192);
                try
                {
                    var credentials = await httpContext.Request.ReadFromJsonAsync<TokenRequest>(httpContext.RequestAborted);
                    httpContext.Items["LoginEmail"] = credentials?.Email?.Trim().ToUpperInvariant() ?? "";
                }
                catch (JsonException) { httpContext.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
                catch (IOException) { httpContext.Response.StatusCode = StatusCodes.Status413PayloadTooLarge; return; }
                finally { httpContext.Request.Body.Position = 0; }
            }
            await next(httpContext);
        });
        application.UseRateLimiter();
        return application;
    }

    private static bool IsLogin(HttpContext httpContext) => HttpMethods.IsPost(httpContext.Request.Method) && httpContext.Request.Path == "/auth/tokens";
    private static FixedWindowRateLimiterOptions Window(int permitLimit, int windowSeconds) => new()
    { PermitLimit = permitLimit, Window = TimeSpan.FromSeconds(windowSeconds), QueueLimit = 0, AutoReplenishment = true };
}
