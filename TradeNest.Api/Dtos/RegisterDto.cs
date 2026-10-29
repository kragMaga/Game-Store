using System.ComponentModel.DataAnnotations;
using TradeNest.Api.Services;

namespace TradeNest.Api.Dtos;

public record class RegisterDto(
    [Required]
    [Username]
    string Username,

    [Required] [EmailAddress] [StringLength(256)]
    string Email,

    [Required] [MinLength(8)]
    string Password
);


