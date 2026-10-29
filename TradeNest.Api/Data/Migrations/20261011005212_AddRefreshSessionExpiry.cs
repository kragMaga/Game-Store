using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeNest.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRefreshSessionExpiry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AbsoluteExpiresAtUtc",
                table: "RefreshTokens",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "SessionId",
                table: "RefreshTokens",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            // Invalidate old-format refresh tokens so all new sessions have explicit idle/absolute expiry.
            migrationBuilder.Sql(
                "UPDATE RefreshTokens SET AbsoluteExpiresAtUtc = ExpiresAtUtc, SessionId = lower(hex(randomblob(16))), RevokedAtUtc = COALESCE(RevokedAtUtc, CURRENT_TIMESTAMP)");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_UserId_SessionId_RevokedAtUtc",
                table: "RefreshTokens",
                columns: new[] { "UserId", "SessionId", "RevokedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RefreshTokens_UserId_SessionId_RevokedAtUtc",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "AbsoluteExpiresAtUtc",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "RefreshTokens");
        }
    }
}
