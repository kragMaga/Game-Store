using System.Text;
using TradeNest.Api.Data;
using TradeNest.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace TradeNest.Api.Services;

// A trusted background job selects each tenant explicitly; no HTTP-supplied ID or
// global administrator claim is used to bypass membership checks on public routes.
public sealed class LowStockNotificationProcessor(TradeNestContext db, IAccountEmailSender emails, TimeProvider clock, BusinessNotificationQueue? notifications = null)
{
    public async Task<bool> QueueAsync(Guid businessId, CancellationToken cancellationToken = default)
    {
        await using var tx = await SecurityTransaction.BeginAsync(db);
        var business = await db.Businesses.AsNoTracking().SingleOrDefaultAsync(b => b.Id == businessId, cancellationToken);
        if (business is null || business.ClosedAtUtc.HasValue || !business.LowStockEmailsEnabled) return false;
        var local = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById(business.TimeZoneId));
        var day = DateOnly.FromDateTime(local.DateTime);
        if (TimeOnly.FromDateTime(local.DateTime) < business.DailyNotificationTime) return false;
        db.InventoryBusinessId = businessId;
        if (await db.BusinessNotificationDispatches.AnyAsync(d => d.BusinessId == businessId && d.LocalDate == day, cancellationToken)) return false;
        var query = db.InventoryProducts.AsNoTracking().Where(p => p.BusinessId == businessId && p.IsActive && p.QuantityOnHandMicros <= p.ReorderLevelMicros);
        var count = await query.CountAsync(cancellationToken);
        var products = await query.OrderBy(p => p.NormalizedSku).ThenBy(p => p.Id).Take(500).ToListAsync(cancellationToken);
        db.BusinessNotificationDispatches.Add(new() { BusinessId = businessId, LocalDate = day, CreatedAtUtc = clock.GetUtcNow().UtcDateTime, ItemCount = count });
        if (count > 0)
        {
            var body = new StringBuilder().AppendLine($"Daily low-stock summary for {business.Name} ({day:yyyy-MM-dd})").AppendLine();
            foreach (var product in products)
                body.AppendLine($"{product.Sku} - {product.Name}: {StockQuantity.FromMicros(product.QuantityOnHandMicros)} {product.StockUnit}; reorder at {StockQuantity.FromMicros(product.ReorderLevelMicros)}");
            if (count > products.Count) body.AppendLine($"{count - products.Count} additional items: consult the inventory report.");
            // The production sender enqueues an encrypted outbox entry using this
            // DbContext. Dispatch marker and email commit together, before SMTP.
            if (notifications is not null)
                await notifications.QueueAsync(businessId, "LowStock", day, business.NotificationEmail, $"Low stock: {business.Name}", body.ToString(), cancellationToken);
            else
                // Preserve the lightweight sender seam used by isolated job tests.
                await emails.SendAsync(business.NotificationEmail, $"Low stock: {business.Name}", body.ToString(), cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken); await tx.CommitAsync(); return count > 0;
    }
}

public sealed class LowStockNotificationWorker(IServiceScopeFactory scopes, IConfiguration configuration,
    ILogger<LowStockNotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("LowStock:Enabled", true)) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        // Scan all tenants in bounded pages. Local dates handle DST changes and
        // restarts without sending a second summary for the same business day.
        do
        {
            try
            {
                var offset = 0;
                while (!stoppingToken.IsCancellationRequested)
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<TradeNestContext>();
                    var ids = await db.Businesses.AsNoTracking().Where(b => b.ClosedAtUtc == null && (b.LowStockEmailsEnabled || b.DebtReminderEmailsEnabled))
                        .OrderBy(b => b.Id).Skip(offset).Take(100).Select(b => b.Id).ToListAsync(stoppingToken);
                    foreach (var id in ids)
                    {
                        // A failing stock email must not suppress debt reminders,
                        // and each operation gets a clean scope/transaction.
                        foreach (var debtReminder in new[] { false, true })
                        {
                            try
                            {
                                using var job = scopes.CreateScope();
                                if (debtReminder) await job.ServiceProvider.GetRequiredService<DebtReminderProcessor>().QueueAsync(id, stoppingToken);
                                else await job.ServiceProvider.GetRequiredService<LowStockNotificationProcessor>().QueueAsync(id, stoppingToken);
                            }
                            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                            { logger.LogWarning("Business alert enqueue failed for business {BusinessId}; error type {ErrorType}", id, exception.GetType().Name); }
                        }
                    }
                    if (ids.Count < 100) break;
                    offset += ids.Count;
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            { logger.LogWarning("Low-stock scan failed; error type {ErrorType}", exception.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
