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
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.DataProtection;

namespace GameStore.Api.Tests;

public sealed class GameStoreWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString = $"Data Source=GameStoreTests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly SqliteConnection _anchorConnection;

    public GameStoreWebApplicationFactory()
    {
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Host settings are available to Program's early configuration reads.
        // Per-factory settings never mutate process-wide environment variables.
        builder.UseEnvironment("Development");
        builder.UseSetting("Jwt:Key", "GameStoreIntegrationTestSigningKey-NotForProduction-12345");
        builder.UseSetting("Jwt:Issuer", "GameStore.Api");
        builder.UseSetting("Jwt:Audience", "GameStore.Client");
        builder.UseSetting("BootstrapAdmin:Password", "Bootstrap-Cedar-River-593!");
        builder.UseSetting("BootstrapAdmin:Username", "Admin");
        builder.UseSetting("BootstrapAdmin:Email", "admin@example.test");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Email:ConfirmationPageUrl"] = "https://gamestore.example.test/confirm-email",
                // Tests explicitly drain an outbox with a fake transport when needed.
                // Ordinary integration tests must never start a live SMTP delivery loop.
                ["Email:Outbox:Enabled"] = "false",
                ["Auth:CleanupEnabled"] = "false",
                ["Auth:UseRefreshTokenCookies"] = "false",
                ["Deployment:MultipleInstances"] = "false",
                ["RateLimits:BaselinePermits"] = "10",
                ["RateLimits:BaselineWindowSeconds"] = "300",
                ["RateLimits:PasswordPermits"] = "5",
                ["RateLimits:PasswordWindowSeconds"] = "300",
                ["RateLimits:RecoveryPermits"] = "3",
                ["RateLimits:RecoveryWindowSeconds"] = "900"
            }));
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Warning);
        });

        builder.ConfigureServices(services =>
        {
            // Each host gets independent in-memory keys; tests need neither the
            // developer's filesystem key ring nor permissions on that directory.
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.RemoveAll<DbContextOptions<GameStoreContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<GameStoreContext>>();
            services.RemoveAll<GameStoreContext>();
            services.RemoveAll<IAccountEmailSender>();

            services.AddDbContext<GameStoreContext>(options => options.UseSqlite(_connectionString));
            services.AddSingleton<TestAccountEmailSender>();
            services.AddSingleton<IAccountEmailSender>(provider => provider.GetRequiredService<TestAccountEmailSender>());
            // Keep integration tests independent of the external breach service.
            services.AddHttpClient("PwnedPasswords")
                .ConfigurePrimaryHttpMessageHandler(() => new CleanPasswordRangeHandler());
        });
    }

    private sealed class CleanPasswordRangeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(new string('0', 35) + ":0\r\n")
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
