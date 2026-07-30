using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Turns <c>shop.Products</c> into the central document catalogue: <c>MerchantId</c> is dropped (user
    /// decision — the SP guide §5.2 read model has no merchant field and every merchant sells from the same
    /// catalogue), so the two merchant-prefixed indexes are replaced by <c>IX_Products_SaleCode_PaymentStatus</c>
    /// and a globally unique <c>IX_Products_DocumentNo</c>. From here on, a request is scoped by the mandatory
    /// <c>SaleCode</c> filter, not by a tenant key.
    /// <para>
    /// <c>Down()</c> re-adds the column with an all-zero default: the original owner per row cannot be
    /// recovered once dropped, so a rollback restores the SHAPE, not the data.
    /// </para>
    /// </summary>
    public partial class ProductsCentralCatalogue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DocumentNo was only unique per merchant, so two merchants may legitimately hold the same number
            // today. Fail with the actual duplicates instead of a bare index-creation error further down.
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM shop.Products GROUP BY DocumentNo HAVING COUNT(*) > 1)
                BEGIN
                    -- THROW takes a message VARIABLE, not an expression — concatenate first.
                    DECLARE @dupes nvarchar(2000) = (
                        SELECT STRING_AGG(CONVERT(nvarchar(150), DocumentNo), N', ') FROM (
                            SELECT TOP (10) DocumentNo FROM shop.Products
                            GROUP BY DocumentNo HAVING COUNT(*) > 1) d);
                    DECLARE @msg nvarchar(2400) = N'ProductsCentralCatalogue: DocumentNo is about to become globally unique but duplicates exist across merchants. Resolve them first. Sample: ' + @dupes;
                    THROW 51000, @msg, 1;
                END
                """);

            migrationBuilder.DropIndex(
                name: "IX_Products_MerchantId_DocumentNo",
                schema: "shop",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_MerchantId_PaymentStatus",
                schema: "shop",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "MerchantId",
                schema: "shop",
                table: "Products");

            migrationBuilder.CreateIndex(
                name: "IX_Products_DocumentNo",
                schema: "shop",
                table: "Products",
                column: "DocumentNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_SaleCode_PaymentStatus",
                schema: "shop",
                table: "Products",
                columns: new[] { "SaleCode", "PaymentStatus" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Products_DocumentNo",
                schema: "shop",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_SaleCode_PaymentStatus",
                schema: "shop",
                table: "Products");

            migrationBuilder.AddColumn<Guid>(
                name: "MerchantId",
                schema: "shop",
                table: "Products",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_Products_MerchantId_DocumentNo",
                schema: "shop",
                table: "Products",
                columns: new[] { "MerchantId", "DocumentNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_MerchantId_PaymentStatus",
                schema: "shop",
                table: "Products",
                columns: new[] { "MerchantId", "PaymentStatus" });
        }
    }
}
