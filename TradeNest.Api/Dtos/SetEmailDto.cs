using System.ComponentModel.DataAnnotations;

namespace TradeNest.Api.Dtos;

public sealed record SetEmailDto([Required, EmailAddress, StringLength(256)] string Email);
