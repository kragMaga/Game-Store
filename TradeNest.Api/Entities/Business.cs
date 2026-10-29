namespace TradeNest.Api.Entities;

// A business is the tenant boundary. Its currency is chosen once; changing it
// later would reinterpret historical prices and customer debts.
public sealed class Business
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string Currency { get; set; }
    public int CurrencyDecimalPlaces { get; set; } = 2;
    public required string NotificationEmail { get; set; }
    public required string TimeZoneId { get; set; }
    public TimeOnly DailyNotificationTime { get; set; } = new(9, 0);
    public bool LowStockEmailsEnabled { get; set; } = true;
    public bool DebtReminderEmailsEnabled { get; set; }
    public string DebtReminderRecipientsJson { get; set; } = "[]";
    public int OwnerUserId { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
