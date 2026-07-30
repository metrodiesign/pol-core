using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Aligns <c>shop.Products</c> with <c>docs/reference/vcentralpay-sp-quick-reference.pdf</c> §5.2:
    /// drops the eight columns the source contract has no field for, renames the four premium
    /// <c>*Amount</c> columns to their §5.2 names, and narrows every money column to
    /// <c>decimal(19,2)</c>. The scaffolder emitted drop+add for the four renames (it cannot infer a
    /// rename); replaced by <c>RenameColumn</c> so existing rows keep their premiums. Narrowing the
    /// scale is guarded by a precondition that aborts if any stored premium has more than 2 decimal
    /// places, so an upgrade can never round money silently.
    /// No <c>DropTable</c>, so the <c>pol_app</c> grants stay in place.
    /// </summary>
    public partial class ProductsSp52Alignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Products_MerchantId_IsActive",
                schema: "shop",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "BranchCode",
                schema: "shop",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "IsActive",
                schema: "shop",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                schema: "shop",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "TotalPremiumCurrency",
                schema: "shop",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "NetPremiumCurrency",
                schema: "shop",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "StampCurrency",
                schema: "shop",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "TaxVatCurrency",
                schema: "shop",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "CommissionCurrency",
                schema: "shop",
                table: "Products");

            migrationBuilder.RenameColumn(
                name: "TotalPremiumAmount",
                schema: "shop",
                table: "Products",
                newName: "TotalPremium");

            migrationBuilder.RenameColumn(
                name: "NetPremiumAmount",
                schema: "shop",
                table: "Products",
                newName: "NetPremium");

            migrationBuilder.RenameColumn(
                name: "StampAmount",
                schema: "shop",
                table: "Products",
                newName: "Stamp");

            migrationBuilder.RenameColumn(
                name: "TaxVatAmount",
                schema: "shop",
                table: "Products",
                newName: "TaxVat");

            // Narrowing decimal(19,4) -> decimal(19,2) rounds silently in SQL Server, so a database
            // upgraded from the Money model could have its premiums (and therefore later charge
            // amounts) altered without anyone approving the conversion. Abort instead: a row with a
            // 3rd/4th decimal place is a business decision, not a migration side effect.
            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1 FROM shop.Products
                    WHERE TotalPremium     <> ROUND(TotalPremium, 2)
                       OR NetPremium       <> ROUND(NetPremium, 2)
                       OR Stamp            <> ROUND(Stamp, 2)
                       OR TaxVat           <> ROUND(TaxVat, 2)
                       OR CommissionAmount <> ROUND(CommissionAmount, 2))
                    THROW 51000, N'ProductsSp52Alignment: shop.Products holds premium values with more than 2 decimal places. Narrowing to decimal(19,2) would silently round money — convert those rows deliberately, then re-run this migration.', 1;
                """);

            migrationBuilder.AlterColumn<decimal>(
                name: "TotalPremium",
                schema: "shop",
                table: "Products",
                type: "decimal(19,2)",
                precision: 19,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(19,4)",
                oldPrecision: 19,
                oldScale: 4);

            migrationBuilder.AlterColumn<decimal>(
                name: "NetPremium",
                schema: "shop",
                table: "Products",
                type: "decimal(19,2)",
                precision: 19,
                scale: 2,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(19,4)",
                oldPrecision: 19,
                oldScale: 4,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "Stamp",
                schema: "shop",
                table: "Products",
                type: "decimal(19,2)",
                precision: 19,
                scale: 2,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(19,4)",
                oldPrecision: 19,
                oldScale: 4,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "TaxVat",
                schema: "shop",
                table: "Products",
                type: "decimal(19,2)",
                precision: 19,
                scale: 2,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(19,4)",
                oldPrecision: 19,
                oldScale: 4,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "CommissionAmount",
                schema: "shop",
                table: "Products",
                type: "decimal(19,2)",
                precision: 19,
                scale: 2,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(19,4)",
                oldPrecision: 19,
                oldScale: 4,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "TotalPremium",
                schema: "shop",
                table: "Products",
                newName: "TotalPremiumAmount");

            migrationBuilder.RenameColumn(
                name: "NetPremium",
                schema: "shop",
                table: "Products",
                newName: "NetPremiumAmount");

            migrationBuilder.RenameColumn(
                name: "Stamp",
                schema: "shop",
                table: "Products",
                newName: "StampAmount");

            migrationBuilder.RenameColumn(
                name: "TaxVat",
                schema: "shop",
                table: "Products",
                newName: "TaxVatAmount");

            migrationBuilder.AlterColumn<decimal>(
                name: "TotalPremiumAmount",
                schema: "shop",
                table: "Products",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(19,2)",
                oldPrecision: 19,
                oldScale: 2);

            migrationBuilder.AlterColumn<decimal>(
                name: "NetPremiumAmount",
                schema: "shop",
                table: "Products",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(19,2)",
                oldPrecision: 19,
                oldScale: 2,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "StampAmount",
                schema: "shop",
                table: "Products",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(19,2)",
                oldPrecision: 19,
                oldScale: 2,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "TaxVatAmount",
                schema: "shop",
                table: "Products",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(19,2)",
                oldPrecision: 19,
                oldScale: 2,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "CommissionAmount",
                schema: "shop",
                table: "Products",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(19,2)",
                oldPrecision: 19,
                oldScale: 2,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BranchCode",
                schema: "shop",
                table: "Products",
                type: "varchar(3)",
                unicode: false,
                maxLength: 3,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                schema: "shop",
                table: "Products",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                schema: "shop",
                table: "Products",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "TotalPremiumCurrency",
                schema: "shop",
                table: "Products",
                type: "char(3)",
                unicode: false,
                fixedLength: true,
                maxLength: 3,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "NetPremiumCurrency",
                schema: "shop",
                table: "Products",
                type: "char(3)",
                unicode: false,
                fixedLength: true,
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StampCurrency",
                schema: "shop",
                table: "Products",
                type: "char(3)",
                unicode: false,
                fixedLength: true,
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxVatCurrency",
                schema: "shop",
                table: "Products",
                type: "char(3)",
                unicode: false,
                fixedLength: true,
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CommissionCurrency",
                schema: "shop",
                table: "Products",
                type: "char(3)",
                unicode: false,
                fixedLength: true,
                maxLength: 3,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_MerchantId_IsActive",
                schema: "shop",
                table: "Products",
                columns: new[] { "MerchantId", "IsActive" });
        }
    }
}
