using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pol.Infrastructure.BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Task2PlatformRoleScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RoleScope",
                schema: "access",
                table: "PlatformAccessRoles",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_PlatformAccessRoles_RoleScope",
                schema: "access",
                table: "PlatformAccessRoles",
                sql: "[RoleScope] IN (1, 3)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PlatformAccessRoles_RoleScope",
                schema: "access",
                table: "PlatformAccessRoles");

            migrationBuilder.DropColumn(
                name: "RoleScope",
                schema: "access",
                table: "PlatformAccessRoles");
        }
    }
}
