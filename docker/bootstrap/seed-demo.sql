-- pol-core demo seed data (dev only). NOT part of the EF migration chain — run by hand after
-- `dotnet ef database update` via scripts/seed-demo.sh. Idempotent (delete-by-deterministic-prefix,
-- then re-insert), one transaction, goes through sec.MerchantIsolationPolicy normally (no bypass-role
-- tricks, no EXECUTE AS). See .ai/specs/demo-seed-data/{requirements,design}.md.
--   sqlcmd -S <server> -U sa -P <pw> -C -b -v DbName=VCentralPay -i seed-demo.sql
-- `-b` = sqlcmd exits non-zero when the self-check at the bottom THROWs (REQ-1.6).
SET NOCOUNT ON;
SET XACT_ABORT ON;
-- sec.MerchantIsolationPolicy's predicates are schema-bound inline functions (same family as
-- indexed views / filtered indexes) — DML against a policy-protected table requires
-- QUOTED_IDENTIFIER ON. sqlcmd's default session does not set this.
SET QUOTED_IDENTIFIER ON;
USE [$(DbName)];
GO

BEGIN TRAN;

-- ============================================================================
-- (ก) admin.Users + its control-plane children (none of these 3 tables carry
-- a MerchantId FILTER predicate — see SecurityObjects migration's MerchantTables
-- list). Delete own demo rows by prefix (child -> parent), then re-insert.
--                                                          REQ-1.3 / REQ-2.4 / REQ-4.1 / REQ-4.2
-- ============================================================================
DELETE FROM admin.RoleAssignments WHERE Id LIKE 'e4000000-%';
DELETE FROM admin.MerchantAccess  WHERE Id LIKE 'e3000000-%';
DELETE FROM admin.Users           WHERE Id LIKE 'e2000000-%';

-- Tier: Scoped = 0, Super = 1. Status: Active = 0, Suspended = 1.
-- PositionId/OfficeId/LevelId/DivisionId point at cfg.* rows the SeedData migration already
-- created (a1.../b2.../c3.../d4... prefixes) — no new master-data rows.
INSERT INTO admin.Users (Id, Subject, Email, Tier, Status, CreatedAt, PositionId, OfficeId, LevelId, DivisionId)
VALUES
    ('e2000000-0000-4000-8000-000000000001', N'demo-adm-1', N'superadmin1@demo.pol.local', 1, 0, SYSUTCDATETIME(),
        'a1000000-0000-4000-8000-000000000001', 'b2000000-0000-4000-8000-000000000001', 'c3000000-0000-4000-8000-000000000001', 'd4000000-0000-4000-8000-000000000001'),
    ('e2000000-0000-4000-8000-000000000002', N'demo-adm-2', N'superadmin2@demo.pol.local', 1, 0, SYSUTCDATETIME(),
        'a1000000-0000-4000-8000-000000000002', 'b2000000-0000-4000-8000-000000000001', 'c3000000-0000-4000-8000-000000000001', 'd4000000-0000-4000-8000-000000000001'),
    ('e2000000-0000-4000-8000-000000000003', N'demo-adm-3', N'scopedadmin1@demo.pol.local', 0, 0, SYSUTCDATETIME(),
        'a1000000-0000-4000-8000-000000000007', 'b2000000-0000-4000-8000-000000000002', 'c3000000-0000-4000-8000-000000000003', 'd4000000-0000-4000-8000-000000000004'),
    ('e2000000-0000-4000-8000-000000000004', N'demo-adm-4', N'scopedadmin2@demo.pol.local', 0, 0, SYSUTCDATETIME(),
        'a1000000-0000-4000-8000-000000000007', 'b2000000-0000-4000-8000-000000000004', 'c3000000-0000-4000-8000-000000000003', 'd4000000-0000-4000-8000-000000000006'),
    ('e2000000-0000-4000-8000-000000000005', N'demo-adm-5', N'auditor1@demo.pol.local', 0, 0, SYSUTCDATETIME(),
        'a1000000-0000-4000-8000-00000000000a', 'b2000000-0000-4000-8000-000000000001', 'c3000000-0000-4000-8000-000000000005', 'd4000000-0000-4000-8000-000000000007'),
    ('e2000000-0000-4000-8000-000000000006', N'demo-adm-6', N'suspendedadmin@demo.pol.local', 0, 1, SYSUTCDATETIME(),
        'a1000000-0000-4000-8000-00000000000b', 'b2000000-0000-4000-8000-000000000003', 'c3000000-0000-4000-8000-000000000006', 'd4000000-0000-4000-8000-000000000009');

-- ============================================================================
-- (ข) Stamp the RLS session context onto the Super branch of sec.fn_merchant_predicate.
-- Must run AFTER (ก) (the row it points at has to exist) and BEFORE (ค) — see note there.
--                                                                              REQ-2.2
-- ============================================================================
EXEC sp_set_session_context @key = N'UserId',     @value = 'e2000000-0000-4000-8000-000000000001';
EXEC sp_set_session_context @key = N'MerchantId', @value = '00000000-0000-0000-0000-000000000000';

-- ============================================================================
-- (ค) Delete demo merchant-scoped rows — FULL child -> parent list (design.md §2/§3),
-- even though T1 only inserts admin.Users above. T2/T3/T4 append INSERTs later and
-- must never need to reorder this block.                              REQ-1.3 / REQ-2.4
--
-- TRAP: sec.MerchantIsolationPolicy's FILTER predicate applies to DELETE too (not just
-- SELECT). Running this block BEFORE (ข) stamps the session context "succeeds" silently
-- with 0 rows removed (RLS filters everything out to nobody), and the next re-run's
-- INSERT then collides on primary key. `sa` does NOT bypass this (only pol_rls_bypass
-- role membership does — see 01-principals.sql) — that's the whole point of REQ-2.3.
-- ============================================================================
DELETE FROM txn.PaymentSessions   WHERE Id LIKE 'ee000000-%';
DELETE FROM shop.Orders           WHERE Id LIKE 'ed000000-%';
DELETE FROM shop.CheckoutSessions WHERE Id LIKE 'ec000000-%';
DELETE FROM shop.CartItems        WHERE Id LIKE 'eb000000-%';
DELETE FROM shop.Carts            WHERE Id LIKE 'ea000000-%';
DELETE FROM shop.Products         WHERE Id LIKE 'e9000000-%';
DELETE FROM txn.PspConnections    WHERE Id LIKE 'e8000000-%';
DELETE FROM merch.RoleAssignments WHERE Id LIKE 'e7000000-%';
DELETE FROM merch.ExternalLogins  WHERE Id LIKE 'e6000000-%';
DELETE FROM merch.Users           WHERE Id LIKE 'e5000000-%';
DELETE FROM merch.Merchants       WHERE Id LIKE 'e1000000-%';

-- ============================================================================
-- (ง) Re-insert merchant-scoped demo data, parent -> child. T1 adds none — T2
-- (merchants/PSP/platform access+roles), T3 (merchant users/products) and T4
-- (funnel: carts/checkouts/orders/payment sessions) each append their INSERT
-- block here, in this order.
-- ============================================================================

-- ============================================================================
-- (จ) Self-check: every table already seeded must have its expected demo row
-- count, or the whole seed is incomplete. T1 asserts only admin.Users; T2-T4
-- append their own tables to @counts as they add INSERTs.                REQ-1.6
-- ============================================================================
DECLARE @counts TABLE (TableName nvarchar(64) NOT NULL, Rows int NOT NULL);

INSERT INTO @counts (TableName, Rows) VALUES
    (N'admin.Users', (SELECT COUNT(*) FROM admin.Users WHERE Id LIKE 'e2000000-%'));

DECLARE @report nvarchar(max) = (
    SELECT STRING_AGG(TableName + N' = ' + CAST(Rows AS nvarchar(10)), CHAR(13) + CHAR(10))
    FROM @counts);
PRINT @report;

IF EXISTS (SELECT 1 FROM @counts WHERE Rows = 0)
BEGIN
    DECLARE @failMsg nvarchar(2048) = N'seed-demo: some target table got 0 rows — seed is incomplete: ' +
        (SELECT STRING_AGG(TableName, N', ') FROM @counts WHERE Rows = 0);
    THROW 51000, @failMsg, 1;
END

COMMIT;
GO

PRINT N'seed-demo: OK.';
GO
