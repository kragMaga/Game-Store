using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using GameStore.Api.Data;
using GameStore.Api.Dtos;
using GameStore.Api.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GameStore.Api.Services;

// Coordinates account registration, login, and recovery. Endpoint-facing methods
// stay stable while session, MFA, and email responsibilities live in focused services.
public class AuthService(
    GameStoreContext dbContext,
    UserManager<User> userManager,
    IAccountEmailSender emailSender,
    AuthSessionService sessions,
    MfaService mfa,
    AccountEmailService accountEmail,
    ILogger<AuthService> logger)
{
    // Keep the endpoint contract intact while routing each operation to its owner.
    public Task<AuthTokensDto?> CompleteMfaLoginAsync(string challengeToken, string code) =>
        mfa.CompleteMfaLoginAsync(challengeToken, code);

    public Task<(string? SharedKey, string? Error)> BeginMfaSetupAsync(int userId, string currentPassword) =>
        mfa.BeginMfaSetupAsync(userId, currentPassword);

    public Task<IReadOnlyList<string>?> EnableMfaAsync(int userId, string code) => mfa.EnableMfaAsync(userId, code);

    public Task<MfaDisableResult> DisableMfaAsync(int userId, string currentPassword, string code) =>
        mfa.DisableMfaAsync(userId, currentPassword, code);

    public Task<(RefreshTokenError Error, AuthTokensDto? Tokens)> RefreshAsync(string? refreshTokenValue) =>
        sessions.RefreshAsync(refreshTokenValue);

    public Task RevokeRefreshTokenAsync(string? refreshTokenValue) => sessions.RevokeRefreshTokenAsync(refreshTokenValue);

    public async Task<RegistrationError> RegisterAsync(RegisterDto request)
    {
        if (string.IsNullOrWhiteSpace(request.Username))
        {
            return RegistrationError.UsernameRequired;
        }

        var username = request.Username.Trim();

        if (!UsernameRules.IsValid(username))
        {
            return RegistrationError.UsernameInvalid;
        }

        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return RegistrationError.EmailRequired;
        }

        var email = request.Email.Trim();
        if (!new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email))
        {
            return RegistrationError.EmailInvalid;
        }

        if (string.IsNullOrWhiteSpace(request.Password))
        {
            return RegistrationError.PasswordRequired;
        }

        if (request.Password.Length < 8)
        {
            return RegistrationError.PasswordTooShort;
        }

        var normalizedUsername = userManager.NormalizeName(username);
        var userAlreadyExists = await dbContext.Users
            .AnyAsync(user => user.NormalizedUserName == normalizedUsername);

        if (userAlreadyExists)
        {
            logger.LogWarning(
                "Registration rejected because the requested username is already in use");
            return RegistrationError.UsernameAlreadyTaken;
        }

        var normalizedEmail = userManager.NormalizeEmail(email);
        if (await dbContext.Users.AnyAsync(user => user.NormalizedEmail == normalizedEmail))
        {
            return RegistrationError.EmailAlreadyTaken;
        }

        var user = new User
        {
            UserName = username,
            NormalizedUserName = normalizedUsername,
            Email = email,
            NormalizedEmail = normalizedEmail,
            Role = "customer",
            SecurityStamp = Guid.NewGuid().ToString(),
            LockoutEnabled = true
        };

        IdentityResult createResult;
        // With the production outbox sender, account creation and the queued message
        // commit together. A crash between them cannot leave a successful registration
        // without its delivery intent. SMTP itself happens later outside this transaction.
        await using var registrationTransaction = await SecurityTransaction.BeginAsync(dbContext);
        try
        {
            createResult = await userManager.CreateAsync(user, request.Password);
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 or 1555 })
        {
            dbContext.Entry(user).State = EntityState.Detached;
            logger.LogWarning("Registration rejected by a database uniqueness constraint");
            // A primary-key collision does not identify a username or email conflict.
            if (exception.InnerException is SqliteException { SqliteExtendedErrorCode: 1555 })
            {
                return RegistrationError.RegistrationFailed;
            }

            if (await dbContext.Users.AnyAsync(candidate => candidate.NormalizedUserName == normalizedUsername))
            {
                return RegistrationError.UsernameAlreadyTaken;
            }

            if (await dbContext.Users.AnyAsync(candidate => candidate.NormalizedEmail == normalizedEmail))
            {
                return RegistrationError.EmailAlreadyTaken;
            }

            return RegistrationError.RegistrationFailed;
        }
        if (!createResult.Succeeded)
        {
            logger.LogWarning("Identity registration failed with error codes: {IdentityErrorCodes}",
                string.Join(", ", createResult.Errors.Select(error => error.Code).Distinct()));

            if (createResult.Errors.Any(error => error.Code == "DuplicateEmail"))
            {
                return RegistrationError.EmailAlreadyTaken;
            }

            if (createResult.Errors.Any(error => error.Code == "DuplicateUserName"))
            {
                logger.LogWarning("Registration rejected because the username conflicts with an existing account");
                return RegistrationError.UsernameAlreadyTaken;
            }

            if (createResult.Errors.Any(error => error.Code.StartsWith("Password", StringComparison.Ordinal)))
            {
                return RegistrationError.PasswordPolicyFailed;
            }

            return RegistrationError.RegistrationFailed;
        }

        var confirmationQueued = await accountEmail.TrySendEmailConfirmationAsync(user);
        await registrationTransaction.CommitAsync();
        logger.LogInformation("Registered new customer account with user ID {UserId}", user.Id);
        return confirmationQueued
            ? RegistrationError.None
            : RegistrationError.ConfirmationEmailUnavailable;
    }

    public async Task<AuthLoginResult?> LoginAsync(LoginDto request)
    {
        var username = request.Username.Trim();
        var user = await userManager.FindByNameAsync(username);

        if (user is null ||
            (!string.IsNullOrWhiteSpace(user.Email) && !user.EmailConfirmed) ||
            await userManager.IsLockedOutAsync(user))
        {
            logger.LogWarning("Login failed because the supplied credentials were not valid");
            return null;
        }

        if (!await userManager.CheckPasswordAsync(user, request.Password))
        {
            if (userManager.SupportsUserLockout)
            {
                await userManager.AccessFailedAsync(user);
            }

            logger.LogWarning("Login failed because the supplied credentials were not valid");
            return null;
        }

        await userManager.ResetAccessFailedCountAsync(user);
        return await CompletePrimaryLoginAsync(user);
    }

    public async Task<AuthLoginResult?> LoginWithExternalIdentityAsync(User user)
    {
        if (await userManager.IsLockedOutAsync(user))
        {
            return null;
        }

        return await CompletePrimaryLoginAsync(user);
    }

    public async Task<IdentityResult> LinkExternalIdentityAsync(User user, UserLoginInfo externalLogin)
    {
        // Linking a provider must not succeed unless stamp rotation and session
        // revocation also succeed; otherwise an incomplete security change persists.
        await using var transaction = await SecurityTransaction.BeginAsync(dbContext);
        user = await userManager.FindByIdAsync(user.Id.ToString())
            ?? throw new InvalidOperationException("The account no longer exists.");
        var result = await userManager.AddLoginAsync(user, externalLogin);
        if (result.Succeeded)
        {
            SecurityTransaction.RequireSucceeded(await userManager.UpdateSecurityStampAsync(user), "UpdateSecurityStamp");
            await sessions.RevokeAllSessionsAsync(user.Id);
            await transaction.CommitAsync();
            logger.LogInformation("Linked external provider {Provider} to user ID {UserId}", externalLogin.LoginProvider, user.Id);
        }

        return result;
    }

    private async Task<AuthLoginResult> CompletePrimaryLoginAsync(User user)
    {
        if (await userManager.GetTwoFactorEnabledAsync(user))
        {
            return new AuthLoginResult(null, await sessions.CreateMfaChallengeTokenAsync(user));
        }

        return new AuthLoginResult(await sessions.IssueTokensAsync(user), null);
    }

    public async Task<bool> ResendEmailConfirmationAsync(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        var user = await userManager.FindByEmailAsync(email.Trim());
        if (user is null || user.EmailConfirmed || string.IsNullOrWhiteSpace(user.Email))
        {
            return false;
        }

        return await accountEmail.TrySendEmailConfirmationAsync(user);
    }

    public async Task<AccountOperationResult> UpdateEmailAsync(int userId, string? requestedEmail)
    {
        var email = requestedEmail?.Trim();
        if (string.IsNullOrWhiteSpace(email) || email.Length > 256 ||
            !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email))
        {
            return AccountOperationResult.InvalidEmail;
        }

        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return AccountOperationResult.UserNotFound;
        }

        var normalizedEmail = userManager.NormalizeEmail(email);
        if (await dbContext.Users.AnyAsync(candidate => candidate.Id != userId && candidate.NormalizedEmail == normalizedEmail))
        {
            return AccountOperationResult.EmailAlreadyInUse;
        }

        // Keep the current confirmed address and sessions until the recipient proves ownership.
        try
        {
            var token = await userManager.GenerateChangeEmailTokenAsync(user, email);
            var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
            var instructions = accountEmail.CreateConfirmationInstructions(email, encodedToken, user.Id);
            await emailSender.SendAsync(email, "Confirm your new Game Store email address",
                $"Your current address stays active until you confirm.\n{instructions}\n\nIf you did not request this change, ignore this message.");
            return AccountOperationResult.Success;
        }
        catch (Exception exception)
        {
            logger.LogWarning("Email change confirmation delivery failed for user ID {UserId}. Failure type: {FailureType}",
                user.Id, exception.GetType().Name);
            return AccountOperationResult.EmailDeliveryFailed;
        }
    }

    public async Task<bool> ConfirmEmailChangeAsync(int userId, string? email, string? token)
    {
        email = email?.Trim();
        if (userId <= 0 || string.IsNullOrWhiteSpace(email) || email.Length > 256 ||
            !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email) || string.IsNullOrWhiteSpace(token))
            return false;

        string decodedToken;
        try { decodedToken = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(token)); }
        catch (FormatException) { return false; }

        await dbContext.Database.OpenConnectionAsync();
        var connection = (SqliteConnection)dbContext.Database.GetDbConnection();
        await using var sqliteTransaction = connection.BeginTransaction(deferred: false);
        await using var transaction = await dbContext.Database.UseTransactionAsync(sqliteTransaction)
            ?? throw new InvalidOperationException("Could not start the email change transaction.");
        try
        {
            var user = await userManager.FindByIdAsync(userId.ToString());
            if (user is null) return false;
            var normalizedEmail = userManager.NormalizeEmail(email);
            if (await dbContext.Users.AnyAsync(candidate => candidate.Id != userId && candidate.NormalizedEmail == normalizedEmail))
                return false;

            var result = await userManager.ChangeEmailAsync(user, email, decodedToken);
            if (!result.Succeeded) return false;
            if (!(await userManager.UpdateSecurityStampAsync(user)).Succeeded) return false;
            await sessions.RevokeAllSessionsAsync(userId);
            await transaction.CommitAsync();
            logger.LogInformation("Confirmed email change and revoked sessions for user ID {UserId}", userId);
            return true;
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 or 1555 })
        {
            logger.LogWarning("Email change rejected by a database uniqueness constraint for user ID {UserId}", userId);
            return false;
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    public async Task<bool> ConfirmEmailAsync(string? email, string? token)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var user = await userManager.FindByEmailAsync(email.Trim());
        if (user is null)
        {
            return false;
        }

        try
        {
            var decodedToken = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(token));
            var result = await userManager.ConfirmEmailAsync(user, decodedToken);
            if (!result.Succeeded)
            {
                return false;
            }

            await userManager.ResetAccessFailedCountAsync(user);
            await userManager.SetLockoutEndDateAsync(user, null);
            await userManager.UpdateSecurityStampAsync(user);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public async Task RequestPasswordResetAsync(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return;
        }

        var user = await userManager.FindByEmailAsync(email.Trim());
        if (user is null || !user.EmailConfirmed || string.IsNullOrWhiteSpace(user.Email))
        {
            return;
        }

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        try
        {
            await emailSender.SendAsync(
                user.Email,
                "Reset your Game Store password",
                $"Use this single-use password reset token in POST /auth/password/reset:\n\n{encodedToken}\n\nIf you did not request a reset, ignore this message.");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Password reset email delivery failed");
        }
    }

    public async Task<bool> ResetPasswordAsync(string? email, string? token, string? newPassword)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(newPassword))
        {
            return false;
        }

        // Password replacement, recovery lockout cleanup, and revocation must all
        // commit together. Failure must leave the old password and sessions intact.
        await using var transaction = await SecurityTransaction.BeginAsync(dbContext);
        var user = await userManager.FindByEmailAsync(email.Trim());
        if (user is null || !user.EmailConfirmed)
        {
            return false;
        }

        try
        {
            var decodedToken = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(token));
            var result = await userManager.ResetPasswordAsync(user, decodedToken, newPassword);
            if (!result.Succeeded)
            {
                return false;
            }

            SecurityTransaction.RequireSucceeded(await userManager.ResetAccessFailedCountAsync(user), "ResetAccessFailedCount");
            SecurityTransaction.RequireSucceeded(await userManager.SetLockoutEndDateAsync(user, null), "ClearLockout");
            await sessions.RevokeAllSessionsAsync(user.Id);
            await transaction.CommitAsync();
            logger.LogInformation("Password reset completed and active sessions revoked for user ID {UserId}", user.Id);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

}
