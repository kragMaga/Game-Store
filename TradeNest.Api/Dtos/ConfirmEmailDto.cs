using System.ComponentModel.DataAnnotations;

namespace TradeNest.Api.Dtos;

public sealed record ConfirmEmailDto(
    [Required, EmailAddress, StringLength(256)] string Email,
    [Required] string Token);
