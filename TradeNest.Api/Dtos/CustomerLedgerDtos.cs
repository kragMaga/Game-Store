using System.ComponentModel.DataAnnotations;

namespace TradeNest.Api.Dtos;

// Staff may look customers up for sales. Creation/profile changes/opening debts
// remain administrative operations; customer balances have separate permissions.
public sealed record CreateCustomerDto(
    [Required, RegularExpression("^[A-Za-z0-9._-]{1,64}$")] string Code,
    [Required, StringLength(160, MinimumLength = 2)] string Name,
    [EmailAddress, StringLength(254)] string? Email = null,
    [StringLength(50)] string? Phone = null);
public sealed record UpdateCustomerDto(
    [Required, StringLength(160, MinimumLength = 2)] string Name,
    bool IsActive,
    [EmailAddress, StringLength(254)] string? Email = null,
    [StringLength(50)] string? Phone = null);
public sealed record OpeningCustomerDebtDto(decimal Amount, DateOnly EffectiveDate,
    [Required, StringLength(300, MinimumLength = 3)] string Reason,
    [Required, RegularExpression("^[A-Za-z0-9._-]{8,64}$")] string IdempotencyKey,
    DateOnly? DueDate = null);
public sealed record PaymentAllocationRequest(Guid ChargeId, decimal Amount);
public sealed record RecordCustomerPaymentDto(decimal Amount, DateOnly EffectiveDate,
    [Required, RegularExpression("^(Cash|BankTransfer|Card|Other)$")] string Method,
    [Required, MinLength(1), MaxLength(100)] IReadOnlyList<PaymentAllocationRequest> Allocations,
    [Required, RegularExpression("^[A-Za-z0-9._-]{8,64}$")] string IdempotencyKey,
    [StringLength(100)] string? Reference = null);
public sealed record ReverseCustomerPaymentDto(DateOnly EffectiveDate,
    [Required, StringLength(300, MinimumLength = 3)] string Reason,
    [Required, RegularExpression("^[A-Za-z0-9._-]{8,64}$")] string IdempotencyKey);

public sealed record CustomerDto(Guid Id, string Code, string Name, string? Email, string? Phone, bool IsActive);
public sealed record CustomerBalanceDto(Guid CustomerId, string Code, string Name, string Currency,
    int CurrencyDecimalPlaces, decimal OpeningDebt, decimal SalesTotal, decimal PaymentsReceived,
    decimal Outstanding, decimal Overdue);
public sealed record CustomerChargeDto(Guid Id, Guid? SaleId, string Kind, DateOnly EffectiveDate,
    DateOnly? DueDate, string Reason, decimal Amount, decimal Paid, decimal Outstanding);
public sealed record CustomerPaymentDto(Guid Id, Guid? CustomerId, string Kind, string Method,
    decimal Amount, DateOnly EffectiveDate, string? Reference, DateTime RecordedAtUtc, int ActorUserId, bool IsReversed, string? DocumentNumber = null);
public sealed record CustomerPaymentReversalDto(Guid Id, Guid PaymentId, string Reason, DateOnly EffectiveDate,
    DateTime RecordedAtUtc, int ActorUserId, string? DocumentNumber = null);
public sealed record CustomerStatementDto(CustomerBalanceDto Balance, PagedResponseDto<CustomerChargeDto> Charges);
public sealed record SaleFinancialStatus(bool PaymentsTracked, decimal? AmountPaid, decimal? Outstanding);
public sealed record CustomerPaymentAllocationDto(Guid ChargeId, Guid? SaleId, decimal Amount);
public sealed record CustomerPaymentDetailDto(CustomerPaymentDto Payment, IReadOnlyList<CustomerPaymentAllocationDto> Allocations,
    CustomerPaymentReversalDto? Reversal);

public sealed record PaymentEntryPreviewDto(CustomerPaymentDto Payment, bool AlreadyRecorded,
    IReadOnlyList<PaymentAllocationRequest> Allocations, IReadOnlyList<DuplicateDocumentDto> PossibleDuplicates);
public sealed record PaymentFilters(DateOnly? From = null, DateOnly? To = null, int? StaffUserId = null, string? Search = null);
