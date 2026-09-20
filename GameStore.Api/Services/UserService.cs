using System.Security.Claims;
using GameStore.Api.Data;
using GameStore.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace GameStore.Api.Services;

public class UserService(GameStoreContext dbContext)
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
        return UserOperationError.InvalidRole;
    }

    var user = await dbContext.Users.FindAsync(id);

    if (user is null)
    {
        return UserOperationError.UserNotFound;
    }

    if (user.Role == "admin" &&
        request.Role == "customer")
    {
        var adminCount = await dbContext.Users
            .CountAsync(user => user.Role == "admin");

        if (adminCount <= 1)
        {
            return UserOperationError.LastAdmin;
        }
    }

    user.Role = request.Role;

    await dbContext.SaveChangesAsync();

    return UserOperationError.None;
}
   
    public async Task<UserOperationError> DeleteUserAsync(
    int id,
    ClaimsPrincipal currentUser)
{
    var user = await dbContext.Users.FindAsync(id);

    if (user is null)
    {
        return UserOperationError.UserNotFound;
    }

    var currentUsername = currentUser.Identity?.Name;

    if (user.Username == currentUsername)
    {
        return UserOperationError.CannotDeleteSelf;
    }

    if (user.Role == "admin")
    {
        var adminCount = await dbContext.Users
            .CountAsync(user => user.Role == "admin");

        if (adminCount <= 1)
        {
            return UserOperationError.LastAdmin;
        }
    }

    dbContext.Users.Remove(user);

    await dbContext.SaveChangesAsync();

    return UserOperationError.None;
}}