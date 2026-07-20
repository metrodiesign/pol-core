using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrderLinesAndCheckoutSessionLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_Orders_Id_MerchantId",
                schema: "shop",
                table: "Orders",
                columns: new[] { "Id", "MerchantId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_CheckoutSessions_Id_MerchantId",
                schema: "shop",
                table: "CheckoutSessions",
                columns: new[] { "Id", "MerchantId" });

            migrationBuilder.CreateTable(
                name: "CheckoutSessionLines",
                schema: "shop",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    CoverageDurationDays = table.Column<int>(type: "int", nullable: false),
                    InsurerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    InsuredFirstName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    InsuredLastName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    InsuredIdNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    InsuredDateOfBirth = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SumInsuredAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    SumInsuredCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    UnitPriceAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    UnitPriceCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CheckoutSessionLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CheckoutSessionLines_CheckoutSessions_SessionId_MerchantId",
                        columns: x => new { x.SessionId, x.MerchantId },
                        principalSchema: "shop",
                        principalTable: "CheckoutSessions",
                        principalColumns: new[] { "Id", "MerchantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OrderLines",
                schema: "shop",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    CoverageDurationDays = table.Column<int>(type: "int", nullable: false),
                    InsurerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    InsuredFirstName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    InsuredLastName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    InsuredIdNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    InsuredDateOfBirth = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SumInsuredAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    SumInsuredCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    UnitPriceAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    UnitPriceCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrderLines_Orders_OrderId_MerchantId",
                        columns: x => new { x.OrderId, x.MerchantId },
                        principalSchema: "shop",
                        principalTable: "Orders",
                        principalColumns: new[] { "Id", "MerchantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CheckoutSessionLines_SessionId_MerchantId",
                schema: "shop",
                table: "CheckoutSessionLines",
                columns: new[] { "SessionId", "MerchantId" });

            migrationBuilder.CreateIndex(
                name: "IX_OrderLines_OrderId_MerchantId",
                schema: "shop",
                table: "OrderLines",
                columns: new[] { "OrderId", "MerchantId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CheckoutSessionLines",
                schema: "shop");

            migrationBuilder.DropTable(
                name: "OrderLines",
                schema: "shop");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Orders_Id_MerchantId",
                schema: "shop",
                table: "Orders");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_CheckoutSessions_Id_MerchantId",
                schema: "shop",
                table: "CheckoutSessions");
        }
    }
}
