using System.Text.Json;
using GameStore.Api.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace GameStore.Api.Services;

public sealed class EmailOutboxProcessor(GameStoreContext db, IEmailTransport transport,
    IDataProtectionProvider protection, TimeProvider clock, ILogger<EmailOutboxProcessor> logger)
{
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        // Expired links must never be sent; clear encrypted contents once terminal.
        await db.EmailOutbox.Where(item => item.Status == "Pending" && item.ExpiresAtUtc <= now)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.Status, "Expired")
                .SetProperty(item => item.ProtectedPayload, "").SetProperty(item => item.CompletedAtUtc, now), cancellationToken);
        await db.EmailOutbox.Where(item => item.CompletedAtUtc < now.AddDays(-7)).ExecuteDeleteAsync(cancellationToken);

        string id, owner;
        // Acquire a lease under SQLite's writer transaction. Other instances cannot
        // claim the same message; a crashed worker's lease becomes available again.
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var message = await db.EmailOutbox.Where(item => item.Status == "Pending" &&
                    item.NextAttemptAtUtc <= now && item.ExpiresAtUtc > now &&
                    (item.LeaseExpiresAtUtc == null || item.LeaseExpiresAtUtc <= now))
                .OrderBy(item => item.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken);
            if (message is null) return false;
            id = message.Id;
            owner = Guid.NewGuid().ToString("N");
            message.LeaseOwner = owner;
            message.LeaseExpiresAtUtc = now.AddMinutes(2);
            message.Attempts++;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        var claimed = await db.EmailOutbox.AsNoTracking().SingleAsync(item => item.Id == id, cancellationToken);
        try
        {
            var payload = JsonSerializer.Deserialize<OutboxEmailPayload>(protection.CreateProtector(
                OutboxAccountEmailSender.ProtectionPurpose).Unprotect(claimed.ProtectedPayload))
                ?? throw new InvalidOperationException("Invalid queued email payload.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await transport.SendAsync(payload.Address, payload.Subject, payload.Body, timeout.Token);
            await db.EmailOutbox.Where(item => item.Id == id && item.LeaseOwner == owner)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.Status, "Sent")
                    .SetProperty(item => item.ProtectedPayload, "").SetProperty(item => item.CompletedAtUtc, clock.GetUtcNow().UtcDateTime)
                    .SetProperty(item => item.LeaseOwner, (string?)null).SetProperty(item => item.LeaseExpiresAtUtc, (DateTime?)null), cancellationToken);
            logger.LogInformation("Delivered queued email {MessageId}", id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            var failed = claimed.Attempts >= 10;
            var retry = clock.GetUtcNow().UtcDateTime.AddSeconds(Math.Min(3600, 30 * Math.Pow(2, claimed.Attempts - 1)));
            await db.EmailOutbox.Where(item => item.Id == id && item.LeaseOwner == owner)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.Status, failed ? "Failed" : "Pending")
                    .SetProperty(item => item.NextAttemptAtUtc, retry)
                    .SetProperty(item => item.FailureType, exception.GetType().Name)
                    .SetProperty(item => item.ProtectedPayload, failed ? "" : claimed.ProtectedPayload)
                    .SetProperty(item => item.CompletedAtUtc, failed ? (DateTime?)clock.GetUtcNow().UtcDateTime : null)
                    .SetProperty(item => item.LeaseOwner, (string?)null).SetProperty(item => item.LeaseExpiresAtUtc, (DateTime?)null), cancellationToken);
            // Exception messages may contain recipients or tokens. Log only safe metadata.
            logger.LogWarning("Queued email {MessageId} delivery failed at attempt {Attempt}. Failure type: {FailureType}",
                id, claimed.Attempts, exception.GetType().Name);
        }
        finally { db.ChangeTracker.Clear(); }
        return true;
    }
}

public sealed class EmailOutboxWorker(IServiceScopeFactory scopes, IConfiguration configuration,
    ILogger<EmailOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Email:Outbox:Enabled", true)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Bound each drain so continuous traffic cannot starve shutdown/cleanup.
                for (var count = 0; count < 20; count++)
                {
                    using var scope = scopes.CreateScope();
                    if (!await scope.ServiceProvider.GetRequiredService<EmailOutboxProcessor>().ProcessNextAsync(stoppingToken)) break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            { logger.LogError("Email outbox processing failed. Failure type: {FailureType}", exception.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
