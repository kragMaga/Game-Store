using System.ComponentModel.DataAnnotations;

namespace GameStore.Api.Dtos;

public sealed record MfaLoginDto(
    [Required] string ChallengeToken,
    [Required] string Code);
