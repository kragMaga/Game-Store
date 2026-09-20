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

    public async Task<bool> UpdateRoleAsync(int id, UpdateUserRoleDto request, ClaimsPrincipal currentUser)
{
    if (request.Role != "admin" && request.Role != "customer")
    {
        return false;
    }

    var user = await dbContext.Users.FindAsync(id);

    if (user is null)
    {
        return false;
    }

    // Don't allow the last admin to become a customer
    if (user.Role == "admin" && request.Role == "customer")
    {
        var adminCount = await dbContext.Users.CountAsync(user => user.Role == "admin");

        if (adminCount <= 1)
        {
            return false;
        }
    }

    user.Role = request.Role;

    await dbContext.SaveChangesAsync();

    return true;
}
    public async Task<bool> DeleteUserAsync(int id, ClaimsPrincipal currentUser)
{
    var user = await dbContext.Users.FindAsync(id);

    if (user is null)
    {
        return false;
    }

    // Don't allow a user to delete themselves
    var currentUsername = currentUser.Identity?.Name;

    if (user.Username == currentUsername)
    {
        return false;
    }

    // Don't delete the last admin
    if (user.Role == "admin")
    {
        var adminCount = await dbContext.Users.CountAsync(user => user.Role == "admin");

        if (adminCount <= 1)
        {
            return false;
        }
    }

    dbContext.Users.Remove(user);

    await dbContext.SaveChangesAsync();

    return true;
}
}