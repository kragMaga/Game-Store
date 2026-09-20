using GameStore.Api.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GameStore.Api.Data;

public static class DbInitializer
{
    public static async Task SeedAdminAsync(GameStoreContext dbContext)
    {
        // Check if an admin already exists
        var adminExists = await dbContext.Users.AnyAsync(user => user.Role == "admin");

        if (adminExists)
        {
            return;
        }

        var admin = new User
        {
            Username = "Admin",
            Role = "admin",
            PasswordHash = ""
        };

        var passwordHasher = new PasswordHasher<User>();

        admin.PasswordHash = passwordHasher.HashPassword(admin, "Admin123");

        dbContext.Users.Add(admin);

        await dbContext.SaveChangesAsync();
    }
}