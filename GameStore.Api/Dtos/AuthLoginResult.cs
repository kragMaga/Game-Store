namespace GameStore.Api.Dtos;

public sealed record AuthLoginResult(AuthTokensDto? Tokens, string? MfaChallengeToken);
