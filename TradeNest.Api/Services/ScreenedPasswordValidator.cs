using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using TradeNest.Api.Entities;

namespace TradeNest.Api.Services;

// Identity runs this alongside its built-in password-strength checks when creating
// or replacing account passwords. Login does not call it.
public sealed class ScreenedPasswordValidator(IHttpClientFactory clients, IConfiguration configuration)
    : IPasswordValidator<User>
{
    // A small local baseline still applies if external screening is explicitly disabled.
    private static readonly HashSet<string> CommonPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "password1", "password123", "password123!", "12345678", "123456789",
        "qwerty123", "qwerty123!", "letmein123", "welcome123", "admin123", "admin123!"
    };

    public async Task<IdentityResult> ValidateAsync(UserManager<User> manager, User user, string? password)
    {
        if (string.IsNullOrEmpty(password))
            return Rejected("PasswordRequired", "Provide a password.");

        if (CommonPasswords.Contains(password) ||
            configuration.GetSection("PasswordScreening:BlockedPasswords").Get<string[]>()?
                .Contains(password, StringComparer.OrdinalIgnoreCase) == true)
            return Rejected("PasswordCompromised", "Choose a password that is not commonly used or compromised.");

        // Disabling network lookups is an explicit offline deployment choice; it
        // does not disable the built-in or organization-specific local block lists.
        if (!configuration.GetValue("PasswordScreening:CheckCompromisedPasswords", true))
            return IdentityResult.Success;

        // SHA-1 is used only for the range lookup protocol, never for password storage.
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password)));
        try
        {
            // Send only the first five hash characters, never the password/full hash.
            // Match the remaining suffix locally; padding reduces response-size clues.
            using var request = new HttpRequestMessage(HttpMethod.Get, hash[..5]);
            request.Headers.Add("Add-Padding", "true");
            using var response = await clients.CreateClient("PwnedPasswords").SendAsync(request);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split(':');
                // Malformed data makes the remote check unavailable, not a confirmed
                // breach match. Local password rules still apply in that case.
                if (parts.Length != 2 || parts[0].Length != 35 ||
                    !parts[0].All(Uri.IsHexDigit) || !long.TryParse(parts[1], out var count) || count < 0)
                    return Unavailable();

                // Padded entries have a zero count and do not indicate a breach match.
                if (count > 0 && parts[0].Equals(hash[5..], StringComparison.OrdinalIgnoreCase))
                    return Rejected("PasswordCompromised", "Choose a password that is not commonly used or compromised.");
            }
            return string.IsNullOrWhiteSpace(body) ? Unavailable() : IdentityResult.Success;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return Unavailable();
        }
    }

    // Fail open for the remote breach check so an outage cannot block account
    // creation or password updates. This is not a clean breach-check result:
    // Identity's strength rules and our local block lists are still enforced.
    private static IdentityResult Unavailable() => IdentityResult.Success;

    private static IdentityResult Rejected(string code, string description) =>
        IdentityResult.Failed(new IdentityError { Code = code, Description = description });
}
