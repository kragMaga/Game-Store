using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeNest.Api.Data.Migrations
{
    // New sale charges/payments are recorded explicitly. Existing sale headers
    // remain financially untracked; never infer that old sales were paid or owed.
    /// <inheritdoc />
    public partial class AddCustomerLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CustomerId",
                table: "InventoryTransactions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerName",
                table: "InventoryTransactions",
                type: "TEXT",
                maxLength: 160,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Customers",
                columns: table => new
                {
                    BusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    NormalizedCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 254, nullable: true),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => new { x.BusinessId, x.Id });
                    table.ForeignKey(
                        name: "FK_Customers_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerCharges",
                columns: table => new
                {
                    BusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CustomerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SaleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    CurrencyDecimalPlaces = table.Column<int>(type: "INTEGER", nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RequestFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerCharges", x => new { x.BusinessId, x.Id });
                    table.CheckConstraint("CK_Charge_Amount", "\"AmountMinor\" >= 0 AND \"CurrencyDecimalPlaces\" BETWEEN 0 AND 4");
                    table.ForeignKey(
                        name: "FK_CustomerCharges_Customers_BusinessId_CustomerId",
                        columns: x => new { x.BusinessId, x.CustomerId },
                        principalTable: "Customers",
                        principalColumns: new[] { "BusinessId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerCharges_InventoryTransactions_BusinessId_SaleId",
                        columns: x => new { x.BusinessId, x.SaleId },
                        principalTable: "InventoryTransactions",
                        principalColumns: new[] { "BusinessId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerCharges_Users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerPayments",
                columns: table => new
                {
                    BusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CustomerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Method = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    CurrencyDecimalPlaces = table.Column<int>(type: "INTEGER", nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RecordedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RequestFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerPayments", x => new { x.BusinessId, x.Id });
                    table.CheckConstraint("CK_Payment_Amount", "\"AmountMinor\" > 0 AND \"CurrencyDecimalPlaces\" BETWEEN 0 AND 4");
                    table.ForeignKey(
                        name: "FK_CustomerPayments_Customers_BusinessId_CustomerId",
                        columns: x => new { x.BusinessId, x.CustomerId },
                        principalTable: "Customers",
                        principalColumns: new[] { "BusinessId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerPayments_Users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerPaymentAllocations",
                columns: table => new
                {
                    BusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PaymentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChargeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerPaymentAllocations", x => new { x.BusinessId, x.PaymentId, x.ChargeId });
                    table.CheckConstraint("CK_Allocation_Amount", "\"AmountMinor\" > 0");
                    table.ForeignKey(
                        name: "FK_CustomerPaymentAllocations_CustomerCharges_BusinessId_ChargeId",
                        columns: x => new { x.BusinessId, x.ChargeId },
                        principalTable: "CustomerCharges",
                        principalColumns: new[] { "BusinessId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerPaymentAllocations_CustomerPayments_BusinessId_PaymentId",
                        columns: x => new { x.BusinessId, x.PaymentId },
                        principalTable: "CustomerPayments",
                        principalColumns: new[] { "BusinessId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerPaymentReversals",
                columns: table => new
                {
                    BusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PaymentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RequestFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerPaymentReversals", x => new { x.BusinessId, x.Id });
                    table.ForeignKey(
                        name: "FK_CustomerPaymentReversals_CustomerPayments_BusinessId_PaymentId",
                        columns: x => new { x.BusinessId, x.PaymentId },
                        principalTable: "CustomerPayments",
                        principalColumns: new[] { "BusinessId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerPaymentReversals_Users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransactions_BusinessId_CustomerId",
                table: "InventoryTransactions",
                columns: new[] { "BusinessId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerCharges_ActorUserId",
                table: "CustomerCharges",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerCharges_BusinessId_CustomerId",
                table: "CustomerCharges",
                columns: new[] { "BusinessId", "CustomerId" },
                unique: true,
                filter: "\"Kind\" = 'OpeningDebt'");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerCharges_BusinessId_IdempotencyKey",
                table: "CustomerCharges",
                columns: new[] { "BusinessId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerCharges_BusinessId_SaleId",
                table: "CustomerCharges",
                columns: new[] { "BusinessId", "SaleId" },
                unique: true,
                filter: "\"SaleId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPaymentAllocations_BusinessId_ChargeId",
                table: "CustomerPaymentAllocations",
                columns: new[] { "BusinessId", "ChargeId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPaymentReversals_ActorUserId",
                table: "CustomerPaymentReversals",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPaymentReversals_BusinessId_IdempotencyKey",
                table: "CustomerPaymentReversals",
                columns: new[] { "BusinessId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPaymentReversals_BusinessId_PaymentId",
                table: "CustomerPaymentReversals",
                columns: new[] { "BusinessId", "PaymentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_ActorUserId",
                table: "CustomerPayments",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_BusinessId_CustomerId_RecordedAtUtc",
                table: "CustomerPayments",
                columns: new[] { "BusinessId", "CustomerId", "RecordedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_BusinessId_IdempotencyKey",
                table: "CustomerPayments",
                columns: new[] { "BusinessId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Customers_BusinessId_NormalizedCode",
                table: "Customers",
                columns: new[] { "BusinessId", "NormalizedCode" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryTransactions_Customers_BusinessId_CustomerId",
                table: "InventoryTransactions",
                columns: new[] { "BusinessId", "CustomerId" },
                principalTable: "Customers",
                principalColumns: new[] { "BusinessId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InventoryTransactions_Customers_BusinessId_CustomerId",
                table: "InventoryTransactions");

            migrationBuilder.DropTable(
                name: "CustomerPaymentAllocations");

            migrationBuilder.DropTable(
                name: "CustomerPaymentReversals");

            migrationBuilder.DropTable(
                name: "CustomerCharges");

            migrationBuilder.DropTable(
                name: "CustomerPayments");

            // SQLite defers the header-table rebuild that removes its customer FK.
            // Clear only columns being removed by this explicit downgrade before
            // dropping Customers, while retaining sale/stock IDs and amounts.
            migrationBuilder.Sql("UPDATE InventoryTransactions SET CustomerId = NULL, CustomerName = NULL;");

            migrationBuilder.DropTable(
                name: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_InventoryTransactions_BusinessId_CustomerId",
                table: "InventoryTransactions");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                table: "InventoryTransactions");

            migrationBuilder.DropColumn(
                name: "CustomerName",
                table: "InventoryTransactions");
        }
    }
}
