using System.ComponentModel.DataAnnotations;

namespace GameStore.Api.Dtos;

public record class LoginDto(
    [Required] string Username,
    [Required] string Password
);
