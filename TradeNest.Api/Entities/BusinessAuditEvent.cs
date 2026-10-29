namespace TradeNest.Api.Entities;

// Keep a small, append-only operational trail for changes in tenant authority.
// Do not put invitation tokens, email bodies, or credentials in these records.
public sealed class BusinessAuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BusinessId { get; set; }
    public int ActorUserId { get; set; }
    public int? TargetUserId { get; set; }
    public required string Action { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
