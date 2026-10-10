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
            var adminPassword = app.Configuration["BootstrapAdmin:Password"];
            if (string.IsNullOrWhiteSpace(adminPassword))
            {
                throw new InvalidOperationException(
                    "Bootstrap admin password is missing. Configure BootstrapAdmin:Password using user secrets or an environment variable.");
            }

            await DbInitializer.SeedAdminAsync(dbContext, adminPassword);
        }
    }
}