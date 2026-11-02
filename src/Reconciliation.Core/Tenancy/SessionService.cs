using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using static Reconciliation.Core.Common.DapperExtensions;

namespace Reconciliation.Core.Tenancy;

public sealed record SessionRenewal(int StatusCode, TokenResponse? Pair = null);

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

    private sealed record SessionRow(Guid Id, Guid UserId, DateTime AbsoluteExpiresAt, DateTime? RevokedAt);
    private sealed record RefreshRow(Guid Id, DateTime ExpiresAt, DateTime? RotatedAt);

    public async Task<SessionRenewal> RefreshAsync(string? refreshToken, CancellationToken cancellationToken)
    {
        if (refreshToken is null || refreshToken.Length != 43 || refreshToken.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            return new SessionRenewal(StatusCodes.Status401Unauthorized);
        var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var sessionId = await connection.QuerySingleOrDefaultAsync<Guid?>(Command(
            "SELECT session_id FROM refresh_token WHERE token_hash=@tokenHash", new { tokenHash }, transaction, cancellationToken: cancellationToken));
        if (sessionId is null) return new SessionRenewal(StatusCodes.Status401Unauthorized);
        var session = await connection.QuerySingleOrDefaultAsync<SessionRow>(Command("""
            SELECT id,user_id AS UserId,absolute_expires_at AS AbsoluteExpiresAt,revoked_at AS RevokedAt
            FROM auth_session WHERE id=@sessionId FOR UPDATE
            """, new { sessionId }, transaction, cancellationToken: cancellationToken));
        // Read again after the family lock: another request may have rotated while this one waited.
        var presented = await connection.QuerySingleOrDefaultAsync<RefreshRow>(Command("""
            SELECT id,expires_at AS ExpiresAt,rotated_at AS RotatedAt FROM refresh_token WHERE token_hash=@tokenHash AND session_id=@sessionId
            """, new { tokenHash, sessionId }, transaction, cancellationToken: cancellationToken));
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (session is null || session.RevokedAt.HasValue || session.AbsoluteExpiresAt <= now || presented is null || presented.RotatedAt.HasValue || presented.ExpiresAt <= now)
            return new SessionRenewal(StatusCodes.Status401Unauthorized);
        var replacement = await InsertRefreshAsync(connection, transaction, session.Id, now, session.AbsoluteExpiresAt, cancellationToken);
        var rowsRotated = await connection.ExecuteAsync(Command("""
            UPDATE refresh_token SET rotated_at=@now,replaced_by=@replacementId WHERE id=@presentedId AND rotated_at IS NULL
            """, new { now, replacementId = replacement.Id, presentedId = presented.Id }, transaction, cancellationToken: cancellationToken));
        if (rowsRotated != 1) return new SessionRenewal(StatusCodes.Status409Conflict);
        await connection.ExecuteAsync(Command("UPDATE auth_session SET last_used_at=@now WHERE id=@sessionId", new { now, sessionId }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return new SessionRenewal(StatusCodes.Status200OK, IssuePair(session.UserId, session.Id, replacement.Value, now));
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
            [new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()), new Claim("sid", sessionId.ToString()), new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())],
            notBefore: now, expires: now.AddMinutes(options.AccessTokenMinutes),
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));
        return new TokenResponse(new JwtSecurityTokenHandler().WriteToken(accessToken), refreshToken, options.AccessTokenMinutes * 60);
    }
}
