using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Tier0EmployeeProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EmployeeId",
                schema: "admin",
                table: "Users",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FirstName",
                schema: "admin",
                table: "Users",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastName",
                schema: "admin",
                table: "Users",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LegacyKey",
                schema: "cfg",
                table: "Offices",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LegacyKey",
                schema: "cfg",
                table: "Divisions",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_EmployeeId",
                schema: "admin",
                table: "Users",
                column: "EmployeeId",
                unique: true,
                filter: "[EmployeeId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Offices_LegacyKey",
                schema: "cfg",
                table: "Offices",
                column: "LegacyKey",
                unique: true,
                filter: "[LegacyKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Divisions_LegacyKey",
                schema: "cfg",
                table: "Divisions",
                column: "LegacyKey",
                unique: true,
                filter: "[LegacyKey] IS NOT NULL");

            // tier0-graph-employee-profile REQ-8.11/8.12: the HR mirror tables (cfg.VibEmp / cfg.branch) are
            // operator-loaded, never created or altered by any migration (REQ-8.7); grant pol_app read access only
            // when they exist so a database without them still migrates. Down() leaves the grant in place.
            migrationBuilder.Sql(
                """
                IF OBJECT_ID(N'cfg.VibEmp', N'U') IS NOT NULL EXEC(N'GRANT SELECT ON cfg.VibEmp TO pol_app');
                IF OBJECT_ID(N'cfg.branch', N'U') IS NOT NULL EXEC(N'GRANT SELECT ON cfg.branch TO pol_app');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_EmployeeId",
                schema: "admin",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Offices_LegacyKey",
                schema: "cfg",
                table: "Offices");

            migrationBuilder.DropIndex(
                name: "IX_Divisions_LegacyKey",
                schema: "cfg",
                table: "Divisions");

            migrationBuilder.DropColumn(
                name: "EmployeeId",
                schema: "admin",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "FirstName",
                schema: "admin",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LastName",
                schema: "admin",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LegacyKey",
                schema: "cfg",
                table: "Offices");

            migrationBuilder.DropColumn(
                name: "LegacyKey",
                schema: "cfg",
                table: "Divisions");
        }
    }
}
