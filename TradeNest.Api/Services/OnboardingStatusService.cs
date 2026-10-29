using TradeNest.Api.Data;
using Microsoft.EntityFrameworkCore;
namespace TradeNest.Api.Services;

// Status informs a frontend walkthrough without inventing mandatory stock/debt
// entries. A business can legitimately start with zero stock, no debt or no staff.
public sealed class OnboardingStatusService(TradeNestContext db, BusinessAccess access)
{
    public async Task<object> GetAsync(Guid businessId, int actor)
    {
        db.InventoryBusinessId = null; await access.RequireAsync(businessId, actor, true); db.InventoryBusinessId = businessId;
        var b = await db.Businesses.AsNoTracking().SingleAsync(b => b.Id == businessId);
        var products = await db.InventoryProducts.CountAsync(p => p.BusinessId == businessId);
        var customers = await db.Customers.CountAsync(c => c.BusinessId == businessId);
        var additionalMembers = await db.BusinessMemberships.CountAsync(m => m.BusinessId == businessId && m.IsActive && m.UserId != b.OwnerUserId);
        return new { BusinessConfigured = true, ProductCount = products, HasProducts = products > 0,
            OpeningStockEntries = await db.StockMovements.CountAsync(m => m.BusinessId == businessId && m.Kind == "OpeningStock"),
            CustomerCount = customers, OpeningDebtEntries = await db.CustomerCharges.CountAsync(c => c.BusinessId == businessId && c.Kind == "OpeningDebt"),
            AdditionalMemberCount = additionalMembers,
            PendingInvitations = await db.BusinessInvitations.CountAsync(i => i.BusinessId == businessId && i.AcceptedAtUtc == null && i.RevokedAtUtc == null && i.ExpiresAtUtc > DateTime.UtcNow),
            HasRecordedPurchase = await db.InventoryTransactions.AnyAsync(t => t.BusinessId == businessId && t.Kind == "Purchase"),
            HasRecordedSale = await db.InventoryTransactions.AnyAsync(t => t.BusinessId == businessId && t.Kind == "Sale"),
            b.LowStockEmailsEnabled, b.DebtReminderEmailsEnabled,
            // Optional steps are explicitly optional instead of a misleading
            // completion percentage that pressures users to enter fictitious debt.
            OptionalSteps = new[] { "Opening stock if goods exist", "Customers and opening debts if applicable", "Invite staff if needed", "Configure reminder email recipients" } };
    }
}
