using System.ComponentModel.DataAnnotations;

namespace TradeNest.Api.Dtos;

public sealed record ForgotPasswordDto([Required, EmailAddress, StringLength(256)] string Email);
