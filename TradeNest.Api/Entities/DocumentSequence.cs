namespace TradeNest.Api.Entities;

// Independent counters per business and document family make references readable
// without exposing another tenant's volume. Increment only in the posting transaction.
public sealed class DocumentSequence : IInventoryRow
{
    public Guid BusinessId { get; set; }
    public required string Prefix { get; set; }
    public long LastNumber { get; set; }
}
