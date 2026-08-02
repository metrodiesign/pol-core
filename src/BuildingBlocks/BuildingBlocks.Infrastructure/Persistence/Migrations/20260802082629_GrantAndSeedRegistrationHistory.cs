using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // registration-attempt-history task 2 (REQ-4.1/4.4 + REQ-1.7's DB layer). merch.RegistrationAttempts is
    // brand new (AddRegistrationAttempts), so pol_app — the sole runtime principal — has no grant on it yet;
    // the table is append-only (AppendOnlyDescriptor-guarded) -> SELECT, INSERT only, same shape as the other
    // audit tables. The new merchants.users.view key joins the EXISTING merchants.users group (no new group);
    // SortOrder 25 (used values are 1-24 — SeedData 1-20 + SeedPolicyPermissions 21-24; 14/15 were deleted
    // without reflowing, so the row count is NOT the next SortOrder). Granted to platform_admin only — the
    // same role that holds merchants.users.approve/reject (REQ-4.1; platform_auditor holds neither).
    public partial class GrantAndSeedRegistrationHistory : Migration
    {
        private const string PlatformAdminRoleId = "11111111-1111-1111-1111-111111111111";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT ON merch.RegistrationAttempts TO pol_app;
                """);

            migrationBuilder.Sql($"""
                INSERT INTO iam.Permissions ([Key], GroupKey, LabelTh, SortOrder) VALUES
                  ('merchants.users.view', 'merchants.users', N'ดูประวัติการลงทะเบียนผู้ใช้งานร้านค้า', 25);

                INSERT INTO iam.RolePermissions (Id, RoleId, PermissionKey) VALUES
                  (NEWID(), '{PlatformAdminRoleId}', 'merchants.users.view');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Children before parents (FK-safe): grants -> permission; then revoke the table grant.
            migrationBuilder.Sql("""
                DELETE FROM iam.RolePermissions WHERE PermissionKey = 'merchants.users.view';
                DELETE FROM iam.Permissions WHERE [Key] = 'merchants.users.view';
                REVOKE SELECT, INSERT ON merch.RegistrationAttempts FROM pol_app;
                """);
        }
    }
}
