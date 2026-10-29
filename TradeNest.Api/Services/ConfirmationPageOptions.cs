namespace TradeNest.Api.Services;

// A trusted configured origin prevents poisoned request Host headers from becoming
// account-confirmation links. An API-only deployment can omit the URL entirely;
// in that case emails contain a token for submission to the confirmation API.
public sealed class ConfirmationPageOptions
{
    public string? ConfirmationPageUrl { get; set; }
    public static bool IsValid(string? value) => string.IsNullOrWhiteSpace(value) ||
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);
}
