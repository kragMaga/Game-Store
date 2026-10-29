using TradeNest.Api.Data;
using TradeNest.Api.Dtos;
using TradeNest.Api.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace TradeNest.Api.Services;

public class UserService(
    TradeNestContext dbContext,
    UserManager<User> userManager,
    ILogger<UserService> logger)
{
    public async Task<AccountOperationResult> UpdateUsernameAsync(int userId, string requestedUsername)
    {
        var username = requestedUsername.Trim();
        if (!UsernameRules.IsValid(username))
        {
            return AccountOperationResult.InvalidUsername;
        }

        await using var transaction = await SecurityTransaction.BeginAsync(dbContext);
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null || user.AccountClosedAtUtc.HasValue)
        {
            return AccountOperationResult.UserNotFound;
        }

        var normalizedUsername = userManager.NormalizeName(username);
        if (await dbContext.Users.AnyAsync(existing =>
                existing.Id != userId && existing.NormalizedUserName == normalizedUsername))
        {
            return AccountOperationResult.UsernameAlreadyInUse;
        }

        user.UserName = username;
        // A new stamp invalidates access tokens containing the previous username.
        user.SecurityStamp = Guid.NewGuid().ToString();
        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            return result.Errors.Any(error => error.Code == "DuplicateUserName")
                ? AccountOperationResult.UsernameAlreadyInUse
                : AccountOperationResult.InvalidUsername;
        }

        // Stamp changes alone do not invalidate refresh credentials. Revoke them
        // too, or an old refresh token could obtain a token with the new stamp.
        await RevokeSessionsAsync(userId);
        await transaction.CommitAsync();
        logger.LogInformation("Updated username and revoked sessions for user ID {UserId}", userId);
        return AccountOperationResult.Success;
    }

    public async Task<AccountOperationResult> ChangePasswordAsync(
        int userId,
        string currentPassword,
        string newPassword)
    {
        // Keep the password update, lockout cleanup, and session revocation together.
        // A failure at any later step must also roll back the new password hash.
        await using var transaction = await SecurityTransaction.BeginAsync(dbContext);
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null || user.AccountClosedAtUtc.HasValue)
        {
            return AccountOperationResult.UserNotFound;
        }

        var result = await userManager.ChangePasswordAsync(user, currentPassword, newPassword);
        if (!result.Succeeded)
        {
            return result.Errors.Any(error => error.Code == "PasswordMismatch")
                ? AccountOperationResult.CurrentPasswordIncorrect
                : AccountOperationResult.PasswordPolicyFailed;
        }

        SecurityTransaction.RequireSucceeded(await userManager.ResetAccessFailedCountAsync(user), "ResetAccessFailedCount");
        SecurityTransaction.RequireSucceeded(await userManager.SetLockoutEndDateAsync(user, null), "ClearLockout");
        await RevokeSessionsAsync(userId);
        await transaction.CommitAsync();
        logger.LogInformation("Changed password and revoked existing sessions for user ID {UserId}", userId);
        return AccountOperationResult.Success;
    }

    private async Task RevokeSessionsAsync(int userId)
    {
        var now = DateTime.UtcNow;
        var sessions = dbContext.AuthSessions.Where(session => session.UserId == userId && session.RevokedAtUtc == null);
        await sessions.ExecuteUpdateAsync(update => update.SetProperty(session => session.RevokedAtUtc, now));
        await dbContext.RefreshTokens
            .Where(token => token.UserId == userId && token.RevokedAtUtc == null)
            .ExecuteUpdateAsync(update => update.SetProperty(token => token.RevokedAtUtc, now));
    }

}
