using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdminConsoleResourceVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Version",
                schema: "admin",
                table: "Users",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                schema: "iam",
                table: "Roles",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                schema: "cfg",
                table: "Positions",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                schema: "cfg",
                table: "Offices",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                schema: "cfg",
                table: "Levels",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                schema: "cfg",
                table: "Divisions",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Version",
                schema: "admin",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Version",
                schema: "iam",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "Version",
                schema: "cfg",
                table: "Positions");

            migrationBuilder.DropColumn(
                name: "Version",
                schema: "cfg",
                table: "Offices");

            migrationBuilder.DropColumn(
                name: "Version",
                schema: "cfg",
                table: "Levels");

            migrationBuilder.DropColumn(
                name: "Version",
                schema: "cfg",
                table: "Divisions");
        }
    }
}
