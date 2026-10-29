using TradeNest.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace TradeNest.Api.Services;

// Remove entire retired sessions, not individual rotated refresh tokens. Keeping
// that history until the session retires preserves refresh-token replay detection.
public sealed class AuthDataCleanup(TradeNestContext db, TimeProvider clock, IConfiguration configuration)
{
    public async Task<int> RunBatchAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var cutoff = now.AddDays(-7);
        var idleCutoff = cutoff.AddMinutes(-configuration.GetValue("Jwt:RefreshTokenIdleMinutes", 30));
        await using var transaction = await SecurityTransaction.BeginAsync(db);
        // Bound writes to 500 sessions per hourly pass to avoid long SQLite locks.
        var ids = await db.AuthSessions.Where(session => session.AbsoluteExpiresAtUtc < cutoff ||
                session.RevokedAtUtc < cutoff || session.LastRefreshedAtUtc < idleCutoff)
            .OrderBy(session => session.AbsoluteExpiresAtUtc).Select(session => session.Id)
            .Take(500).ToListAsync(cancellationToken);
        await db.RefreshTokens.Where(token => ids.Contains(token.SessionId))
            .ExecuteDeleteAsync(cancellationToken);
        var deleted = await db.AuthSessions.Where(session => ids.Contains(session.Id))
            .ExecuteDeleteAsync(cancellationToken);
        // Refresh tokens do not have a user FK in the existing schema. Deleted
        // accounts can leave orphans; remove only expired history after retention.
        var orphans = await db.RefreshTokens.Where(token => token.AbsoluteExpiresAtUtc < cutoff &&
                !db.AuthSessions.Any(session => session.Id == token.SessionId))
            .OrderBy(token => token.Id).Select(token => token.Id).Take(1000).ToListAsync(cancellationToken);
        await db.RefreshTokens.Where(token => orphans.Contains(token.Id)).ExecuteDeleteAsync(cancellationToken);
        // Expired invitations no longer authorize access. Retain thirty days for
        // support, then prune a bounded batch; authority audit history remains.
        var invitationCutoff = now.AddDays(-30);
        var invitations = await db.BusinessInvitations.Where(i => i.ExpiresAtUtc < invitationCutoff)
            .OrderBy(i => i.ExpiresAtUtc).Select(i => i.Id).Take(1000).ToListAsync(cancellationToken);
        await db.BusinessInvitations.Where(i => invitations.Contains(i.Id)).ExecuteDeleteAsync(cancellationToken);
        // Dispatch markers older than ninety days cannot affect today's dedupe.
        var dispatchCutoff = DateOnly.FromDateTime(now.AddDays(-90));
        var dispatches = await db.BusinessNotificationDispatches.IgnoreQueryFilters().Where(d => d.LocalDate < dispatchCutoff)
            .OrderBy(d => d.LocalDate).Select(d => new { d.BusinessId, d.LocalDate }).Take(500).ToListAsync(cancellationToken);
        foreach (var item in dispatches)
            await db.BusinessNotificationDispatches.IgnoreQueryFilters().Where(d => d.BusinessId == item.BusinessId && d.LocalDate == item.LocalDate).ExecuteDeleteAsync(cancellationToken);
        // Keep operational metadata for ninety days, but discard encrypted retry
        // bodies after seven. These trusted maintenance queries span tenants.
        var expiredNotifications = await db.BusinessNotifications.IgnoreQueryFilters().Where(n => n.RetryUntilUtc < now && n.ProtectedPayload != "")
            .OrderBy(n => n.RetryUntilUtc).Select(n => n.Id).Take(500).ToListAsync(cancellationToken);
        await db.BusinessNotifications.IgnoreQueryFilters().Where(n => expiredNotifications.Contains(n.Id))
            .ExecuteUpdateAsync(set => set.SetProperty(n => n.ProtectedPayload, ""), cancellationToken);
        var oldNotifications = await db.BusinessNotifications.IgnoreQueryFilters().Where(n => n.CreatedAtUtc < now.AddDays(-90))
            .OrderBy(n => n.CreatedAtUtc).Select(n => n.Id).Take(500).ToListAsync(cancellationToken);
        await db.BusinessNotifications.IgnoreQueryFilters().Where(n => oldNotifications.Contains(n.Id)).ExecuteDeleteAsync(cancellationToken);
        var reminders = await db.DebtReminderDispatches.IgnoreQueryFilters().Where(d => d.LocalDate < dispatchCutoff).OrderBy(d => d.LocalDate)
            .Select(d => new { d.BusinessId, d.LocalDate }).Take(500).ToListAsync(cancellationToken);
        foreach (var reminder in reminders)
            await db.DebtReminderDispatches.IgnoreQueryFilters().Where(d => d.BusinessId == reminder.BusinessId && d.LocalDate == reminder.LocalDate).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync();
        return deleted;
    }
}

// A fresh scope per pass avoids retaining tracked entities for the worker's life.
// Cancellation stops delays promptly; a failed pass is logged and retried later.
public sealed class AuthDataCleanupWorker(IServiceScopeFactory scopes, IConfiguration configuration,
    ILogger<AuthDataCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Auth:CleanupEnabled", true)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var count = await scope.ServiceProvider.GetRequiredService<AuthDataCleanup>().RunBatchAsync(stoppingToken);
                logger.LogInformation("Authentication cleanup removed {SessionCount} retired sessions", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                // Exception messages can contain SQL/parameters; log only the type.
                logger.LogError("Authentication cleanup failed. Failure type: {FailureType}", exception.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
