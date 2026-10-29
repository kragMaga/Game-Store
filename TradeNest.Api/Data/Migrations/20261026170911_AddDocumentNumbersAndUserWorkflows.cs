using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeNest.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentNumbersAndUserWorkflows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DocumentNumber",
                table: "InventoryTransactions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DocumentNumber",
                table: "InventoryCorrections",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DocumentNumber",
                table: "CustomerPayments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DocumentNumber",
                table: "CustomerPaymentReversals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DocumentSequences",
                columns: table => new
                {
                    BusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Prefix = table.Column<string>(type: "TEXT", nullable: false),
                    LastNumber = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentSequences", x => new { x.BusinessId, x.Prefix });
                    table.ForeignKey(
                        name: "FK_DocumentSequences_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransactions_BusinessId_DocumentNumber",
                table: "InventoryTransactions",
                columns: new[] { "BusinessId", "DocumentNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCorrections_BusinessId_DocumentNumber",
                table: "InventoryCorrections",
                columns: new[] { "BusinessId", "DocumentNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_BusinessId_DocumentNumber",
                table: "CustomerPayments",
                columns: new[] { "BusinessId", "DocumentNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPaymentReversals_BusinessId_DocumentNumber",
                table: "CustomerPaymentReversals",
                columns: new[] { "BusinessId", "DocumentNumber" },
                unique: true);
            // Number existing rows deterministically without changing their IDs,
            // external references, stock, or monetary values. Seed counters above
            // all assigned values before the first post-upgrade write.
            migrationBuilder.Sql("""
                WITH numbered AS (
                    SELECT BusinessId, Id, CASE WHEN Kind = 'Sale' THEN 'SALE' ELSE 'PUR' END AS Prefix,
                        ROW_NUMBER() OVER (PARTITION BY BusinessId, Kind ORDER BY RecordedAtUtc, Id) AS Number
                    FROM InventoryTransactions
                )
                UPDATE InventoryTransactions SET DocumentNumber = (
                    SELECT Prefix || '-' || printf('%06d', Number) FROM numbered
                    WHERE numbered.BusinessId = InventoryTransactions.BusinessId AND numbered.Id = InventoryTransactions.Id
                );
                INSERT INTO DocumentSequences (BusinessId, Prefix, LastNumber)
                SELECT BusinessId, CASE WHEN Kind = 'Sale' THEN 'SALE' ELSE 'PUR' END, COUNT(*) FROM InventoryTransactions GROUP BY BusinessId, Kind;
                """);
            migrationBuilder.Sql("""
                WITH numbered AS (
                    SELECT BusinessId, Id, 'PAY' AS Prefix,
                        ROW_NUMBER() OVER (PARTITION BY BusinessId ORDER BY RecordedAtUtc, Id) AS Number
                    FROM CustomerPayments
                )
                UPDATE CustomerPayments SET DocumentNumber = (
                    SELECT Prefix || '-' || printf('%06d', Number) FROM numbered
                    WHERE numbered.BusinessId = CustomerPayments.BusinessId AND numbered.Id = CustomerPayments.Id
                );
                INSERT INTO DocumentSequences (BusinessId, Prefix, LastNumber)
                SELECT BusinessId, 'PAY', COUNT(*) FROM CustomerPayments GROUP BY BusinessId;
                """);
            migrationBuilder.Sql("""
                WITH numbered AS (
                    SELECT BusinessId, Id, 'REV' AS Prefix,
                        ROW_NUMBER() OVER (PARTITION BY BusinessId ORDER BY RecordedAtUtc, Id) AS Number
                    FROM CustomerPaymentReversals
                )
                UPDATE CustomerPaymentReversals SET DocumentNumber = (
                    SELECT Prefix || '-' || printf('%06d', Number) FROM numbered
                    WHERE numbered.BusinessId = CustomerPaymentReversals.BusinessId AND numbered.Id = CustomerPaymentReversals.Id
                );
                INSERT INTO DocumentSequences (BusinessId, Prefix, LastNumber)
                SELECT BusinessId, 'REV', COUNT(*) FROM CustomerPaymentReversals GROUP BY BusinessId;
                """);
            migrationBuilder.Sql("""
                WITH numbered AS (
                    SELECT BusinessId, Id, 'COR' AS Prefix,
                        ROW_NUMBER() OVER (PARTITION BY BusinessId ORDER BY RecordedAtUtc, Id) AS Number
                    FROM InventoryCorrections
                )
                UPDATE InventoryCorrections SET DocumentNumber = (
                    SELECT Prefix || '-' || printf('%06d', Number) FROM numbered
                    WHERE numbered.BusinessId = InventoryCorrections.BusinessId AND numbered.Id = InventoryCorrections.Id
                );
                INSERT INTO DocumentSequences (BusinessId, Prefix, LastNumber)
                SELECT BusinessId, 'COR', COUNT(*) FROM InventoryCorrections GROUP BY BusinessId;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentSequences");

            migrationBuilder.DropIndex(
                name: "IX_InventoryTransactions_BusinessId_DocumentNumber",
                table: "InventoryTransactions");

            migrationBuilder.DropIndex(
                name: "IX_InventoryCorrections_BusinessId_DocumentNumber",
                table: "InventoryCorrections");

            migrationBuilder.DropIndex(
                name: "IX_CustomerPayments_BusinessId_DocumentNumber",
                table: "CustomerPayments");

            migrationBuilder.DropIndex(
                name: "IX_CustomerPaymentReversals_BusinessId_DocumentNumber",
                table: "CustomerPaymentReversals");

            migrationBuilder.DropColumn(
                name: "DocumentNumber",
                table: "InventoryTransactions");

            migrationBuilder.DropColumn(
                name: "DocumentNumber",
                table: "InventoryCorrections");

            migrationBuilder.DropColumn(
                name: "DocumentNumber",
                table: "CustomerPayments");

            migrationBuilder.DropColumn(
                name: "DocumentNumber",
                table: "CustomerPaymentReversals");
        }
    }
}
