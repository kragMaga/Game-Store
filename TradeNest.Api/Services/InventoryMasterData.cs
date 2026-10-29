using TradeNest.Api.Data;
using TradeNest.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace TradeNest.Api.Services;

// Shared by interactive product creation and spreadsheet imports. Older clients
// may submit a name; it resolves to one reusable tenant-owned master record.
internal static class InventoryMasterData
{
    public static async Task<UnitOfMeasure> ResolveUnitAsync(TradeNestContext db, Guid businessId, Guid? id, string name)
    {
        var normalized = name.Trim().ToUpperInvariant();
        var unit = id.HasValue
            ? await db.UnitsOfMeasure.SingleOrDefaultAsync(x => x.BusinessId == businessId && x.Id == id)
            : db.UnitsOfMeasure.Local.FirstOrDefault(x => x.BusinessId == businessId && x.NormalizedName == normalized)
                ?? await db.UnitsOfMeasure.SingleOrDefaultAsync(x => x.BusinessId == businessId && x.NormalizedName == normalized);
        if (id.HasValue && unit is null) throw new BusinessRequestException(404, "Unit of measure not found");
        if (unit is not null)
        {
            if (!unit.IsActive) throw new BusinessRequestException(409, "Unit of measure is inactive");
            return unit;
        }
        unit = new UnitOfMeasure { BusinessId = businessId, Name = name.Trim(), NormalizedName = normalized };
        db.UnitsOfMeasure.Add(unit);
        return unit;
    }

    public static async Task SnapshotAsync(TradeNestContext db, InventoryProduct product, StockMovement movement)
    {
        movement.CategoryId = product.CategoryId;
        movement.CategoryName = product.CategoryId.HasValue
            ? await db.ProductCategories.Where(x => x.BusinessId == product.BusinessId && x.Id == product.CategoryId).Select(x => x.Name).SingleAsync()
            : null;
        movement.CategoryRecorded = true;
    }
}
