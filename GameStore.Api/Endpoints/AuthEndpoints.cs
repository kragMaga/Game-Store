using System.Security.Claims;
using GameStore.Api.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using GameStore.Api.Dtos;
using GameStore.Api.Services;

namespace GameStore.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/auth/register", async (RegisterDto request, AuthService authService) =>
{
            var result = await authService.RegisterAsync(request);

            return result switch
            {
                RegistrationError.None => Results.Ok(new { message = "User registered successfully." }),
                RegistrationError.UsernameRequired => Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Username is required",
                    detail: "Provide a username."),
                RegistrationError.UsernameLengthInvalid => Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid username",
                    detail: "Username must be between 5 and 20 characters."),
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
                    detail: "Use at least 8 characters, including an uppercase letter, a lowercase letter, and a number."),
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
    .WithDescription("Registers a customer account. Username conflicts return 409 ProblemDetails.")
    .RequireRateLimiting("auth");

        app.MapPost("/auth/login", async (
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

        app.MapPost("/auth/login/mfa", async (
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

        app.MapGet("/auth/external/login", (IConfiguration configuration) =>
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

        app.MapGet("/auth/external/complete", async (
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
                    Role = "customer",
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

        app.MapGet("/auth/external/link", (IConfiguration configuration, ClaimsPrincipal currentUser) =>
        {
            if (string.IsNullOrWhiteSpace(configuration["ExternalAuth:Authority"]))
            {
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "External sign-in is not configured");
            }

            var userId = currentUser.FindFirstValue(ClaimTypes.NameIdentifier);
            var properties = new AuthenticationProperties { RedirectUri = "/auth/external/link/complete" };
            properties.Items["linkUserId"] = userId ?? string.Empty;
            return Results.Challenge(properties, ["external"]);
        })
            .WithTags("Authentication")
            .WithSummary("Start linking an external identity")
            .RequireAuthorization()
            .RequireRateLimiting("auth");

        app.MapGet("/auth/external/link/complete", async (
            SignInManager<User> signInManager,
            UserManager<User> userManager,
            AuthService authService,
            HttpContext httpContext) =>
        {
            var externalInfo = await signInManager.GetExternalLoginInfoAsync();
            string? targetUserId = null;
            externalInfo?.AuthenticationProperties?.Items.TryGetValue("linkUserId", out targetUserId);
            if (externalInfo is null || !int.TryParse(targetUserId, out var userId))
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
            .WithSummary("Complete linking an external identity");

        app.MapPost("/auth/refresh", async (
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

        app.MapPost("/auth/confirm-email", async (ConfirmEmailDto request, AuthService authService) =>
{
    var confirmed = await authService.ConfirmEmailAsync(request.Email, request.Token);
    return confirmed
        ? Results.NoContent()
        : Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid or expired confirmation token");
})
    .WithTags("Authentication")
    .WithSummary("Confirm an email address")
    .RequireRateLimiting("auth");

        app.MapPost("/auth/email/resend-confirmation", async (ForgotPasswordDto request, AuthService authService) =>
{
    await authService.ResendEmailConfirmationAsync(request.Email);
    return Results.Accepted(value: new { message = "If confirmation is pending, instructions will be sent to the account email." });
})
    .WithTags("Authentication")
    .WithSummary("Resend an email confirmation")
    .RequireRateLimiting("auth");

        app.MapPost("/auth/password/forgot", async (ForgotPasswordDto request, AuthService authService) =>
{
    await authService.RequestPasswordResetAsync(request.Email);
    return Results.Accepted(value: new { message = "If an eligible account exists, password-reset instructions will be sent." });
})
    .WithTags("Authentication")
    .WithSummary("Request a password reset")
    .WithDescription("Always returns the same response to prevent account enumeration.")
    .RequireRateLimiting("auth");

        app.MapPost("/auth/password/reset", async (ResetPasswordDto request, AuthService authService) =>
{
    var reset = await authService.ResetPasswordAsync(request.Email, request.Token, request.NewPassword);
    return reset
        ? Results.NoContent()
        : Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Password reset could not be completed", detail: "The reset token is invalid, expired, or the password does not meet policy.");
})
    .WithTags("Authentication")
    .WithSummary("Reset a password")
    .RequireRateLimiting("auth");

        app.MapPost("/auth/logout", async (
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

        app.MapPut("/auth/me/username", async (
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
            .WithDescription("Usernames are case-insensitive. Updating a username invalidates existing access and refresh tokens.")
            .RequireAuthorization();

        app.MapPut("/auth/me/email", async (
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
                AccountOperationResult.Success => Results.Accepted(value: new { message = "Confirm the new email address before using recovery features." }),
                AccountOperationResult.InvalidEmail => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid email address"),
                AccountOperationResult.EmailAlreadyInUse => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Email address is already in use"),
                _ => Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication required")
            };
        })
            .WithTags("Authentication")
            .WithSummary("Add or update the current email address")
            .WithDescription("The new email must be confirmed; active sessions are revoked for safety.")
            .RequireAuthorization()
            .RequireRateLimiting("auth");

        app.MapPost("/auth/me/mfa/setup", async (
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
            var label = Uri.EscapeDataString($"Game Store:{username}");
            var issuer = Uri.EscapeDataString("Game Store");
            var secret = Uri.EscapeDataString(sharedKey);
            var provisioningUri = $"otpauth://totp/{label}?secret={secret}&issuer={issuer}&digits=6&period=30";
            return Results.Ok(new { sharedKey, provisioningUri });
        })
            .WithTags("Authentication")
            .WithSummary("Start authenticator-app setup")
            .WithDescription("Requires the current password. Confirm the generated authenticator code to enable MFA.")
            .RequireAuthorization()
            .RequireRateLimiting("auth");

        app.MapPost("/auth/me/mfa/enable", async (
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
            .WithDescription("Returns recovery codes once. Store them securely; enabling MFA signs out active sessions.")
            .RequireAuthorization()
            .RequireRateLimiting("auth");

        app.MapPost("/auth/me/mfa/disable", async (
            MfaDisableDto request,
            ClaimsPrincipal currentUser,
            AuthService authService) =>
        {
            if (!int.TryParse(currentUser.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication required");
            }

            var disabled = await authService.DisableMfaAsync(userId, request.CurrentPassword, request.Code);
            return disabled
                ? Results.NoContent()
                : Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "MFA could not be disabled", detail: "The password or authenticator/recovery code was incorrect.");
        })
            .WithTags("Authentication")
            .WithSummary("Disable authenticator-app MFA")
            .WithDescription("Requires the current password and an authenticator or recovery code. Active sessions are revoked.")
            .RequireAuthorization()
            .RequireRateLimiting("auth");

        app.MapPut("/auth/me/password", async (
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
                    detail: "Use at least 8 characters, including an uppercase letter, a lowercase letter, and a number."),
                _ => Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication required")
            };
        })
            .WithTags("Authentication")
            .WithSummary("Change the current password")
            .WithDescription("Requires the current password. A successful change signs out all active sessions.")
            .RequireAuthorization()
            .RequireRateLimiting("auth");

        app.MapGet("/auth/me", (ClaimsPrincipal user) =>
{
    return Results.Ok(new
    {
        Username = user.Identity?.Name,
        IsAuthenticated = user.Identity?.IsAuthenticated,
        Roles = user.Claims
            .Where(claim => claim.Type == ClaimTypes.Role)
            .Select(claim => claim.Value)
    });
})
    .WithTags("Authentication")
    .WithSummary("Get the current user")
    .RequireAuthorization();

        app.MapGet("/auth/users", async (UserService userService) =>
{
    var users = await userService.GetUsersAsync();

    return Results.Ok(users);
})
    .WithTags("User administration")
    .WithSummary("List users")
    .RequireAuthorization("CanManageUsers");


 app.MapPut("/auth/users/{id}/role", async (
    int id,
    UpdateUserRoleDto request,
    UserService userService) =>
{
    var error = await userService.UpdateRoleAsync(
        id,
        request);

    return error switch
{
    UserOperationError.None => Results.NoContent(),

    UserOperationError.InvalidRole => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "Invalid role",
        detail: "Role must be either admin or customer."),

    UserOperationError.UserNotFound => Results.Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "User not found",
        detail: "The requested user does not exist."),

    UserOperationError.LastAdmin => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "Operation not allowed",
        detail: "The last admin cannot be demoted."),

    _ => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "Operation failed",
        detail: "The operation could not be completed.")
};
})
    .WithTags("User administration")
    .WithSummary("Change a user's role")
    .WithDescription("Assigns the admin or customer role. The last administrator cannot be demoted.")
    .RequireAuthorization("CanManageUsers");


app.MapDelete("/auth/users/{id}", async (int id, ClaimsPrincipal currentUser, UserService userService) =>
{
    var error = await userService.DeleteUserAsync(id, currentUser);

    return error switch
{
    UserOperationError.None => Results.NoContent(),

    UserOperationError.UserNotFound => Results.Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "User not found",
        detail: "The requested user does not exist."),

    UserOperationError.CannotDeleteSelf => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "Operation not allowed",
        detail: "You cannot delete your own account."),

    UserOperationError.LastAdmin => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "Operation not allowed",
        detail: "The last admin cannot be deleted."),

    _ => Results.Problem(
        statusCode: StatusCodes.Status500InternalServerError,
        title: "Operation failed",
        detail: "The operation could not be completed.")
};
})
    .WithTags("User administration")
    .WithSummary("Delete a user")
    .WithDescription("Deletes a user unless that would delete the current account or the last administrator.")
    .RequireAuthorization("CanManageUsers");


    }
}