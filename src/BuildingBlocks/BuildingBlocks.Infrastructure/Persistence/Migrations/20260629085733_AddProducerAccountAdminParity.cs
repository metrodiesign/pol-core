using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProducerAccountAdminParity : Migration
    {
        // Moves the producer ACCOUNT off the tenant RLS floor and onto the control plane, mirroring Admin
        // (AdminAccount + AdminTenantAssignment). The account table (formerly the RLS-keyed VCentralPay.TenantUsers) is
        // renamed in place to VCentralPay.ProducerAccounts, its TenantId column is migrated into a new
        // VCentralPay.ProducerTenantAssignments edge (UNIQUE on ProducerAccountId — one tenant per producer), and the
        // RLS predicates + pol_app read grant are removed. Child FK columns TenantUserId -> ProducerAccountId are
        // renamed in place (sp_rename — data preserved). The integration-event payload + the Admin-side
        // ProducerRegistrationNotices table keep their TenantUserId column (a message identity, out of scope here).

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 0) Close the read FIRST. pol_app's SELECT grant + the RLS predicates together scope a tenant principal to
            //    its own rows; dropping the predicates (step 1) while the grant still stands would, in the window before
            //    the later REVOKE, let pol_app read EVERY producer account unfiltered. ALTER SECURITY POLICY is
            //    non-transactional, so an apply interrupted after step 1 but before that REVOKE would LEAVE the DB in
            //    exactly that leaky state. Revoking up front means the predicate drop can never widen access. REVOKE of
            //    an absent grant is a harmless no-op, so a retried apply is safe.
            migrationBuilder.Sql("REVOKE SELECT ON VCentralPay.TenantUsers FROM pol_app;");

            // 1) Detach the RLS predicates from the account table — a table under a security policy cannot be renamed
            //    or have its predicated column dropped. Never drop the shared TenantIsolationPolicy itself. Guarded by
            //    IF EXISTS: ALTER SECURITY POLICY is not reliably rolled back with the surrounding migration
            //    transaction, so a retried apply (e.g. a prior attempt that failed AFTER this committed) must be a
            //    no-op rather than error with "policy does not contain a predicate on TenantUsers".
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM sys.security_predicates sp
                           JOIN sys.security_policies p ON sp.object_id = p.object_id
                           WHERE p.name = 'TenantIsolationPolicy' AND sp.target_object_id = OBJECT_ID('VCentralPay.TenantUsers'))
                    ALTER SECURITY POLICY VCentralPay.TenantIsolationPolicy
                        DROP FILTER PREDICATE ON VCentralPay.TenantUsers,
                        DROP BLOCK PREDICATE ON VCentralPay.TenantUsers AFTER INSERT,
                        DROP BLOCK PREDICATE ON VCentralPay.TenantUsers AFTER UPDATE;
                """);

            // 2) Rename the account table + its PK/unique index in place (data preserved). Object permissions follow
            //    the object id across a rename; pol_app's SELECT was already revoked up front (step 0).
            migrationBuilder.Sql("""
                EXEC sp_rename 'VCentralPay.TenantUsers', 'ProducerAccounts';
                EXEC sp_rename 'VCentralPay.PK_TenantUsers', 'PK_ProducerAccounts';
                EXEC sp_rename 'VCentralPay.ProducerAccounts.IX_TenantUsers_Subject', 'IX_ProducerAccounts_Subject', 'INDEX';
                """);

            // 3) Child FK column + index renames TenantUserId -> ProducerAccountId (sp_rename under the hood; data preserved).
            migrationBuilder.RenameColumn(
                name: "TenantUserId", schema: "VCentralPay", table: "TenantUserProfiles", newName: "ProducerAccountId");
            migrationBuilder.RenameIndex(
                name: "IX_TenantUserProfiles_TenantUserId", schema: "VCentralPay", table: "TenantUserProfiles",
                newName: "IX_TenantUserProfiles_ProducerAccountId");

            migrationBuilder.RenameColumn(
                name: "TenantUserId", schema: "VCentralPay", table: "ProducerSessions", newName: "ProducerAccountId");
            migrationBuilder.RenameIndex(
                name: "IX_ProducerSessions_TenantUserId", schema: "VCentralPay", table: "ProducerSessions",
                newName: "IX_ProducerSessions_ProducerAccountId");

            migrationBuilder.RenameColumn(
                name: "TenantUserId", schema: "VCentralPay", table: "ProducerRoleAssignments", newName: "ProducerAccountId");
            migrationBuilder.RenameIndex(
                name: "IX_ProducerRoleAssignments_TenantUserId_TenantId", schema: "VCentralPay", table: "ProducerRoleAssignments",
                newName: "IX_ProducerRoleAssignments_ProducerAccountId_TenantId");
            migrationBuilder.RenameIndex(
                name: "IX_ProducerRoleAssignments_TenantUserId_RoleId", schema: "VCentralPay", table: "ProducerRoleAssignments",
                newName: "IX_ProducerRoleAssignments_ProducerAccountId_RoleId");

            migrationBuilder.RenameColumn(
                name: "TenantUserId", schema: "VCentralPay", table: "ProducerAuthAudits", newName: "ProducerAccountId");
            migrationBuilder.RenameIndex(
                name: "IX_ProducerAuthAudits_TenantUserId", schema: "VCentralPay", table: "ProducerAuthAudits",
                newName: "IX_ProducerAuthAudits_ProducerAccountId");

            migrationBuilder.RenameColumn(
                name: "TenantUserId", schema: "VCentralPay", table: "ExternalLogins", newName: "ProducerAccountId");

            // 4) The new tenant edge (control-plane). UNIQUE on ProducerAccountId => one tenant per producer account.
            migrationBuilder.CreateTable(
                name: "ProducerTenantAssignments",
                schema: "VCentralPay",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProducerAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignedByAdminId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProducerTenantAssignments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProducerTenantAssignments_ProducerAccountId",
                schema: "VCentralPay",
                table: "ProducerTenantAssignments",
                column: "ProducerAccountId",
                unique: true);

            // 5) Backfill one edge per already-approved account (TenantId NOT NULL — Active/bound). The original
            //    approving admin is unknown post-hoc, so AssignedByAdminId is the empty guid (migrated marker);
            //    AssignedAt reuses the account's CreatedAt.
            migrationBuilder.Sql("""
                INSERT INTO VCentralPay.ProducerTenantAssignments (Id, ProducerAccountId, TenantId, AssignedByAdminId, AssignedAt)
                SELECT NEWID(), Id, TenantId, CAST('00000000-0000-0000-0000-000000000000' AS uniqueidentifier), CreatedAt
                FROM VCentralPay.ProducerAccounts
                WHERE TenantId IS NOT NULL;
                """);

            // 6) Drop the migrated TenantId column off the account (now lives only on the edge).
            migrationBuilder.DropColumn(name: "TenantId", schema: "VCentralPay", table: "ProducerAccounts");

            // 7) Grants: the account table is control-plane now — pol_app's read was already revoked up front (step 0);
            //    pol_admin keeps CRUD (the pre-rename grant carried over) and gains the edge table.
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE ON VCentralPay.ProducerAccounts TO pol_admin;
                GRANT SELECT, INSERT ON VCentralPay.ProducerTenantAssignments TO pol_admin;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 1) Restore the TenantId column on the account and backfill it from the edge before dropping the edge.
            migrationBuilder.AddColumn<Guid>(
                name: "TenantId", schema: "VCentralPay", table: "ProducerAccounts", type: "uniqueidentifier", nullable: true);

            migrationBuilder.Sql("""
                UPDATE pa SET pa.TenantId = a.TenantId
                FROM VCentralPay.ProducerAccounts pa
                JOIN VCentralPay.ProducerTenantAssignments a ON a.ProducerAccountId = pa.Id;
                """);

            migrationBuilder.DropTable(name: "ProducerTenantAssignments", schema: "VCentralPay");

            // 2) Reverse the child FK column + index renames.
            migrationBuilder.RenameColumn(
                name: "ProducerAccountId", schema: "VCentralPay", table: "TenantUserProfiles", newName: "TenantUserId");
            migrationBuilder.RenameIndex(
                name: "IX_TenantUserProfiles_ProducerAccountId", schema: "VCentralPay", table: "TenantUserProfiles",
                newName: "IX_TenantUserProfiles_TenantUserId");

            migrationBuilder.RenameColumn(
                name: "ProducerAccountId", schema: "VCentralPay", table: "ProducerSessions", newName: "TenantUserId");
            migrationBuilder.RenameIndex(
                name: "IX_ProducerSessions_ProducerAccountId", schema: "VCentralPay", table: "ProducerSessions",
                newName: "IX_ProducerSessions_TenantUserId");

            migrationBuilder.RenameColumn(
                name: "ProducerAccountId", schema: "VCentralPay", table: "ProducerRoleAssignments", newName: "TenantUserId");
            migrationBuilder.RenameIndex(
                name: "IX_ProducerRoleAssignments_ProducerAccountId_TenantId", schema: "VCentralPay", table: "ProducerRoleAssignments",
                newName: "IX_ProducerRoleAssignments_TenantUserId_TenantId");
            migrationBuilder.RenameIndex(
                name: "IX_ProducerRoleAssignments_ProducerAccountId_RoleId", schema: "VCentralPay", table: "ProducerRoleAssignments",
                newName: "IX_ProducerRoleAssignments_TenantUserId_RoleId");

            migrationBuilder.RenameColumn(
                name: "ProducerAccountId", schema: "VCentralPay", table: "ProducerAuthAudits", newName: "TenantUserId");
            migrationBuilder.RenameIndex(
                name: "IX_ProducerAuthAudits_ProducerAccountId", schema: "VCentralPay", table: "ProducerAuthAudits",
                newName: "IX_ProducerAuthAudits_TenantUserId");

            migrationBuilder.RenameColumn(
                name: "ProducerAccountId", schema: "VCentralPay", table: "ExternalLogins", newName: "TenantUserId");

            // 3) Rename the account table + PK/index back to VCentralPay.TenantUsers.
            migrationBuilder.Sql("""
                EXEC sp_rename 'VCentralPay.ProducerAccounts.IX_ProducerAccounts_Subject', 'IX_TenantUsers_Subject', 'INDEX';
                EXEC sp_rename 'VCentralPay.PK_ProducerAccounts', 'PK_TenantUsers';
                EXEC sp_rename 'VCentralPay.ProducerAccounts', 'TenantUsers';
                """);

            // 4) Re-attach the RLS predicates and restore the pol_app read grant. Guarded by NOT EXISTS so a retried
            //    revert is a no-op rather than erroring with "policy already contains a predicate".
            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.security_predicates sp
                               JOIN sys.security_policies p ON sp.object_id = p.object_id
                               WHERE p.name = 'TenantIsolationPolicy' AND sp.target_object_id = OBJECT_ID('VCentralPay.TenantUsers'))
                    ALTER SECURITY POLICY VCentralPay.TenantIsolationPolicy
                        ADD FILTER PREDICATE VCentralPay.fn_tenant_predicate(TenantId) ON VCentralPay.TenantUsers,
                        ADD BLOCK PREDICATE VCentralPay.fn_tenant_predicate(TenantId) ON VCentralPay.TenantUsers AFTER INSERT,
                        ADD BLOCK PREDICATE VCentralPay.fn_tenant_predicate(TenantId) ON VCentralPay.TenantUsers AFTER UPDATE;
                """);

            migrationBuilder.Sql("GRANT SELECT ON VCentralPay.TenantUsers TO pol_app;");
        }
    }
}
