using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using GameStore.Api.Data;
using GameStore.Api.Dtos;
using GameStore.Api.Entities;
using GameStore.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GameStore.Api.Tests;

[Collection("API integration")]
// Focused checks for session revocation, all-or-nothing security updates, stale MFA
// challenges, and concurrent last-admin protection. These do not run the full suite.
public sealed class SecurityBatchOneTests
{
    [Theory]
    [InlineData("username")]
    [InlineData("role")]
    public async Task Username_and_role_changes_revoke_existing_refresh_credentials(string change)
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameStoreContext>();
        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
        var users = scope.ServiceProvider.GetRequiredService<UserService>();
        var userId = (await db.Users.SingleAsync(user => user.UserName == "Admin")).Id;
        if (change == "role")
        {
            db.Users.Add(NewAdmin("second-admin"));
            await db.SaveChangesAsync();
        }
        var tokens = (await auth.LoginAsync(new("Admin", "Bootstrap-Cedar-River-593!")))!.Tokens!;
        if (change == "username")
            Assert.Equal(AccountOperationResult.Success, await users.UpdateUsernameAsync(userId, "RenamedAdmin"));
        else
            Assert.Equal(UserOperationError.None, await users.UpdateRoleAsync(userId, new("customer")));
        Assert.False(await db.AuthSessions.AnyAsync(session => session.UserId == userId && session.RevokedAtUtc == null));
        var refresh = await auth.RefreshAsync(tokens.RefreshToken);
        Assert.Equal(RefreshTokenError.Invalid, refresh.Error);
        Assert.Null(refresh.Tokens);
    }

    [Theory]
    [InlineData("setup")]
    [InlineData("enable")]
    [InlineData("password")]
    [InlineData("reset-password")]
    [InlineData("link")]
    public async Task Security_mutations_rollback_on_revocation_failure_and_can_be_retried(string operation)
    {
        // Force the final database update to fail after Identity has already saved
        // earlier changes. Verify rollback, then retry to prove nothing was consumed.
        using var factory = new GameStoreWebApplicationFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddDataProtection().UseEphemeralDataProtectionProvider()));
        using var scope = configured.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameStoreContext>();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
        var users = scope.ServiceProvider.GetRequiredService<UserService>();
        var user = (await manager.FindByNameAsync("Admin"))!;
        Assert.True((await manager.ResetAuthenticatorKeyAsync(user)).Succeeded);
        var key = (await manager.GetAuthenticatorKeyAsync(user))!;
        var resetToken = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(
            Encoding.UTF8.GetBytes(await manager.GeneratePasswordResetTokenAsync(user)));
        var userId = user.Id;
        var original = await db.Users.AsNoTracking().SingleAsync(item => item.Id == userId);
        Assert.NotNull((await auth.LoginAsync(new("Admin", "Bootstrap-Cedar-River-593!")))!.Tokens);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER FailBatchRevocation BEFORE UPDATE ON RefreshTokens
            WHEN NEW.RevokedAtUtc IS NOT NULL
            BEGIN SELECT RAISE(ABORT, 'Simulated revocation failure'); END;
            """);

        async Task Mutate()
        {
            switch (operation)
            {
                case "setup":
                    Assert.NotNull((await auth.BeginMfaSetupAsync(userId, "Bootstrap-Cedar-River-593!")).SharedKey);
                    break;
                case "enable":
                    Assert.Equal(10, (await auth.EnableMfaAsync(userId, Totp(key)))!.Count);
                    break;
                case "password":
                    Assert.Equal(AccountOperationResult.Success,
                        await users.ChangePasswordAsync(userId, "Bootstrap-Cedar-River-593!", "Fresh-River-Moon-693!"));
                    break;
                case "reset-password":
                    Assert.True(await auth.ResetPasswordAsync("admin@example.test", resetToken, "Fresh-River-Moon-693!"));
                    break;
                case "link":
                    Assert.True((await auth.LinkExternalIdentityAsync(user, new("test-provider", "test-subject", "Test"))).Succeeded);
                    break;
            }
        }

        await Assert.ThrowsAnyAsync<Exception>(Mutate);
        var unchanged = await db.Users.AsNoTracking().SingleAsync(item => item.Id == userId);
        Assert.Equal(original.SecurityStamp, unchanged.SecurityStamp);
        Assert.Equal(original.PasswordHash, unchanged.PasswordHash);
        Assert.False(unchanged.TwoFactorEnabled);
        Assert.Equal(key, await manager.GetAuthenticatorKeyAsync(unchanged));
        Assert.Equal(0, await manager.CountRecoveryCodesAsync(unchanged));
        Assert.Empty(await manager.GetLoginsAsync(unchanged));
        Assert.True(await db.AuthSessions.AnyAsync(session => session.UserId == userId && session.RevokedAtUtc == null));
        Assert.True(await db.RefreshTokens.AnyAsync(token => token.UserId == userId && token.RevokedAtUtc == null));
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailBatchRevocation");
        await Mutate();
        Assert.False(await db.AuthSessions.AnyAsync(session => session.UserId == userId && session.RevokedAtUtc == null));
        Assert.False(await db.RefreshTokens.AnyAsync(token => token.UserId == userId && token.RevokedAtUtc == null));
    }

    [Fact]
    public async Task Security_stamp_change_invalidates_outstanding_mfa_challenge_without_consuming_recovery_code()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var setup = factory.Services.CreateScope();
        var manager = setup.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = (await manager.FindByNameAsync("Admin"))!;
        Assert.True((await manager.SetTwoFactorEnabledAsync(user, true)).Succeeded);
        var recoveryCode = (await manager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))!.First();
        var challenge = (await setup.ServiceProvider.GetRequiredService<AuthService>().LoginAsync(new("Admin", "Bootstrap-Cedar-River-593!")))!.MfaChallengeToken!;
        Assert.True((await manager.UpdateSecurityStampAsync(user)).Succeeded);
        using var verification = factory.Services.CreateScope();
        Assert.Null(await verification.ServiceProvider.GetRequiredService<AuthService>().CompleteMfaLoginAsync(challenge, recoveryCode));
        var freshManager = verification.ServiceProvider.GetRequiredService<UserManager<User>>();
        Assert.Equal(10, await freshManager.CountRecoveryCodesAsync((await freshManager.FindByIdAsync(user.Id.ToString()))!));
        using var freshLogin = factory.Services.CreateScope();
        var auth = freshLogin.ServiceProvider.GetRequiredService<AuthService>();
        var freshChallenge = (await auth.LoginAsync(new("Admin", "Bootstrap-Cedar-River-593!")))!.MfaChallengeToken!;
        Assert.NotNull(await auth.CompleteMfaLoginAsync(freshChallenge, recoveryCode));
    }

    [Theory]
    [InlineData("demote", "demote")]
    [InlineData("delete", "delete")]
    [InlineData("demote", "delete")]
    public async Task Concurrent_admin_removals_always_preserve_one_admin(string firstOperation, string secondOperation)
    {
        // Release two independent request scopes together. Both target different
        // admins, but only one removal may succeed when just two admins remain.
        using var factory = new GameStoreWebApplicationFactory();
        int firstId, secondId;
        using (var setup = factory.Services.CreateScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<GameStoreContext>();
            firstId = (await db.Users.SingleAsync(user => user.UserName == "Admin")).Id;
            var second = NewAdmin("second-admin");
            db.Users.Add(second);
            await db.SaveChangesAsync();
            secondId = second.Id;
        }
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<UserOperationError> Remove(int target, int actor, string operation)
        {
            await start.Task;
            using var scope = factory.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<UserService>();
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor.ToString())], "test"));
            return operation == "demote"
                ? await service.UpdateRoleAsync(target, new("customer"))
                : await service.DeleteUserAsync(target, principal);
        }
        var first = Task.Run(() => Remove(firstId, secondId, firstOperation));
        var secondTask = Task.Run(() => Remove(secondId, firstId, secondOperation));
        start.SetResult();
        var results = await Task.WhenAll(first, secondTask);
        Assert.Contains(UserOperationError.None, results);
        Assert.Contains(UserOperationError.LastAdmin, results);
        using var verification = factory.Services.CreateScope();
        Assert.Equal(1, await verification.ServiceProvider.GetRequiredService<GameStoreContext>().Users.CountAsync(user => user.Role == "admin"));
    }

    private static User NewAdmin(string name) => new()
    {
        UserName = name, NormalizedUserName = name.ToUpperInvariant(), Email = name + "@example.test",
        NormalizedEmail = name.ToUpperInvariant() + "@EXAMPLE.TEST", Role = "admin", EmailConfirmed = true,
        SecurityStamp = Guid.NewGuid().ToString()
    };

    private static string Totp(string secret)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(secret.Select(character => Convert.ToString(alphabet.IndexOf(character), 2).PadLeft(5, '0')));
        var key = Enumerable.Range(0, bits.Length / 8).Select(index => Convert.ToByte(bits.Substring(index * 8, 8), 2)).ToArray();
        var counter = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30));
        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 15;
        var binary = ((hash[offset] & 127) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }
}
