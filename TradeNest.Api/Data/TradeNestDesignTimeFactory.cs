using TradeNest.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TradeNest.Api.Data;

// Migration tooling needs the model, not SMTP credentials, JWT secrets, or a live host.
public sealed class TradeNestDesignTimeFactory : IDesignTimeDbContextFactory<TradeNestContext>
{
    public TradeNestContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<TradeNestContext>()
        .UseSqlite(LegacyCompatibility.DefaultDatabaseConnection).Options);
}
