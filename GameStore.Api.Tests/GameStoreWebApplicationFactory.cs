using GameStore.Api.Data;
using GameStore.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace GameStore.Api.Tests;

public sealed class GameStoreWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString = $"Data Source=GameStoreTests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly SqliteConnection _anchorConnection;

    static GameStoreWebApplicationFactory()
    {
        Environment.SetEnvironmentVariable("Jwt__Key", "GameStoreIntegrationTestSigningKey-NotForProduction-12345");
        Environment.SetEnvironmentVariable("Jwt__Issuer", "GameStore.Api");
        Environment.SetEnvironmentVariable("Jwt__Audience", "GameStore.Client");
        Environment.SetEnvironmentVariable("BootstrapAdmin__Password", "Admin123");
        Environment.SetEnvironmentVariable("BootstrapAdmin__Username", "Admin");
        Environment.SetEnvironmentVariable("BootstrapAdmin__Email", "admin@example.test");
    }

    public GameStoreWebApplicationFactory()
    {
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Warning);
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<GameStoreContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<GameStoreContext>>();
            services.RemoveAll<GameStoreContext>();
            services.RemoveAll<IAccountEmailSender>();

            services.AddDbContext<GameStoreContext>(options => options.UseSqlite(_connectionString));
            services.AddSingleton<TestAccountEmailSender>();
            services.AddSingleton<IAccountEmailSender>(provider => provider.GetRequiredService<TestAccountEmailSender>());
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            _anchorConnection.Dispose();
        }
    }
}
