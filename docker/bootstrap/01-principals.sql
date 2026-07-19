-- pol-core DB principal bootstrap (idempotent). Runs as sa, BEFORE EF migrations.
-- Contains NO secrets: the password is passed as a sqlcmd variable, e.g.
--   sqlcmd -v POL_APP_PASSWORD=... -i 01-principals.sql
-- Object-level GRANT/DENY live in the EF migration, AFTER the tables exist.
SET NOCOUNT ON;

IF DB_ID(N'$(DbName)') IS NULL
    EXEC(N'CREATE DATABASE [$(DbName)]');
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
