namespace TradeNest.Api.Entities;

// Roles belong to memberships, not to a global JWT role. The same person can
// own one business while working as staff in another.
public enum BusinessRole { Owner, Admin, Staff, Viewer }

public sealed class BusinessMembership
{
    public Guid BusinessId { get; set; }
    public int UserId { get; set; }
    public BusinessRole Role { get; set; }
    public bool IsActive { get; set; } = true;
    // Staff cannot register later debt payments unless an administrator grants
    // this business-specific permission. Future payment endpoints must enforce it.
    public bool CanRecordCustomerPayments { get; set; }
    public DateTime JoinedAtUtc { get; set; }
}
