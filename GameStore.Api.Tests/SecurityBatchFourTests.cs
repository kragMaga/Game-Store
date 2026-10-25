using System.Net;
using System.Net.Http.Json;
using GameStore.Api.Data;
using GameStore.Api.Dtos;
using GameStore.Api.Entities;
using GameStore.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GameStore.Api.Tests;

[Collection("API integration")]
public sealed class SecurityBatchFourTests
{
    [Fact]
    public async Task Missing_frontend_url_emails_token_and_identity_validates_it()
    {
        using var root = new GameStoreWebApplicationFactory();
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
        var db = scope.ServiceProvider.GetRequiredService<GameStoreContext>();
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
    public async Task Admin_user_list_is_paged_ordered_and_rejects_large_offsets()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/auth/login", new { username = "Admin", password = "Bootstrap-Cedar-River-593!" });
        var tokens = (await login.Content.ReadFromJsonAsync<AuthTokensDto>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameStoreContext>();
        db.Users.AddRange(Enumerable.Range(1, 4).Select(index => new User { UserName = "paged-" + index, Role = "customer" }));
        await db.SaveChangesAsync();
        var first = (await client.GetFromJsonAsync<PagedResponseDto<UserSummaryDto>>("/auth/users?pageSize=2"))!;
        var second = (await client.GetFromJsonAsync<PagedResponseDto<UserSummaryDto>>("/auth/users?page=2&pageSize=2"))!;
        Assert.Equal(5, first.TotalItems);
        Assert.Equal(3, first.TotalPages);
        Assert.Equal(2, first.Items.Count);
        Assert.True(first.Items.Max(user => user.Id) < second.Items.Min(user => user.Id));
        using var invalid = await client.GetAsync("/auth/users?page=1000&pageSize=100");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.GetAsync("/auth/users");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [Fact]
    public async Task Cleanup_keeps_active_replay_history_and_recently_retired_sessions()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameStoreContext>();
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
        using var root = new GameStoreWebApplicationFactory();
        using var tuned = root.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            { ["RateLimits:PasswordPermits"] = "1", ["RateLimits:PasswordWindowSeconds"] = "30" })));
        using var defaults = new GameStoreWebApplicationFactory();
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
        using var root = new GameStoreWebApplicationFactory();
        using var factory = root.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })));
        Assert.ThrowsAny<Microsoft.Extensions.Options.OptionsValidationException>(() => factory.CreateClient());
    }

    [Fact]
    public async Task Multi_instance_mode_does_not_reuse_stale_local_catalog_entries()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Deployment:MultipleInstances"] = "true" }).Build();
        using var cache = new CatalogResponseCache(config);
        var databaseValue = 1;
        Assert.Equal(1, await cache.GetOrCreateAsync("same", () => Task.FromResult(databaseValue)));
        databaseValue = 2;
        Assert.Equal(2, await cache.GetOrCreateAsync("same", () => Task.FromResult(databaseValue)));
        Assert.Equal(0, cache.EntryCount);
    }
}
