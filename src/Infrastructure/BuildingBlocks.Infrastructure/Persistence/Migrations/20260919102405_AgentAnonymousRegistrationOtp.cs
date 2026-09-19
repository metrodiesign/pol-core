using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentAnonymousRegistrationOtp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                SET QUOTED_IDENTIFIER ON;
                IF EXISTS (
                    SELECT 1 FROM acct.AgentRegistrations
                    GROUP BY MerchantId, LOWER(TRIM(Email)) HAVING COUNT(*) > 1
                )
                    THROW 51000, 'Agent registration email duplicates require operator resolution before migration.', 1;
                """);

            migrationBuilder.DropIndex(
                name: "IX_AgentRegistrations_Provider_TenantId_ExternalUserId",
                schema: "acct",
                table: "AgentRegistrations");

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                schema: "acct",
                table: "RegistrationSessions",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128);

            migrationBuilder.AlterColumn<string>(
                name: "Provider",
                schema: "acct",
                table: "RegistrationSessions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<string>(
                name: "ExternalUserId",
                schema: "acct",
                table: "RegistrationSessions",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256);

            migrationBuilder.AddColumn<Guid>(
                name: "RegistrationId",
                schema: "acct",
                table: "RegistrationSessions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                schema: "acct",
                table: "AgentRegistrations",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128);

            migrationBuilder.AlterColumn<string>(
                name: "Provider",
                schema: "acct",
                table: "AgentRegistrations",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<string>(
                name: "ExternalUserId",
                schema: "acct",
                table: "AgentRegistrations",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256);

            migrationBuilder.AddColumn<Guid>(
                name: "AccountId",
                schema: "acct",
                table: "AgentRegistrations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmailNormalized",
                schema: "acct",
                table: "AgentRegistrations",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "PhoneVerifiedAt",
                schema: "acct",
                table: "AgentRegistrations",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhoneVerifiedNumber",
                schema: "acct",
                table: "AgentRegistrations",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                schema: "acct",
                table: "AgentRegistrationAttempts",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128);

            migrationBuilder.AlterColumn<string>(
                name: "Provider",
                schema: "acct",
                table: "AgentRegistrationAttempts",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<string>(
                name: "ExternalUserId",
                schema: "acct",
                table: "AgentRegistrationAttempts",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256);

            migrationBuilder.CreateTable(
                name: "ContactVerifications",
                schema: "acct",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RegistrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Recipient = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    CodeHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConfirmedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContactVerifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContactVerifications_AgentRegistrations_RegistrationId",
                        column: x => x.RegistrationId,
                        principalSchema: "acct",
                        principalTable: "AgentRegistrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Keep backfill in its own EF command: the generated deployment script separates commands with GO.
            migrationBuilder.Sql("""
                UPDATE acct.AgentRegistrations SET EmailNormalized = LOWER(TRIM(Email));
                UPDATE registration SET AccountId = agent.AccountId
                FROM acct.AgentRegistrations registration
                INNER JOIN acct.AgentRegistrationAttempts attempt ON attempt.Id = registration.CurrentAttemptId
                    AND attempt.RegistrationId = registration.Id
                INNER JOIN acct.Agents agent ON agent.MerchantId = registration.MerchantId
                    AND agent.SaleId = attempt.SaleId
                WHERE registration.Status = 3;
                IF EXISTS (SELECT 1 FROM acct.AgentRegistrations WHERE Status = 3 AND AccountId IS NULL)
                    THROW 51001, 'Approved agent registration account backfill is incomplete.', 1;
                IF DATABASE_PRINCIPAL_ID(N'pol_app') IS NOT NULL
                    GRANT SELECT, INSERT, UPDATE ON OBJECT::acct.ContactVerifications TO pol_app;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_RegistrationSessions_RegistrationId",
                schema: "acct",
                table: "RegistrationSessions",
                column: "RegistrationId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRegistrations_AccountId",
                schema: "acct",
                table: "AgentRegistrations",
                column: "AccountId",
                unique: true,
                filter: "[AccountId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRegistrations_MerchantId_EmailNormalized",
                schema: "acct",
                table: "AgentRegistrations",
                columns: new[] { "MerchantId", "EmailNormalized" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentRegistrations_Provider_TenantId_ExternalUserId",
                schema: "acct",
                table: "AgentRegistrations",
                columns: new[] { "Provider", "TenantId", "ExternalUserId" },
                unique: true,
                filter: "[Provider] IS NOT NULL AND [TenantId] IS NOT NULL AND [ExternalUserId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ContactVerifications_Recipient_CreatedAt",
                schema: "acct",
                table: "ContactVerifications",
                columns: new[] { "Recipient", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ContactVerifications_RegistrationId_CreatedAt",
                schema: "acct",
                table: "ContactVerifications",
                columns: new[] { "RegistrationId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM acct.AgentRegistrations WHERE Provider IS NULL OR TenantId IS NULL OR ExternalUserId IS NULL)
                    OR EXISTS (SELECT 1 FROM acct.AgentRegistrationAttempts WHERE Provider IS NULL OR TenantId IS NULL OR ExternalUserId IS NULL)
                    OR EXISTS (SELECT 1 FROM acct.RegistrationSessions WHERE Provider IS NULL OR TenantId IS NULL OR ExternalUserId IS NULL)
                    THROW 51002, 'Anonymous registration data prevents rollback; restore a pre-migration backup or roll forward.', 1;
                """);

            migrationBuilder.DropTable(
                name: "ContactVerifications",
                schema: "acct");

            migrationBuilder.DropIndex(
                name: "IX_RegistrationSessions_RegistrationId",
                schema: "acct",
                table: "RegistrationSessions");

            migrationBuilder.DropIndex(
                name: "IX_AgentRegistrations_AccountId",
                schema: "acct",
                table: "AgentRegistrations");

            migrationBuilder.DropIndex(
                name: "IX_AgentRegistrations_MerchantId_EmailNormalized",
                schema: "acct",
                table: "AgentRegistrations");

            migrationBuilder.DropIndex(
                name: "IX_AgentRegistrations_Provider_TenantId_ExternalUserId",
                schema: "acct",
                table: "AgentRegistrations");

            migrationBuilder.DropColumn(
                name: "RegistrationId",
                schema: "acct",
                table: "RegistrationSessions");

            migrationBuilder.DropColumn(
                name: "AccountId",
                schema: "acct",
                table: "AgentRegistrations");

            migrationBuilder.DropColumn(
                name: "EmailNormalized",
                schema: "acct",
                table: "AgentRegistrations");

            migrationBuilder.DropColumn(
                name: "PhoneVerifiedAt",
                schema: "acct",
                table: "AgentRegistrations");

            migrationBuilder.DropColumn(
                name: "PhoneVerifiedNumber",
                schema: "acct",
                table: "AgentRegistrations");

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                schema: "acct",
                table: "RegistrationSessions",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Provider",
                schema: "acct",
                table: "RegistrationSessions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ExternalUserId",
                schema: "acct",
                table: "RegistrationSessions",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                schema: "acct",
                table: "AgentRegistrations",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Provider",
                schema: "acct",
                table: "AgentRegistrations",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ExternalUserId",
                schema: "acct",
                table: "AgentRegistrations",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                schema: "acct",
                table: "AgentRegistrationAttempts",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Provider",
                schema: "acct",
                table: "AgentRegistrationAttempts",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ExternalUserId",
                schema: "acct",
                table: "AgentRegistrationAttempts",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentRegistrations_Provider_TenantId_ExternalUserId",
                schema: "acct",
                table: "AgentRegistrations",
                columns: new[] { "Provider", "TenantId", "ExternalUserId" },
                unique: true);
        }
    }
}
