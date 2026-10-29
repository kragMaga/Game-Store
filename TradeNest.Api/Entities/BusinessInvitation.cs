namespace TradeNest.Api.Entities;

// Only a SHA-256 hash is stored here. The raw random token goes to the intended
// recipient through the encrypted email outbox and is never returned to the inviter.
public sealed class BusinessInvitation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BusinessId { get; set; }
    public required string NormalizedEmail { get; set; }
    public required string TokenHash { get; set; }
    public BusinessRole Role { get; set; }
    public bool CanRecordCustomerPayments { get; set; }
    public int InvitedByUserId { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? AcceptedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
}
