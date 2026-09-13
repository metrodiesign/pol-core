using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MerchantRealApiIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MerchantUserInvitations",
                schema: "merch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    TokenHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AcceptedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AcceptedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MerchantUserInvitations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MerchantUserManagementAudits",
                schema: "merch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TargetUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    InvitationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Action = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MerchantUserManagementAudits", x => x.Id);
                    table.CheckConstraint("CK_MerchantUserManagementAudits_Target", "[TargetUserId] IS NOT NULL OR [InvitationId] IS NOT NULL");
                });

            migrationBuilder.CreateIndex(
                name: "IX_MerchantUserInvitations_MerchantId_NormalizedEmail",
                schema: "merch",
                table: "MerchantUserInvitations",
                columns: new[] { "MerchantId", "NormalizedEmail" },
                unique: true,
                filter: "[AcceptedAt] IS NULL AND [RevokedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MerchantUserInvitations_TokenHash",
                schema: "merch",
                table: "MerchantUserInvitations",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MerchantUserManagementAudits_MerchantId_OccurredAt",
                schema: "merch",
                table: "MerchantUserManagementAudits",
                columns: new[] { "MerchantId", "OccurredAt" });

            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE ON merch.MerchantUserInvitations TO pol_app;
                GRANT SELECT, INSERT ON merch.MerchantUserManagementAudits TO pol_app;
                """);

            migrationBuilder.Sql("""
                INSERT INTO iam.Permissions ([Key], GroupKey, Name, Status, SortOrder) VALUES
                  ('payment.view', 'payment', N'ดูรายการชำระเงิน', 1, 20),
                  ('users.view',   'roles',   N'ดูผู้ใช้งานร้านค้า', 1, 21),
                  ('users.manage', 'roles',   N'จัดการผู้ใช้งานร้านค้า', 1, 22);

                INSERT INTO iam.RolePermissions (Id, RoleId, PermissionKey) VALUES
                  (NEWID(), 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'payment.view'),
                  (NEWID(), 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'users.view'),
                  (NEWID(), 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'users.manage'),
                  (NEWID(), 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 'payment.view');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM iam.RolePermissions
                WHERE RoleId IN (
                    'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
                    'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb')
                  AND PermissionKey IN ('payment.view', 'users.view', 'users.manage');

                DELETE FROM iam.Permissions
                WHERE [Key] IN ('payment.view', 'users.view', 'users.manage');
                """);

            migrationBuilder.DropTable(
                name: "MerchantUserInvitations",
                schema: "merch");

            migrationBuilder.DropTable(
                name: "MerchantUserManagementAudits",
                schema: "merch");
        }
    }
}
