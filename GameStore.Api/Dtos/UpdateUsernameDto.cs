using System.ComponentModel.DataAnnotations;

namespace GameStore.Api.Dtos;

public sealed record UpdateUsernameDto(
    [Required, StringLength(20, MinimumLength = 5)] string Username);
