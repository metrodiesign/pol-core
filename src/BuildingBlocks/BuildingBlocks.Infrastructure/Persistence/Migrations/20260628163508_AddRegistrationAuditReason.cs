using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRegistrationAuditReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Reason",
                schema: "VCentralPay",
                table: "RegistrationAudits",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Reason",
                schema: "VCentralPay",
                table: "RegistrationAudits");
        }
    }
}
