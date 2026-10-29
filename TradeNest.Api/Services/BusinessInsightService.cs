using TradeNest.Api.Data;
using TradeNest.Api.Dtos;
using TradeNest.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace TradeNest.Api.Services;

// Aged debt uses TODAY in the business timezone and corrected/current allocations,
// not original sale totals. No due date means unscheduled, never silently overdue.
public sealed class BusinessInsightService(TradeNestContext db, BusinessAccess access, CustomerLedgerService ledger, TimeProvider clock)
{
    private async Task<Business> Scope(Guid businessId, int actor, bool admin = true)
    {
        db.InventoryBusinessId = null; await access.RequireAsync(businessId, actor, admin); db.InventoryBusinessId = businessId;
        return await db.Businesses.AsNoTracking().SingleAsync(b => b.Id == businessId);
    }
    internal static DateOnly Today(Business business, TimeProvider clock) => DateOnly.FromDateTime(
        TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById(business.TimeZoneId)).DateTime);
    internal IQueryable<DebtAgeMinor> Aged(Guid businessId, DateOnly today)
    {
        var d30 = today.AddDays(-30); var d60 = today.AddDays(-60); var d90 = today.AddDays(-90);
        return ledger.Rows(businessId).Where(c => c.CustomerId.HasValue && c.Amount > c.Paid).GroupBy(c => c.CustomerId!.Value)
            .Select(g => new DebtAgeMinor { CustomerId = g.Key,
                Unscheduled = g.Sum(c => c.DueDate == null ? c.Amount - c.Paid : 0),
                Current = g.Sum(c => c.DueDate >= today ? c.Amount - c.Paid : 0),
                Days1To30 = g.Sum(c => c.DueDate < today && c.DueDate >= d30 ? c.Amount - c.Paid : 0),
                Days31To60 = g.Sum(c => c.DueDate < d30 && c.DueDate >= d60 ? c.Amount - c.Paid : 0),
                Days61To90 = g.Sum(c => c.DueDate < d60 && c.DueDate >= d90 ? c.Amount - c.Paid : 0),
                Over90 = g.Sum(c => c.DueDate < d90 ? c.Amount - c.Paid : 0), Total = g.Sum(c => c.Amount - c.Paid) });
    }
    public async Task<object> AgeingAsync(Guid businessId, int actor, int page, int size)
    {
        Page(page, size); var b = await Scope(businessId, actor); var today = Today(b, clock);
        var query = from age in Aged(businessId, today) join customer in db.Customers.AsNoTracking()
            on age.CustomerId equals customer.Id where customer.BusinessId == businessId
            select new { Customer = customer, Age = age };
        var count = await query.CountAsync();
        var selected = await query.OrderBy(x => x.Customer.NormalizedCode).ThenBy(x => x.Customer.Id).Skip((page - 1) * size).Take(size).ToListAsync();
        // Totals cover the whole report, not only the displayed customer page.
        var totals = await Aged(businessId, today).GroupBy(x => 1).Select(g => new DebtAgeMinor {
            Unscheduled = g.Sum(x => x.Unscheduled), Current = g.Sum(x => x.Current), Days1To30 = g.Sum(x => x.Days1To30),
            Days31To60 = g.Sum(x => x.Days31To60), Days61To90 = g.Sum(x => x.Days61To90), Over90 = g.Sum(x => x.Over90), Total = g.Sum(x => x.Total) }).SingleOrDefaultAsync() ?? new();
        return new { AsOf = today, b.Currency, Totals = View(totals, b.CurrencyDecimalPlaces),
            Items = selected.Select(x => new { x.Customer.Id, x.Customer.Code, x.Customer.Name, x.Customer.IsActive, Balances = View(x.Age, b.CurrencyDecimalPlaces) }),
            Page = page, PageSize = size, TotalCount = count };
    }
    public async Task<ReportTable> AgeingExportAsync(Guid businessId, int actor)
    {
        var b = await Scope(businessId, actor); var today = Today(b, clock);
        var query = from age in Aged(businessId, today) join customer in db.Customers.AsNoTracking()
            on age.CustomerId equals customer.Id where customer.BusinessId == businessId select new { Customer = customer, Age = age };
        var count = await query.CountAsync();
        if (count > InventoryReportService.ExportLimit) throw new BusinessRequestException(413, "Export exceeds 10000 customers");
        var selected = await query.OrderBy(x => x.Customer.NormalizedCode).ThenBy(x => x.Customer.Id).ToListAsync();
        decimal Money(long amount) => InventoryMoney.FromMinor(amount, b.CurrencyDecimalPlaces);
        var rows = selected.Select(x => new object?[] { x.Customer.Code, x.Customer.Name, b.Currency, today,
            Money(x.Age.Unscheduled), Money(x.Age.Current), Money(x.Age.Days1To30), Money(x.Age.Days31To60), Money(x.Age.Days61To90), Money(x.Age.Over90), Money(x.Age.Total) }).ToList();
        return new("debt-ageing", b.Currency, ["Code", "Customer", "Currency", "As of", "Unscheduled", "Current", "1-30 days", "31-60 days", "61-90 days", "Over 90 days", "Total"], rows, count, 1, 100);
    }
    internal async Task<DebtReminderTotals> ReminderTotals(Guid businessId, DateOnly today)
    {
        var result = await ledger.Rows(businessId).Where(c => c.CustomerId.HasValue && c.DueDate < today && c.Amount > c.Paid)
            .GroupBy(c => 1).Select(g => new { Amount = g.Sum(c => c.Amount - c.Paid), Customers = g.Select(c => c.CustomerId).Distinct().Count() }).SingleOrDefaultAsync();
        return new(result?.Customers ?? 0, result?.Amount ?? 0);
    }
    public async Task<object> DashboardAsync(Guid businessId, int actor)
    {
        var b = await Scope(businessId, actor); var today = Today(b, clock);
        var overdue = await ReminderTotals(businessId, today);
        var outstanding = await ledger.Rows(businessId).Where(c => c.CustomerId.HasValue).SumAsync(c => (long?)(c.Amount - c.Paid)) ?? 0;
        var low = db.InventoryProducts.AsNoTracking().Where(p => p.BusinessId == businessId && p.IsActive && p.QuantityOnHandMicros <= p.ReorderLevelMicros);
        var products = await low.OrderBy(p => p.QuantityOnHandMicros).ThenBy(p => p.Id).Take(10).ToListAsync();
        var recent = await db.BusinessAuditEvents.AsNoTracking().Where(a => a.BusinessId == businessId).OrderByDescending(a => a.CreatedAtUtc).ThenBy(a => a.Id)
            .Take(10).Select(a => new { a.Id, a.Action, a.ActorUserId, a.CreatedAtUtc }).ToListAsync();
        var sales = await db.InventoryTransactions.Where(t => t.BusinessId == businessId && t.Kind == "Sale" && t.EffectiveDate == today).SumAsync(t => (long?)t.TotalMinor) ?? 0;
        var returns = await db.InventoryCorrections.Where(c => c.BusinessId == businessId && c.EffectiveDate == today && (c.Kind == "SaleReturn" || c.Kind == "SaleReversal")).SumAsync(c => (long?)c.AmountMinor) ?? 0;
        return new { LocalDate = today, b.Currency, b.CurrencyDecimalPlaces, SalesToday = InventoryMoney.FromMinor(sales, b.CurrencyDecimalPlaces),
            ReturnsToday = InventoryMoney.FromMinor(returns, b.CurrencyDecimalPlaces), NetSalesToday = InventoryMoney.FromMinor(sales - returns, b.CurrencyDecimalPlaces),
            CustomerDebt = InventoryMoney.FromMinor(outstanding, b.CurrencyDecimalPlaces),
            DebtReminder = new { overdue.CustomerCount, Amount = InventoryMoney.FromMinor(overdue.AmountMinor, b.CurrencyDecimalPlaces), b.DebtReminderEmailsEnabled },
            LowStockCount = await low.CountAsync(), LowStock = products.Select(p => new { p.Id, p.Name, p.Sku, p.StockUnit,
                Quantity = StockQuantity.FromMicros(p.QuantityOnHandMicros), ReorderLevel = StockQuantity.FromMicros(p.ReorderLevelMicros) }), RecentActivity = recent,
            FailedNotifications = await db.BusinessNotifications.CountAsync(n => n.BusinessId == businessId && (n.Status == "Failed" || n.Status == "Expired")) };
    }
    public async Task<ReportTable> ReconciliationAsync(Guid businessId, int actor, DateOnly from, DateOnly to, int page, int size, bool export = false)
    {
        Page(page, size); var b = await Scope(businessId, actor, admin: false);
        if (from == default || to < from || to > Today(b, clock)) throw new BusinessRequestException(400, "Select a valid non-future date range");
        var query = db.InventoryProducts.AsNoTracking().Where(p => p.BusinessId == businessId);
        var count = await query.CountAsync();
        if (export && count > InventoryReportService.ExportLimit) throw new BusinessRequestException(413, "Export exceeds 10000 products");
        var selected = await query.OrderBy(p => p.NormalizedSku).ThenBy(p => p.Id).Skip(export ? 0 : (page - 1) * size).Take(export ? InventoryReportService.ExportLimit : size).ToListAsync();
        var ids = selected.Select(p => p.Id).ToArray();
        // Sum deltas by effective date. BalanceAfter is posting-order evidence and
        // cannot be used as a historical balance when entries can be backdated.
        var groups = await db.StockMovements.AsNoTracking().Where(m => m.BusinessId == businessId && ids.Contains(m.ProductId) && m.EffectiveDate <= to)
            .GroupBy(m => new { m.ProductId, m.Kind, Before = m.EffectiveDate < from }).Select(g => new { g.Key.ProductId, g.Key.Kind, g.Key.Before, Quantity = g.Sum(m => m.BaseQuantityMicros) }).ToListAsync();
        var rows = new List<object?[]>();
        foreach (var p in selected)
        {
            var own = groups.Where(g => g.ProductId == p.Id).ToList(); var opening = own.Where(g => g.Before).Sum(g => g.Quantity);
            long Kind(params string[] kinds) => own.Where(g => !g.Before && kinds.Contains(g.Kind)).Sum(g => g.Quantity);
            var receipt = Kind("Purchase"); var sales = Kind("Sale"); var saleReturns = Kind("SaleReturn", "SaleReversal"); var purchaseReturns = Kind("PurchaseReturn", "PurchaseReversal");
            var initial = Kind("OpeningStock"); var adjusted = own.Where(g => !g.Before && g.Kind is not ("OpeningStock" or "Purchase" or "Sale" or "SaleReturn" or "SaleReversal" or "PurchaseReturn" or "PurchaseReversal")).Sum(g => g.Quantity);
            rows.Add([p.Sku, p.Name, p.StockUnit, StockQuantity.FromMicros(opening), StockQuantity.FromMicros(initial), StockQuantity.FromMicros(receipt),
                StockQuantity.FromMicros(sales), StockQuantity.FromMicros(saleReturns), StockQuantity.FromMicros(purchaseReturns), StockQuantity.FromMicros(adjusted), StockQuantity.FromMicros(opening + initial + receipt + sales + saleReturns + purchaseReturns + adjusted)]);
        }
        return new("stock-reconciliation", b.Currency, ["SKU", "Product", "Base unit", "Opening", "Initial stock", "Purchases", "Sales delta", "Sales returns", "Purchase returns delta", "Adjustments", "Closing"], rows, count, page, size);
    }
    private static object View(DebtAgeMinor a, int places) => new { Unscheduled = InventoryMoney.FromMinor(a.Unscheduled, places), Current = InventoryMoney.FromMinor(a.Current, places),
        Days1To30 = InventoryMoney.FromMinor(a.Days1To30, places), Days31To60 = InventoryMoney.FromMinor(a.Days31To60, places),
        Days61To90 = InventoryMoney.FromMinor(a.Days61To90, places), Over90 = InventoryMoney.FromMinor(a.Over90, places), Total = InventoryMoney.FromMinor(a.Total, places) };
    internal static void Page(int page, int size)
    { if (page is < 1 or > 1000 || size is < 1 or > 100 || (long)(page - 1) * size > 10000) throw new BusinessRequestException(400, "Invalid pagination"); }
    internal sealed class DebtAgeMinor
    { public Guid CustomerId { get; init; } public long Unscheduled { get; init; } public long Current { get; init; } public long Days1To30 { get; init; } public long Days31To60 { get; init; } public long Days61To90 { get; init; } public long Over90 { get; init; } public long Total { get; init; } }
    internal sealed record DebtReminderTotals(int CustomerCount, long AmountMinor);
}
