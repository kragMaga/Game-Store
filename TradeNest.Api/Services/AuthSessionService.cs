using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using TradeNest.Api.Data;
using TradeNest.Api.Dtos;
using TradeNest.Api.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace TradeNest.Api.Services;

// Owns JWT issuance and the persistent session/refresh-token lifecycle. All
// callers share one scoped DbContext, preserving existing transaction boundaries.
public sealed class AuthSessionService(
    TradeNestContext dbContext,
    UserManager<User> userManager,
    IConfiguration configuration,
    ILogger<AuthSessionService> logger)
{
    internal async Task<AuthTokensDto> IssueTokensAsync(User user)
    {
        var refreshTokenValue = CreateRefreshTokenValue();
        var now = DateTime.UtcNow;
        var sessionId = Guid.NewGuid().ToString("N");
        var absoluteExpiresAtUtc = now.AddDays(configuration.GetValue("Jwt:MaxSessionDays", 7));
        var session = new AuthSession
        {
            Id = sessionId,
            UserId = user.Id,
            User = user,
            CreatedAtUtc = now,
            LastRefreshedAtUtc = now,
            AbsoluteExpiresAtUtc = absoluteExpiresAtUtc
        };
        var refreshToken = CreateRefreshToken(user, refreshTokenValue, sessionId, now, absoluteExpiresAtUtc);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        dbContext.AuthSessions.Add(session);
        await dbContext.SaveChangesAsync();
        dbContext.RefreshTokens.Add(refreshToken);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        logger.LogInformation("User ID {UserId} authenticated successfully", user.Id);
        return CreateAuthTokens(user, refreshTokenValue, now, sessionId);
    }

    public async Task<(RefreshTokenError Error, AuthTokensDto? Tokens)> RefreshAsync(string? refreshTokenValue)
    {
        if (string.IsNullOrWhiteSpace(refreshTokenValue))
        {
            return (RefreshTokenError.Invalid, null);
        }

        var presentedHash = HashRefreshToken(refreshTokenValue);
        await dbContext.Database.OpenConnectionAsync();
        var sqliteConnection = (SqliteConnection)dbContext.Database.GetDbConnection();
        await using var sqliteTransaction = sqliteConnection.BeginTransaction(deferred: false);
        await using var transaction = await dbContext.Database.UseTransactionAsync(sqliteTransaction)
            ?? throw new InvalidOperationException("Could not start the refresh transaction.");

        try
        {
        var currentToken = await dbContext.RefreshTokens
            .SingleOrDefaultAsync(token => token.TokenHash == presentedHash);

        if (currentToken is null)
        {
            logger.LogWarning("Refresh token request rejected because the token is invalid");
            return (RefreshTokenError.Invalid, null);
        }

        var session = await dbContext.AuthSessions
            .Include(candidate => candidate.User)
            .SingleOrDefaultAsync(candidate => candidate.Id == currentToken.SessionId);
        if (session is null)
        {
            logger.LogError("Refresh token references missing session {SessionId}", currentToken.SessionId);
            return (RefreshTokenError.Invalid, null);
        }

        var now = DateTime.UtcNow;
        if (currentToken.RevokedAtUtc.HasValue)
        {
            if (currentToken.ReplacedByTokenHash is not null)
            {
                await dbContext.AuthSessions
                    .Where(candidate => candidate.Id == session.Id && candidate.RevokedAtUtc == null)
                    .ExecuteUpdateAsync(update => update.SetProperty(candidate => candidate.RevokedAtUtc, now));
                await dbContext.RefreshTokens
                    .Where(token => token.SessionId == currentToken.SessionId && token.RevokedAtUtc == null)
                    .ExecuteUpdateAsync(update => update.SetProperty(token => token.RevokedAtUtc, now));
                await transaction.CommitAsync();
                logger.LogWarning("Refresh-token reuse detected for session {SessionId}; that session was revoked", currentToken.SessionId);
            }
            else
            {
                logger.LogWarning("Revoked refresh token rejected for session {SessionId}", currentToken.SessionId);
            }

            return (RefreshTokenError.Invalid, null);
        }

        var idleExpiry = session.LastRefreshedAtUtc.AddMinutes(
            configuration.GetValue("Jwt:RefreshTokenIdleMinutes", 30));
        if (session.User.AccountClosedAtUtc.HasValue || session.RevokedAtUtc.HasValue ||
            currentToken.ExpiresAtUtc <= now ||
            idleExpiry <= now ||
            session.AbsoluteExpiresAtUtc <= now)
        {
            currentToken.RevokedAtUtc = now;
            if (idleExpiry <= now || session.AbsoluteExpiresAtUtc <= now)
            {
                session.RevokedAtUtc = now;
            }
            await dbContext.SaveChangesAsync();
            await transaction.CommitAsync();
            logger.LogWarning("Expired refresh token rejected for session {SessionId}", currentToken.SessionId);
            return (RefreshTokenError.Invalid, null);
        }

        var replacementValue = CreateRefreshTokenValue();
        var replacement = CreateRefreshToken(
            session.User,
            replacementValue,
            session.Id,
            now,
            session.AbsoluteExpiresAtUtc);
        var updatedCount = await dbContext.RefreshTokens
            .Where(token => token.Id == currentToken.Id &&
                            token.RevokedAtUtc == null &&
                            token.ExpiresAtUtc > now &&
                            token.AbsoluteExpiresAtUtc > now)
            .ExecuteUpdateAsync(update => update
                .SetProperty(token => token.RevokedAtUtc, now)
                .SetProperty(token => token.ReplacedByTokenHash, replacement.TokenHash));

        if (updatedCount != 1)
        {
            logger.LogWarning("Concurrent refresh attempt rejected for session {SessionId}", currentToken.SessionId);
            return (RefreshTokenError.Invalid, null);
        }

        var sessionUpdatedCount = await dbContext.AuthSessions
            .Where(candidate => candidate.Id == session.Id &&
                                candidate.RevokedAtUtc == null &&
                                candidate.LastRefreshedAtUtc == session.LastRefreshedAtUtc &&
                                candidate.AbsoluteExpiresAtUtc > now)
            .ExecuteUpdateAsync(update => update.SetProperty(candidate => candidate.LastRefreshedAtUtc, now));

        if (sessionUpdatedCount != 1)
        {
            await transaction.RollbackAsync();
            logger.LogWarning("Concurrent refresh attempt rejected for session {SessionId}", session.Id);
            return (RefreshTokenError.Invalid, null);
        }

        dbContext.RefreshTokens.Add(replacement);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        logger.LogInformation("Refresh token rotated for session {SessionId}", session.Id);
        return (RefreshTokenError.None, CreateAuthTokens(session.User, replacementValue, now, session.Id));
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    internal async Task RevokeAllSessionsAsync(int userId)
    {
        var now = DateTime.UtcNow;
        await dbContext.AuthSessions
            .Where(session => session.UserId == userId && session.RevokedAtUtc == null)
            .ExecuteUpdateAsync(update => update.SetProperty(session => session.RevokedAtUtc, now));
        await dbContext.RefreshTokens
            .Where(refreshToken => refreshToken.UserId == userId && refreshToken.RevokedAtUtc == null)
            .ExecuteUpdateAsync(update => update.SetProperty(refreshToken => refreshToken.RevokedAtUtc, now));
    }

    public async Task RevokeRefreshTokenAsync(string? refreshTokenValue)
    {
        if (string.IsNullOrWhiteSpace(refreshTokenValue))
        {
            return;
        }

        var presentedHash = HashRefreshToken(refreshTokenValue);
        var now = DateTime.UtcNow;
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        var token = await dbContext.RefreshTokens
            .SingleOrDefaultAsync(candidate => candidate.TokenHash == presentedHash);

        if (token is null)
        {
            return;
        }

        var revokedCount = await dbContext.AuthSessions
            .Where(session => session.Id == token.SessionId && session.RevokedAtUtc == null)
            .ExecuteUpdateAsync(update => update.SetProperty(session => session.RevokedAtUtc, now));
        await dbContext.RefreshTokens
            .Where(candidate => candidate.SessionId == token.SessionId && candidate.RevokedAtUtc == null)
            .ExecuteUpdateAsync(update => update.SetProperty(candidate => candidate.RevokedAtUtc, now));
        await transaction.CommitAsync();

        if (revokedCount > 0)
        {
            logger.LogInformation("Refresh token revoked");
        }
    }

    private AuthTokensDto CreateAuthTokens(User user, string refreshToken, DateTime now, string sessionId)
    {
        var accessTokenMinutes = configuration.GetValue("Jwt:AccessTokenMinutes", 15);
        var expiresAt = now.AddMinutes(accessTokenMinutes);
        var accessToken = CreateAccessToken(user, expiresAt, sessionId);
        return new AuthTokensDto(accessToken, refreshToken, "Bearer", accessTokenMinutes * 60);
    }

    private string CreateAccessToken(User user, DateTime expiresAt, string sessionId)
    {
        // Signed claims identify the account and persisted login session.
        // Bearer validation still checks current stamps/revocation in the database;
        // a valid signature alone does not keep a revoked session authorized.
        var jwtKey = configuration["Jwt:Key"]
            ?? throw new InvalidOperationException("JWT key is missing.");

        var jwtIssuer = configuration["Jwt:Issuer"]
            ?? throw new InvalidOperationException(
                "JWT issuer is missing.");

        var jwtAudience = configuration["Jwt:Audience"]
            ?? throw new InvalidOperationException(
                "JWT audience is missing.");

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.UserName ?? string.Empty),

            new Claim("security_stamp", user.SecurityStamp ?? string.Empty),
            new Claim("session_id", sessionId)
        };

        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(jwtKey));

        var credentials = new SigningCredentials(
            key,
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: jwtIssuer,
            audience: jwtAudience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler()
            .WriteToken(token);
    }

    internal async Task<string> CreateMfaChallengeTokenAsync(User user)
    {
        // This short-lived token proves only completion of the first factor.
        // Store its hash so MFA completion can consume the challenge once.
        var challengeId = CreateRefreshTokenValue();
        var storeResult = await userManager.SetAuthenticationTokenAsync(
            user,
            LegacyCompatibility.MfaLoginProvider,
            "MfaLoginChallenge",
            HashRefreshToken(challengeId));
        if (!storeResult.Succeeded)
        {
            throw new InvalidOperationException("Could not persist the MFA challenge.");
        }

        var jwtKey = configuration["Jwt:Key"]
            ?? throw new InvalidOperationException("JWT key is missing.");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: configuration["Jwt:Issuer"],
            audience: configuration["Jwt:Audience"],
            claims:
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim("mfa_challenge", "true"),
                // Completion compares this signed snapshot with the current account stamp.
                new Claim("security_stamp", user.SecurityStamp ?? string.Empty),
                new Claim("mfa_challenge_id", challengeId)
            ],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    internal TokenValidationParameters GetJwtValidationParameters()
    {
        var jwtKey = configuration["Jwt:Key"]
            ?? throw new InvalidOperationException("JWT key is missing.");
        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = configuration["Jwt:Issuer"],
            ValidAudience = configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    }

    private RefreshToken CreateRefreshToken(
        User user,
        string tokenValue,
        string sessionId,
        DateTime createdAtUtc,
        DateTime absoluteExpiresAtUtc)
    {
        var idleExpiresAtUtc = createdAtUtc.AddMinutes(
            configuration.GetValue("Jwt:RefreshTokenIdleMinutes", 30));

        return new RefreshToken
        {
            UserId = user.Id,
            TokenHash = HashRefreshToken(tokenValue),
            SessionId = sessionId,
            CreatedAtUtc = createdAtUtc,
            ExpiresAtUtc = idleExpiresAtUtc < absoluteExpiresAtUtc
                ? idleExpiresAtUtc
                : absoluteExpiresAtUtc,
            AbsoluteExpiresAtUtc = absoluteExpiresAtUtc
        };
    }

    private static string CreateRefreshTokenValue()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    internal static string HashRefreshToken(string tokenValue)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tokenValue)));
    }

}
