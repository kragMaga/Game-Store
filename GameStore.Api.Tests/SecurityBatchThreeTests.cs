using System.Net;
using System.Net.Http.Json;
using GameStore.Api.Data;
using GameStore.Api.Dtos;
using GameStore.Api.Entities;
using GameStore.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace GameStore.Api.Tests;

[Collection("API integration")]
public sealed class SecurityBatchThreeTests
{
    [Fact]
    public async Task Queued_confirmation_survives_smtp_outage_and_is_retried_without_duplicate_registration()
    {
        var transport = new TestTransport { Fail = true };
        var clock = new TestClock();
        using var root = new GameStoreWebApplicationFactory();
        using var factory = Configure(root, transport, clock);
        using var client = factory.CreateClient();
        using var registered = await client.PostAsJsonAsync("/auth/register", new
            { username = "queued-user", email = "queued@example.test", password = "Cedar-Moon-River-593!" });
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        Assert.Equal(0, transport.Attempts);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameStoreContext>();
            var row = Assert.Single(await db.EmailOutbox.AsNoTracking().ToListAsync());
            Assert.DoesNotContain("queued@example.test", row.ProtectedPayload);
            Assert.DoesNotContain("token=", row.ProtectedPayload);
            Assert.True(await scope.ServiceProvider.GetRequiredService<EmailOutboxProcessor>().ProcessNextAsync(default));
            row = await db.EmailOutbox.AsNoTracking().SingleAsync();
            Assert.Equal("Pending", row.Status);
            Assert.Equal(1, row.Attempts);
            Assert.True(row.NextAttemptAtUtc > clock.GetUtcNow().UtcDateTime);
        }
        transport.Fail = false;
        clock.Advance(TimeSpan.FromMinutes(1));
        using (var scope = factory.Services.CreateScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<EmailOutboxProcessor>().ProcessNextAsync(default));
            var db = scope.ServiceProvider.GetRequiredService<GameStoreContext>();
            var row = await db.EmailOutbox.AsNoTracking().SingleAsync();
            Assert.Equal("Sent", row.Status);
            Assert.Empty(row.ProtectedPayload);
            Assert.Equal(2, transport.Attempts);
            Assert.Equal(1, await db.Users.CountAsync(user => user.Email == "queued@example.test"));
        }
        Assert.Contains("https://gamestore.example.test/confirm-email#", transport.Body);
        var link = new Uri(transport.Body.Split('\n').Single(line => line.StartsWith("https://", StringComparison.Ordinal)));
        var parameters = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(link.Fragment.TrimStart('#'));
        using var confirmed = await client.PostAsJsonAsync("/auth/confirm-email",
            new { email = parameters["email"].ToString(), token = parameters["token"].ToString() });
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
    }

    [Fact]
    public async Task Queue_persistence_failure_rolls_back_account_creation()
    {
        using var root = new GameStoreWebApplicationFactory();
        using var factory = Configure(root, new TestTransport(), new TestClock());
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameStoreContext>();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailQueue BEFORE INSERT ON EmailOutbox BEGIN SELECT RAISE(ABORT, 'Simulated queue failure'); END");
        using var response = await client.PostAsJsonAsync("/auth/register", new
            { username = "rollback-user", email = "rollback@example.test", password = "Cedar-Moon-River-593!" });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(await db.Users.AnyAsync(user => user.Email == "rollback@example.test"));
        Assert.Empty(await db.EmailOutbox.ToListAsync());
    }

    [Fact]
    public async Task Outbox_leases_prevent_parallel_delivery_and_expired_messages_are_not_sent()
    {
        var transport = new TestTransport { Entered = new(), Release = new() };
        var clock = new TestClock();
        using var root = new GameStoreWebApplicationFactory();
        using var factory = Configure(root, transport, clock);
        using var first = factory.Services.CreateScope();
        await first.ServiceProvider.GetRequiredService<IAccountEmailSender>().SendAsync("recipient@example.test", "subject", "secret-token");
        var sending = first.ServiceProvider.GetRequiredService<EmailOutboxProcessor>().ProcessNextAsync(default);
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using (var second = factory.Services.CreateScope())
            Assert.False(await second.ServiceProvider.GetRequiredService<EmailOutboxProcessor>().ProcessNextAsync(default));
        transport.Release.SetResult();
        Assert.True(await sending);
        Assert.Equal(1, transport.Attempts);
        await first.ServiceProvider.GetRequiredService<IAccountEmailSender>().SendAsync("recipient@example.test", "expired", "expired-token");
        clock.Advance(TimeSpan.FromHours(13));
        Assert.False(await first.ServiceProvider.GetRequiredService<EmailOutboxProcessor>().ProcessNextAsync(default));
        var expired = await first.ServiceProvider.GetRequiredService<GameStoreContext>().EmailOutbox.AsNoTracking().SingleAsync(row => row.Status == "Expired");
        Assert.Empty(expired.ProtectedPayload);
        Assert.Equal(1, transport.Attempts);
    }

    [Theory]
    [InlineData("http://example.test/confirm")]
    [InlineData("https://user:password@example.test/confirm")]
    [InlineData("https://example.test/confirm#fragment")]
    public void Invalid_confirmation_url_stops_startup(string? url)
    {
        using var root = new GameStoreWebApplicationFactory();
        using var factory = root.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Email:ConfirmationPageUrl"] = url })));
        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Email:ConfirmationPageUrl", error.ToString());
    }

    [Fact]
    public async Task Api_does_not_host_confirmation_pages()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        foreach (var route in new[] { "/confirm-email", "/confirmation.js", "/confirmation.css" })
        {
            using var response = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task Catalog_cache_has_a_separate_bounded_entry_budget()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Cache:CatalogMaxEntries"] = "2" }).Build();
        using var cache = new CatalogResponseCache(config);
        for (var index = 0; index < 20; index++)
            Assert.Equal(index, await cache.GetOrCreateAsync("entry-" + index, () => Task.FromResult(index)));
        Assert.InRange(cache.EntryCount, 0, 2);
    }

    [Fact]
    public async Task Catalog_search_indexes_track_changes_and_migration_backfills_existing_games()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameStoreContext>();
        var game = new Game { Name = "Moonlight 100%_Fun", GenreId = 1, Price = 25, ReleasedDate = new(2026, 1, 1) };
        db.Games.Add(game);
        await db.SaveChangesAsync();
        Assert.Single(await CatalogSearch.Games(db, "NLIGHT").ToListAsync());
        Assert.Single(await CatalogSearch.Games(db, "%_").ToListAsync());
        Assert.Empty(await CatalogSearch.Games(db, "\" OR anything").ToListAsync());
        game.Name = "Sunrise Adventure";
        await db.SaveChangesAsync();
        Assert.Empty(await CatalogSearch.Games(db, "moonlight").ToListAsync());
        var genre = (await db.Genres.FindAsync(1))!;
        genre.Name = "Unusual Genre";
        await db.SaveChangesAsync();
        Assert.Single(await CatalogSearch.Games(db, "usual gen").ToListAsync());
        Assert.Single(await CatalogSearch.Genres(db, "USUAL").ToListAsync());
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261011021928_EnforceUniqueNormalizedEmail");
        await migrator.MigrateAsync();
        Assert.Single(await CatalogSearch.Games(db, "sunrise").ToListAsync());
        // Verify SQLite uses its FTS virtual index, not merely equivalent results
        // from a fallback table scan. This is not a production-sized benchmark.
        await db.Database.OpenConnectionAsync();
        using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "EXPLAIN QUERY PLAN SELECT rowid FROM GameSearch WHERE GameSearch MATCH @term";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@term";
            parameter.Value = "\"sunrise\"";
            command.Parameters.Add(parameter);
            using var reader = await command.ExecuteReaderAsync();
            var indexed = false;
            while (await reader.ReadAsync()) indexed |= reader.GetString(3).Contains("VIRTUAL TABLE INDEX", StringComparison.OrdinalIgnoreCase);
            Assert.True(indexed);
        }
        await db.Database.CloseConnectionAsync();
        await db.Games.Where(item => item.Id == game.Id).ExecuteDeleteAsync();
        Assert.Empty(await CatalogSearch.Games(db, "sunrise").ToListAsync());
    }

    [Theory]
    [InlineData("/games")]
    [InlineData("/genres")]
    public async Task Oversized_search_is_rejected_before_querying_or_caching(string route)
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/auth/login", new { username = "Admin", password = "Bootstrap-Cedar-River-593!" });
        var tokens = (await login.Content.ReadFromJsonAsync<AuthTokensDto>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
        using var response = await client.GetAsync(route + "?search=" + new string('a', 101));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Services.GetRequiredService<CatalogResponseCache>().EntryCount);
    }

    private static WebApplicationFactory<Program> Configure(GameStoreWebApplicationFactory root, TestTransport transport, TestClock clock) =>
        root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.RemoveAll<IAccountEmailSender>();
            services.AddScoped<IAccountEmailSender, OutboxAccountEmailSender>();
            services.RemoveAll<IEmailTransport>();
            services.AddSingleton<IEmailTransport>(transport);
            services.AddSingleton<TimeProvider>(clock);
        }));

    private sealed class TestClock : TimeProvider
    {
        private TimeSpan offset;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + offset;
        public void Advance(TimeSpan amount) => offset += amount;
    }

    private sealed class TestTransport : IEmailTransport
    {
        public bool Fail { get; set; }
        public int Attempts { get; private set; }
        public string Body { get; private set; } = "";
        public TaskCompletionSource? Entered { get; init; }
        public TaskCompletionSource? Release { get; init; }
        public async Task SendAsync(string address, string subject, string body, CancellationToken cancellationToken = default)
        {
            Attempts++;
            Entered?.TrySetResult();
            if (Release is not null) await Release.Task.WaitAsync(cancellationToken);
            if (Fail) throw new System.Net.Mail.SmtpException("Simulated SMTP outage");
            Body = body;
        }
    }
}
