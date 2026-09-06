using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SharedRoleScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Roles_ScopeMerchant",
                schema: "iam",
                table: "Roles");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Roles_ScopeMerchant",
                schema: "iam",
                table: "Roles",
                sql: "([Scope] IN (1, 3) AND [MerchantId] IS NULL) OR [Scope] = 2");

            // Scope 3 = Shared: the commerce vocabulary (group payment) and the merchant_staff seed role are held
            // in common by Tier 0 (workforce admin) and Tier 1 (agent/broker). txn.manage — the admin-side twin of
            // payment.create on the dual-console sites — gated nothing once both tiers pass the same shared key,
            // so it is retired; platform_admin / platform_auditor pick up the shared keys they used it for.
            migrationBuilder.Sql("""
                UPDATE iam.PermissionGroups SET Scope = 3 WHERE [Key] = 'payment';
                UPDATE iam.Roles SET Scope = 3 WHERE Id = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb' AND Code = 'merchant_staff';

                DELETE FROM iam.RolePermissions WHERE PermissionKey = 'txn.manage';
                DELETE FROM iam.Permissions WHERE [Key] = 'txn.manage';

                INSERT INTO iam.RolePermissions (Id, RoleId, PermissionKey) VALUES
                  ('f9000000-0000-4000-8000-000000000005', '11111111-1111-1111-1111-111111111111', 'payment.view'),
                  ('f9000000-0000-4000-8000-000000000006', '11111111-1111-1111-1111-111111111111', 'payment.create'),
                  ('f9000000-0000-4000-8000-000000000007', '11111111-1111-1111-1111-111111111111', 'payment.redirect'),
                  ('f9000000-0000-4000-8000-000000000008', '55555555-5555-5555-5555-555555555555', 'payment.view');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM iam.RolePermissions WHERE Id IN (
                  'f9000000-0000-4000-8000-000000000005',
                  'f9000000-0000-4000-8000-000000000006',
                  'f9000000-0000-4000-8000-000000000007',
                  'f9000000-0000-4000-8000-000000000008');

                INSERT INTO iam.Permissions ([Key], GroupKey, Name, Status, SortOrder) VALUES
                  ('txn.manage', 'txn', N'จัดการธุรกรรม', 1, 23);
                INSERT INTO iam.RolePermissions (Id, RoleId, PermissionKey) VALUES
                  ('f9000000-0000-4000-8000-000000000001', '11111111-1111-1111-1111-111111111111', 'txn.manage');

                UPDATE iam.Roles SET Scope = 2 WHERE Id = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb' AND Code = 'merchant_staff';
                UPDATE iam.PermissionGroups SET Scope = 2 WHERE [Key] = 'payment';
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_Roles_ScopeMerchant",
                schema: "iam",
                table: "Roles");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Roles_ScopeMerchant",
                schema: "iam",
                table: "Roles",
                sql: "([Scope] = 1 AND [MerchantId] IS NULL) OR [Scope] = 2");
        }
    }
}
