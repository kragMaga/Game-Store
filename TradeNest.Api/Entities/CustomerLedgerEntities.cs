namespace TradeNest.Api.Entities;

// Customer identities are tenant-owned. Codes are stable; names/contact details
// can change without rewriting the customer snapshot on a completed sale.
public sealed class Customer : IInventoryRow
{
    public Guid BusinessId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Code { get; set; }
    public required string NormalizedCode { get; set; }
    public required string Name { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
}

// A charge is an immutable amount owed, originating from an opening debt or sale.
// Null CustomerId is permitted only for fully paid walk-in sales, not credit.
public sealed class CustomerCharge : IInventoryRow
{
    public Guid BusinessId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? CustomerId { get; set; }
    public Guid? SaleId { get; set; }
    public required string Kind { get; set; }
    public long AmountMinor { get; set; }
    public required string Currency { get; set; }
    public int CurrencyDecimalPlaces { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public DateOnly? DueDate { get; set; }
    public required string Reason { get; set; }
    public DateTime RecordedAtUtc { get; set; }
    public int ActorUserId { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string RequestFingerprint { get; set; }
}

public sealed class CustomerPayment : IInventoryRow
{
    public Guid BusinessId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    // Stable business reference; original external references remain separate.
    public string? DocumentNumber { get; set; }
    public Guid? CustomerId { get; set; }
    public required string Kind { get; set; }
    public required string Method { get; set; }
    public long AmountMinor { get; set; }
    public required string Currency { get; set; }
    public int CurrencyDecimalPlaces { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public string? Reference { get; set; }
    public DateTime RecordedAtUtc { get; set; }
    public int ActorUserId { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string RequestFingerprint { get; set; }
}

// One receipt may settle several debts, but all allocations must belong to the
// same customer and sum exactly to the payment. No floating/unallocated credit yet.
public sealed class CustomerPaymentAllocation : IInventoryRow
{
    public Guid BusinessId { get; set; }
    public Guid PaymentId { get; set; }
    public Guid ChargeId { get; set; }
    public long AmountMinor { get; set; }
}

// Correct a mistaken receipt with a separate append-only event, never by deleting
// payment history. A reversal restores debt; it does not represent a cash refund.
public sealed class CustomerPaymentReversal : IInventoryRow
{
    public Guid BusinessId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    // Stable business reference; original external references remain separate.
    public string? DocumentNumber { get; set; }
    public Guid PaymentId { get; set; }
    public required string Reason { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public DateTime RecordedAtUtc { get; set; }
    public int ActorUserId { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string RequestFingerprint { get; set; }
}
