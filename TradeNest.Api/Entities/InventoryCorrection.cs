namespace TradeNest.Api.Entities;

// Corrections append evidence instead of rewriting completed transactions. Stock
// and customer-ledger effects commit in the same transaction as this header.
public sealed class InventoryCorrection : IInventoryRow
{
    public Guid BusinessId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    // Stable business reference; original external references remain separate.
    public string? DocumentNumber { get; set; }
    public required string Kind { get; set; }
    public Guid? TransactionId { get; set; }
    public required string Reason { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public DateTime RecordedAtUtc { get; set; }
    public int ActorUserId { get; set; }
    public required string Currency { get; set; }
    public int CurrencyDecimalPlaces { get; set; }
    public long AmountMinor { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string RequestFingerprint { get; set; }
}

public sealed class InventoryCorrectionLine : IInventoryRow
{
    public Guid BusinessId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CorrectionId { get; set; }
    public Guid? TransactionLineId { get; set; }
    public Guid ProductId { get; set; }
    public Guid StockMovementId { get; set; }
    public long QuantityMicros { get; set; }
    public long AmountMinor { get; set; }
    public bool Restockable { get; set; }
}

// DeltaMinor changes the charge, while RefundMinor reduces net money received.
// Keeping both allows a paid return to reduce sales without inventing customer debt.
public sealed class CustomerChargeAdjustment : IInventoryRow
{
    public Guid BusinessId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CorrectionId { get; set; }
    public Guid ChargeId { get; set; }
    public long DeltaMinor { get; set; }
    public long RefundMinor { get; set; }
    public string? RefundMethod { get; set; }
}

// The unique business/local-date key deduplicates daily email enqueueing across
// worker restarts and API instances. SMTP delivery itself remains at least once.
public sealed class BusinessNotificationDispatch : IInventoryRow
{
    public Guid BusinessId { get; set; }
    public DateOnly LocalDate { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int ItemCount { get; set; }
}
