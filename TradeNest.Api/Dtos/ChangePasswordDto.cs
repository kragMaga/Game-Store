using System.ComponentModel.DataAnnotations;

namespace TradeNest.Api.Dtos;

public sealed record ChangePasswordDto(
    [Required] string CurrentPassword,
    [Required, MinLength(8)] string NewPassword);
