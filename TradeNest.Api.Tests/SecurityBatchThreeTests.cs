using System.Net;
using System.Net.Http.Json;
using TradeNest.Api.Data;
using TradeNest.Api.Dtos;
using TradeNest.Api.Entities;
using TradeNest.Api.Services;
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

namespace TradeNest.Api.Tests;

[Collection("API integration")]
public sealed class SecurityBatchThreeTests
{
    [Fact]
    public async Task Queued_confirmation_survives_smtp_outage_and_is_retried_without_duplicate_registration()
    {
        var transport = new TestTransport { Fail = true };
        var clock = new TestClock();
        using var root = new TradeNestWebApplicationFactory();
        using var factory = Configure(root, transport, clock);
        using var client = factory.CreateClient();
        using var registered = await client.PostAsJsonAsync("/auth/register", new
            { username = "queued-user", email = "queued@example.test", password = "Cedar-Moon-River-593!" });
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        Assert.Equal(0, transport.Attempts);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradeNestContext>();
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
            var db = scope.ServiceProvider.GetRequiredService<TradeNestContext>();
            var row = await db.EmailOutbox.AsNoTracking().SingleAsync();
            Assert.Equal("Sent", row.Status);
            Assert.Empty(row.ProtectedPayload);
            Assert.Equal(2, transport.Attempts);
            Assert.Equal(1, await db.Users.CountAsync(user => user.Email == "queued@example.test"));
        }
        Assert.Contains("https://tradenest.example.test/confirm-email#", transport.Body);
        var link = new Uri(transport.Body.Split('\n').Single(line => line.StartsWith("https://", StringComparison.Ordinal)));
        var parameters = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(link.Fragment.TrimStart('#'));
        using var confirmed = await client.PostAsJsonAsync("/auth/confirm-email",
            new { email = parameters["email"].ToString(), token = parameters["token"].ToString() });
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
    }

    [Fact]
    public async Task Queue_persistence_failure_rolls_back_account_creation()
    {
        using var root = new TradeNestWebApplicationFactory();
        using var factory = Configure(root, new TestTransport(), new TestClock());
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeNestContext>();
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
        using var root = new TradeNestWebApplicationFactory();
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
        var expired = await first.ServiceProvider.GetRequiredService<TradeNestContext>().EmailOutbox.AsNoTracking().SingleAsync(row => row.Status == "Expired");
        Assert.Empty(expired.ProtectedPayload);
        Assert.Equal(1, transport.Attempts);
    }

    [Theory]
    [InlineData("http://example.test/confirm")]
    [InlineData("https://user:password@example.test/confirm")]
    [InlineData("https://example.test/confirm#fragment")]
    public void Invalid_confirmation_url_stops_startup(string? url)
    {
        using var root = new TradeNestWebApplicationFactory();
        using var factory = root.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Email:ConfirmationPageUrl"] = url })));
        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Email:ConfirmationPageUrl", error.ToString());
    }

    [Fact]
    public async Task Api_does_not_host_confirmation_pages()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var client = factory.CreateClient();
        foreach (var route in new[] { "/confirm-email", "/confirmation.js", "/confirmation.css" })
        {
            using var response = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    private static WebApplicationFactory<Program> Configure(TradeNestWebApplicationFactory root, TestTransport transport, TestClock clock) =>
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
