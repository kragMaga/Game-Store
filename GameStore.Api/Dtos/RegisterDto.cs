using System.ComponentModel.DataAnnotations;
using GameStore.Api.Services;

namespace GameStore.Api.Dtos;

public record class RegisterDto(
    [Required]
    [Username]
    string Username,

    [Required] [EmailAddress] [StringLength(256)]
    string Email,

    [Required] [MinLength(8)]
    string Password
);


