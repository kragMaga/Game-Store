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

var builder = WebApplication.CreateBuilder(args);

builder.Configuration["Auth:UseRefreshTokenCookies"] ??= (!builder.Environment.IsDevelopment()).ToString();

var connString = builder.Configuration.GetConnectionString("GameStore");
builder.Services.AddSqlite<GameStoreContext>(connString);
builder.Services.AddMemoryCache();
builder.Services.AddAntiforgery(AuthCookieSupport.ConfigureAntiforgeryCookie);
builder.Services.AddSingleton<CatalogResponseCache>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<UserService>();
builder.Services.AddIdentityCore<User>(options =>
{
    options.User.RequireUniqueEmail = true;
    options.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_";
    options.Password.RequiredLength = 8;
    options.Password.RequireDigit = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireNonAlphanumeric = false;
    options.Lockout.AllowedForNewUsers = true;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromDays(3650);
})
    .AddEntityFrameworkStores<GameStoreContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();
builder.Services.AddSingleton<ILookupNormalizer, AsciiLookupNormalizer>();
builder.Services.AddTransient<IAccountEmailSender, SmtpAccountEmailSender>();

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

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "300";
        await Results.Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "Too many requests",
            detail: "Wait before retrying.").ExecuteAsync(context.HttpContext);
    };
    options.AddPolicy("auth", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});

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
            options.SaveTokens = false;
            options.GetClaimsFromUserInfoEndpoint = true;
            options.Scope.Add("email");
            options.Scope.Add("profile");
            options.TokenValidationParameters.NameClaimType = "name";
        });
}

builder.Services.AddAuthorization(options =>
{
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

app.UseMiddleware<RequestLoggingMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGamesEndpoints();
app.MapGenresEndpoints();
app.MapAuthEndpoints();

await app.MigrateDbAsync();
app.Run();

public partial class Program { }