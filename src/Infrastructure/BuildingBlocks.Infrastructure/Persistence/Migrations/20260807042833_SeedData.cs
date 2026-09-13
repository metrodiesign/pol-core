using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Dev-seeded central IAM catalog (rf2 design.md "Seed" section — the third hand-written migration, empty EF
    /// model so it never diffs). Seeds the SINGLE <c>iam.*</c> catalog that replaces the two duplicated
    /// <c>admin.*</c>/<c>merch.*</c> catalogs the pre-rf2 chain seeded: 19 permission keys in 7 groups (mirroring
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
            // --- Central IAM catalog: 7 groups / 19 keys (REQ-2.1; mirrors Keys.All + Keys.GroupScope — the
            // code-canonical vocabulary an integration test asserts never drifts). Scope: {txn, merchant, user,
            // system, merchants.users} = Platform (0); {payment, roles} = Merchant (1). ---
            migrationBuilder.Sql("""
                INSERT INTO iam.PermissionGroups ([Key], Scope, Name, Status, SortOrder) VALUES
                  ('txn',             0, N'ธุรกรรม',           0, 1),
                  ('merchant',        0, N'ร้านค้า',            0, 2),
                  ('user',            0, N'ผู้ใช้งาน',           0, 3),
                  ('system',          0, N'ระบบ',               0, 4),
                  ('merchants.users', 0, N'ผู้ใช้งานร้านค้า',     0, 5),
                  ('payment',         1, N'การชำระเงิน',        0, 6),
                  ('roles',           1, N'บทบาทและสิทธิ์',      0, 7);

                INSERT INTO iam.Permissions ([Key], GroupKey, Name, Status, SortOrder) VALUES
                  ('txn.view',                'txn',             N'ดูรายการธุรกรรม',           0, 1),
                  ('txn.refund',              'txn',             N'สั่งคืนเงิน',               0, 2),
                  ('txn.export',              'txn',             N'ส่งออกข้อมูลธุรกรรม',        0, 3),
                  ('merchant.view',           'merchant',        N'ดูข้อมูลร้านค้า',           0, 4),
                  ('merchant.manage',         'merchant',        N'เพิ่ม/แก้ไข/ระงับร้านค้า',  0, 5),
                  ('user.view',               'user',            N'ดูรายชื่อผู้ใช้งาน',        0, 6),
                  ('user.manage',             'user',            N'เปิด/แก้ไข/ปิดบัญชีผู้ใช้', 0, 7),
                  ('user.roles',              'user',            N'กำหนดบทบาทให้ผู้ใช้',       0, 8),
                  ('audit.view',              'system',          N'ดูบันทึกกิจกรรม (audit)',   0, 9),
                  ('settings.manage',         'system',          N'ตั้งค่าระบบและความปลอดภัย',  0, 10),
                  ('apikey.manage',           'system',          N'จัดการ API client / secret', 0, 11),
                  ('merchants.users.approve', 'merchants.users', N'อนุมัติผู้ใช้งานร้านค้า',   0, 12),
                  ('merchants.users.reject',  'merchants.users', N'ปฏิเสธผู้ใช้งานร้านค้า',    0, 13),
                  ('merchants.users.view',    'merchants.users', N'ดูประวัติการสมัครร้านค้า',   0, 14),
                  ('payment.create',          'payment',         N'สร้างรายการชำระเงิน',       0, 15),
                  ('payment.redirect',        'payment',         N'เปิดหน้าชำระเงินให้ลูกค้า',   0, 16),
                  ('roles.view',              'roles',           N'ดูบทบาท',                 0, 17),
                  ('roles.manage',            'roles',           N'สร้าง/แก้ไข/ลบบทบาท',       0, 18),
                  ('users.roles',             'roles',           N'กำหนดบทบาทให้ผู้ใช้',       0, 19);
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

            // Role -> permission grants per the design matrix. platform_admin = all 14 Platform keys;
            // platform_auditor = txn.view/merchant.view/user.view/audit.view; merchant_manager = all 5 Merchant
            // keys; merchant_staff = payment.create/payment.redirect. FK PermissionKey
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
                  (NEWID(), '{PlatformAdminRoleId}', 'merchants.users.view'),
                  (NEWID(), '{PlatformAuditorRoleId}', 'txn.view'),
                  (NEWID(), '{PlatformAuditorRoleId}', 'merchant.view'),
                  (NEWID(), '{PlatformAuditorRoleId}', 'user.view'),
                  (NEWID(), '{PlatformAuditorRoleId}', 'audit.view'),
                  (NEWID(), '{MerchantManagerRoleId}', 'payment.create'),
                  (NEWID(), '{MerchantManagerRoleId}', 'payment.redirect'),
                  (NEWID(), '{MerchantManagerRoleId}', 'roles.view'),
                  (NEWID(), '{MerchantManagerRoleId}', 'roles.manage'),
                  (NEWID(), '{MerchantManagerRoleId}', 'users.roles'),
                  (NEWID(), '{MerchantStaffRoleId}', 'payment.create'),
                  (NEWID(), '{MerchantStaffRoleId}', 'payment.redirect');
                """);

            // --- Admin master data: HR org lists (Positions/Offices/Levels/Divisions). Fixed, deterministic GUIDs
            // (never NEWID in a migration) so every environment shares the same ids and the PlatformUser FKs stay
            // stable. RFC-4122-well-formed, namespaced by table for readability. Carried verbatim from the pre-rf2
            // chain — rf2 replaced only the RBAC catalogs, not the HR seed (the CI fresh-DB gate pins these counts). ---
            migrationBuilder.Sql("""
                INSERT INTO cfg.Positions (Id, Code, Name, Status) VALUES
                  ('a1000000-0000-4000-8000-000000000001', 'ceo',               N'ประธานเจ้าหน้าที่บริหาร',            0),
                  ('a1000000-0000-4000-8000-000000000002', 'coo',               N'ประธานเจ้าหน้าที่ปฏิบัติการ',        0),
                  ('a1000000-0000-4000-8000-000000000003', 'cfo',               N'ประธานเจ้าหน้าที่การเงิน',           0),
                  ('a1000000-0000-4000-8000-000000000004', 'cto',               N'ประธานเจ้าหน้าที่เทคโนโลยีสารสนเทศ', 0),
                  ('a1000000-0000-4000-8000-000000000005', 'director',          N'ผู้อำนวยการ',                       0),
                  ('a1000000-0000-4000-8000-000000000006', 'deputy_director',   N'รองผู้อำนวยการ',                    0),
                  ('a1000000-0000-4000-8000-000000000007', 'manager',           N'ผู้จัดการ',                         0),
                  ('a1000000-0000-4000-8000-000000000008', 'assistant_manager', N'ผู้ช่วยผู้จัดการ',                   0),
                  ('a1000000-0000-4000-8000-000000000009', 'supervisor',        N'หัวหน้างาน',                        0),
                  ('a1000000-0000-4000-8000-00000000000a', 'senior_officer',    N'เจ้าหน้าที่อาวุโส',                 0),
                  ('a1000000-0000-4000-8000-00000000000b', 'officer',           N'เจ้าหน้าที่',                       0),
                  ('a1000000-0000-4000-8000-00000000000c', 'staff',             N'พนักงาน',                           0);
                """);

            migrationBuilder.Sql("""
                INSERT INTO cfg.Offices (Id, Code, Name, Status) VALUES
                  ('b2000000-0000-4000-8000-000000000001', 'hq',        N'สำนักงานใหญ่',                     0),
                  ('b2000000-0000-4000-8000-000000000002', 'north',     N'สำนักงานภาคเหนือ',                 0),
                  ('b2000000-0000-4000-8000-000000000003', 'northeast', N'สำนักงานภาคตะวันออกเฉียงเหนือ',    0),
                  ('b2000000-0000-4000-8000-000000000004', 'central',   N'สำนักงานภาคกลาง',                  0),
                  ('b2000000-0000-4000-8000-000000000005', 'east',      N'สำนักงานภาคตะวันออก',              0),
                  ('b2000000-0000-4000-8000-000000000006', 'west',      N'สำนักงานภาคตะวันตก',               0),
                  ('b2000000-0000-4000-8000-000000000007', 'south',     N'สำนักงานภาคใต้',                   0),
                  ('b2000000-0000-4000-8000-000000000008', 'remote',    N'ปฏิบัติงานนอกสถานที่',             0);
                """);

            migrationBuilder.Sql("""
                INSERT INTO cfg.Levels (Id, Code, Name, Status) VALUES
                  ('c3000000-0000-4000-8000-000000000001', 'level_1',  N'ระดับ 1',  0),
                  ('c3000000-0000-4000-8000-000000000002', 'level_2',  N'ระดับ 2',  0),
                  ('c3000000-0000-4000-8000-000000000003', 'level_3',  N'ระดับ 3',  0),
                  ('c3000000-0000-4000-8000-000000000004', 'level_4',  N'ระดับ 4',  0),
                  ('c3000000-0000-4000-8000-000000000005', 'level_5',  N'ระดับ 5',  0),
                  ('c3000000-0000-4000-8000-000000000006', 'level_6',  N'ระดับ 6',  0),
                  ('c3000000-0000-4000-8000-000000000007', 'level_7',  N'ระดับ 7',  0),
                  ('c3000000-0000-4000-8000-000000000008', 'level_8',  N'ระดับ 8',  0),
                  ('c3000000-0000-4000-8000-000000000009', 'level_9',  N'ระดับ 9',  0),
                  ('c3000000-0000-4000-8000-00000000000a', 'level_10', N'ระดับ 10', 0);
                """);

            migrationBuilder.Sql("""
                INSERT INTO cfg.Divisions (Id, Code, Name, Status) VALUES
                  ('d4000000-0000-4000-8000-000000000001', 'executive',        N'สำนักผู้บริหาร',                    0),
                  ('d4000000-0000-4000-8000-000000000002', 'finance',          N'ฝ่ายการเงินและบัญชี',               0),
                  ('d4000000-0000-4000-8000-000000000003', 'technology',       N'ฝ่ายเทคโนโลยีสารสนเทศ',             0),
                  ('d4000000-0000-4000-8000-000000000004', 'operations',       N'ฝ่ายปฏิบัติการ',                    0),
                  ('d4000000-0000-4000-8000-000000000005', 'product',          N'ฝ่ายผลิตภัณฑ์',                     0),
                  ('d4000000-0000-4000-8000-000000000006', 'sales_marketing',  N'ฝ่ายขายและการตลาด',                 0),
                  ('d4000000-0000-4000-8000-000000000007', 'risk_compliance',  N'ฝ่ายบริหารความเสี่ยงและกำกับดูแล',  0),
                  ('d4000000-0000-4000-8000-000000000008', 'legal',            N'ฝ่ายกฎหมาย',                        0),
                  ('d4000000-0000-4000-8000-000000000009', 'hr',               N'ฝ่ายทรัพยากรบุคคล',                 0),
                  ('d4000000-0000-4000-8000-00000000000a', 'customer_service', N'ฝ่ายบริการลูกค้า',                  0);
                """);

            // Supported demo anchor: synthetic merchant + disabled sandbox PSP reference. No credential,
            // user PII, Checkout, policy, or external-catalogue mirror data is seeded.
            migrationBuilder.Sql("""
                INSERT INTO merch.Merchants
                    (Id, Code, Name, Note, Status, Country, Currency, EnabledChannels, CreatedAt, Metadata)
                VALUES
                    ('e1000000-0000-4000-8000-000000000001', 'demo', N'ร้านค้าตัวอย่าง',
                     N'Synthetic baseline data', 0, 'TH', 'THB', 'card', SYSUTCDATETIME(), '{}');

                INSERT INTO txn.PspConnections
                    (Id, MerchantId, Psp, EnabledMethods, SecretRefName, Metadata, IsEnabled, CreatedAt)
                VALUES
                    ('e8000000-0000-4000-8000-000000000001',
                     'e1000000-0000-4000-8000-000000000001', 0, 'card',
                     'demo-disabled', NULL, 0, SYSUTCDATETIME());
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM txn.PspConnections WHERE Id = 'e8000000-0000-4000-8000-000000000001';
                DELETE FROM merch.Merchants WHERE Id = 'e1000000-0000-4000-8000-000000000001';
                """);

            // Children before parents (FK-safe): grants -> roles -> permissions -> groups.
            migrationBuilder.Sql($"""
                DELETE FROM iam.RolePermissions WHERE RoleId IN
                  ('{PlatformAdminRoleId}', '{PlatformAuditorRoleId}', '{MerchantManagerRoleId}', '{MerchantStaffRoleId}');
                DELETE FROM iam.Roles WHERE Id IN
                  ('{PlatformAdminRoleId}', '{PlatformAuditorRoleId}', '{MerchantManagerRoleId}', '{MerchantStaffRoleId}');
                DELETE FROM iam.Permissions WHERE [Key] IN
                  ('txn.view','txn.refund','txn.export','merchant.view','merchant.manage','user.view','user.manage',
                   'user.roles','audit.view','settings.manage','apikey.manage','merchants.users.approve',
                   'merchants.users.reject','merchants.users.view','payment.create','payment.redirect',
                   'roles.view','roles.manage','users.roles');
                DELETE FROM iam.PermissionGroups WHERE [Key] IN
                  ('txn','merchant','user','system','merchants.users','payment','roles');
                """);
            migrationBuilder.Sql("DELETE FROM cfg.Positions WHERE Id LIKE 'a1000000-%';");
            migrationBuilder.Sql("DELETE FROM cfg.Offices   WHERE Id LIKE 'b2000000-%';");
            migrationBuilder.Sql("DELETE FROM cfg.Levels    WHERE Id LIKE 'c3000000-%';");
            migrationBuilder.Sql("DELETE FROM cfg.Divisions WHERE Id LIKE 'd4000000-%';");
        }
    }
}

