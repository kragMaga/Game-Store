using TradeNest.Api.Data;
using TradeNest.Api.Dtos;
using TradeNest.Api.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace TradeNest.Api.Services;

// Closing disables use while retaining transaction evidence. Account closure is
// deactivation, not automatic erasure of identities referenced by business history.
public sealed class BusinessLifecycleService(TradeNestContext db, BusinessAccess access, UserManager<User> users,
    AuthSessionService sessions, TimeProvider clock)
{
    public async Task<object> InvitationsAsync(Guid businessId, int actor, int page, int size)
    {
        Page(page, size); await access.RequireAsync(businessId, actor, true);
        var query = db.BusinessInvitations.AsNoTracking().Where(i => i.BusinessId == businessId);
        var count = await query.CountAsync();
        // Never expose token hashes. Metadata is enough to monitor/cancel invites.
        var rows = await query.OrderByDescending(i => i.ExpiresAtUtc).ThenBy(i => i.Id).Skip((page - 1) * size).Take(size)
            .Select(i => new { i.Id, Email = i.NormalizedEmail, i.Role, i.CanRecordCustomerPayments, i.ExpiresAtUtc, i.AcceptedAtUtc, i.RevokedAtUtc }).ToListAsync();
        return new { Items = rows, Page = page, PageSize = size, TotalCount = count };
    }
    public async Task<object> AuditAsync(Guid businessId, int actor, int page, int size)
    {
        Page(page, size); await access.RequireAsync(businessId, actor, true);
        var query = db.BusinessAuditEvents.AsNoTracking().Where(a => a.BusinessId == businessId);
        var count = await query.CountAsync();
        var rows = await query.OrderByDescending(a => a.CreatedAtUtc).ThenBy(a => a.Id).Skip((page - 1) * size).Take(size)
            .Select(a => new { a.Id, a.ActorUserId, a.TargetUserId, a.Action, a.CreatedAtUtc }).ToListAsync();
        return new { Items = rows, Page = page, PageSize = size, TotalCount = count };
    }
    public async Task CancelAsync(Guid businessId, Guid invitationId, int actor)
    {
        await using var tx = await SecurityTransaction.BeginAsync(db);
        var member = await access.RequireAsync(businessId, actor, true);
        var invitation = await db.BusinessInvitations.SingleOrDefaultAsync(i => i.BusinessId == businessId && i.Id == invitationId)
            ?? throw new BusinessRequestException(404, "Invitation not found");
        if (invitation.Role == BusinessRole.Admin && member.Role != BusinessRole.Owner) throw new BusinessRequestException(403, "Only the owner manages admin invitations");
        if (invitation.AcceptedAtUtc.HasValue) throw new BusinessRequestException(409, "Accepted invitation cannot be cancelled; manage the membership instead");
        if (!invitation.RevokedAtUtc.HasValue)
        {
            invitation.RevokedAtUtc = clock.GetUtcNow().UtcDateTime;
            Audit(businessId, actor, "InvitationCancelled"); await db.SaveChangesAsync();
        }
        await tx.CommitAsync();
    }
    public async Task TransferAsync(Guid businessId, int actor, int newOwnerId)
    {
        await using var tx = await SecurityTransaction.BeginAsync(db);
        var business = await Owner(businessId, actor);
        if (newOwnerId == actor) throw new BusinessRequestException(400, "Select another active member");
        var target = await db.BusinessMemberships.SingleOrDefaultAsync(m => m.BusinessId == businessId && m.UserId == newOwnerId && m.IsActive)
            ?? throw new BusinessRequestException(404, "Active member not found");
        var targetUser = await db.Users.AsNoTracking().SingleAsync(u => u.Id == newOwnerId);
        if (!targetUser.EmailConfirmed || targetUser.AccountClosedAtUtc.HasValue) throw new BusinessRequestException(409, "New owner must have an active confirmed account");
        var oldOwner = await db.BusinessMemberships.SingleAsync(m => m.BusinessId == businessId && m.UserId == actor);
        // Persist demotion before promotion to satisfy the one-owner unique index.
        // The enclosing transaction hides the intermediate state and rolls it back
        // if promotion/auditing fails.
        oldOwner.Role = BusinessRole.Admin; oldOwner.CanRecordCustomerPayments = false;
        await db.SaveChangesAsync();
        target.Role = BusinessRole.Owner; target.CanRecordCustomerPayments = false; business.OwnerUserId = newOwnerId;
        Audit(businessId, actor, "OwnershipTransferred", newOwnerId);
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
    public async Task CloseAsync(Guid businessId, int actor, string name, bool reopen)
    {
        await using var tx = await SecurityTransaction.BeginAsync(db);
        var business = await Owner(businessId, actor);
        if (!string.Equals(name?.Trim(), business.Name, StringComparison.Ordinal)) throw new BusinessRequestException(400, "Confirm the current business name");
        if (reopen)
        {
            if (business.ClosedAtUtc is null) throw new BusinessRequestException(409, "Business is already open");
            business.ClosedAtUtc = null;
            // Reopening does not silently opt back into daily email notifications.
        }
        else
        {
            if (business.ClosedAtUtc.HasValue) { await tx.CommitAsync(); return; }
            business.ClosedAtUtc = clock.GetUtcNow().UtcDateTime; business.LowStockEmailsEnabled = false; business.DebtReminderEmailsEnabled = false;
            await db.BusinessInvitations.Where(i => i.BusinessId == businessId && i.AcceptedAtUtc == null && i.RevokedAtUtc == null)
                .ExecuteUpdateAsync(set => set.SetProperty(i => i.RevokedAtUtc, business.ClosedAtUtc));
        }
        Audit(businessId, actor, reopen ? "BusinessReopened" : "BusinessClosed");
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
    // This deliberately bypasses the ordinary closed-business guard, but only
    // for its current, active owner. Other members cannot reopen or transfer it.
    private async Task<Business> Owner(Guid businessId, int actor)
    {
        var business = await db.Businesses.SingleOrDefaultAsync(b => b.Id == businessId && b.OwnerUserId == actor);
        if (business is null || !await db.Users.AnyAsync(u => u.Id == actor && u.AccountClosedAtUtc == null) || !await db.BusinessMemberships.AnyAsync(m => m.BusinessId == businessId && m.UserId == actor && m.IsActive && m.Role == BusinessRole.Owner))
            throw new BusinessRequestException(404, "Owned business not found");
        return business;
    }
    public async Task CloseAccountAsync(int actor)
    {
        await using var tx = await SecurityTransaction.BeginAsync(db);
        if (await db.Businesses.AnyAsync(b => b.OwnerUserId == actor && b.ClosedAtUtc == null))
            throw new BusinessRequestException(409, "Transfer or close your open businesses first");
        var user = await users.FindByIdAsync(actor.ToString()) ?? throw new BusinessRequestException(404, "Account not found");
        user.AccountClosedAtUtc = clock.GetUtcNow().UtcDateTime;
        user.LockoutEnabled = true; user.LockoutEnd = DateTimeOffset.MaxValue;
        SecurityTransaction.RequireSucceeded(await users.UpdateAsync(user), "CloseAccount");
        SecurityTransaction.RequireSucceeded(await users.UpdateSecurityStampAsync(user), "UpdateSecurityStamp");
        await sessions.RevokeAllSessionsAsync(actor);
        await db.BusinessMemberships.Where(m => m.UserId == actor && m.IsActive).ExecuteUpdateAsync(set => set.SetProperty(m => m.IsActive, false));
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
    private static void Page(int page, int size)
    { if (page is < 1 or > 1000 || size is < 1 or > 100 || (long)(page - 1) * size > 10000) throw new BusinessRequestException(400, "Invalid pagination"); }
    private void Audit(Guid businessId, int actor, string action, int? target = null) => db.BusinessAuditEvents.Add(new()
        { BusinessId = businessId, ActorUserId = actor, Action = action, TargetUserId = target, CreatedAtUtc = clock.GetUtcNow().UtcDateTime });
}
