using GameStore.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GameStore.Api.Tests;

public sealed class GameStoreWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    static GameStoreWebApplicationFactory()
    {
        Environment.SetEnvironmentVariable("Jwt__Key", "GameStoreIntegrationTestSigningKey-NotForProduction-12345");
        Environment.SetEnvironmentVariable("Jwt__Issuer", "GameStore.Api");
        Environment.SetEnvironmentVariable("Jwt__Audience", "GameStore.Client");
        Environment.SetEnvironmentVariable("BootstrapAdmin__Password", "Admin123");
    }

    public GameStoreWebApplicationFactory()
    {
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<GameStoreContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<GameStoreContext>>();
            services.RemoveAll<GameStoreContext>();

            services.AddSingleton(_connection);
            services.AddDbContext<GameStoreContext>(options => options.UseSqlite(_connection));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            _connection.Dispose();
        }
    }
}
