using Microsoft.AspNetCore.Identity;

namespace TradeNest.Api.Services;

public sealed class AsciiLookupNormalizer : ILookupNormalizer
{
    public string? NormalizeName(string? name) => NormalizeAscii(name);

    public string? NormalizeEmail(string? email) => email?.Trim().ToUpperInvariant();

    private static string? NormalizeAscii(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var normalized = value.Trim();
        return string.Create(normalized.Length, normalized, static (destination, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                var character = source[index];
                destination[index] = character is >= 'a' and <= 'z'
                    ? (char)(character - ('a' - 'A'))
                    : character;
            }
        });
    }
}
