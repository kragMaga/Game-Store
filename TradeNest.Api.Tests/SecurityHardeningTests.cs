using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using TradeNest.Api.Entities;
using TradeNest.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Identity;
using System.Buffers.Binary;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using TradeNest.Api.Data;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TradeNest.Api.Tests;

[Collection("API integration")]
public sealed class SecurityHardeningTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Email_change_keeps_old_address_and_sessions_until_valid_confirmation(bool addressClaimedBeforeConfirmation)
    {
        var sender = new RecoverableEmailSender();
        using var factory = new TradeNestWebApplicationFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.RemoveAll<IAccountEmailSender>();
            services.AddSingleton<IAccountEmailSender>(sender);
        }));
        using var client = configured.CreateClient();
        using var login = await client.PostAsJsonAsync("/auth/login", new { username = "Admin", password = "Bootstrap-Cedar-River-593!" });
        var tokens = await login.Content.ReadFromJsonAsync<TradeNest.Api.Dtos.AuthTokensDto>();
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens!.AccessToken);
        const string newEmail = "new-admin@example.test";
        using var failedRequest = await client.PutAsJsonAsync("/auth/me/email", new { email = newEmail });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failedRequest.StatusCode);
        using var beforeConfirmation = await client.GetAsync("/auth/me");
        Assert.Equal(HttpStatusCode.OK, beforeConfirmation.StatusCode);

        using var scope = configured.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeNestContext>();
        var original = await db.Users.AsNoTracking().SingleAsync(user => user.UserName == "Admin");
        Assert.Equal("admin@example.test", original.Email);
        Assert.True(original.EmailConfirmed);
        sender.Fail = false;
        using var request = await client.PutAsJsonAsync("/auth/me/email", new { email = newEmail });
        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
        var link = new Uri(sender.Body.Split('\n').Single(line => line.StartsWith("https://", StringComparison.Ordinal)));
        var parameters = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(link.Fragment.TrimStart('#'));
        Assert.Equal("email-change", parameters["purpose"].ToString());
        Assert.Equal(newEmail, parameters["email"].ToString());
        Assert.Equal(original.Id.ToString(), parameters["userId"].ToString());
        var token = parameters["token"].ToString();
        using var forged = await client.PostAsJsonAsync("/auth/email/confirm-change", new { userId = original.Id, email = newEmail, token = "forged-token" });
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        using var wrongAddress = await client.PostAsJsonAsync("/auth/email/confirm-change", new { userId = original.Id, email = "tampered@example.test", token });
        Assert.Equal(HttpStatusCode.BadRequest, wrongAddress.StatusCode);
        using var stillSignedIn = await client.GetAsync("/auth/me");
        Assert.Equal(HttpStatusCode.OK, stillSignedIn.StatusCode);
        var unchanged = await db.Users.AsNoTracking().SingleAsync(user => user.Id == original.Id);
        Assert.Equal(original.Email, unchanged.Email);
        Assert.Equal(original.SecurityStamp, unchanged.SecurityStamp);

        if (addressClaimedBeforeConfirmation)
        {
            db.Users.Add(new User { UserName = "other-player", NormalizedUserName = "OTHER-PLAYER", Email = newEmail,
                NormalizedEmail = newEmail.ToUpperInvariant()});
            await db.SaveChangesAsync();
        }
        using var confirmed = await client.PostAsJsonAsync("/auth/email/confirm-change", new { userId = original.Id, email = newEmail, token });
        Assert.Equal(addressClaimedBeforeConfirmation ? HttpStatusCode.BadRequest : HttpStatusCode.NoContent, confirmed.StatusCode);
        var updated = await db.Users.AsNoTracking().SingleAsync(user => user.Id == original.Id);
        Assert.Equal(addressClaimedBeforeConfirmation ? original.Email : newEmail, updated.Email);
        Assert.True(updated.EmailConfirmed);
        using var oldSession = await client.GetAsync("/auth/me");
        Assert.Equal(addressClaimedBeforeConfirmation ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, oldSession.StatusCode);
        using var newLogin = await client.PostAsJsonAsync("/auth/login", new { username = "Admin", password = "Bootstrap-Cedar-River-593!" });
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
        if (!addressClaimedBeforeConfirmation)
        {
            using var replay = await client.PostAsJsonAsync("/auth/email/confirm-change", new { userId = original.Id, email = newEmail, token });
            Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        }
    }

    [Fact]
    public async Task Confirmation_link_uses_frontend_and_only_valid_identity_token_confirms_email()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddDataProtection().UseEphemeralDataProtectionProvider()));
        using var client = configured.CreateClient();
        const string email = "confirm+player@example.test";
        using var registration = await client.PostAsJsonAsync("/auth/register",
            new { username = "confirm-player", email, password = "Cedar-Moon-River-593!" });
        Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
        var message = Assert.Single(configured.Services.GetRequiredService<TestAccountEmailSender>().SentMessages);
        Assert.DoesNotContain("POST /auth/confirm-email", message.Body);
        var link = new Uri(message.Body.Split('\n').Single(line => line.StartsWith("https://", StringComparison.Ordinal)));
        Assert.Equal("https://tradenest.example.test/confirm-email", link.GetLeftPart(UriPartial.Path));
        Assert.Empty(link.Query);
        var parameters = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(link.Fragment.TrimStart('#'));
        Assert.Equal(email, parameters["email"].ToString());
        var token = parameters["token"].ToString();
        Assert.NotEmpty(token);
        using var scope = configured.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeNestContext>();
        foreach (var invalidToken in new[] { "%not-base64", Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("forged-token")) })
        {
            using var invalid = await client.PostAsJsonAsync("/auth/confirm-email", new { email, token = invalidToken });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.False((await db.Users.AsNoTracking().SingleAsync(user => user.Email == email)).EmailConfirmed);
        }
        using var confirmed = await client.PostAsJsonAsync("/auth/confirm-email", new { email, token });
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        Assert.True((await db.Users.AsNoTracking().SingleAsync(user => user.Email == email)).EmailConfirmed);
    }

    [Fact]
    public async Task Email_outage_preserves_registration_and_allows_resending_without_duplicate_accounts()
    {
        var sender = new RecoverableEmailSender();
        using var factory = new TradeNestWebApplicationFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.RemoveAll<IAccountEmailSender>();
            services.AddSingleton<IAccountEmailSender>(sender);
        }));
        using var client = configured.CreateClient();
        var registration = new { username = "email-outage", email = "outage@example.test", password = "Cedar-Moon-River-593!" };
        using var response = await client.PostAsJsonAsync("/auth/register", registration);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Account created", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, sender.Attempts);

        using var duplicate = await client.PostAsJsonAsync("/auth/register", registration);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(1, sender.Attempts);
        using var scope = configured.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeNestContext>();
        var user = Assert.Single(await db.Users.Where(user => user.NormalizedEmail == "OUTAGE@EXAMPLE.TEST").ToListAsync());
        Assert.False(user.EmailConfirmed);

        using var failedResend = await client.PostAsJsonAsync("/auth/email/resend-confirmation", new { registration.email });
        Assert.Equal(HttpStatusCode.Accepted, failedResend.StatusCode);
        sender.Fail = false;
        using var successfulResend = await client.PostAsJsonAsync("/auth/email/resend-confirmation", new { registration.email });
        Assert.Equal(HttpStatusCode.Accepted, successfulResend.StatusCode);
        Assert.Equal(await failedResend.Content.ReadAsStringAsync(), await successfulResend.Content.ReadAsStringAsync());
        Assert.Equal(3, sender.Attempts);
        Assert.Contains("confirm your account", sender.Subject);
        Assert.Equal(1, await db.Users.CountAsync(user => user.NormalizedEmail == "OUTAGE@EXAMPLE.TEST"));
    }

    private sealed class RecoverableEmailSender : IAccountEmailSender
    {
        public bool Fail { get; set; } = true;
        public int Attempts { get; private set; }
        public string Subject { get; private set; } = "";
        public string Body { get; private set; } = "";
        public Task SendAsync(string emailAddress, string subject, string textBody, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (Fail)
                throw new System.Net.Mail.SmtpException("Simulated email outage");
            Subject = subject;
            Body = textBody;
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registration_does_not_misreport_unrelated_unique_or_primary_key_conflicts(bool primaryKey)
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeNestContext>();
        if (primaryKey)
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER ForceRegistrationPrimaryKeyCollision BEFORE INSERT ON Users
                WHEN NEW.Username = 'collision-user'
                BEGIN
                    INSERT INTO Users SELECT * FROM Users LIMIT 1;
                END;
                """);
        }
        else
        {
            db.Users.Add(new User
            {
                UserName = "existing-customer", NormalizedUserName = "EXISTING-CUSTOMER",
                Email = "existing@example.test", NormalizedEmail = "EXISTING@EXAMPLE.TEST"});
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX TestUniqueConfirmation ON Users(EmailConfirmed)");
        }

        using var response = await client.PostAsJsonAsync("/auth/register", new
        {
            username = "collision-user", email = "collision@example.test", password = "Cedar-Moon-River-593!"
        });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.Equal("Registration failed", problem!.Title);
        Assert.Empty(factory.Services.GetRequiredService<TestAccountEmailSender>().SentMessages);
    }

    [Fact]
    public void Password_hashes_use_configured_work_factor_salts_and_upgrade_older_hashes()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var user = new User {};
        const string password = "Unusual-Cedar-Moon-593!";
        var hash = hasher.HashPassword(user, password);
        Assert.NotEqual(hash, hasher.HashPassword(user, password));
        Assert.Equal(210_000u, BinaryPrimitives.ReadUInt32BigEndian(Convert.FromBase64String(hash).AsSpan(5, 4)));
        Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(user, hash, password));
        var older = new PasswordHasher<User>(Options.Create(new PasswordHasherOptions { IterationCount = 100_000 }));
        Assert.Equal(PasswordVerificationResult.SuccessRehashNeeded,
            hasher.VerifyHashedPassword(user, older.HashPassword(user, password), password));
    }

    [Fact]
    public async Task Registration_rejects_common_password_before_sending_confirmation()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/auth/register",
            new { username = "screened-user", email = "screened@example.test", password = "Password123" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Services.GetRequiredService<TestAccountEmailSender>().SentMessages);
    }

    [Theory]
    [InlineData("10.0.0.10", "203.0.113.7")]
    [InlineData("10.0.0.11", "10.0.0.11")]
    public async Task Only_configured_proxy_can_forward_client_ip(string peer, string expected)
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var configuredFactory = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["ReverseProxy:KnownProxies:0"] = "10.0.0.10" })));
        var options = configuredFactory.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>();
        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, options);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.7";
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        await middleware.Invoke(context);
        Assert.Equal(IPAddress.Parse(expected), context.Connection.RemoteIpAddress);
        Assert.Equal(peer == "10.0.0.10" ? "https" : "", context.Request.Scheme);
    }

    [Fact]
    public async Task Common_password_is_rejected_without_network_access()
    {
        var handler = new RangeHandler("", HttpStatusCode.OK);
        var result = await Validator(handler).ValidateAsync(null!, new User {}, "Password123");
        Assert.False(result.Succeeded);
        Assert.Equal("PasswordCompromised", Assert.Single(result.Errors).Code);
        Assert.Null(handler.RequestUri);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Breach_lookup_sends_only_hash_prefix_and_checks_suffix(bool compromised)
    {
        const string password = "Unusual-Cedar-Moon-593!";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password)));
        var handler = new RangeHandler($"{hash[5..]}:{(compromised ? 7 : 0)}\r\n", HttpStatusCode.OK);
        var result = await Validator(handler).ValidateAsync(null!, new User {}, password);
        Assert.Equal(!compromised, result.Succeeded);
        Assert.Equal($"https://api.pwnedpasswords.com/range/{hash[..5]}", handler.RequestUri?.ToString());
        Assert.True(handler.Padded);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "")]
    [InlineData(HttpStatusCode.OK, "")]
    [InlineData(HttpStatusCode.OK, "invalid-response")]
    public async Task Screening_allows_password_when_service_is_unavailable_or_invalid(HttpStatusCode status, string body)
    {
        var result = await Validator(new RangeHandler(body, status))
            .ValidateAsync(null!, new User {}, "Unusual-Cedar-Moon-593!");
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Screening_allows_password_when_network_fails_or_times_out(bool timeout)
    {
        Exception failure = timeout ? new TaskCanceledException("Simulated timeout") : new HttpRequestException("Simulated network failure");
        var result = await Validator(new RangeHandler("", HttpStatusCode.OK, failure))
            .ValidateAsync(null!, new User {}, "Unusual-Cedar-Moon-593!");
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Password_attempts_have_stricter_limit_and_spoofed_forwarded_ip_does_not_bypass_it()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var client = factory.CreateClient();
        for (var attempt = 0; attempt < 6; attempt++)
        {
            client.DefaultRequestHeaders.Remove("X-Forwarded-For");
            client.DefaultRequestHeaders.Add("X-Forwarded-For", $"203.0.113.{attempt + 1}");
            using var response = await client.PostAsJsonAsync("/auth/login", new { username = "missing-user", password = "Invalid-Password-593!" });
            Assert.Equal(attempt < 5 ? HttpStatusCode.Unauthorized : HttpStatusCode.TooManyRequests, response.StatusCode);
        }
    }

    [Fact]
    public async Task Recovery_requests_share_stricter_limit()
    {
        using var factory = new TradeNestWebApplicationFactory();
        using var client = factory.CreateClient();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var response = await client.PostAsJsonAsync(
                attempt % 2 == 0 ? "/auth/password/forgot" : "/auth/email/resend-confirmation",
                new { email = "missing@example.test" });
            Assert.Equal(attempt < 3 ? HttpStatusCode.Accepted : HttpStatusCode.TooManyRequests, response.StatusCode);
            if (attempt == 3)
                Assert.InRange(response.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 900);
        }
    }

    private static ScreenedPasswordValidator Validator(RangeHandler handler)
    {
        var configuration = new ConfigurationBuilder().Build();
        return new ScreenedPasswordValidator(new ClientFactory(handler), configuration);
    }

    private sealed class ClientFactory(RangeHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler)
        {
            BaseAddress = new Uri("https://api.pwnedpasswords.com/range/")
        };
    }

    private sealed class RangeHandler(string body, HttpStatusCode status, Exception? failure = null) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public bool Padded { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (failure is not null) throw failure;
            RequestUri = request.RequestUri;
            Padded = request.Headers.GetValues("Add-Padding").Single() == "true";
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
