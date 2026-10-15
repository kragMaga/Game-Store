using System.ComponentModel.DataAnnotations;

namespace GameStore.Api.Dtos;

public sealed record ConfirmEmailDto(
    [Required] string Email,
    [Required] string Token);
