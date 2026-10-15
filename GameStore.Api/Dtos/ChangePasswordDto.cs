using System.ComponentModel.DataAnnotations;

namespace GameStore.Api.Dtos;

public sealed record ChangePasswordDto(
    [Required] string CurrentPassword,
    [Required, MinLength(8)] string NewPassword);
