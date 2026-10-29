using System.ComponentModel.DataAnnotations;

namespace TradeNest.Api.Dtos;

public sealed record MfaDisableDto(
    [Required] string CurrentPassword,
    [Required] string Code);
