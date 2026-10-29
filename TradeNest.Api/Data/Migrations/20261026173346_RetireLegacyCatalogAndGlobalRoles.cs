using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable
namespace TradeNest.Api.Data.Migrations;

// Retire application mappings without destroying the old catalog or global-role
// history. Archives are intentionally unmapped and have no HTTP surface.
public partial class RetireLegacyCatalogAndGlobalRoles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Derived FTS indexes can be rebuilt. Remove triggers before renaming the
        // source tables so they cannot reference retired search structures.
        migrationBuilder.Sql("""
            DROP TRIGGER IF EXISTS GameSearch_Insert;
            DROP TRIGGER IF EXISTS GameSearch_Update;
            DROP TRIGGER IF EXISTS GameSearch_Delete;
            DROP TRIGGER IF EXISTS GenreSearch_Insert;
            DROP TRIGGER IF EXISTS GenreSearch_Update;
            DROP TRIGGER IF EXISTS GenreSearch_Delete;
            DROP TABLE IF EXISTS GameSearch;
            DROP TABLE IF EXISTS GenreSearch;
            CREATE TABLE ArchivedGlobalRoles (UserId INTEGER NOT NULL PRIMARY KEY, Role TEXT NOT NULL);
            INSERT INTO ArchivedGlobalRoles (UserId, Role) SELECT Id, Role FROM Users;
            """);
        migrationBuilder.RenameTable(name: "Games", newName: "ArchivedGames");
        migrationBuilder.RenameTable(name: "Genres", newName: "ArchivedGenres");
        migrationBuilder.DropColumn(name: "Role", table: "Users");
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Downgrade restores archived source rows rather than creating an empty
        // catalog. Accounts created after retirement receive no admin privilege.
        migrationBuilder.AddColumn<string>(name: "Role", table: "Users", type: "TEXT", nullable: false, defaultValue: "customer");
        migrationBuilder.Sql("""
            UPDATE Users SET Role = (SELECT Role FROM ArchivedGlobalRoles WHERE UserId = Users.Id)
            WHERE EXISTS (SELECT 1 FROM ArchivedGlobalRoles WHERE UserId = Users.Id);
            DROP TABLE ArchivedGlobalRoles;
            """);
        migrationBuilder.RenameTable(name: "ArchivedGenres", newName: "Genres");
        migrationBuilder.RenameTable(name: "ArchivedGames", newName: "Games");
        // Recreate the same FTS structures expected by historical migrations.
        migrationBuilder.Sql("""
            CREATE VIRTUAL TABLE GameSearch USING fts5(Name, GenreName, tokenize='trigram');
            CREATE VIRTUAL TABLE GenreSearch USING fts5(Name, tokenize='trigram');
            INSERT INTO GameSearch(rowid, Name, GenreName)
                SELECT Games.Id, Games.Name, Genres.Name FROM Games JOIN Genres ON Games.GenreId = Genres.Id;
            INSERT INTO GenreSearch(rowid, Name) SELECT Id, Name FROM Genres;
            CREATE TRIGGER GameSearch_Insert AFTER INSERT ON Games BEGIN
                INSERT INTO GameSearch(rowid, Name, GenreName)
                SELECT NEW.Id, NEW.Name, Name FROM Genres WHERE Id = NEW.GenreId;
            END;
            CREATE TRIGGER GameSearch_Update AFTER UPDATE OF Name, GenreId ON Games BEGIN
                DELETE FROM GameSearch WHERE rowid = OLD.Id;
                INSERT INTO GameSearch(rowid, Name, GenreName)
                SELECT NEW.Id, NEW.Name, Name FROM Genres WHERE Id = NEW.GenreId;
            END;
            CREATE TRIGGER GameSearch_Delete AFTER DELETE ON Games BEGIN
                DELETE FROM GameSearch WHERE rowid = OLD.Id;
            END;
            CREATE TRIGGER GenreSearch_Insert AFTER INSERT ON Genres BEGIN
                INSERT INTO GenreSearch(rowid, Name) VALUES(NEW.Id, NEW.Name);
            END;
            CREATE TRIGGER GenreSearch_Update AFTER UPDATE OF Name ON Genres BEGIN
                UPDATE GenreSearch SET Name = NEW.Name WHERE rowid = NEW.Id;
                UPDATE GameSearch SET GenreName = NEW.Name
                    WHERE rowid IN (SELECT Id FROM Games WHERE GenreId = NEW.Id);
            END;
            CREATE TRIGGER GenreSearch_Delete AFTER DELETE ON Genres BEGIN
                DELETE FROM GenreSearch WHERE rowid = OLD.Id;
            END;
            """);
    }
}
