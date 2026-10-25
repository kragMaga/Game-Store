using System.ComponentModel.DataAnnotations;

namespace GameStore.Api.Services;

// Bind once at startup: operators can tune shared-IP budgets without rebuilding.
// Limits remain positive and bounded; configuration cannot silently disable them.
public sealed class AuthRateLimitOptions
{
    [Range(1, 1000)] public int BaselinePermits { get; set; } = 10;
    [Range(1, 3600)] public int BaselineWindowSeconds { get; set; } = 300;
    [Range(1, 1000)] public int PasswordPermits { get; set; } = 5;
    [Range(1, 3600)] public int PasswordWindowSeconds { get; set; } = 300;
    [Range(1, 1000)] public int RecoveryPermits { get; set; } = 3;
    [Range(1, 86400)] public int RecoveryWindowSeconds { get; set; } = 900;
}
