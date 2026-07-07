using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderCheckoutSession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CheckoutSessionId",
                schema: "VCentralPay",
                table: "Orders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotificationRecipient",
                schema: "VCentralPay",
                table: "CheckoutSessions",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_CheckoutSessionId",
                schema: "VCentralPay",
                table: "Orders",
                column: "CheckoutSessionId",
                unique: true,
                filter: "[CheckoutSessionId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_CheckoutSessionId",
                schema: "VCentralPay",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CheckoutSessionId",
                schema: "VCentralPay",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "NotificationRecipient",
                schema: "VCentralPay",
                table: "CheckoutSessions");
        }
    }
}
