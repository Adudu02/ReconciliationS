using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Tenancy;

public sealed class SessionService(NpgsqlDataSource dataSource, IPasswordHasher<string> passwordHasher,
    DummyPasswordHash dummyHash, SymmetricSecurityKey signingKey, SessionOptions options, TimeProvider timeProvider)
{
    public async Task<TokenResponse?> LoginAsync(TokenRequest credentials, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credentials.Email) || string.IsNullOrEmpty(credentials.Password)) return null;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Serialize password verification/session creation with password changes, while allowing foreign-key reads.
        var account = await connection.QuerySingleOrDefaultAsync<LoginAccount>(Command("""
            SELECT id,email,password_hash AS PasswordHash FROM app_user WHERE lower(email)=lower(@Email) FOR NO KEY UPDATE
            """, new { credentials.Email }, transaction, cancellationToken: cancellationToken));
        var hashToCompare = account?.PasswordHash ?? dummyHash.Value;
        var verification = passwordHasher.VerifyHashedPassword(credentials.Email, hashToCompare, credentials.Password);
        if (account is null || verification == PasswordVerificationResult.Failed) return null;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var absoluteExpiresAt = now.AddDays(options.AbsoluteDays);
        var sessionId = await connection.QuerySingleAsync<Guid>(Command("""
            INSERT INTO auth_session(user_id,created_at,last_used_at,absolute_expires_at)
            VALUES(@Id,@now,@now,@absoluteExpiresAt) RETURNING id
            """, new { account.Id, now, absoluteExpiresAt }, transaction, cancellationToken: cancellationToken));
        var refresh = await InsertRefreshAsync(connection, transaction, sessionId, now, absoluteExpiresAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return IssuePair(account.Id, sessionId, refresh.Value, now);
    }

    private async Task<(Guid Id, string Value)> InsertRefreshAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid sessionId, DateTime now, DateTime absoluteExpiresAt, CancellationToken cancellationToken)
    {
        var refreshToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken));
        var id = Guid.NewGuid();
        var expiresAt = now.AddTicks(Math.Min(TimeSpan.FromMinutes(options.IdleMinutes).Ticks, (absoluteExpiresAt - now).Ticks));
        await connection.ExecuteAsync(Command("""
            INSERT INTO refresh_token(id,session_id,token_hash,issued_at,expires_at) VALUES(@id,@sessionId,@tokenHash,@now,@expiresAt)
            """, new { id, sessionId, tokenHash, now, expiresAt }, transaction, cancellationToken: cancellationToken));
        return (id, refreshToken);
    }

    private TokenResponse IssuePair(Guid userId, Guid sessionId, string refreshToken, DateTime now)
    {
        var accessToken = new JwtSecurityToken(JwtTokenPolicy.TokenIssuer, JwtTokenPolicy.TokenAudience,
            [new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()), new Claim("sid", sessionId.ToString())],
            notBefore: now, expires: now.AddMinutes(options.AccessTokenMinutes),
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));
        return new TokenResponse(new JwtSecurityTokenHandler().WriteToken(accessToken), refreshToken, options.AccessTokenMinutes * 60);
    }
}
