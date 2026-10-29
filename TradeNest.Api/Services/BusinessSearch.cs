namespace TradeNest.Api.Services;

// LIKE provides SQLite's normal case behaviour without applying incompatible
// .NET/SQLite Unicode uppercasing. Escape wildcard characters so references and
// user-entered names are always searched literally, never as query expressions.
internal static class BusinessSearch
{
    public static string Escape(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    public static string ContainsPattern(string value) => "%" + Escape(value.Trim()) + "%";
}
