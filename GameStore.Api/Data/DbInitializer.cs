using GameStore.Api.Entities;
using GameStore.Api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GameStore.Api.Data;

public static class DbInitializer
{
    public static async Task SeedAdminAsync(
        GameStoreContext dbContext,
        string adminUsername,
        string adminEmail,
        string adminPassword,
        UserManager<User> userManager)
    {
        var username = adminUsername.Trim();
        if (!UsernameRules.IsValid(username))
        {
            throw new InvalidOperationException("Bootstrap admin username must be 5-20 ASCII letters, digits, periods, underscores, or hyphens.");
        }

        var email = adminEmail.Trim();
        if (!new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email))
        {
            throw new InvalidOperationException("Bootstrap admin email must be a valid email address.");
        }

        // Serialize first-admin creation and run the same Identity/password validators
        // used by registration. Direct hashing would bypass strength and breach checks.
        await using var transaction = await SecurityTransaction.BeginAsync(dbContext);
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

        var result = await userManager.CreateAsync(admin, adminPassword);
        if (!result.Succeeded)
            throw new InvalidOperationException("Bootstrap admin validation failed. Identity error codes: " +
                string.Join(", ", result.Errors.Select(error => error.Code).Distinct()));
        await transaction.CommitAsync();
    }
}
