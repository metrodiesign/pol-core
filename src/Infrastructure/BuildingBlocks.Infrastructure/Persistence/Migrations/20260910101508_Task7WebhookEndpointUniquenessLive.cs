using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Task7WebhookEndpointUniquenessLive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WebhookEndpoints_MerchantId_Enabled",
                schema: "admin",
                table: "WebhookEndpoints");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookEndpoints_MerchantId",
                schema: "admin",
                table: "WebhookEndpoints",
                column: "MerchantId",
                unique: true,
                filter: "[Enabled] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WebhookEndpoints_MerchantId",
                schema: "admin",
                table: "WebhookEndpoints");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookEndpoints_MerchantId_Enabled",
                schema: "admin",
                table: "WebhookEndpoints",
                columns: new[] { "MerchantId", "Enabled" });
        }
    }
}
