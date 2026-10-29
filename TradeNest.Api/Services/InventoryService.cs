using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TradeNest.Api.Data;
using TradeNest.Api.Dtos;
using TradeNest.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace TradeNest.Api.Services;

// Every public operation resolves current tenant membership before selecting the
// EF scope. Explicit tenant predicates remain alongside global filters as defence
// in depth, particularly for future bulk operations that bypass SaveChanges guards.
public sealed partial class InventoryService(TradeNestContext db, BusinessAccess access, TimeProvider clock)
{
    private async Task Scope(Guid businessId, int actorId, bool admin = false)
    {
        // A failed membership check must not leave a previously selected scope
        // available if another operation reuses this scoped service.
        db.InventoryBusinessId = null;
        await access.RequireAsync(businessId, actorId, admin);
        db.InventoryBusinessId = businessId;
    }

    public async Task<CategoryDto> CreateCategoryAsync(Guid businessId, int actorId, CreateCategoryDto request)
    {
        var name = Name(request.Name, 100);
        await using var transaction = await SecurityTransaction.BeginAsync(db);
        await Scope(businessId, actorId, admin: true);
        var normalized = name.ToUpperInvariant();
        if (await db.ProductCategories.AnyAsync(item => item.BusinessId == businessId && item.NormalizedName == normalized))
            throw new BusinessRequestException(409, "Category name already exists in this business");
        var category = new ProductCategory { BusinessId = businessId, Name = name, NormalizedName = normalized };
        db.ProductCategories.Add(category);
        Audit(businessId, actorId, "CategoryCreated");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return new(category.Id, category.Name);
    }

    public async Task<PagedResponseDto<CategoryDto>> CategoriesAsync(Guid businessId, int actorId, int page, int size)
    {
        Page(page, size);
        await Scope(businessId, actorId);
        var query = db.ProductCategories.AsNoTracking().Where(item => item.BusinessId == businessId);
        var count = await query.CountAsync();
        var rows = await query.OrderBy(item => item.NormalizedName).ThenBy(item => item.Id)
            .Skip((page - 1) * size).Take(size).Select(item => new CategoryDto(item.Id, item.Name, item.IsActive)).ToListAsync();
        return new(rows, page, size, count, Pages(count, size));
    }

    public async Task<InventoryProductDto> CreateProductAsync(Guid businessId, int actorId, CreateInventoryProductDto request)
    {
        var name = Name(request.Name, 160);
        var unitName = request.StockUnitId.HasValue ? request.StockUnit ?? "" : Name(request.StockUnit!, 40, minimum: 1);
        var reorder = StockQuantity.ToMicros(request.ReorderLevel);
        WholeStock(reorder, request.AllowsFractionalStock);
        await using var transaction = await SecurityTransaction.BeginAsync(db);
        await Scope(businessId, actorId, admin: true);
        await CategoryExists(businessId, request.CategoryId);
        var master = await InventoryMasterData.ResolveUnitAsync(db, businessId, request.StockUnitId, unitName);
        unitName = master.Name;
        var normalizedSku = request.Sku.ToUpperInvariant();
        if (await db.InventoryProducts.AnyAsync(item => item.BusinessId == businessId && item.NormalizedSku == normalizedSku))
            throw new BusinessRequestException(409, "SKU already exists in this business");
        var product = new InventoryProduct { BusinessId = businessId, Name = name, Sku = request.Sku,
            NormalizedSku = normalizedSku, CategoryId = request.CategoryId, StockUnit = unitName, StockUnitId = master.Id,
            AllowsFractionalStock = request.AllowsFractionalStock, ReorderLevelMicros = reorder,
            DefaultSellingPriceMicros = request.DefaultSellingPrice.HasValue ? InventoryMoney.PriceMicros(request.DefaultSellingPrice.Value) : null,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime };
        db.InventoryProducts.Add(product);
        // A base-unit option always exists and has factor exactly one. This lets
        // opening stock use the same conversion pipeline as a crate/bag/roll.
        db.ProductUnits.Add(new ProductUnit { BusinessId = businessId, ProductId = product.Id,
            Name = unitName, NormalizedName = unitName.ToUpperInvariant(), UnitOfMeasureId = master.Id, IsBaseUnit = true,
            AllowsFractionalQuantity = request.AllowsFractionalStock, ConversionFactorMicros = StockQuantity.Scale });
        Audit(businessId, actorId, "ProductCreated");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return Product(product);
    }

    public async Task<PagedResponseDto<InventoryProductDto>> ProductsAsync(Guid businessId, int actorId, int page,
        int size, string? search, bool lowStockOnly = false)
    {
        Page(page, size);
        if (search?.Length > 100) throw new BusinessRequestException(400, "Search cannot exceed 100 characters");
        await Scope(businessId, actorId);
        var query = db.InventoryProducts.AsNoTracking().Where(item => item.BusinessId == businessId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            // Parameterized literal matching; no raw SQL or exposed FTS operators.
            var term = search.Trim();
            var pattern = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            var skuTerm = term.ToUpperInvariant();
            query = query.Where(item => EF.Functions.Like(item.Name, pattern, "\\") || item.NormalizedSku.Contains(skuTerm));
        }
        if (lowStockOnly) query = query.Where(item => item.IsActive && item.QuantityOnHandMicros <= item.ReorderLevelMicros);
        var count = await query.CountAsync();
        var rows = await query.OrderBy(item => item.NormalizedSku).ThenBy(item => item.Id)
            .Skip((page - 1) * size).Take(size).ToListAsync();
        return new(rows.Select(Product).ToList(), page, size, count, Pages(count, size));
    }

    public async Task<InventoryProductDto> GetProductAsync(Guid businessId, Guid productId, int actorId)
    {
        await Scope(businessId, actorId);
        return Product(await FindProduct(businessId, productId));
    }

    public async Task UpdateProductAsync(Guid businessId, Guid productId, int actorId, UpdateInventoryProductDto request)
    {
        var name = Name(request.Name, 160);
        var reorder = StockQuantity.ToMicros(request.ReorderLevel);
        await using var transaction = await SecurityTransaction.BeginAsync(db);
        await Scope(businessId, actorId, admin: true);
        var product = await FindProduct(businessId, productId, tracked: true);
        WholeStock(reorder, product.AllowsFractionalStock);
        // Keeping an existing inactive category is allowed; selecting it anew is not.
        if (request.CategoryId != product.CategoryId) await CategoryExists(businessId, request.CategoryId);
        product.Name = name;
        product.CategoryId = request.CategoryId;
        product.ReorderLevelMicros = reorder;
        product.IsActive = request.IsActive;
        product.DefaultSellingPriceMicros = request.DefaultSellingPrice.HasValue ? InventoryMoney.PriceMicros(request.DefaultSellingPrice.Value) : null;
        // SKU, stock-unit meaning, and fractional rules are intentionally immutable
        // once created. Editing them must not reinterpret historical quantities.
        Audit(businessId, actorId, "ProductUpdated");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public async Task<ProductUnitDto> CreateUnitAsync(Guid businessId, Guid productId, int actorId, CreateProductUnitDto request)
    {
        var name = request.UnitOfMeasureId.HasValue ? request.Name ?? "" : Name(request.Name!, 40, minimum: 1);
        var factor = StockQuantity.ToMicros(request.ConversionFactor, allowZero: false);
        await using var transaction = await SecurityTransaction.BeginAsync(db);
        await Scope(businessId, actorId, admin: true);
        var product = await FindProduct(businessId, productId);
        WholeStock(factor, product.AllowsFractionalStock);
        var master = await InventoryMasterData.ResolveUnitAsync(db, businessId, request.UnitOfMeasureId, name);
        name = master.Name;
        var normalized = name.ToUpperInvariant();
        if (await db.ProductUnits.AnyAsync(item => item.BusinessId == businessId && item.ProductId == productId && item.NormalizedName == normalized))
            throw new BusinessRequestException(409, "Unit name already exists for this product");
        var unit = new ProductUnit { BusinessId = businessId, ProductId = productId, Name = name,
            NormalizedName = normalized, UnitOfMeasureId = master.Id, ConversionFactorMicros = factor, AllowsFractionalQuantity = request.AllowsFractionalQuantity };
        db.ProductUnits.Add(unit);
        Audit(businessId, actorId, "ProductUnitCreated");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return Unit(unit);
    }

    public async Task<PagedResponseDto<ProductUnitDto>> UnitsAsync(Guid businessId, Guid productId, int actorId, int page, int size)
    {
        Page(page, size);
        await Scope(businessId, actorId);
        _ = await FindProduct(businessId, productId);
        var query = db.ProductUnits.AsNoTracking().Where(item => item.BusinessId == businessId && item.ProductId == productId);
        var count = await query.CountAsync();
        var rows = await query.OrderByDescending(item => item.IsBaseUnit).ThenBy(item => item.Name).ThenBy(item => item.Id)
            .Skip((page - 1) * size).Take(size).ToListAsync();
        return new(rows.Select(Unit).ToList(), page, size, count, Pages(count, size));
    }

    public async Task<StockMovementDto> OpeningStockAsync(Guid businessId, Guid productId, int actorId, OpeningStockDto request)
    {
        var quantity = StockQuantity.ToMicros(request.Quantity);
        var reason = Name(request.Reason, 300, minimum: 3);
        if (request.EffectiveDate == default)
            throw new BusinessRequestException(400, "Opening date is required");
        var fingerprint = Fingerprint(productId, request, reason);
        await using var transaction = await SecurityTransaction.BeginAsync(db);
        await Scope(businessId, actorId, admin: true);
        // Check idempotency before mutable product state. A legitimate retry must
        // still return the original result after the product is later archived.
        var existing = await db.StockMovements.AsNoTracking().SingleOrDefaultAsync(item =>
            item.BusinessId == businessId && item.IdempotencyKey == request.IdempotencyKey);
        if (existing is not null)
        {
            if (existing.RequestFingerprint != fingerprint)
                throw new BusinessRequestException(409, "Idempotency key has already been used for a different request");
            return Movement(existing);
        }
        // Business dates use the configured local timezone, not the API server's
        // timezone. Check new entries after replay detection so a later timezone
        // setting change cannot invalidate a previously successful request's retry.
        var timeZoneId = await db.Businesses.Where(item => item.Id == businessId).Select(item => item.TimeZoneId).SingleAsync();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)).DateTime);
        if (request.EffectiveDate > today) throw new BusinessRequestException(400, "Opening stock cannot be future-dated");
        var product = await FindProduct(businessId, productId, tracked: true);
        if (!product.IsActive) throw new BusinessRequestException(409, "Product is inactive");
        if (product.OpeningStockRecorded || product.QuantityOnHandMicros != 0 ||
            await db.StockMovements.AnyAsync(item => item.BusinessId == businessId && item.ProductId == productId))
            throw new BusinessRequestException(409, "Opening stock must be recorded only once, before other movements");
        var unit = await db.ProductUnits.SingleOrDefaultAsync(item => item.BusinessId == businessId && item.ProductId == productId && item.Id == request.UnitId)
            ?? throw new BusinessRequestException(404, "Product unit not found");
        WholeStock(quantity, unit.AllowsFractionalQuantity);
        var baseQuantity = StockQuantity.ConvertToBase(quantity, unit.ConversionFactorMicros);
        WholeStock(baseQuantity, product.AllowsFractionalStock);
        var movement = new StockMovement { BusinessId = businessId, ProductId = productId, UnitId = unit.Id,
            Kind = "OpeningStock", SequenceNumber = 1, UnitName = unit.Name, UnitQuantityMicros = quantity,
            ConversionFactorMicros = unit.ConversionFactorMicros, BaseQuantityMicros = baseQuantity,
            BalanceAfterMicros = baseQuantity, Reason = reason, EffectiveDate = request.EffectiveDate,
            RecordedAtUtc = clock.GetUtcNow().UtcDateTime, ActorUserId = actorId,
            IdempotencyKey = request.IdempotencyKey, RequestFingerprint = fingerprint };
        await InventoryMasterData.SnapshotAsync(db, product, movement);
        product.QuantityOnHandMicros = baseQuantity;
        product.OpeningStockRecorded = true;
        db.StockMovements.Add(movement);
        Audit(businessId, actorId, "OpeningStockRecorded");
        // Failure of ANY write rolls back the balance, initialization flag, movement,
        // and audit event. No quantity is returned as successful before commit.
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return Movement(movement);
    }

    public async Task<PagedResponseDto<StockMovementDto>> MovementsAsync(Guid businessId, Guid productId, int actorId, int page, int size)
    {
        Page(page, size);
        await Scope(businessId, actorId);
        _ = await FindProduct(businessId, productId);
        var query = db.StockMovements.AsNoTracking().Where(item => item.BusinessId == businessId && item.ProductId == productId);
        var count = await query.CountAsync();
        var rows = await query.OrderByDescending(item => item.SequenceNumber)
            .Skip((page - 1) * size).Take(size).ToListAsync();
        return new(rows.Select(Movement).ToList(), page, size, count, Pages(count, size));
    }

    private async Task<InventoryProduct> FindProduct(Guid businessId, Guid productId, bool tracked = false)
    {
        // Read operations must not reuse a previously tracked balance after a
        // different request commits stock. Only transactional writers need tracking.
        var query = tracked ? db.InventoryProducts : db.InventoryProducts.AsNoTracking();
        return await query.SingleOrDefaultAsync(item => item.BusinessId == businessId && item.Id == productId)
            ?? throw new BusinessRequestException(404, "Product not found");
    }

    private async Task CategoryExists(Guid businessId, Guid? id)
    {
        if (id.HasValue && !await db.ProductCategories.AnyAsync(item => item.BusinessId == businessId && item.Id == id && item.IsActive))
            throw new BusinessRequestException(404, "Category not found");
    }

    private static void WholeStock(long value, bool fractional)
    {
        if (!fractional && value % StockQuantity.Scale != 0)
            throw new BusinessRequestException(400, "This unit requires whole quantities");
    }

    private static string Name(string value, int maximum, int minimum = 2)
    {
        var trimmed = value?.Trim() ?? "";
        if (trimmed.Length < minimum || trimmed.Length > maximum)
            throw new BusinessRequestException(400, $"Text must contain {minimum}-{maximum} characters");
        return trimmed;
    }

    private static void Page(int page, int size)
    {
        if (page is < 1 or > 1000 || size is < 1 or > 100 || (long)(page - 1) * size > 10_000)
            throw new BusinessRequestException(400, "Invalid pagination parameters");
    }
    private static int Pages(int count, int size) => (int)Math.Ceiling(count / (double)size);
    private void Audit(Guid businessId, int actorId, string action) => db.BusinessAuditEvents.Add(new()
        { BusinessId = businessId, ActorUserId = actorId, Action = action, CreatedAtUtc = clock.GetUtcNow().UtcDateTime });
    private static string Fingerprint(Guid productId, OpeningStockDto request, string reason) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', "OpeningStock", productId,
            request.UnitId, request.Quantity.ToString("G29", CultureInfo.InvariantCulture), request.EffectiveDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), reason))));
    private static InventoryProductDto Product(InventoryProduct item) => new(item.Id, item.Name, item.Sku, item.CategoryId,
        item.StockUnit, item.AllowsFractionalStock, StockQuantity.FromMicros(item.QuantityOnHandMicros),
        StockQuantity.FromMicros(item.ReorderLevelMicros), item.IsActive && item.QuantityOnHandMicros <= item.ReorderLevelMicros,
        item.OpeningStockRecorded, item.IsActive, item.DefaultSellingPriceMicros.HasValue ? StockQuantity.FromMicros(item.DefaultSellingPriceMicros.Value) : null, item.StockUnitId);
    private static ProductUnitDto Unit(ProductUnit item) => new(item.Id, item.Name,
        StockQuantity.FromMicros(item.ConversionFactorMicros), item.AllowsFractionalQuantity, item.IsBaseUnit, item.UnitOfMeasureId);
    private static StockMovementDto Movement(StockMovement item) => new(item.Id, item.ProductId, item.Kind, item.UnitName,
        StockQuantity.FromMicros(item.UnitQuantityMicros), StockQuantity.FromMicros(item.ConversionFactorMicros),
        StockQuantity.FromMicros(item.BaseQuantityMicros), StockQuantity.FromMicros(item.BalanceAfterMicros), item.Reason,
        item.EffectiveDate, item.RecordedAtUtc, item.ActorUserId, item.SequenceNumber);
}
