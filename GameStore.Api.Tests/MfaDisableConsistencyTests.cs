using System.Net;
using System.Net.Http.Json;
using GameStore.Api.Data;
using GameStore.Api.Dtos;
using GameStore.Api.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace GameStore.Api.Tests;

[Collection("API integration")]
// Inject failures at individual disable steps, then check persisted state from a
// fresh scope. This catches partial commits that a successful HTTP response can hide.
public sealed class MfaDisableConsistencyTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("disable")]
    [InlineData("reset")]
    [InlineData("stamp")]
    [InlineData("revoke")]
    public async Task Disable_mfa_commits_all_changes_or_rolls_everything_back(string failure)
    {
        var fault = new FailurePlan();
        using var factory = new GameStoreWebApplicationFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(fault);
            services.AddScoped<UserManager<User>, FaultInjectingUserManager>();
        }));
        using var client = configured.CreateClient();
        int userId;
        string key;
        string recoveryCode;
        using (var setup = configured.Services.CreateScope())
        {
            var manager = setup.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = (await manager.FindByNameAsync("Admin"))!;
            userId = user.Id;
            Assert.True((await manager.ResetAuthenticatorKeyAsync(user)).Succeeded);
            key = (await manager.GetAuthenticatorKeyAsync(user))!;
            recoveryCode = (await manager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))!.First();
        }
        using var login = await client.PostAsJsonAsync("/auth/login", new { username = "Admin", password = "Bootstrap-Cedar-River-593!" });
        login.EnsureSuccessStatusCode();
        var tokens = (await login.Content.ReadFromJsonAsync<AuthTokensDto>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
        string stamp;
        using (var setup = configured.Services.CreateScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<GameStoreContext>();
            await db.Users.Where(user => user.Id == userId).ExecuteUpdateAsync(update => update.SetProperty(user => user.TwoFactorEnabled, true));
            stamp = (await db.Users.AsNoTracking().SingleAsync(user => user.Id == userId)).SecurityStamp!;
            if (failure == "revoke")
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE TRIGGER FailRefreshRevocation BEFORE UPDATE ON RefreshTokens
                    WHEN NEW.RevokedAtUtc IS NOT NULL
                    BEGIN SELECT RAISE(ABORT, 'Simulated revocation failure'); END;
                    """);
        }
        fault.Step = failure;
        using var response = await client.PostAsJsonAsync("/auth/me/mfa/disable", new { currentPassword = "Bootstrap-Cedar-River-593!", code = recoveryCode });
        Assert.Equal(failure == "none" ? HttpStatusCode.NoContent : HttpStatusCode.InternalServerError, response.StatusCode);
        using (var verification = configured.Services.CreateScope())
        {
            var db = verification.ServiceProvider.GetRequiredService<GameStoreContext>();
            var manager = verification.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = (await manager.FindByIdAsync(userId.ToString()))!;
            Assert.Equal(failure != "none", user.TwoFactorEnabled);
            Assert.Equal(failure == "none" ? 9 : 10, await manager.CountRecoveryCodesAsync(user));
            if (failure == "none")
            {
                Assert.NotEqual(stamp, user.SecurityStamp);
                Assert.NotEqual(key, await manager.GetAuthenticatorKeyAsync(user));
            }
            else
            {
                Assert.Equal(stamp, user.SecurityStamp);
                Assert.Equal(key, await manager.GetAuthenticatorKeyAsync(user));
            }
            Assert.Equal(failure != "none", await db.AuthSessions.AnyAsync(session => session.UserId == userId && session.RevokedAtUtc == null));
            Assert.Equal(failure != "none", await db.RefreshTokens.AnyAsync(token => token.UserId == userId && token.RevokedAtUtc == null));
            if (failure == "revoke") await db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailRefreshRevocation");
        }
        using var oldSession = await client.GetAsync("/auth/me");
        Assert.Equal(failure == "none" ? HttpStatusCode.Unauthorized : HttpStatusCode.OK, oldSession.StatusCode);
        if (failure != "none")
        {
            fault.Step = "none";
            using var retry = await client.PostAsJsonAsync("/auth/me/mfa/disable", new { currentPassword = "Bootstrap-Cedar-River-593!", code = recoveryCode });
            Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
        }
    }

    [Fact]
    public async Task Disable_mfa_requires_authorization()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/auth/me/mfa/disable", new { currentPassword = "invalid", code = "invalid" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Disable_mfa_limits_repeated_password_and_code_attempts()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/auth/login", new { username = "Admin", password = "Bootstrap-Cedar-River-593!" });
        var tokens = (await login.Content.ReadFromJsonAsync<AuthTokensDto>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
        for (var attempt = 0; attempt < 6; attempt++)
        {
            using var response = await client.PostAsJsonAsync("/auth/me/mfa/disable", new { currentPassword = "invalid", code = "invalid" });
            Assert.Equal(attempt < 5 ? HttpStatusCode.BadRequest : HttpStatusCode.TooManyRequests, response.StatusCode);
        }
    }

    private sealed class FailurePlan { public string Step { get; set; } = "none"; }

    // This substitute is registered only in tests. It lets Identity report a failed
    // result without a database exception, exercising the explicit result checks.
    private sealed class FaultInjectingUserManager(
        IUserStore<User> store, IOptions<IdentityOptions> options, IPasswordHasher<User> hasher,
        IEnumerable<IUserValidator<User>> userValidators, IEnumerable<IPasswordValidator<User>> passwordValidators,
        ILookupNormalizer normalizer, IdentityErrorDescriber describer, IServiceProvider services,
        ILogger<UserManager<User>> logger, FailurePlan fault)
        : UserManager<User>(store, options, hasher, userValidators, passwordValidators, normalizer, describer, services, logger)
    {
        private static Task<IdentityResult> Failure() => Task.FromResult(IdentityResult.Failed(new IdentityError { Code = "SimulatedFailure" }));
        public override Task<IdentityResult> SetTwoFactorEnabledAsync(User user, bool enabled) =>
            fault.Step == "disable" ? Failure() : base.SetTwoFactorEnabledAsync(user, enabled);
        public override Task<IdentityResult> ResetAuthenticatorKeyAsync(User user) =>
            fault.Step == "reset" ? Failure() : base.ResetAuthenticatorKeyAsync(user);
        public override Task<IdentityResult> UpdateSecurityStampAsync(User user) =>
            fault.Step == "stamp" ? Failure() : base.UpdateSecurityStampAsync(user);
    }
}
