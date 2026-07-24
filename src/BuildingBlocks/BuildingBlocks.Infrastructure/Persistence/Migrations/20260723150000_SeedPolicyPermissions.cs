using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // policy-reference-record task 3 (REQ-3.2, 3.6, 4.2): adds 2 permission groups / 4 keys onto the existing
    // central iam.* catalog (mirrors the SeedData migration's structure, hand-written per HANDOFF sharp edge #3 —
    // no dotnet ef scaffold, no EF model diff, raw SQL only). merchants.policies (Platform) is the admin
    // cross-merchant surface task 5/6 gate on; policies (Merchant) is the producer self-scope surface task 4/6
    // gate on. Role ids are the same stable GUIDs SeedData.cs seeded.
    public partial class SeedPolicyPermissions : Migration
    {
        private const string PlatformAdminRoleId = "11111111-1111-1111-1111-111111111111";
        private const string PlatformAuditorRoleId = "55555555-5555-5555-5555-555555555555";
        private const string MerchantManagerRoleId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        private const string MerchantStaffRoleId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO iam.PermissionGroups ([Key], Scope, LabelTh, SortOrder) VALUES
                  ('merchants.policies', 0, N'กรมธรรม์ร้านค้า', 9),
                  ('policies',           1, N'กรมธรรม์',         10);

                INSERT INTO iam.Permissions ([Key], GroupKey, LabelTh, SortOrder) VALUES
                  ('merchants.policies.read',  'merchants.policies', N'ดูข้อมูลกรมธรรม์ร้านค้า',   21),
                  ('merchants.policies.write', 'merchants.policies', N'แก้ไขข้อมูลกรมธรรม์ร้านค้า', 22),
                  ('policies.read',            'policies',           N'ดูข้อมูลกรมธรรม์',          23),
                  ('policies.write',           'policies',           N'แก้ไขข้อมูลกรมธรรม์',        24);
                """);

            // Role grants (design matrix): platform_admin +read+write, platform_auditor +read,
            // merchant_manager +read+write, merchant_staff +read.
            migrationBuilder.Sql($"""
                INSERT INTO iam.RolePermissions (Id, RoleId, PermissionKey) VALUES
                  (NEWID(), '{PlatformAdminRoleId}',   'merchants.policies.read'),
                  (NEWID(), '{PlatformAdminRoleId}',   'merchants.policies.write'),
                  (NEWID(), '{PlatformAuditorRoleId}', 'merchants.policies.read'),
                  (NEWID(), '{MerchantManagerRoleId}', 'policies.read'),
                  (NEWID(), '{MerchantManagerRoleId}', 'policies.write'),
                  (NEWID(), '{MerchantStaffRoleId}',   'policies.read');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Children before parents (FK-safe): grants -> permissions -> groups.
            migrationBuilder.Sql("""
                DELETE FROM iam.RolePermissions WHERE PermissionKey IN
                  ('merchants.policies.read','merchants.policies.write','policies.read','policies.write');
                DELETE FROM iam.Permissions WHERE [Key] IN
                  ('merchants.policies.read','merchants.policies.write','policies.read','policies.write');
                DELETE FROM iam.PermissionGroups WHERE [Key] IN ('merchants.policies','policies');
                """);
        }
    }
}
