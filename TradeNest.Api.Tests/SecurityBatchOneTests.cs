using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using TradeNest.Api.Data;
using TradeNest.Api.Dtos;
using TradeNest.Api.Entities;
using TradeNest.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TradeNest.Api.Tests;

[Collection("API integration")]
// Focused checks for session revocation, all-or-nothing security updates, stale MFA
// challenges, and authentication stamp validation. These do not run the full suite.
public sealed class SecurityBatchOneTests
{
    [Fact]
    public async Task Username_changes_revoke_existing_refresh_credentials()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeNestContext>();
        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
        var users = scope.ServiceProvider.GetRequiredService<UserService>();
        var userId = (await db.Users.SingleAsync(user => user.UserName == "Admin")).Id;
        var tokens = (await auth.LoginAsync(new("Admin", "Bootstrap-Cedar-River-593!")))!.Tokens!;
        Assert.Equal(AccountOperationResult.Success, await users.UpdateUsernameAsync(userId, "RenamedAdmin"));
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
        using var factory = new TradeNestWebApplicationFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddDataProtection().UseEphemeralDataProtectionProvider()));
        using var scope = configured.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeNestContext>();
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
        using var factory = new TradeNestWebApplicationFactory();
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
