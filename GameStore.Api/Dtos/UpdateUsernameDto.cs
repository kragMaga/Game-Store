using System.ComponentModel.DataAnnotations;
using GameStore.Api.Services;

namespace GameStore.Api.Dtos;

public sealed record UpdateUsernameDto(
    [Required, Username] string Username);
