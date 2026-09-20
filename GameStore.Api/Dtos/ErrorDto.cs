namespace GameStore.Api.Dtos;

public record class ErrorDto(
    string Error,
    string Message
);