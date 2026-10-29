using System.ComponentModel.DataAnnotations;

namespace TradeNest.Api.Dtos;

public sealed record MfaVerifyDto([Required] string Code);
