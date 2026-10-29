using System.Text.Json;
using TradeNest.Api.Data;
using TradeNest.Api.Entities;
using TradeNest.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace TradeNest.Api.Tests;

[Collection("API integration")]
public sealed class RenameCompatibilityTests
{
    [Fact]
    public async Task Renamed_worker_delivers_a_payload_encrypted_before_the_brand_change()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<TradeNestContext>();
        var protection = services.GetRequiredService<IDataProtectionProvider>();
        var now = DateTime.UtcNow;
        // Deliberately use the historical literal, not the new compatibility
        // constant: an accidental future purpose rename must fail this regression.
        var payload = new OutboxEmailPayload("owner@example.test", "Existing queued message", "Existing confirmation instructions");
        db.EmailOutbox.Add(new EmailOutboxMessage {
            ProtectedPayload = protection.CreateProtector("GameStore.EmailOutbox.v1").Protect(JsonSerializer.Serialize(payload)),
            CreatedAtUtc = now, NextAttemptAtUtc = now, ExpiresAtUtc = now.AddHours(1)
        });
        await db.SaveChangesAsync();
        var transport = new CapturingTransport();
        var worker = new EmailOutboxProcessor(db, transport, protection, TimeProvider.System, NullLogger<EmailOutboxProcessor>.Instance);
        Assert.True(await worker.ProcessNextAsync(CancellationToken.None));
        Assert.Equal(payload.Body, transport.Body);
        Assert.Equal("Sent", (await db.EmailOutbox.AsNoTracking().SingleAsync()).Status);
    }

    private sealed class CapturingTransport : IEmailTransport
    {
        public string? Body { get; private set; }
        public Task SendAsync(string emailAddress, string subject, string body, CancellationToken cancellationToken = default)
        { Body = body; return Task.CompletedTask; }
    }
}
