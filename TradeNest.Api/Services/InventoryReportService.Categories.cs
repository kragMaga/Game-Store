using TradeNest.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace TradeNest.Api.Services;

public sealed partial class InventoryReportService
{
    // Aggregation happens in SQL before pagination. Base unit IDs prevent adding
    // kilograms to pieces; original category labels survive product/master edits.
    public async Task<ReportTable> CategoriesAsync(Guid businessId, int actor, string report, DateOnly? startDate, DateOnly? to,
        Guid? categoryId = null, int page = 1, int size = 100, bool export = false)
    {
        Range(startDate, to);
        if (report is not ("inventory" or "sales" or "purchases" or "movements" or "corrections"))
            throw new BusinessRequestException(404, "Category report not found");
        if (page is < 1 or > 1000 || size is < 1 or > 100 || (long)(page - 1) * size > 10000)
            throw new BusinessRequestException(400, "Invalid pagination");
        if (report == "inventory" && (startDate.HasValue || to.HasValue))
            throw new BusinessRequestException(400, "Inventory reports show current stock; use movements for a date range");
        var business = await Scope(businessId, actor, true);
        IQueryable<CategoryFact> facts;
        if (report == "inventory")
        {
            facts = from p in db.InventoryProducts.AsNoTracking()
                    join c in db.ProductCategories on new { p.BusinessId, Id = p.CategoryId } equals new { c.BusinessId, Id = (Guid?)c.Id } into categories
                    from c in categories.DefaultIfEmpty()
                    where p.BusinessId == businessId
                    select new CategoryFact { CategoryId = p.CategoryId, CategoryName = c == null ? null : c.Name, Recorded = true,
                        UnitId = p.StockUnitId, UnitName = p.StockUnit, Kind = "CurrentStock", Quantity = p.QuantityOnHandMicros,
                        Amount = 0, Reorder = p.ReorderLevelMicros, LowStock = p.IsActive && p.QuantityOnHandMicros <= p.ReorderLevelMicros ? 1 : 0 };
        }
        else if (report is "sales" or "purchases")
        {
            var documents = Documents(businessId, report == "sales" ? "Sale" : "Purchase", startDate, to);
            facts = from l in db.InventoryTransactionLines.AsNoTracking()
                    join t in documents on new { l.BusinessId, Id = l.TransactionId } equals new { t.BusinessId, t.Id }
                    join m in db.StockMovements on new { l.BusinessId, Id = l.StockMovementId } equals new { m.BusinessId, m.Id }
                    join p in db.InventoryProducts on new { l.BusinessId, Id = l.ProductId } equals new { p.BusinessId, p.Id }
                    where l.BusinessId == businessId
                    select new CategoryFact { CategoryId = m.CategoryId, CategoryName = m.CategoryName, Recorded = m.CategoryRecorded,
                        UnitId = p.StockUnitId, UnitName = p.StockUnit, Kind = t.Kind, Quantity = l.BaseQuantityMicros, Amount = l.LineTotalMinor, Reorder = 0, LowStock = 0 };
        }
        else if (report == "corrections")
        {
            facts = from l in db.InventoryCorrectionLines.AsNoTracking()
                    join c in db.InventoryCorrections on new { l.BusinessId, Id = l.CorrectionId } equals new { c.BusinessId, c.Id }
                    join m in db.StockMovements on new { l.BusinessId, Id = l.StockMovementId } equals new { m.BusinessId, m.Id }
                    join p in db.InventoryProducts on new { l.BusinessId, Id = l.ProductId } equals new { p.BusinessId, p.Id }
                    where l.BusinessId == businessId && (!startDate.HasValue || c.EffectiveDate >= startDate) && (!to.HasValue || c.EffectiveDate <= to)
                    select new CategoryFact { CategoryId = m.CategoryId, CategoryName = m.CategoryName, Recorded = m.CategoryRecorded,
                        UnitId = p.StockUnitId, UnitName = p.StockUnit, Kind = c.Kind, Quantity = m.BaseQuantityMicros, Amount = l.AmountMinor, Reorder = 0, LowStock = 0 };
        }
        else
        {
            facts = from m in db.StockMovements.AsNoTracking()
                    join p in db.InventoryProducts on new { m.BusinessId, Id = m.ProductId } equals new { p.BusinessId, p.Id }
                    where m.BusinessId == businessId && (!startDate.HasValue || m.EffectiveDate >= startDate) && (!to.HasValue || m.EffectiveDate <= to)
                    select new CategoryFact { CategoryId = m.CategoryId, CategoryName = m.CategoryName, Recorded = m.CategoryRecorded,
                        UnitId = p.StockUnitId, UnitName = p.StockUnit, Kind = m.Kind, Quantity = m.BaseQuantityMicros, Amount = 0, Reorder = 0, LowStock = 0 };
        }
        if (categoryId.HasValue) facts = facts.Where(x => x.CategoryId == categoryId);
        var grouped = facts.GroupBy(x => new { x.CategoryId, x.CategoryName, x.Recorded, x.UnitId, x.UnitName, x.Kind })
            .Select(g => new { g.Key, Quantity = g.Sum(x => x.Quantity), Amount = g.Sum(x => x.Amount),
                Reorder = g.Sum(x => x.Reorder), LowStock = g.Sum(x => x.LowStock), Count = g.Count() });
        var count = await grouped.CountAsync();
        var data = await grouped.OrderBy(x => x.Key.CategoryId).ThenBy(x => x.Key.CategoryName).ThenBy(x => x.Key.Recorded)
            .ThenBy(x => x.Key.UnitId).ThenBy(x => x.Key.UnitName).ThenBy(x => x.Key.Kind)
            .Skip(export ? 0 : (page - 1) * size).Take(export ? ExportLimit + 1 : size).ToListAsync();
        if (export && data.Count > ExportLimit) throw new BusinessRequestException(413, "Export exceeds row limit; narrow the filters");
        // Amounts are gross line totals for sales/purchases and credited amounts
        // for returns. They are not profit, inventory valuation, or net revenue.
        var rows = data.Select(x => new object?[] { x.Key.CategoryId,
            x.Key.Recorded ? x.Key.CategoryName ?? "Uncategorized" : "Historical category not recorded",
            x.Key.Recorded, x.Key.UnitId, x.Key.UnitName, x.Key.Kind, x.Count,
            StockQuantity.FromMicros(x.Quantity), StockQuantity.FromMicros(x.Reorder), x.LowStock,
            business.Currency, InventoryMoney.FromMinor(x.Amount, business.CurrencyDecimalPlaces) }).ToList();
        return new("category-" + report, business.Currency, ["Category ID", "Category", "Category recorded", "Base unit ID", "Base unit", "Kind", "Record count",
            "Base quantity / stock delta", "Reorder quantity", "Low-stock products", "Currency", "Gross / credit amount"],
            rows, count, export ? 1 : page, export ? ExportLimit : size);
    }

    private sealed class CategoryFact
    {
        public Guid? CategoryId { get; set; }
        public string? CategoryName { get; set; }
        public bool Recorded { get; set; }
        public Guid UnitId { get; set; }
        public string UnitName { get; set; } = "";
        public string Kind { get; set; } = "";
        public long Quantity { get; set; }
        public long Amount { get; set; }
        public long Reorder { get; set; }
        public int LowStock { get; set; }
    }
}
