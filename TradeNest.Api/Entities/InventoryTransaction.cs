namespace TradeNest.Api.Entities;

// These are records of transactions performed elsewhere, not invoices, payment
// processing, or an accounting ledger. Completed headers/lines are immutable.
public sealed class InventoryTransaction : IInventoryRow
{
    public Guid BusinessId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    // Stable business reference; original external references remain separate.
    public string? DocumentNumber { get; set; }
    public required string Kind { get; set; }
    public Guid? SupplierId { get; set; }
    public Guid? CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public string? Reference { get; set; }
    public required string Currency { get; set; }
    public int CurrencyDecimalPlaces { get; set; }
    public long TotalMinor { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public DateTime RecordedAtUtc { get; set; }
    public int ActorUserId { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string RequestFingerprint { get; set; }
}

public sealed class InventoryTransactionLine : IInventoryRow
{
    public Guid BusinessId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TransactionId { get; set; }
    public int Position { get; set; }
    public Guid ProductId { get; set; }
    public Guid UnitId { get; set; }
    public Guid StockMovementId { get; set; }
    // Snapshot descriptive data as well as conversions/prices. Later product
    // edits must not rewrite what the business originally bought or sold.
    public required string ProductName { get; set; }
    public required string Sku { get; set; }
    public required string UnitName { get; set; }
    public long QuantityMicros { get; set; }
    public long ConversionFactorMicros { get; set; }
    public long BaseQuantityMicros { get; set; }
    public long UnitPriceMicros { get; set; }
    public long LineTotalMinor { get; set; }
}

public sealed class Supplier : IInventoryRow
{
    public Guid BusinessId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    public string? ContactReference { get; set; }
}
