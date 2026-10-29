using TradeNest.Api.Data;
using TradeNest.Api.Dtos;
using TradeNest.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace TradeNest.Api.Services;

public sealed partial class InventoryService
{
    public async Task<UnitOfMeasureDto> CreateMeasureAsync(Guid businessId, int actorId, CreateUnitOfMeasureDto request)
    {
        var name = Name(request.Name, 40, 1);
        await using var tx = await SecurityTransaction.BeginAsync(db);
        await Scope(businessId, actorId, true);
        if (await db.UnitsOfMeasure.AnyAsync(x => x.BusinessId == businessId && x.NormalizedName == name.ToUpperInvariant()))
            throw new BusinessRequestException(409, "Unit of measure already exists in this business");
        var unit = await InventoryMasterData.ResolveUnitAsync(db, businessId, null, name);
        Audit(businessId, actorId, "UnitOfMeasureCreated");
        await db.SaveChangesAsync(); await tx.CommitAsync();
        return new(unit.Id, unit.Name, unit.IsActive);
    }

    public async Task<PagedResponseDto<UnitOfMeasureDto>> MeasuresAsync(Guid businessId, int actorId, int page, int size)
    {
        Page(page, size); await Scope(businessId, actorId);
        var query = db.UnitsOfMeasure.AsNoTracking().Where(x => x.BusinessId == businessId);
        var count = await query.CountAsync();
        var rows = await query.OrderBy(x => x.NormalizedName).ThenBy(x => x.Id).Skip((page - 1) * size).Take(size)
            .Select(x => new UnitOfMeasureDto(x.Id, x.Name, x.IsActive)).ToListAsync();
        return new(rows, page, size, count, Pages(count, size));
    }

    // Masters are never hard-deleted: posted records must keep valid references.
    // Descriptive renames do not change saved unit labels, conversions or categories.
    public async Task UpdateMasterAsync(Guid businessId, Guid id, int actorId, UpdateMasterDataDto request, bool category)
    {
        var name = Name(request.Name, category ? 100 : 40, category ? 2 : 1);
        var normalized = name.ToUpperInvariant();
        await using var tx = await SecurityTransaction.BeginAsync(db);
        await Scope(businessId, actorId, true);
        if (category)
        {
            var row = await db.ProductCategories.SingleOrDefaultAsync(x => x.BusinessId == businessId && x.Id == id)
                ?? throw new BusinessRequestException(404, "Category not found");
            if (await db.ProductCategories.AnyAsync(x => x.BusinessId == businessId && x.Id != id && x.NormalizedName == normalized))
                throw new BusinessRequestException(409, "Category name already exists in this business");
            row.Name = name; row.NormalizedName = normalized; row.IsActive = request.IsActive;
        }
        else
        {
            var row = await db.UnitsOfMeasure.SingleOrDefaultAsync(x => x.BusinessId == businessId && x.Id == id)
                ?? throw new BusinessRequestException(404, "Unit of measure not found");
            if (await db.UnitsOfMeasure.AnyAsync(x => x.BusinessId == businessId && x.Id != id && x.NormalizedName == normalized))
                throw new BusinessRequestException(409, "Unit of measure already exists in this business");
            row.Name = name; row.NormalizedName = normalized; row.IsActive = request.IsActive;
        }
        Audit(businessId, actorId, category ? "CategoryUpdated" : "UnitOfMeasureUpdated");
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
}
