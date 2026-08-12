using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdminConsolePermissionKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO iam.Permissions ([Key], GroupKey, Name, Status, SortOrder) VALUES
                  ('txn.manage',             'txn',             N'จัดการธุรกรรม',             1, 23),
                  ('merchants.users.manage', 'merchants.users', N'จัดการผู้ใช้งานร้านค้า',    1, 24),
                  ('merchants.roles.view',   'merchants.users', N'ดูบทบาทผู้ใช้งานร้านค้า',    1, 25),
                  ('merchants.roles.manage', 'merchants.users', N'จัดการบทบาทผู้ใช้งานร้านค้า', 1, 26);

                INSERT INTO iam.RolePermissions (Id, RoleId, PermissionKey) VALUES
                  ('f9000000-0000-4000-8000-000000000001', '11111111-1111-1111-1111-111111111111', 'txn.manage'),
                  ('f9000000-0000-4000-8000-000000000002', '11111111-1111-1111-1111-111111111111', 'merchants.users.manage'),
                  ('f9000000-0000-4000-8000-000000000003', '11111111-1111-1111-1111-111111111111', 'merchants.roles.view'),
                  ('f9000000-0000-4000-8000-000000000004', '11111111-1111-1111-1111-111111111111', 'merchants.roles.manage');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM iam.RolePermissions
                WHERE PermissionKey IN (
                  'txn.manage',
                  'merchants.users.manage',
                  'merchants.roles.view',
                  'merchants.roles.manage');

                DELETE FROM iam.Permissions
                WHERE [Key] IN (
                  'txn.manage',
                  'merchants.users.manage',
                  'merchants.roles.view',
                  'merchants.roles.manage');
                """);
        }
    }
}
