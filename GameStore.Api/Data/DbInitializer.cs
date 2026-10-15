using GameStore.Api.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GameStore.Api.Data;

public static class DbInitializer
{
    public static async Task SeedAdminAsync(
        GameStoreContext dbContext,
        string adminUsername,
        string adminEmail,
        string adminPassword)
    {
        var username = adminUsername.Trim();
        if (username.Length is < 5 or > 20 ||
            !username.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
        {
            throw new InvalidOperationException("Bootstrap admin username must be 5-20 ASCII letters, digits, periods, underscores, or hyphens.");
        }

        var email = adminEmail.Trim();
        if (!new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email))
        {
            throw new InvalidOperationException("Bootstrap admin email must be a valid email address.");
        }

        // Check if an admin already exists
        var adminExists = await dbContext.Users.AnyAsync(user => user.Role == "admin");

        if (adminExists)
        {
            return;
        }

        var normalizedUsername = adminUsername.Trim().ToUpperInvariant();
        var admin = new User
        {
            UserName = adminUsername.Trim(),
            NormalizedUserName = normalizedUsername,
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            Role = "admin",
            PasswordHash = "",
            SecurityStamp = Guid.NewGuid().ToString(),
            LockoutEnabled = true,
            EmailConfirmed = true
        };

        var passwordHasher = new PasswordHasher<User>();

        admin.PasswordHash = passwordHasher.HashPassword(admin, adminPassword);

        dbContext.Users.Add(admin);

        await dbContext.SaveChangesAsync();
    }
}