using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminRoleRbacTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TargetRoleId",
                schema: "producer",
                table: "AdminAccountAudits",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AdminPermissionGroups",
                schema: "producer",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    LabelTh = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminPermissionGroups", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "AdminRoles",
                schema: "producer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Color = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AdminPermissions",
                schema: "producer",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    GroupKey = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    LabelTh = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminPermissions", x => x.Key);
                    table.ForeignKey(
                        name: "FK_AdminPermissions_AdminPermissionGroups_GroupKey",
                        column: x => x.GroupKey,
                        principalSchema: "producer",
                        principalTable: "AdminPermissionGroups",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AdminRoleAssignments",
                schema: "producer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AdminAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignedByAdminId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminRoleAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdminRoleAssignments_AdminRoles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "producer",
                        principalTable: "AdminRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AdminRolePermissions",
                schema: "producer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PermissionKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminRolePermissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdminRolePermissions_AdminPermissions_PermissionKey",
                        column: x => x.PermissionKey,
                        principalSchema: "producer",
                        principalTable: "AdminPermissions",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AdminRolePermissions_AdminRoles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "producer",
                        principalTable: "AdminRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminPermissions_GroupKey",
                schema: "producer",
                table: "AdminPermissions",
                column: "GroupKey");

            migrationBuilder.CreateIndex(
                name: "IX_AdminRoleAssignments_AdminAccountId_RoleId",
                schema: "producer",
                table: "AdminRoleAssignments",
                columns: new[] { "AdminAccountId", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdminRoleAssignments_RoleId",
                schema: "producer",
                table: "AdminRoleAssignments",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminRolePermissions_PermissionKey",
                schema: "producer",
                table: "AdminRolePermissions",
                column: "PermissionKey");

            migrationBuilder.CreateIndex(
                name: "IX_AdminRolePermissions_RoleId_PermissionKey",
                schema: "producer",
                table: "AdminRolePermissions",
                columns: new[] { "RoleId", "PermissionKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdminRoles_Code",
                schema: "producer",
                table: "AdminRoles",
                column: "Code",
                unique: true);

            // Control-plane: pol_admin only, NO tenant RLS predicate. Catalog tables are dev-seeded (here, and by
            // future feature migrations) so pol_admin reads them at runtime but never writes (SELECT only). Role /
            // grant / assignment tables are mutated by the management endpoints (SELECT, INSERT, UPDATE, DELETE).
            migrationBuilder.Sql("""
                GRANT SELECT                         ON producer.AdminPermissionGroups TO pol_admin;
                GRANT SELECT                         ON producer.AdminPermissions      TO pol_admin;
                GRANT SELECT, INSERT, UPDATE, DELETE ON producer.AdminRoles            TO pol_admin;
                GRANT SELECT, INSERT, UPDATE, DELETE ON producer.AdminRolePermissions  TO pol_admin;
                GRANT SELECT, INSERT, UPDATE, DELETE ON producer.AdminRoleAssignments  TO pol_admin;
                """);

            // Seed the initial catalog (REQ-1.3) — mirrors AdminPermissions.All (a test asserts they never drift)
            // and the frontend's producer-role mock. N'...' so the Thai labels persist as Unicode.
            migrationBuilder.Sql("""
                INSERT INTO producer.AdminPermissionGroups ([Key], LabelTh, SortOrder) VALUES
                  ('txn',      N'ธุรกรรม',   1),
                  ('merchant', N'ร้านค้า',   2),
                  ('finance',  N'การเงิน',   3),
                  ('user',     N'ผู้ใช้งาน', 4),
                  ('system',   N'ระบบ',      5);

                INSERT INTO producer.AdminPermissions ([Key], GroupKey, LabelTh, SortOrder) VALUES
                  ('txn.view',        'txn',      N'ดูรายการธุรกรรม',          1),
                  ('txn.refund',      'txn',      N'สั่งคืนเงิน',              2),
                  ('txn.export',      'txn',      N'ส่งออกข้อมูลธุรกรรม',       3),
                  ('merchant.view',   'merchant', N'ดูข้อมูลร้านค้า',          4),
                  ('merchant.manage', 'merchant', N'เพิ่ม/แก้ไข/ระงับร้านค้า', 5),
                  ('invoice.view',    'finance',  N'ดูใบแจ้งหนี้',             6),
                  ('invoice.manage',  'finance',  N'ออก/ยกเลิกใบแจ้งหนี้',      7),
                  ('settlement.run',  'finance',  N'สั่ง Settlement รอบพิเศษ',  8),
                  ('user.view',       'user',     N'ดูรายชื่อผู้ใช้งาน',       9),
                  ('user.manage',     'user',     N'เปิด/แก้ไข/ปิดบัญชีผู้ใช้', 10),
                  ('user.roles',      'user',     N'กำหนดบทบาทให้ผู้ใช้',      11),
                  ('audit.view',      'system',   N'ดูบันทึกกิจกรรม (audit)',  12),
                  ('settings.manage', 'system',   N'ตั้งค่าระบบและความปลอดภัย', 13),
                  ('apikey.manage',   'system',   N'จัดการ API client / secret', 14);
                """);

            // Seed the 5 default roles (REQ-2.5) with stable ids; super_admin is the recovery anchor.
            migrationBuilder.Sql("""
                INSERT INTO producer.AdminRoles (Id, Code, Name, Description, Color, Status) VALUES
                  ('11111111-1111-1111-1111-111111111111', 'super_admin', N'ผู้ดูแลระบบสูงสุด',    N'เข้าถึงได้ทุกส่วนของระบบ รวมถึงการตั้งค่าความปลอดภัย', 'red',   0),
                  ('22222222-2222-2222-2222-222222222222', 'ops_admin',   N'ผู้ดูแลฝ่ายปฏิบัติการ', N'ดูแลธุรกรรมและร้านค้าประจำวัน',                  'blue',  0),
                  ('33333333-3333-3333-3333-333333333333', 'finance',     N'ผู้ดูแลการเงิน',       N'จัดการใบแจ้งหนี้และรอบ Settlement',              'green', 0),
                  ('44444444-4444-4444-4444-444444444444', 'support',     N'เจ้าหน้าที่ซัพพอร์ต',   N'ตอบคำถามลูกค้า ดูข้อมูลได้อย่างเดียว',           'amber', 0),
                  ('55555555-5555-5555-5555-555555555555', 'auditor',     N'ผู้ตรวจสอบ',          N'เข้าถึงบันทึกกิจกรรมและรายงานแบบอ่านอย่างเดียว',  'gray',  1);

                INSERT INTO producer.AdminRolePermissions (Id, RoleId, PermissionKey) VALUES
                  (NEWID(), '11111111-1111-1111-1111-111111111111', 'txn.view'),
                  (NEWID(), '11111111-1111-1111-1111-111111111111', 'txn.refund'),
                  (NEWID(), '11111111-1111-1111-1111-111111111111', 'txn.export'),
                  (NEWID(), '11111111-1111-1111-1111-111111111111', 'merchant.view'),
                  (NEWID(), '11111111-1111-1111-1111-111111111111', 'merchant.manage'),
                  (NEWID(), '11111111-1111-1111-1111-111111111111', 'invoice.view'),
                  (NEWID(), '11111111-1111-1111-1111-111111111111', 'invoice.manage'),
                  (NEWID(), '11111111-1111-1111-1111-111111111111', 'settlement.run'),
                  (NEWID(), '11111111-1111-1111-1111-111111111111', 'user.view'),
                  (NEWID(), '11111111-1111-1111-1111-111111111111', 'user.manage'),
                  (NEWID(), '11111111-1111-1111-1111-111111111111', 'user.roles'),
                  (NEWID(), '11111111-1111-1111-1111-111111111111', 'audit.view'),
                  (NEWID(), '11111111-1111-1111-1111-111111111111', 'settings.manage'),
                  (NEWID(), '11111111-1111-1111-1111-111111111111', 'apikey.manage'),
                  (NEWID(), '22222222-2222-2222-2222-222222222222', 'txn.view'),
                  (NEWID(), '22222222-2222-2222-2222-222222222222', 'txn.refund'),
                  (NEWID(), '22222222-2222-2222-2222-222222222222', 'txn.export'),
                  (NEWID(), '22222222-2222-2222-2222-222222222222', 'merchant.view'),
                  (NEWID(), '22222222-2222-2222-2222-222222222222', 'merchant.manage'),
                  (NEWID(), '22222222-2222-2222-2222-222222222222', 'user.view'),
                  (NEWID(), '33333333-3333-3333-3333-333333333333', 'txn.view'),
                  (NEWID(), '33333333-3333-3333-3333-333333333333', 'txn.export'),
                  (NEWID(), '33333333-3333-3333-3333-333333333333', 'invoice.view'),
                  (NEWID(), '33333333-3333-3333-3333-333333333333', 'invoice.manage'),
                  (NEWID(), '33333333-3333-3333-3333-333333333333', 'settlement.run'),
                  (NEWID(), '44444444-4444-4444-4444-444444444444', 'txn.view'),
                  (NEWID(), '44444444-4444-4444-4444-444444444444', 'merchant.view'),
                  (NEWID(), '44444444-4444-4444-4444-444444444444', 'user.view'),
                  (NEWID(), '55555555-5555-5555-5555-555555555555', 'txn.view'),
                  (NEWID(), '55555555-5555-5555-5555-555555555555', 'invoice.view'),
                  (NEWID(), '55555555-5555-5555-5555-555555555555', 'audit.view');
                """);

            // Back-fill: bind super_admin to every EXISTING Super-tier account (Tier = 1) so an admin provisioned
            // before this feature is not locked out of the role endpoints (orthogonal model, no Super-bypass —
            // REQ-8.1). Idempotent via NOT EXISTS. Future Supers get the role at self-provision time.
            migrationBuilder.Sql("""
                INSERT INTO producer.AdminRoleAssignments (Id, AdminAccountId, RoleId, AssignedByAdminId, AssignedAt)
                SELECT NEWID(), a.Id, '11111111-1111-1111-1111-111111111111', a.Id, SYSUTCDATETIME()
                FROM producer.AdminAccounts a
                WHERE a.Tier = 1
                  AND NOT EXISTS (
                      SELECT 1 FROM producer.AdminRoleAssignments x
                      WHERE x.AdminAccountId = a.Id AND x.RoleId = '11111111-1111-1111-1111-111111111111');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                REVOKE SELECT                         ON producer.AdminPermissionGroups FROM pol_admin;
                REVOKE SELECT                         ON producer.AdminPermissions      FROM pol_admin;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON producer.AdminRoles            FROM pol_admin;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON producer.AdminRolePermissions  FROM pol_admin;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON producer.AdminRoleAssignments  FROM pol_admin;
                """);

            migrationBuilder.DropTable(
                name: "AdminRoleAssignments",
                schema: "producer");

            migrationBuilder.DropTable(
                name: "AdminRolePermissions",
                schema: "producer");

            migrationBuilder.DropTable(
                name: "AdminPermissions",
                schema: "producer");

            migrationBuilder.DropTable(
                name: "AdminRoles",
                schema: "producer");

            migrationBuilder.DropTable(
                name: "AdminPermissionGroups",
                schema: "producer");

            migrationBuilder.DropColumn(
                name: "TargetRoleId",
                schema: "producer",
                table: "AdminAccountAudits");
        }
    }
}
