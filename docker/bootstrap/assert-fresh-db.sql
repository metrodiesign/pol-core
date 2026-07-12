-- Fresh-DB gate (rf1-schema-reset REQ-7.7): asserts every hand-written raw object from the
-- SecurityObjects/SeedData migrations landed on a freshly-migrated DB. Runs AFTER
-- `dotnet ef database update`, BEFORE the test suite — a fast catalog-level smoke check so a
-- silently-partial migration fails here with a precise message instead of surfacing later as a
-- confusing permission-denied buried in Integration.Tests output.
--   sqlcmd -S <server> -U sa -P <pw> -C -b -v DbName=VCentralPay -i assert-fresh-db.sql
-- `-b` makes sqlcmd exit non-zero on the first THROW below.
SET NOCOUNT ON;
USE [$(DbName)];
GO

DECLARE @fail nvarchar(max) = N'';

-- --- Schemas (7) + dbo ownership (REQ-3.10 — ownership chaining requires dbo) ---
IF (SELECT COUNT(*) FROM sys.schemas s JOIN sys.database_principals dp ON dp.principal_id = s.principal_id
    WHERE s.name IN (N'admin', N'cfg', N'iam', N'merch', N'sec', N'shop', N'txn') AND dp.name = N'dbo') <> 7
    SET @fail += N'schemas: expected 7 of {admin,cfg,iam,merch,sec,shop,txn} owned by dbo; ';

-- --- Raw table: merch.RegistrationNotices (ExcludeFromMigrations — EF never diffs/creates it) ---
IF OBJECT_ID(N'merch.RegistrationNotices', N'U') IS NULL
    SET @fail += N'table merch.RegistrationNotices missing; ';

-- --- Functions (3) ---
IF OBJECT_ID(N'sec.fn_merchant_predicate', N'IF') IS NULL
    SET @fail += N'function sec.fn_merchant_predicate missing; ';
IF OBJECT_ID(N'sec.fn_cartitem_predicate', N'IF') IS NULL
    SET @fail += N'function sec.fn_cartitem_predicate missing; ';
IF OBJECT_ID(N'sec.fn_outbox_predicate', N'IF') IS NULL
    SET @fail += N'function sec.fn_outbox_predicate missing; ';

-- --- Procs (3) ---
IF OBJECT_ID(N'sec.usp_resolve_webhook_merchant', N'P') IS NULL
    SET @fail += N'proc sec.usp_resolve_webhook_merchant missing; ';
IF OBJECT_ID(N'sec.usp_resolve_order_summary', N'P') IS NULL
    SET @fail += N'proc sec.usp_resolve_order_summary missing; ';
IF OBJECT_ID(N'sec.usp_vault_audit_head', N'P') IS NULL
    SET @fail += N'proc sec.usp_vault_audit_head missing; ';

-- --- Security policy: enabled + schema-bound (REQ-3.6) ---
IF NOT EXISTS (SELECT 1 FROM sys.security_policies
               WHERE name = N'MerchantIsolationPolicy' AND is_enabled = 1 AND is_schema_bound = 1)
    SET @fail += N'sec.MerchantIsolationPolicy missing/disabled/not schema-bound; ';

-- --- RLS bypass role membership (REQ-3.8/3.9 — T5: pol_admin is deliberately NOT a member) ---
IF ISNULL(IS_ROLEMEMBER(N'pol_rls_bypass', N'pol_admin'), 0) <> 0
    SET @fail += N'pol_admin must NOT be in pol_rls_bypass; ';
IF ISNULL(IS_ROLEMEMBER(N'pol_rls_bypass', N'pol_app'), 0) <> 0
    SET @fail += N'pol_app must NOT be in pol_rls_bypass; ';
IF ISNULL(IS_ROLEMEMBER(N'pol_rls_bypass', N'pol_worker'), 0) <> 0
    SET @fail += N'pol_worker must NOT be in pol_rls_bypass; ';
IF ISNULL(IS_ROLEMEMBER(N'pol_rls_bypass', N'pol_resolver'), 0) <> 1
    SET @fail += N'pol_resolver must be in pol_rls_bypass; ';
IF ISNULL(IS_ROLEMEMBER(N'pol_rls_bypass', N'pol_vault_auditor'), 0) <> 1
    SET @fail += N'pol_vault_auditor must be in pol_rls_bypass; ';

-- --- Grants: floor (every runtime principal has >=1 GRANT) + the 2 security-critical negatives the
-- SecurityObjects migration comment calls out as hard invariants (never a rename/reset regression target) ---
IF EXISTS (SELECT 1 FROM (VALUES (N'pol_app'), (N'pol_admin'), (N'pol_worker'), (N'pol_resolver'), (N'pol_vault_auditor')) v(name)
           WHERE (SELECT COUNT(*) FROM sys.database_permissions p
                  JOIN sys.database_principals dp ON dp.principal_id = p.grantee_principal_id
                  WHERE dp.name = v.name AND p.state = 'G') = 0)
    SET @fail += N'one or more runtime principals have zero GRANTs; ';
IF EXISTS (SELECT 1 FROM sys.database_permissions p
           JOIN sys.database_principals dp ON dp.principal_id = p.grantee_principal_id
           WHERE dp.name = N'pol_app' AND p.permission_name = 'SELECT'
             AND p.major_id = OBJECT_ID(N'merch.VaultRevealAudits'))
    SET @fail += N'pol_app must not have SELECT on merch.VaultRevealAudits (append-only invariant); ';
IF EXISTS (SELECT 1 FROM sys.database_permissions p
           JOIN sys.database_principals dp ON dp.principal_id = p.grantee_principal_id
           WHERE dp.name = N'pol_admin' AND p.permission_name = 'SELECT'
             AND p.major_id = OBJECT_ID(N'merch.VaultSecrets'))
    SET @fail += N'pol_admin must not have SELECT on merch.VaultSecrets (no plaintext read-back invariant); ';

-- --- Seeds: central RBAC catalog (rf2 — iam.* replaced the two per-side catalogs) + HR master data
-- (exact counts — fixed VALUES lists, no NEWID rows) ---
IF (SELECT COUNT(*) FROM iam.PermissionGroups) <> 8
    SET @fail += N'iam.PermissionGroups expected 8 rows; ';
IF (SELECT COUNT(*) FROM iam.Permissions) <> 20
    SET @fail += N'iam.Permissions expected 20 rows; ';
IF (SELECT COUNT(*) FROM iam.Roles) <> 4
    SET @fail += N'iam.Roles expected 4 rows; ';
IF (SELECT COUNT(*) FROM iam.RolePermissions) <> 28
    SET @fail += N'iam.RolePermissions expected 28 rows; ';
IF OBJECT_ID(N'admin.Roles', N'U') IS NOT NULL OR OBJECT_ID(N'merch.Roles', N'U') IS NOT NULL
    SET @fail += N'legacy per-side RBAC catalog tables must not exist (rf2 cutover); ';
-- master data lives in cfg since the masterdata-module cutover (it left the Admins module) —
-- assert the old admin.* copies are gone, so a half-applied schema move fails here, not later
IF OBJECT_ID(N'admin.Positions', N'U') IS NOT NULL OR OBJECT_ID(N'admin.Offices', N'U') IS NOT NULL
   OR OBJECT_ID(N'admin.Levels', N'U') IS NOT NULL OR OBJECT_ID(N'admin.Divisions', N'U') IS NOT NULL
    SET @fail += N'master-data tables must not exist in admin (masterdata-module cutover moved them to cfg); ';
IF (SELECT COUNT(*) FROM cfg.Positions) <> 12
    SET @fail += N'cfg.Positions expected 12 rows; ';
IF (SELECT COUNT(*) FROM cfg.Offices) <> 8
    SET @fail += N'cfg.Offices expected 8 rows; ';
IF (SELECT COUNT(*) FROM cfg.Levels) <> 10
    SET @fail += N'cfg.Levels expected 10 rows; ';
IF (SELECT COUNT(*) FROM cfg.Divisions) <> 10
    SET @fail += N'cfg.Divisions expected 10 rows; ';
-- cfg.* is pol_admin-only (REQ-3.4) — the funnel principal never reads reference data
IF EXISTS (SELECT 1 FROM sys.database_permissions p
           JOIN sys.database_principals dp ON dp.principal_id = p.grantee_principal_id
           JOIN sys.objects o ON o.object_id = p.major_id
           WHERE dp.name = N'pol_app' AND p.state = 'G' AND SCHEMA_NAME(o.schema_id) = N'cfg')
    SET @fail += N'pol_app must have no grants on cfg.* ; ';

IF LEN(@fail) > 0
    THROW 50000, @fail, 1;

PRINT N'assert-fresh-db: OK — schemas, RegistrationNotices, 3 functions, 3 procs, policy, bypass-role membership, grant floor + 2 security invariants, iam RBAC catalog + master-data seed counts all verified.';
GO
