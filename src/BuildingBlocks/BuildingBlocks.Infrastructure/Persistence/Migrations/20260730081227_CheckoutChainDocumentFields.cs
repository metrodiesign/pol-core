using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CheckoutChainDocumentFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoverageDurationDays",
                schema: "shop",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "InsurerName",
                schema: "shop",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "SumInsuredAmount",
                schema: "shop",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "SumInsuredCurrency",
                schema: "shop",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "CoverageDurationDays",
                schema: "shop",
                table: "CheckoutSessionItems");

            migrationBuilder.DropColumn(
                name: "InsurerName",
                schema: "shop",
                table: "CheckoutSessionItems");

            migrationBuilder.DropColumn(
                name: "SumInsuredAmount",
                schema: "shop",
                table: "CheckoutSessionItems");

            migrationBuilder.DropColumn(
                name: "SumInsuredCurrency",
                schema: "shop",
                table: "CheckoutSessionItems");

            migrationBuilder.AddColumn<string>(
                name: "DocumentNo",
                schema: "shop",
                table: "OrderItems",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DocumentType",
                schema: "shop",
                table: "OrderItems",
                type: "varchar(20)",
                unicode: false,
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "EndDate",
                schema: "shop",
                table: "OrderItems",
                type: "datetime2(0)",
                precision: 0,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PolicyNumber",
                schema: "shop",
                table: "OrderItems",
                type: "varchar(150)",
                unicode: false,
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProductGroup",
                schema: "shop",
                table: "OrderItems",
                type: "varchar(10)",
                unicode: false,
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "StartDate",
                schema: "shop",
                table: "OrderItems",
                type: "datetime2(0)",
                precision: 0,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DocumentNo",
                schema: "shop",
                table: "CheckoutSessionItems",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DocumentType",
                schema: "shop",
                table: "CheckoutSessionItems",
                type: "varchar(20)",
                unicode: false,
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "EndDate",
                schema: "shop",
                table: "CheckoutSessionItems",
                type: "datetime2(0)",
                precision: 0,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PolicyNumber",
                schema: "shop",
                table: "CheckoutSessionItems",
                type: "varchar(150)",
                unicode: false,
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProductGroup",
                schema: "shop",
                table: "CheckoutSessionItems",
                type: "varchar(10)",
                unicode: false,
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "StartDate",
                schema: "shop",
                table: "CheckoutSessionItems",
                type: "datetime2(0)",
                precision: 0,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DocumentNo",
                schema: "shop",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "DocumentType",
                schema: "shop",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "EndDate",
                schema: "shop",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "PolicyNumber",
                schema: "shop",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "ProductGroup",
                schema: "shop",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "StartDate",
                schema: "shop",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "DocumentNo",
                schema: "shop",
                table: "CheckoutSessionItems");

            migrationBuilder.DropColumn(
                name: "DocumentType",
                schema: "shop",
                table: "CheckoutSessionItems");

            migrationBuilder.DropColumn(
                name: "EndDate",
                schema: "shop",
                table: "CheckoutSessionItems");

            migrationBuilder.DropColumn(
                name: "PolicyNumber",
                schema: "shop",
                table: "CheckoutSessionItems");

            migrationBuilder.DropColumn(
                name: "ProductGroup",
                schema: "shop",
                table: "CheckoutSessionItems");

            migrationBuilder.DropColumn(
                name: "StartDate",
                schema: "shop",
                table: "CheckoutSessionItems");

            migrationBuilder.AddColumn<int>(
                name: "CoverageDurationDays",
                schema: "shop",
                table: "OrderItems",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "InsurerName",
                schema: "shop",
                table: "OrderItems",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "SumInsuredAmount",
                schema: "shop",
                table: "OrderItems",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "SumInsuredCurrency",
                schema: "shop",
                table: "OrderItems",
                type: "char(3)",
                unicode: false,
                fixedLength: true,
                maxLength: 3,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "CoverageDurationDays",
                schema: "shop",
                table: "CheckoutSessionItems",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "InsurerName",
                schema: "shop",
                table: "CheckoutSessionItems",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "SumInsuredAmount",
                schema: "shop",
                table: "CheckoutSessionItems",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "SumInsuredCurrency",
                schema: "shop",
                table: "CheckoutSessionItems",
                type: "char(3)",
                unicode: false,
                fixedLength: true,
                maxLength: 3,
                nullable: false,
                defaultValue: "");
        }
    }
}
