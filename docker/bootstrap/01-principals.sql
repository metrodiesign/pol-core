-- pol-core DB principals bootstrap (idempotent). Runs as sa, BEFORE EF migrations.
-- Contains NO secrets: passwords are passed as sqlcmd variables, e.g.
--   sqlcmd -v POL_APP_PASSWORD=... POL_ADMIN_PASSWORD=... POL_WORKER_PASSWORD=... -i 01-principals.sql
-- Object-level GRANT/DENY live in the RLS EF migration (G2), AFTER the tables exist.
SET NOCOUNT ON;

IF DB_ID(N'$(DbName)') IS NULL
    EXEC(N'CREATE DATABASE [$(DbName)]');
GO

-- Server logins (one per runtime host; NONE is sysadmin — RLS applies to every principal).
IF NOT EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = N'pol_app')
    CREATE LOGIN pol_app    WITH PASSWORD = N'$(POL_APP_PASSWORD)',    CHECK_POLICY = ON;
IF NOT EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = N'pol_admin')
    CREATE LOGIN pol_admin  WITH PASSWORD = N'$(POL_ADMIN_PASSWORD)',  CHECK_POLICY = ON;
IF NOT EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = N'pol_worker')
    CREATE LOGIN pol_worker WITH PASSWORD = N'$(POL_WORKER_PASSWORD)', CHECK_POLICY = ON;
GO

USE [$(DbName)];
GO

-- Database users.
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'pol_app')
    CREATE USER pol_app    FOR LOGIN pol_app;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'pol_admin')
    CREATE USER pol_admin  FOR LOGIN pol_admin;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'pol_worker')
    CREATE USER pol_worker FOR LOGIN pol_worker;

-- Login-less user that the order-summary/webhook resolve procs run as (WITH EXECUTE AS). Its bypass-role
-- membership is the ONLY reason those procs can read the connection->merchant mapping; nothing can log
-- in as it.
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'pol_resolver')
    CREATE USER pol_resolver WITHOUT LOGIN;

-- Login-less user that the vault reveal-audit head-read proc runs as (WITH EXECUTE AS). Its bypass-role
-- membership lets that proc read a merchant's audit chain head while pol_app (INSERT-only on the audit table)
-- cannot SELECT it. Least privilege: this user is granted SELECT on VCentralPay.VaultRevealAudits ONLY.
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'pol_vault_auditor')
    CREATE USER pol_vault_auditor WITHOUT LOGIN;
GO

-- The RLS bypass role. Membership = the only cross-merchant escape hatch (proven on live SQL 2025:
-- ownership chaining and EXECUTE AS OWNER do NOT bypass RLS; role membership does). pol_app and
-- pol_worker are deliberately NOT members. T5: pol_admin is deliberately NOT a member either — the
-- keyed "admin" PolDbContext now stamps its own SESSION_CONTEXT via AdminActorContext, so it goes
-- through the same RLS floor as every other principal instead of bypassing it.
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'pol_rls_bypass' AND type = 'R')
    CREATE ROLE pol_rls_bypass;
IF IS_ROLEMEMBER(N'pol_rls_bypass', N'pol_admin') = 1
    ALTER ROLE pol_rls_bypass DROP MEMBER pol_admin;
IF IS_ROLEMEMBER(N'pol_rls_bypass', N'pol_resolver') = 0
    ALTER ROLE pol_rls_bypass ADD MEMBER pol_resolver;
IF IS_ROLEMEMBER(N'pol_rls_bypass', N'pol_vault_auditor') = 0
    ALTER ROLE pol_rls_bypass ADD MEMBER pol_vault_auditor;
GO
