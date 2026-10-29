using Microsoft.EntityFrameworkCore;
namespace TradeNest.Api.Data;

public static class DataExtensions
{
    public static async Task MigrateDbAsync(this WebApplication app)
    {
        // Startup manages schema only. There is no privileged global user seed:
        // account registration and business creation establish tenant ownership.
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<TradeNestContext>().Database.MigrateAsync();
    }
}
