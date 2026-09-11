using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pol.Infrastructure.BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Task5OrdersLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "checkout");

            migrationBuilder.AddColumn<string>(
                name: "BusinessType",
                schema: "shop",
                table: "Orders",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByAccountId",
                schema: "shop",
                table: "Orders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FrozenAt",
                schema: "shop",
                table: "Orders",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsFrozen",
                schema: "shop",
                table: "Orders",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "IssuedAt",
                schema: "shop",
                table: "Orders",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OrderChargeAmount",
                schema: "shop",
                table: "Orders",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "OrderChargeCurrency",
                schema: "shop",
                table: "Orders",
                type: "char(3)",
                unicode: false,
                fixedLength: true,
                maxLength: 3,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "OrderDiscountAmount",
                schema: "shop",
                table: "Orders",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "OrderDiscountCurrency",
                schema: "shop",
                table: "Orders",
                type: "char(3)",
                unicode: false,
                fixedLength: true,
                maxLength: 3,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerBranchIdAtCreation",
                schema: "shop",
                table: "Orders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerSaleId",
                schema: "shop",
                table: "Orders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PaymentStatus",
                schema: "shop",
                table: "Orders",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "SubtotalAmount",
                schema: "shop",
                table: "Orders",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "SubtotalCurrency",
                schema: "shop",
                table: "Orders",
                type: "char(3)",
                unicode: false,
                fixedLength: true,
                maxLength: 3,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "LineAmount",
                schema: "shop",
                table: "OrderItems",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "LineCurrency",
                schema: "shop",
                table: "OrderItems",
                type: "char(3)",
                unicode: false,
                fixedLength: true,
                maxLength: 3,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "TaxAmount",
                schema: "shop",
                table: "OrderItems",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "TaxCurrency",
                schema: "shop",
                table: "OrderItems",
                type: "char(3)",
                unicode: false,
                fixedLength: true,
                maxLength: 3,
                nullable: false,
                defaultValue: "");

            // Existing Order/OrderItem rows predate the Task 5 money breakdown. Backfill from the
            // already persisted trusted totals before enforcing the new non-null money columns.
            migrationBuilder.Sql("""
                UPDATE [shop].[Orders]
                SET [OrderChargeCurrency] = [AmountCurrency],
                    [OrderDiscountCurrency] = [AmountCurrency],
                    [SubtotalAmount] = [AmountAmount],
                    [SubtotalCurrency] = [AmountCurrency],
                    [PaymentStatus] = 1;
                UPDATE [shop].[OrderItems]
                SET [TaxCurrency] = [UnitPriceCurrency],
                    [LineAmount] = ([UnitPriceAmount] * [Quantity]) - [DiscountAmount],
                    [LineCurrency] = [UnitPriceCurrency];
                """);

            migrationBuilder.DropIndex(
                name: "IX_Orders_SummaryToken",
                schema: "shop",
                table: "Orders");

            migrationBuilder.AlterColumn<string>(
                name: "SummaryToken",
                schema: "shop",
                table: "Orders",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<DateTime>(
                name: "SummaryTokenExpiresAt",
                schema: "shop",
                table: "Orders",
                type: "datetime2",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "datetime2");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_SummaryToken",
                schema: "shop",
                table: "Orders",
                column: "SummaryToken",
                unique: true,
                filter: "[SummaryToken] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Orders_PaymentStatus",
                schema: "shop",
                table: "Orders",
                sql: "[PaymentStatus] IN (1, 2, 3)");

            migrationBuilder.CreateTable(
                name: "PaymentLinks",
                schema: "checkout",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RotatedFromLinkId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentLinks", x => x.Id);
                    table.UniqueConstraint("AK_PaymentLinks_Id_MerchantId", x => new { x.Id, x.MerchantId });
                    table.CheckConstraint("CK_PaymentLinks_Status", "[Status] IN (1, 2, 3)");
                    table.ForeignKey(
                        name: "FK_PaymentLinks_Orders_OrderId_MerchantId",
                        columns: x => new { x.OrderId, x.MerchantId },
                        principalSchema: "shop",
                        principalTable: "Orders",
                        principalColumns: new[] { "Id", "MerchantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PaymentLinkReplays",
                schema: "checkout",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LinkId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Operation = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RequestHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    ProtectedRawToken = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentLinkReplays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentLinkReplays_Orders_OrderId_MerchantId",
                        columns: x => new { x.OrderId, x.MerchantId },
                        principalSchema: "shop",
                        principalTable: "Orders",
                        principalColumns: new[] { "Id", "MerchantId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PaymentLinkReplays_PaymentLinks_LinkId_MerchantId",
                        columns: x => new { x.LinkId, x.MerchantId },
                        principalSchema: "checkout",
                        principalTable: "PaymentLinks",
                        principalColumns: new[] { "Id", "MerchantId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentLinkReplays_ExpiresAt",
                schema: "checkout",
                table: "PaymentLinkReplays",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentLinkReplays_LinkId_MerchantId",
                schema: "checkout",
                table: "PaymentLinkReplays",
                columns: new[] { "LinkId", "MerchantId" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentLinkReplays_MerchantId_Operation_IdempotencyKey",
                schema: "checkout",
                table: "PaymentLinkReplays",
                columns: new[] { "MerchantId", "Operation", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentLinkReplays_OrderId_MerchantId",
                schema: "checkout",
                table: "PaymentLinkReplays",
                columns: new[] { "OrderId", "MerchantId" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentLinks_OrderId_MerchantId",
                schema: "checkout",
                table: "PaymentLinks",
                columns: new[] { "OrderId", "MerchantId" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentLinks_OrderId_Status",
                schema: "checkout",
                table: "PaymentLinks",
                columns: new[] { "OrderId", "Status" },
                unique: true,
                filter: "[Status] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentLinks_TokenHash",
                schema: "checkout",
                table: "PaymentLinks",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentLinkReplays",
                schema: "checkout");

            migrationBuilder.DropTable(
                name: "PaymentLinks",
                schema: "checkout");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Orders_PaymentStatus",
                schema: "shop",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_SummaryToken",
                schema: "shop",
                table: "Orders");

            migrationBuilder.AlterColumn<string>(
                name: "SummaryToken",
                schema: "shop",
                table: "Orders",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "SummaryTokenExpiresAt",
                schema: "shop",
                table: "Orders",
                type: "datetime2",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_SummaryToken",
                schema: "shop",
                table: "Orders",
                column: "SummaryToken",
                unique: true);

            migrationBuilder.DropColumn(
                name: "BusinessType",
                schema: "shop",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CreatedByAccountId",
                schema: "shop",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "FrozenAt",
                schema: "shop",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "IsFrozen",
                schema: "shop",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "IssuedAt",
                schema: "shop",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "OrderChargeAmount",
                schema: "shop",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "OrderChargeCurrency",
                schema: "shop",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "OrderDiscountAmount",
                schema: "shop",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "OrderDiscountCurrency",
                schema: "shop",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "OwnerBranchIdAtCreation",
                schema: "shop",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "OwnerSaleId",
                schema: "shop",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PaymentStatus",
                schema: "shop",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "SubtotalAmount",
                schema: "shop",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "SubtotalCurrency",
                schema: "shop",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "LineAmount",
                schema: "shop",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "LineCurrency",
                schema: "shop",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "TaxAmount",
                schema: "shop",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "TaxCurrency",
                schema: "shop",
                table: "OrderItems");

        }
    }
}
