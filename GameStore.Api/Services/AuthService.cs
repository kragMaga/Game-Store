using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using GameStore.Api.Data;
using GameStore.Api.Dtos;
using GameStore.Api.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace GameStore.Api.Services;

public class AuthService(
    GameStoreContext dbContext,
    UserManager<User> userManager,
    IAccountEmailSender emailSender,
    IConfiguration configuration,
    ILogger<AuthService> logger)
{
    public async Task<RegistrationError> RegisterAsync(RegisterDto request)
    {
        if (string.IsNullOrWhiteSpace(request.Username))
        {
            return RegistrationError.UsernameRequired;
        }

        var username = request.Username.Trim();

        if (username.Length is < 5 or > 20 ||
            !username.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
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

        logger.LogInformation("Registered new customer account with user ID {UserId}", user.Id);
        return await TrySendEmailConfirmationAsync(user)
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
        var result = await userManager.AddLoginAsync(user, externalLogin);
        if (result.Succeeded)
        {
            await userManager.UpdateSecurityStampAsync(user);
            await RevokeAllSessionsAsync(user.Id);
            logger.LogInformation("Linked external provider {Provider} to user ID {UserId}", externalLogin.LoginProvider, user.Id);
        }

        return result;
    }

    public async Task<AuthTokensDto?> CompleteMfaLoginAsync(string challengeToken, string code)
    {
        ClaimsPrincipal principal;
        try
        {
            var handler = new JwtSecurityTokenHandler();
            principal = handler.ValidateToken(challengeToken, GetJwtValidationParameters(), out _);
        }
        catch (Exception exception) when (exception is SecurityTokenException or ArgumentException)
        {
            return null;
        }

        var challengeId = principal.FindFirstValue("mfa_challenge_id");
        if (principal.FindFirstValue("mfa_challenge") != "true" ||
            string.IsNullOrWhiteSpace(challengeId) ||
            !int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return null;
        }

        await dbContext.Database.OpenConnectionAsync();
        var sqliteConnection = (SqliteConnection)dbContext.Database.GetDbConnection();
        await using var sqliteTransaction = sqliteConnection.BeginTransaction(deferred: false);
        await using var transaction = await dbContext.Database.UseTransactionAsync(sqliteTransaction)
            ?? throw new InvalidOperationException("Could not start the MFA transaction.");

        try
        {
            var user = await userManager.FindByIdAsync(userId.ToString());
            var storedChallengeHash = user is null
                ? null
                : await userManager.GetAuthenticationTokenAsync(user, "GameStore", "MfaLoginChallenge");
            if (user is null ||
                user.LockoutEnd > DateTimeOffset.UtcNow ||
                (!user.EmailConfirmed && !string.IsNullOrWhiteSpace(user.Email)) ||
                !await userManager.GetTwoFactorEnabledAsync(user) ||
                !string.Equals(storedChallengeHash, HashRefreshToken(challengeId), StringComparison.Ordinal))
            {
                return null;
            }

            var normalizedCode = code.Replace(" ", string.Empty).Replace("-", string.Empty);
            var validCode = await userManager.VerifyTwoFactorTokenAsync(
                user,
                TokenOptions.DefaultAuthenticatorProvider,
                normalizedCode);
            if (!validCode)
            {
                var recoveryResult = await userManager.RedeemTwoFactorRecoveryCodeAsync(user, code.Trim());
                if (!recoveryResult.Succeeded)
                {
                    await userManager.AccessFailedAsync(user);
                    await transaction.CommitAsync();
                    return null;
                }
            }

            var removedChallengeCount = await dbContext.Set<IdentityUserToken<int>>()
                .Where(token => token.UserId == user.Id &&
                                token.LoginProvider == "GameStore" &&
                                token.Name == "MfaLoginChallenge" &&
                                token.Value == storedChallengeHash)
                .ExecuteDeleteAsync();
            if (removedChallengeCount != 1)
            {
                await transaction.RollbackAsync();
                return null;
            }

            await userManager.ResetAccessFailedCountAsync(user);
            await transaction.CommitAsync();
            logger.LogInformation("Two-factor authentication succeeded for user ID {UserId}", user.Id);
            return await IssueTokensAsync(user);
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    public async Task<(string? SharedKey, string? Error)> BeginMfaSetupAsync(int userId, string currentPassword)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null || !await userManager.CheckPasswordAsync(user, currentPassword))
        {
            return (null, "Current password is incorrect.");
        }

        if (await userManager.GetTwoFactorEnabledAsync(user))
        {
            return (null, "Multi-factor authentication is already enabled.");
        }

        await userManager.ResetAuthenticatorKeyAsync(user);
        return (await userManager.GetAuthenticatorKeyAsync(user), null);
    }

    public async Task<IReadOnlyList<string>?> EnableMfaAsync(int userId, string code)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return null;
        }

        var normalizedCode = code.Replace(" ", string.Empty).Replace("-", string.Empty);
        if (!await userManager.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, normalizedCode))
        {
            return null;
        }

        var enabled = await userManager.SetTwoFactorEnabledAsync(user, true);
        if (!enabled.Succeeded)
        {
            return null;
        }

        var recoveryCodes = await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        await userManager.UpdateSecurityStampAsync(user);
        await RevokeAllSessionsAsync(user.Id);
        return recoveryCodes?.ToArray() ?? [];
    }

    public async Task<MfaDisableResult> DisableMfaAsync(int userId, string currentPassword, string code)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        try
        {
            var user = await userManager.FindByIdAsync(userId.ToString());
            if (user is null || !await userManager.CheckPasswordAsync(user, currentPassword) ||
                !await userManager.GetTwoFactorEnabledAsync(user))
            {
                return MfaDisableResult.InvalidCredentials;
            }

            var normalizedCode = code.Replace(" ", string.Empty).Replace("-", string.Empty);
            var validCode = await userManager.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, normalizedCode);
            if (!validCode)
            {
                var recoveryResult = await userManager.RedeemTwoFactorRecoveryCodeAsync(user, code.Trim());
                if (!recoveryResult.Succeeded)
                {
                    return MfaDisableResult.InvalidCredentials;
                }
            }

            if (!CheckMfaDisableStep(await userManager.SetTwoFactorEnabledAsync(user, false), "DisableTwoFactor", userId))
                return MfaDisableResult.Failed;
            if (!CheckMfaDisableStep(await userManager.ResetAuthenticatorKeyAsync(user), "ResetAuthenticatorKey", userId))
                return MfaDisableResult.Failed;
            if (!CheckMfaDisableStep(await userManager.UpdateSecurityStampAsync(user), "UpdateSecurityStamp", userId))
                return MfaDisableResult.Failed;

            await RevokeAllSessionsAsync(user.Id);
            // Revocation throws on database failure. Verify persisted state before committing.
            if (await dbContext.AuthSessions.AnyAsync(session => session.UserId == userId && session.RevokedAtUtc == null) ||
                await dbContext.RefreshTokens.AnyAsync(token => token.UserId == userId && token.RevokedAtUtc == null))
                return MfaDisableResult.Failed;

            await transaction.CommitAsync();
            logger.LogInformation("Disabled MFA and revoked sessions for user ID {UserId}", userId);
            return MfaDisableResult.Success;
        }
        finally
        {
            // A failed operation may have mutated tracked Identity objects before rollback.
            dbContext.ChangeTracker.Clear();
        }
    }

    private bool CheckMfaDisableStep(IdentityResult result, string step, int userId)
    {
        if (result.Succeeded) return true;
        logger.LogWarning("MFA disable failed at {Step} for user ID {UserId}. Identity error codes: {IdentityErrorCodes}",
            step, userId, string.Join(", ", result.Errors.Select(error => error.Code).Distinct()));
        return false;
    }

    private async Task<AuthTokensDto> IssueTokensAsync(User user)
    {
        var refreshTokenValue = CreateRefreshTokenValue();
        var now = DateTime.UtcNow;
        var sessionId = Guid.NewGuid().ToString("N");
        var absoluteExpiresAtUtc = now.AddDays(configuration.GetValue("Jwt:MaxSessionDays", 7));
        var session = new AuthSession
        {
            Id = sessionId,
            UserId = user.Id,
            User = user,
            CreatedAtUtc = now,
            LastRefreshedAtUtc = now,
            AbsoluteExpiresAtUtc = absoluteExpiresAtUtc
        };
        var refreshToken = CreateRefreshToken(user, refreshTokenValue, sessionId, now, absoluteExpiresAtUtc);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        dbContext.AuthSessions.Add(session);
        await dbContext.SaveChangesAsync();
        dbContext.RefreshTokens.Add(refreshToken);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        logger.LogInformation("User ID {UserId} authenticated successfully", user.Id);
        return CreateAuthTokens(user, refreshTokenValue, now, sessionId);
    }

    private async Task<AuthLoginResult> CompletePrimaryLoginAsync(User user)
    {
        if (await userManager.GetTwoFactorEnabledAsync(user))
        {
            return new AuthLoginResult(null, await CreateMfaChallengeTokenAsync(user));
        }

        return new AuthLoginResult(await IssueTokensAsync(user), null);
    }

    public async Task<(RefreshTokenError Error, AuthTokensDto? Tokens)> RefreshAsync(string? refreshTokenValue)
    {
        if (string.IsNullOrWhiteSpace(refreshTokenValue))
        {
            return (RefreshTokenError.Invalid, null);
        }

        var presentedHash = HashRefreshToken(refreshTokenValue);
        await dbContext.Database.OpenConnectionAsync();
        var sqliteConnection = (SqliteConnection)dbContext.Database.GetDbConnection();
        await using var sqliteTransaction = sqliteConnection.BeginTransaction(deferred: false);
        await using var transaction = await dbContext.Database.UseTransactionAsync(sqliteTransaction)
            ?? throw new InvalidOperationException("Could not start the refresh transaction.");

        try
        {
        var currentToken = await dbContext.RefreshTokens
            .SingleOrDefaultAsync(token => token.TokenHash == presentedHash);

        if (currentToken is null)
        {
            logger.LogWarning("Refresh token request rejected because the token is invalid");
            return (RefreshTokenError.Invalid, null);
        }

        var session = await dbContext.AuthSessions
            .Include(candidate => candidate.User)
            .SingleOrDefaultAsync(candidate => candidate.Id == currentToken.SessionId);
        if (session is null)
        {
            logger.LogError("Refresh token references missing session {SessionId}", currentToken.SessionId);
            return (RefreshTokenError.Invalid, null);
        }

        var now = DateTime.UtcNow;
        if (currentToken.RevokedAtUtc.HasValue)
        {
            if (currentToken.ReplacedByTokenHash is not null)
            {
                await dbContext.AuthSessions
                    .Where(candidate => candidate.Id == session.Id && candidate.RevokedAtUtc == null)
                    .ExecuteUpdateAsync(update => update.SetProperty(candidate => candidate.RevokedAtUtc, now));
                await dbContext.RefreshTokens
                    .Where(token => token.SessionId == currentToken.SessionId && token.RevokedAtUtc == null)
                    .ExecuteUpdateAsync(update => update.SetProperty(token => token.RevokedAtUtc, now));
                await transaction.CommitAsync();
                logger.LogWarning("Refresh-token reuse detected for session {SessionId}; that session was revoked", currentToken.SessionId);
            }
            else
            {
                logger.LogWarning("Revoked refresh token rejected for session {SessionId}", currentToken.SessionId);
            }

            return (RefreshTokenError.Invalid, null);
        }

        var idleExpiry = session.LastRefreshedAtUtc.AddMinutes(
            configuration.GetValue("Jwt:RefreshTokenIdleMinutes", 30));
        if (session.RevokedAtUtc.HasValue ||
            currentToken.ExpiresAtUtc <= now ||
            idleExpiry <= now ||
            session.AbsoluteExpiresAtUtc <= now)
        {
            currentToken.RevokedAtUtc = now;
            if (idleExpiry <= now || session.AbsoluteExpiresAtUtc <= now)
            {
                session.RevokedAtUtc = now;
            }
            await dbContext.SaveChangesAsync();
            await transaction.CommitAsync();
            logger.LogWarning("Expired refresh token rejected for session {SessionId}", currentToken.SessionId);
            return (RefreshTokenError.Invalid, null);
        }

        var replacementValue = CreateRefreshTokenValue();
        var replacement = CreateRefreshToken(
            session.User,
            replacementValue,
            session.Id,
            now,
            session.AbsoluteExpiresAtUtc);
        var updatedCount = await dbContext.RefreshTokens
            .Where(token => token.Id == currentToken.Id &&
                            token.RevokedAtUtc == null &&
                            token.ExpiresAtUtc > now &&
                            token.AbsoluteExpiresAtUtc > now)
            .ExecuteUpdateAsync(update => update
                .SetProperty(token => token.RevokedAtUtc, now)
                .SetProperty(token => token.ReplacedByTokenHash, replacement.TokenHash));

        if (updatedCount != 1)
        {
            logger.LogWarning("Concurrent refresh attempt rejected for session {SessionId}", currentToken.SessionId);
            return (RefreshTokenError.Invalid, null);
        }

        var sessionUpdatedCount = await dbContext.AuthSessions
            .Where(candidate => candidate.Id == session.Id &&
                                candidate.RevokedAtUtc == null &&
                                candidate.LastRefreshedAtUtc == session.LastRefreshedAtUtc &&
                                candidate.AbsoluteExpiresAtUtc > now)
            .ExecuteUpdateAsync(update => update.SetProperty(candidate => candidate.LastRefreshedAtUtc, now));

        if (sessionUpdatedCount != 1)
        {
            await transaction.RollbackAsync();
            logger.LogWarning("Concurrent refresh attempt rejected for session {SessionId}", session.Id);
            return (RefreshTokenError.Invalid, null);
        }

        dbContext.RefreshTokens.Add(replacement);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        logger.LogInformation("Refresh token rotated for session {SessionId}", session.Id);
        return (RefreshTokenError.None, CreateAuthTokens(session.User, replacementValue, now, session.Id));
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
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

        return await TrySendEmailConfirmationAsync(user);
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
            var link = CreateConfirmationUrl(email, encodedToken, user.Id);
            await emailSender.SendAsync(email, "Confirm your new Game Store email address",
                $"Click the link below to confirm your new email address. Your current address stays active until you confirm:\n\n{link}\n\nIf you did not request this change, ignore this message.");
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
            await RevokeAllSessionsAsync(userId);
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

            await userManager.ResetAccessFailedCountAsync(user);
            await userManager.SetLockoutEndDateAsync(user, null);
            await RevokeAllSessionsAsync(user.Id);
            logger.LogInformation("Password reset completed and active sessions revoked for user ID {UserId}", user.Id);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private async Task<bool> TrySendEmailConfirmationAsync(User user)
    {
        try
        {
            await SendEmailConfirmationAsync(user);
            return true;
        }
        catch (Exception exception)
        {
            // Provider exception messages may include addresses or message contents.
            logger.LogWarning("Confirmation email could not be delivered for user ID {UserId}. Failure type: {FailureType}",
                user.Id, exception.GetType().Name);
            return false;
        }
    }

    private async Task SendEmailConfirmationAsync(User user)
    {
        if (string.IsNullOrWhiteSpace(user.Email))
        {
            return;
        }

        var token = await userManager.GenerateEmailConfirmationTokenAsync(user);
        var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        var confirmationUrl = CreateConfirmationUrl(user.Email, encodedToken);
        await emailSender.SendAsync(
            user.Email,
            "Welcome to Game Store — confirm your account",
            $"Your Game Store registration was successful. Click the link below to confirm your email address:\n\n{confirmationUrl}\n\nIf you did not create this account, ignore this message.");
    }

    private string CreateConfirmationUrl(string email, string encodedToken, int? userId = null)
    {
        var pageUrl = configuration["Email:ConfirmationPageUrl"];
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri) ||
            pageUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(pageUri.UserInfo) || !string.IsNullOrEmpty(pageUri.Fragment))
        {
            throw new InvalidOperationException("Email:ConfirmationPageUrl must be an absolute HTTPS frontend URL without credentials or a fragment.");
        }

        // Fragments stay in the browser rather than entering server access logs or Referer headers.
        return new UriBuilder(pageUri)
        {
            Fragment = $"email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(encodedToken)}" +
                (userId.HasValue ? $"&purpose=email-change&userId={userId.Value}" : "")
        }.Uri.AbsoluteUri;
    }

    private async Task RevokeAllSessionsAsync(int userId)
    {
        var now = DateTime.UtcNow;
        await dbContext.AuthSessions
            .Where(session => session.UserId == userId && session.RevokedAtUtc == null)
            .ExecuteUpdateAsync(update => update.SetProperty(session => session.RevokedAtUtc, now));
        await dbContext.RefreshTokens
            .Where(refreshToken => refreshToken.UserId == userId && refreshToken.RevokedAtUtc == null)
            .ExecuteUpdateAsync(update => update.SetProperty(refreshToken => refreshToken.RevokedAtUtc, now));
    }

    public async Task RevokeRefreshTokenAsync(string? refreshTokenValue)
    {
        if (string.IsNullOrWhiteSpace(refreshTokenValue))
        {
            return;
        }

        var presentedHash = HashRefreshToken(refreshTokenValue);
        var now = DateTime.UtcNow;
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        var token = await dbContext.RefreshTokens
            .SingleOrDefaultAsync(candidate => candidate.TokenHash == presentedHash);

        if (token is null)
        {
            return;
        }

        var revokedCount = await dbContext.AuthSessions
            .Where(session => session.Id == token.SessionId && session.RevokedAtUtc == null)
            .ExecuteUpdateAsync(update => update.SetProperty(session => session.RevokedAtUtc, now));
        await dbContext.RefreshTokens
            .Where(candidate => candidate.SessionId == token.SessionId && candidate.RevokedAtUtc == null)
            .ExecuteUpdateAsync(update => update.SetProperty(candidate => candidate.RevokedAtUtc, now));
        await transaction.CommitAsync();

        if (revokedCount > 0)
        {
            logger.LogInformation("Refresh token revoked");
        }
    }

    private AuthTokensDto CreateAuthTokens(User user, string refreshToken, DateTime now, string sessionId)
    {
        var accessTokenMinutes = configuration.GetValue("Jwt:AccessTokenMinutes", 15);
        var expiresAt = now.AddMinutes(accessTokenMinutes);
        var accessToken = CreateAccessToken(user, expiresAt, sessionId);
        return new AuthTokensDto(accessToken, refreshToken, "Bearer", accessTokenMinutes * 60);
    }

    private string CreateAccessToken(User user, DateTime expiresAt, string sessionId)
    {
        var jwtKey = configuration["Jwt:Key"]
            ?? throw new InvalidOperationException("JWT key is missing.");

        var jwtIssuer = configuration["Jwt:Issuer"]
            ?? throw new InvalidOperationException(
                "JWT issuer is missing.");

        var jwtAudience = configuration["Jwt:Audience"]
            ?? throw new InvalidOperationException(
                "JWT audience is missing.");

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.UserName ?? string.Empty),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim("security_stamp", user.SecurityStamp ?? string.Empty),
            new Claim("session_id", sessionId)
        };

        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(jwtKey));

        var credentials = new SigningCredentials(
            key,
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: jwtIssuer,
            audience: jwtAudience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler()
            .WriteToken(token);
    }

    private async Task<string> CreateMfaChallengeTokenAsync(User user)
    {
        var challengeId = CreateRefreshTokenValue();
        var storeResult = await userManager.SetAuthenticationTokenAsync(
            user,
            "GameStore",
            "MfaLoginChallenge",
            HashRefreshToken(challengeId));
        if (!storeResult.Succeeded)
        {
            throw new InvalidOperationException("Could not persist the MFA challenge.");
        }

        var jwtKey = configuration["Jwt:Key"]
            ?? throw new InvalidOperationException("JWT key is missing.");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: configuration["Jwt:Issuer"],
            audience: configuration["Jwt:Audience"],
            claims:
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim("mfa_challenge", "true"),
                new Claim("mfa_challenge_id", challengeId)
            ],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private TokenValidationParameters GetJwtValidationParameters()
    {
        var jwtKey = configuration["Jwt:Key"]
            ?? throw new InvalidOperationException("JWT key is missing.");
        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = configuration["Jwt:Issuer"],
            ValidAudience = configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    }

    private RefreshToken CreateRefreshToken(
        User user,
        string tokenValue,
        string sessionId,
        DateTime createdAtUtc,
        DateTime absoluteExpiresAtUtc)
    {
        var idleExpiresAtUtc = createdAtUtc.AddMinutes(
            configuration.GetValue("Jwt:RefreshTokenIdleMinutes", 30));

        return new RefreshToken
        {
            UserId = user.Id,
            TokenHash = HashRefreshToken(tokenValue),
            SessionId = sessionId,
            CreatedAtUtc = createdAtUtc,
            ExpiresAtUtc = idleExpiresAtUtc < absoluteExpiresAtUtc
                ? idleExpiresAtUtc
                : absoluteExpiresAtUtc,
            AbsoluteExpiresAtUtc = absoluteExpiresAtUtc
        };
    }

    private static string CreateRefreshTokenValue()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string HashRefreshToken(string tokenValue)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tokenValue)));
    }
}
