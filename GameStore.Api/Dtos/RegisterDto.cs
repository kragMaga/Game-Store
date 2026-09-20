using System.ComponentModel.DataAnnotations;

namespace GameStore.Api.Dtos;

public record class RegisterDto(
    [Required]
    [StringLength(20, MinimumLength = 5)]
    string Username,

    [Required] [MinLength(8)]
    string Password
);


