using System.Text.Json;
using TradeNest.Api.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace TradeNest.Api.Services;

public sealed class EmailOutboxProcessor(TradeNestContext db, IEmailTransport transport,
    IDataProtectionProvider protection, TimeProvider clock, ILogger<EmailOutboxProcessor> logger)
{
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        // Persist terminal business metadata before the short-lived outbox row
        // is removed. Business history retains no authentication email payloads.
        await using (var expiry = await db.Database.BeginTransactionAsync(cancellationToken))
        {
        await db.BusinessNotifications.IgnoreQueryFilters().Where(n => db.EmailOutbox.Any(o => o.Id == n.OutboxMessageId && o.Status == "Pending" && o.ExpiresAtUtc <= now))
            .ExecuteUpdateAsync(set => set.SetProperty(n => n.Status, "Expired").SetProperty(n => n.CompletedAtUtc, now), cancellationToken);
        // Expired links must never be sent; clear encrypted contents once terminal.
        await db.EmailOutbox.Where(item => item.Status == "Pending" && item.ExpiresAtUtc <= now)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.Status, "Expired")
                .SetProperty(item => item.ProtectedPayload, "").SetProperty(item => item.CompletedAtUtc, now), cancellationToken);
        await db.EmailOutbox.Where(item => item.CompletedAtUtc < now.AddDays(-7)).ExecuteDeleteAsync(cancellationToken);

        await expiry.CommitAsync(cancellationToken);
        }

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
            await db.BusinessNotifications.IgnoreQueryFilters().Where(n => n.OutboxMessageId == message.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(n => n.Attempts, n => n.Attempts + 1), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        var claimed = await db.EmailOutbox.AsNoTracking().SingleAsync(item => item.Id == id, cancellationToken);
        try
        {
            if (!await BusinessNotificationDeliveryPolicy.CanSendAsync(db, id, cancellationToken))
            {
                await using var cancellation = await db.Database.BeginTransactionAsync(cancellationToken);
                var cancelled = await db.EmailOutbox.Where(o => o.Id == id && o.LeaseOwner == owner).ExecuteUpdateAsync(set => set
                    .SetProperty(o => o.Status, "Cancelled").SetProperty(o => o.ProtectedPayload, "").SetProperty(o => o.CompletedAtUtc, clock.GetUtcNow().UtcDateTime)
                    .SetProperty(o => o.LeaseOwner, (string?)null).SetProperty(o => o.LeaseExpiresAtUtc, (DateTime?)null), cancellationToken);
                if (cancelled == 1) await db.BusinessNotifications.IgnoreQueryFilters().Where(n => n.OutboxMessageId == id).ExecuteUpdateAsync(set => set
                    .SetProperty(n => n.Status, "Cancelled").SetProperty(n => n.ProtectedPayload, "").SetProperty(n => n.CompletedAtUtc, clock.GetUtcNow().UtcDateTime), cancellationToken);
                await cancellation.CommitAsync(cancellationToken); return true;
            }
            var payload = JsonSerializer.Deserialize<OutboxEmailPayload>(protection.CreateProtector(
                OutboxAccountEmailSender.ProtectionPurpose).Unprotect(claimed.ProtectedPayload))
                ?? throw new InvalidOperationException("Invalid queued email payload.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await transport.SendAsync(payload.Address, payload.Subject, payload.Body, timeout.Token);
            await using var statusTransaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var owned = await db.EmailOutbox.Where(item => item.Id == id && item.LeaseOwner == owner)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.Status, "Sent")
                    .SetProperty(item => item.ProtectedPayload, "").SetProperty(item => item.CompletedAtUtc, clock.GetUtcNow().UtcDateTime)
                    .SetProperty(item => item.LeaseOwner, (string?)null).SetProperty(item => item.LeaseExpiresAtUtc, (DateTime?)null), cancellationToken);
            if (owned == 1) await db.BusinessNotifications.IgnoreQueryFilters().Where(n => n.OutboxMessageId == id)
                .ExecuteUpdateAsync(set => set.SetProperty(n => n.Status, "Sent").SetProperty(n => n.CompletedAtUtc, clock.GetUtcNow().UtcDateTime).SetProperty(n => n.FailureType, (string?)null), cancellationToken);
            await statusTransaction.CommitAsync(cancellationToken);
            logger.LogInformation("Delivered queued email {MessageId}", id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            var failed = claimed.Attempts >= 10;
            var retry = clock.GetUtcNow().UtcDateTime.AddSeconds(Math.Min(3600, 30 * Math.Pow(2, claimed.Attempts - 1)));
            await using var statusTransaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var owned = await db.EmailOutbox.Where(item => item.Id == id && item.LeaseOwner == owner)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.Status, failed ? "Failed" : "Pending")
                    .SetProperty(item => item.NextAttemptAtUtc, retry)
                    .SetProperty(item => item.FailureType, exception.GetType().Name)
                    .SetProperty(item => item.ProtectedPayload, failed ? "" : claimed.ProtectedPayload)
                    .SetProperty(item => item.CompletedAtUtc, failed ? (DateTime?)clock.GetUtcNow().UtcDateTime : null)
                    .SetProperty(item => item.LeaseOwner, (string?)null).SetProperty(item => item.LeaseExpiresAtUtc, (DateTime?)null), cancellationToken);
            if (owned == 1) await db.BusinessNotifications.IgnoreQueryFilters().Where(n => n.OutboxMessageId == id)
                .ExecuteUpdateAsync(set => set.SetProperty(n => n.Status, failed ? "Failed" : "Pending")
                    .SetProperty(n => n.FailureType, exception.GetType().Name)
                    .SetProperty(n => n.CompletedAtUtc, failed ? (DateTime?)clock.GetUtcNow().UtcDateTime : null), cancellationToken);
            await statusTransaction.CommitAsync(cancellationToken);
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
