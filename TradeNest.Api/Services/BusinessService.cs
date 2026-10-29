using System.Security.Cryptography;
using System.Text;
using TradeNest.Api.Data;
using TradeNest.Api.Dtos;
using TradeNest.Api.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace TradeNest.Api.Services;

// Coordinates tenant onboarding and authority changes. Write operations acquire
// the SQLite writer transaction BEFORE checking permissions, so concurrent role
// removal cannot race a permission-sensitive mutation.
public sealed class BusinessService(TradeNestContext db, BusinessAccess access, UserManager<User> users,
    IAccountEmailSender emails, TimeProvider clock)
{
    public async Task<BusinessSummaryDto> CreateAsync(int actorId, CreateBusinessDto request)
    {
        ValidateSettings(request.Name, request.TimeZoneId);
        _ = InventoryMoney.MinorScale(request.CurrencyDecimalPlaces);
        await using var transaction = await SecurityTransaction.BeginAsync(db);
        var actor = await users.FindByIdAsync(actorId.ToString());
        if (actor is null || actor.AccountClosedAtUtc.HasValue || !actor.EmailConfirmed)
            throw new BusinessRequestException(403, "Confirm your account email before creating a business");
        var business = new Business { Name = request.Name.Trim(), Currency = request.Currency, CurrencyDecimalPlaces = request.CurrencyDecimalPlaces,
            NotificationEmail = request.NotificationEmail.Trim(), TimeZoneId = request.TimeZoneId,
            OwnerUserId = actorId, CreatedAtUtc = clock.GetUtcNow().UtcDateTime };
        db.Businesses.Add(business);
        db.BusinessMemberships.Add(new BusinessMembership { BusinessId = business.Id, UserId = actorId,
            Role = BusinessRole.Owner, JoinedAtUtc = clock.GetUtcNow().UtcDateTime });
        Audit(business.Id, actorId, "BusinessCreated", actorId);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return new(business.Id, business.Name, business.Currency, "Owner");
    }

    public async Task<PagedResponseDto<BusinessSummaryDto>> ListAsync(int actorId, int page, int pageSize)
    {
        var query = from membership in db.BusinessMemberships.AsNoTracking()
                    join business in db.Businesses.AsNoTracking() on membership.BusinessId equals business.Id
                    where membership.UserId == actorId && membership.IsActive
                    select new { business.Id, business.Name, business.Currency, membership.Role, business.ClosedAtUtc };
        var count = await query.CountAsync();
        var rows = await query.OrderBy(item => item.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        return new(rows.Select(item => new BusinessSummaryDto(item.Id, item.Name, item.Currency, item.Role.ToString(), item.ClosedAtUtc)).ToList(),
            page, pageSize, count, (int)Math.Ceiling(count / (double)pageSize));
    }

    public async Task<object> SettingsAsync(Guid businessId, int actorId)
    {
        await access.RequireAsync(businessId, actorId, administrator: true);
        return await db.Businesses.AsNoTracking().Where(item =>  item.Id == businessId).Select(item => new
        { item.Id, item.Name, item.Currency, item.CurrencyDecimalPlaces, item.NotificationEmail, item.TimeZoneId,
            item.DailyNotificationTime, item.LowStockEmailsEnabled }).SingleAsync();
    }

    public async Task UpdateSettingsAsync(Guid businessId, int actorId, UpdateBusinessSettingsDto request)
    {
        ValidateSettings(request.Name, request.TimeZoneId);
        await using var transaction = await SecurityTransaction.BeginAsync(db);
        await access.RequireAsync(businessId, actorId, administrator: true);
        var business = await db.Businesses.SingleAsync(item => item.Id == businessId);
        business.Name = request.Name.Trim();
        business.NotificationEmail = request.NotificationEmail.Trim();
        business.TimeZoneId = request.TimeZoneId;
        business.DailyNotificationTime = request.DailyNotificationTime;
        business.LowStockEmailsEnabled = request.LowStockEmailsEnabled;
        Audit(businessId, actorId, "SettingsUpdated");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public async Task<PagedResponseDto<BusinessMemberDto>> MembersAsync(Guid businessId, int actorId, int page, int pageSize)
    {
        await access.RequireAsync(businessId, actorId, administrator: true);
        var query = from member in db.BusinessMemberships.AsNoTracking()
                    join user in db.Users.AsNoTracking() on member.UserId equals user.Id
                    where member.BusinessId == businessId
                    select new { member.UserId, user.UserName, member.Role, member.IsActive, member.CanRecordCustomerPayments };
        var count = await query.CountAsync();
        var rows = await query.OrderBy(item => item.UserId).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        return new(rows.Select(item => new BusinessMemberDto(item.UserId, item.UserName, item.Role.ToString(), item.IsActive,
            item.IsActive && (BusinessAccess.IsAdministrator(item.Role) || item.Role == BusinessRole.Staff && item.CanRecordCustomerPayments))).ToList(),
            page, pageSize, count, (int)Math.Ceiling(count / (double)pageSize));
    }

    public async Task InviteAsync(Guid businessId, int actorId, InviteBusinessMemberDto request)
    {
        var role = ParseRole(request.Role);
        await using var transaction = await SecurityTransaction.BeginAsync(db);
        var actor = await access.RequireAsync(businessId, actorId, administrator: true);
        RequireRoleAuthority(actor, role);
        var normalized = users.NormalizeEmail(request.Email.Trim())!;
        var existingUser = await users.FindByEmailAsync(request.Email.Trim());
        if (existingUser is not null && await db.BusinessMemberships.AnyAsync(item =>
                item.BusinessId == businessId && item.UserId == existingUser.Id))
            throw new BusinessRequestException(409, "This account already has a membership; update it instead");
        // Reissuing an invite invalidates older outstanding links for the same
        // address in THIS business, while leaving other businesses' invites alone.
        var now = clock.GetUtcNow().UtcDateTime;
        await db.BusinessInvitations.Where(item => item.BusinessId == businessId && item.NormalizedEmail == normalized &&
                item.AcceptedAtUtc == null && item.RevokedAtUtc == null)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.RevokedAtUtc, now));
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        db.BusinessInvitations.Add(new BusinessInvitation { BusinessId = businessId, NormalizedEmail = normalized,
            TokenHash = Hash(token), Role = role, CanRecordCustomerPayments = role == BusinessRole.Staff && request.CanRecordCustomerPayments,
            InvitedByUserId = actorId, ExpiresAtUtc = now.AddDays(7) });
        Audit(businessId, actorId, "InvitationCreated");
        // The outbox SaveChanges joins this transaction. Queue persistence failure
        // rolls back both the invitation and its audit event; SMTP happens later.
        await emails.SendAsync(request.Email.Trim(), "Business inventory invitation",
            $"You have been invited to a business inventory workspace. Sign in with this confirmed email address, then submit the token below as JSON {{ token }} to POST /business-invitations/accept. The invitation expires in seven days.\n\n{token}\n\nIf unexpected, ignore this email.");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public async Task<Guid> AcceptAsync(int actorId, string token)
    {
        await using var transaction = await SecurityTransaction.BeginAsync(db);
        var hash = Hash(token);
        var invitation = await db.BusinessInvitations.SingleOrDefaultAsync(item => item.TokenHash == hash);
        var actor = await users.FindByIdAsync(actorId.ToString());
        var now = clock.GetUtcNow().UtcDateTime;
        if (invitation is null || actor is null || actor.AccountClosedAtUtc.HasValue || !actor.EmailConfirmed ||
            actor.NormalizedEmail != invitation.NormalizedEmail || invitation.ExpiresAtUtc <= now ||
            invitation.AcceptedAtUtc is not null || invitation.RevokedAtUtc is not null)
            throw new BusinessRequestException(400, "Invitation is invalid or unavailable for this account");
        // Do not honour an invitation whose issuer has since lost the authority
        // to appoint this role. This prevents delayed privilege escalation.
        var issuer = await access.RequireAsync(invitation.BusinessId, invitation.InvitedByUserId, administrator: true);
        RequireRoleAuthority(issuer, invitation.Role);
        if (await db.BusinessMemberships.AnyAsync(item => item.BusinessId == invitation.BusinessId && item.UserId == actorId))
            throw new BusinessRequestException(409, "This account already has a membership");
        db.BusinessMemberships.Add(new BusinessMembership { BusinessId = invitation.BusinessId, UserId = actorId,
            Role = invitation.Role, CanRecordCustomerPayments = invitation.CanRecordCustomerPayments, JoinedAtUtc = now });
        invitation.AcceptedAtUtc = now;
        Audit(invitation.BusinessId, actorId, "InvitationAccepted", actorId);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return invitation.BusinessId;
    }

    public async Task UpdateMemberAsync(Guid businessId, int actorId, int targetId, UpdateBusinessMemberDto request)
    {
        var role = ParseRole(request.Role);
        await using var transaction = await SecurityTransaction.BeginAsync(db);
        var actor = await access.RequireAsync(businessId, actorId, administrator: true);
        var target = await db.BusinessMemberships.SingleOrDefaultAsync(item => item.BusinessId == businessId && item.UserId == targetId);
        if (target is null) throw new BusinessRequestException(404, "Member not found");
        if (target.Role == BusinessRole.Owner) throw new BusinessRequestException(409, "The owner cannot be changed through member administration");
        RequireRoleAuthority(actor, target.Role);
        RequireRoleAuthority(actor, role);
        target.Role = role;
        target.IsActive = request.IsActive;
        target.CanRecordCustomerPayments = role == BusinessRole.Staff && request.CanRecordCustomerPayments;
        Audit(businessId, actorId, "MembershipUpdated", targetId);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        // No global session revocation: access checks read live membership on
        // each request, so another business's sessions need not be interrupted.
    }

    private static BusinessRole ParseRole(string role) => role switch
    {
        "Admin" => BusinessRole.Admin, "Staff" => BusinessRole.Staff, "Viewer" => BusinessRole.Viewer,
        _ => throw new BusinessRequestException(400, "Role must be Admin, Staff, or Viewer")
    };

    private static void RequireRoleAuthority(BusinessMembership actor, BusinessRole targetRole)
    {
        // Admins manage ordinary staff/viewers. Only owners appoint or change
        // admins, preventing an appointed admin from taking over the business.
        if (targetRole == BusinessRole.Admin && actor.Role != BusinessRole.Owner)
            throw new BusinessRequestException(403, "Only the owner may appoint or manage administrators");
    }

    private static void ValidateSettings(string name, string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length < 2)
            throw new BusinessRequestException(400, "Business name must contain at least two characters");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new BusinessRequestException(400, "Time zone is not recognised"); }
    }

    private void Audit(Guid businessId, int actorId, string action, int? targetId = null) =>
        db.BusinessAuditEvents.Add(new BusinessAuditEvent { BusinessId = businessId, ActorUserId = actorId,
            TargetUserId = targetId, Action = action, CreatedAtUtc = clock.GetUtcNow().UtcDateTime });

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
