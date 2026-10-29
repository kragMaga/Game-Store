using System.ComponentModel.DataAnnotations;

namespace TradeNest.Api.Dtos;

public sealed record MfaLoginDto(
    [Required] string ChallengeToken,
    [Required] string Code);
