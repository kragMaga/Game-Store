using System.ComponentModel.DataAnnotations;

namespace TradeNest.Api.Dtos;

// UnitPrice means price PER ENTERED UNIT (for example per crate), not per base
// bottle. Amounts and stock are calculated by the server, never accepted as totals.
public sealed record InventoryTransactionLineRequest(Guid ProductId, Guid UnitId, decimal Quantity, decimal UnitPrice);
public sealed record RecordInventoryTransactionDto(
    DateOnly EffectiveDate,
    [Required, MinLength(1), MaxLength(100)] IReadOnlyList<InventoryTransactionLineRequest> Lines,
    [Required, RegularExpression("^[A-Za-z0-9._-]{8,64}$")] string IdempotencyKey,
    Guid? SupplierId = null,
    [StringLength(100)] string? Reference = null,
    Guid? CustomerId = null,
    decimal? AmountPaid = null,
    [RegularExpression("^(Cash|BankTransfer|Card|Other)$")] string? PaymentMethod = null,
    DateOnly? DueDate = null);
public sealed record CreateSupplierDto(
    [Required, StringLength(160, MinimumLength = 2)] string Name,
    [StringLength(150)] string? ContactReference = null);
public sealed record SupplierDto(Guid Id, string Name, string? ContactReference);
public sealed record InventoryTransactionSummaryDto(Guid Id, string Kind, Guid? SupplierId, string? Reference,
    string Currency, int CurrencyDecimalPlaces, decimal Total, DateOnly EffectiveDate, DateTime RecordedAtUtc, int ActorUserId,
    Guid? CustomerId = null, string? CustomerName = null, bool PaymentsTracked = false, decimal? AmountPaid = null, decimal? Outstanding = null, string? DocumentNumber = null);
public sealed record InventoryTransactionLineDto(Guid Id, Guid ProductId, Guid UnitId, Guid StockMovementId,
    string ProductName, string Sku, string UnitName, decimal Quantity, decimal ConversionFactor,
    decimal BaseQuantity, decimal UnitPrice, decimal LineTotal);
public sealed record InventoryTransactionDto(InventoryTransactionSummaryDto Transaction, IReadOnlyList<InventoryTransactionLineDto> Lines);

// Filters never widen visibility: staff still see only their own sales/purchases.
public sealed record TransactionFilters(DateOnly? From = null, DateOnly? To = null, Guid? ProductId = null,
    Guid? CustomerId = null, Guid? SupplierId = null, int? StaffUserId = null, string? Search = null);
public sealed record DuplicateDocumentDto(Guid Id, string? DocumentNumber, DateOnly EffectiveDate, string? Reference);
public sealed record TransactionEntryPreviewDto(InventoryTransactionDto Entry, bool AlreadyRecorded,
    IReadOnlyList<DuplicateDocumentDto> PossibleDuplicates);
