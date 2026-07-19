using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// rls-to-query-filter task 8: the cutover. Tears down SQL Server Row-Level Security (policy, 3
    /// predicate functions, 3 procs, the bypass role) in favor of the app-layer EF query-filter floor
    /// tasks 2-7 built as scaffolding, collapses 5 DB principals to 1 (<c>pol_app</c> — the owner-confirmed
    /// "1 principal" sign-off, design.md "Human sign-off item — RESOLVED"), and lands every column/table
    /// tasks 2-7 assumed but never migrated: <c>shop.CartItems.MerchantId</c> (+ backfill + composite FK),
    /// <c>admin.Users.AuthorizationVersion</c>, <c>admin.ProvisioningOperations</c>, <c>merch.UserOutbox</c>
    /// (+ atomic move of the legacy registration-sentinel rows out of <c>txn.OutboxMessages</c>).
    /// Preserves <c>merch.RegistrationNotices</c> (still a live raw table, untouched). RLS teardown runs
    /// FIRST — REVOKE -> DROP POLICY -> DROP PROC/FUNCTION -> DROP USER/LOGIN/ROLE — before any EF-diffable
    /// schema change, because the CartItems.MerchantId backfill below is a real UPDATE against an
    /// RLS-covered table and the migration connection (sa, no SESSION_CONTEXT, not a pol_rls_bypass member)
    /// is not exempt from the still-active policy; tearing RLS down before that UPDATE runs is what makes it
    /// possible at all. Then: EF-diffable schema changes -> outbox move -> GRANT the new single-principal
    /// matrix. Not wrapped in one transaction (<c>DROP SECURITY POLICY</c> is not transactional on SQL
    /// Server, same lesson as <c>SecurityObjects</c>) — the real safety net for this migration is the
    /// pre-migration DB backup (rls-to-query-filter task 8 evidence), not this file's own atomicity.
    /// </summary>
    public partial class RlsTeardownAndOnePrincipal : Migration
    {
        // The legacy registration-sentinel value a still-pending (no real merchant yet) applicant's outbox
        // row carries — Merchants.Infrastructure/Persistence/Users/UserRepositories.cs's
        // RegistrationOutbox.SentinelMerchantId. Moved from txn.OutboxMessages to merch.UserOutbox (its real
        // home, task 5) and forbidden on txn.OutboxMessages going forward. NOTE (task 8 sequencing): the
        // CHECK below only holds once RegistrationOutboxWriter (still writing the sentinel into
        // txn.OutboxMessages via PolDbContext today) is rewired onto MerchantUserOutbox/merch.UserOutbox —
        // that rewire is task 8.5's job (SubmitRegistrationHandler's outbox-enqueue composition, already
        // named in that sub-step's scope). Until 8.5 lands, submitting a new registration against a
        // migrated DB fails on this constraint — expected, closed before task 8's own 8.8 verification pass.
        private const string SentinelMerchantId = "f0f0f0f0-0000-4000-8000-00000000ad17";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // === 1. RLS teardown FIRST, before any DML on RLS-covered tables. The migration connection
            // (sa, via POL_DESIGN_SQL — no SESSION_CONTEXT stamped, not a pol_rls_bypass member) is NOT
            // exempt from the still-active security policy (SecurityObjects.cs's own header comment: dbo/
            // ownership chaining does not bypass RLS on this SQL Server, only role membership does). Section
            // 3 below runs a real UPDATE on shop.CartItems (the MerchantId backfill) — if the old BLOCK-
            // after-update predicate were still active when that runs, it would reject every row (no
            // SESSION_CONTEXT => every branch of fn_merchant_predicate evaluates false), and the migration
            // would fail outright. Tearing down RLS first removes that hazard entirely. REVOKE the old
            // 5-principal grant matrix (mirrors SecurityObjects.Down() verbatim), then drop the policy
            // (depends on the predicate functions), then the procs, then the functions.
            // merch.RegistrationNotices (raw table, ExcludeFromMigrations) is untouched — still live. ===
            migrationBuilder.Sql("""
                REVOKE SELECT, INSERT, UPDATE, DELETE ON shop.Products         FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON shop.Carts            FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON shop.CartItems        FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON shop.CheckoutSessions FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON shop.Orders           FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE           ON txn.PaymentSessions  FROM pol_app;
                REVOKE SELECT                           ON txn.PspConnections   FROM pol_app;
                REVOKE SELECT, INSERT                   ON txn.IdempotencyRecords FROM pol_app;
                REVOKE INSERT                           ON txn.OutboxMessages   FROM pol_app;
                REVOKE SELECT                           ON merch.Merchants      FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE           ON merch.VaultSecrets   FROM pol_app;
                REVOKE INSERT                           ON merch.VaultRevealAudits FROM pol_app;
                REVOKE EXECUTE ON sec.usp_resolve_webhook_merchant FROM pol_app;
                REVOKE EXECUTE ON sec.usp_resolve_order_summary    FROM pol_app;
                REVOKE EXECUTE ON sec.usp_vault_audit_head         FROM pol_app;

                REVOKE SELECT ON txn.PspConnections FROM pol_resolver;
                REVOKE SELECT ON shop.Orders        FROM pol_resolver;

                REVOKE SELECT ON merch.VaultRevealAudits FROM pol_vault_auditor;

                REVOKE SELECT, UPDATE         ON txn.OutboxMessages     FROM pol_worker;
                REVOKE SELECT, UPDATE         ON txn.PaymentSessions    FROM pol_worker;
                REVOKE SELECT, INSERT, UPDATE ON shop.Orders            FROM pol_worker;
                REVOKE SELECT, UPDATE         ON shop.CheckoutSessions  FROM pol_worker;
                REVOKE SELECT, INSERT         ON merch.RegistrationNotices FROM pol_worker;

                REVOKE SELECT ON shop.Products         FROM pol_admin;
                REVOKE SELECT ON shop.Carts            FROM pol_admin;
                REVOKE SELECT ON shop.CartItems        FROM pol_admin;
                REVOKE SELECT ON shop.CheckoutSessions FROM pol_admin;
                REVOKE SELECT ON shop.Orders           FROM pol_admin;
                REVOKE SELECT ON txn.PaymentSessions   FROM pol_admin;
                REVOKE SELECT, INSERT ON txn.PspConnections FROM pol_admin;
                REVOKE SELECT ON txn.IdempotencyRecords FROM pol_admin;
                REVOKE INSERT ON txn.OutboxMessages FROM pol_admin;

                REVOKE SELECT, INSERT, UPDATE         ON admin.Users           FROM pol_admin;
                REVOKE SELECT, INSERT                 ON admin.UserAudits      FROM pol_admin;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON admin.MerchantAccess  FROM pol_admin;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON admin.Sessions    FROM pol_admin;
                REVOKE SELECT, INSERT                 ON admin.AuthAudits      FROM pol_admin;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON admin.RoleAssignments    FROM pol_admin;

                REVOKE SELECT, INSERT, UPDATE         ON cfg.Positions                 FROM pol_admin;
                REVOKE SELECT, INSERT, UPDATE         ON cfg.Offices                   FROM pol_admin;
                REVOKE SELECT, INSERT, UPDATE         ON cfg.Levels                    FROM pol_admin;
                REVOKE SELECT, INSERT, UPDATE         ON cfg.Divisions                 FROM pol_admin;

                REVOKE SELECT, INSERT, UPDATE         ON merch.Merchants               FROM pol_admin;
                REVOKE SELECT, INSERT, UPDATE         ON merch.Users           FROM pol_admin;
                REVOKE SELECT, INSERT                 ON merch.ExternalLogins          FROM pol_admin;
                REVOKE SELECT, INSERT                 ON merch.RegistrationAudits      FROM pol_admin;
                REVOKE INSERT                         ON merch.VaultSecrets            FROM pol_admin;
                REVOKE SELECT                         ON merch.VaultRevealAudits       FROM pol_admin;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON merch.Sessions    FROM pol_admin;
                REVOKE SELECT, INSERT                 ON merch.AuthAudits      FROM pol_admin;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON merch.RoleAssignments  FROM pol_admin;
                REVOKE SELECT, INSERT                 ON merch.ProvisioningAudits      FROM pol_admin;

                REVOKE SELECT                         ON iam.PermissionGroups FROM pol_admin;
                REVOKE SELECT                         ON iam.Permissions      FROM pol_admin;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON iam.Roles           FROM pol_admin;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON iam.RolePermissions FROM pol_admin;

                REVOKE SELECT, INSERT ON dbo.DataProtectionKeys FROM pol_admin;
                """);

            migrationBuilder.Sql("DROP SECURITY POLICY IF EXISTS sec.MerchantIsolationPolicy;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS sec.usp_vault_audit_head;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS sec.usp_resolve_order_summary;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS sec.usp_resolve_webhook_merchant;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS sec.fn_outbox_predicate;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS sec.fn_cartitem_predicate;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS sec.fn_merchant_predicate;");

            // === 2. Principal collapse (owner-confirmed sign-off, design.md "Human sign-off item —
            // RESOLVED"): pol_app is the sole surviving runtime principal (minimal rename churn across the
            // deployment files vs. minting a new name). DROP USER before DROP LOGIN; pol_resolver/
            // pol_vault_auditor lose their only role membership when dropped, so pol_rls_bypass drops clean. ===
            migrationBuilder.Sql("""
                DROP USER IF EXISTS pol_admin;
                DROP USER IF EXISTS pol_worker;
                DROP USER IF EXISTS pol_resolver;
                DROP USER IF EXISTS pol_vault_auditor;
                """);
            // DROP LOGIN has no IF EXISTS clause in T-SQL (unlike DROP USER/DROP ROLE) — guard explicitly.
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = N'pol_admin')
                    DROP LOGIN pol_admin;
                IF EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = N'pol_worker')
                    DROP LOGIN pol_worker;
                """);
            migrationBuilder.Sql("DROP ROLE IF EXISTS pol_rls_bypass;");

            // === 3. EF-diffable schema changes (CartItems.MerchantId, AuthorizationVersion, new tables) —
            // RLS is gone as of section 1, so the backfill UPDATE below is unobstructed. ===

            migrationBuilder.DropForeignKey(
                name: "FK_CartItems_Carts_CartId",
                schema: "shop",
                table: "CartItems");

            migrationBuilder.DropIndex(
                name: "IX_CartItems_CartId",
                schema: "shop",
                table: "CartItems");

            migrationBuilder.AddColumn<long>(
                name: "AuthorizationVersion",
                schema: "admin",
                table: "Users",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Added NULLABLE first (no default) so the backfill below can distinguish "not yet stamped" from
            // a real value — REQ-6.1/6.2, Codex-R1 #8. Every existing row gets its parent Cart's MerchantId,
            // then the column is tightened to NOT NULL once no row is left unstamped.
            migrationBuilder.AddColumn<Guid>(
                name: "MerchantId",
                schema: "shop",
                table: "CartItems",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE ci SET ci.MerchantId = c.MerchantId
                FROM shop.CartItems ci JOIN shop.Carts c ON ci.CartId = c.Id
                WHERE ci.MerchantId IS NULL;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "MerchantId",
                schema: "shop",
                table: "CartItems",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Carts_Id_MerchantId",
                schema: "shop",
                table: "Carts",
                columns: new[] { "Id", "MerchantId" });

            migrationBuilder.CreateTable(
                name: "ProvisioningOperations",
                schema: "admin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CallerAdminId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpectedAuthorizationVersion = table.Column<long>(type: "bigint", nullable: false),
                    RequestHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Result = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProvisioningOperations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserOutbox",
                schema: "merch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    LeaseExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LeaseOwner = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserOutbox", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CartItems_CartId_MerchantId",
                schema: "shop",
                table: "CartItems",
                columns: new[] { "CartId", "MerchantId" });

            migrationBuilder.CreateIndex(
                name: "UX_ProvisioningOperations_Key",
                schema: "admin",
                table: "ProvisioningOperations",
                column: "OperationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserOutbox_ProcessedAt_LeaseExpiresAt",
                schema: "merch",
                table: "UserOutbox",
                columns: new[] { "ProcessedAt", "LeaseExpiresAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_CartItems_Carts_CartId_MerchantId",
                schema: "shop",
                table: "CartItems",
                columns: new[] { "CartId", "MerchantId" },
                principalSchema: "shop",
                principalTable: "Carts",
                principalColumns: new[] { "Id", "MerchantId" },
                onDelete: ReferentialAction.Cascade);

            // === 4. Outbox atomic move (design.md "Migration (R5 #7)"): the legacy registration-sentinel
            // rows in txn.OutboxMessages move to their real home, merch.UserOutbox, preserving Id and
            // processing/lease/attempt state. Assumes deployment quiescence (no in-flight dispatcher during
            // the migration window), matching the design's own stated assumption. Sentinel forbidden on
            // txn.OutboxMessages afterward. ===
            migrationBuilder.Sql($"""
                INSERT INTO merch.UserOutbox (Id, MerchantId, Type, Payload, OccurredAt, ProcessedAt, Attempts, Error, LeaseExpiresAt, LeaseOwner)
                SELECT Id, MerchantId, Type, Payload, OccurredAt, ProcessedAt, Attempts, Error, LeaseExpiresAt, LeaseOwner
                FROM txn.OutboxMessages
                WHERE MerchantId = '{SentinelMerchantId}';

                DELETE FROM txn.OutboxMessages WHERE MerchantId = '{SentinelMerchantId}';

                ALTER TABLE txn.OutboxMessages ADD CONSTRAINT CK_OutboxMessages_NoSentinel CHECK (MerchantId <> '{SentinelMerchantId}');
                """);

            // === 5. GRANT the new single-principal matrix to pol_app: the union of what pol_app/pol_admin/
            // pol_worker/pol_resolver/pol_vault_auditor each held, PLUS SELECT on merch.VaultRevealAudits
            // (task 6's deferred grant — pol_app can now read the audit chain head directly, no more
            // usp_vault_audit_head bypass proc) PLUS the new task 5/7 tables (merch.UserOutbox,
            // admin.ProvisioningOperations). The 3 resolver/vault-head EXECUTE grants are gone with their
            // procs (WebhookMerchantResolver/OrderSummaryReader read the tables directly now — no more RLS
            // to bypass). Accepted blast-radius widening (app compromise => DB-level read of vault plaintext
            // + audit) is the explicit, signed-off tradeoff of the 1-principal collapse, not an oversight. ===
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE, DELETE ON shop.Products         TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON shop.Carts            TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON shop.CartItems        TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON shop.CheckoutSessions TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON shop.Orders           TO pol_app;
                GRANT SELECT, INSERT, UPDATE          ON txn.PaymentSessions   TO pol_app;
                GRANT SELECT, INSERT                  ON txn.PspConnections    TO pol_app;
                GRANT SELECT, INSERT                  ON txn.IdempotencyRecords TO pol_app;
                GRANT SELECT, INSERT, UPDATE          ON txn.OutboxMessages    TO pol_app;

                GRANT SELECT, INSERT, UPDATE          ON merch.Merchants           TO pol_app;
                GRANT SELECT, INSERT, UPDATE          ON merch.VaultSecrets        TO pol_app;
                GRANT SELECT, INSERT                  ON merch.VaultRevealAudits   TO pol_app;
                GRANT SELECT, INSERT                  ON merch.RegistrationNotices TO pol_app;
                GRANT SELECT, INSERT, UPDATE          ON merch.UserOutbox          TO pol_app;
                GRANT SELECT, INSERT, UPDATE          ON merch.Users               TO pol_app;
                GRANT SELECT, INSERT                  ON merch.ExternalLogins      TO pol_app;
                GRANT SELECT, INSERT                  ON merch.RegistrationAudits  TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE  ON merch.Sessions            TO pol_app;
                GRANT SELECT, INSERT                  ON merch.AuthAudits          TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE  ON merch.RoleAssignments     TO pol_app;
                GRANT SELECT, INSERT                  ON merch.ProvisioningAudits  TO pol_app;

                GRANT SELECT, INSERT, UPDATE          ON admin.Users               TO pol_app;
                GRANT SELECT, INSERT                  ON admin.UserAudits          TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE  ON admin.MerchantAccess      TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE  ON admin.Sessions            TO pol_app;
                GRANT SELECT, INSERT                  ON admin.AuthAudits          TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE  ON admin.RoleAssignments     TO pol_app;
                GRANT SELECT, INSERT, UPDATE          ON admin.ProvisioningOperations TO pol_app;

                GRANT SELECT, INSERT, UPDATE ON cfg.Positions TO pol_app;
                GRANT SELECT, INSERT, UPDATE ON cfg.Offices   TO pol_app;
                GRANT SELECT, INSERT, UPDATE ON cfg.Levels    TO pol_app;
                GRANT SELECT, INSERT, UPDATE ON cfg.Divisions TO pol_app;

                GRANT SELECT                         ON iam.PermissionGroups TO pol_app;
                GRANT SELECT                         ON iam.Permissions      TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON iam.Roles           TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON iam.RolePermissions TO pol_app;

                GRANT SELECT, INSERT ON dbo.DataProtectionKeys TO pol_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Defense-in-depth only (design.md "Migration (R5 #7)") — the real safety net for this
            // migration is the pre-migration DB backup. Recreated LOGINs get a non-functional placeholder
            // password (Down() has no access to the original secrets) — rotate immediately after a real
            // rollback, or prefer restoring from the backup over relying on this Down().

            // === 1. Revoke the single-principal matrix ===
            migrationBuilder.Sql("""
                REVOKE SELECT, INSERT, UPDATE, DELETE ON shop.Products         FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON shop.Carts            FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON shop.CartItems        FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON shop.CheckoutSessions FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON shop.Orders           FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE          ON txn.PaymentSessions   FROM pol_app;
                REVOKE SELECT, INSERT                  ON txn.PspConnections    FROM pol_app;
                REVOKE SELECT, INSERT                  ON txn.IdempotencyRecords FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE          ON txn.OutboxMessages    FROM pol_app;

                REVOKE SELECT, INSERT, UPDATE          ON merch.Merchants           FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE          ON merch.VaultSecrets        FROM pol_app;
                REVOKE SELECT, INSERT                  ON merch.VaultRevealAudits   FROM pol_app;
                REVOKE SELECT, INSERT                  ON merch.RegistrationNotices FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE          ON merch.UserOutbox          FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE          ON merch.Users               FROM pol_app;
                REVOKE SELECT, INSERT                  ON merch.ExternalLogins      FROM pol_app;
                REVOKE SELECT, INSERT                  ON merch.RegistrationAudits  FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE  ON merch.Sessions            FROM pol_app;
                REVOKE SELECT, INSERT                  ON merch.AuthAudits          FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE  ON merch.RoleAssignments     FROM pol_app;
                REVOKE SELECT, INSERT                  ON merch.ProvisioningAudits  FROM pol_app;

                REVOKE SELECT, INSERT, UPDATE          ON admin.Users               FROM pol_app;
                REVOKE SELECT, INSERT                  ON admin.UserAudits          FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE  ON admin.MerchantAccess      FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE  ON admin.Sessions            FROM pol_app;
                REVOKE SELECT, INSERT                  ON admin.AuthAudits          FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE  ON admin.RoleAssignments     FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE          ON admin.ProvisioningOperations FROM pol_app;

                REVOKE SELECT, INSERT, UPDATE ON cfg.Positions FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE ON cfg.Offices   FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE ON cfg.Levels    FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE ON cfg.Divisions FROM pol_app;

                REVOKE SELECT                         ON iam.PermissionGroups FROM pol_app;
                REVOKE SELECT                         ON iam.Permissions      FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON iam.Roles           FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON iam.RolePermissions FROM pol_app;

                REVOKE SELECT, INSERT ON dbo.DataProtectionKeys FROM pol_app;
                """);

            // === 2. Recreate the old principals (placeholder passwords — see class-level remark) ===
            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = N'pol_admin')
                    CREATE LOGIN pol_admin WITH PASSWORD = N'CHANGE_ME_IMMEDIATELY_AFTER_ROLLBACK!2026', CHECK_POLICY = ON;
                IF NOT EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = N'pol_worker')
                    CREATE LOGIN pol_worker WITH PASSWORD = N'CHANGE_ME_IMMEDIATELY_AFTER_ROLLBACK!2026', CHECK_POLICY = ON;
                """);
            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'pol_admin')
                    CREATE USER pol_admin FOR LOGIN pol_admin;
                IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'pol_worker')
                    CREATE USER pol_worker FOR LOGIN pol_worker;
                IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'pol_resolver')
                    CREATE USER pol_resolver WITHOUT LOGIN;
                IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'pol_vault_auditor')
                    CREATE USER pol_vault_auditor WITHOUT LOGIN;

                IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'pol_rls_bypass' AND type = 'R')
                    CREATE ROLE pol_rls_bypass;
                IF IS_ROLEMEMBER(N'pol_rls_bypass', N'pol_resolver') = 0
                    ALTER ROLE pol_rls_bypass ADD MEMBER pol_resolver;
                IF IS_ROLEMEMBER(N'pol_rls_bypass', N'pol_vault_auditor') = 0
                    ALTER ROLE pol_rls_bypass ADD MEMBER pol_vault_auditor;
                """);

            // === 3. Recreate functions/procs/policy verbatim from SecurityObjects.Up() ===
            migrationBuilder.Sql("""
                CREATE FUNCTION sec.fn_merchant_predicate(@MerchantId uniqueidentifier)
                RETURNS TABLE WITH SCHEMABINDING AS RETURN
                SELECT 1 AS allowed
                WHERE IS_ROLEMEMBER(N'pol_rls_bypass') = 1
                   OR @MerchantId = CAST(SESSION_CONTEXT(N'MerchantId') AS uniqueidentifier)
                   OR (CAST(SESSION_CONTEXT(N'MerchantId') AS uniqueidentifier) = CONVERT(uniqueidentifier, '00000000-0000-0000-0000-000000000000')
                       AND SESSION_CONTEXT(N'UserId') IS NOT NULL
                       AND (EXISTS (SELECT 1 FROM admin.Users u
                                    WHERE u.Id = CAST(SESSION_CONTEXT(N'UserId') AS uniqueidentifier)
                                      AND u.Tier = 1 /* Super */)
                            OR EXISTS (SELECT 1 FROM admin.MerchantAccess a
                                       WHERE a.PlatformUserId = CAST(SESSION_CONTEXT(N'UserId') AS uniqueidentifier)
                                         AND a.MerchantId = @MerchantId)));
                """);
            migrationBuilder.Sql("""
                CREATE FUNCTION sec.fn_cartitem_predicate(@CartId uniqueidentifier)
                RETURNS TABLE WITH SCHEMABINDING AS RETURN
                SELECT 1 AS allowed
                FROM shop.Carts c
                CROSS APPLY sec.fn_merchant_predicate(c.MerchantId) p
                WHERE c.Id = @CartId;
                """);
            migrationBuilder.Sql("""
                CREATE FUNCTION sec.fn_outbox_predicate(@MerchantId uniqueidentifier)
                RETURNS TABLE WITH SCHEMABINDING AS RETURN
                SELECT 1 AS allowed
                WHERE @MerchantId = CONVERT(uniqueidentifier, 'f0f0f0f0-0000-4000-8000-00000000ad17')
                   OR EXISTS (SELECT 1 FROM sec.fn_merchant_predicate(@MerchantId));
                """);
            migrationBuilder.Sql("""
                CREATE PROCEDURE sec.usp_resolve_webhook_merchant @PspConnectionId uniqueidentifier
                WITH EXECUTE AS 'pol_resolver' AS
                BEGIN
                    SET NOCOUNT ON;
                    SELECT TOP 1 MerchantId AS [Value] FROM txn.PspConnections WHERE Id = @PspConnectionId;
                END
                """);
            migrationBuilder.Sql("""
                CREATE PROCEDURE sec.usp_resolve_order_summary @Token nvarchar(64)
                WITH EXECUTE AS 'pol_resolver' AS
                BEGIN
                    SET NOCOUNT ON;
                    SELECT TOP 1 Id, MerchantId, AmountAmount, AmountCurrency, Status, PaymentSessionId, SummaryTokenExpiresAt
                    FROM shop.Orders WHERE SummaryToken = @Token;
                END
                """);
            migrationBuilder.Sql("""
                CREATE PROCEDURE sec.usp_vault_audit_head @MerchantId uniqueidentifier
                WITH EXECUTE AS 'pol_vault_auditor' AS
                BEGIN
                    SET NOCOUNT ON;
                    DECLARE @res nvarchar(80) = CONCAT(N'vault-audit:', CONVERT(nvarchar(36), @MerchantId));
                    DECLARE @lock int;
                    EXEC @lock = sp_getapplock
                        @Resource = @res, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 15000;
                    IF @lock < 0
                        THROW 50000, N'Could not acquire the vault audit chain lock for the merchant.', 1;
                    SELECT TOP 1 Seq AS LastSeq, Hash AS LastHash
                    FROM merch.VaultRevealAudits WHERE MerchantId = @MerchantId ORDER BY Seq DESC;
                END
                """);

            var clauses = new List<string>
            {
                "ADD FILTER PREDICATE sec.fn_merchant_predicate(MerchantId) ON shop.Products",
                "ADD BLOCK PREDICATE sec.fn_merchant_predicate(MerchantId) ON shop.Products AFTER INSERT",
                "ADD BLOCK PREDICATE sec.fn_merchant_predicate(MerchantId) ON shop.Products AFTER UPDATE",
                "ADD FILTER PREDICATE sec.fn_merchant_predicate(MerchantId) ON shop.Carts",
                "ADD BLOCK PREDICATE sec.fn_merchant_predicate(MerchantId) ON shop.Carts AFTER INSERT",
                "ADD BLOCK PREDICATE sec.fn_merchant_predicate(MerchantId) ON shop.Carts AFTER UPDATE",
                "ADD FILTER PREDICATE sec.fn_merchant_predicate(MerchantId) ON shop.CheckoutSessions",
                "ADD BLOCK PREDICATE sec.fn_merchant_predicate(MerchantId) ON shop.CheckoutSessions AFTER INSERT",
                "ADD BLOCK PREDICATE sec.fn_merchant_predicate(MerchantId) ON shop.CheckoutSessions AFTER UPDATE",
                "ADD FILTER PREDICATE sec.fn_merchant_predicate(MerchantId) ON shop.Orders",
                "ADD BLOCK PREDICATE sec.fn_merchant_predicate(MerchantId) ON shop.Orders AFTER INSERT",
                "ADD BLOCK PREDICATE sec.fn_merchant_predicate(MerchantId) ON shop.Orders AFTER UPDATE",
                "ADD FILTER PREDICATE sec.fn_merchant_predicate(MerchantId) ON txn.PaymentSessions",
                "ADD BLOCK PREDICATE sec.fn_merchant_predicate(MerchantId) ON txn.PaymentSessions AFTER INSERT",
                "ADD BLOCK PREDICATE sec.fn_merchant_predicate(MerchantId) ON txn.PaymentSessions AFTER UPDATE",
                "ADD FILTER PREDICATE sec.fn_merchant_predicate(MerchantId) ON txn.PspConnections",
                "ADD BLOCK PREDICATE sec.fn_merchant_predicate(MerchantId) ON txn.PspConnections AFTER INSERT",
                "ADD BLOCK PREDICATE sec.fn_merchant_predicate(MerchantId) ON txn.PspConnections AFTER UPDATE",
                "ADD FILTER PREDICATE sec.fn_merchant_predicate(MerchantId) ON txn.IdempotencyRecords",
                "ADD BLOCK PREDICATE sec.fn_merchant_predicate(MerchantId) ON txn.IdempotencyRecords AFTER INSERT",
                "ADD BLOCK PREDICATE sec.fn_merchant_predicate(MerchantId) ON txn.IdempotencyRecords AFTER UPDATE",
                "ADD FILTER PREDICATE sec.fn_merchant_predicate(MerchantId) ON merch.VaultSecrets",
                "ADD BLOCK PREDICATE sec.fn_merchant_predicate(MerchantId) ON merch.VaultSecrets AFTER INSERT",
                "ADD BLOCK PREDICATE sec.fn_merchant_predicate(MerchantId) ON merch.VaultSecrets AFTER UPDATE",
                "ADD FILTER PREDICATE sec.fn_merchant_predicate(Id) ON merch.Merchants",
                "ADD BLOCK PREDICATE sec.fn_merchant_predicate(Id) ON merch.Merchants AFTER INSERT",
                "ADD BLOCK PREDICATE sec.fn_merchant_predicate(Id) ON merch.Merchants AFTER UPDATE",
                "ADD FILTER PREDICATE sec.fn_cartitem_predicate(CartId) ON shop.CartItems",
                "ADD BLOCK PREDICATE sec.fn_cartitem_predicate(CartId) ON shop.CartItems AFTER INSERT",
                "ADD BLOCK PREDICATE sec.fn_cartitem_predicate(CartId) ON shop.CartItems AFTER UPDATE",
                "ADD BLOCK PREDICATE sec.fn_outbox_predicate(MerchantId) ON txn.OutboxMessages AFTER INSERT",
                "ADD BLOCK PREDICATE sec.fn_merchant_predicate(MerchantId) ON merch.VaultRevealAudits AFTER INSERT",
            };
            migrationBuilder.Sql(
                "CREATE SECURITY POLICY sec.MerchantIsolationPolicy\n" +
                string.Join(",\n", clauses) +
                "\nWITH (STATE = ON, SCHEMABINDING = ON);");

            // === 4. Re-run the old grant matrix verbatim from SecurityObjects.Up() ===
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE, DELETE ON shop.Products         TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON shop.Carts            TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON shop.CartItems        TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON shop.CheckoutSessions TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON shop.Orders           TO pol_app;
                GRANT SELECT, INSERT, UPDATE           ON txn.PaymentSessions  TO pol_app;
                GRANT SELECT                           ON txn.PspConnections   TO pol_app;
                GRANT SELECT, INSERT                   ON txn.IdempotencyRecords TO pol_app;
                GRANT INSERT                           ON txn.OutboxMessages   TO pol_app;
                GRANT SELECT                           ON merch.Merchants      TO pol_app;
                GRANT SELECT, INSERT, UPDATE           ON merch.VaultSecrets   TO pol_app;
                GRANT INSERT                           ON merch.VaultRevealAudits TO pol_app;
                GRANT EXECUTE ON sec.usp_resolve_webhook_merchant TO pol_app;
                GRANT EXECUTE ON sec.usp_resolve_order_summary    TO pol_app;
                GRANT EXECUTE ON sec.usp_vault_audit_head         TO pol_app;

                GRANT SELECT ON txn.PspConnections TO pol_resolver;
                GRANT SELECT ON shop.Orders        TO pol_resolver;

                GRANT SELECT ON merch.VaultRevealAudits TO pol_vault_auditor;

                GRANT SELECT, UPDATE         ON txn.OutboxMessages     TO pol_worker;
                GRANT SELECT, UPDATE         ON txn.PaymentSessions    TO pol_worker;
                GRANT SELECT, INSERT, UPDATE ON shop.Orders            TO pol_worker;
                GRANT SELECT, UPDATE         ON shop.CheckoutSessions  TO pol_worker;
                GRANT SELECT, INSERT         ON merch.RegistrationNotices TO pol_worker;

                GRANT SELECT ON shop.Products         TO pol_admin;
                GRANT SELECT ON shop.Carts            TO pol_admin;
                GRANT SELECT ON shop.CartItems        TO pol_admin;
                GRANT SELECT ON shop.CheckoutSessions TO pol_admin;
                GRANT SELECT ON shop.Orders           TO pol_admin;
                GRANT SELECT ON txn.PaymentSessions   TO pol_admin;
                GRANT SELECT, INSERT ON txn.PspConnections   TO pol_admin;
                GRANT SELECT ON txn.IdempotencyRecords TO pol_admin;
                GRANT INSERT ON txn.OutboxMessages TO pol_admin;

                GRANT SELECT, INSERT, UPDATE         ON admin.Users           TO pol_admin;
                GRANT SELECT, INSERT                 ON admin.UserAudits      TO pol_admin;
                GRANT SELECT, INSERT, UPDATE, DELETE ON admin.MerchantAccess  TO pol_admin;
                GRANT SELECT, INSERT, UPDATE, DELETE ON admin.Sessions    TO pol_admin;
                GRANT SELECT, INSERT                 ON admin.AuthAudits      TO pol_admin;
                GRANT SELECT, INSERT, UPDATE, DELETE ON admin.RoleAssignments    TO pol_admin;

                GRANT SELECT, INSERT, UPDATE         ON cfg.Positions                 TO pol_admin;
                GRANT SELECT, INSERT, UPDATE         ON cfg.Offices                   TO pol_admin;
                GRANT SELECT, INSERT, UPDATE         ON cfg.Levels                    TO pol_admin;
                GRANT SELECT, INSERT, UPDATE         ON cfg.Divisions                 TO pol_admin;

                GRANT SELECT, INSERT, UPDATE         ON merch.Merchants               TO pol_admin;
                GRANT SELECT, INSERT, UPDATE         ON merch.Users           TO pol_admin;
                GRANT SELECT, INSERT                 ON merch.ExternalLogins          TO pol_admin;
                GRANT SELECT, INSERT                 ON merch.RegistrationAudits      TO pol_admin;
                GRANT INSERT                         ON merch.VaultSecrets            TO pol_admin;
                GRANT SELECT                         ON merch.VaultRevealAudits       TO pol_admin;
                GRANT SELECT, INSERT, UPDATE, DELETE ON merch.Sessions    TO pol_admin;
                GRANT SELECT, INSERT                 ON merch.AuthAudits      TO pol_admin;
                GRANT SELECT, INSERT, UPDATE, DELETE ON merch.RoleAssignments  TO pol_admin;
                GRANT SELECT, INSERT                 ON merch.ProvisioningAudits      TO pol_admin;

                GRANT SELECT                         ON iam.PermissionGroups TO pol_admin;
                GRANT SELECT                         ON iam.Permissions      TO pol_admin;
                GRANT SELECT, INSERT, UPDATE, DELETE ON iam.Roles           TO pol_admin;
                GRANT SELECT, INSERT, UPDATE, DELETE ON iam.RolePermissions TO pol_admin;

                GRANT SELECT, INSERT ON dbo.DataProtectionKeys TO pol_admin;
                """);

            // === 5. Reverse the outbox move (sentinel rows only — any real merch.UserOutbox row created
            // after the cutover is discarded when the table is dropped below; not byte-identical, a
            // structural rollback only) ===
            migrationBuilder.Sql($"""
                ALTER TABLE txn.OutboxMessages DROP CONSTRAINT IF EXISTS CK_OutboxMessages_NoSentinel;

                INSERT INTO txn.OutboxMessages (Id, MerchantId, Type, Payload, OccurredAt, ProcessedAt, Attempts, Error, LeaseExpiresAt, LeaseOwner)
                SELECT Id, MerchantId, Type, Payload, OccurredAt, ProcessedAt, Attempts, Error, LeaseExpiresAt, LeaseOwner
                FROM merch.UserOutbox
                WHERE MerchantId = '{SentinelMerchantId}';
                """);

            // === 6. Drop the new columns/tables (EF-diffable side, reverse of Up()'s section 3) ===
            migrationBuilder.DropForeignKey(
                name: "FK_CartItems_Carts_CartId_MerchantId",
                schema: "shop",
                table: "CartItems");

            migrationBuilder.DropTable(
                name: "ProvisioningOperations",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "UserOutbox",
                schema: "merch");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Carts_Id_MerchantId",
                schema: "shop",
                table: "Carts");

            migrationBuilder.DropIndex(
                name: "IX_CartItems_CartId_MerchantId",
                schema: "shop",
                table: "CartItems");

            migrationBuilder.DropColumn(
                name: "AuthorizationVersion",
                schema: "admin",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "MerchantId",
                schema: "shop",
                table: "CartItems");

            migrationBuilder.CreateIndex(
                name: "IX_CartItems_CartId",
                schema: "shop",
                table: "CartItems",
                column: "CartId");

            migrationBuilder.AddForeignKey(
                name: "FK_CartItems_Carts_CartId",
                schema: "shop",
                table: "CartItems",
                column: "CartId",
                principalSchema: "shop",
                principalTable: "Carts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
