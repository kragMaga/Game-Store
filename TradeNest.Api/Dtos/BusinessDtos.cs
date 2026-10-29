using System.ComponentModel.DataAnnotations;

namespace TradeNest.Api.Dtos;

// Currency and precision are deliberately absent from settings updates. They are
// chosen at onboarding and copied onto completed purchase/sale records.
public sealed record CreateBusinessDto(
    [Required, StringLength(120, MinimumLength = 2)] string Name,
    [Required, RegularExpression("^[A-Z]{3}$")] string Currency,
    [Required, EmailAddress, StringLength(254)] string NotificationEmail,
    [Required, StringLength(100)] string TimeZoneId,
    [Range(0, 4)] int CurrencyDecimalPlaces = 2);

public sealed record UpdateBusinessSettingsDto(
    [Required, StringLength(120, MinimumLength = 2)] string Name,
    [Required, EmailAddress, StringLength(254)] string NotificationEmail,
    [Required, StringLength(100)] string TimeZoneId,
    TimeOnly DailyNotificationTime,
    bool LowStockEmailsEnabled);

public sealed record InviteBusinessMemberDto(
    [Required, EmailAddress, StringLength(254)] string Email,
    [Required, RegularExpression("^(Admin|Staff|Viewer)$")] string Role,
    bool CanRecordCustomerPayments = false);

public sealed record UpdateBusinessMemberDto(
    [Required, RegularExpression("^(Admin|Staff|Viewer)$")] string Role,
    bool CanRecordCustomerPayments,
    bool IsActive);

public sealed record AcceptBusinessInvitationDto([Required, StringLength(128, MinimumLength = 32)] string Token);

public sealed record BusinessSummaryDto(Guid Id, string Name, string Currency, string Role, DateTime? ClosedAtUtc = null);
public sealed record BusinessMemberDto(int UserId, string? Username, string Role, bool IsActive,
    bool CanRecordCustomerPayments);
