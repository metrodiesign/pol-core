using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProducerAccountDetailsDropProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Person details (name, id, license, phone, photo) move onto producer.ProducerAccounts — a "tenant" is
            // the company/app, not the person, so person data belongs to the person's own account, never a
            // tenant-scoped profile. TenantUserProfiles becomes an empty FK shell and is dropped. Revoke its grants
            // before dropping (mirror DropRegistrationTicketsTable). Dev only — no row copy.
            migrationBuilder.Sql(
                "REVOKE SELECT, INSERT, UPDATE ON producer.TenantUserProfiles FROM pol_admin;");

            migrationBuilder.DropTable(
                name: "TenantUserProfiles",
                schema: "producer");

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                schema: "producer",
                table: "ProducerAccounts",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "FirstName",
                schema: "producer",
                table: "ProducerAccounts",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "IdNumber",
                schema: "producer",
                table: "ProducerAccounts",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastName",
                schema: "producer",
                table: "ProducerAccounts",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LicenseNumber",
                schema: "producer",
                table: "ProducerAccounts",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PersonType",
                schema: "producer",
                table: "ProducerAccounts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Phone",
                schema: "producer",
                table: "ProducerAccounts",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhotoContentType",
                schema: "producer",
                table: "ProducerAccounts",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhotoObjectKey",
                schema: "producer",
                table: "ProducerAccounts",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProducerCode",
                schema: "producer",
                table: "ProducerAccounts",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisplayName",
                schema: "producer",
                table: "ProducerAccounts");

            migrationBuilder.DropColumn(
                name: "FirstName",
                schema: "producer",
                table: "ProducerAccounts");

            migrationBuilder.DropColumn(
                name: "IdNumber",
                schema: "producer",
                table: "ProducerAccounts");

            migrationBuilder.DropColumn(
                name: "LastName",
                schema: "producer",
                table: "ProducerAccounts");

            migrationBuilder.DropColumn(
                name: "LicenseNumber",
                schema: "producer",
                table: "ProducerAccounts");

            migrationBuilder.DropColumn(
                name: "PersonType",
                schema: "producer",
                table: "ProducerAccounts");

            migrationBuilder.DropColumn(
                name: "Phone",
                schema: "producer",
                table: "ProducerAccounts");

            migrationBuilder.DropColumn(
                name: "PhotoContentType",
                schema: "producer",
                table: "ProducerAccounts");

            migrationBuilder.DropColumn(
                name: "PhotoObjectKey",
                schema: "producer",
                table: "ProducerAccounts");

            migrationBuilder.DropColumn(
                name: "ProducerCode",
                schema: "producer",
                table: "ProducerAccounts");

            migrationBuilder.CreateTable(
                name: "TenantUserProfiles",
                schema: "producer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IdNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LastName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LicenseNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PersonType = table.Column<int>(type: "int", nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    PhotoContentType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    PhotoObjectKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ProducerAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProducerCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantUserProfiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantUserProfiles_ProducerAccountId",
                schema: "producer",
                table: "TenantUserProfiles",
                column: "ProducerAccountId",
                unique: true);

            migrationBuilder.Sql(
                "GRANT SELECT, INSERT, UPDATE ON producer.TenantUserProfiles TO pol_admin;");
        }
    }
}
