using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeNest.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessMasterDataAndCategoryHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CategoryId",
                table: "StockMovements",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CategoryName",
                table: "StockMovements",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CategoryRecorded",
                table: "StockMovements",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "UnitOfMeasureId",
                table: "ProductUnits",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "ProductCategories",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StockUnitId",
                table: "InventoryProducts",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "UnitsOfMeasure",
                columns: table => new
                {
                    BusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnitsOfMeasure", x => new { x.BusinessId, x.Id });
                    table.ForeignKey(
                        name: "FK_UnitsOfMeasure_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Reuse existing normalized product-unit names, including their Unicode
            // normalization. Existing GUIDs provide stable IDs without random SQL.
            // Do this before SQLite rebuilds tables to enforce the new foreign keys.
            migrationBuilder.Sql("""
                INSERT INTO UnitsOfMeasure (BusinessId, Id, Name, NormalizedName, IsActive)
                SELECT BusinessId, MIN(Id), MIN(Name), NormalizedName, 1
                FROM ProductUnits GROUP BY BusinessId, NormalizedName;
                UPDATE ProductUnits SET UnitOfMeasureId = (
                    SELECT u.Id FROM UnitsOfMeasure u
                    WHERE u.BusinessId = ProductUnits.BusinessId AND u.NormalizedName = ProductUnits.NormalizedName);
                UPDATE InventoryProducts SET StockUnitId = (
                    SELECT u.UnitOfMeasureId FROM ProductUnits u
                    WHERE u.BusinessId = InventoryProducts.BusinessId AND u.ProductId = InventoryProducts.Id AND u.IsBaseUnit = 1);
                """);
            // Older movements deliberately keep CategoryRecorded=false. A current
            // category cannot reliably tell us what the category was in the past.

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_BusinessId_CategoryId_EffectiveDate",
                table: "StockMovements",
                columns: new[] { "BusinessId", "CategoryId", "EffectiveDate" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductUnits_BusinessId_UnitOfMeasureId",
                table: "ProductUnits",
                columns: new[] { "BusinessId", "UnitOfMeasureId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryProducts_BusinessId_StockUnitId",
                table: "InventoryProducts",
                columns: new[] { "BusinessId", "StockUnitId" });

            migrationBuilder.CreateIndex(
                name: "IX_UnitsOfMeasure_BusinessId_NormalizedName",
                table: "UnitsOfMeasure",
                columns: new[] { "BusinessId", "NormalizedName" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryProducts_UnitsOfMeasure_BusinessId_StockUnitId",
                table: "InventoryProducts",
                columns: new[] { "BusinessId", "StockUnitId" },
                principalTable: "UnitsOfMeasure",
                principalColumns: new[] { "BusinessId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ProductUnits_UnitsOfMeasure_BusinessId_UnitOfMeasureId",
                table: "ProductUnits",
                columns: new[] { "BusinessId", "UnitOfMeasureId" },
                principalTable: "UnitsOfMeasure",
                principalColumns: new[] { "BusinessId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StockMovements_ProductCategories_BusinessId_CategoryId",
                table: "StockMovements",
                columns: new[] { "BusinessId", "CategoryId" },
                principalTable: "ProductCategories",
                principalColumns: new[] { "BusinessId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // SQLite defers FK-removal table rebuilds until the end of the batch.
            // Disable checks outside its transaction before removing the master;
            // the generated rebuild batch restores foreign_keys=1 afterwards.
            migrationBuilder.Sql("PRAGMA foreign_keys = 0;", suppressTransaction: true);
            migrationBuilder.DropForeignKey(
                name: "FK_InventoryProducts_UnitsOfMeasure_BusinessId_StockUnitId",
                table: "InventoryProducts");

            migrationBuilder.DropForeignKey(
                name: "FK_ProductUnits_UnitsOfMeasure_BusinessId_UnitOfMeasureId",
                table: "ProductUnits");

            migrationBuilder.DropForeignKey(
                name: "FK_StockMovements_ProductCategories_BusinessId_CategoryId",
                table: "StockMovements");


            migrationBuilder.DropIndex(
                name: "IX_StockMovements_BusinessId_CategoryId_EffectiveDate",
                table: "StockMovements");

            migrationBuilder.DropIndex(
                name: "IX_ProductUnits_BusinessId_UnitOfMeasureId",
                table: "ProductUnits");

            migrationBuilder.DropIndex(
                name: "IX_InventoryProducts_BusinessId_StockUnitId",
                table: "InventoryProducts");

            migrationBuilder.DropColumn(
                name: "CategoryId",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "CategoryName",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "CategoryRecorded",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "UnitOfMeasureId",
                table: "ProductUnits");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "ProductCategories");

            migrationBuilder.DropColumn(
                name: "StockUnitId",
                table: "InventoryProducts");
            migrationBuilder.DropTable(name: "UnitsOfMeasure");
        }
    }
}
