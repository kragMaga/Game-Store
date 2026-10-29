using System.ComponentModel.DataAnnotations;

namespace TradeNest.Api.Dtos;

public record class LoginDto(
    [Required] string Username,
    [Required] string Password
);
