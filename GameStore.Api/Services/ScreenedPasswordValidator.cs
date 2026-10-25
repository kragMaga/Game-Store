using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using GameStore.Api.Entities;

namespace GameStore.Api.Services;

public sealed class ScreenedPasswordValidator(IHttpClientFactory clients, IConfiguration configuration)
    : IPasswordValidator<User>
{
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

        if (!configuration.GetValue("PasswordScreening:CheckCompromisedPasswords", true))
            return IdentityResult.Success;

        // SHA-1 is used only for the range lookup protocol, never for password storage.
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password)));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, hash[..5]);
            request.Headers.Add("Add-Padding", "true");
            using var response = await clients.CreateClient("PwnedPasswords").SendAsync(request);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split(':');
                if (parts.Length != 2 || parts[0].Length != 35 ||
                    !parts[0].All(Uri.IsHexDigit) || !long.TryParse(parts[1], out var count) || count < 0)
                    return Unavailable();

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

    private static IdentityResult Unavailable() => Rejected("PasswordScreeningUnavailable",
        "Password screening is temporarily unavailable. Try again later.");

    private static IdentityResult Rejected(string code, string description) =>
        IdentityResult.Failed(new IdentityError { Code = code, Description = description });
}
