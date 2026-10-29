namespace TradeNest.Api.Dtos;

public sealed record AuthTokensDto(
    string AccessToken,
    string RefreshToken,
    string TokenType,
    int ExpiresIn);
