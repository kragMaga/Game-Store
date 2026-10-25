using System.Text;
using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using GameStore.Api.Data;
using GameStore.Api.Documentation;
using GameStore.Api.Entities;
using GameStore.Api.Endpoints;
using GameStore.Api.Middleware;
using GameStore.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.OpenApi.Models;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Browser deployments default to HttpOnly refresh cookies. Development can use
// JSON refresh tokens for API tools; an explicit configuration value takes priority.
builder.Configuration["Auth:UseRefreshTokenCookies"] ??= (!builder.Environment.IsDevelopment()).ToString();

// Register dependencies before Build() so endpoint handlers can request them.
// Scoped services share one DbContext per request; the catalog cache is shared
// across requests within this application instance.
var connString = builder.Configuration.GetConnectionString("GameStore");
builder.Services.AddSqlite<GameStoreContext>(connString);
builder.Services.AddMemoryCache();
// Refresh/logout cookie requests need a CSRF token as well as the cookie.
builder.Services.AddAntiforgery(AuthCookieSupport.ConfigureAntiforgeryCookie);
builder.Services.AddSingleton<CatalogResponseCache>();
builder.Services.AddScoped<AuthService>();
// Each component shares the request's DbContext/UserManager. Splitting ownership
// does not split the transactions protecting multi-step account changes.
builder.Services.AddScoped<AuthSessionService>();
builder.Services.AddScoped<MfaService>();
builder.Services.AddScoped<AccountEmailService>();
builder.Services.AddHealthChecks().AddCheck<DatabaseReadinessCheck>("database", tags: ["ready"],
    timeout: TimeSpan.FromSeconds(3));
builder.Services.AddScoped<UserService>();
// Retire old authentication history in short background batches, preserving a
// seven-day investigation window and all replay evidence for active sessions.
builder.Services.AddScoped<AuthDataCleanup>();
builder.Services.AddHostedService<AuthDataCleanupWorker>();
builder.Services.AddOptions<AuthRateLimitOptions>().BindConfiguration("RateLimits")
    .ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<DeploymentOptions>().BindConfiguration("Deployment")
    .Validate(options => !options.MultipleInstances || options.SharedRateLimitingEnforced,
        "Multiple API instances require shared gateway rate limiting (Deployment:SharedRateLimitingEnforced).")
    .ValidateOnStart();
// Identity handles password hashing, account validation, lockout, MFA, and its
// confirmation/reset tokens. These options define the baseline account policy.
builder.Services.AddIdentityCore<User>(options =>
{
    options.User.RequireUniqueEmail = true;
    // Use the same character list as DTOs/services, including periods.
    options.User.AllowedUserNameCharacters = UsernameRules.AllowedCharacters;
    options.Password.RequiredLength = 8;
    options.Password.RequireDigit = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireNonAlphanumeric = false;
    options.Lockout.AllowedForNewUsers = true;
    options.Lockout.MaxFailedAccessAttempts = 5;
    // A long lockout is intentional here: account recovery clears it.
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromDays(3650);
})
    // Persist Identity accounts and tokens in the existing EF Core database.
    .AddEntityFrameworkStores<GameStoreContext>()
    // Add our length/character rule alongside Identity's default user validators.
    .AddUserValidator<UsernameValidator>()
    // Add common/compromised-password screening alongside the strength rules above.
    .AddPasswordValidator<ScreenedPasswordValidator>()
    .AddSignInManager()
    // Supplies the providers used for email/reset tokens and authenticator MFA.
    .AddDefaultTokenProviders();
// Normalize usernames consistently so case differences cannot create two accounts.
builder.Services.AddSingleton<ILookupNormalizer, AsciiLookupNormalizer>();
// Scoped because recent-auth checks read the current account/session from the DB.
// TimeProvider supplies the clock and lets tests simulate expiry without waiting.
builder.Services.AddScoped<RecentAuthenticationService>();
builder.Services.AddSingleton(TimeProvider.System);
// Increase Identity's password-hashing work factor. Older hashes stay verifiable
// and Identity can upgrade their work factor during password verification.
builder.Services.Configure<PasswordHasherOptions>(options => options.IterationCount = 210_000);
// The validator uses this named client for hash-prefix breach lookups. A timeout
// bounds how long a new-password check can wait for the external service.
builder.Services.AddHttpClient("PwnedPasswords", client =>
{
    client.BaseAddress = new Uri("https://api.pwnedpasswords.com/range/");
    client.Timeout = TimeSpan.FromSeconds(5);
});
// Read the final configuration when options are resolved, including test/host
// overrides. Accept forwarded IP/scheme headers only from explicitly trusted proxies.
builder.Services.AddOptions<ForwardedHeadersOptions>().Configure<IConfiguration>((options, configuration) =>
{
    var trustedProxies = configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [];
    options.ForwardedHeaders = trustedProxies.Length == 0 ? ForwardedHeaders.None
        : ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Remove implicit trust entries; an empty configured list disables forwarding.
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
    foreach (var proxy in trustedProxies)
        options.KnownProxies.Add(IPAddress.Parse(proxy));
    // Process one proxy hop. A longer deployment chain needs an explicit trust review.
    options.ForwardLimit = 1;
});
// Request handlers persist encrypted delivery intent; only the worker contacts SMTP.
builder.Services.AddScoped<IAccountEmailSender, OutboxAccountEmailSender>();
builder.Services.AddTransient<IEmailTransport, SmtpAccountEmailSender>();
builder.Services.AddScoped<EmailOutboxProcessor>();
builder.Services.AddHostedService<EmailOutboxWorker>();
// A frontend is optional. Validate a supplied link target, while allowing API
// clients to confirm with the emailed token when no page has been built yet.
builder.Services.AddOptions<ConfirmationPageOptions>().BindConfiguration("Email")
    .Validate(options => ConfirmationPageOptions.IsValid(options.ConfirmationPageUrl),
        "Email:ConfirmationPageUrl must be an absolute HTTPS URL without credentials or a fragment.")
    .ValidateOnStart();

// Fail startup when production email settings are missing or TLS is disabled,
// rather than discovering these configuration problems during account recovery.
if (!builder.Environment.IsDevelopment() &&
    (string.IsNullOrWhiteSpace(builder.Configuration["Email:Smtp:Host"]) ||
     string.IsNullOrWhiteSpace(builder.Configuration["Email:FromAddress"])))
{
    throw new InvalidOperationException("Production email delivery must be configured using Email:Smtp:Host and Email:FromAddress.");
}

if (!builder.Environment.IsDevelopment() && !builder.Configuration.GetValue("Email:Smtp:EnableSsl", true))
{
    throw new InvalidOperationException("Production SMTP delivery must use TLS (Email:Smtp:EnableSsl=true).");
}

builder.Services.AddRateLimiter(_ => { });
// Resolve options through DI so host/test configuration overrides are respected.
// Values are a startup snapshot: restart after changing the budgets.
builder.Services.AddOptions<RateLimiterOptions>().Configure<IOptions<AuthRateLimitOptions>>((options, configured) =>
{
    var limits = configured.Value;
    // This additional limiter runs alongside the named auth baseline.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        // Recovery/email operations share a tighter budget; password-checking
        // routes each get their own budget so one route cannot exhaust another's.
        var path = context.Request.Path.Value?.TrimEnd('/').ToLowerInvariant();
        var reset = path is "/auth/password/forgot" or "/auth/password/reset" or "/auth/email/resend-confirmation" or "/auth/me/email";
        var passwordAttempt = path is "/auth/login" or "/auth/login/mfa" or "/auth/reauthenticate" or "/auth/me/password"
            or "/auth/me/mfa/setup" or "/auth/me/mfa/disable";
        if (!reset && !passwordAttempt)
            return RateLimitPartition.GetNoLimiter("other");

        // Normalize IPv4/IPv6 representations of the same IP. Missing IPs share
        // a conservative fallback bucket rather than getting unlimited requests.
        var key = $"{(reset ? "recovery" : path)}:{context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown"}";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = reset ? limits.RecoveryPermits : limits.PasswordPermits,
            Window = TimeSpan.FromSeconds(reset ? limits.RecoveryWindowSeconds : limits.PasswordWindowSeconds),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        // Log only the route template and diagnostic identifiers, never submitted
        // credentials, MFA codes, headers containing tokens, or request bodies.
        var httpContext = context.HttpContext;
        var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("GameStore.Api.Security.RateLimiting");
        var route = (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
        logger.LogWarning(new EventId(429, "RateLimitRejected"),
            "Rate limit rejected request for route {Route}. ClientIpAvailable: {ClientIpAvailable}. TraceId: {TraceId}",
            route, httpContext.Connection.RemoteIpAddress is not null, httpContext.TraceIdentifier);
        // A fixed window may be nearly over. Report its remaining time instead
        // of always telling the client to wait a whole new window.
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var remaining))
        {
            var retryAfter = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
            context.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        await Results.Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "Too many requests",
            detail: "Wait before retrying.").ExecuteAsync(context.HttpContext);
    };
    // Baseline budget shared across endpoints that require the "auth" policy.
    // A sensitive endpoint must pass both this budget and the global limiter above.
    options.AddPolicy("auth", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        httpContext.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limits.BaselinePermits,
            Window = TimeSpan.FromSeconds(limits.BaselineWindowSeconds),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});

// Validate token settings at startup. These values control signature verification,
// short-lived access tokens, refresh inactivity, and the absolute session deadline.
var jwtKey = builder.Configuration["Jwt:Key"] ?? throw new InvalidOperationException("JWT key is missing. Configure Jwt:Key using user secrets or an environment variable.");
if (Encoding.UTF8.GetByteCount(jwtKey) < 32)
{
    throw new InvalidOperationException("JWT key must be at least 32 bytes long.");
}

var accessTokenMinutes = builder.Configuration.GetValue("Jwt:AccessTokenMinutes", 15);
var refreshTokenIdleMinutes = builder.Configuration.GetValue("Jwt:RefreshTokenIdleMinutes", 30);
var maxSessionDays = builder.Configuration.GetValue("Jwt:MaxSessionDays", 7);
if (accessTokenMinutes is < 1 or > 60)
{
    throw new InvalidOperationException("Jwt:AccessTokenMinutes must be between 1 and 60.");
}

if (refreshTokenIdleMinutes is < 5 or > 1440)
{
    throw new InvalidOperationException("Jwt:RefreshTokenIdleMinutes must be between 5 and 1440.");
}

if (maxSessionDays is < 1 or > 90)
{
    throw new InvalidOperationException("Jwt:MaxSessionDays must be between 1 and 90.");
}

var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? throw new InvalidOperationException("JWT issuer is missing.");

var jwtAudience = builder.Configuration["Jwt:Audience"] ?? throw new InvalidOperationException("JWT audience is missing.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
    {
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                // A valid JWT signature is not enough: logout, lockout, password
                // changes, or role changes can invalidate it before its expiry.
                // Compare its account stamp and session against current database state.
                var userIdValue = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                var securityStamp = context.Principal?.FindFirstValue("security_stamp");
                var sessionId = context.Principal?.FindFirstValue("session_id");
                if (!int.TryParse(userIdValue, out var userId) ||
                    string.IsNullOrWhiteSpace(securityStamp) ||
                    string.IsNullOrWhiteSpace(sessionId))
                {
                    context.Fail("The access token is missing required session claims.");
                    return;
                }

                var db = context.HttpContext.RequestServices.GetRequiredService<GameStoreContext>();
                var now = DateTime.UtcNow;
                var session = await db.AuthSessions.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.Id == sessionId && item.UserId == userId);
                var user = await db.Users.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.Id == userId);

                if (session is null || user is null ||
                    session.RevokedAtUtc.HasValue ||
                    session.AbsoluteExpiresAtUtc <= now ||
                    session.LastRefreshedAtUtc.AddMinutes(refreshTokenIdleMinutes) <= now ||
                    user.LockoutEnd > now ||
                    !string.Equals(user.SecurityStamp, securityStamp, StringComparison.Ordinal))
                {
                    context.Fail("The access token's account or session is no longer valid.");
                }
            }
        };

        options.TokenValidationParameters = new TokenValidationParameters
        {
            // Accept only unexpired tokens signed by this API for the intended client.
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,

            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,

            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwtKey))
        };
    });

// OIDC sign-in is optional. If enabled, require the complete provider configuration
// instead of starting with a partially configured external authentication handler.
var externalAuthority = builder.Configuration["ExternalAuth:Authority"];
var externalClientId = builder.Configuration["ExternalAuth:ClientId"];
var externalClientSecret = builder.Configuration["ExternalAuth:ClientSecret"];
var hasExternalProviderSetting = new[] { externalAuthority, externalClientId, externalClientSecret }
    .Any(value => !string.IsNullOrWhiteSpace(value));
if (hasExternalProviderSetting)
{
    if (string.IsNullOrWhiteSpace(externalAuthority) ||
        string.IsNullOrWhiteSpace(externalClientId) ||
        string.IsNullOrWhiteSpace(externalClientSecret))
    {
        throw new InvalidOperationException("ExternalAuth:Authority, ExternalAuth:ClientId, and ExternalAuth:ClientSecret must all be configured together.");
    }

    builder.Services.AddAuthentication()
        .AddCookie(IdentityConstants.ExternalScheme, options =>
        {
            // This short-lived cookie carries the provider identity between callback
            // steps. It is separate from the API's bearer access/refresh credentials.
            options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        })
        .AddOpenIdConnect("external", options =>
        {
            options.Authority = externalAuthority;
            options.ClientId = externalClientId;
            options.ClientSecret = externalClientSecret;
            options.SignInScheme = IdentityConstants.ExternalScheme;
            options.CallbackPath = "/signin-oidc";
            options.ResponseType = "code";
            // Re-entering external sign-in must prove fresh authentication instead
            // of silently upgrading an old provider cookie into a recent app session.
            options.Prompt = "login";
            options.MaxAge = TimeSpan.Zero;
            options.Events.OnTokenValidated = context =>
            {
                // prompt/max_age request reauthentication; this check verifies the
                // provider actually supplied a sufficiently recent signed auth_time.
                if (!RecentAuthenticationService.HasRecentProviderAuthentication(context.Principal,
                        context.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow()))
                    context.Fail("The provider did not prove recent authentication.");
                return Task.CompletedTask;
            };
            // Keep provider access/refresh tokens out of the temporary sign-in cookie.
            options.SaveTokens = false;
            options.GetClaimsFromUserInfoEndpoint = true;
            options.Scope.Add("email");
            options.Scope.Add("profile");
            options.TokenValidationParameters.NameClaimType = "name";
        });
}

builder.Services.AddAuthorization(options =>
{
    // Policies answer "is this user allowed?" RecentAuthenticationFilter separately
    // answers "did this user prove their identity recently enough for this change?"
    options.AddPolicy("CanManageGames", policy =>
    {
        policy.RequireRole("admin");
    });

    options.AddPolicy("CanManageUsers", policy =>
    {
        policy.RequireRole("admin");
    });

    options.AddPolicy("CanManageGenres", policy =>
    {
        policy.RequireRole("admin");
    });
});

// Standardize API errors and describe bearer-token requirements in Swagger.
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Game Store API",
        Version = "v1",
        Description = "Browse games and genres, manage the catalog, and administer user accounts."
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter a JWT access token."
    });

    options.OperationFilter<AuthorizeCheckOperationFilter>();
});

// Identity confirmation/reset tokens, OIDC state, and reauthentication proofs use
// Data Protection keys. Persist/share the key ring in production so restarts or
// another API instance do not make still-valid proofs unreadable.
var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath))
        .SetApplicationName("GameStore.Api");
}
else if (!builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException("Production must configure DataProtection:KeysPath to a persistent, protected shared key ring.");
}

var app = builder.Build();

// Middleware order matters: resolve trusted proxy IPs first, then register cache
// prevention before later middleware can return early with a 401, 429, or error.
app.UseForwardedHeaders();
app.UseMiddleware<AuthResponseCacheMiddleware>();
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Authentication establishes who the caller is; authorization then checks access.
// Endpoint filters, including recent-auth checks, run when the selected handler runs.
app.UseAuthentication();
app.UseAuthorization();

app.MapGamesEndpoints();
app.MapGenresEndpoints();
app.MapAuthEndpoints();
app.MapOperationalHealth();

// Apply schema changes and create the first validated admin before serving requests.
await app.MigrateDbAsync();
app.Run();

public partial class Program { }
