using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropRegistrationTicketsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The wire ticket is now stateless (signed+time-limited token, no server row); the account's unique
            // (Subject) index is the duplicate-registration guard. Revoke grants before dropping the table.
            migrationBuilder.Sql(
                "REVOKE SELECT, INSERT, UPDATE ON producer.RegistrationTickets FROM pol_admin;");

            migrationBuilder.DropTable(
                name: "RegistrationTickets",
                schema: "producer");

            migrationBuilder.AlterColumn<string>(
                name: "LastName",
                schema: "producer",
                table: "TenantUserProfiles",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "FirstName",
                schema: "producer",
                table: "TenantUserProfiles",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "LastName",
                schema: "producer",
                table: "TenantUserProfiles",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<string>(
                name: "FirstName",
                schema: "producer",
                table: "TenantUserProfiles",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200);

            migrationBuilder.CreateTable(
                name: "RegistrationTickets",
                schema: "producer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    HostedDomain = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Purpose = table.Column<int>(type: "int", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    UsedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrationTickets", x => x.Id);
                });

            migrationBuilder.Sql(
                "GRANT SELECT, INSERT, UPDATE ON producer.RegistrationTickets TO pol_admin;");
        }
    }
}
