namespace TradeNest.Api.Entities;

// Durable, tenant-scoped notification metadata survives outbox payload cleanup.
// Protected payload is retained for at most seven days for explicit admin retries.
public sealed class BusinessNotification : IInventoryRow
{
    public Guid BusinessId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Kind { get; set; }
    public DateOnly LocalDate { get; set; }
    public required string Recipient { get; set; }
    public required string OutboxMessageId { get; set; }
    public required string ProtectedPayload { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime RetryUntilUtc { get; set; }
    public string Status { get; set; } = "Pending";
    public int Attempts { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? FailureType { get; set; }
}

// A separate day marker suppresses repeat scans even when there are no overdue
// debts or email is disabled. Dashboard reminders always use live ledger balances.
public sealed class DebtReminderDispatch : IInventoryRow
{
    public Guid BusinessId { get; set; }
    public DateOnly LocalDate { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

// Only committed imports are retained here. A protected preview token binds the
// reviewed rows to the business/admin and expires without persisting uploaded files.
public sealed class InventoryImportBatch : IInventoryRow
{
    public Guid BusinessId { get; set; }
    public Guid Id { get; set; }
    public required string Kind { get; set; }
    public int ActorUserId { get; set; }
    public int RowCount { get; set; }
    public DateTime CommittedAtUtc { get; set; }
}
