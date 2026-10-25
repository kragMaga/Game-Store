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
        if (!UsernameRules.IsValid(username))
        {
            return AccountOperationResult.InvalidUsername;
        }

        await using var transaction = await SecurityTransaction.BeginAsync(dbContext);
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

    public async Task<PagedResponseDto<UserSummaryDto>> GetUsersAsync(int page, int pageSize)
    {
        // Stable ordering prevents users jumping between pages. Bounds are checked
        // at the endpoint before we calculate the database offset.
        var query = dbContext.Users.AsNoTracking();
        var total = await query.CountAsync();
        var items = await query.OrderBy(user => user.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(user => new UserSummaryDto(user.Id, user.UserName, user.Role))
            .ToListAsync();
        return new(items, page, pageSize, total, (int)Math.Ceiling(total / (double)pageSize));
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

    // Lock before counting admins. Concurrent requests must not both see two
    // admins and each demote one, leaving the application with no administrator.
    await using var transaction = await SecurityTransaction.BeginAsync(dbContext);
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
    // Revoke refresh credentials as well as invalidating the old role's access tokens.
    await RevokeSessionsAsync(id);
    await transaction.CommitAsync();

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
    // Share the same database locking strategy as demotion so deletion and
    // demotion requests cannot race past the last-admin check.
    await using var transaction = await SecurityTransaction.BeginAsync(dbContext);
    var user = await dbContext.Users.FindAsync(id);

    if (user is null)
    {
        logger.LogWarning("User deletion failed because user ID {UserId} was not found", id);
        return UserOperationError.UserNotFound;
    }

    // IDs remain stable when usernames change, making them safer for ownership checks.
    if (currentUser.FindFirstValue(ClaimTypes.NameIdentifier) == id.ToString())
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
    await transaction.CommitAsync();

    logger.LogInformation("Deleted user account with ID {UserId}", id);
    return UserOperationError.None;
}}
