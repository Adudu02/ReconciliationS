using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Tenancy;

public sealed record RefreshRequest(string? RefreshToken);
public sealed record TokenRequest(string Email, string Password);
public sealed record TokenResponse(string AccessToken, string? RefreshToken, int ExpiresIn);
public sealed record LoginAccount(Guid Id, string Email, string PasswordHash);

public static class Authentication
{
    public static IServiceCollection AddTenancyAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var signingKeyText = configuration["Jwt:SigningKey"];
        if (string.IsNullOrWhiteSpace(signingKeyText) || Encoding.UTF8.GetByteCount(signingKeyText) < 32 || signingKeyText == "synthetic-local-signing-key-at-least-32-bytes")
            throw new InvalidOperationException("Set Jwt__SigningKey to at least 32 bytes.");
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKeyText));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(provider =>
        {
            var options = configuration.GetSection(SessionOptions.SectionName).Get<SessionOptions>() ?? new SessionOptions();
            options.Validate(provider.GetRequiredService<TimeProvider>());
            return options;
        });
        services.AddSingleton(signingKey);
        services.AddSingleton<SessionService>();
        services.AddSingleton<IPasswordHasher<string>, PasswordHasher<string>>();
        services.AddSingleton(provider => new DummyPasswordHash(provider.GetRequiredService<IPasswordHasher<string>>()
            .HashPassword("unknown@example.invalid", "synthetic-unused-password")));
        services.AddLoginProtection(configuration);
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<TimeProvider>((options, timeProvider) =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = signingKey,
                ValidateIssuer = true,
                ValidIssuer = JwtTokenPolicy.TokenIssuer,
                ValidateAudience = true,
                ValidAudience = JwtTokenPolicy.TokenAudience,
                ValidateLifetime = true,
                ClockSkew = JwtTokenPolicy.ClockSkew,
                LifetimeValidator = (notBefore, expires, securityToken, validationParameters) =>
                {
                    var now = timeProvider.GetUtcNow().UtcDateTime;
                    return expires.HasValue && (notBefore is null || notBefore <= expires) &&
                        (notBefore is null || notBefore <= now.Add(validationParameters.ClockSkew)) &&
                        expires > now.Subtract(validationParameters.ClockSkew);
                },
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                NameClaimType = JwtRegisteredClaimNames.Sub
            };
        });
        services.AddAuthorizationBuilder().SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        CompanyRoleHandler.AddCompanyAuthorization(services);
        services.AddSingleton<IAuthorizationHandler, PlatformOperatorHandler>();
        services.AddAuthorizationBuilder().AddPolicy(Policies.PlatformOperator, policy =>
            policy.RequireAuthenticatedUser().AddRequirements(new PlatformOperatorRequirement()));
        return services;
    }

    public static IEndpointRouteBuilder MapAuthentication(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/auth/tokens", async (TokenRequest credentials, SessionService sessions, CancellationToken cancellationToken) =>
        {
            var pair = await sessions.LoginAsync(credentials, cancellationToken);
            return pair is null ? Results.Unauthorized() : Results.Ok(pair);
        }).AllowAnonymous();
        routes.MapPost("/auth/refresh", async (RefreshRequest request, HttpContext httpContext, SessionService sessions, CancellationToken cancellationToken) =>
        {
            var renewed = await sessions.RefreshAsync(request.RefreshToken, httpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
            return renewed.Pair is null ? Results.StatusCode(renewed.StatusCode) : Results.Ok(renewed.Pair);
        }).AllowAnonymous();
        routes.MapPost("/auth/logout", async (HttpContext httpContext, SessionService sessions, CancellationToken cancellationToken) =>
        {
            if (httpContext.User.UserId() is not Guid userId || httpContext.User.SessionId() is not Guid sessionId) return Results.Unauthorized();
            return Results.StatusCode(await sessions.LogoutAsync(userId, sessionId, httpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken));
        }).RequireAuthorization();
        return routes;
    }
}
