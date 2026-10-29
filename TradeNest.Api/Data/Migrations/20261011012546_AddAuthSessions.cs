using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeNest.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuthSessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastRefreshedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AbsoluteExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuthSessions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql(
                "INSERT INTO AuthSessions (Id, UserId, CreatedAtUtc, LastRefreshedAtUtc, AbsoluteExpiresAtUtc, RevokedAtUtc) " +
                "SELECT SessionId, UserId, MIN(CreatedAtUtc), MAX(CreatedAtUtc), MIN(AbsoluteExpiresAtUtc), MAX(RevokedAtUtc) " +
                "FROM RefreshTokens GROUP BY SessionId, UserId");

            migrationBuilder.Sql(
                "CREATE TRIGGER TR_RefreshTokens_ValidateSession_Insert " +
                "BEFORE INSERT ON RefreshTokens " +
                "WHEN NOT EXISTS (SELECT 1 FROM AuthSessions WHERE Id = NEW.SessionId AND UserId = NEW.UserId) " +
                "BEGIN SELECT RAISE(ABORT, 'Refresh token references an invalid authentication session'); END");

            migrationBuilder.Sql(
                "CREATE TRIGGER TR_RefreshTokens_ValidateSession_Update " +
                "BEFORE UPDATE OF SessionId, UserId ON RefreshTokens " +
                "WHEN NOT EXISTS (SELECT 1 FROM AuthSessions WHERE Id = NEW.SessionId AND UserId = NEW.UserId) " +
                "BEGIN SELECT RAISE(ABORT, 'Refresh token references an invalid authentication session'); END");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_SessionId_ExpiresAtUtc",
                table: "RefreshTokens",
                columns: new[] { "SessionId", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuthSessions_UserId_RevokedAtUtc",
                table: "AuthSessions",
                columns: new[] { "UserId", "RevokedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_RefreshTokens_ValidateSession_Insert");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_RefreshTokens_ValidateSession_Update");
            migrationBuilder.DropTable(
                name: "AuthSessions");

            migrationBuilder.DropIndex(
                name: "IX_RefreshTokens_SessionId_ExpiresAtUtc",
                table: "RefreshTokens");

        }
    }
}
