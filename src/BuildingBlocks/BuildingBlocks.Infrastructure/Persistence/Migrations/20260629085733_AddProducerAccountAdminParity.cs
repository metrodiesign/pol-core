using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProducerAccountAdminParity : Migration
    {
        // Moves the producer ACCOUNT off the tenant RLS floor and onto the control plane, mirroring Admin
        // (AdminAccount + AdminTenantAssignment). The account table (formerly the RLS-keyed producer.TenantUsers) is
        // renamed in place to producer.ProducerAccounts, its TenantId column is migrated into a new
        // producer.ProducerTenantAssignments edge (UNIQUE on ProducerAccountId — one tenant per producer), and the
        // RLS predicates + pol_app read grant are removed. Child FK columns TenantUserId -> ProducerAccountId are
        // renamed in place (sp_rename — data preserved). The integration-event payload + the Admin-side
        // ProducerRegistrationNotices table keep their TenantUserId column (a message identity, out of scope here).

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) Detach the RLS predicates from the account table — a table under a security policy cannot be renamed
            //    or have its predicated column dropped. Never drop the shared TenantIsolationPolicy itself. Guarded by
            //    IF EXISTS: ALTER SECURITY POLICY is not reliably rolled back with the surrounding migration
            //    transaction, so a retried apply (e.g. a prior attempt that failed AFTER this committed) must be a
            //    no-op rather than error with "policy does not contain a predicate on TenantUsers".
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM sys.security_predicates sp
                           JOIN sys.security_policies p ON sp.object_id = p.object_id
                           WHERE p.name = 'TenantIsolationPolicy' AND sp.target_object_id = OBJECT_ID('producer.TenantUsers'))
                    ALTER SECURITY POLICY producer.TenantIsolationPolicy
                        DROP FILTER PREDICATE ON producer.TenantUsers,
                        DROP BLOCK PREDICATE ON producer.TenantUsers AFTER INSERT,
                        DROP BLOCK PREDICATE ON producer.TenantUsers AFTER UPDATE;
                """);

            // 2) Rename the account table + its PK/unique index in place (data preserved). Object permissions follow
            //    the object id across a rename, so pol_app still holds its old SELECT here — revoked in step 7.
            migrationBuilder.Sql("""
                EXEC sp_rename 'producer.TenantUsers', 'ProducerAccounts';
                EXEC sp_rename 'producer.PK_TenantUsers', 'PK_ProducerAccounts';
                EXEC sp_rename 'producer.ProducerAccounts.IX_TenantUsers_Subject', 'IX_ProducerAccounts_Subject', 'INDEX';
                """);

            // 3) Child FK column + index renames TenantUserId -> ProducerAccountId (sp_rename under the hood; data preserved).
            migrationBuilder.RenameColumn(
                name: "TenantUserId", schema: "producer", table: "TenantUserProfiles", newName: "ProducerAccountId");
            migrationBuilder.RenameIndex(
                name: "IX_TenantUserProfiles_TenantUserId", schema: "producer", table: "TenantUserProfiles",
                newName: "IX_TenantUserProfiles_ProducerAccountId");

            migrationBuilder.RenameColumn(
                name: "TenantUserId", schema: "producer", table: "ProducerSessions", newName: "ProducerAccountId");
            migrationBuilder.RenameIndex(
                name: "IX_ProducerSessions_TenantUserId", schema: "producer", table: "ProducerSessions",
                newName: "IX_ProducerSessions_ProducerAccountId");

            migrationBuilder.RenameColumn(
                name: "TenantUserId", schema: "producer", table: "ProducerRoleAssignments", newName: "ProducerAccountId");
            migrationBuilder.RenameIndex(
                name: "IX_ProducerRoleAssignments_TenantUserId_TenantId", schema: "producer", table: "ProducerRoleAssignments",
                newName: "IX_ProducerRoleAssignments_ProducerAccountId_TenantId");
            migrationBuilder.RenameIndex(
                name: "IX_ProducerRoleAssignments_TenantUserId_RoleId", schema: "producer", table: "ProducerRoleAssignments",
                newName: "IX_ProducerRoleAssignments_ProducerAccountId_RoleId");

            migrationBuilder.RenameColumn(
                name: "TenantUserId", schema: "producer", table: "ProducerAuthAudits", newName: "ProducerAccountId");
            migrationBuilder.RenameIndex(
                name: "IX_ProducerAuthAudits_TenantUserId", schema: "producer", table: "ProducerAuthAudits",
                newName: "IX_ProducerAuthAudits_ProducerAccountId");

            migrationBuilder.RenameColumn(
                name: "TenantUserId", schema: "producer", table: "ExternalLogins", newName: "ProducerAccountId");

            // 4) The new tenant edge (control-plane). UNIQUE on ProducerAccountId => one tenant per producer account.
            migrationBuilder.CreateTable(
                name: "ProducerTenantAssignments",
                schema: "producer",
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
                schema: "producer",
                table: "ProducerTenantAssignments",
                column: "ProducerAccountId",
                unique: true);

            // 5) Backfill one edge per already-approved account (TenantId NOT NULL — Active/bound). The original
            //    approving admin is unknown post-hoc, so AssignedByAdminId is the empty guid (migrated marker);
            //    AssignedAt reuses the account's CreatedAt.
            migrationBuilder.Sql("""
                INSERT INTO producer.ProducerTenantAssignments (Id, ProducerAccountId, TenantId, AssignedByAdminId, AssignedAt)
                SELECT NEWID(), Id, TenantId, CAST('00000000-0000-0000-0000-000000000000' AS uniqueidentifier), CreatedAt
                FROM producer.ProducerAccounts
                WHERE TenantId IS NOT NULL;
                """);

            // 6) Drop the migrated TenantId column off the account (now lives only on the edge).
            migrationBuilder.DropColumn(name: "TenantId", schema: "producer", table: "ProducerAccounts");

            // 7) Grants: the account table is control-plane now — pol_app loses its read; pol_admin keeps CRUD (the
            //    pre-rename grant carried over) and gains the edge table.
            migrationBuilder.Sql("""
                REVOKE SELECT ON producer.ProducerAccounts FROM pol_app;
                GRANT SELECT, INSERT, UPDATE ON producer.ProducerAccounts TO pol_admin;
                GRANT SELECT, INSERT ON producer.ProducerTenantAssignments TO pol_admin;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 1) Restore the TenantId column on the account and backfill it from the edge before dropping the edge.
            migrationBuilder.AddColumn<Guid>(
                name: "TenantId", schema: "producer", table: "ProducerAccounts", type: "uniqueidentifier", nullable: true);

            migrationBuilder.Sql("""
                UPDATE pa SET pa.TenantId = a.TenantId
                FROM producer.ProducerAccounts pa
                JOIN producer.ProducerTenantAssignments a ON a.ProducerAccountId = pa.Id;
                """);

            migrationBuilder.DropTable(name: "ProducerTenantAssignments", schema: "producer");

            // 2) Reverse the child FK column + index renames.
            migrationBuilder.RenameColumn(
                name: "ProducerAccountId", schema: "producer", table: "TenantUserProfiles", newName: "TenantUserId");
            migrationBuilder.RenameIndex(
                name: "IX_TenantUserProfiles_ProducerAccountId", schema: "producer", table: "TenantUserProfiles",
                newName: "IX_TenantUserProfiles_TenantUserId");

            migrationBuilder.RenameColumn(
                name: "ProducerAccountId", schema: "producer", table: "ProducerSessions", newName: "TenantUserId");
            migrationBuilder.RenameIndex(
                name: "IX_ProducerSessions_ProducerAccountId", schema: "producer", table: "ProducerSessions",
                newName: "IX_ProducerSessions_TenantUserId");

            migrationBuilder.RenameColumn(
                name: "ProducerAccountId", schema: "producer", table: "ProducerRoleAssignments", newName: "TenantUserId");
            migrationBuilder.RenameIndex(
                name: "IX_ProducerRoleAssignments_ProducerAccountId_TenantId", schema: "producer", table: "ProducerRoleAssignments",
                newName: "IX_ProducerRoleAssignments_TenantUserId_TenantId");
            migrationBuilder.RenameIndex(
                name: "IX_ProducerRoleAssignments_ProducerAccountId_RoleId", schema: "producer", table: "ProducerRoleAssignments",
                newName: "IX_ProducerRoleAssignments_TenantUserId_RoleId");

            migrationBuilder.RenameColumn(
                name: "ProducerAccountId", schema: "producer", table: "ProducerAuthAudits", newName: "TenantUserId");
            migrationBuilder.RenameIndex(
                name: "IX_ProducerAuthAudits_ProducerAccountId", schema: "producer", table: "ProducerAuthAudits",
                newName: "IX_ProducerAuthAudits_TenantUserId");

            migrationBuilder.RenameColumn(
                name: "ProducerAccountId", schema: "producer", table: "ExternalLogins", newName: "TenantUserId");

            // 3) Rename the account table + PK/index back to producer.TenantUsers.
            migrationBuilder.Sql("""
                EXEC sp_rename 'producer.ProducerAccounts.IX_ProducerAccounts_Subject', 'IX_TenantUsers_Subject', 'INDEX';
                EXEC sp_rename 'producer.PK_ProducerAccounts', 'PK_TenantUsers';
                EXEC sp_rename 'producer.ProducerAccounts', 'TenantUsers';
                """);

            // 4) Re-attach the RLS predicates and restore the pol_app read grant. Guarded by NOT EXISTS so a retried
            //    revert is a no-op rather than erroring with "policy already contains a predicate".
            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.security_predicates sp
                               JOIN sys.security_policies p ON sp.object_id = p.object_id
                               WHERE p.name = 'TenantIsolationPolicy' AND sp.target_object_id = OBJECT_ID('producer.TenantUsers'))
                    ALTER SECURITY POLICY producer.TenantIsolationPolicy
                        ADD FILTER PREDICATE producer.fn_tenant_predicate(TenantId) ON producer.TenantUsers,
                        ADD BLOCK PREDICATE producer.fn_tenant_predicate(TenantId) ON producer.TenantUsers AFTER INSERT,
                        ADD BLOCK PREDICATE producer.fn_tenant_predicate(TenantId) ON producer.TenantUsers AFTER UPDATE;
                """);

            migrationBuilder.Sql("GRANT SELECT ON producer.TenantUsers TO pol_app;");
        }
    }
}
