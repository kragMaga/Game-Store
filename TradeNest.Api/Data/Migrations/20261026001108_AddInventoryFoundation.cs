using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeNest.Api.Data.Migrations
{
    // Add tenant inventory alongside the legacy game catalog. Composite tenant
    // keys constrain category/unit links, while exact integer quantities avoid
    // floating-point storage and preserve reproducible movement balances.
    /// <inheritdoc />
    public partial class AddInventoryFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProductCategories",
                columns: table => new
                {
                    BusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductCategories", x => new { x.BusinessId, x.Id });
                    table.ForeignKey(
                        name: "FK_ProductCategories_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InventoryProducts",
                columns: table => new
                {
                    BusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CategoryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Sku = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    NormalizedSku = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    StockUnit = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    AllowsFractionalStock = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReorderLevelMicros = table.Column<long>(type: "INTEGER", nullable: false),
                    QuantityOnHandMicros = table.Column<long>(type: "INTEGER", nullable: false),
                    OpeningStockRecorded = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryProducts", x => new { x.BusinessId, x.Id });
                    table.CheckConstraint("CK_Product_Stock", "\"QuantityOnHandMicros\" >= 0 AND \"ReorderLevelMicros\" >= 0");
                    table.ForeignKey(
                        name: "FK_InventoryProducts_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryProducts_ProductCategories_BusinessId_CategoryId",
                        columns: x => new { x.BusinessId, x.CategoryId },
                        principalTable: "ProductCategories",
                        principalColumns: new[] { "BusinessId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductUnits",
                columns: table => new
                {
                    BusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    IsBaseUnit = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowsFractionalQuantity = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConversionFactorMicros = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductUnits", x => new { x.BusinessId, x.Id });
                    table.UniqueConstraint("AK_ProductUnits_BusinessId_ProductId_Id", x => new { x.BusinessId, x.ProductId, x.Id });
                    table.CheckConstraint("CK_Unit_Factor", "\"ConversionFactorMicros\" > 0");
                    table.ForeignKey(
                        name: "FK_ProductUnits_InventoryProducts_BusinessId_ProductId",
                        columns: x => new { x.BusinessId, x.ProductId },
                        principalTable: "InventoryProducts",
                        principalColumns: new[] { "BusinessId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockMovements",
                columns: table => new
                {
                    BusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UnitId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    UnitName = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    UnitQuantityMicros = table.Column<long>(type: "INTEGER", nullable: false),
                    ConversionFactorMicros = table.Column<long>(type: "INTEGER", nullable: false),
                    BaseQuantityMicros = table.Column<long>(type: "INTEGER", nullable: false),
                    BalanceAfterMicros = table.Column<long>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RequestFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockMovements", x => new { x.BusinessId, x.Id });
                    table.CheckConstraint("CK_Movement_Balance", "\"BalanceAfterMicros\" >= 0");
                    table.ForeignKey(
                        name: "FK_StockMovements_InventoryProducts_BusinessId_ProductId",
                        columns: x => new { x.BusinessId, x.ProductId },
                        principalTable: "InventoryProducts",
                        principalColumns: new[] { "BusinessId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockMovements_ProductUnits_BusinessId_ProductId_UnitId",
                        columns: x => new { x.BusinessId, x.ProductId, x.UnitId },
                        principalTable: "ProductUnits",
                        principalColumns: new[] { "BusinessId", "ProductId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockMovements_Users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryProducts_BusinessId_CategoryId",
                table: "InventoryProducts",
                columns: new[] { "BusinessId", "CategoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryProducts_BusinessId_NormalizedSku",
                table: "InventoryProducts",
                columns: new[] { "BusinessId", "NormalizedSku" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductCategories_BusinessId_NormalizedName",
                table: "ProductCategories",
                columns: new[] { "BusinessId", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductUnits_BusinessId_ProductId",
                table: "ProductUnits",
                columns: new[] { "BusinessId", "ProductId" },
                unique: true,
                filter: "\"IsBaseUnit\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_ProductUnits_BusinessId_ProductId_NormalizedName",
                table: "ProductUnits",
                columns: new[] { "BusinessId", "ProductId", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_ActorUserId",
                table: "StockMovements",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_BusinessId_IdempotencyKey",
                table: "StockMovements",
                columns: new[] { "BusinessId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_BusinessId_ProductId_RecordedAtUtc",
                table: "StockMovements",
                columns: new[] { "BusinessId", "ProductId", "RecordedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_BusinessId_ProductId_UnitId",
                table: "StockMovements",
                columns: new[] { "BusinessId", "ProductId", "UnitId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StockMovements");

            migrationBuilder.DropTable(
                name: "ProductUnits");

            migrationBuilder.DropTable(
                name: "InventoryProducts");

            migrationBuilder.DropTable(
                name: "ProductCategories");
        }
    }
}
