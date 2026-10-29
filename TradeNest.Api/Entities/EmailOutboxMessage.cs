namespace TradeNest.Api.Entities;

// Persist delivery intent independently of the SMTP server's availability.
// The payload contains email addresses/tokens, so it is encrypted with Data Protection.
public sealed class EmailOutboxMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public required string ProtectedPayload { get; set; }
    public string Status { get; set; } = "Pending";
    public int Attempts { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseExpiresAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? FailureType { get; set; }
}
