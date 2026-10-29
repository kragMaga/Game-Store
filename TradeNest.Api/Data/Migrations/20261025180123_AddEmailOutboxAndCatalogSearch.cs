using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeNest.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailOutboxAndCatalogSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailOutbox",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedPayload = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LeaseOwner = table.Column<string>(type: "TEXT", nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FailureType = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailOutbox", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailOutbox_Status_NextAttemptAtUtc_LeaseExpiresAtUtc",
                table: "EmailOutbox",
                columns: new[] { "Status", "NextAttemptAtUtc", "LeaseExpiresAtUtc" });
            // FTS tables are maintained by SQLite triggers, including writes made
            // outside EF. Backfill existing catalog rows when upgrading a database.
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS GameSearch_Insert;
                DROP TRIGGER IF EXISTS GameSearch_Update;
                DROP TRIGGER IF EXISTS GameSearch_Delete;
                DROP TRIGGER IF EXISTS GenreSearch_Insert;
                DROP TRIGGER IF EXISTS GenreSearch_Update;
                DROP TRIGGER IF EXISTS GenreSearch_Delete;
                DROP TABLE IF EXISTS GameSearch;
                DROP TABLE IF EXISTS GenreSearch;
                """);
            migrationBuilder.DropTable(
                name: "EmailOutbox");
        }
    }
}
