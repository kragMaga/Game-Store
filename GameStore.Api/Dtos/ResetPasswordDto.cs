using System.ComponentModel.DataAnnotations;

namespace GameStore.Api.Dtos;

public sealed record ResetPasswordDto(
    [Required, EmailAddress] string Email,
    [Required] string Token,
    [Required, MinLength(8)] string NewPassword);
