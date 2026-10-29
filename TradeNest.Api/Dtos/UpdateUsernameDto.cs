using System.ComponentModel.DataAnnotations;
using TradeNest.Api.Services;

namespace TradeNest.Api.Dtos;

public sealed record UpdateUsernameDto(
    [Required, Username] string Username);
