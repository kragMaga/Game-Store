using Microsoft.EntityFrameworkCore;

namespace GameStore.Api.Data;

public static class DataExtensions
{
    public static async Task MigrateDbAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GameStoreContext>();
        await dbContext.Database.MigrateAsync();

        if (!await dbContext.Users.AnyAsync(user => user.Role == "admin"))
        {
                var adminUsername = app.Configuration["BootstrapAdmin:Username"];
                var adminEmail = app.Configuration["BootstrapAdmin:Email"];
                var adminPassword = app.Configuration["BootstrapAdmin:Password"];
                if (string.IsNullOrWhiteSpace(adminUsername) ||
                    string.IsNullOrWhiteSpace(adminEmail) ||
                    string.IsNullOrWhiteSpace(adminPassword))
            {
                throw new InvalidOperationException(
                        "Bootstrap admin credentials are missing. Configure BootstrapAdmin:Username, BootstrapAdmin:Email, and BootstrapAdmin:Password using user secrets or environment variables.");
            }

                await DbInitializer.SeedAdminAsync(dbContext, adminUsername, adminEmail, adminPassword,
                    scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<GameStore.Api.Entities.User>>());
        }
    }
}
