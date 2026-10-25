using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GameStore.Api.Tests;

[Collection("API integration")]
public sealed class ApiErrorResponseTests
{
    [Fact]
    public async Task Swagger_document_exposes_api_routes_and_bearer_auth_for_protected_operations()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();

        using var document = await client.GetFromJsonAsync<JsonDocument>("/swagger/v1/swagger.json");

        Assert.NotNull(document);
        var root = document.RootElement;
        Assert.Equal("Game Store API", root.GetProperty("info").GetProperty("title").GetString());
        var paths = root.GetProperty("paths");
        Assert.Contains(paths.EnumerateObject(), path => path.Name.StartsWith("/games", StringComparison.Ordinal));

        var loginOperation = paths.GetProperty("/auth/login").GetProperty("post");
        Assert.False(loginOperation.TryGetProperty("security", out _));

        var currentUserOperation = paths.GetProperty("/auth/me").GetProperty("get");
        Assert.True(currentUserOperation.TryGetProperty("security", out var security));
        Assert.Equal("Bearer", security[0].EnumerateObject().First().Name);
    }

    [Fact]
    public async Task Register_with_valid_details_returns_success()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            username = "player-one",
            email = "player-one@example.test",
            password = "Cedar-Moon-River-593!"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var registrationEmail = Assert.Single(factory.Services.GetRequiredService<TestAccountEmailSender>().SentMessages);
        Assert.Contains("Welcome", registrationEmail.Subject, StringComparison.Ordinal);
        Assert.Contains("registration was successful", registrationEmail.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Registration_requires_email_confirmation_and_username_matching_is_case_insensitive()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        var emailSender = factory.Services.GetRequiredService<TestAccountEmailSender>();

        var registration = await client.PostAsJsonAsync("/auth/register", new
        {
            username = "CaseUser",
            email = "caseuser@example.test",
            password = "Cedar-Moon-River-593!"
        });
        Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
        var confirmation = emailSender.SentMessages.Single();
        var confirmationToken = ExtractEmailToken(confirmation.Body);

        var unconfirmedLogin = await client.PostAsJsonAsync("/auth/login", new
        {
            username = "caseuser",
            password = "Cedar-Moon-River-593!"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, unconfirmedLogin.StatusCode);

        var confirmResponse = await client.PostAsJsonAsync("/auth/confirm-email", new
        {
            email = "CASEUSER@example.test",
            token = confirmationToken
        });
        Assert.Equal(HttpStatusCode.NoContent, confirmResponse.StatusCode);

        var caseVariant = await client.PostAsJsonAsync("/auth/register", new
        {
            username = "caseuser",
            email = "another@example.test",
            password = "Cedar-Moon-River-593!"
        });
        using var problem = await AssertProblemAsync(caseVariant, HttpStatusCode.Conflict, "Username is already taken");

        var duplicateEmail = await client.PostAsJsonAsync("/auth/register", new
        {
            username = "different-user",
            email = "CASEUSER@example.test",
            password = "Cedar-Moon-River-593!"
        });
        using var duplicateEmailProblem = await AssertProblemAsync(duplicateEmail, HttpStatusCode.Conflict, "Email address is already in use");

        var login = await client.PostAsJsonAsync("/auth/login", new
        {
            username = "CASEUSER",
            password = "Cedar-Moon-River-593!"
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Five_bad_passwords_lock_account_until_verified_password_reset()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        var emailSender = factory.Services.GetRequiredService<TestAccountEmailSender>();

        var registration = await client.PostAsJsonAsync("/auth/register", new
        {
            username = "locked-user",
            email = "locked@example.test",
            password = "Cedar-Moon-River-593!"
        });
        Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
        var confirmationToken = ExtractEmailToken(emailSender.SentMessages.Last().Body);
        var confirmationResponse = await client.PostAsJsonAsync("/auth/confirm-email", new
        {
            email = "locked@example.test",
            token = confirmationToken
        });
        Assert.Equal(HttpStatusCode.NoContent, confirmationResponse.StatusCode);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var invalidLogin = await client.PostAsJsonAsync("/auth/login", new
            {
                username = "LOCKED-USER",
                password = "WrongPassword123"
            });
            Assert.Equal(HttpStatusCode.Unauthorized, invalidLogin.StatusCode);
        }

        using var blockedLogin = await client.PostAsJsonAsync("/auth/login", new
        {
            username = "locked-user",
            password = "Cedar-Moon-River-593!"
        });
        Assert.Equal(HttpStatusCode.TooManyRequests, blockedLogin.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameStore.Api.Data.GameStoreContext>();
            Assert.True((await db.Users.SingleAsync(user => user.UserName == "locked-user")).LockoutEnd > DateTimeOffset.UtcNow);
        }

        using var forgotResponse = await client.PostAsJsonAsync("/auth/password/forgot", new { email = "locked@example.test" });
        Assert.Equal(HttpStatusCode.Accepted, forgotResponse.StatusCode);
        var resetToken = ExtractEmailToken(emailSender.SentMessages.Last().Body);

        using var resetResponse = await client.PostAsJsonAsync("/auth/password/reset", new
        {
            email = "locked@example.test",
            token = resetToken,
            newPassword = "NewPassword456"
        });
        Assert.Equal(HttpStatusCode.NoContent, resetResponse.StatusCode);

        // The per-IP login window remains exhausted even after account recovery.
        using var recoveredScope = factory.Services.CreateScope();
        var recoveredLogin = await recoveredScope.ServiceProvider.GetRequiredService<GameStore.Api.Services.AuthService>()
            .LoginAsync(new GameStore.Api.Dtos.LoginDto("locked-user", "NewPassword456"));
        Assert.NotNull(recoveredLogin?.Tokens);
    }

    [Fact]
    public async Task Role_change_invalidates_access_token_with_old_role()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        var emailSender = factory.Services.GetRequiredService<TestAccountEmailSender>();

        await client.PostAsJsonAsync("/auth/register", new
        {
            username = "role-user",
            email = "role@example.test",
            password = "Cedar-Moon-River-593!"
        });
        var confirmationToken = ExtractEmailToken(emailSender.SentMessages.Last().Body);
        await client.PostAsJsonAsync("/auth/confirm-email", new { email = "role@example.test", token = confirmationToken });
        using var targetLogin = await LoginAsUserAsync(client, "role-user", "Cedar-Moon-River-593!");
        var targetAccessToken = targetLogin.RootElement.GetProperty("accessToken").GetString();

        using var adminClient = factory.CreateClient();
        await SignInAsAdminAsync(adminClient);
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GameStore.Api.Data.GameStoreContext>();
        var targetUser = await dbContext.Users.SingleAsync(user => user.NormalizedUserName == "ROLE-USER");

        using var roleResponse = await adminClient.PutAsJsonAsync($"/auth/users/{targetUser.Id}/role", new { role = "admin" });
        Assert.Equal(HttpStatusCode.NoContent, roleResponse.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", targetAccessToken);
        using var oldTokenResponse = await client.GetAsync("/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, oldTokenResponse.StatusCode);
    }

    [Fact]
    public async Task Username_and_password_changes_invalidate_previous_sessions()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        var emailSender = factory.Services.GetRequiredService<TestAccountEmailSender>();
        await client.PostAsJsonAsync("/auth/register", new
        {
            username = "profile-user",
            email = "profile@example.test",
            password = "Cedar-Moon-River-593!"
        });
        var confirmationToken = ExtractEmailToken(emailSender.SentMessages.Last().Body);
        await client.PostAsJsonAsync("/auth/confirm-email", new { email = "profile@example.test", token = confirmationToken });

        using var login = await LoginAsUserAsync(client, "profile-user", "Cedar-Moon-River-593!");
        var oldAccessToken = login.RootElement.GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", oldAccessToken);

        using var usernameUpdate = await client.PutAsJsonAsync("/auth/me/username", new { username = "profile-renamed" });
        Assert.Equal(HttpStatusCode.NoContent, usernameUpdate.StatusCode);
        using var staleUsernameToken = await client.GetAsync("/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, staleUsernameToken.StatusCode);

        using var renamedLogin = await LoginAsUserAsync(client, "PROFILE-RENAMED", "Cedar-Moon-River-593!");
        var currentAccessToken = renamedLogin.RootElement.GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", currentAccessToken);
        using var passwordChange = await client.PutAsJsonAsync("/auth/me/password", new
        {
            currentPassword = "Cedar-Moon-River-593!",
            newPassword = "DifferentPassword456"
        });
        Assert.Equal(HttpStatusCode.NoContent, passwordChange.StatusCode);
        using var stalePasswordToken = await client.GetAsync("/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, stalePasswordToken.StatusCode);

        using var oldPasswordLogin = await client.PostAsJsonAsync("/auth/login", new { username = "profile-renamed", password = "Cedar-Moon-River-593!" });
        Assert.Equal(HttpStatusCode.Unauthorized, oldPasswordLogin.StatusCode);
        using var newPasswordLogin = await LoginAsUserAsync(client, "profile-renamed", "DifferentPassword456");
        Assert.True(newPasswordLogin.RootElement.TryGetProperty("accessToken", out _));
    }

    [Fact]
    public async Task Authentication_rate_limit_returns_too_many_requests()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        HttpResponseMessage? lastResponse = null;

        for (var requestNumber = 0; requestNumber < 31; requestNumber++)
        {
            lastResponse?.Dispose();
            lastResponse = await client.PostAsJsonAsync("/auth/login", new
            {
                username = "missing-account",
                password = "WrongPassword123"
            });
        }

        Assert.Equal((HttpStatusCode)429, lastResponse!.StatusCode);
        lastResponse.Dispose();
    }

    [Fact]
    public async Task Production_cookie_mode_uses_http_only_refresh_cookie_and_csrf_protection()
    {
        var previousValue = Environment.GetEnvironmentVariable("Auth__UseRefreshTokenCookies");
        Environment.SetEnvironmentVariable("Auth__UseRefreshTokenCookies", "true");
        try
        {
            using var factory = new GameStoreWebApplicationFactory();
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                HandleCookies = true
            });

            var login = await client.PostAsJsonAsync("/auth/login", new { username = "Admin", password = "Admin123" });
            login.EnsureSuccessStatusCode();
            using var loginBody = await JsonDocument.ParseAsync(await login.Content.ReadAsStreamAsync());
            Assert.False(loginBody.RootElement.TryGetProperty("refreshToken", out _));
            Assert.True(login.Headers.TryGetValues("X-CSRF-TOKEN", out var csrfHeaders));
            var csrfToken = csrfHeaders!.Single();
            Assert.Contains(login.Headers.GetValues("Set-Cookie"), cookie => cookie.Contains("__Host-GameStore.Refresh=") && cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase));

            using var rejectedRefresh = await client.PostAsync("/auth/refresh", content: null);
            Assert.Equal(HttpStatusCode.BadRequest, rejectedRefresh.StatusCode);

            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrfToken);
            using var refresh = await client.PostAsync("/auth/refresh", content: null);
            refresh.EnsureSuccessStatusCode();
            Assert.True(refresh.Headers.TryGetValues("X-CSRF-TOKEN", out var rotatedCsrfHeaders));
            Assert.Contains(refresh.Headers.GetValues("Set-Cookie"), cookie => cookie.Contains("__Host-GameStore.Refresh=") && cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase));

            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", rotatedCsrfHeaders!.Single());
            using var logout = await client.PostAsync("/auth/logout", content: null);
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
            Assert.Contains(logout.Headers.GetValues("Set-Cookie"), cookie => cookie.Contains("__Host-GameStore.Refresh=") && cookie.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("Auth__UseRefreshTokenCookies", previousValue);
        }
    }

    [Fact]
    public async Task Authenticator_mfa_challenges_login_and_recovery_codes_are_single_use()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        var emailSender = factory.Services.GetRequiredService<TestAccountEmailSender>();
        await client.PostAsJsonAsync("/auth/register", new
        {
            username = "mfa-user",
            email = "mfa@example.test",
            password = "Cedar-Moon-River-593!"
        });
        var confirmationToken = ExtractEmailToken(emailSender.SentMessages.Last().Body);
        await client.PostAsJsonAsync("/auth/confirm-email", new { email = "mfa@example.test", token = confirmationToken });
        using var login = await LoginAsUserAsync(client, "mfa-user", "Cedar-Moon-River-593!");
        var accessToken = login.RootElement.GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var setup = await client.PostAsJsonAsync("/auth/me/mfa/setup", new { currentPassword = "Cedar-Moon-River-593!" });
        Assert.True(setup.IsSuccessStatusCode, await setup.Content.ReadAsStringAsync());
        using var setupBody = await JsonDocument.ParseAsync(await setup.Content.ReadAsStreamAsync());
        var sharedKey = setupBody.RootElement.GetProperty("sharedKey").GetString()!;

        using var refreshedLogin = await LoginAsUserAsync(client, "mfa-user", "Cedar-Moon-River-593!");
        accessToken = refreshedLogin.RootElement.GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var enable = await client.PostAsJsonAsync("/auth/me/mfa/enable", new { code = GenerateTotp(sharedKey) });
        Assert.True(enable.IsSuccessStatusCode, await enable.Content.ReadAsStringAsync());
        using var enableBody = await JsonDocument.ParseAsync(await enable.Content.ReadAsStreamAsync());
        var recoveryCode = enableBody.RootElement.GetProperty("recoveryCodes")[0].GetString()!;
        client.DefaultRequestHeaders.Authorization = null;

        using var mfaRequired = await client.PostAsJsonAsync("/auth/login", new { username = "mfa-user", password = "Cedar-Moon-River-593!" });
        Assert.Equal(HttpStatusCode.Accepted, mfaRequired.StatusCode);
        using var challenge = await JsonDocument.ParseAsync(await mfaRequired.Content.ReadAsStreamAsync());
        var challengeToken = challenge.RootElement.GetProperty("challengeToken").GetString()!;

        using var mfaLogin = await client.PostAsJsonAsync("/auth/login/mfa", new { challengeToken, code = recoveryCode });
        mfaLogin.EnsureSuccessStatusCode();

        using var secondChallengeResponse = await client.PostAsJsonAsync("/auth/login", new { username = "mfa-user", password = "Cedar-Moon-River-593!" });
        using var secondChallenge = await JsonDocument.ParseAsync(await secondChallengeResponse.Content.ReadAsStreamAsync());
        using var reusedRecoveryCode = await client.PostAsJsonAsync("/auth/login/mfa", new
        {
            challengeToken = secondChallenge.RootElement.GetProperty("challengeToken").GetString(),
            code = recoveryCode
        });
        Assert.Equal(HttpStatusCode.Unauthorized, reusedRecoveryCode.StatusCode);
    }

    [Fact]
    public async Task Register_with_existing_username_returns_conflict_problem()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            username = "Admin",
            email = "duplicate@example.test",
            password = "Cedar-Moon-River-593!"
        });

        using var problem = await AssertProblemAsync(response, HttpStatusCode.Conflict, "Username is already taken");
    }

    [Theory]
    [InlineData("ab", "Cedar-Moon-River-593!", "Invalid username")]
    [InlineData("player1", "short", "Password is too short")]
    public async Task Register_with_invalid_fields_returns_actionable_problem(
        string username,
        string password,
        string expectedTitle)
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            username,
            email = "validation@example.test",
            password
        });

        using var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, expectedTitle);
    }

    [Fact]
    public async Task Login_with_invalid_credentials_returns_generic_unauthorized_problem()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/login", new
        {
            username = "missing-user",
            password = "WrongPassword123"
        });

        using var problem = await AssertProblemAsync(
            response,
            HttpStatusCode.Unauthorized,
            "Authentication failed");
        Assert.Equal("The username or password is incorrect.", problem.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Refresh_rotates_token_and_rejects_reuse()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        using var initialTokens = await LoginAsAdminAsync(client);
        using var independentSession = await LoginAsAdminAsync(client);
        var originalRefreshToken = initialTokens.RootElement.GetProperty("refreshToken").GetString();
        var independentRefreshToken = independentSession.RootElement.GetProperty("refreshToken").GetString();

        using var refreshedResponse = await client.PostAsJsonAsync("/auth/refresh", new
        {
            refreshToken = originalRefreshToken
        });
        refreshedResponse.EnsureSuccessStatusCode();
        using var refreshedTokens = await JsonDocument.ParseAsync(await refreshedResponse.Content.ReadAsStreamAsync());
        var replacementRefreshToken = refreshedTokens.RootElement.GetProperty("refreshToken").GetString();
        Assert.NotEqual(originalRefreshToken, replacementRefreshToken);
        Assert.False(string.IsNullOrWhiteSpace(refreshedTokens.RootElement.GetProperty("accessToken").GetString()));

        using var reusedResponse = await client.PostAsJsonAsync("/auth/refresh", new
        {
            refreshToken = originalRefreshToken
        });
        using var problem = await AssertProblemAsync(reusedResponse, HttpStatusCode.Unauthorized, "Invalid refresh token");

        using var revokedReplacementResponse = await client.PostAsJsonAsync("/auth/refresh", new
        {
            refreshToken = replacementRefreshToken
        });
        using var revokedReplacementProblem = await AssertProblemAsync(
            revokedReplacementResponse,
            HttpStatusCode.Unauthorized,
            "Invalid refresh token");

        using var independentRefresh = await client.PostAsJsonAsync("/auth/refresh", new
        {
            refreshToken = independentRefreshToken
        });
        Assert.Equal(HttpStatusCode.OK, independentRefresh.StatusCode);
    }

    [Fact]
    public async Task Concurrent_refresh_requests_only_rotate_once()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        using var tokens = await LoginAsAdminAsync(client);
        var refreshToken = tokens.RootElement.GetProperty("refreshToken").GetString();

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync("/auth/refresh", new { refreshToken }),
            client.PostAsJsonAsync("/auth/refresh", new { refreshToken }));

        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Unauthorized));

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GameStore.Api.Data.GameStoreContext>();
        Assert.Equal(2, await dbContext.RefreshTokens.CountAsync());
        Assert.Single(await dbContext.AuthSessions.ToListAsync());

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task Refresh_tokens_have_idle_expiry_and_seven_day_absolute_limit()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        using var tokens = await LoginAsAdminAsync(client);
        var refreshToken = tokens.RootElement.GetProperty("refreshToken").GetString();
        var now = DateTime.UtcNow;

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GameStore.Api.Data.GameStoreContext>();
        var storedToken = await dbContext.RefreshTokens.SingleAsync();

        Assert.InRange(storedToken.ExpiresAtUtc, now.AddMinutes(29), now.AddMinutes(31));
        Assert.InRange(storedToken.AbsoluteExpiresAtUtc, now.AddDays(6).AddHours(23), now.AddDays(7).AddMinutes(1));

        storedToken.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await dbContext.SaveChangesAsync();
        scope.ServiceProvider.GetRequiredService<GameStore.Api.Data.GameStoreContext>().ChangeTracker.Clear();

        using var expiredRefresh = await client.PostAsJsonAsync("/auth/refresh", new { refreshToken });
        using var problem = await AssertProblemAsync(expiredRefresh, HttpStatusCode.Unauthorized, "Invalid refresh token");
    }

    [Fact]
    public async Task Refresh_slides_idle_expiry_but_keeps_absolute_session_deadline()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        using var initialTokens = await LoginAsAdminAsync(client);
        var originalRefreshToken = initialTokens.RootElement.GetProperty("refreshToken").GetString();

        using var initialScope = factory.Services.CreateScope();
        var dbContext = initialScope.ServiceProvider.GetRequiredService<GameStore.Api.Data.GameStoreContext>();
        var initialRecord = await dbContext.RefreshTokens.SingleAsync();
        var absoluteDeadline = initialRecord.AbsoluteExpiresAtUtc;

        using var refreshResponse = await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = originalRefreshToken });
        refreshResponse.EnsureSuccessStatusCode();
        using var refreshedTokens = await JsonDocument.ParseAsync(await refreshResponse.Content.ReadAsStreamAsync());
        var replacementValue = refreshedTokens.RootElement.GetProperty("refreshToken").GetString();

        using var verifyScope = factory.Services.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<GameStore.Api.Data.GameStoreContext>();
        var records = await verifyContext.RefreshTokens.OrderBy(token => token.Id).ToListAsync();

        Assert.Equal(2, records.Count);
        Assert.Equal(records[0].SessionId, records[1].SessionId);
        Assert.Equal(absoluteDeadline, records[1].AbsoluteExpiresAtUtc);
        Assert.InRange(records[1].ExpiresAtUtc, DateTime.UtcNow.AddMinutes(29), DateTime.UtcNow.AddMinutes(31));
        Assert.NotEqual(records[1].TokenHash, replacementValue);
    }

    [Fact]
    public async Task Refresh_token_cannot_extend_session_past_absolute_expiry()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        using var tokens = await LoginAsAdminAsync(client);
        var refreshToken = tokens.RootElement.GetProperty("refreshToken").GetString();

        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<GameStore.Api.Data.GameStoreContext>();
            var storedToken = await dbContext.RefreshTokens.SingleAsync();
            storedToken.AbsoluteExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
            var storedSession = await dbContext.AuthSessions.SingleAsync();
            storedSession.AbsoluteExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
            await dbContext.SaveChangesAsync();
        }

        using var response = await client.PostAsJsonAsync("/auth/refresh", new { refreshToken });
        using var problem = await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "Invalid refresh token");
    }

    [Fact]
    public async Task Refresh_rejects_token_when_persisted_session_idle_deadline_has_passed()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        using var tokens = await LoginAsAdminAsync(client);
        var refreshToken = tokens.RootElement.GetProperty("refreshToken").GetString();

        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<GameStore.Api.Data.GameStoreContext>();
            var session = await dbContext.AuthSessions.SingleAsync();
            session.LastRefreshedAtUtc = DateTime.UtcNow.AddMinutes(-31);
            await dbContext.SaveChangesAsync();
        }

        using var response = await client.PostAsJsonAsync("/auth/refresh", new { refreshToken });
        using var problem = await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "Invalid refresh token");
    }

    [Fact]
    public async Task Logout_revokes_refresh_token_idempotently()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        using var tokens = await LoginAsAdminAsync(client);
        var refreshToken = tokens.RootElement.GetProperty("refreshToken").GetString();

        using var firstLogout = await client.PostAsJsonAsync("/auth/logout", new { refreshToken });
        using var secondLogout = await client.PostAsJsonAsync("/auth/logout", new { refreshToken });

        Assert.Equal(HttpStatusCode.NoContent, firstLogout.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, secondLogout.StatusCode);

        using var refreshResponse = await client.PostAsJsonAsync("/auth/refresh", new { refreshToken });
        using var problem = await AssertProblemAsync(refreshResponse, HttpStatusCode.Unauthorized, "Invalid refresh token");
    }

    [Fact]
    public async Task Logout_does_not_revoke_other_sessions()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        using var firstSession = await LoginAsAdminAsync(client);
        using var secondSession = await LoginAsAdminAsync(client);
        var firstRefreshToken = firstSession.RootElement.GetProperty("refreshToken").GetString();
        var secondRefreshToken = secondSession.RootElement.GetProperty("refreshToken").GetString();

        using var logoutResponse = await client.PostAsJsonAsync("/auth/logout", new { refreshToken = firstRefreshToken });
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);

        using var secondSessionRefresh = await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = secondRefreshToken });
        Assert.Equal(HttpStatusCode.OK, secondSessionRefresh.StatusCode);
    }

    [Fact]
    public async Task Login_persists_only_a_hash_of_the_refresh_token()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        using var tokens = await LoginAsAdminAsync(client);
        var refreshToken = tokens.RootElement.GetProperty("refreshToken").GetString();

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GameStore.Api.Data.GameStoreContext>();
        var persistedHash = await dbContext.RefreshTokens.Select(token => token.TokenHash).SingleAsync();

        Assert.NotEqual(refreshToken, persistedHash);
        Assert.Equal(64, persistedHash.Length);
    }

    [Fact]
    public async Task Each_login_has_its_own_session_and_rotated_tokens_keep_that_session_id()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        using var firstLogin = await LoginAsAdminAsync(client);
        using var secondLogin = await LoginAsAdminAsync(client);
        var firstRefreshToken = firstLogin.RootElement.GetProperty("refreshToken").GetString();
        var secondRefreshToken = secondLogin.RootElement.GetProperty("refreshToken").GetString();

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GameStore.Api.Data.GameStoreContext>();
        var sessions = await dbContext.AuthSessions.ToListAsync();
        Assert.Equal(2, sessions.Count);
        Assert.Equal(2, sessions.Select(session => session.Id).Distinct().Count());

        var initialTokens = await dbContext.RefreshTokens.OrderBy(token => token.Id).ToListAsync();
        Assert.Equal(2, initialTokens.Count);
        Assert.NotEqual(firstRefreshToken, initialTokens[0].TokenHash);
        Assert.NotEqual(secondRefreshToken, initialTokens[1].TokenHash);
        Assert.NotEqual(initialTokens[0].SessionId, initialTokens[1].SessionId);

        using var rotation = await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = firstRefreshToken });
        rotation.EnsureSuccessStatusCode();
        using var rotatedResponse = await JsonDocument.ParseAsync(await rotation.Content.ReadAsStreamAsync());
        var replacementToken = rotatedResponse.RootElement.GetProperty("refreshToken").GetString();

        scope.ServiceProvider.GetRequiredService<GameStore.Api.Data.GameStoreContext>().ChangeTracker.Clear();
        var tokenHistory = await dbContext.RefreshTokens.OrderBy(token => token.Id).ToListAsync();
        Assert.Equal(3, tokenHistory.Count);
        Assert.Equal(tokenHistory[0].SessionId, tokenHistory[2].SessionId);
        Assert.NotEqual(tokenHistory[1].SessionId, tokenHistory[2].SessionId);
        Assert.NotEqual(replacementToken, tokenHistory[2].TokenHash);
    }

    [Fact]
    public async Task Protected_endpoint_without_token_returns_problem_details()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/auth/me");

        using var problem = await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "Unauthorized");
    }

    [Fact]
    public async Task Game_lookup_for_missing_id_returns_not_found_problem()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        await SignInAsAdminAsync(client);

        var response = await client.GetAsync("/games/987654");

        using var problem = await AssertProblemAsync(response, HttpStatusCode.NotFound, "Game not found");
        Assert.Equal("No game exists with ID 987654.", problem.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Create_game_with_unknown_genre_returns_bad_request_problem()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        await SignInAsAdminAsync(client);

        var response = await client.PostAsJsonAsync("/games", new
        {
            name = "Test Game",
            genreId = 987654,
            price = 29.99m,
            releasedDate = "2026-01-01"
        });

        using var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "Invalid genre");
        Assert.Equal("No genre exists with ID 987654.", problem.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Games_can_be_searched_filtered_and_paginated()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        await SignInAsAdminAsync(client);
        await CreateGameAsync(client, "Zelda: Dawn", 1, 20);
        await CreateGameAsync(client, "Zelda: Dusk", 1, 40);
        await CreateGameAsync(client, "Racing Star", 4, 30);

        using var firstPage = await client.GetFromJsonAsync<JsonDocument>(
            "/games?page=1&pageSize=1&genreId=1&minPrice=20&maxPrice=40&search=ZELDA");
        using var secondPage = await client.GetFromJsonAsync<JsonDocument>(
            "/games?page=2&pageSize=1&genreId=1&minPrice=20&maxPrice=40&search=ZELDA");

        Assert.NotNull(firstPage);
        Assert.NotNull(secondPage);
        Assert.Equal(2, firstPage.RootElement.GetProperty("totalItems").GetInt32());
        Assert.Equal(2, firstPage.RootElement.GetProperty("totalPages").GetInt32());
        Assert.Equal(1, firstPage.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(1, secondPage.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(1, firstPage.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(2, secondPage.RootElement.GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task Catalog_cache_is_invalidated_after_game_mutations()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        await SignInAsAdminAsync(client);
        await CreateGameAsync(client, "Cache Sample One", 1, 20);

        using var initial = await client.GetFromJsonAsync<JsonDocument>("/games?search=cache%20sample");
        Assert.NotNull(initial);
        Assert.Equal(1, initial.RootElement.GetProperty("totalItems").GetInt32());

        await CreateGameAsync(client, "Cache Sample Two", 1, 25);
        using var afterMutation = await client.GetFromJsonAsync<JsonDocument>("/games?search=cache%20sample");
        Assert.NotNull(afterMutation);
        Assert.Equal(2, afterMutation.RootElement.GetProperty("totalItems").GetInt32());
    }

    [Fact]
    public async Task Genre_list_is_searchable_and_paginated()
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        await SignInAsAdminAsync(client);

        using var response = await client.GetFromJsonAsync<JsonDocument>("/Genres?page=1&pageSize=2&search=SPORT");

        Assert.NotNull(response);
        Assert.Equal(1, response.RootElement.GetProperty("totalItems").GetInt32());
        Assert.Equal("Sports", response.RootElement.GetProperty("items")[0].GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("/games?page=0", "Invalid pagination parameters")]
    [InlineData("/games?pageSize=101", "Invalid pagination parameters")]
    [InlineData("/games?minPrice=50&maxPrice=10", "Invalid price range")]
    [InlineData("/Genres?page=0", "Invalid pagination parameters")]
    public async Task Collection_query_validation_returns_problem_details(string url, string expectedTitle)
    {
        using var factory = new GameStoreWebApplicationFactory();
        using var client = factory.CreateClient();
        await SignInAsAdminAsync(client);

        var response = await client.GetAsync(url);

        using var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, expectedTitle);
    }

    private static async Task SignInAsAdminAsync(HttpClient client)
    {
        using var body = await LoginAsAdminAsync(client);
        var token = body.RootElement.GetProperty("accessToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static async Task<JsonDocument> LoginAsAdminAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/auth/login", new
        {
            username = "Admin",
            password = "Admin123"
        });
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
    }

    private static async Task<JsonDocument> LoginAsUserAsync(HttpClient client, string username, string password)
    {
        var response = await client.PostAsJsonAsync("/auth/login", new { username, password });
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
    }

    private static string ExtractEmailToken(string body)
    {
        var tokenStart = body.IndexOf("\n\n", StringComparison.Ordinal) + 2;
        var tokenEnd = body.IndexOf("\n\n", tokenStart, StringComparison.Ordinal);
        Assert.True(tokenStart >= 2 && tokenEnd > tokenStart, "The test email did not contain a token.");
        var value = body[tokenStart..tokenEnd].Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var confirmationLink))
        {
            return Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(confirmationLink.Fragment.TrimStart('#'))["token"].ToString();
        }
        return value; // Password-reset emails still contain a token.
    }

    private static string GenerateTotp(string base32Secret)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(base32Secret.TrimEnd('=').Select(character =>
            Convert.ToString(alphabet.IndexOf(character), 2).PadLeft(5, '0')));
        var key = Enumerable.Range(0, bits.Length / 8)
            .Select(index => Convert.ToByte(bits.Substring(index * 8, 8), 2))
            .ToArray();
        var counter = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30));
        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) |
                     ((hash[offset + 1] & 0xff) << 16) |
                     ((hash[offset + 2] & 0xff) << 8) |
                     (hash[offset + 3] & 0xff);
        return (binary % 1_000_000).ToString("D6");
    }

    private static async Task CreateGameAsync(HttpClient client, string name, int genreId, decimal price)
    {
        var response = await client.PostAsJsonAsync("/games", new
        {
            name,
            genreId,
            price,
            releasedDate = "2026-01-01"
        });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<JsonDocument> AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedTitle)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal((int)expectedStatus, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(expectedTitle, body.RootElement.GetProperty("title").GetString());
        return body;
    }
}

// AUTOMATEDTEST SCRIPT
// dotnet test GameStore.Api.Tests/GameStore.Api.Tests.csproj
