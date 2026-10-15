namespace GameStore.Api.Entities;

public sealed class AuthSession
{
    public required string Id { get; set; }

    public int UserId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime LastRefreshedAtUtc { get; set; }

    public DateTime AbsoluteExpiresAtUtc { get; set; }

    public DateTime? RevokedAtUtc { get; set; }

    public User User { get; set; } = null!;
}
