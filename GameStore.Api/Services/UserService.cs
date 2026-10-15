using System.Security.Claims;
using GameStore.Api.Data;
using GameStore.Api.Dtos;
using GameStore.Api.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GameStore.Api.Services;

public class UserService(
    GameStoreContext dbContext,
    UserManager<User> userManager,
    ILogger<UserService> logger)
{
    public async Task<AccountOperationResult> UpdateUsernameAsync(int userId, string requestedUsername)
    {
        var username = requestedUsername.Trim();
        if (!IsAllowedUsername(username))
        {
            return AccountOperationResult.InvalidUsername;
        }

        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
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
        user.SecurityStamp = Guid.NewGuid().ToString();
        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            return result.Errors.Any(error => error.Code == "DuplicateUserName")
                ? AccountOperationResult.UsernameAlreadyInUse
                : AccountOperationResult.InvalidUsername;
        }

        logger.LogInformation("Updated username for user ID {UserId}", userId);
        return AccountOperationResult.Success;
    }

    public async Task<AccountOperationResult> ChangePasswordAsync(
        int userId,
        string currentPassword,
        string newPassword)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
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

        await userManager.ResetAccessFailedCountAsync(user);
        await userManager.SetLockoutEndDateAsync(user, null);
        await RevokeSessionsAsync(userId);
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

    private static bool IsAllowedUsername(string username)
    {
        return username.Length is >= 5 and <= 20 &&
               username.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    }

    public async Task<List<object>> GetUsersAsync()
    {
        return await dbContext.Users
            .Select(user => (object)new
            {
                user.Id,
                Username = user.UserName,
                user.Role
            })
            .ToListAsync();
    }

    public async Task<UserOperationError> UpdateRoleAsync(
    int id,
    UpdateUserRoleDto request)
{
    if (request.Role != "admin" &&
        request.Role != "customer")
    {
        logger.LogWarning("Role update rejected for user ID {UserId}: requested role is invalid", id);
        return UserOperationError.InvalidRole;
    }

    var user = await dbContext.Users.FindAsync(id);

    if (user is null)
    {
        logger.LogWarning("Role update failed because user ID {UserId} was not found", id);
        return UserOperationError.UserNotFound;
    }

    if (user.Role == "admin" &&
        request.Role == "customer")
    {
        var adminCount = await dbContext.Users
            .CountAsync(user => user.Role == "admin");

        if (adminCount <= 1)
        {
            logger.LogWarning("Role update rejected for user ID {UserId}: cannot demote the last admin", id);
            return UserOperationError.LastAdmin;
        }
    }

    var previousRole = user.Role;
    user.Role = request.Role;
    user.SecurityStamp = Guid.NewGuid().ToString();

    await dbContext.SaveChangesAsync();

    logger.LogInformation(
        "Updated role for user ID {UserId} from {PreviousRole} to {NewRole}",
        id,
        previousRole,
        request.Role);
    return UserOperationError.None;
}
   
    public async Task<UserOperationError> DeleteUserAsync(
    int id,
    ClaimsPrincipal currentUser)
{
    var user = await dbContext.Users.FindAsync(id);

    if (user is null)
    {
        logger.LogWarning("User deletion failed because user ID {UserId} was not found", id);
        return UserOperationError.UserNotFound;
    }

    var currentUsername = currentUser.Identity?.Name;

    if (user.UserName == currentUsername)
    {
        logger.LogWarning("User deletion rejected: user ID {UserId} attempted to delete their own account", id);
        return UserOperationError.CannotDeleteSelf;
    }

    if (user.Role == "admin")
    {
        var adminCount = await dbContext.Users
            .CountAsync(user => user.Role == "admin");

        if (adminCount <= 1)
        {
            logger.LogWarning("User deletion rejected for user ID {UserId}: cannot delete the last admin", id);
            return UserOperationError.LastAdmin;
        }
    }

    dbContext.Users.Remove(user);

    await dbContext.SaveChangesAsync();

    logger.LogInformation("Deleted user account with ID {UserId}", id);
    return UserOperationError.None;
}}