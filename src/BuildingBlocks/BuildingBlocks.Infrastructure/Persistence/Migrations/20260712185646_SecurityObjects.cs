using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The multi-merchant isolation floor (rf1 design.md "RLS" section), hand-written because EF's model does not
    /// know about SQL-native functions/procs/policies/grants. Must run AFTER <c>InitialSchema</c> (SCHEMABINDING
    /// requires the tables to exist first) and its statements are NOT wrapped in one transaction (<c>ALTER
    /// SECURITY POLICY</c> is not transactional on SQL Server — a prior-session lesson). Order: schemas -> functions
    /// -> procs -> RegistrationNotices (raw table, excluded from the EF model diff) -> policy -> grants.
    /// rf2: the RBAC catalog grants moved off the duplicated admin.*/merch.* catalog tables (dropped in this
    /// reset's InitialSchema) onto the single central iam.* catalog — pol_admin gets read-only on the vocabulary
    /// (PermissionGroups/Permissions) and full CRUD on Roles/RolePermissions; pol_app gets NOTHING on iam.* (it
    /// never resolves permissions). The per-side assignment tables (admin/merch.RoleAssignments) keep their grants.
    /// </summary>
    public partial class SecurityObjects : Migration
    {
        // (schema, table): FILTER + BLOCK(insert) + BLOCK(update) via fn_merchant_predicate(MerchantId).
        private static readonly (string Schema, string Table)[] MerchantTables =
        {
            ("shop", "Products"), ("shop", "Carts"), ("shop", "CheckoutSessions"), ("shop", "Orders"),
            ("txn", "PaymentSessions"), ("txn", "PspConnections"), ("txn", "IdempotencyRecords"),
            ("merch", "VaultSecrets"),
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // --- Legacy principals (rls-to-query-filter task 8 catch-up) ---
            // This migration's procs/grants below name the pre-teardown principals, which the OLD
            // docker/bootstrap/01-principals.sql used to create before any migration ran. The 1-principal
            // bootstrap no longer does, so a FRESH database replaying the chain (CI, local `down -v`) hit
            // "Cannot execute as the user 'pol_resolver'". Create them here, login-less and idempotent, so the
            // chain is self-contained; 20260719081817_RlsTeardownAndOnePrincipal drops every one of them.
            // Already-migrated databases never re-run this migration, so this block changes nothing for them.
            migrationBuilder.Sql("""
                IF DATABASE_PRINCIPAL_ID(N'pol_admin') IS NULL         CREATE USER pol_admin WITHOUT LOGIN;
                IF DATABASE_PRINCIPAL_ID(N'pol_worker') IS NULL        CREATE USER pol_worker WITHOUT LOGIN;
                IF DATABASE_PRINCIPAL_ID(N'pol_resolver') IS NULL      CREATE USER pol_resolver WITHOUT LOGIN;
                IF DATABASE_PRINCIPAL_ID(N'pol_vault_auditor') IS NULL CREATE USER pol_vault_auditor WITHOUT LOGIN;
                IF DATABASE_PRINCIPAL_ID(N'pol_rls_bypass') IS NULL    CREATE ROLE pol_rls_bypass;
                """);

            // --- Schemas (REQ-3.10: every schema owned by dbo, so ownership chaining lets a predicate reach
            // admin.Users / admin.MerchantAccess across schemas without an explicit grant). shop/
            // txn/admin/merch/cfg already exist (EnsureSchema'd by InitialSchema); re-assert authorization in case
            // the running principal was not dbo. sec has no EF entity, so it needs its own CREATE. iam is not
            // touched by any predicate (no RLS on iam.*), so it needs no ownership-chaining re-assert here.
            migrationBuilder.Sql("""
                IF SCHEMA_ID(N'sec') IS NULL EXEC(N'CREATE SCHEMA sec AUTHORIZATION dbo;');
                ALTER AUTHORIZATION ON SCHEMA::shop  TO dbo;
                ALTER AUTHORIZATION ON SCHEMA::txn   TO dbo;
                ALTER AUTHORIZATION ON SCHEMA::admin TO dbo;
                ALTER AUTHORIZATION ON SCHEMA::merch TO dbo;
                ALTER AUTHORIZATION ON SCHEMA::cfg   TO dbo;
                ALTER AUTHORIZATION ON SCHEMA::sec   TO dbo;
                """);

            // --- Functions ---
            // The general-purpose predicate (design.md "RLS" section, verbatim): bypass role, own-merchant, or a
            // bound platform user acting as Super (unrestricted) / Scoped (only its assigned merchants via
            // admin.MerchantAccess). T4: the empty-MerchantId branch requires UserId IS NOT NULL — an empty
            // sentinel with no bound user is never treated as "platform branch" (deny, not fail-open).
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

            // CartItems carries no MerchantId of its own (parent-scoped through shop.Carts). Delegates to
            // fn_merchant_predicate on the parent's MerchantId (rather than duplicating the bypass/Super/Scoped
            // logic a second time) so a Super/Scoped platform user reading CartItems is governed by the exact same
            // rule as everywhere else — T5 removed pol_admin from pol_rls_bypass, so (unlike the pre-rf1 predicate)
            // this can no longer lean on a blanket bypass for the admin cross-merchant case.
            migrationBuilder.Sql("""
                CREATE FUNCTION sec.fn_cartitem_predicate(@CartId uniqueidentifier)
                RETURNS TABLE WITH SCHEMABINDING AS RETURN
                SELECT 1 AS allowed
                FROM shop.Carts c
                CROSS APPLY sec.fn_merchant_predicate(c.MerchantId) p
                WHERE c.Id = @CartId;
                """);

            // BLOCK predicate for txn.OutboxMessages ONLY (insert-only; the dispatcher drains cross-merchant, so
            // there is no FILTER). Extends fn_merchant_predicate with ONE well-known carve-out (deviation from a
            // literal port — see task evidence): merchant-less registration writes its outbox row via the keyed
            // pol_admin connection while AdminActorContext is deliberately unbound (unstamped SESSION_CONTEXT —
            // anonymous /merchant-users/register, REQ-20.2), stamped with the fixed sentinel id
            // Merchants.Infrastructure/Persistence/MerchantUserRepositories.cs's MerchantsOutbox.SentinelMerchantId.
            // Under the pre-rf1 design pol_admin's blanket RLS bypass made this write pass unconditionally; T5
            // removes that bypass, so without this carve-out the insert would be BLOCKed (unstamped context can
            // satisfy no other branch of fn_merchant_predicate) and anonymous registration would fail at the DB.
            // Every other write (the funnel's own pol_app inserts, a real bound merchant) still goes through the
            // normal fn_merchant_predicate branches unchanged.
            migrationBuilder.Sql("""
                CREATE FUNCTION sec.fn_outbox_predicate(@MerchantId uniqueidentifier)
                RETURNS TABLE WITH SCHEMABINDING AS RETURN
                SELECT 1 AS allowed
                WHERE @MerchantId = CONVERT(uniqueidentifier, 'f0f0f0f0-0000-4000-8000-00000000ad17')
                   OR EXISTS (SELECT 1 FROM sec.fn_merchant_predicate(@MerchantId));
                """);

            // --- Procs (WITH EXECUTE AS a login-less bypass principal; port of the pre-rf1 procs, renamed/re-homed
            // per the rename table, logic unchanged) ---
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

            // --- merch.RegistrationNotices (raw: RegistrationNoticeConfiguration.ExcludeFromMigrations, so EF maps
            // it for runtime reads/writes but never diffs/creates it) ---
            migrationBuilder.Sql("""
                CREATE TABLE merch.RegistrationNotices (
                    Id             uniqueidentifier NOT NULL CONSTRAINT PK_RegistrationNotices PRIMARY KEY,
                    MerchantUserId uniqueidentifier NOT NULL,
                    Subject        nvarchar(256)    NOT NULL,
                    Email          nvarchar(320)    NOT NULL,
                    DisplayName    nvarchar(200)    NOT NULL,
                    HostedDomain   nvarchar(256)    NULL,
                    OccurredAt     datetime2        NOT NULL,
                    CreatedAt      datetime2        NOT NULL
                );
                CREATE UNIQUE INDEX IX_RegistrationNotices_MerchantUserId
                    ON merch.RegistrationNotices (MerchantUserId);
                """);

            // --- Policy: sec.MerchantIsolationPolicy (REQ-3.6) ---
            var clauses = new List<string>();
            foreach (var (schema, table) in MerchantTables)
            {
                clauses.Add($"ADD FILTER PREDICATE sec.fn_merchant_predicate(MerchantId) ON {schema}.{table}");
                clauses.Add($"ADD BLOCK PREDICATE sec.fn_merchant_predicate(MerchantId) ON {schema}.{table} AFTER INSERT");
                clauses.Add($"ADD BLOCK PREDICATE sec.fn_merchant_predicate(MerchantId) ON {schema}.{table} AFTER UPDATE");
            }
            // merch.Merchants: self-row (predicate on Id, not a MerchantId column) — new coverage vs pre-rf1
            // (Tenants was never under the policy); a Scoped platform user can no longer INSERT a brand-new merchant
            // (its Id can never already be in admin.MerchantAccess), so provisioning is Super-only at the DB.
            clauses.Add("ADD FILTER PREDICATE sec.fn_merchant_predicate(Id) ON merch.Merchants");
            clauses.Add("ADD BLOCK PREDICATE sec.fn_merchant_predicate(Id) ON merch.Merchants AFTER INSERT");
            clauses.Add("ADD BLOCK PREDICATE sec.fn_merchant_predicate(Id) ON merch.Merchants AFTER UPDATE");
            // shop.CartItems: parent-scoped through shop.Carts via fn_cartitem_predicate.
            clauses.Add("ADD FILTER PREDICATE sec.fn_cartitem_predicate(CartId) ON shop.CartItems");
            clauses.Add("ADD BLOCK PREDICATE sec.fn_cartitem_predicate(CartId) ON shop.CartItems AFTER INSERT");
            clauses.Add("ADD BLOCK PREDICATE sec.fn_cartitem_predicate(CartId) ON shop.CartItems AFTER UPDATE");
            // txn.OutboxMessages: BLOCK-insert only (the worker dispatcher drains cross-merchant — no FILTER) via
            // fn_outbox_predicate (the sentinel-registration carve-out above).
            clauses.Add("ADD BLOCK PREDICATE sec.fn_outbox_predicate(MerchantId) ON txn.OutboxMessages AFTER INSERT");
            // merch.VaultRevealAudits: BLOCK-insert only (append-only; pol_app has no SELECT/UPDATE/DELETE grant on
            // it at all, so a FILTER would be moot) via the general predicate — no sentinel writer touches this one.
            clauses.Add("ADD BLOCK PREDICATE sec.fn_merchant_predicate(MerchantId) ON merch.VaultRevealAudits AFTER INSERT");

            migrationBuilder.Sql(
                "CREATE SECURITY POLICY sec.MerchantIsolationPolicy\n" +
                string.Join(",\n", clauses) +
                "\nWITH (STATE = ON, SCHEMABINDING = ON);");

            // --- GRANT matrix (per-table; as-built handler is ground truth per design.md) ---
            migrationBuilder.Sql("""
                -- pol_app (the funnel, RLS-filtered): own-merchant CRUD on shop.*, the payment/outbox/idempotency
                -- interim slice of txn.*, its own merchant's row on merch.Merchants, the vault store (no DELETE —
                -- IVaultSecretStore exposes none), an insert-only outbox slot, and EXECUTE on the 3 resolver procs.
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

                -- pol_resolver (login-less; the webhook/order-summary procs run as this user).
                GRANT SELECT ON txn.PspConnections TO pol_resolver;
                GRANT SELECT ON shop.Orders        TO pol_resolver;

                -- pol_vault_auditor (login-less; the head-read proc runs as this user so it can read the chain head
                -- while pol_app stays unable to SELECT the table).
                GRANT SELECT ON merch.VaultRevealAudits TO pol_vault_auditor;

                -- pol_worker (dispatcher): drain the outbox, update payment/checkout state, create Orders
                -- (CheckoutConfirmedConsumer), record registration notices (control-plane, worker-only reader today).
                GRANT SELECT, UPDATE         ON txn.OutboxMessages     TO pol_worker;
                GRANT SELECT, UPDATE         ON txn.PaymentSessions    TO pol_worker;
                GRANT SELECT, INSERT, UPDATE ON shop.Orders            TO pol_worker;
                GRANT SELECT, UPDATE         ON shop.CheckoutSessions  TO pol_worker;
                GRANT SELECT, INSERT         ON merch.RegistrationNotices TO pol_worker;

                -- pol_admin (control plane, T5: NOT a pol_rls_bypass member — every read/write below goes through
                -- the Super/Scoped branches of fn_merchant_predicate, or is a control-plane table outside the
                -- policy entirely). Cross-merchant READ on the funnel (admin queries via the IAdminQuery seam);
                -- full CRUD on admin.*/merch.* control-plane tables and SELECT/INSERT/UPDATE on cfg.* (HR master
                -- data — no DELETE, same as before the schema move); VaultSecrets stays INSERT-only (provisioning
                -- writes a PSP secret but can NEVER read plaintext back — masked read-back uses
                -- PspConnection.Metadata, not the vault; this is a hard security invariant, not an oversight).
                GRANT SELECT ON shop.Products         TO pol_admin;
                GRANT SELECT ON shop.Carts            TO pol_admin;
                GRANT SELECT ON shop.CartItems        TO pol_admin;
                GRANT SELECT ON shop.CheckoutSessions TO pol_admin;
                GRANT SELECT ON shop.Orders           TO pol_admin;
                GRANT SELECT ON txn.PaymentSessions   TO pol_admin;
                GRANT SELECT, INSERT ON txn.PspConnections   TO pol_admin;
                GRANT SELECT ON txn.IdempotencyRecords TO pol_admin;
                GRANT INSERT ON txn.OutboxMessages TO pol_admin; -- merchant-less registration event (REQ-20.2)

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

                -- pol_admin on the central iam catalog (rf2 grant matrix, REQ-9.1): read-only on the vocabulary,
                -- full CRUD on Roles/RolePermissions (both consoles' role management runs on the keyed pol_admin
                -- connection). pol_app is deliberately granted NOTHING on iam.* — the funnel never resolves
                -- permissions, and per-request resolution for BOTH sides runs on pol_admin.
                GRANT SELECT                         ON iam.PermissionGroups TO pol_admin;
                GRANT SELECT                         ON iam.Permissions      TO pol_admin;
                GRANT SELECT, INSERT, UPDATE, DELETE ON iam.Roles           TO pol_admin;
                GRANT SELECT, INSERT, UPDATE, DELETE ON iam.RolePermissions TO pol_admin;

                GRANT SELECT, INSERT ON dbo.DataProtectionKeys TO pol_admin;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse order: grants, then policy (it depends on the predicate functions), then procs/functions,
            // then the raw table. Schema AUTHORIZATION is not reverted (mirrors EF's own generated Down, which
            // never drops a schema either).
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
            migrationBuilder.Sql("DROP TABLE IF EXISTS merch.RegistrationNotices;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS sec.usp_vault_audit_head;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS sec.usp_resolve_order_summary;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS sec.usp_resolve_webhook_merchant;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS sec.fn_outbox_predicate;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS sec.fn_cartitem_predicate;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS sec.fn_merchant_predicate;");
        }
    }
}
