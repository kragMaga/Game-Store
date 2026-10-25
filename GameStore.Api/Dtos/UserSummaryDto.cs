namespace GameStore.Api.Dtos;

// Administrative lists expose only these fields, never Identity hashes or tokens.
public sealed record UserSummaryDto(int Id, string? Username, string Role);
