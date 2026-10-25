using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using GameStore.Api.Data;
using GameStore.Api.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GameStore.Api.Services;

// A long-lived session may browse normally, but sensitive changes need a recent
// completed sign-in or a short-lived proof issued after checking credentials again.
public sealed class RecentAuthenticationService(
    GameStoreContext db, UserManager<User> users, IDataProtectionProvider protection,
    TimeProvider clock, IConfiguration configuration)
{
    // Clients send the additional proof here, alongside their usual bearer token.
    public const string HeaderName = "X-Reauthentication-Token";
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    // A separate protection purpose prevents other protected tokens from being
    // mistaken for reauthentication proofs, even when they share the same key ring.
    private readonly IDataProtector _protector = protection.CreateProtector("GameStore.Reauthentication.v1");
    // Bind the proof to one account, one login/device session, and one security version.
    private sealed record Proof(int UserId, string SessionId, string Stamp, DateTimeOffset AuthenticatedAt);

    public static bool HasRecentProviderAuthentication(ClaimsPrincipal? principal, DateTimeOffset now)
    {
        // A newly issued ID token can still represent an old provider session.
        // Check the signed authentication time, not the token's issue time.
        if (!long.TryParse(principal?.FindFirstValue("auth_time"), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var authenticatedAt)) return false;
        var seconds = now.ToUnixTimeSeconds();
        // Allow at most one minute of provider clock skew into the future.
        return authenticatedAt > seconds - (long)Window.TotalSeconds && authenticatedAt <= seconds + 60;
    }

    // Called by the endpoint filter before allowing a sensitive handler to execute.
    public async Task<bool> IsRecentAsync(HttpContext context) => await GetCurrentProofAsync(context) is not null;

    public async Task<string?> CreateExternalLinkProofAsync(HttpContext context)
    {
        // Browser redirects do not preserve the API's bearer/proof headers. Carry
        // the existing proof through OIDC state without restarting its five-minute age.
        var proof = await GetCurrentProofAsync(context);
        return proof is null ? null : _protector.Protect(JsonSerializer.Serialize(proof));
    }

    public async Task<int?> ValidateExternalLinkProofAsync(string? value)
    {
        // Recheck expiry, stamp, and session on return from the provider. An account
        // change or logout during the redirect must cancel the linking attempt.
        var proof = ReadProof(value);
        return proof is not null && await IsCurrentAsync(proof) ? proof.UserId : null;
    }

    private async Task<Proof?> GetCurrentProofAsync(HttpContext context)
    {
        // These claims come from the authenticated bearer token, not request-body IDs.
        if (!int.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return null;
        var sessionId = context.User.FindFirstValue("session_id");
        var stamp = context.User.FindFirstValue("security_stamp");
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(stamp)) return null;

        // A proof from another account/session must never authorize this request.
        var supplied = ReadProof(context.Request.Headers[HeaderName].ToString());
        if (supplied is not null && supplied.UserId == userId && supplied.SessionId == sessionId &&
            supplied.Stamp == stamp && await IsCurrentAsync(supplied)) return supplied;

        // Refreshing an access token must not make an old sign-in "recent" again.
        // The persisted session creation time records the original completed login.
        var session = await db.AuthSessions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == sessionId && item.UserId == userId);
        if (session is null) return null;
        var original = new Proof(userId, sessionId, stamp, new DateTimeOffset(session.CreatedAtUtc, TimeSpan.Zero));
        return await IsCurrentAsync(original) ? original : null;
    }

    // Reject expired or future-dated local proofs; refreshing a JWT cannot extend them.
    private bool IsWithinWindow(Proof proof) => proof.AuthenticatedAt <= clock.GetUtcNow() &&
        clock.GetUtcNow() - proof.AuthenticatedAt < Window;

    private async Task<bool> IsCurrentAsync(Proof proof)
    {
        // Encryption prevents tampering, but the account/session can change after
        // issuance. Database checks make revocation and stamp changes effective now.
        if (!IsWithinWindow(proof)) return false;
        var now = clock.GetUtcNow().UtcDateTime;
        var session = await db.AuthSessions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == proof.SessionId && item.UserId == proof.UserId);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == proof.UserId);
        return session is not null && user is not null && session.RevokedAtUtc is null &&
            session.AbsoluteExpiresAtUtc > now &&
            session.LastRefreshedAtUtc.AddMinutes(configuration.GetValue("Jwt:RefreshTokenIdleMinutes", 30)) > now &&
            !(user.LockoutEnd > clock.GetUtcNow()) && user.SecurityStamp == proof.Stamp;
    }

    private Proof? ReadProof(string? value)
    {
        // Treat an unreadable or tampered proof as absent. Never log the supplied token.
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return JsonSerializer.Deserialize<Proof>(_protector.Unprotect(value)); }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException)
        { return null; }
    }

    public async Task<string?> ReauthenticateAsync(ClaimsPrincipal principal, string password, string? code)
    {
        // Reauthenticate the signed-in account only; a caller cannot select another
        // user ID. Include recovery-code consumption in the security transaction.
        if (!int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return null;
        var sessionId = principal.FindFirstValue("session_id");
        var stamp = principal.FindFirstValue("security_stamp");
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(stamp)) return null;
        await using var transaction = await SecurityTransaction.BeginAsync(db);
        var user = await users.FindByIdAsync(userId.ToString());
        var now = clock.GetUtcNow();
        // "now" here checks whether this existing session is still usable. The new
        // proof is only issued below after password and any required MFA checks pass.
        if (user is null || !await IsCurrentAsync(new Proof(userId, sessionId, stamp, now))) return null;

        var valid = await users.CheckPasswordAsync(user, password);
        if (valid && await users.GetTwoFactorEnabledAsync(user))
        {
            // Password alone must not bypass enabled MFA. A valid recovery code is
            // an alternative to an authenticator code and is consumed once on success.
            var normalized = code?.Replace(" ", "").Replace("-", "");
            valid = !string.IsNullOrWhiteSpace(normalized) && await users.VerifyTwoFactorTokenAsync(
                user, TokenOptions.DefaultAuthenticatorProvider, normalized);
            if (!valid && !string.IsNullOrWhiteSpace(code))
                valid = (await users.RedeemTwoFactorRecoveryCodeAsync(user, code.Trim())).Succeeded;
        }
        if (!valid)
        {
            // Persist failed attempts even though no proof is issued. This supplements
            // IP limiting when an attacker changes addresses or guesses MFA codes.
            SecurityTransaction.RequireSucceeded(await users.AccessFailedAsync(user), "ReauthenticationFailureCount");
            await transaction.CommitAsync();
            return null;
        }
        SecurityTransaction.RequireSucceeded(await users.ResetAccessFailedCountAsync(user), "ResetAccessFailedCount");
        // The proof is encrypted/authenticated, short-lived, and bound to this exact
        // account, session, and stamp. It cannot be reused as an access token.
        var value = _protector.Protect(JsonSerializer.Serialize(new Proof(userId, sessionId, user.SecurityStamp!, now)));
        await transaction.CommitAsync();
        return value;
    }
}
