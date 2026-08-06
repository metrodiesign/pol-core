using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// products-external-source-of-truth REQ-6.1/6.2/6.3/2.2: the identifier-column cutover. The catalogue mirror
    /// <c>shop.Products</c> is retired, and every buy-path line stops carrying the surrogate <c>ProductId</c> and
    /// carries <c>DocumentNo</c> instead. REQ-6.1 requires the <c>DROP TABLE</c> to ride in the SAME migration as
    /// the column change, so it does.
    /// <para>HAND-EDITED, deliberately — do NOT regenerate <c>Up</c> from the scaffolder. The scaffolder emits a
    /// bare AddColumn(DocumentNo NOT NULL default "") + DropColumn(ProductId), which would blank every existing
    /// cart line's document. <c>shop.CartItems</c> is the only buy-path table that did not already carry
    /// <c>DocumentNo</c> (checkout/order items got it earlier), so its three new columns are added NULLABLE, then
    /// backfilled from <c>shop.Products</c> via the surrogate FK-less join BEFORE that table is dropped (REQ-6.2),
    /// rows that no longer resolve to a product are deleted rather than left blank (REQ-6.3), and only then are the
    /// columns tightened to NOT NULL. <c>Down</c> restores the structure (empty <c>shop.Products</c>, the
    /// <c>ProductId</c> columns back as NOT NULL) but never the data (REQ-6.8).</para>
    /// </summary>
    public partial class DropProductCatalogueCutoverDocumentNo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Step 3 (design.md migration table) — add the three new columns NULLABLE so existing rows survive the
            // add; they are tightened to NOT NULL in step 6, after the backfill.
            migrationBuilder.AddColumn<string>(
                name: "DocumentNo",
                schema: "shop",
                table: "CartItems",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SaleCode",
                schema: "shop",
                table: "CartItems",
                type: "varchar(20)",
                unicode: false,
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProductGroup",
                schema: "shop",
                table: "CartItems",
                type: "varchar(10)",
                unicode: false,
                maxLength: 10,
                nullable: true);

            // Step 4 — backfill from the catalogue mirror while it still exists (REQ-6.2). The join is on the old
            // surrogate ProductId; there is no FK, but the id was minted from that table so it resolves.
            migrationBuilder.Sql(
                """
                UPDATE ci
                SET ci.DocumentNo = p.DocumentNo,
                    ci.SaleCode = p.SaleCode,
                    ci.ProductGroup = p.ProductGroup
                FROM shop.CartItems ci
                INNER JOIN shop.Products p ON p.Id = ci.ProductId;
                """);

            // Step 5 — a cart line whose product no longer exists cannot be given a document, so it is dropped
            // rather than left with NULLs (REQ-6.3).
            migrationBuilder.Sql("DELETE FROM shop.CartItems WHERE DocumentNo IS NULL;");

            // Step 6 — now every surviving row has values, tighten to NOT NULL (REQ-6.2).
            migrationBuilder.AlterColumn<string>(
                name: "DocumentNo",
                schema: "shop",
                table: "CartItems",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(150)",
                oldMaxLength: 150,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "SaleCode",
                schema: "shop",
                table: "CartItems",
                type: "varchar(20)",
                unicode: false,
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(20)",
                oldUnicode: false,
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ProductGroup",
                schema: "shop",
                table: "CartItems",
                type: "varchar(10)",
                unicode: false,
                maxLength: 10,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(10)",
                oldUnicode: false,
                oldMaxLength: 10,
                oldNullable: true);

            // Steps 7-9 — drop the surrogate identifier from every buy-path line (REQ-2.2).
            migrationBuilder.DropColumn(
                name: "ProductId",
                schema: "shop",
                table: "CartItems");

            migrationBuilder.DropColumn(
                name: "ProductId",
                schema: "shop",
                table: "CheckoutSessionItems");

            migrationBuilder.DropColumn(
                name: "ProductId",
                schema: "shop",
                table: "OrderItems");

            // Step 11 — retire the catalogue mirror. No separate REVOKE: the GRANTs on this object vanish with it
            // when it is dropped (design.md — REQ-6.6). There is no FK into it, so this does not cascade.
            migrationBuilder.DropTable(
                name: "Products",
                schema: "shop");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverses the STRUCTURE only, never the data (REQ-6.8): the ProductId columns come back NOT NULL as
            // they were (Guid.Empty for existing rows), and shop.Products is recreated empty.
            migrationBuilder.DropColumn(
                name: "DocumentNo",
                schema: "shop",
                table: "CartItems");

            migrationBuilder.DropColumn(
                name: "SaleCode",
                schema: "shop",
                table: "CartItems");

            migrationBuilder.DropColumn(
                name: "ProductGroup",
                schema: "shop",
                table: "CartItems");

            migrationBuilder.AddColumn<Guid>(
                name: "ProductId",
                schema: "shop",
                table: "OrderItems",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "ProductId",
                schema: "shop",
                table: "CheckoutSessionItems",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "ProductId",
                schema: "shop",
                table: "CartItems",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "Products",
                schema: "shop",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationNumber = table.Column<string>(type: "varchar(150)", unicode: false, maxLength: 150, nullable: true),
                    BrokerCode = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: true),
                    BrokerName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CommissionAmount = table.Column<decimal>(type: "decimal(19,2)", precision: 19, scale: 2, nullable: true),
                    CommissionPercent = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: true),
                    DocumentNo = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    DocumentType = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    EndDate = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: true),
                    EndorsementNumber = table.Column<string>(type: "varchar(150)", unicode: false, maxLength: 150, nullable: true),
                    LicensePlateNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    NetPremium = table.Column<decimal>(type: "decimal(19,2)", precision: 19, scale: 2, nullable: true),
                    PaidDate = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: true),
                    PaymentStatus = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    PolicyBranch = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    PolicyNumber = table.Column<string>(type: "varchar(150)", unicode: false, maxLength: 150, nullable: true),
                    PolicySequenceNo = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    PolicyType = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    PolicyYear = table.Column<string>(type: "varchar(2)", unicode: false, maxLength: 2, nullable: true),
                    PreviousPolicyNumber = table.Column<string>(type: "varchar(150)", unicode: false, maxLength: 150, nullable: true),
                    ProductGroup = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    ReferenceBranch = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: true),
                    ReferenceNo = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    ReferencePre = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: true),
                    ReferenceYear = table.Column<string>(type: "varchar(2)", unicode: false, maxLength: 2, nullable: true),
                    SaleCode = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    SaleFullName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ShowName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SoldOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Stamp = table.Column<decimal>(type: "decimal(19,2)", precision: 19, scale: 2, nullable: true),
                    StartDate = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: true),
                    TaxVat = table.Column<decimal>(type: "decimal(19,2)", precision: 19, scale: 2, nullable: true),
                    TotalPremium = table.Column<decimal>(type: "decimal(19,2)", precision: 19, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.Id);
                });

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
    }
}
