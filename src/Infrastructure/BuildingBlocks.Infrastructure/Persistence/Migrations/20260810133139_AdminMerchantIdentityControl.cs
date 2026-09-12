using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdminMerchantIdentityControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Version",
                schema: "merch",
                table: "Users",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByAudience",
                schema: "merch",
                table: "MerchantUserInvitations",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "MerchantUser");

            migrationBuilder.AddColumn<string>(
                name: "IntendedRoleCodesJson",
                schema: "merch",
                table: "MerchantUserInvitations",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.CreateTable(
                name: "AdminUserOperationRecords",
                schema: "merch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IntentHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Result = table.Column<string>(type: "nvarchar(max)", maxLength: 16384, nullable: false),
                    HttpStatus = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminUserOperationRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminUserOperationRecords_ActorId_Operation_IdempotencyKey",
                schema: "merch",
                table: "AdminUserOperationRecords",
                columns: new[] { "ActorId", "Operation", "IdempotencyKey" },
                unique: true,
                filter: "[MerchantId] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AdminUserOperationRecords_ExpiresAt",
                schema: "merch",
                table: "AdminUserOperationRecords",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_AdminUserOperationRecords_MerchantId_ActorId_Operation_IdempotencyKey",
                schema: "merch",
                table: "AdminUserOperationRecords",
                columns: new[] { "MerchantId", "ActorId", "Operation", "IdempotencyKey" },
                unique: true,
                filter: "[MerchantId] IS NOT NULL");

            migrationBuilder.Sql(
                """
                ALTER TABLE merch.Users ADD CONSTRAINT CK_Users_Version CHECK ([Version] >= 1);
                ALTER TABLE merch.MerchantUserInvitations ADD CONSTRAINT CK_MerchantUserInvitations_CreatedByAudience
                    CHECK ([CreatedByAudience] IN ('MerchantUser', 'Admin'));
                ALTER TABLE merch.AdminUserOperationRecords ADD CONSTRAINT CK_AdminUserOperationRecords_Control
                    CHECK ([ActorId] <> '00000000-0000-0000-0000-000000000000'
                       AND [HttpStatus] BETWEEN 200 AND 299 AND [ExpiresAt] > [CreatedAt]);
                GRANT SELECT, INSERT ON merch.AdminUserOperationRecords TO pol_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                REVOKE SELECT, INSERT ON merch.AdminUserOperationRecords FROM pol_app;
                ALTER TABLE merch.MerchantUserInvitations DROP CONSTRAINT CK_MerchantUserInvitations_CreatedByAudience;
                ALTER TABLE merch.Users DROP CONSTRAINT CK_Users_Version;
                """);

            migrationBuilder.DropTable(
                name: "AdminUserOperationRecords",
                schema: "merch");

            migrationBuilder.DropColumn(
                name: "Version",
                schema: "merch",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CreatedByAudience",
                schema: "merch",
                table: "MerchantUserInvitations");

            migrationBuilder.DropColumn(
                name: "IntendedRoleCodesJson",
                schema: "merch",
                table: "MerchantUserInvitations");
        }
    }
}
