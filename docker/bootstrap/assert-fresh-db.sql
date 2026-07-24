-- Fresh-DB gate (rf1-schema-reset REQ-7.7): asserts every hand-written raw object from the
-- SecurityObjects/SeedData migrations landed on a freshly-migrated DB. Runs AFTER
-- `dotnet ef database update`, BEFORE the test suite — a fast catalog-level smoke check so a
-- silently-partial migration fails here with a precise message instead of surfacing later as a
-- confusing permission-denied buried in Integration.Tests output.
--   sqlcmd -S <server> -U sa -P <pw> -C -b -v DbName=VCentralPay -i assert-fresh-db.sql
-- `-b` makes sqlcmd exit non-zero on the first THROW below.
-- rls-to-query-filter task 8 (RlsTeardownAndOnePrincipal migration): the RLS/5-principal apparatus this
-- file used to assert the PRESENCE of is now torn down — the checks below assert its ABSENCE instead, so
-- a regression that accidentally re-introduces the old apparatus fails CI loudly instead of silently.
SET NOCOUNT ON;
USE [$(DbName)];
GO

DECLARE @fail nvarchar(max) = N'';

-- --- Schemas (6) + dbo ownership (REQ-3.10 — ownership chaining requires dbo) ---
IF (SELECT COUNT(*) FROM sys.schemas s JOIN sys.database_principals dp ON dp.principal_id = s.principal_id
    WHERE s.name IN (N'admin', N'cfg', N'iam', N'merch', N'shop', N'txn') AND dp.name = N'dbo') <> 6
    SET @fail += N'schemas: expected 6 of {admin,cfg,iam,merch,shop,txn} owned by dbo; ';

-- --- sec schema itself must not exist (DropEmptySecSchema — RlsTeardownAndOnePrincipal already
-- tore down every object inside it; this migration drops the now-empty container) ---
IF EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'sec')
    SET @fail += N'schema sec must not exist; ';

-- --- Raw table: merch.RegistrationNotices (ExcludeFromMigrations — EF never diffs/creates it) ---
IF OBJECT_ID(N'merch.RegistrationNotices', N'U') IS NULL
    SET @fail += N'table merch.RegistrationNotices missing; ';

-- --- No RLS predicate function survives (torn down by RlsTeardownAndOnePrincipal) ---
IF OBJECT_ID(N'sec.fn_merchant_predicate', N'IF') IS NOT NULL
    SET @fail += N'function sec.fn_merchant_predicate must not exist; ';
IF OBJECT_ID(N'sec.fn_cartitem_predicate', N'IF') IS NOT NULL
    SET @fail += N'function sec.fn_cartitem_predicate must not exist; ';
IF OBJECT_ID(N'sec.fn_outbox_predicate', N'IF') IS NOT NULL
    SET @fail += N'function sec.fn_outbox_predicate must not exist; ';

-- --- No EXECUTE-AS resolver/audit-head proc survives (pol_app reads the tables directly now) ---
IF OBJECT_ID(N'sec.usp_resolve_webhook_merchant', N'P') IS NOT NULL
    SET @fail += N'proc sec.usp_resolve_webhook_merchant must not exist; ';
IF OBJECT_ID(N'sec.usp_resolve_order_summary', N'P') IS NOT NULL
    SET @fail += N'proc sec.usp_resolve_order_summary must not exist; ';
IF OBJECT_ID(N'sec.usp_vault_audit_head', N'P') IS NOT NULL
    SET @fail += N'proc sec.usp_vault_audit_head must not exist; ';

-- --- No security policy survives ---
IF EXISTS (SELECT 1 FROM sys.security_policies WHERE name = N'MerchantIsolationPolicy')
    SET @fail += N'sec.MerchantIsolationPolicy must not exist; ';

-- --- 1-principal end state: pol_app is the SOLE runtime principal — no legacy principal and no
-- pol_rls_bypass role survives the collapse (rls-to-query-filter task 8, design.md "Human sign-off
-- item — RESOLVED") ---
IF EXISTS (SELECT 1 FROM sys.database_principals
           WHERE name IN (N'pol_admin', N'pol_worker', N'pol_resolver', N'pol_vault_auditor'))
    SET @fail += N'legacy principal (pol_admin/pol_worker/pol_resolver/pol_vault_auditor) must not exist; ';
IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'pol_rls_bypass' AND type = 'R')
    SET @fail += N'pol_rls_bypass role must not exist; ';

-- --- Grants: pol_app has >=1 GRANT (floor) ---
IF (SELECT COUNT(*) FROM sys.database_permissions p
    JOIN sys.database_principals dp ON dp.principal_id = p.grantee_principal_id
    WHERE dp.name = N'pol_app' AND p.state = 'G') = 0
    SET @fail += N'pol_app has zero GRANTs; ';

-- --- Seeds: central RBAC catalog (rf2 — iam.* replaced the two per-side catalogs) + HR master data
-- (exact counts — fixed VALUES lists, no NEWID rows) ---
IF (SELECT COUNT(*) FROM iam.PermissionGroups) <> 10
    SET @fail += N'iam.PermissionGroups expected 10 rows; ';
IF (SELECT COUNT(*) FROM iam.Permissions) <> 24
    SET @fail += N'iam.Permissions expected 24 rows; ';
IF (SELECT COUNT(*) FROM iam.Roles) <> 4
    SET @fail += N'iam.Roles expected 4 rows; ';
IF (SELECT COUNT(*) FROM iam.RolePermissions) <> 34
    SET @fail += N'iam.RolePermissions expected 34 rows; ';
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
-- cfg.* was pol_admin-only pre-collapse (REQ-3.4); post-collapse pol_app is the sole principal and
-- legitimately holds cfg.* grants (RlsTeardownAndOnePrincipal's GRANT block) — no negative check left here.

IF LEN(@fail) > 0
    THROW 50000, @fail, 1;

PRINT N'assert-fresh-db: OK — schemas, RegistrationNotices, no legacy RLS object/principal survives, pol_app grant floor, iam RBAC catalog + master-data seed counts all verified.';
GO
