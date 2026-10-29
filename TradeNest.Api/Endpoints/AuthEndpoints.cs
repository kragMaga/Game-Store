using System.Security.Claims;
using TradeNest.Api.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using TradeNest.Api.Dtos;
using TradeNest.Api.Services;

namespace TradeNest.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        // Group validation enforces DTO annotations before handlers can dereference
        // missing fields. Keep service validation too for non-HTTP callers.
        var auth = app.MapGroup("/auth").WithParameterValidation();
        auth.MapPost("/register", async (RegisterDto request, AuthService authService) =>
{
            var result = await authService.RegisterAsync(request);

            return result switch
            {
                RegistrationError.None => Results.Ok(new { message = "User registered successfully." }),
                RegistrationError.ConfirmationEmailUnavailable => Results.Ok(new
                {
                    message = "Account created, but the confirmation email could not be sent. Request another confirmation email using POST /auth/email/resend-confirmation."
                }),
                RegistrationError.UsernameRequired => Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Username is required",
                    detail: "Provide a username."),
                RegistrationError.UsernameInvalid => Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid username",
                    detail: "Use 5–20 ASCII letters, digits, periods, underscores, or hyphens."),
                RegistrationError.PasswordRequired => Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Password is required",
                    detail: "Provide a password containing at least 8 characters."),
                RegistrationError.PasswordTooShort => Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Password is too short",
                    detail: "Password must contain at least 8 characters."),
                RegistrationError.PasswordPolicyFailed => Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Password does not meet policy",
                    detail: "Use at least 8 characters with uppercase, lowercase, and a number. Locally blocked passwords and confirmed breach matches are rejected."),
                RegistrationError.UsernameAlreadyTaken => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Username is already taken",
                    detail: "Choose a different username."),
                RegistrationError.EmailRequired => Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Email is required",
                    detail: "Provide a valid email address for account confirmation and recovery."),
                RegistrationError.EmailInvalid => Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid email address"),
                RegistrationError.EmailAlreadyTaken => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Email address is already in use"),
                _ => Results.Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "Registration failed",
                    detail: "The registration could not be completed.")
            };
})
    .WithTags("Authentication")
    .WithSummary("Register an account")
    .WithDescription("Registers an account. Username conflicts return 409 ProblemDetails.")
    .RequireRateLimiting("auth");

        auth.MapPost("/login", async (
            LoginDto request,
            AuthService authService,
            HttpContext httpContext,
            IConfiguration configuration,
            IAntiforgery antiforgery) =>
{
    if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Username and password are required",
            detail: "Provide both a username and a password.");
    }

    var result = await authService.LoginAsync(request);

    if (result is null)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status401Unauthorized,
            title: "Authentication failed",
            detail: "The username or password is incorrect.");
    }

    return result.Tokens is not null
        ? AuthCookieSupport.CreateTokenResponse(httpContext, result.Tokens, configuration, antiforgery)
        : Results.Accepted(value: new { requiresTwoFactor = true, challengeToken = result.MfaChallengeToken });
})
    .WithTags("Authentication")
    .WithSummary("Log in")
    .WithDescription("Valid credentials return tokens, or a short-lived MFA challenge when authenticator MFA is enabled.")
    .RequireRateLimiting("auth");

        auth.MapPost("/login/mfa", async (
            MfaLoginDto request,
            AuthService authService,
            HttpContext httpContext,
            IConfiguration configuration,
            IAntiforgery antiforgery) =>
{
    var tokens = await authService.CompleteMfaLoginAsync(request.ChallengeToken, request.Code);
    return tokens is null
        ? Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "MFA verification failed")
        : AuthCookieSupport.CreateTokenResponse(httpContext, tokens, configuration, antiforgery);
})
    .WithTags("Authentication")
    .WithSummary("Complete two-factor login")
    .RequireRateLimiting("auth");

        auth.MapGet("/external/login", (IConfiguration configuration) =>
{
    if (string.IsNullOrWhiteSpace(configuration["ExternalAuth:Authority"]))
    {
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "External sign-in is not configured");
    }

    var properties = new AuthenticationProperties { RedirectUri = "/auth/external/complete" };
    return Results.Challenge(properties, ["external"]);
})
    .WithTags("Authentication")
    .WithSummary("Start OpenID Connect sign-in")
    .RequireRateLimiting("auth");

        auth.MapGet("/external/complete", async (
            SignInManager<User> signInManager,
            UserManager<User> userManager,
            AuthService authService,
            HttpContext httpContext,
            IConfiguration configuration,
            IAntiforgery antiforgery) =>
        {
            var externalInfo = await signInManager.GetExternalLoginInfoAsync();
            if (externalInfo is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "External sign-in failed");
            }

            var user = await userManager.FindByLoginAsync(externalInfo.LoginProvider, externalInfo.ProviderKey);
            if (user is null)
            {
                var email = externalInfo.Principal.FindFirstValue(ClaimTypes.Email)
                    ?? externalInfo.Principal.FindFirstValue("email");
                var verifiedEmail = externalInfo.Principal.FindFirstValue("email_verified");
                if (string.IsNullOrWhiteSpace(email) || !string.Equals(verifiedEmail, "true", StringComparison.OrdinalIgnoreCase))
                {
                    await httpContext.SignOutAsync(IdentityConstants.ExternalScheme);
                    return Results.Problem(
                        statusCode: StatusCodes.Status403Forbidden,
                        title: "Verified email required",
                        detail: "The identity provider must assert a verified email address.");
                }

                if (await userManager.FindByEmailAsync(email) is not null)
                {
                    await httpContext.SignOutAsync(IdentityConstants.ExternalScheme);
                    return Results.Problem(
                        statusCode: StatusCodes.Status409Conflict,
                        title: "Account already exists",
                        detail: "Sign in to the existing account and link the external provider from account settings.");
                }

                user = new User
                {
                    UserName = $"ext{Guid.NewGuid():N}"[..19],
                    Email = email,
                    NormalizedEmail = userManager.NormalizeEmail(email),
                    EmailConfirmed = true,

                    SecurityStamp = Guid.NewGuid().ToString(),
                    LockoutEnabled = true
                };

                var createResult = await userManager.CreateAsync(user);
                if (!createResult.Succeeded)
                {
                    user = await userManager.FindByLoginAsync(externalInfo.LoginProvider, externalInfo.ProviderKey);
                    if (user is null)
                    {
                        await httpContext.SignOutAsync(IdentityConstants.ExternalScheme);
                        return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "External account could not be linked");
                    }
                }
                else
                {
                    var linkResult = await userManager.AddLoginAsync(user, externalInfo);
                    if (!linkResult.Succeeded)
                    {
                        await userManager.DeleteAsync(user);
                        await httpContext.SignOutAsync(IdentityConstants.ExternalScheme);
                        return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "External account could not be linked");
                    }
                }
            }

            await httpContext.SignOutAsync(IdentityConstants.ExternalScheme);
            var login = await authService.LoginWithExternalIdentityAsync(user);
            if (login is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Account is unavailable");
            }

            return login.Tokens is not null
                ? AuthCookieSupport.CreateTokenResponse(httpContext, login.Tokens, configuration, antiforgery)
                : Results.Accepted(value: new { requiresTwoFactor = true, challengeToken = login.MfaChallengeToken });
        })
            .WithTags("Authentication")
            .WithSummary("Complete OpenID Connect sign-in")
            .RequireRateLimiting("auth");

        auth.MapGet("/external/link", async (IConfiguration configuration, ClaimsPrincipal currentUser,
            HttpContext httpContext, RecentAuthenticationService recentAuthentication) =>
        {
            if (string.IsNullOrWhiteSpace(configuration["ExternalAuth:Authority"]))
            {
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "External sign-in is not configured");
            }

            var userId = currentUser.FindFirstValue(ClaimTypes.NameIdentifier);
            var properties = new AuthenticationProperties { RedirectUri = "/auth/external/link/complete" };
            properties.Items["linkUserId"] = userId ?? string.Empty;
            // Carry the original recent-auth proof through protected OIDC state.
            // The callback has no bearer header, so it must recheck this proof itself.
            properties.Items["linkReauthentication"] = await recentAuthentication.CreateExternalLinkProofAsync(httpContext);
            return Results.Challenge(properties, ["external"]);
        })
            .WithTags("Authentication")
            .WithSummary("Start linking an external identity")
            .AddEndpointFilter<RecentAuthenticationFilter>()
            .RequireAuthorization()
            .RequireRateLimiting("auth");

        auth.MapGet("/external/link/complete", async (
            SignInManager<User> signInManager,
            UserManager<User> userManager,
            AuthService authService,
            HttpContext httpContext,
            RecentAuthenticationService recentAuthentication) =>
        {
            var externalInfo = await signInManager.GetExternalLoginInfoAsync();
            string? proof = null;
            externalInfo?.AuthenticationProperties?.Items.TryGetValue("linkReauthentication", out proof);
            var targetUserId = await recentAuthentication.ValidateExternalLinkProofAsync(proof);
            if (externalInfo is null || targetUserId is not int userId)
            {
                await httpContext.SignOutAsync(IdentityConstants.ExternalScheme);
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "External identity linking failed");
            }

            var user = await userManager.FindByIdAsync(userId.ToString());
            if (user is null)
            {
                await httpContext.SignOutAsync(IdentityConstants.ExternalScheme);
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Account is unavailable");
            }

            var result = await authService.LinkExternalIdentityAsync(
                user,
                new UserLoginInfo(externalInfo.LoginProvider, externalInfo.ProviderKey, externalInfo.ProviderDisplayName));
            await httpContext.SignOutAsync(IdentityConstants.ExternalScheme);
            return result.Succeeded
                ? Results.NoContent()
                : Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "External identity is already linked");
        })
            .WithTags("Authentication")
            .WithSummary("Complete linking an external identity")
            .RequireRateLimiting("auth");

        auth.MapPost("/reauthenticate", async (ReauthenticateDto request, ClaimsPrincipal user,
            RecentAuthenticationService recentAuthentication) =>
        {
            var proof = await recentAuthentication.ReauthenticateAsync(user, request.CurrentPassword, request.Code);
            return proof is null
                ? Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Reauthentication failed")
                : Results.Ok(new { reauthenticationToken = proof, expiresIn = (int)RecentAuthenticationService.Window.TotalSeconds });
        })
            .WithTags("Authentication")
            .WithSummary("Reauthenticate for sensitive changes")
            .RequireAuthorization()
            .RequireRateLimiting("auth");

        auth.MapPost("/refresh", async (
            AuthService authService,
            HttpContext httpContext,
            IConfiguration configuration,
            IAntiforgery antiforgery) =>
{
    if (!await AuthCookieSupport.ValidateCookieRequestAsync(httpContext, configuration, antiforgery))
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid anti-forgery token");
    }

    var refreshToken = await AuthCookieSupport.ReadRefreshTokenAsync(httpContext, configuration);
    var result = await authService.RefreshAsync(refreshToken);

    return result.Error == RefreshTokenError.None
        ? AuthCookieSupport.CreateTokenResponse(httpContext, result.Tokens!, configuration, antiforgery)
        : Results.Problem(
            statusCode: StatusCodes.Status401Unauthorized,
            title: "Invalid refresh token",
            detail: "The refresh token is invalid, expired, revoked, or has already been used. Log in again.");
})
    .WithTags("Authentication")
    .WithSummary("Refresh authentication tokens")
    .WithDescription("Rotates a valid refresh token and returns a new access-token and refresh-token pair.")
    .RequireRateLimiting("auth");

        auth.MapPost("/confirm-email", async (ConfirmEmailDto request, AuthService authService) =>
{
    var confirmed = await authService.ConfirmEmailAsync(request.Email, request.Token);
    return confirmed
        ? Results.NoContent()
        : Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid or expired confirmation token");
})
    .WithTags("Authentication")
    .WithSummary("Confirm an email address")
    .RequireRateLimiting("auth");

        auth.MapPost("/email/resend-confirmation", async (ForgotPasswordDto request, AuthService authService) =>
{
    await authService.ResendEmailConfirmationAsync(request.Email);
    return Results.Accepted(value: new { message = "If confirmation is pending, instructions will be sent to the account email." });
})
    .WithTags("Authentication")
    .WithSummary("Resend an email confirmation")
    .RequireRateLimiting("auth");

        auth.MapPost("/email/confirm-change", async (ConfirmEmailChangeDto request, AuthService authService) =>
        {
            var confirmed = await authService.ConfirmEmailChangeAsync(request.UserId, request.Email, request.Token);
            return confirmed
                ? Results.NoContent()
                : Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Email change could not be confirmed",
                    detail: "The link is invalid or expired, or the email address is no longer available.");
        })
            .WithTags("Authentication")
            .WithSummary("Confirm a new email address")
            .RequireRateLimiting("auth");

        auth.MapPost("/password/forgot", async (ForgotPasswordDto request, AuthService authService) =>
{
    await authService.RequestPasswordResetAsync(request.Email);
    return Results.Accepted(value: new { message = "If an eligible account exists, password-reset instructions will be sent." });
})
    .WithTags("Authentication")
    .WithSummary("Request a password reset")
    .WithDescription("Always returns the same response to prevent account enumeration.")
    .RequireRateLimiting("auth");

        auth.MapPost("/password/reset", async (ResetPasswordDto request, AuthService authService) =>
{
    var reset = await authService.ResetPasswordAsync(request.Email, request.Token, request.NewPassword);
    return reset
        ? Results.NoContent()
        : Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Password reset could not be completed", detail: "The reset token is invalid, expired, or the password does not meet policy.");
})
    .WithTags("Authentication")
    .WithSummary("Reset a password")
    .RequireRateLimiting("auth");

        auth.MapPost("/logout", async (
            AuthService authService,
            HttpContext httpContext,
            IConfiguration configuration,
            IAntiforgery antiforgery) =>
{
    if (!await AuthCookieSupport.ValidateCookieRequestAsync(httpContext, configuration, antiforgery))
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid anti-forgery token");
    }

    var refreshToken = await AuthCookieSupport.ReadRefreshTokenAsync(httpContext, configuration);
    await authService.RevokeRefreshTokenAsync(refreshToken);
    if (AuthCookieSupport.CookieModeEnabled(configuration))
    {
        AuthCookieSupport.ClearRefreshCookie(httpContext);
    }

    return Results.NoContent();
})
    .WithTags("Authentication")
    .WithSummary("Revoke a refresh token")
    .WithDescription("Revokes the supplied refresh token. Safe to call repeatedly; the token is not returned or logged.")
    .RequireRateLimiting("auth");

        auth.MapPut("/me/username", async (
            UpdateUsernameDto request,
            ClaimsPrincipal currentUser,
            UserService userService) =>
        {
            if (!int.TryParse(currentUser.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication required");
            }

            var result = await userService.UpdateUsernameAsync(userId, request.Username);
            return result switch
            {
                AccountOperationResult.Success => Results.NoContent(),
                AccountOperationResult.InvalidUsername => Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid username",
                    detail: "Use 5–20 letters, numbers, periods, underscores, or hyphens."),
                AccountOperationResult.UsernameAlreadyInUse => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Username is already taken",
                    detail: "Choose a different username."),
                _ => Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication required")
            };
        })
            .WithTags("Authentication")
            .WithSummary("Update the current username")
            .AddEndpointFilter<RecentAuthenticationFilter>()
            .WithDescription("Usernames are case-insensitive. Updating a username invalidates existing access and refresh tokens.")
            .RequireAuthorization();

        auth.MapPut("/me/email", async (
            SetEmailDto request,
            ClaimsPrincipal currentUser,
            AuthService authService) =>
        {
            if (!int.TryParse(currentUser.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication required");
            }

            var result = await authService.UpdateEmailAsync(userId, request.Email);
            return result switch
            {
                AccountOperationResult.Success => Results.Accepted(value: new { message = "Check the new email address for a confirmation link. Your current email and sign-in remain active until you confirm." }),
                AccountOperationResult.EmailDeliveryFailed => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Confirmation email could not be sent", detail: "Your current email and sign-in are unchanged. Try the email change again later."),
                AccountOperationResult.InvalidEmail => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid email address"),
                AccountOperationResult.EmailAlreadyInUse => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Email address is already in use"),
                _ => Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication required")
            };
        })
            .WithTags("Authentication")
            .WithSummary("Add or update the current email address")
            .AddEndpointFilter<RecentAuthenticationFilter>()
            .WithDescription("Sends confirmation to the new address. The current email and sessions remain active until confirmation succeeds.")
            .RequireAuthorization()
            .RequireRateLimiting("auth");

        auth.MapPost("/me/mfa/setup", async (
            MfaSetupDto request,
            ClaimsPrincipal currentUser,
            AuthService authService) =>
        {
            if (!int.TryParse(currentUser.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication required");
            }

            var (sharedKey, error) = await authService.BeginMfaSetupAsync(userId, request.CurrentPassword);
            if (sharedKey is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "MFA setup could not start", detail: error);
            }

            var username = currentUser.Identity?.Name ?? "account";
            var label = Uri.EscapeDataString($"TradeNest:{username}");
            var issuer = Uri.EscapeDataString("TradeNest");
            var secret = Uri.EscapeDataString(sharedKey);
            var provisioningUri = $"otpauth://totp/{label}?secret={secret}&issuer={issuer}&digits=6&period=30";
            return Results.Ok(new { sharedKey, provisioningUri });
        })
            .WithTags("Authentication")
            .WithSummary("Start authenticator-app setup")
            .WithDescription("Requires the current password. Confirm the generated authenticator code to enable MFA.")
            .RequireAuthorization()
            .RequireRateLimiting("auth");

        auth.MapPost("/me/mfa/enable", async (
            MfaVerifyDto request,
            ClaimsPrincipal currentUser,
            AuthService authService) =>
        {
            if (!int.TryParse(currentUser.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication required");
            }

            var recoveryCodes = await authService.EnableMfaAsync(userId, request.Code);
            return recoveryCodes is null
                ? Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid authenticator code")
                : Results.Ok(new { recoveryCodes });
        })
            .WithTags("Authentication")
            .WithSummary("Enable authenticator-app MFA")
            .AddEndpointFilter<RecentAuthenticationFilter>()
            .WithDescription("Returns recovery codes once. Store them securely; enabling MFA signs out active sessions.")
            .RequireAuthorization()
            .RequireRateLimiting("auth");

        auth.MapPost("/me/mfa/disable", async (
            MfaDisableDto request,
            ClaimsPrincipal currentUser,
            AuthService authService) =>
        {
            if (!int.TryParse(currentUser.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication required");
            }

            var result = await authService.DisableMfaAsync(userId, request.CurrentPassword, request.Code);
            return result switch
            {
                MfaDisableResult.Success => Results.NoContent(),
                MfaDisableResult.InvalidCredentials => Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "MFA could not be disabled", detail: "The password or authenticator/recovery code was incorrect."),
                _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError,
                    title: "MFA could not be disabled", detail: "The security change could not be completed. Try again later.")
            };
        })
            .WithTags("Authentication")
            .WithSummary("Disable authenticator-app MFA")
            .WithDescription("Requires the current password and an authenticator or recovery code. Active sessions are revoked.")
            .RequireAuthorization()
            .RequireRateLimiting("auth");

        auth.MapPut("/me/password", async (
            ChangePasswordDto request,
            ClaimsPrincipal currentUser,
            UserService userService) =>
        {
            if (!int.TryParse(currentUser.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication required");
            }

            var result = await userService.ChangePasswordAsync(userId, request.CurrentPassword, request.NewPassword);
            return result switch
            {
                AccountOperationResult.Success => Results.NoContent(),
                AccountOperationResult.CurrentPasswordIncorrect => Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Current password is incorrect"),
                AccountOperationResult.PasswordPolicyFailed => Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Password does not meet policy",
                    detail: "Use at least 8 characters with uppercase, lowercase, and a number. Locally blocked passwords and confirmed breach matches are rejected."),
                _ => Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication required")
            };
        })
            .WithTags("Authentication")
            .WithSummary("Change the current password")
            .WithDescription("Requires the current password. A successful change signs out all active sessions.")
            .RequireAuthorization()
            .RequireRateLimiting("auth");

        auth.MapGet("/me", (ClaimsPrincipal user) =>
{
    return Results.Ok(new
    {
        Username = user.Identity?.Name,
        IsAuthenticated = user.Identity?.IsAuthenticated
    });
})
    .WithTags("Authentication")
    .WithSummary("Get the current user")
    .RequireAuthorization();

    }
}
