using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeNest.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CompleteInventoryOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Nullable lifecycle markers preserve the state of existing accounts/businesses.
            migrationBuilder.AddColumn<DateTime>(
                name: "AccountClosedAtUtc",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClosedAtUtc",
                table: "Businesses",
                type: "TEXT",
                nullable: true);

            // Add append-only correction/dispatch tables; do not rewrite legacy stock or money.
            migrationBuilder.CreateTable(
                name: "BusinessNotificationDispatches",
                columns: table => new
                {
                    BusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LocalDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ItemCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessNotificationDispatches", x => new { x.BusinessId, x.LocalDate });
                    table.ForeignKey(
                        name: "FK_BusinessNotificationDispatches_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InventoryCorrections",
                columns: table => new
                {
                    BusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    TransactionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", nullable: false),
                    CurrencyDecimalPlaces = table.Column<int>(type: "INTEGER", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RequestFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryCorrections", x => new { x.BusinessId, x.Id });
                    table.ForeignKey(
                        name: "FK_InventoryCorrections_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryCorrections_InventoryTransactions_BusinessId_TransactionId",
                        columns: x => new { x.BusinessId, x.TransactionId },
                        principalTable: "InventoryTransactions",
                        principalColumns: new[] { "BusinessId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryCorrections_Users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerChargeAdjustments",
                columns: table => new
                {
                    BusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CorrectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChargeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeltaMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    RefundMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    RefundMethod = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerChargeAdjustments", x => new { x.BusinessId, x.Id });
                    table.CheckConstraint("CK_Adjustment_Refund", "\"RefundMinor\" >= 0");
                    table.ForeignKey(
                        name: "FK_CustomerChargeAdjustments_CustomerCharges_BusinessId_ChargeId",
                        columns: x => new { x.BusinessId, x.ChargeId },
                        principalTable: "CustomerCharges",
                        principalColumns: new[] { "BusinessId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerChargeAdjustments_InventoryCorrections_BusinessId_CorrectionId",
                        columns: x => new { x.BusinessId, x.CorrectionId },
                        principalTable: "InventoryCorrections",
                        principalColumns: new[] { "BusinessId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InventoryCorrectionLines",
                columns: table => new
                {
                    BusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CorrectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TransactionLineId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StockMovementId = table.Column<Guid>(type: "TEXT", nullable: false),
                    QuantityMicros = table.Column<long>(type: "INTEGER", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Restockable = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryCorrectionLines", x => new { x.BusinessId, x.Id });
                    table.ForeignKey(
                        name: "FK_InventoryCorrectionLines_InventoryCorrections_BusinessId_CorrectionId",
                        columns: x => new { x.BusinessId, x.CorrectionId },
                        principalTable: "InventoryCorrections",
                        principalColumns: new[] { "BusinessId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryCorrectionLines_InventoryProducts_BusinessId_ProductId",
                        columns: x => new { x.BusinessId, x.ProductId },
                        principalTable: "InventoryProducts",
                        principalColumns: new[] { "BusinessId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryCorrectionLines_InventoryTransactionLines_BusinessId_TransactionLineId",
                        columns: x => new { x.BusinessId, x.TransactionLineId },
                        principalTable: "InventoryTransactionLines",
                        principalColumns: new[] { "BusinessId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryCorrectionLines_StockMovements_BusinessId_StockMovementId",
                        columns: x => new { x.BusinessId, x.StockMovementId },
                        principalTable: "StockMovements",
                        principalColumns: new[] { "BusinessId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerChargeAdjustments_BusinessId_ChargeId",
                table: "CustomerChargeAdjustments",
                columns: new[] { "BusinessId", "ChargeId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerChargeAdjustments_BusinessId_CorrectionId",
                table: "CustomerChargeAdjustments",
                columns: new[] { "BusinessId", "CorrectionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCorrectionLines_BusinessId_CorrectionId",
                table: "InventoryCorrectionLines",
                columns: new[] { "BusinessId", "CorrectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCorrectionLines_BusinessId_ProductId",
                table: "InventoryCorrectionLines",
                columns: new[] { "BusinessId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCorrectionLines_BusinessId_StockMovementId",
                table: "InventoryCorrectionLines",
                columns: new[] { "BusinessId", "StockMovementId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCorrectionLines_BusinessId_TransactionLineId",
                table: "InventoryCorrectionLines",
                columns: new[] { "BusinessId", "TransactionLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCorrections_ActorUserId",
                table: "InventoryCorrections",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCorrections_BusinessId_IdempotencyKey",
                table: "InventoryCorrections",
                columns: new[] { "BusinessId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCorrections_BusinessId_TransactionId",
                table: "InventoryCorrections",
                columns: new[] { "BusinessId", "TransactionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BusinessNotificationDispatches");

            migrationBuilder.DropTable(
                name: "CustomerChargeAdjustments");

            migrationBuilder.DropTable(
                name: "InventoryCorrectionLines");

            migrationBuilder.DropTable(
                name: "InventoryCorrections");

            migrationBuilder.DropColumn(
                name: "AccountClosedAtUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ClosedAtUtc",
                table: "Businesses");
        }
    }
}
