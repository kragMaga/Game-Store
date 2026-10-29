using TradeNest.Api.Data;
using TradeNest.Api.Services;
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

namespace TradeNest.Api.Tests;

public sealed class TradeNestWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString = $"Data Source=TradeNestTests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly SqliteConnection _anchorConnection;
    private readonly bool _seedTestAccount;

    public TradeNestWebApplicationFactory(bool seedTestAccount = true)
    {
        _seedTestAccount = seedTestAccount;
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Host settings are available to Program's early configuration reads.
        // Per-factory settings never mutate process-wide environment variables.
        builder.UseEnvironment("Development");
        builder.UseSetting("LowStock:Enabled", "false");
        builder.UseSetting("Jwt:Key", "TradeNestIntegrationTestSigningKey-NotForProduction-12345");
        builder.UseSetting("Jwt:Issuer", "TradeNest.Api");
        builder.UseSetting("Jwt:Audience", "TradeNest.Client");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Email:ConfirmationPageUrl"] = "https://tradenest.example.test/confirm-email",
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
                ["RateLimits:RecoveryWindowSeconds"] = "900",
                ["RateLimits:InventoryWritePermits"] = "120",
                ["RateLimits:InventoryWriteWindowSeconds"] = "60"
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
            services.RemoveAll<DbContextOptions<TradeNestContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<TradeNestContext>>();
            services.RemoveAll<TradeNestContext>();
            services.RemoveAll<IAccountEmailSender>();

            services.AddDbContext<TradeNestContext>(options => options.UseSqlite(_connectionString));
            // Seed only the test host. Production startup creates no global admin.
            services.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter>(_ => new TestAccountInitializer(_seedTestAccount));
            services.AddSingleton<TestAccountEmailSender>();
            services.AddSingleton<IAccountEmailSender>(provider => provider.GetRequiredService<TestAccountEmailSender>());
            // Keep integration tests independent of the external breach service.
            services.AddHttpClient("PwnedPasswords")
                .ConfigurePrimaryHttpMessageHandler(() => new CleanPasswordRangeHandler());
        });
    }

    private sealed class TestAccountInitializer(bool enabled) : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
        {
            using var scope = app.ApplicationServices.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<TradeNest.Api.Entities.User>>();
            if (enabled && users.FindByNameAsync("Admin").GetAwaiter().GetResult() is null)
            {
                var result = users.CreateAsync(new TradeNest.Api.Entities.User { UserName = "Admin", Email = "admin@example.test", EmailConfirmed = true },
                    "Bootstrap-Cedar-River-593!").GetAwaiter().GetResult();
                if (!result.Succeeded) throw new InvalidOperationException("Test account setup failed.");
            }
            next(app);
        };
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
