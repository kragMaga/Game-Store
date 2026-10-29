using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeNest.Api.Data.Migrations
{
    // Add purchase/sale records without rewriting existing opening balances.
    // Existing businesses retain two-decimal totals unless explicitly configured
    // at creation. New document/line keys preserve tenant boundaries in SQL.
    /// <inheritdoc />
    public partial class AddPurchasesAndSales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SequenceNumber",
                table: "StockMovements",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "CurrencyDecimalPlaces",
                table: "Businesses",
                type: "INTEGER",
                nullable: false,
                defaultValue: 2);

            // Backfill an ordering before adding the unique sequence index. Older
            // openings had no sequence number, and all rows must keep their IDs
            // and quantities unchanged while becoming part of the extended ledger.
            migrationBuilder.Sql("""
                WITH ordered AS (
                    SELECT BusinessId, Id,
                        ROW_NUMBER() OVER (PARTITION BY BusinessId, ProductId ORDER BY RecordedAtUtc, Id) AS Position
                    FROM StockMovements
                )
                UPDATE StockMovements SET SequenceNumber = (
                    SELECT Position FROM ordered
                    WHERE ordered.BusinessId = StockMovements.BusinessId AND ordered.Id = StockMovements.Id
                );
                """);

            migrationBuilder.CreateTable(
                name: "Suppliers",
                columns: table => new
                {
                    BusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    ContactReference = table.Column<string>(type: "TEXT", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Suppliers", x => new { x.BusinessId, x.Id });
                    table.ForeignKey(
                        name: "FK_Suppliers_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InventoryTransactions",
                columns: table => new
                {
                    BusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    SupplierId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    CurrencyDecimalPlaces = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RequestFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryTransactions", x => new { x.BusinessId, x.Id });
                    table.CheckConstraint("CK_Transaction_Total", "\"TotalMinor\" >= 0 AND \"CurrencyDecimalPlaces\" BETWEEN 0 AND 4");
                    table.ForeignKey(
                        name: "FK_InventoryTransactions_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransactions_Suppliers_BusinessId_SupplierId",
                        columns: x => new { x.BusinessId, x.SupplierId },
                        principalTable: "Suppliers",
                        principalColumns: new[] { "BusinessId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransactions_Users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InventoryTransactionLines",
                columns: table => new
                {
                    BusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TransactionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UnitId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StockMovementId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProductName = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Sku = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UnitName = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    QuantityMicros = table.Column<long>(type: "INTEGER", nullable: false),
                    ConversionFactorMicros = table.Column<long>(type: "INTEGER", nullable: false),
                    BaseQuantityMicros = table.Column<long>(type: "INTEGER", nullable: false),
                    UnitPriceMicros = table.Column<long>(type: "INTEGER", nullable: false),
                    LineTotalMinor = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryTransactionLines", x => new { x.BusinessId, x.Id });
                    table.CheckConstraint("CK_TransactionLine_Amounts", "\"QuantityMicros\" > 0 AND \"BaseQuantityMicros\" > 0 AND \"UnitPriceMicros\" >= 0 AND \"LineTotalMinor\" >= 0");
                    table.ForeignKey(
                        name: "FK_InventoryTransactionLines_InventoryProducts_BusinessId_ProductId",
                        columns: x => new { x.BusinessId, x.ProductId },
                        principalTable: "InventoryProducts",
                        principalColumns: new[] { "BusinessId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransactionLines_InventoryTransactions_BusinessId_TransactionId",
                        columns: x => new { x.BusinessId, x.TransactionId },
                        principalTable: "InventoryTransactions",
                        principalColumns: new[] { "BusinessId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransactionLines_ProductUnits_BusinessId_ProductId_UnitId",
                        columns: x => new { x.BusinessId, x.ProductId, x.UnitId },
                        principalTable: "ProductUnits",
                        principalColumns: new[] { "BusinessId", "ProductId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransactionLines_StockMovements_BusinessId_StockMovementId",
                        columns: x => new { x.BusinessId, x.StockMovementId },
                        principalTable: "StockMovements",
                        principalColumns: new[] { "BusinessId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_BusinessId_ProductId_SequenceNumber",
                table: "StockMovements",
                columns: new[] { "BusinessId", "ProductId", "SequenceNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransactionLines_BusinessId_ProductId_UnitId",
                table: "InventoryTransactionLines",
                columns: new[] { "BusinessId", "ProductId", "UnitId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransactionLines_BusinessId_StockMovementId",
                table: "InventoryTransactionLines",
                columns: new[] { "BusinessId", "StockMovementId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransactionLines_BusinessId_TransactionId_Position",
                table: "InventoryTransactionLines",
                columns: new[] { "BusinessId", "TransactionId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransactions_ActorUserId",
                table: "InventoryTransactions",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransactions_BusinessId_IdempotencyKey",
                table: "InventoryTransactions",
                columns: new[] { "BusinessId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransactions_BusinessId_Kind_RecordedAtUtc",
                table: "InventoryTransactions",
                columns: new[] { "BusinessId", "Kind", "RecordedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransactions_BusinessId_SupplierId",
                table: "InventoryTransactions",
                columns: new[] { "BusinessId", "SupplierId" });

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_BusinessId_NormalizedName",
                table: "Suppliers",
                columns: new[] { "BusinessId", "NormalizedName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventoryTransactionLines");

            migrationBuilder.DropTable(
                name: "InventoryTransactions");

            migrationBuilder.DropTable(
                name: "Suppliers");

            migrationBuilder.DropIndex(
                name: "IX_StockMovements_BusinessId_ProductId_SequenceNumber",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "SequenceNumber",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "CurrencyDecimalPlaces",
                table: "Businesses");
        }
    }
}
