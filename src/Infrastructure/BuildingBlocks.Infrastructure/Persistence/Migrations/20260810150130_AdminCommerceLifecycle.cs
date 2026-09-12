using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdminCommerceLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Version",
                schema: "txn",
                table: "PaymentSessions",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginatorId",
                schema: "shop",
                table: "Orders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                schema: "shop",
                table: "Orders",
                type: "datetime2",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE shop.Orders
                SET UpdatedAt = CreatedAt
                WHERE UpdatedAt IS NULL;
                """);

            migrationBuilder.AlterColumn<DateTime>(
                name: "UpdatedAt",
                schema: "shop",
                table: "Orders",
                type: "datetime2",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldNullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                schema: "shop",
                table: "Orders",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginatorId",
                schema: "shop",
                table: "Carts",
                type: "uniqueidentifier",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Version",
                schema: "txn",
                table: "PaymentSessions");

            migrationBuilder.DropColumn(
                name: "OriginatorId",
                schema: "shop",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                schema: "shop",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "Version",
                schema: "shop",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "OriginatorId",
                schema: "shop",
                table: "Carts");
        }
    }
}
