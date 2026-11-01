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
        var key = configuration["Jwt:SigningKey"];
        if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32)
            throw new InvalidOperationException("Set Jwt__SigningKey to at least 32 bytes.");
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
        services.AddSingleton(signingKey);
        services.AddSingleton<IPasswordHasher<string>, PasswordHasher<string>>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = signingKey,
                ValidateIssuer = true,
                ValidIssuer = "reconciliation",
                ValidateAudience = true,
                ValidAudience = "reconciliation",
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                NameClaimType = JwtRegisteredClaimNames.Sub
            };
        });
        services.AddAuthorizationBuilder().SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        CompanyAuthorization.AddCompanyAuthorization(services);
        return services;
    }

    public static IEndpointRouteBuilder MapAuthentication(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/auth/tokens", async (LoginRequest request, NpgsqlDataSource source,
            IPasswordHasher<string> hasher, SymmetricSecurityKey signingKey, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password)) return Results.Unauthorized();
            await using var connection = await source.OpenConnectionAsync(ct);
            var user = await connection.QuerySingleOrDefaultAsync<LoginUser>(new CommandDefinition(
                "SELECT id, email, password_hash AS PasswordHash FROM app_user WHERE lower(email) = lower(@Email)", request, cancellationToken: ct));
            // Perform the slow verification even for an unknown email.
            var hash = user?.PasswordHash ?? hasher.HashPassword(request.Email, "synthetic-unused-password");
            var result = hasher.VerifyHashedPassword(request.Email, hash, request.Password);
            if (user is null || result == PasswordVerificationResult.Failed) return Results.Unauthorized();
            var token = new JwtSecurityToken("reconciliation", "reconciliation",
                [new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString())],
                notBefore: DateTime.UtcNow, expires: DateTime.UtcNow.AddHours(1),
                signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));
            return Results.Ok(new TokenResponse(new JwtSecurityTokenHandler().WriteToken(token)));
        }).AllowAnonymous();
        return routes;
    }
}
