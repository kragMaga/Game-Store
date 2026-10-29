namespace TradeNest.Api.Services;

// TradeNest is the product and code name. These strings identify existing cookies,
// encrypted payloads, Identity token rows or database files, not UI branding.
// Changing them during a cosmetic rename could strand data or invalidate sessions.
// Retire them only through an explicit data/security migration, not search/replace.
public static class LegacyCompatibility
{
    public const string DataProtectionApplication = "GameStore.Api";
    public const string PreviousApiDirectory = "GameStore.Api";
    public const string PreviousConnectionName = "GameStore";
    public const string DefaultDatabaseConnection = "Data Source=GameStore.db";
    public const string RefreshCookie = "__Host-GameStore.Refresh";
    public const string AntiforgeryCookie = "__Host-GameStore.AntiForgery";
    public const string ReauthenticationPurpose = "GameStore.Reauthentication.v1";
    public const string EmailOutboxPurpose = "GameStore.EmailOutbox.v1";
    public const string NotificationRetryPurpose = "GameStore.BusinessNotificationRetry.v1";
    public const string ImportPreviewPurpose = "GameStore.InventoryImportPreview.v1";
    public const string MfaLoginProvider = "GameStore";
}
