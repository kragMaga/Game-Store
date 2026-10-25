using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GameStore.Api.Data;

// Migration tooling needs the model, not SMTP credentials, JWT secrets, or a live host.
public sealed class GameStoreDesignTimeFactory : IDesignTimeDbContextFactory<GameStoreContext>
{
    public GameStoreContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<GameStoreContext>()
        .UseSqlite("Data Source=GameStore.db").Options);
}
