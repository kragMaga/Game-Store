namespace TradeNest.Api.Entities;

// All inventory rows carry a tenant ID. Composite relationships and default query
// filters provide a second boundary behind the service's membership checks.
public interface IInventoryRow { Guid BusinessId { get; set; } }

// Reusable business-owned units; pack conversion factors still belong to ProductUnit.
public sealed class UnitOfMeasure : IInventoryRow
{
    public Guid BusinessId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class ProductCategory : IInventoryRow
{
    public bool IsActive { get; set; } = true;
    public Guid BusinessId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
}

public sealed class InventoryProduct : IInventoryRow
{
    public Guid StockUnitId { get; set; }
    public Guid BusinessId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? CategoryId { get; set; }
    public required string Name { get; set; }
    public required string Sku { get; set; }
    public required string NormalizedSku { get; set; }
    public required string StockUnit { get; set; }
    public bool AllowsFractionalStock { get; set; }
    // Optional suggestion per BASE stock unit. Actual transaction prices remain
    // explicit snapshots and are never rewritten when this default changes.
    public long? DefaultSellingPriceMicros { get; set; }
    public bool IsActive { get; set; } = true;
    // SQLite INTEGER stores exact millionths, avoiding floating-point stock drift.
    // This balance is maintained by stock transactions, never product-edit DTOs.
    public long ReorderLevelMicros { get; set; }
    public long QuantityOnHandMicros { get; set; }
    public bool OpeningStockRecorded { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class ProductUnit : IInventoryRow
{
    public Guid UnitOfMeasureId { get; set; }
    public Guid BusinessId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    public bool IsBaseUnit { get; set; }
    public bool AllowsFractionalQuantity { get; set; }
    // Example: a crate containing 24 bottles has ConversionFactorMicros=24000000.
    // Conversions are immutable; add another named unit if the pack size changes.
    public long ConversionFactorMicros { get; set; }
}

public sealed class StockMovement : IInventoryRow
{
    // Snapshot classification at posting time. Null category plus Recorded=true
    // means uncategorized; false means older history with no reliable snapshot.
    public Guid? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public bool CategoryRecorded { get; set; }
    public Guid BusinessId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public Guid UnitId { get; set; }
    // Timestamp ties are common for multi-line transactions. A per-product
    // sequence preserves the exact posting order of running stock balances.
    public long SequenceNumber { get; set; }
    public required string Kind { get; set; }
    public required string UnitName { get; set; }
    public long UnitQuantityMicros { get; set; }
    public long ConversionFactorMicros { get; set; }
    public long BaseQuantityMicros { get; set; }
    public long BalanceAfterMicros { get; set; }
    public required string Reason { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public DateTime RecordedAtUtc { get; set; }
    public int ActorUserId { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string RequestFingerprint { get; set; }
}
