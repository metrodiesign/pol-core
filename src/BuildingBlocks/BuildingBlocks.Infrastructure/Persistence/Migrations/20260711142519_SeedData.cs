using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Dev-seeded catalog + master data (rf1 design.md T9: the third hand-written migration). Ports every seed
    /// migration from the pre-reset chain forward with the rename table applied — key/label/GUID VALUES are
    /// unchanged except where the value itself embedded a renamed token (<c>producer.*</c> permission keys,
    /// <c>tenant_owner</c>/<c>tenant_member</c> anchor role codes). Admin RBAC seeds the full 6-group/16-key
    /// catalog in one pass (merging what was originally two migrations — the base catalog plus the later
    /// merchant-user-approval addition) since there is no intermediate state to replay in a big-bang reset.
    /// </summary>
    public partial class SeedData : Migration
    {
        private const string MerchantOwnerRoleId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        private const string MerchantMemberRoleId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
        private const string SuperAdminRoleId = "11111111-1111-1111-1111-111111111111";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // --- Admin RBAC catalog (REQ-1.3/2.5; Permissions.All is the code-canonical vocabulary this
            // mirrors — an integration test asserts they never drift) ---
            migrationBuilder.Sql("""
                INSERT INTO admin.PermissionGroups ([Key], LabelTh, SortOrder) VALUES
                  ('txn',           N'ธุรกรรม',              1),
                  ('merchant',      N'ร้านค้า',               2),
                  ('finance',       N'การเงิน',               3),
                  ('user',          N'ผู้ใช้งาน',              4),
                  ('system',        N'ระบบ',                  5),
                  ('merchant_user', N'ผู้ใช้งานร้านค้า',       6);

                INSERT INTO admin.Permissions ([Key], GroupKey, LabelTh, SortOrder) VALUES
                  ('txn.view',              'txn',           N'ดูรายการธุรกรรม',           1),
                  ('txn.refund',            'txn',           N'สั่งคืนเงิน',               2),
                  ('txn.export',            'txn',           N'ส่งออกข้อมูลธุรกรรม',        3),
                  ('merchant.view',         'merchant',      N'ดูข้อมูลร้านค้า',           4),
                  ('merchant.manage',       'merchant',      N'เพิ่ม/แก้ไข/ระงับร้านค้า',  5),
                  ('invoice.view',          'finance',       N'ดูใบแจ้งหนี้',              6),
                  ('invoice.manage',        'finance',       N'ออก/ยกเลิกใบแจ้งหนี้',       7),
                  ('settlement.run',        'finance',       N'สั่ง Settlement รอบพิเศษ',   8),
                  ('user.view',             'user',          N'ดูรายชื่อผู้ใช้งาน',        9),
                  ('user.manage',           'user',          N'เปิด/แก้ไข/ปิดบัญชีผู้ใช้', 10),
                  ('user.roles',            'user',          N'กำหนดบทบาทให้ผู้ใช้',       11),
                  ('audit.view',            'system',        N'ดูบันทึกกิจกรรม (audit)',   12),
                  ('settings.manage',       'system',        N'ตั้งค่าระบบและความปลอดภัย',  13),
                  ('apikey.manage',         'system',        N'จัดการ API client / secret', 14),
                  ('merchant_user.approve', 'merchant_user', N'อนุมัติผู้ใช้งานร้านค้า',   15),
                  ('merchant_user.reject',  'merchant_user', N'ปฏิเสธผู้ใช้งานร้านค้า',    16);
                """);

            // Seed the 5 default roles with stable ids; super_admin is the recovery anchor.
            migrationBuilder.Sql("""
                INSERT INTO admin.Roles (Id, Code, Name, Description, Color, Status) VALUES
                  ('11111111-1111-1111-1111-111111111111', 'super_admin', N'ผู้ดูแลระบบสูงสุด',    N'เข้าถึงได้ทุกส่วนของระบบ รวมถึงการตั้งค่าความปลอดภัย', 'red',   0),
                  ('22222222-2222-2222-2222-222222222222', 'ops_admin',   N'ผู้ดูแลฝ่ายปฏิบัติการ', N'ดูแลธุรกรรมและร้านค้าประจำวัน',                  'blue',  0),
                  ('33333333-3333-3333-3333-333333333333', 'finance',     N'ผู้ดูแลการเงิน',       N'จัดการใบแจ้งหนี้และรอบ Settlement',              'green', 0),
                  ('44444444-4444-4444-4444-444444444444', 'support',     N'เจ้าหน้าที่ซัพพอร์ต',   N'ตอบคำถามลูกค้า ดูข้อมูลได้อย่างเดียว',           'amber', 0),
                  ('55555555-5555-5555-5555-555555555555', 'auditor',     N'ผู้ตรวจสอบ',          N'เข้าถึงบันทึกกิจกรรมและรายงานแบบอ่านอย่างเดียว',  'gray',  1);

                INSERT INTO admin.RolePermissions (Id, RoleId, PermissionKey) VALUES
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
                  (NEWID(), '11111111-1111-1111-1111-111111111111', 'merchant_user.approve'),
                  (NEWID(), '11111111-1111-1111-1111-111111111111', 'merchant_user.reject'),
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

            // Back-fill: bind super_admin to every EXISTING Super-tier account (a no-op on a fresh reset — kept so
            // a platform user created by an out-of-band bootstrap step ahead of this migration is never locked out).
            migrationBuilder.Sql($"""
                INSERT INTO admin.RoleAssignments (Id, PlatformUserId, RoleId, AssignedByAdminId, AssignedAt)
                SELECT NEWID(), a.Id, '{SuperAdminRoleId}', a.Id, SYSUTCDATETIME()
                FROM admin.Users a
                WHERE a.Tier = 1
                  AND NOT EXISTS (
                      SELECT 1 FROM admin.RoleAssignments x
                      WHERE x.PlatformUserId = a.Id AND x.RoleId = '{SuperAdminRoleId}');
                """);

            // --- Admin master data: HR org lists (Positions/Offices/Levels/Divisions). Fixed, deterministic GUIDs
            // (never NEWID in a migration) so every environment shares the same ids and the PlatformUser FKs stay
            // stable. RFC-4122-well-formed, namespaced by table for readability. Values UNCHANGED — no rename. ---
            migrationBuilder.Sql("""
                INSERT INTO admin.Positions (Id, Code, Name, IsActive) VALUES
                  ('a1000000-0000-4000-8000-000000000001', 'ceo',               N'ประธานเจ้าหน้าที่บริหาร',            1),
                  ('a1000000-0000-4000-8000-000000000002', 'coo',               N'ประธานเจ้าหน้าที่ปฏิบัติการ',        1),
                  ('a1000000-0000-4000-8000-000000000003', 'cfo',               N'ประธานเจ้าหน้าที่การเงิน',           1),
                  ('a1000000-0000-4000-8000-000000000004', 'cto',               N'ประธานเจ้าหน้าที่เทคโนโลยีสารสนเทศ', 1),
                  ('a1000000-0000-4000-8000-000000000005', 'director',          N'ผู้อำนวยการ',                       1),
                  ('a1000000-0000-4000-8000-000000000006', 'deputy_director',   N'รองผู้อำนวยการ',                    1),
                  ('a1000000-0000-4000-8000-000000000007', 'manager',           N'ผู้จัดการ',                         1),
                  ('a1000000-0000-4000-8000-000000000008', 'assistant_manager', N'ผู้ช่วยผู้จัดการ',                   1),
                  ('a1000000-0000-4000-8000-000000000009', 'supervisor',        N'หัวหน้างาน',                        1),
                  ('a1000000-0000-4000-8000-00000000000a', 'senior_officer',    N'เจ้าหน้าที่อาวุโส',                 1),
                  ('a1000000-0000-4000-8000-00000000000b', 'officer',           N'เจ้าหน้าที่',                       1),
                  ('a1000000-0000-4000-8000-00000000000c', 'staff',             N'พนักงาน',                           1);
                """);

            migrationBuilder.Sql("""
                INSERT INTO admin.Offices (Id, Code, Name, IsActive) VALUES
                  ('b2000000-0000-4000-8000-000000000001', 'hq',        N'สำนักงานใหญ่',                     1),
                  ('b2000000-0000-4000-8000-000000000002', 'north',     N'สำนักงานภาคเหนือ',                 1),
                  ('b2000000-0000-4000-8000-000000000003', 'northeast', N'สำนักงานภาคตะวันออกเฉียงเหนือ',    1),
                  ('b2000000-0000-4000-8000-000000000004', 'central',   N'สำนักงานภาคกลาง',                  1),
                  ('b2000000-0000-4000-8000-000000000005', 'east',      N'สำนักงานภาคตะวันออก',              1),
                  ('b2000000-0000-4000-8000-000000000006', 'west',      N'สำนักงานภาคตะวันตก',               1),
                  ('b2000000-0000-4000-8000-000000000007', 'south',     N'สำนักงานภาคใต้',                   1),
                  ('b2000000-0000-4000-8000-000000000008', 'remote',    N'ปฏิบัติงานนอกสถานที่',             1);
                """);

            migrationBuilder.Sql("""
                INSERT INTO admin.Levels (Id, Code, Name, IsActive) VALUES
                  ('c3000000-0000-4000-8000-000000000001', 'level_1',  N'ระดับ 1',  1),
                  ('c3000000-0000-4000-8000-000000000002', 'level_2',  N'ระดับ 2',  1),
                  ('c3000000-0000-4000-8000-000000000003', 'level_3',  N'ระดับ 3',  1),
                  ('c3000000-0000-4000-8000-000000000004', 'level_4',  N'ระดับ 4',  1),
                  ('c3000000-0000-4000-8000-000000000005', 'level_5',  N'ระดับ 5',  1),
                  ('c3000000-0000-4000-8000-000000000006', 'level_6',  N'ระดับ 6',  1),
                  ('c3000000-0000-4000-8000-000000000007', 'level_7',  N'ระดับ 7',  1),
                  ('c3000000-0000-4000-8000-000000000008', 'level_8',  N'ระดับ 8',  1),
                  ('c3000000-0000-4000-8000-000000000009', 'level_9',  N'ระดับ 9',  1),
                  ('c3000000-0000-4000-8000-00000000000a', 'level_10', N'ระดับ 10', 1);
                """);

            migrationBuilder.Sql("""
                INSERT INTO admin.Divisions (Id, Code, Name, IsActive) VALUES
                  ('d4000000-0000-4000-8000-000000000001', 'executive',        N'สำนักผู้บริหาร',                    1),
                  ('d4000000-0000-4000-8000-000000000002', 'finance',          N'ฝ่ายการเงินและบัญชี',               1),
                  ('d4000000-0000-4000-8000-000000000003', 'technology',       N'ฝ่ายเทคโนโลยีสารสนเทศ',             1),
                  ('d4000000-0000-4000-8000-000000000004', 'operations',       N'ฝ่ายปฏิบัติการ',                    1),
                  ('d4000000-0000-4000-8000-000000000005', 'product',          N'ฝ่ายผลิตภัณฑ์',                     1),
                  ('d4000000-0000-4000-8000-000000000006', 'sales_marketing',  N'ฝ่ายขายและการตลาด',                 1),
                  ('d4000000-0000-4000-8000-000000000007', 'risk_compliance',  N'ฝ่ายบริหารความเสี่ยงและกำกับดูแล',  1),
                  ('d4000000-0000-4000-8000-000000000008', 'legal',            N'ฝ่ายกฎหมาย',                        1),
                  ('d4000000-0000-4000-8000-000000000009', 'hr',               N'ฝ่ายทรัพยากรบุคคล',                 1),
                  ('d4000000-0000-4000-8000-00000000000a', 'customer_service', N'ฝ่ายบริการลูกค้า',                  1);
                """);

            // --- MerchantUser RBAC catalog (Permissions.All is the code-canonical vocabulary this
            // mirrors — an integration test asserts they never drift) ---
            migrationBuilder.Sql("""
                INSERT INTO merch.PermissionGroups ([Key], LabelTh, SortOrder) VALUES
                  ('catalog', N'สินค้า',         1),
                  ('payment', N'การชำระเงิน',    2),
                  ('roles',   N'บทบาทและสิทธิ์', 3);

                INSERT INTO merch.Permissions ([Key], GroupKey, LabelTh, SortOrder) VALUES
                  ('product.create',          'catalog', N'สร้างสินค้า',            1),
                  ('product.update',          'catalog', N'แก้ไขสินค้า',            2),
                  ('payment.create',          'payment', N'สร้างรายการชำระเงิน',     3),
                  ('payment.redirect',        'payment', N'เปิดหน้าชำระเงินให้ลูกค้า', 4),
                  ('merchant_user.roles.view',   'roles', N'ดูบทบาท',               5),
                  ('merchant_user.roles.manage', 'roles', N'สร้าง/แก้ไข/ลบบทบาท',     6),
                  ('merchant_user.user.roles',   'roles', N'กำหนดบทบาทให้ผู้ใช้',     7);
                """);

            // Seed the two anchor roles: merchant_owner grants every key (undeletable/undeactivatable recovery
            // anchor); merchant_member is the ordinary default approval choice (product/payment only, no
            // roles.*/user.roles). Status 0 = Active.
            migrationBuilder.Sql($"""
                INSERT INTO merch.Roles (Id, Code, Name, Description, Color, Status) VALUES
                  ('{MerchantOwnerRoleId}',  'merchant_owner',  N'เจ้าของร้าน',   N'เข้าถึงได้ทุกส่วนของร้าน รวมถึงการจัดการบทบาทและผู้ใช้', 'red',  0),
                  ('{MerchantMemberRoleId}', 'merchant_member', N'ผู้ใช้งานร้าน', N'จัดการสินค้าและการชำระเงิน (ไม่รวมการจัดการบทบาท)',    'blue', 0);

                INSERT INTO merch.RolePermissions (Id, RoleId, PermissionKey) VALUES
                  (NEWID(), '{MerchantOwnerRoleId}', 'product.create'),
                  (NEWID(), '{MerchantOwnerRoleId}', 'product.update'),
                  (NEWID(), '{MerchantOwnerRoleId}', 'payment.create'),
                  (NEWID(), '{MerchantOwnerRoleId}', 'payment.redirect'),
                  (NEWID(), '{MerchantOwnerRoleId}', 'merchant_user.roles.view'),
                  (NEWID(), '{MerchantOwnerRoleId}', 'merchant_user.roles.manage'),
                  (NEWID(), '{MerchantOwnerRoleId}', 'merchant_user.user.roles'),
                  (NEWID(), '{MerchantMemberRoleId}', 'product.create'),
                  (NEWID(), '{MerchantMemberRoleId}', 'product.update'),
                  (NEWID(), '{MerchantMemberRoleId}', 'payment.create'),
                  (NEWID(), '{MerchantMemberRoleId}', 'payment.redirect');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse order: children before parents (FK-safe), MerchantUser catalog first, then Admin master
            // data, then the Admin RBAC catalog.
            migrationBuilder.Sql($"""
                DELETE FROM merch.RolePermissions WHERE RoleId IN ('{MerchantOwnerRoleId}', '{MerchantMemberRoleId}');
                DELETE FROM merch.Roles WHERE Id     IN ('{MerchantOwnerRoleId}', '{MerchantMemberRoleId}');
                DELETE FROM merch.Permissions WHERE [Key] IN
                  ('product.create','product.update','payment.create','payment.redirect',
                   'merchant_user.roles.view','merchant_user.roles.manage','merchant_user.user.roles');
                DELETE FROM merch.PermissionGroups WHERE [Key] IN ('catalog','payment','roles');
                """);

            migrationBuilder.Sql("DELETE FROM admin.Positions WHERE Id LIKE 'a1000000-%';");
            migrationBuilder.Sql("DELETE FROM admin.Offices   WHERE Id LIKE 'b2000000-%';");
            migrationBuilder.Sql("DELETE FROM admin.Levels    WHERE Id LIKE 'c3000000-%';");
            migrationBuilder.Sql("DELETE FROM admin.Divisions WHERE Id LIKE 'd4000000-%';");

            migrationBuilder.Sql($"""
                DELETE FROM admin.RoleAssignments WHERE RoleId = '{SuperAdminRoleId}';
                DELETE FROM admin.RolePermissions WHERE RoleId IN
                  ('11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222',
                   '33333333-3333-3333-3333-333333333333', '44444444-4444-4444-4444-444444444444',
                   '55555555-5555-5555-5555-555555555555');
                DELETE FROM admin.Roles WHERE Id IN
                  ('11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222',
                   '33333333-3333-3333-3333-333333333333', '44444444-4444-4444-4444-444444444444',
                   '55555555-5555-5555-5555-555555555555');
                DELETE FROM admin.Permissions WHERE [Key] IN
                  ('txn.view','txn.refund','txn.export','merchant.view','merchant.manage',
                   'invoice.view','invoice.manage','settlement.run','user.view','user.manage','user.roles',
                   'audit.view','settings.manage','apikey.manage','merchant_user.approve','merchant_user.reject');
                DELETE FROM admin.PermissionGroups WHERE [Key] IN
                  ('txn','merchant','finance','user','system','merchant_user');
                """);
        }
    }
}
