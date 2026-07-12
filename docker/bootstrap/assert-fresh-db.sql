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

-- --- Schemas (5) + dbo ownership (REQ-3.10 — ownership chaining requires dbo) ---
IF (SELECT COUNT(*) FROM sys.schemas s JOIN sys.database_principals dp ON dp.principal_id = s.principal_id
    WHERE s.name IN (N'admin', N'merch', N'sec', N'shop', N'txn') AND dp.name = N'dbo') <> 5
    SET @fail += N'schemas: expected 5 of {admin,merch,sec,shop,txn} owned by dbo; ';

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

-- --- Seeds: RBAC catalogs + HR master data (exact counts — fixed VALUES lists, no NEWID rows) ---
IF (SELECT COUNT(*) FROM admin.PermissionGroups) <> 6
    SET @fail += N'admin.PermissionGroups expected 6 rows; ';
IF (SELECT COUNT(*) FROM admin.Permissions) <> 16
    SET @fail += N'admin.Permissions expected 16 rows; ';
IF (SELECT COUNT(*) FROM admin.Roles) <> 5
    SET @fail += N'admin.Roles expected 5 rows; ';
IF (SELECT COUNT(*) FROM admin.Positions) <> 12
    SET @fail += N'admin.Positions expected 12 rows; ';
IF (SELECT COUNT(*) FROM admin.Offices) <> 8
    SET @fail += N'admin.Offices expected 8 rows; ';
IF (SELECT COUNT(*) FROM admin.Levels) <> 10
    SET @fail += N'admin.Levels expected 10 rows; ';
IF (SELECT COUNT(*) FROM admin.Divisions) <> 10
    SET @fail += N'admin.Divisions expected 10 rows; ';
IF (SELECT COUNT(*) FROM merch.PermissionGroups) <> 3
    SET @fail += N'merch.PermissionGroups expected 3 rows; ';
IF (SELECT COUNT(*) FROM merch.Permissions) <> 7
    SET @fail += N'merch.Permissions expected 7 rows; ';
IF (SELECT COUNT(*) FROM merch.Roles) <> 2
    SET @fail += N'merch.Roles expected 2 rows; ';

IF LEN(@fail) > 0
    THROW 50000, @fail, 1;

PRINT N'assert-fresh-db: OK — schemas, RegistrationNotices, 3 functions, 3 procs, policy, bypass-role membership, grant floor + 2 security invariants, RBAC/master-data seed counts all verified.';
GO
