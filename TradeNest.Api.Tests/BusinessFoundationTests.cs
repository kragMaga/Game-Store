using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using TradeNest.Api.Data;
using TradeNest.Api.Dtos;
using TradeNest.Api.Entities;
using TradeNest.Api.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace TradeNest.Api.Tests;

[Collection("API integration")]
public sealed class BusinessFoundationTests
{
    private const string Password = "Cedar-Moon-River-593!";

    [Fact]
    public async Task Business_routes_isolate_tenants_for_accounts_with_other_business_memberships()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeNestContext>();
        var service = scope.ServiceProvider.GetRequiredService<BusinessService>();
        var ownerA = await db.Users.SingleAsync(); // confirmed test account without global authority
        var ownerB = await CreateUser(scope.ServiceProvider, "owner-b");
        using var client = await Client(factory, "Admin", "Bootstrap-Cedar-River-593!");
        using var created = await client.PostAsJsonAsync("/businesses", NewBusiness("Business A"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var a = (await created.Content.ReadFromJsonAsync<BusinessSummaryDto>())!;
        var b = await service.CreateAsync(ownerB.Id, NewBusiness("Business B"));
        var list = (await client.GetFromJsonAsync<PagedResponseDto<BusinessSummaryDto>>("/businesses"))!;
        Assert.Equal(a.Id, Assert.Single(list.Items).Id);
        foreach (var route in new[] { $"/businesses/{b.Id}/settings", $"/businesses/{b.Id}/members" })
        {
            using var denied = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            Assert.True(denied.Headers.CacheControl!.NoStore);
        }
        using var writeDenied = await client.PutAsJsonAsync($"/businesses/{b.Id}/settings", Settings("Hijacked"));
        Assert.Equal(HttpStatusCode.NotFound, writeDenied.StatusCode);
        Assert.Equal("Business B", (await db.Businesses.AsNoTracking().SingleAsync(item => item.Id == b.Id)).Name);
        using var otherClient = await Client(factory, ownerB.UserName!, Password);
        using var ownSettings = await otherClient.GetAsync($"/businesses/{b.Id}/settings");
        Assert.Equal(HttpStatusCode.OK, ownSettings.StatusCode);
        using var invalid = await client.GetAsync("/businesses?page=1000&pageSize=100");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var anonymous = factory.CreateClient();
        using var unauthorized = await anonymous.GetAsync("/businesses");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(1, await db.BusinessMemberships.CountAsync(item => item.BusinessId == a.Id && item.Role == BusinessRole.Owner));
        using var updated = await client.PutAsJsonAsync($"/businesses/{a.Id}/settings", Settings("Updated Store A"));
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        var settings = await db.Businesses.AsNoTracking().SingleAsync(item => item.Id == a.Id);
        Assert.Equal("Updated Store A", settings.Name);
        Assert.Equal("NGN", settings.Currency);
        // The database backs up the service's owner guard even if a future code
        // path accidentally tries to add another owner directly.
        db.BusinessMemberships.Add(new BusinessMembership { BusinessId = a.Id, UserId = ownerB.Id,
            Role = BusinessRole.Owner, JoinedAtUtc = DateTime.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Members_have_business_specific_roles_and_owner_and_payment_permissions_are_protected()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeNestContext>();
        var service = scope.ServiceProvider.GetRequiredService<BusinessService>();
        var access = scope.ServiceProvider.GetRequiredService<BusinessAccess>();
        var owner = await db.Users.SingleAsync();
        var staff = await CreateUser(scope.ServiceProvider, "staff-member");
        var a = await service.CreateAsync(owner.Id, NewBusiness("Store A"));
        var b = await service.CreateAsync(staff.Id, NewBusiness("Store B"));
        await service.InviteAsync(a.Id, owner.Id, new(staff.Email!, "Staff"));
        await service.AcceptAsync(staff.Id, LastToken(factory));
        Assert.False(BusinessAccess.CanRecordPayments(await access.RequireAsync(a.Id, staff.Id)));
        await service.UpdateMemberAsync(a.Id, owner.Id, staff.Id, new("Staff", true, true));
        Assert.True(BusinessAccess.CanRecordPayments(await access.RequireAsync(a.Id, staff.Id)));
        await service.UpdateMemberAsync(a.Id, owner.Id, staff.Id, new("Staff", false, true));
        Assert.False(BusinessAccess.CanRecordPayments(await access.RequireAsync(a.Id, staff.Id)));
        Assert.Equal(BusinessRole.Owner, (await access.RequireAsync(b.Id, staff.Id)).Role);
        using var staffClient = await Client(factory, staff.UserName!, Password);
        using var denied = await staffClient.GetAsync($"/businesses/{a.Id}/members");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var protectedOwner = await Assert.ThrowsAsync<BusinessRequestException>(() =>
            service.UpdateMemberAsync(a.Id, owner.Id, owner.Id, new("Staff", false, false)));
        Assert.Equal(409, protectedOwner.StatusCode);
        await service.UpdateMemberAsync(a.Id, owner.Id, staff.Id, new("Admin", false, true));
        var cannotAppointAdmin = await Assert.ThrowsAsync<BusinessRequestException>(() =>
            service.InviteAsync(a.Id, staff.Id, new("next-admin@example.test", "Admin")));
        Assert.Equal(403, cannotAppointAdmin.StatusCode);
        await service.UpdateMemberAsync(a.Id, owner.Id, staff.Id, new("Staff", false, false));
        using var revoked = await staffClient.GetAsync($"/businesses/{a.Id}/members");
        Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
        // Revocation is local to Store A; the account still owns Store B.
        using var stillOwner = await staffClient.GetAsync($"/businesses/{b.Id}/settings");
        Assert.Equal(HttpStatusCode.OK, stillOwner.StatusCode);
        Assert.True(await db.BusinessAuditEvents.AnyAsync(item => item.BusinessId == a.Id && item.Action == "MembershipUpdated"));
    }

    [Fact]
    public async Task Invitations_are_email_bound_one_use_expiring_and_reissues_revoke_old_tokens()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeNestContext>();
        var service = scope.ServiceProvider.GetRequiredService<BusinessService>();
        var owner = await db.Users.SingleAsync();
        var recipient = await CreateUser(scope.ServiceProvider, "invited-user");
        var outsider = await CreateUser(scope.ServiceProvider, "wrong-email");
        var business = await service.CreateAsync(owner.Id, NewBusiness("Inviting Store"));
        await service.InviteAsync(business.Id, owner.Id, new(recipient.Email!, "Staff", true));
        var oldToken = LastToken(factory);
        Assert.DoesNotContain(oldToken, (await db.BusinessInvitations.AsNoTracking().SingleAsync()).TokenHash);
        await Assert.ThrowsAsync<BusinessRequestException>(() => service.AcceptAsync(outsider.Id, oldToken));
        // Knowing the right address and token is insufficient before the account
        // has completed the existing Identity email-confirmation flow.
        await db.Users.Where(item => item.Id == recipient.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.EmailConfirmed, false));
        await Assert.ThrowsAsync<BusinessRequestException>(() => service.AcceptAsync(recipient.Id, oldToken));
        await db.Users.Where(item => item.Id == recipient.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.EmailConfirmed, true));
        await service.InviteAsync(business.Id, owner.Id, new(recipient.Email!, "Staff", true));
        var token = LastToken(factory);
        await Assert.ThrowsAsync<BusinessRequestException>(() => service.AcceptAsync(recipient.Id, oldToken));
        using var client = await Client(factory, recipient.UserName!, Password);
        using var accepted = await client.PostAsJsonAsync("/business-invitations/accept", new { token });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var replay = await client.PostAsJsonAsync("/business-invitations/accept", new { token });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        await service.InviteAsync(business.Id, owner.Id, new(outsider.Email!, "Viewer"));
        var expired = LastToken(factory);
        await db.BusinessInvitations.Where(item => item.NormalizedEmail == outsider.NormalizedEmail)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.ExpiresAtUtc, DateTime.UtcNow.AddSeconds(-1)));
        await Assert.ThrowsAsync<BusinessRequestException>(() => service.AcceptAsync(outsider.Id, expired));
    }

    [Fact]
    public async Task Invitation_acceptance_rechecks_issuer_authority()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeNestContext>();
        var service = scope.ServiceProvider.GetRequiredService<BusinessService>();
        var owner = await db.Users.SingleAsync();
        var admin = await CreateUser(scope.ServiceProvider, "appointed-admin");
        var recipient = await CreateUser(scope.ServiceProvider, "future-staff");
        var business = await service.CreateAsync(owner.Id, NewBusiness("Authority Store"));
        await service.InviteAsync(business.Id, owner.Id, new(admin.Email!, "Admin"));
        await service.AcceptAsync(admin.Id, LastToken(factory));
        await service.InviteAsync(business.Id, admin.Id, new(recipient.Email!, "Staff"));
        var token = LastToken(factory);
        await service.UpdateMemberAsync(business.Id, owner.Id, admin.Id, new("Staff", false, true));
        await Assert.ThrowsAsync<BusinessRequestException>(() => service.AcceptAsync(recipient.Id, token));
        Assert.False(await db.BusinessMemberships.AnyAsync(item => item.BusinessId == business.Id && item.UserId == recipient.Id));

    }

    [Fact]
    public async Task Outbox_failure_rolls_back_invitation_and_invalid_settings_cannot_create_tenants()
    {
        using var root = new TradeNestWebApplicationFactory();
        using var factory = root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAccountEmailSender>();
            services.AddScoped<IAccountEmailSender, OutboxAccountEmailSender>();
        }));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeNestContext>();
        var service = scope.ServiceProvider.GetRequiredService<BusinessService>();
        var owner = await db.Users.SingleAsync();
        var business = await service.CreateAsync(owner.Id, NewBusiness("Atomic Store"));
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailInvitationQueue BEFORE INSERT ON EmailOutbox BEGIN SELECT RAISE(ABORT, 'Queue failure'); END");
        await Assert.ThrowsAsync<DbUpdateException>(() => service.InviteAsync(business.Id, owner.Id, new("staff@example.test", "Staff")));
        Assert.Empty(await db.BusinessInvitations.AsNoTracking().ToListAsync());
        Assert.False(await db.BusinessAuditEvents.AnyAsync(item => item.Action == "InvitationCreated"));
        using var client = await Client(factory, "Admin", "Bootstrap-Cedar-River-593!");
        using var invalidZone = await client.PostAsJsonAsync("/businesses", NewBusiness("Invalid") with { TimeZoneId = "Unknown/BadZone" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidZone.StatusCode);
        using var invalidCurrency = await client.PostAsJsonAsync("/businesses", NewBusiness("Invalid") with { Currency = "naira" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidCurrency.StatusCode);
        Assert.Equal(1, await db.Businesses.CountAsync());
    }

    private static CreateBusinessDto NewBusiness(string name) => new(name, "NGN", "inventory@example.test", "Africa/Lagos");
    private static UpdateBusinessSettingsDto Settings(string name) => new(name, "alerts@example.test", "Africa/Lagos", new(9, 0), true);
    private static string LastToken(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<TestAccountEmailSender>().SentMessages.Last().Body.Split("\n\n")[1];

    private static async Task<User> CreateUser(IServiceProvider services, string name)
    {
        var user = new User { UserName = name, Email = name + "@example.test", EmailConfirmed = true};
        var result = await services.GetRequiredService<UserManager<User>>().CreateAsync(user, Password);
        Assert.True(result.Succeeded, string.Join(",", result.Errors.Select(item => item.Code)));
        return user;
    }

    private static async Task<HttpClient> Client(WebApplicationFactory<Program> factory, string username, string password)
    {
        var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/auth/login", new { username, password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<AuthTokensDto>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", token.AccessToken);
        return client;
    }
}
