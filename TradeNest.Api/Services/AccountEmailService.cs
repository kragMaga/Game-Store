using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using TradeNest.Api.Entities;
using Microsoft.AspNetCore.Identity;

namespace TradeNest.Api.Services;

// Builds confirmation messages and queues delivery. Identity remains responsible
// for token creation/validation; the outbox remains responsible for SMTP retries.
public sealed class AccountEmailService(
    UserManager<User> userManager,
    IAccountEmailSender emailSender,
    IConfiguration configuration,
    ILogger<AccountEmailService> logger)
{
    internal async Task<bool> TrySendEmailConfirmationAsync(User user)
    {
        try
        {
            await SendEmailConfirmationAsync(user);
            return true;
        }
        catch (Exception exception) when (emailSender is not OutboxAccountEmailSender)
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
        var instructions = CreateConfirmationInstructions(user.Email, encodedToken);
        await emailSender.SendAsync(
            user.Email,
            "Welcome to TradeNest — confirm your account",
            $"Your TradeNest registration was successful.\n{instructions}\n\nIf you did not create this account, ignore this message.");
    }

    internal string CreateConfirmationInstructions(string email, string encodedToken, int? userId = null)
    {
        var pageUrl = configuration["Email:ConfirmationPageUrl"];
        // Keep Identity's full, expiring token: a short hand-made code would need
        // its own secure storage, attempt limits, expiry, and replay protection.
        // Clients submit this token in JSON, never in a URL query or a log entry.
        if (string.IsNullOrWhiteSpace(pageUrl))
        {
            var endpoint = userId.HasValue ? "/auth/email/confirm-change" : "/auth/confirm-email";
            var identity = userId.HasValue ? $"userId: {userId.Value}, email: {email}" : $"email: {email}";
            return $"Your email confirmation token is:\n\n{encodedToken}\n\nSubmit it as the token field with {identity} in a JSON POST to {endpoint}.";
        }
        if (!ConfirmationPageOptions.IsValid(pageUrl))
        {
            throw new InvalidOperationException("Email:ConfirmationPageUrl must be an absolute HTTPS frontend URL without credentials or a fragment.");
        }
        var pageUri = new Uri(pageUrl!, UriKind.Absolute);

        // Fragments stay in the browser rather than entering server access logs or Referer headers.
        var link = new UriBuilder(pageUri)
        {
            Fragment = $"email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(encodedToken)}" +
                (userId.HasValue ? $"&purpose=email-change&userId={userId.Value}" : "")
        }.Uri.AbsoluteUri;
        return $"Click the link below to confirm your email address:\n\n{link}";
    }

}
