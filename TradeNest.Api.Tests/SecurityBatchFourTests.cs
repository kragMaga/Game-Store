using System.Net;
using System.Net.Http.Json;
using TradeNest.Api.Data;
using TradeNest.Api.Dtos;
using TradeNest.Api.Entities;
using TradeNest.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TradeNest.Api.Tests;

[Collection("API integration")]
public sealed class SecurityBatchFourTests
{
    [Fact]
    public async Task Missing_frontend_url_emails_token_and_identity_validates_it()
    {
        using var root = new TradeNestWebApplicationFactory();
        using var factory = root.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Email:ConfirmationPageUrl"] = null })));
        using var client = factory.CreateClient();
        using var registered = await client.PostAsJsonAsync("/auth/register", new
            { username = "api-only-user", email = "api-only@example.test", password = "Cedar-Moon-River-593!" });
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        var message = Assert.Single(factory.Services.GetRequiredService<TestAccountEmailSender>().SentMessages);
        var token = message.Body.Split("\n\n")[1];
        Assert.Contains("POST to /auth/confirm-email", message.Body);
        using var rejected = await client.PostAsJsonAsync("/auth/confirm-email", new { email = message.Email, token = "invalid" });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var confirmed = await client.PostAsJsonAsync("/auth/confirm-email", new { email = message.Email, token });
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);

        // Email changes need the user ID as well as the new address. Prove the
        // API-only instructions retain the distinct Identity email-change flow.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeNestContext>();
        var user = await db.Users.SingleAsync(item => item.Email == message.Email);
        Assert.Equal(AccountOperationResult.Success, await scope.ServiceProvider.GetRequiredService<AuthService>()
            .UpdateEmailAsync(user.Id, "new-api-only@example.test"));
        var change = factory.Services.GetRequiredService<TestAccountEmailSender>().SentMessages.Last();
        Assert.Contains($"userId: {user.Id}", change.Body);
        Assert.Contains("POST to /auth/email/confirm-change", change.Body);
        using var changed = await client.PostAsJsonAsync("/auth/email/confirm-change",
            new { userId = user.Id, email = change.Email, token = change.Body.Split("\n\n")[1] });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
    }

    [Fact]
    public async Task Cleanup_keeps_active_replay_history_and_recently_retired_sessions()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeNestContext>();
        var user = await db.Users.SingleAsync();
        var now = DateTime.UtcNow;
        db.AuthSessions.AddRange(
            Session("active", now, now.AddDays(1)),
            Session("old", now.AddDays(-20), now.AddDays(-10)),
            Session("recent", now, now.AddDays(1), now.AddDays(-1)),
            Session("idle", now.AddDays(-10), now.AddDays(1)));
        foreach (var id in new[] { "active", "old", "recent", "idle" })
            db.RefreshTokens.Add(new RefreshToken { UserId = user.Id, SessionId = id, TokenHash = id.PadRight(64, '0'),
                CreatedAtUtc = now.AddDays(-20), ExpiresAtUtc = now.AddDays(-1), AbsoluteExpiresAtUtc = now.AddDays(1),
                RevokedAtUtc = now.AddDays(-1), ReplacedByTokenHash = "replacement" });
        await db.SaveChangesAsync();
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<AuthDataCleanup>().RunBatchAsync());
        Assert.Equal(new[] { "active", "recent" }, await db.AuthSessions.OrderBy(row => row.Id).Select(row => row.Id).ToArrayAsync());
        Assert.Equal(new[] { "active", "recent" }, await db.RefreshTokens.OrderBy(row => row.SessionId).Select(row => row.SessionId).ToArrayAsync());
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AuthDataCleanup>().RunBatchAsync());

        AuthSession Session(string id, DateTime refreshed, DateTime expires, DateTime? revoked = null) => new()
        {
            Id = id, UserId = user.Id, CreatedAtUtc = now.AddDays(-20), LastRefreshedAtUtc = refreshed,
            AbsoluteExpiresAtUtc = expires, RevokedAtUtc = revoked
        };
    }

    [Fact]
    public async Task Rate_limit_settings_are_isolated_per_host_and_report_remaining_time()
    {
        using var root = new TradeNestWebApplicationFactory();
        using var tuned = root.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            { ["RateLimits:PasswordPermits"] = "1", ["RateLimits:PasswordWindowSeconds"] = "30" })));
        using var defaults = new TradeNestWebApplicationFactory();
        using var tunedClient = tuned.CreateClient();
        using var defaultClient = defaults.CreateClient();
        var body = new { username = "missing-account", password = "WrongPassword123" };
        using var first = await tunedClient.PostAsJsonAsync("/auth/login", body);
        using var rejected = await tunedClient.PostAsJsonAsync("/auth/login", body);
        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.InRange(rejected.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 30);
        for (var index = 0; index < 2; index++)
        {
            using var allowed = await defaultClient.PostAsJsonAsync("/auth/login", body);
            Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }
    }

    [Theory]
    [InlineData("RateLimits:PasswordPermits", "0")]
    [InlineData("RateLimits:RecoveryWindowSeconds", "-1")]
    [InlineData("Deployment:MultipleInstances", "true")]
    public void Unsafe_configuration_is_rejected(string key, string value)
    {
        using var root = new TradeNestWebApplicationFactory();
        using var factory = root.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })));
        Assert.ThrowsAny<Microsoft.Extensions.Options.OptionsValidationException>(() => factory.CreateClient());
    }

}
