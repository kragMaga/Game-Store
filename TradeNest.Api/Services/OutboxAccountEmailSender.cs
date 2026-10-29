using System.Text.Json;
using TradeNest.Api.Data;
using TradeNest.Api.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace TradeNest.Api.Services;

// Separate the durable queue from transport so a worker cannot accidentally queue
// its own delivery again. Tests can replace the transport without contacting SMTP.
public interface IEmailTransport : IAccountEmailSender { }
public sealed record OutboxEmailPayload(string Address, string Subject, string Body);

public sealed class OutboxAccountEmailSender(TradeNestContext db, IDataProtectionProvider protection,
    TimeProvider clock, IOptions<DataProtectionTokenProviderOptions> tokens) : IAccountEmailSender
{
    public const string ProtectionPurpose = LegacyCompatibility.EmailOutboxPurpose;
    public async Task SendAsync(string emailAddress, string subject, string textBody, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        // Do not deliver links after their Identity token lifetime. A shorter queue
        // lifetime also limits retention of sensitive message contents.
        var lifetime = tokens.Value.TokenLifespan < TimeSpan.FromHours(12) ? tokens.Value.TokenLifespan : TimeSpan.FromHours(12);
        var message = new EmailOutboxMessage
        {
            ProtectedPayload = protection.CreateProtector(ProtectionPurpose)
                .Protect(JsonSerializer.Serialize(new OutboxEmailPayload(emailAddress, subject, textBody))),
            CreatedAtUtc = now, NextAttemptAtUtc = now, ExpiresAtUtc = now.Add(lifetime)
        };
        db.EmailOutbox.Add(message);
        await db.SaveChangesAsync(cancellationToken);
    }
}
