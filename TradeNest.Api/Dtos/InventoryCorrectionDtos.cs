using System.ComponentModel.DataAnnotations;
namespace TradeNest.Api.Dtos;

// Quantities use the original document unit; stock counts use the base unit.
public sealed record ReturnLineDto(Guid TransactionLineId, decimal Quantity, bool Restockable = true);
public sealed record ReturnInventoryDto(DateOnly EffectiveDate,
    [Required, StringLength(300, MinimumLength = 3)] string Reason,
    [Required, RegularExpression("^[A-Za-z0-9._-]{8,64}$")] string IdempotencyKey,
    IReadOnlyList<ReturnLineDto>? Lines = null, bool ReverseRemaining = false,
    decimal RefundAmount = 0, string? RefundMethod = null);
public sealed record AdjustStockDto(decimal Quantity, decimal ExpectedQuantityOnHand, DateOnly EffectiveDate,
    [Required, StringLength(300, MinimumLength = 3)] string Reason,
    [Required, RegularExpression("^[A-Za-z0-9._-]{8,64}$")] string IdempotencyKey);
public sealed record CorrectOpeningDebtDto(decimal Amount, DateOnly EffectiveDate,
    [Required, StringLength(300, MinimumLength = 3)] string Reason,
    [Required, RegularExpression("^[A-Za-z0-9._-]{8,64}$")] string IdempotencyKey,
    decimal RefundAmount = 0, string? RefundMethod = null);
public sealed record CorrectionDto(Guid Id, string Kind, Guid? TransactionId, string Reason,
    DateOnly EffectiveDate, DateTime RecordedAtUtc, int ActorUserId, string Currency,
    decimal Amount, decimal RefundAmount, string? DocumentNumber = null);
