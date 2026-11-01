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
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace Reconciliation.Core.Tenancy;

public sealed record LoginRequest(string Email, string Password);
public sealed record TokenResponse(string AccessToken);
public sealed record LoginUser(Guid Id, string Email, string PasswordHash);

public static class Authentication
{
    public static IServiceCollection AddTenancyAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var signingKeyText = configuration["Jwt:SigningKey"];
        if (string.IsNullOrWhiteSpace(signingKeyText) || Encoding.UTF8.GetByteCount(signingKeyText) < 32 || signingKeyText == "synthetic-local-signing-key-at-least-32-bytes")
            throw new InvalidOperationException("Set Jwt__SigningKey to at least 32 bytes.");
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKeyText));
        services.AddSingleton(signingKey);
        services.AddSingleton<IPasswordHasher<string>, PasswordHasher<string>>();
        services.AddSingleton(provider => new DummyPasswordHash(provider.GetRequiredService<IPasswordHasher<string>>()
            .HashPassword("unknown@example.invalid", "synthetic-unused-password")));
        services.AddLoginProtection(configuration);
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
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
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                NameClaimType = JwtRegisteredClaimNames.Sub
            };
        });
        services.AddAuthorizationBuilder().SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        CompanyAuthorization.AddCompanyAuthorization(services);
        services.AddSingleton<IAuthorizationHandler, PlatformAuthorization>();
        services.AddAuthorizationBuilder().AddPolicy(Policies.PlatformOperator, policy =>
            policy.RequireAuthenticatedUser().AddRequirements(new PlatformOperatorRequirement()));
        return services;
    }

    public static IEndpointRouteBuilder MapAuthentication(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/auth/tokens", async (LoginRequest credentials, NpgsqlDataSource dataSource,
            IPasswordHasher<string> passwordHasher, DummyPasswordHash dummyHash, SymmetricSecurityKey signingKey, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(credentials.Email) || string.IsNullOrEmpty(credentials.Password)) return Results.Unauthorized();
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            var account = await connection.QuerySingleOrDefaultAsync<LoginUser>(new CommandDefinition(
                "SELECT id, email, password_hash AS PasswordHash FROM app_user WHERE lower(email) = lower(@Email)", credentials, cancellationToken: cancellationToken));
            // Perform the slow verification even for an unknown email.
            var hashToCompare = account?.PasswordHash ?? dummyHash.Value;
            var verification = passwordHasher.VerifyHashedPassword(credentials.Email, hashToCompare, credentials.Password);
            if (account is null || verification == PasswordVerificationResult.Failed) return Results.Unauthorized();
            var accessToken = new JwtSecurityToken(JwtTokenPolicy.TokenIssuer, JwtTokenPolicy.TokenAudience,
                [new Claim(JwtRegisteredClaimNames.Sub, account.Id.ToString())],
                notBefore: DateTime.UtcNow, expires: DateTime.UtcNow.Add(JwtTokenPolicy.TokenLifetime),
                signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));
            return Results.Ok(new TokenResponse(new JwtSecurityTokenHandler().WriteToken(accessToken)));
        }).AllowAnonymous();
        return routes;
    }
}
