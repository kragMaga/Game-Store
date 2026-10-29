using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeNest.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessInsightsAndImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DefaultSellingPriceMicros",
                table: "InventoryProducts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DebtReminderEmailsEnabled",
                table: "Businesses",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "DebtReminderRecipientsJson",
                table: "Businesses",
                type: "TEXT",
                nullable: false,
                // Existing businesses have no extra reminder recipients.
                defaultValue: "[]");

            migrationBuilder.CreateTable(
                name: "BusinessNotifications",
                columns: table => new
                {
                    BusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    LocalDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Recipient = table.Column<string>(type: "TEXT", nullable: false),
                    OutboxMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedPayload = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RetryUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FailureType = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessNotifications", x => new { x.BusinessId, x.Id });
                    table.ForeignKey(
                        name: "FK_BusinessNotifications_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DebtReminderDispatches",
                columns: table => new
                {
                    BusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LocalDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DebtReminderDispatches", x => new { x.BusinessId, x.LocalDate });
                    table.ForeignKey(
                        name: "FK_DebtReminderDispatches_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InventoryImportBatches",
                columns: table => new
                {
                    BusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    RowCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CommittedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryImportBatches", x => new { x.BusinessId, x.Id });
                    table.ForeignKey(
                        name: "FK_InventoryImportBatches_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryImportBatches_Users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessNotifications_BusinessId_Kind_LocalDate_Recipient",
                table: "BusinessNotifications",
                columns: new[] { "BusinessId", "Kind", "LocalDate", "Recipient" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BusinessNotifications_OutboxMessageId",
                table: "BusinessNotifications",
                column: "OutboxMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryImportBatches_ActorUserId",
                table: "InventoryImportBatches",
                column: "ActorUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BusinessNotifications");

            migrationBuilder.DropTable(
                name: "DebtReminderDispatches");

            migrationBuilder.DropTable(
                name: "InventoryImportBatches");

            migrationBuilder.DropColumn(
                name: "DefaultSellingPriceMicros",
                table: "InventoryProducts");

            migrationBuilder.DropColumn(
                name: "DebtReminderEmailsEnabled",
                table: "Businesses");

            migrationBuilder.DropColumn(
                name: "DebtReminderRecipientsJson",
                table: "Businesses");
        }
    }
}
