using System.ComponentModel.DataAnnotations;

namespace GameStore.Api.Dtos;

public sealed record MfaDisableDto(
    [Required] string CurrentPassword,
    [Required] string Code);
