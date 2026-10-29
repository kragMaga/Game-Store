using TradeNest.Api.Services;
using TradeNest.Api.Dtos;
using Microsoft.AspNetCore.Antiforgery;

namespace TradeNest.Api.Endpoints;

internal static class AuthCookieSupport
{
    private const string RefreshCookieName = LegacyCompatibility.RefreshCookie;
    private const string AntiforgeryCookieName = LegacyCompatibility.AntiforgeryCookie;

    public static bool CookieModeEnabled(IConfiguration configuration) =>
        configuration.GetValue("Auth:UseRefreshTokenCookies", false);

    public static async Task<string?> ReadRefreshTokenAsync(HttpContext context, IConfiguration configuration)
    {
        if (CookieModeEnabled(configuration))
        {
            return context.Request.Cookies[RefreshCookieName];
        }

        if (!context.Request.HasJsonContentType() || context.Request.ContentLength is null or 0)
        {
            return null;
        }

        var request = await context.Request.ReadFromJsonAsync<RefreshTokenRequestDto>();
        return request?.RefreshToken;
    }

    public static async Task<bool> ValidateCookieRequestAsync(
        HttpContext context,
        IConfiguration configuration,
        IAntiforgery antiforgery)
    {
        if (!CookieModeEnabled(configuration))
        {
            return true;
        }

        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    public static IResult CreateTokenResponse(
        HttpContext context,
        AuthTokensDto tokens,
        IConfiguration configuration,
        IAntiforgery antiforgery)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";

        if (!CookieModeEnabled(configuration))
        {
            return Results.Ok(tokens);
        }

        var idleMinutes = configuration.GetValue("Jwt:RefreshTokenIdleMinutes", 30);
        context.Response.Cookies.Append(RefreshCookieName, tokens.RefreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
            MaxAge = TimeSpan.FromMinutes(idleMinutes)
        });

        var antiforgeryTokens = antiforgery.GetAndStoreTokens(context);
        if (!string.IsNullOrWhiteSpace(antiforgeryTokens.RequestToken))
        {
            context.Response.Headers["X-CSRF-TOKEN"] = antiforgeryTokens.RequestToken;
        }

        return Results.Ok(new
        {
            tokens.AccessToken,
            tokens.TokenType,
            tokens.ExpiresIn
        });
    }

    public static void ClearRefreshCookie(HttpContext context)
    {
        context.Response.Cookies.Delete(RefreshCookieName, new CookieOptions
        {
            Secure = true,
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Path = "/"
        });
        context.Response.Cookies.Delete(AntiforgeryCookieName, new CookieOptions
        {
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/"
        });
    }

    public static void ConfigureAntiforgeryCookie(AntiforgeryOptions options)
    {
        options.HeaderName = "X-CSRF-TOKEN";
        options.Cookie.Name = AntiforgeryCookieName;
        options.Cookie.HttpOnly = false;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.Path = "/";
    }
}
