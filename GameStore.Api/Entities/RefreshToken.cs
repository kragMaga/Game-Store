namespace GameStore.Api.Entities;

public sealed class RefreshToken
{
    public int Id { get; set; }

    public int UserId { get; set; }

    public required string TokenHash { get; set; }

    public required string SessionId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    public DateTime AbsoluteExpiresAtUtc { get; set; }

    public DateTime? RevokedAtUtc { get; set; }

    public string? ReplacedByTokenHash { get; set; }

}
