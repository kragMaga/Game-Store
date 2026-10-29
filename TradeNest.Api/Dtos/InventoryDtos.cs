using System.ComponentModel.DataAnnotations;

namespace TradeNest.Api.Dtos;

// QuantityOnHand is intentionally absent from product edits. Opening balances and
// future receipts/sales must create an auditable movement in the same transaction.
public sealed record CreateCategoryDto([Required, StringLength(100, MinimumLength = 2)] string Name);
public sealed record CreateInventoryProductDto(
    [Required, StringLength(160, MinimumLength = 2)] string Name,
    [Required, RegularExpression("^[A-Za-z0-9._-]{1,64}$")] string Sku,
    [StringLength(40, MinimumLength = 1)] string? StockUnit,
    bool AllowsFractionalStock,
    decimal ReorderLevel = 0,
    Guid? CategoryId = null, decimal? DefaultSellingPrice = null, Guid? StockUnitId = null);
public sealed record UpdateInventoryProductDto(
    [Required, StringLength(160, MinimumLength = 2)] string Name,
    decimal ReorderLevel,
    bool IsActive,
    Guid? CategoryId = null, decimal? DefaultSellingPrice = null);
public sealed record CreateProductUnitDto(
    [StringLength(40, MinimumLength = 1)] string? Name,
    decimal ConversionFactor,
    bool AllowsFractionalQuantity = false, Guid? UnitOfMeasureId = null);
public sealed record OpeningStockDto(
    Guid UnitId,
    decimal Quantity,
    DateOnly EffectiveDate,
    [Required, StringLength(300, MinimumLength = 3)] string Reason,
    [Required, RegularExpression("^[A-Za-z0-9._-]{8,64}$")] string IdempotencyKey);

// Explicit projections make the base-unit meaning visible and hide internal
// scaled numbers, tenant scope state, and idempotency fingerprints.
public sealed record CategoryDto(Guid Id, string Name, bool IsActive = true);
public sealed record InventoryProductDto(Guid Id, string Name, string Sku, Guid? CategoryId,
    string StockUnit, bool AllowsFractionalStock, decimal QuantityOnHand, decimal ReorderLevel,
    bool IsLowStock, bool OpeningStockRecorded, bool IsActive, decimal? DefaultSellingPrice = null, Guid StockUnitId = default);
public sealed record ProductUnitDto(Guid Id, string Name, decimal ConversionFactor,
    bool AllowsFractionalQuantity, bool IsBaseUnit, Guid UnitOfMeasureId = default);
public sealed record StockMovementDto(Guid Id, Guid ProductId, string Kind, string UnitName,
    decimal Quantity, decimal ConversionFactor, decimal BaseQuantity, decimal BalanceAfter,
    string Reason, DateOnly EffectiveDate, DateTime RecordedAtUtc, int ActorUserId, long SequenceNumber);

// Deactivation hides entries from new selection without deleting referenced history.
public sealed record UpdateMasterDataDto([Required, StringLength(100, MinimumLength = 1)] string Name, bool IsActive);
public sealed record UnitOfMeasureDto(Guid Id, string Name, bool IsActive);
public sealed record CreateUnitOfMeasureDto([Required, StringLength(40, MinimumLength = 1)] string Name);
