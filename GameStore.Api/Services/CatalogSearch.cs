using GameStore.Api.Data;
using GameStore.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace GameStore.Api.Services;

public static class CatalogSearch
{
    public const int MaxLength = 100;
    private static string Phrase(string term) => "\"" + term.Replace("\"", "\"\"") + "\"";
    private static string Pattern(string term) => "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    public static IQueryable<Game> Games(GameStoreContext db, string term)
    {
        if (string.IsNullOrWhiteSpace(term)) return db.Games;
        // Trigrams index overlapping groups of three characters, preserving contains
        // searches rather than silently changing the API to whole-word matching.
        // Quote FTS syntax and parameterize SQL so punctuation stays literal.
        if (term.EnumerateRunes().Count() >= 3)
            return db.Games.FromSqlInterpolated($"SELECT Games.* FROM Games WHERE Id IN (SELECT rowid FROM GameSearch WHERE GameSearch MATCH {Phrase(term)})");
        var pattern = Pattern(term);
        // SQLite cannot index <3-character trigram searches. Keep this bounded-input
        // fallback; LIKE avoids applying LOWER to every database row.
        return db.Games.Where(game => EF.Functions.Like(game.Name, pattern, "\\") || EF.Functions.Like(game.Genre!.Name, pattern, "\\"));
    }

    public static IQueryable<Genre> Genres(GameStoreContext db, string term)
    {
        if (string.IsNullOrWhiteSpace(term)) return db.Genres;
        if (term.EnumerateRunes().Count() >= 3)
            return db.Genres.FromSqlInterpolated($"SELECT Genres.* FROM Genres WHERE Id IN (SELECT rowid FROM GenreSearch WHERE GenreSearch MATCH {Phrase(term)})");
        var pattern = Pattern(term);
        return db.Genres.Where(genre => EF.Functions.Like(genre.Name, pattern, "\\"));
    }
}
