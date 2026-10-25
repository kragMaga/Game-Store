using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using GameStore.Api.Data;
using GameStore.Api.Dtos;
using GameStore.Api.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace GameStore.Api.Services;

// MFA owns challenge completion, authenticator setup, and recovery-code changes.
// Identity updates and revocation still commit together on the shared context.
public sealed class MfaService(
    GameStoreContext dbContext,
    UserManager<User> userManager,
    AuthSessionService sessions,
    ILogger<MfaService> logger)
{
    public async Task<AuthTokensDto?> CompleteMfaLoginAsync(string challengeToken, string code)
    {
        ClaimsPrincipal principal;
        try
        {
            var handler = new JwtSecurityTokenHandler();
            principal = handler.ValidateToken(challengeToken, sessions.GetJwtValidationParameters(), out _);
        }
        catch (Exception exception) when (exception is SecurityTokenException or ArgumentException)
        {
            return null;
        }

        var challengeId = principal.FindFirstValue("mfa_challenge_id");
        // Bind the second-factor step to the account state at password verification.
        // A password reset or another stamp change makes that earlier proof stale.
        var challengeStamp = principal.FindFirstValue("security_stamp");
        if (principal.FindFirstValue("mfa_challenge") != "true" ||
            string.IsNullOrWhiteSpace(challengeId) ||
            string.IsNullOrWhiteSpace(challengeStamp) ||
            !int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return null;
        }

        await dbContext.Database.OpenConnectionAsync();
        var sqliteConnection = (SqliteConnection)dbContext.Database.GetDbConnection();
        await using var sqliteTransaction = sqliteConnection.BeginTransaction(deferred: false);
        await using var transaction = await dbContext.Database.UseTransactionAsync(sqliteTransaction)
            ?? throw new InvalidOperationException("Could not start the MFA transaction.");

        try
        {
            var user = await userManager.FindByIdAsync(userId.ToString());
            var storedChallengeHash = user is null
                ? null
                : await userManager.GetAuthenticationTokenAsync(user, "GameStore", "MfaLoginChallenge");
            if (user is null ||
                !string.Equals(user.SecurityStamp, challengeStamp, StringComparison.Ordinal) ||
                user.LockoutEnd > DateTimeOffset.UtcNow ||
                (!user.EmailConfirmed && !string.IsNullOrWhiteSpace(user.Email)) ||
                !await userManager.GetTwoFactorEnabledAsync(user) ||
                !string.Equals(storedChallengeHash, AuthSessionService.HashRefreshToken(challengeId), StringComparison.Ordinal))
            {
                return null;
            }

            var normalizedCode = code.Replace(" ", string.Empty).Replace("-", string.Empty);
            var validCode = await userManager.VerifyTwoFactorTokenAsync(
                user,
                TokenOptions.DefaultAuthenticatorProvider,
                normalizedCode);
            if (!validCode)
            {
                var recoveryResult = await userManager.RedeemTwoFactorRecoveryCodeAsync(user, code.Trim());
                if (!recoveryResult.Succeeded)
                {
                    await userManager.AccessFailedAsync(user);
                    await transaction.CommitAsync();
                    return null;
                }
            }

            var removedChallengeCount = await dbContext.Set<IdentityUserToken<int>>()
                .Where(token => token.UserId == user.Id &&
                                token.LoginProvider == "GameStore" &&
                                token.Name == "MfaLoginChallenge" &&
                                token.Value == storedChallengeHash)
                .ExecuteDeleteAsync();
            if (removedChallengeCount != 1)
            {
                await transaction.RollbackAsync();
                return null;
            }

            await userManager.ResetAccessFailedCountAsync(user);
            await transaction.CommitAsync();
            logger.LogInformation("Two-factor authentication succeeded for user ID {UserId}", user.Id);
            return await sessions.IssueTokensAsync(user);
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    public async Task<(string? SharedKey, string? Error)> BeginMfaSetupAsync(int userId, string currentPassword)
    {
        // Roll back key replacement if any later stamp or revocation update fails.
        await using var transaction = await SecurityTransaction.BeginAsync(dbContext);
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null || !await userManager.CheckPasswordAsync(user, currentPassword))
        {
            return (null, "Current password is incorrect.");
        }

        if (await userManager.GetTwoFactorEnabledAsync(user))
        {
            return (null, "Multi-factor authentication is already enabled.");
        }

        SecurityTransaction.RequireSucceeded(await userManager.ResetAuthenticatorKeyAsync(user), "ResetAuthenticatorKey");
        var key = await userManager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Authenticator key could not be created.");
        // The new authenticator secret changes account security. Sign out existing
        // sessions; the user signs in again before confirming setup with a code.
        SecurityTransaction.RequireSucceeded(await userManager.UpdateSecurityStampAsync(user), "UpdateSecurityStamp");
        await sessions.RevokeAllSessionsAsync(userId);
        await transaction.CommitAsync();
        return (key, null);
    }

    public async Task<IReadOnlyList<string>?> EnableMfaAsync(int userId, string code)
    {
        // Enabling MFA and issuing its recovery codes must succeed as one operation.
        await using var transaction = await SecurityTransaction.BeginAsync(dbContext);
        var user = await userManager.FindByIdAsync(userId.ToString());
        // Reject repeated enable requests instead of replacing existing recovery codes.
        if (user is null || await userManager.GetTwoFactorEnabledAsync(user))
        {
            return null;
        }

        var normalizedCode = code.Replace(" ", string.Empty).Replace("-", string.Empty);
        if (!await userManager.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, normalizedCode))
        {
            return null;
        }

        var enabled = await userManager.SetTwoFactorEnabledAsync(user, true);
        if (!enabled.Succeeded)
        {
            return null;
        }

        var recoveryCodes = (await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))?.ToArray();
        // Never commit enabled MFA without the promised recovery codes. The caller
        // only receives these codes after every remaining update has committed.
        if (recoveryCodes is null || recoveryCodes.Length != 10)
            throw new InvalidOperationException("Recovery codes could not be created.");
        SecurityTransaction.RequireSucceeded(await userManager.UpdateSecurityStampAsync(user), "UpdateSecurityStamp");
        await sessions.RevokeAllSessionsAsync(user.Id);
        await transaction.CommitAsync();
        return recoveryCodes;
    }

    public async Task<MfaDisableResult> DisableMfaAsync(int userId, string currentPassword, string code)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        try
        {
            var user = await userManager.FindByIdAsync(userId.ToString());
            if (user is null || !await userManager.CheckPasswordAsync(user, currentPassword) ||
                !await userManager.GetTwoFactorEnabledAsync(user))
            {
                return MfaDisableResult.InvalidCredentials;
            }

            var normalizedCode = code.Replace(" ", string.Empty).Replace("-", string.Empty);
            var validCode = await userManager.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, normalizedCode);
            if (!validCode)
            {
                var recoveryResult = await userManager.RedeemTwoFactorRecoveryCodeAsync(user, code.Trim());
                if (!recoveryResult.Succeeded)
                {
                    return MfaDisableResult.InvalidCredentials;
                }
            }

            if (!CheckMfaDisableStep(await userManager.SetTwoFactorEnabledAsync(user, false), "DisableTwoFactor", userId))
                return MfaDisableResult.Failed;
            if (!CheckMfaDisableStep(await userManager.ResetAuthenticatorKeyAsync(user), "ResetAuthenticatorKey", userId))
                return MfaDisableResult.Failed;
            if (!CheckMfaDisableStep(await userManager.UpdateSecurityStampAsync(user), "UpdateSecurityStamp", userId))
                return MfaDisableResult.Failed;

            await sessions.RevokeAllSessionsAsync(user.Id);
            // Revocation throws on database failure. Verify persisted state before committing.
            if (await dbContext.AuthSessions.AnyAsync(session => session.UserId == userId && session.RevokedAtUtc == null) ||
                await dbContext.RefreshTokens.AnyAsync(token => token.UserId == userId && token.RevokedAtUtc == null))
                return MfaDisableResult.Failed;

            await transaction.CommitAsync();
            logger.LogInformation("Disabled MFA and revoked sessions for user ID {UserId}", userId);
            return MfaDisableResult.Success;
        }
        finally
        {
            // A failed operation may have mutated tracked Identity objects before rollback.
            dbContext.ChangeTracker.Clear();
        }
    }

    private bool CheckMfaDisableStep(IdentityResult result, string step, int userId)
    {
        if (result.Succeeded) return true;
        logger.LogWarning("MFA disable failed at {Step} for user ID {UserId}. Identity error codes: {IdentityErrorCodes}",
            step, userId, string.Join(", ", result.Errors.Select(error => error.Code).Distinct()));
        return false;
    }

}
