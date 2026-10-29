using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeNest.Api.Data.Migrations
{
    // Add the tenant foundation alongside the legacy catalog. Existing Identity,
    // game data, and manually maintained FTS triggers are left intact. Restrictive
    // user relationships preserve tenant ownership and authority history.
    /// <inheritdoc />
    public partial class AddBusinessFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Businesses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    NotificationEmail = table.Column<string>(type: "TEXT", maxLength: 254, nullable: false),
                    TimeZoneId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DailyNotificationTime = table.Column<TimeOnly>(type: "TEXT", nullable: false),
                    LowStockEmailsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    OwnerUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Businesses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Businesses_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BusinessAuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    Action = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessAuditEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BusinessAuditEvents_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BusinessAuditEvents_Users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BusinessInvitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 254, nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CanRecordCustomerPayments = table.Column<bool>(type: "INTEGER", nullable: false),
                    InvitedByUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AcceptedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessInvitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BusinessInvitations_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BusinessInvitations_Users_InvitedByUserId",
                        column: x => x.InvitedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BusinessMemberships",
                columns: table => new
                {
                    BusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanRecordCustomerPayments = table.Column<bool>(type: "INTEGER", nullable: false),
                    JoinedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessMemberships", x => new { x.BusinessId, x.UserId });
                    table.ForeignKey(
                        name: "FK_BusinessMemberships_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BusinessMemberships_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessAuditEvents_ActorUserId",
                table: "BusinessAuditEvents",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessAuditEvents_BusinessId_CreatedAtUtc",
                table: "BusinessAuditEvents",
                columns: new[] { "BusinessId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Businesses_OwnerUserId",
                table: "Businesses",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessInvitations_BusinessId_NormalizedEmail",
                table: "BusinessInvitations",
                columns: new[] { "BusinessId", "NormalizedEmail" });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessInvitations_InvitedByUserId",
                table: "BusinessInvitations",
                column: "InvitedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessInvitations_TokenHash",
                table: "BusinessInvitations",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BusinessMemberships_BusinessId",
                table: "BusinessMemberships",
                column: "BusinessId",
                unique: true,
                filter: "\"Role\" = 'Owner'");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessMemberships_UserId",
                table: "BusinessMemberships",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BusinessAuditEvents");

            migrationBuilder.DropTable(
                name: "BusinessInvitations");

            migrationBuilder.DropTable(
                name: "BusinessMemberships");

            migrationBuilder.DropTable(
                name: "Businesses");
        }
    }
}
