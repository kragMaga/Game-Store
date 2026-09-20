namespace GameStore.Api.Dtos;

public record class LoginDto(
    string Username,
    string Password
);