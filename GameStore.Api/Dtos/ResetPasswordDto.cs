using System.ComponentModel.DataAnnotations;

namespace GameStore.Api.Dtos;

public sealed record ResetPasswordDto(
    [Required, EmailAddress, StringLength(256)] string Email,
    [Required] string Token,
    [Required, MinLength(8)] string NewPassword);
