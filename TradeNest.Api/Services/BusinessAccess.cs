using TradeNest.Api.Data;
using TradeNest.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace TradeNest.Api.Services;

// Business services use this explicit guard for every tenant operation. A route
// ID selects a business; it never proves membership or grants authority.
public sealed class BusinessAccess(TradeNestContext db)
{
    public async Task<BusinessMembership> RequireAsync(Guid businessId, int userId, bool administrator = false)
    {
        var membership = await db.BusinessMemberships.AsNoTracking()
            .SingleOrDefaultAsync(item => item.BusinessId == businessId && item.UserId == userId && item.IsActive &&
                db.Users.Any(user => user.Id == userId && user.AccountClosedAtUtc == null));
        // Return the same result for an absent business and a nonmember. Neither
        // a guessed ID nor the legacy global admin role exposes another tenant.
        if (membership is null || !await db.Businesses.AnyAsync(b => b.Id == businessId && b.ClosedAtUtc == null)) throw new BusinessRequestException(404, "Business not found");
        if (administrator && !IsAdministrator(membership.Role))
            throw new BusinessRequestException(403, "Business administrator permission required");
        return membership;
    }

    public static bool IsAdministrator(BusinessRole role) => role is BusinessRole.Owner or BusinessRole.Admin;

    public static bool CanRecordPayments(BusinessMembership member) => member.IsActive &&
        (IsAdministrator(member.Role) || member.Role == BusinessRole.Staff && member.CanRecordCustomerPayments);
}

// Translate only intentional business errors into public ProblemDetails. Unexpected
// database/outbox failures continue through the application's normal error handler.
public sealed class BusinessRequestException(int statusCode, string title) : Exception(title)
{
    public int StatusCode { get; } = statusCode;
}
