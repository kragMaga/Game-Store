using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace TradeNest.Api.Data.Migrations;

// The catalog is no longer needed, including its temporary retirement archives.
public partial class RemoveLegacyArchives : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Delete the dependent rows first to respect the legacy genre foreign key.
        // Only obsolete catalog/global-role data is removed; tenant data stays intact.
        migrationBuilder.DropTable(name: "ArchivedGames");
        migrationBuilder.DropTable(name: "ArchivedGenres");
        migrationBuilder.DropTable(name: "ArchivedGlobalRoles");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Historical downgrades still need these tables. Recreate their schema only:
        // migration rollback cannot recover data deliberately deleted by Up.
        migrationBuilder.Sql("""
            CREATE TABLE ArchivedGenres (Id INTEGER NOT NULL CONSTRAINT PK_Genres PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL);
            CREATE TABLE ArchivedGames (
                Id INTEGER NOT NULL CONSTRAINT PK_Games PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL, GenreId INTEGER NOT NULL, Price TEXT NOT NULL, ReleasedDate TEXT NOT NULL,
                CONSTRAINT FK_Games_Genres_GenreId FOREIGN KEY (GenreId) REFERENCES ArchivedGenres (Id) ON DELETE CASCADE);
            CREATE INDEX IX_Games_GenreId_Price ON ArchivedGames (GenreId, Price);
            CREATE INDEX IX_Games_Price ON ArchivedGames (Price);
            CREATE TABLE ArchivedGlobalRoles (UserId INTEGER NOT NULL PRIMARY KEY, Role TEXT NOT NULL);
            """);
    }
}
