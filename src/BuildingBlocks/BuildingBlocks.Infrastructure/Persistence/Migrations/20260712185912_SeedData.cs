using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Dev-seeded central IAM catalog (rf2 design.md "Seed" section — the third hand-written migration, empty EF
    /// model so it never diffs). Seeds the SINGLE <c>iam.*</c> catalog that replaces the two duplicated
    /// <c>admin.*</c>/<c>merch.*</c> catalogs the pre-rf2 chain seeded: 20 permission keys in 8 groups (mirroring
    /// <c>Iam.Domain.Permissions.Keys.All</c> exactly — an integration drift guard asserts DB rows SetEquals that
    /// vocabulary, REQ-10.2) and 4 seed roles with stable GUIDs + their permission grants (REQ-2.3). Group
    /// <c>Scope</c> 0 = Platform, 1 = Merchant; role <c>Scope</c> 0 = Platform, 1 = Merchant; role/permission
    /// <c>Status</c> 0 = Active. The two anchors — <c>platform_admin</c>/<c>merchant_manager</c> — inherit the
    /// stable ids of the pre-rf2 recovery anchors (super_admin / merchant_owner) so unrelated drift is minimised;
    /// bootstrap self-provision (REQ-8.1) binds <c>platform_admin</c> to the first Super by CODE at boot, so this
    /// migration seeds no assignment rows (a fresh reset has no users to back-fill).
    /// </summary>
    public partial class SeedData : Migration
    {
        private const string PlatformAdminRoleId = "11111111-1111-1111-1111-111111111111"; // inherits super_admin id
        private const string PlatformAuditorRoleId = "55555555-5555-5555-5555-555555555555"; // inherits auditor id
        private const string MerchantManagerRoleId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"; // inherits merchant_owner id
        private const string MerchantStaffRoleId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"; // inherits merchant_member id

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // --- Central IAM catalog: 8 groups / 20 keys (REQ-2.1; mirrors Keys.All + Keys.GroupScope — the
            // code-canonical vocabulary an integration test asserts never drifts). Scope: {txn, merchant, user,
            // system, merchants.users} = Platform (0); {catalog, payment, roles} = Merchant (1). ---
            migrationBuilder.Sql("""
                INSERT INTO iam.PermissionGroups ([Key], Scope, LabelTh, SortOrder) VALUES
                  ('txn',             0, N'ธุรกรรม',           1),
                  ('merchant',        0, N'ร้านค้า',            2),
                  ('user',            0, N'ผู้ใช้งาน',           3),
                  ('system',          0, N'ระบบ',               4),
                  ('merchants.users', 0, N'ผู้ใช้งานร้านค้า',     5),
                  ('catalog',         1, N'สินค้า',             6),
                  ('payment',         1, N'การชำระเงิน',        7),
                  ('roles',           1, N'บทบาทและสิทธิ์',      8);

                INSERT INTO iam.Permissions ([Key], GroupKey, LabelTh, SortOrder) VALUES
                  ('txn.view',                'txn',             N'ดูรายการธุรกรรม',           1),
                  ('txn.refund',              'txn',             N'สั่งคืนเงิน',               2),
                  ('txn.export',              'txn',             N'ส่งออกข้อมูลธุรกรรม',        3),
                  ('merchant.view',           'merchant',        N'ดูข้อมูลร้านค้า',           4),
                  ('merchant.manage',         'merchant',        N'เพิ่ม/แก้ไข/ระงับร้านค้า',  5),
                  ('user.view',               'user',            N'ดูรายชื่อผู้ใช้งาน',        6),
                  ('user.manage',             'user',            N'เปิด/แก้ไข/ปิดบัญชีผู้ใช้', 7),
                  ('user.roles',              'user',            N'กำหนดบทบาทให้ผู้ใช้',       8),
                  ('audit.view',              'system',          N'ดูบันทึกกิจกรรม (audit)',   9),
                  ('settings.manage',         'system',          N'ตั้งค่าระบบและความปลอดภัย',  10),
                  ('apikey.manage',           'system',          N'จัดการ API client / secret', 11),
                  ('merchants.users.approve', 'merchants.users', N'อนุมัติผู้ใช้งานร้านค้า',   12),
                  ('merchants.users.reject',  'merchants.users', N'ปฏิเสธผู้ใช้งานร้านค้า',    13),
                  ('product.create',          'catalog',         N'สร้างสินค้า',              14),
                  ('product.update',          'catalog',         N'แก้ไขสินค้า',              15),
                  ('payment.create',          'payment',         N'สร้างรายการชำระเงิน',       16),
                  ('payment.redirect',        'payment',         N'เปิดหน้าชำระเงินให้ลูกค้า',   17),
                  ('roles.view',              'roles',           N'ดูบทบาท',                 18),
                  ('roles.manage',            'roles',           N'สร้าง/แก้ไข/ลบบทบาท',       19),
                  ('users.roles',             'roles',           N'กำหนดบทบาทให้ผู้ใช้',       20);
                """);

            // --- 4 seed roles with stable ids (REQ-2.3). platform_admin / merchant_manager are the undeletable/
            // undeactivatable anchors (REQ-2.4, enforced in the Role aggregate). Scope column: 0 = Platform,
            // 1 = Merchant. MerchantId is NULL for all four (shared/seed — REQ-3.2); the CHECK constraint requires
            // Platform => MerchantId NULL. Status 0 = Active for all four (platform_auditor is Active by plan,
            // unlike the pre-rf2 auditor which seeded Inactive). ---
            migrationBuilder.Sql($"""
                INSERT INTO iam.Roles (Id, Code, Name, Description, Color, Status, Scope, MerchantId) VALUES
                  ('{PlatformAdminRoleId}',   'platform_admin',   N'ผู้ดูแลแพลตฟอร์ม', N'เข้าถึงได้ทุกส่วนของแพลตฟอร์ม รวมถึงการตั้งค่าความปลอดภัย', 'red',  0, 0, NULL),
                  ('{PlatformAuditorRoleId}', 'platform_auditor', N'ผู้ตรวจสอบ',       N'อ่านข้อมูลธุรกรรม/ร้านค้า/ผู้ใช้ และบันทึกกิจกรรมเท่านั้น',  'gray', 0, 0, NULL),
                  ('{MerchantManagerRoleId}', 'merchant_manager', N'ผู้จัดการร้าน',    N'เข้าถึงได้ทุกส่วนของร้าน รวมถึงการจัดการบทบาทและผู้ใช้',     'red',  0, 1, NULL),
                  ('{MerchantStaffRoleId}',   'merchant_staff',   N'พนักงานร้าน',      N'จัดการสินค้าและการชำระเงิน (ไม่รวมการจัดการบทบาท)',        'blue', 0, 1, NULL);
                """);

            // Role -> permission grants per the design matrix. platform_admin = all 13 Platform keys;
            // platform_auditor = txn.view/merchant.view/user.view/audit.view; merchant_manager = all 7 Merchant
            // keys; merchant_staff = product.create/product.update/payment.create/payment.redirect. FK PermissionKey
            // -> iam.Permissions.Key (Restrict) guarantees no phantom-key grant (REQ-2.6).
            migrationBuilder.Sql($"""
                INSERT INTO iam.RolePermissions (Id, RoleId, PermissionKey) VALUES
                  (NEWID(), '{PlatformAdminRoleId}', 'txn.view'),
                  (NEWID(), '{PlatformAdminRoleId}', 'txn.refund'),
                  (NEWID(), '{PlatformAdminRoleId}', 'txn.export'),
                  (NEWID(), '{PlatformAdminRoleId}', 'merchant.view'),
                  (NEWID(), '{PlatformAdminRoleId}', 'merchant.manage'),
                  (NEWID(), '{PlatformAdminRoleId}', 'user.view'),
                  (NEWID(), '{PlatformAdminRoleId}', 'user.manage'),
                  (NEWID(), '{PlatformAdminRoleId}', 'user.roles'),
                  (NEWID(), '{PlatformAdminRoleId}', 'audit.view'),
                  (NEWID(), '{PlatformAdminRoleId}', 'settings.manage'),
                  (NEWID(), '{PlatformAdminRoleId}', 'apikey.manage'),
                  (NEWID(), '{PlatformAdminRoleId}', 'merchants.users.approve'),
                  (NEWID(), '{PlatformAdminRoleId}', 'merchants.users.reject'),
                  (NEWID(), '{PlatformAuditorRoleId}', 'txn.view'),
                  (NEWID(), '{PlatformAuditorRoleId}', 'merchant.view'),
                  (NEWID(), '{PlatformAuditorRoleId}', 'user.view'),
                  (NEWID(), '{PlatformAuditorRoleId}', 'audit.view'),
                  (NEWID(), '{MerchantManagerRoleId}', 'product.create'),
                  (NEWID(), '{MerchantManagerRoleId}', 'product.update'),
                  (NEWID(), '{MerchantManagerRoleId}', 'payment.create'),
                  (NEWID(), '{MerchantManagerRoleId}', 'payment.redirect'),
                  (NEWID(), '{MerchantManagerRoleId}', 'roles.view'),
                  (NEWID(), '{MerchantManagerRoleId}', 'roles.manage'),
                  (NEWID(), '{MerchantManagerRoleId}', 'users.roles'),
                  (NEWID(), '{MerchantStaffRoleId}', 'product.create'),
                  (NEWID(), '{MerchantStaffRoleId}', 'product.update'),
                  (NEWID(), '{MerchantStaffRoleId}', 'payment.create'),
                  (NEWID(), '{MerchantStaffRoleId}', 'payment.redirect');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Children before parents (FK-safe): grants -> roles -> permissions -> groups.
            migrationBuilder.Sql($"""
                DELETE FROM iam.RolePermissions WHERE RoleId IN
                  ('{PlatformAdminRoleId}', '{PlatformAuditorRoleId}', '{MerchantManagerRoleId}', '{MerchantStaffRoleId}');
                DELETE FROM iam.Roles WHERE Id IN
                  ('{PlatformAdminRoleId}', '{PlatformAuditorRoleId}', '{MerchantManagerRoleId}', '{MerchantStaffRoleId}');
                DELETE FROM iam.Permissions WHERE [Key] IN
                  ('txn.view','txn.refund','txn.export','merchant.view','merchant.manage','user.view','user.manage',
                   'user.roles','audit.view','settings.manage','apikey.manage','merchants.users.approve',
                   'merchants.users.reject','product.create','product.update','payment.create','payment.redirect',
                   'roles.view','roles.manage','users.roles');
                DELETE FROM iam.PermissionGroups WHERE [Key] IN
                  ('txn','merchant','user','system','merchants.users','catalog','payment','roles');
                """);
        }
    }
}
