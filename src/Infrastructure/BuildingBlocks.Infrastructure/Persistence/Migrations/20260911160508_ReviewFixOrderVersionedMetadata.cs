using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReviewFixOrderVersionedMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Metadata",
                schema: "shop",
                table: "Orders",
                type: "json",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestMetadata",
                schema: "shop",
                table: "OrderItems",
                type: "json",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Metadata",
                schema: "shop",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "RequestMetadata",
                schema: "shop",
                table: "OrderItems");
        }
    }
}
