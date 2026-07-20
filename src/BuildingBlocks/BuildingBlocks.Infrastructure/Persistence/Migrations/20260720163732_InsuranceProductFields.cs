using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InsuranceProductFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CoverageDurationDays",
                schema: "shop",
                table: "Products",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "InsurerName",
                schema: "shop",
                table: "Products",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "SumInsuredAmount",
                schema: "shop",
                table: "Products",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "SumInsuredCurrency",
                schema: "shop",
                table: "Products",
                type: "char(3)",
                unicode: false,
                fixedLength: true,
                maxLength: 3,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoverageDurationDays",
                schema: "shop",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "InsurerName",
                schema: "shop",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "SumInsuredAmount",
                schema: "shop",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "SumInsuredCurrency",
                schema: "shop",
                table: "Products");
        }
    }
}
