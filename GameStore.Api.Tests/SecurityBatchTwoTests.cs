using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using GameStore.Api.Data;
using GameStore.Api.Dtos;
using GameStore.Api.Entities;
using GameStore.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GameStore.Api.Tests;

[Collection("API integration")]
// Keep security tests in one collection because older fixtures change process-wide
// settings. This batch checks validation, bootstrap rules, recent auth, and cache headers.
public sealed class SecurityBatchTwoTests
{
    private const string Password = "Bootstrap-Cedar-River-593!";
    // The intended recent-authentication window is ten minutes. Use eleven
    // minutes for expired cases to avoid clock precision at the exact boundary.
    private const int ExpiredAuthenticationMinutes = 11;

    [Theory]
    [InlineData("recent", true)]
    [InlineData("stale", false)]
    [InlineData("future", false)]
    [InlineData("missing", false)]
    [InlineData("invalid", false)]
    public void External_sign_in_requires_a_recent_signed_authentication_time(string scenario, bool expected)
    {
        var now = DateTimeOffset.UtcNow;
        var value = scenario switch
        {
            "recent" => now.ToUnixTimeSeconds().ToString(),
            "stale" => now.AddMinutes(-ExpiredAuthenticationMinutes).ToUnixTimeSeconds().ToString(),
            "future" => now.AddMinutes(2).ToUnixTimeSeconds().ToString(),
            _ => "invalid"
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            scenario == "missing" ? [] : new[] { new Claim("auth_time", value) }, "provider"));
        Assert.Equal(expected, RecentAuthenticationService.HasRecentProviderAuthentication(principal, now));
    }

    [Fact]
    public async Task Period_usernames_work_for_registration_and_identity_updates()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/auth/register",
            new { username = "player.one", email = "period@example.test", password = "Cedar-Moon-River-593!" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(factory.Services.GetRequiredService<TestAccountEmailSender>().SentMessages);
        using var scope = factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = (await manager.FindByNameAsync("PLAYER.ONE"))!;
        Assert.Equal(AccountOperationResult.Success,
            await scope.ServiceProvider.GetRequiredService<UserService>().UpdateUsernameAsync(user.Id, "player.two"));
        Assert.NotNull(await manager.FindByNameAsync("PLAYER.TWO"));
        Assert.Equal(AccountOperationResult.InvalidUsername,
            await scope.ServiceProvider.GetRequiredService<UserService>().UpdateUsernameAsync(user.Id, "bad name"));
    }

    [Theory]
    [InlineData("/auth/login", "{\"username\":null,\"password\":null}")]
    [InlineData("/auth/login/mfa", "{\"challengeToken\":null,\"code\":null}")]
    [InlineData("/auth/password/reset", "{\"email\":\"invalid\",\"token\":null,\"newPassword\":null}")]
    [InlineData("/auth/confirm-email", "{\"email\":\"invalid\",\"token\":null}")]
    public async Task Auth_dtos_reject_invalid_fields_before_handlers_and_prevent_caching(string route, string json)
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsync(route, new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("errors").EnumerateObject().Any());
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("Password123")]
    public async Task Bootstrap_rejects_weak_and_common_passwords_without_creating_an_account(string password)
    {
        using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameStoreContext>();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        await db.Users.ExecuteDeleteAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DbInitializer.SeedAdminAsync(db, "new.admin", "new-admin@example.test", password, manager));
        Assert.DoesNotContain(password, error.Message);
        Assert.Empty(await db.Users.ToListAsync());
        await DbInitializer.SeedAdminAsync(db, "new.admin", "new-admin@example.test", Password, manager);
        Assert.True(await manager.CheckPasswordAsync((await manager.FindByNameAsync("NEW.ADMIN"))!, Password));
    }

    [Fact]
    public async Task Sensitive_actions_require_recent_auth_and_refresh_does_not_reset_the_window()
    {
        // Age the persisted sign-in, then prove that refresh alone cannot authorize
        // sensitive changes. A valid reauthentication proof works only until expiry.
        var clock = new MutableClock();
        using var factory = Factory(clock);
        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/auth/login", new { username = "Admin", password = Password });
        var tokens = (await login.Content.ReadFromJsonAsync<AuthTokensDto>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameStoreContext>();
        await db.AuthSessions.ExecuteUpdateAsync(update => update.SetProperty(session => session.CreatedAtUtc, DateTime.UtcNow.AddMinutes(-ExpiredAuthenticationMinutes)));
        using var denied = await client.PutAsJsonAsync("/auth/me/email", new { email = "changed@example.test" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.True(denied.Headers.CacheControl!.NoStore);
        using var refreshed = await client.PostAsJsonAsync("/auth/refresh", new { tokens.RefreshToken });
        var replacement = (await refreshed.Content.ReadFromJsonAsync<AuthTokensDto>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", replacement.AccessToken);
        using var stillDenied = await client.PutAsJsonAsync("/auth/me/email", new { email = "changed@example.test" });
        Assert.Equal(HttpStatusCode.Forbidden, stillDenied.StatusCode);
        using var reauthenticated = await client.PostAsJsonAsync("/auth/reauthenticate", new { currentPassword = Password });
        reauthenticated.EnsureSuccessStatusCode();
        Assert.True(reauthenticated.Headers.CacheControl!.NoStore);
        using var body = JsonDocument.Parse(await reauthenticated.Content.ReadAsStringAsync());
        var proof = body.RootElement.GetProperty("reauthenticationToken").GetString()!;
        client.DefaultRequestHeaders.Add(RecentAuthenticationService.HeaderName, proof);
        using var accepted = await client.PutAsJsonAsync("/auth/me/email", new { email = "changed@example.test" });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        clock.Advance(TimeSpan.FromMinutes(ExpiredAuthenticationMinutes));
        using var expired = await client.PutAsJsonAsync("/auth/me/username", new { username = "renamed.admin" });
        Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);
    }

    [Fact]
    public async Task Reauthentication_requires_mfa_and_proofs_are_bound_to_session_stamp_and_external_link_state()
    {
        using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var db = scope.ServiceProvider.GetRequiredService<GameStoreContext>();
        var user = (await manager.FindByNameAsync("Admin"))!;
        var tokens = (await auth.LoginAsync(new("Admin", Password)))!.Tokens!;
        var session = await db.AuthSessions.AsNoTracking().SingleAsync();
        Assert.True((await manager.SetTwoFactorEnabledAsync(user, true)).Succeeded);
        var codes = (await manager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))!.ToArray();
        // Identity rotates the stamp when MFA is enabled. Model the authenticated
        // principal after that change rather than testing with an already stale one.
        var principal = Principal(user, session.Id);
        var recent = scope.ServiceProvider.GetRequiredService<RecentAuthenticationService>();
        Assert.Null(await recent.ReauthenticateAsync(principal, Password, null));
        var proof = await recent.ReauthenticateAsync(principal, Password, codes[0]);
        Assert.NotNull(proof);
        await db.AuthSessions.ExecuteUpdateAsync(update => update.SetProperty(item => item.CreatedAtUtc, DateTime.UtcNow.AddMinutes(-ExpiredAuthenticationMinutes)));
        var context = new DefaultHttpContext { User = principal };
        context.Request.Headers[RecentAuthenticationService.HeaderName] = proof;
        Assert.True(await recent.IsRecentAsync(context));
        var linkProof = await recent.CreateExternalLinkProofAsync(context);
        Assert.Equal(user.Id, await recent.ValidateExternalLinkProofAsync(linkProof));
        context.Request.Headers[RecentAuthenticationService.HeaderName] = "tampered";
        Assert.False(await recent.IsRecentAsync(context));
        var otherSession = new AuthSession { Id = Guid.NewGuid().ToString("N"), UserId = user.Id,
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-ExpiredAuthenticationMinutes), LastRefreshedAtUtc = DateTime.UtcNow, AbsoluteExpiresAtUtc = DateTime.UtcNow.AddDays(1) };
        db.AuthSessions.Add(otherSession);
        await db.SaveChangesAsync();
        context.User = Principal(user, otherSession.Id);
        context.Request.Headers[RecentAuthenticationService.HeaderName] = proof;
        Assert.False(await recent.IsRecentAsync(context));
        var freshUser = (await manager.FindByIdAsync(user.Id.ToString()))!;
        Assert.True((await manager.UpdateSecurityStampAsync(freshUser)).Succeeded);
        Assert.Null(await recent.ValidateExternalLinkProofAsync(linkProof));
    }

    [Fact]
    public async Task Mfa_keys_challenges_and_unauthorized_responses_are_not_cached()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        using var denied = await client.GetAsync("/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.True(denied.Headers.CacheControl!.NoStore);
        using var login = await client.PostAsJsonAsync("/auth/login", new { username = "Admin", password = Password });
        var tokens = (await login.Content.ReadFromJsonAsync<AuthTokensDto>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
        using var setup = await client.PostAsJsonAsync("/auth/me/mfa/setup", new { currentPassword = Password });
        setup.EnsureSuccessStatusCode();
        Assert.True(setup.Headers.CacheControl!.NoStore);
        using var scope = factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        Assert.True((await manager.SetTwoFactorEnabledAsync((await manager.FindByNameAsync("Admin"))!, true)).Succeeded);
        using var challenge = await client.PostAsJsonAsync("/auth/login", new { username = "Admin", password = Password });
        Assert.Equal(HttpStatusCode.Accepted, challenge.StatusCode);
        Assert.True(challenge.Headers.CacheControl!.NoStore);
    }

    private static ClaimsPrincipal Principal(User user, string sessionId) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim("session_id", sessionId),
         new Claim("security_stamp", user.SecurityStamp!)], "test"));

    private static WebApplicationFactory<Program> Factory(MutableClock? clock = null)
    {
        var factory = new GameStoreWebApplicationFactory();
        return factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            if (clock is not null) services.AddSingleton<TimeProvider>(clock);
        }));
    }

    private sealed class MutableClock : TimeProvider
    {
        // Advance proof-validation time without waiting out ten real minutes.
        private TimeSpan _offset;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + _offset;
        public void Advance(TimeSpan amount) => _offset += amount;
    }
}
