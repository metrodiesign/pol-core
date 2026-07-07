using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProducerApprovePermissionToAdminCatalog : Migration
    {
        // producer-google-sso REQ-18.1: the producer-approval permission lives in the ADMIN catalog (the approver is
        // an Admin, gated by the Admin RequirePermission). A DATA-only migration (no model change) — it adds a new
        // Admin permission group `producer` + the keys `producer.approve`/`producer.reject` (mirroring the new
        // AdminPermissions.cs entries; a test asserts code and DB never drift) and seed-grants both to the
        // `super_admin` role so the bootstrap Super can approve the first producer. Idempotent INSERTs via NOT EXISTS
        // so a re-run (or a hand-applied scratch DB) does not double-insert.
        private const string SuperAdminRoleId = "11111111-1111-1111-1111-111111111111";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO VCentralPay.AdminPermissionGroups ([Key], LabelTh, SortOrder)
                SELECT 'producer', N'ผู้ผลิต', 6
                WHERE NOT EXISTS (SELECT 1 FROM VCentralPay.AdminPermissionGroups WHERE [Key] = 'producer');

                INSERT INTO VCentralPay.AdminPermissions ([Key], GroupKey, LabelTh, SortOrder)
                SELECT v.[Key], 'producer', v.LabelTh, v.SortOrder
                FROM (VALUES
                    ('producer.approve', N'อนุมัติผู้ผลิต', 15),
                    ('producer.reject',  N'ปฏิเสธผู้ผลิต',  16)
                ) AS v([Key], LabelTh, SortOrder)
                WHERE NOT EXISTS (SELECT 1 FROM VCentralPay.AdminPermissions p WHERE p.[Key] = v.[Key]);
                """);

            // Seed-grant both keys to super_admin (REQ-18.1) so the bootstrap Super can approve the first producer.
            migrationBuilder.Sql($"""
                INSERT INTO VCentralPay.AdminRolePermissions (Id, RoleId, PermissionKey)
                SELECT NEWID(), '{SuperAdminRoleId}', v.[Key]
                FROM (VALUES ('producer.approve'), ('producer.reject')) AS v([Key])
                WHERE NOT EXISTS (
                    SELECT 1 FROM VCentralPay.AdminRolePermissions x
                    WHERE x.RoleId = '{SuperAdminRoleId}' AND x.PermissionKey = v.[Key]);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse order: the grants (FK -> AdminPermissions) before the permission rows, then the group last.
            migrationBuilder.Sql($"""
                DELETE FROM VCentralPay.AdminRolePermissions
                WHERE RoleId = '{SuperAdminRoleId}' AND PermissionKey IN ('producer.approve', 'producer.reject');

                DELETE FROM VCentralPay.AdminPermissions WHERE [Key] IN ('producer.approve', 'producer.reject');
                DELETE FROM VCentralPay.AdminPermissionGroups WHERE [Key] = 'producer';
                """);
        }
    }
}
