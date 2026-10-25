using System.ComponentModel.DataAnnotations;

namespace GameStore.Api.Dtos;

public sealed record ConfirmEmailChangeDto(
    [Range(1, int.MaxValue)] int UserId,
    [Required, EmailAddress, StringLength(256)] string Email,
    [Required] string Token);
