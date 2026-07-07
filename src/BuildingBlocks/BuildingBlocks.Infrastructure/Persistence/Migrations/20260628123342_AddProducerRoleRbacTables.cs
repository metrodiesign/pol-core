using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProducerRoleRbacTables : Migration
    {
        // Stable seed-role ids so grants/back-fills/Down can target them deterministically (mirrors AddAdminRoleRbacTables).
        private const string TenantOwnerRoleId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        private const string TenantMemberRoleId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The RBAC tables themselves were created by AddProducerIdentityTables (EF model diff). This migration is
            // control-plane GRANTs + the seed. Catalog tables are SELECT-only for pol_admin (dev-seeded by migration,
            // never written at runtime); role/grant/assignment tables are full CRUD; pol_app is granted NOTHING (S5).
            migrationBuilder.Sql("""
                GRANT SELECT                         ON VCentralPay.ProducerPermissionGroups TO pol_admin;
                GRANT SELECT                         ON VCentralPay.ProducerPermissions      TO pol_admin;
                GRANT SELECT, INSERT, UPDATE, DELETE ON VCentralPay.ProducerRoles            TO pol_admin;
                GRANT SELECT, INSERT, UPDATE, DELETE ON VCentralPay.ProducerRolePermissions  TO pol_admin;
                GRANT SELECT, INSERT, UPDATE, DELETE ON VCentralPay.ProducerRoleAssignments  TO pol_admin;
                """);

            // Seed the initial catalog (REQ-15.2) — mirrors ProducerPermissions.All (an integration test asserts they
            // never drift). N'...' so the Thai labels persist as Unicode.
            migrationBuilder.Sql("""
                INSERT INTO VCentralPay.ProducerPermissionGroups ([Key], LabelTh, SortOrder) VALUES
                  ('catalog', N'สินค้า',       1),
                  ('payment', N'การชำระเงิน',  2),
                  ('roles',   N'บทบาทและสิทธิ์', 3);

                INSERT INTO VCentralPay.ProducerPermissions ([Key], GroupKey, LabelTh, SortOrder) VALUES
                  ('product.create',        'catalog', N'สร้างสินค้า',            1),
                  ('product.update',        'catalog', N'แก้ไขสินค้า',            2),
                  ('payment.create',        'payment', N'สร้างรายการชำระเงิน',     3),
                  ('payment.redirect',      'payment', N'เปิดหน้าชำระเงินให้ลูกค้า', 4),
                  ('producer.roles.view',   'roles',   N'ดูบทบาท',               5),
                  ('producer.roles.manage', 'roles',   N'สร้าง/แก้ไข/ลบบทบาท',     6),
                  ('producer.user.roles',   'roles',   N'กำหนดบทบาทให้ผู้ใช้',     7);
                """);

            // Seed the two anchor roles (REQ-16.5): tenant_owner grants every key (undeletable/undeactivatable
            // recovery anchor); tenant_member is the ordinary default approval choice (product/payment only, no
            // roles.*/user.roles). Status 0 = Active.
            migrationBuilder.Sql($"""
                INSERT INTO VCentralPay.ProducerRoles (Id, Code, Name, Description, Color, Status) VALUES
                  ('{TenantOwnerRoleId}',  'tenant_owner',  N'เจ้าของร้าน',   N'เข้าถึงได้ทุกส่วนของร้าน รวมถึงการจัดการบทบาทและผู้ใช้', 'red',  0),
                  ('{TenantMemberRoleId}', 'tenant_member', N'ผู้ใช้งานร้าน', N'จัดการสินค้าและการชำระเงิน (ไม่รวมการจัดการบทบาท)',    'blue', 0);

                INSERT INTO VCentralPay.ProducerRolePermissions (Id, RoleId, PermissionKey) VALUES
                  (NEWID(), '{TenantOwnerRoleId}', 'product.create'),
                  (NEWID(), '{TenantOwnerRoleId}', 'product.update'),
                  (NEWID(), '{TenantOwnerRoleId}', 'payment.create'),
                  (NEWID(), '{TenantOwnerRoleId}', 'payment.redirect'),
                  (NEWID(), '{TenantOwnerRoleId}', 'producer.roles.view'),
                  (NEWID(), '{TenantOwnerRoleId}', 'producer.roles.manage'),
                  (NEWID(), '{TenantOwnerRoleId}', 'producer.user.roles'),
                  (NEWID(), '{TenantMemberRoleId}', 'product.create'),
                  (NEWID(), '{TenantMemberRoleId}', 'product.update'),
                  (NEWID(), '{TenantMemberRoleId}', 'payment.create'),
                  (NEWID(), '{TenantMemberRoleId}', 'payment.redirect');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove the seed (children first for the catalog FK), then the grants. The tables themselves are dropped
            // by AddProducerIdentityTables' Down.
            migrationBuilder.Sql($"""
                DELETE FROM VCentralPay.ProducerRolePermissions WHERE RoleId IN ('{TenantOwnerRoleId}', '{TenantMemberRoleId}');
                DELETE FROM VCentralPay.ProducerRoles           WHERE Id     IN ('{TenantOwnerRoleId}', '{TenantMemberRoleId}');
                DELETE FROM VCentralPay.ProducerPermissions WHERE [Key] IN
                  ('product.create','product.update','payment.create','payment.redirect',
                   'producer.roles.view','producer.roles.manage','producer.user.roles');
                DELETE FROM VCentralPay.ProducerPermissionGroups WHERE [Key] IN ('catalog','payment','roles');
                """);

            migrationBuilder.Sql("""
                REVOKE SELECT                         ON VCentralPay.ProducerPermissionGroups FROM pol_admin;
                REVOKE SELECT                         ON VCentralPay.ProducerPermissions      FROM pol_admin;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON VCentralPay.ProducerRoles            FROM pol_admin;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON VCentralPay.ProducerRolePermissions  FROM pol_admin;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON VCentralPay.ProducerRoleAssignments  FROM pol_admin;
                """);
        }
    }
}
