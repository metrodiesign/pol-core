-- pol-core DB principal bootstrap (idempotent). Runs as sa, BEFORE EF migrations.
-- Contains NO secrets: the password is passed as a sqlcmd variable, e.g.
--   sqlcmd -v POL_APP_PASSWORD=... -i 01-principals.sql
-- Object-level GRANT/DENY live in the EF migration, AFTER the tables exist.
SET NOCOUNT ON;

DECLARE @major int = TRY_CONVERT(int, SERVERPROPERTY('ProductMajorVersion'));
DECLARE @version nvarchar(128) = CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion'));
DECLARE @build int = TRY_CONVERT(int, PARSENAME(@version, 2));
DECLARE @revision int = TRY_CONVERT(int, PARSENAME(@version, 1));
IF @major <> 17 OR @build < 4045 OR (@build = 4045 AND @revision < 5)
    THROW 50000, N'01-principals: SQL Server 2025 CU5 (17.0.4045.5) or newer is required.', 1;
GO

IF DB_ID(N'$(DbName)') IS NULL
    EXEC(N'CREATE DATABASE [$(DbName)] COLLATE Thai_100_CI_AS');
GO

ALTER DATABASE [$(DbName)] SET COMPATIBILITY_LEVEL = 170;
GO

IF (SELECT compatibility_level FROM sys.databases WHERE name = N'$(DbName)') <> 170
    THROW 50000, N'01-principals: database compatibility level 170 is required.', 1;
GO

-- Collation gate: a fresh CREATE DATABASE above always pins Thai_100_CI_AS, but the EF dev
-- auto-migrate path (src/Hosts/Api/Program.cs MigrateAsync()) can also create $(DbName) itself —
-- without COLLATE — if it races ahead of this script, and the guard above then never runs again.
-- Fail loudly here instead of shipping mojibake into every downstream varchar column.
DECLARE @dbCollation nvarchar(128) = ISNULL(CONVERT(nvarchar(128), DATABASEPROPERTYEX(N'$(DbName)', N'Collation')), N'');
IF @dbCollation <> N'Thai_100_CI_AS'
BEGIN
    DECLARE @collationMsg nvarchar(400) = CONCAT(
        N'01-principals: database [$(DbName)] collation is ', @dbCollation,
        N', expected Thai_100_CI_AS. Back up the database first, then recreate it (reset-only cutover: drop and re-run bootstrap) to fix.');
    THROW 50000, @collationMsg, 1;
END
GO

-- Sole runtime login (rls-to-query-filter task 8: RLS teardown collapsed pol_admin/pol_worker/
-- pol_resolver/pol_vault_auditor + the pol_rls_bypass role into pol_app — the app-layer EF query-filter
-- floor replaced SQL Server RLS, so there is no bypass role or EXECUTE-AS resolver/auditor left to
-- provision here).
IF NOT EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = N'pol_app')
    CREATE LOGIN pol_app WITH PASSWORD = N'$(POL_APP_PASSWORD)', CHECK_POLICY = ON;
GO

USE [$(DbName)];
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'pol_app')
    CREATE USER pol_app FOR LOGIN pol_app;
GO
