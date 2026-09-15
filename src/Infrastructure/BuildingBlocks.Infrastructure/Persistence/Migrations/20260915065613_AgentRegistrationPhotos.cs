using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentRegistrationPhotos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "KycPhotoContentType",
                schema: "acct",
                table: "AgentRegistrations",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KycPhotoObjectKey",
                schema: "acct",
                table: "AgentRegistrations",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhotoContentType",
                schema: "acct",
                table: "AgentRegistrations",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhotoObjectKey",
                schema: "acct",
                table: "AgentRegistrations",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KycPhotoContentType",
                schema: "acct",
                table: "AgentRegistrationAttempts",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KycPhotoObjectKey",
                schema: "acct",
                table: "AgentRegistrationAttempts",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhotoContentType",
                schema: "acct",
                table: "AgentRegistrationAttempts",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhotoObjectKey",
                schema: "acct",
                table: "AgentRegistrationAttempts",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KycPhotoContentType",
                schema: "acct",
                table: "AgentRegistrations");

            migrationBuilder.DropColumn(
                name: "KycPhotoObjectKey",
                schema: "acct",
                table: "AgentRegistrations");

            migrationBuilder.DropColumn(
                name: "PhotoContentType",
                schema: "acct",
                table: "AgentRegistrations");

            migrationBuilder.DropColumn(
                name: "PhotoObjectKey",
                schema: "acct",
                table: "AgentRegistrations");

            migrationBuilder.DropColumn(
                name: "KycPhotoContentType",
                schema: "acct",
                table: "AgentRegistrationAttempts");

            migrationBuilder.DropColumn(
                name: "KycPhotoObjectKey",
                schema: "acct",
                table: "AgentRegistrationAttempts");

            migrationBuilder.DropColumn(
                name: "PhotoContentType",
                schema: "acct",
                table: "AgentRegistrationAttempts");

            migrationBuilder.DropColumn(
                name: "PhotoObjectKey",
                schema: "acct",
                table: "AgentRegistrationAttempts");
        }
    }
}
