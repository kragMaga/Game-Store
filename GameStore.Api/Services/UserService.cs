using System.Security.Claims;
using GameStore.Api.Data;
using GameStore.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace GameStore.Api.Services;

public class UserService(GameStoreContext dbContext, ILogger<UserService> logger)
{
    public async Task<List<object>> GetUsersAsync()
    {
        return await dbContext.Users
            .Select(user => (object)new
            {
                user.Id,
                user.Username,
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

    if (user.Username == currentUsername)
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