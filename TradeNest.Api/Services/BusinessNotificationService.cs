using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;
using TradeNest.Api.Data;
using TradeNest.Api.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace TradeNest.Api.Services;

public sealed record DebtReminderSettingsDto(bool EmailEnabled, IReadOnlyList<string> AdditionalRecipients);

// Business notifications have their own tenant metadata. Authentication emails
// stay outside this API so no admin can inspect/retry another user's auth tokens.
public sealed class BusinessNotificationQueue(TradeNestContext db, IDataProtectionProvider protection, TimeProvider clock)
{
    private const string Purpose = LegacyCompatibility.NotificationRetryPurpose;
    public async Task QueueAsync(Guid businessId, string kind, DateOnly day, string recipient, string subject, string body, CancellationToken token = default)
    {
        if (db.Database.CurrentTransaction is null || db.InventoryBusinessId != businessId)
            throw new InvalidOperationException("Business notification requires an explicit tenant transaction.");
        var payload = JsonSerializer.Serialize(new OutboxEmailPayload(recipient, subject, body)); var now = clock.GetUtcNow().UtcDateTime;
        var outbox = Message(payload, now);
        db.BusinessNotifications.Add(new() { BusinessId = businessId, Kind = kind, LocalDate = day, Recipient = recipient,
            OutboxMessageId = outbox.Id, ProtectedPayload = protection.CreateProtector(Purpose).Protect(payload), CreatedAtUtc = now, RetryUntilUtc = now.AddDays(7) });
        db.EmailOutbox.Add(outbox); await db.SaveChangesAsync(token);
    }
    internal EmailOutboxMessage Message(string payload, DateTime now) => new() { ProtectedPayload = protection.CreateProtector(OutboxAccountEmailSender.ProtectionPurpose).Protect(payload),
        CreatedAtUtc = now, NextAttemptAtUtc = now, ExpiresAtUtc = now.AddHours(12) };
    internal string Read(string payload) => protection.CreateProtector(Purpose).Unprotect(payload);
}

public sealed class BusinessNotificationService(TradeNestContext db, BusinessAccess access, BusinessNotificationQueue queue, TimeProvider clock)
{
    private async Task Scope(Guid businessId, int actor)
    { db.InventoryBusinessId = null; await access.RequireAsync(businessId, actor, true); db.InventoryBusinessId = businessId; }
    public async Task<object> HistoryAsync(Guid businessId, int actor, int page, int size)
    {
        BusinessInsightService.Page(page, size); await Scope(businessId, actor);
        var query = db.BusinessNotifications.AsNoTracking().Where(n => n.BusinessId == businessId); var count = await query.CountAsync();
        var now = clock.GetUtcNow().UtcDateTime;
        var rows = await query.OrderByDescending(n => n.CreatedAtUtc).ThenBy(n => n.Id).Skip((page - 1) * size).Take(size)
            .Select(n => new { n.Id, n.Kind, n.LocalDate, n.Recipient, n.Status, n.Attempts, n.CreatedAtUtc, n.CompletedAtUtc,
                n.FailureType, CanRetry = (n.Status == "Failed" || n.Status == "Expired") && n.RetryUntilUtc > now }).ToListAsync();
        return new { Items = rows, Page = page, PageSize = size, TotalCount = count };
    }
    public async Task RetryAsync(Guid businessId, Guid id, int actor)
    {
        await using var tx = await SecurityTransaction.BeginAsync(db); await Scope(businessId, actor);
        var notification = await db.BusinessNotifications.SingleOrDefaultAsync(n => n.BusinessId == businessId && n.Id == id)
            ?? throw new BusinessRequestException(404, "Notification not found");
        if (notification.Status is not ("Failed" or "Expired") || notification.RetryUntilUtc <= clock.GetUtcNow().UtcDateTime)
            throw new BusinessRequestException(409, "Only failed/expired notifications less than seven days old can be retried");
        // Requeue only this business alert, never arbitrary outbox IDs or payloads.
        string payload;
        try { payload = queue.Read(notification.ProtectedPayload); }
        catch (CryptographicException) { throw new BusinessRequestException(409, "Notification payload is no longer available"); }
        var message = queue.Message(payload, clock.GetUtcNow().UtcDateTime); db.EmailOutbox.Add(message);
        notification.OutboxMessageId = message.Id; notification.Status = "Pending"; notification.CompletedAtUtc = null; notification.FailureType = null;
        db.BusinessAuditEvents.Add(new() { BusinessId = businessId, ActorUserId = actor, Action = "BusinessNotificationRetried", CreatedAtUtc = clock.GetUtcNow().UtcDateTime });
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
    public async Task<object> SettingsAsync(Guid businessId, int actor)
    {
        await Scope(businessId, actor); var b = await db.Businesses.AsNoTracking().SingleAsync(b => b.Id == businessId);
        return new { EmailEnabled = b.DebtReminderEmailsEnabled, AdditionalRecipients = JsonSerializer.Deserialize<string[]>(b.DebtReminderRecipientsJson),
            UsesConfiguredRecipientsOnly = true, b.DailyNotificationTime, b.TimeZoneId };
    }
    public async Task SettingsAsync(Guid businessId, int actor, DebtReminderSettingsDto request)
    {
        if (request.AdditionalRecipients is null || request.AdditionalRecipients.Count > 20 || request.AdditionalRecipients.Any(e => string.IsNullOrWhiteSpace(e) || e.Length > 254 || !new EmailAddressAttribute().IsValid(e) || e.Contains('\r') || e.Contains('\n')))
            throw new BusinessRequestException(400, "Provide at most twenty valid reminder email addresses");
        if (request.EmailEnabled && request.AdditionalRecipients.Count == 0)
            throw new BusinessRequestException(400, "Specify at least one address before enabling reminder emails");
        var addresses = request.AdditionalRecipients.Select(e => e.Trim().ToLowerInvariant()).Distinct().ToArray();
        await using var tx = await SecurityTransaction.BeginAsync(db); await Scope(businessId, actor);
        var b = await db.Businesses.SingleAsync(b => b.Id == businessId);
        b.DebtReminderEmailsEnabled = request.EmailEnabled; b.DebtReminderRecipientsJson = JsonSerializer.Serialize(addresses);
        db.BusinessAuditEvents.Add(new() { BusinessId = businessId, ActorUserId = actor, Action = "DebtReminderSettingsUpdated", CreatedAtUtc = clock.GetUtcNow().UtcDateTime });
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
}

// Reminders tell the business about overdue debt; customer email records are
// not consulted when selecting destinations. The dashboard reads live balances even if emails
// are disabled or today's dispatch has already been queued.
public sealed class DebtReminderProcessor(TradeNestContext db, BusinessInsightService insights, BusinessNotificationQueue queue, TimeProvider clock)
{
    public async Task<bool> QueueAsync(Guid businessId, CancellationToken token = default)
    {
        await using var tx = await SecurityTransaction.BeginAsync(db);
        var b = await db.Businesses.AsNoTracking().SingleOrDefaultAsync(b => b.Id == businessId, token);
        if (b is null || b.ClosedAtUtc.HasValue || !b.DebtReminderEmailsEnabled) return false;
        var local = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById(b.TimeZoneId));
        var day = DateOnly.FromDateTime(local.DateTime);
        if (TimeOnly.FromDateTime(local.DateTime) < b.DailyNotificationTime) return false;
        db.InventoryBusinessId = businessId;
        if (await db.DebtReminderDispatches.AnyAsync(d => d.BusinessId == businessId && d.LocalDate == day, token)) return false;
        var totals = await insights.ReminderTotals(businessId, day);
        db.DebtReminderDispatches.Add(new() { BusinessId = businessId, LocalDate = day, CreatedAtUtc = clock.GetUtcNow().UtcDateTime });
        if (totals.AmountMinor > 0)
        {
            // Email destinations are explicitly selected by the business. Dashboard
            // access is Owner/Admin; selecting an email address grants no API access.
            var addresses = JsonSerializer.Deserialize<string[]>(b.DebtReminderRecipientsJson) ?? [];
            var body = $"Internal overdue-debt reminder for {b.Name} on {day:yyyy-MM-dd}.\n{totals.CustomerCount} customers owe {InventoryMoney.FromMinor(totals.AmountMinor, b.CurrencyDecimalPlaces)} {b.Currency} overdue. Review the debt ageing report on your business dashboard. This is an internal business reminder.";
            foreach (var email in addresses.Distinct(StringComparer.OrdinalIgnoreCase))
                await queue.QueueAsync(businessId, "OverdueDebt", day, email.ToLowerInvariant(), $"Overdue balances: {b.Name}", body, token);
        }
        await db.SaveChangesAsync(token); await tx.CommitAsync(); return totals.AmountMinor > 0;
    }
}

// Recheck business settings immediately before SMTP. A queued reminder must not
// go to an address that the business removed while it waited in the outbox.
internal static class BusinessNotificationDeliveryPolicy
{
    public static async Task<bool> CanSendAsync(TradeNestContext db, string outboxId, CancellationToken token)
    {
        var n = await db.BusinessNotifications.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(n => n.OutboxMessageId == outboxId, token);
        if (n is null) return true; // Authentication/outside-business emails use their existing lifecycle.
        var business = await db.Businesses.AsNoTracking().SingleOrDefaultAsync(b => b.Id == n.BusinessId, token);
        if (business is null || business.ClosedAtUtc.HasValue) return false;
        if (n.Kind == "LowStock") return business.LowStockEmailsEnabled && string.Equals(business.NotificationEmail, n.Recipient, StringComparison.OrdinalIgnoreCase);
        if (n.Kind != "OverdueDebt" || !business.DebtReminderEmailsEnabled) return false;
        var extras = JsonSerializer.Deserialize<string[]>(business.DebtReminderRecipientsJson) ?? [];
        return extras.Contains(n.Recipient, StringComparer.OrdinalIgnoreCase);
    }
}
