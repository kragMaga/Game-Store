using System.ComponentModel.DataAnnotations;

namespace GameStore.Api.Dtos;

public sealed record ConfirmEmailDto(
    [Required, EmailAddress, StringLength(256)] string Email,
    [Required] string Token);
