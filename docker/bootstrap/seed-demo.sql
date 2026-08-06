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
DELETE FROM txn.PaymentSessions    WHERE Id LIKE 'ee000000-%';
DELETE FROM shop.OrderItemPolicies WHERE Id LIKE 'f1000000-%';
DELETE FROM shop.OrderItems        WHERE Id LIKE 'ef000000-%';
DELETE FROM shop.Orders            WHERE Id LIKE 'ed000000-%';
DELETE FROM shop.CheckoutSessions  WHERE Id LIKE 'ec000000-%';
DELETE FROM shop.CartItems        WHERE Id LIKE 'eb000000-%';
DELETE FROM shop.Carts            WHERE Id LIKE 'ea000000-%';
DELETE FROM txn.PspConnections    WHERE Id LIKE 'e8000000-%';
DELETE FROM merch.RoleAssignments WHERE Id LIKE 'e7000000-%';
DELETE FROM merch.ExternalLogins  WHERE Id LIKE 'e6000000-%';
DELETE FROM merch.Users           WHERE Id LIKE 'e5000000-%';
DELETE FROM merch.Merchants       WHERE Id LIKE 'e1000000-%';

-- ============================================================================
-- (ง) Re-insert merchant-scoped demo data, parent -> child. T1 adds none — T2
-- (merchants/PSP/platform access+roles), T3 (merchant users) and T4
-- (funnel: carts/checkouts/orders/payment sessions) each append their INSERT
-- block here, in this order.
-- ============================================================================

-- merch.Merchants (REQ-3.1). Code is stored NORMALIZED LOWERCASE (Merchant.Create ->
-- MerchantCode.Normalize) — 'vPrivilege' in the design doc's table is display notation only,
-- the DB row must be 'vprivilege' or MerchantCode.IsAllowed would never see it again on read.
-- Status: Active = 0 (only value). LegalEntityId is NOT NULL — demo-only placeholder tax ids.
INSERT INTO merch.Merchants (Id, Code, DisplayName, LegalEntityId, Status, Country, Currency, EnabledChannels, CreatedAt, Metadata)
VALUES
    ('e1000000-0000-4000-8000-000000000001', 'vprivilege', N'บริษัท วีพริวิเลจ จำกัด', '0105561000011', 0, 'TH', 'THB', 'card,promptpay,installment', SYSUTCDATETIME(), N'{}'),
    ('e1000000-0000-4000-8000-000000000002', 'vcommerce',  N'บริษัท วีคอมเมิร์ซ จำกัด', '0105561000029', 0, 'TH', 'THB', 'card,promptpay',              SYSUTCDATETIME(), N'{}'),
    ('e1000000-0000-4000-8000-000000000003', 'vsouvenir',  N'บริษัท วีซูวีเนียร์ จำกัด', '0105561000037', 0, 'TH', 'THB', 'card',                        SYSUTCDATETIME(), N'{}');

-- txn.PspConnections (REQ-3.2/3.3): 2 per merchant (2C2P Psp=0, Omise Psp=1). EnabledMethods is a
-- subset of the owning merchant's EnabledChannels. Metadata NULL (nullable). SecretRefName points
-- at a vault ref with no backing secret — merch.VaultSecrets is deliberately NOT seeded (REQ-3.3).
-- Omise on vSouvenir is IsEnabled=0 so both values of IsEnabled are represented (design §4).
-- purchase-flow-completion REQ-6.1: this column is now what a MERCHANT SEES AT CHECKOUT — a channel
-- missing from the 2C2P row is refused with 400 when the merchant starts a checkout, not later at pay
-- time. vPrivilege carries all three (the merchant to demo the full channel picker with); vCommerce and
-- vSouvenir stay narrower on purpose, so the refusal is demoable too. An EXISTING deployment must widen
-- txn.PspConnections.EnabledMethods itself — see the ops step in the PR.
INSERT INTO txn.PspConnections (Id, MerchantId, Psp, EnabledMethods, SecretRefName, Metadata, IsEnabled, CreatedAt)
VALUES
    ('e8000000-0000-4000-8000-000000000001', 'e1000000-0000-4000-8000-000000000001', 0, 'card,promptpay,installment', 'psp/vprivilege/2c2p', NULL, 1, SYSUTCDATETIME()),
    ('e8000000-0000-4000-8000-000000000002', 'e1000000-0000-4000-8000-000000000001', 1, 'card,promptpay,installment', 'psp/vprivilege/omise', NULL, 1, SYSUTCDATETIME()),
    ('e8000000-0000-4000-8000-000000000003', 'e1000000-0000-4000-8000-000000000002', 0, 'card,promptpay',             'psp/vcommerce/2c2p',  NULL, 1, SYSUTCDATETIME()),
    ('e8000000-0000-4000-8000-000000000004', 'e1000000-0000-4000-8000-000000000002', 1, 'card,promptpay',             'psp/vcommerce/omise', NULL, 1, SYSUTCDATETIME()),
    ('e8000000-0000-4000-8000-000000000005', 'e1000000-0000-4000-8000-000000000003', 0, 'card',                       'psp/vsouvenir/2c2p',  NULL, 1, SYSUTCDATETIME()),
    ('e8000000-0000-4000-8000-000000000006', 'e1000000-0000-4000-8000-000000000003', 1, 'card',                       'psp/vsouvenir/omise', NULL, 0, SYSUTCDATETIME());

-- admin.MerchantAccess (REQ-4.3): Scoped platform users only (Tier=0 — e2…0003/0004/0005).
-- Super (e2…0001/0002) gets zero rows — the Super branch of sec.fn_merchant_predicate never
-- reads this table. AssignedByAdminId = the Super used to stamp session context (ข).
INSERT INTO admin.MerchantAccess (Id, PlatformUserId, MerchantId, AssignedByAdminId, AssignedAt)
VALUES
    ('e3000000-0000-4000-8000-000000000001', 'e2000000-0000-4000-8000-000000000003', 'e1000000-0000-4000-8000-000000000001', 'e2000000-0000-4000-8000-000000000001', SYSUTCDATETIME()),
    ('e3000000-0000-4000-8000-000000000002', 'e2000000-0000-4000-8000-000000000004', 'e1000000-0000-4000-8000-000000000002', 'e2000000-0000-4000-8000-000000000001', SYSUTCDATETIME()),
    ('e3000000-0000-4000-8000-000000000003', 'e2000000-0000-4000-8000-000000000004', 'e1000000-0000-4000-8000-000000000003', 'e2000000-0000-4000-8000-000000000001', SYSUTCDATETIME()),
    ('e3000000-0000-4000-8000-000000000004', 'e2000000-0000-4000-8000-000000000005', 'e1000000-0000-4000-8000-000000000001', 'e2000000-0000-4000-8000-000000000001', SYSUTCDATETIME());

-- admin.RoleAssignments (REQ-4.4): RoleId is the role migration 20260712185912_SeedData already
-- seeded — no new iam.* rows. platform_admin -> both Super + Scoped e2…0003/0004;
-- platform_auditor -> e2…0005/0006. AssignedById = the Super used to stamp session context (ข).
-- NOTE: admin.RoleAssignments has NO MerchantId column (unlike merch.RoleAssignments) — do not add one.
INSERT INTO admin.RoleAssignments (Id, PlatformUserId, RoleId, AssignedById, AssignedAt)
VALUES
    ('e4000000-0000-4000-8000-000000000001', 'e2000000-0000-4000-8000-000000000001', '11111111-1111-1111-1111-111111111111', 'e2000000-0000-4000-8000-000000000001', SYSUTCDATETIME()),
    ('e4000000-0000-4000-8000-000000000002', 'e2000000-0000-4000-8000-000000000002', '11111111-1111-1111-1111-111111111111', 'e2000000-0000-4000-8000-000000000001', SYSUTCDATETIME()),
    ('e4000000-0000-4000-8000-000000000003', 'e2000000-0000-4000-8000-000000000003', '11111111-1111-1111-1111-111111111111', 'e2000000-0000-4000-8000-000000000001', SYSUTCDATETIME()),
    ('e4000000-0000-4000-8000-000000000004', 'e2000000-0000-4000-8000-000000000004', '11111111-1111-1111-1111-111111111111', 'e2000000-0000-4000-8000-000000000001', SYSUTCDATETIME()),
    ('e4000000-0000-4000-8000-000000000005', 'e2000000-0000-4000-8000-000000000005', '55555555-5555-5555-5555-555555555555', 'e2000000-0000-4000-8000-000000000001', SYSUTCDATETIME()),
    ('e4000000-0000-4000-8000-000000000006', 'e2000000-0000-4000-8000-000000000006', '55555555-5555-5555-5555-555555555555', 'e2000000-0000-4000-8000-000000000001', SYSUTCDATETIME());

-- merch.Users (REQ-5.1): 4 per merchant, all 4 UserStatus values + both PersonType values covered.
-- NOTE: merch.Users/ExternalLogins/RoleAssignments carry NO RLS predicate at all (see
-- SecurityObjects migration's MerchantTables list — only merch.Merchants/shop.*/txn.* are policy-
-- protected); the session-context stamp from (ข) is irrelevant here but harmless to leave in place.
-- Subject is fake (demo-mch-<n>) — REQ-5.2's Google login can never resolve to these.
-- SaleCode (products-external-source-of-truth REQ-6.10) MUST be an agent code that actually exists in
-- the upstream (hippodb/mammothdb seed the shared roster 77001-77006), or a merchant user who logs in
-- would search the live catalogue with a code the source has never heard of and see zero rows — the
-- exact demo-is-empty-and-nobody-can-tell-why failure this requirement exists to prevent. The old
-- PRD-VP-* placeholders were never in the source. varchar(20) non-unicode (REQ-10.6) — plain ASCII.
INSERT INTO merch.Users (Id, Subject, Email, Status, MerchantId, CreatedAt, DisplayName, FirstName, LastName, PersonType, IdNumber, SaleCode, LicenseNumber, Phone)
VALUES
    -- vprivilege (merchant e1...0001)
    ('e5000000-0000-4000-8000-000000000001', N'demo-mch-1', N'somchai.p@demo.pol.local', 1, 'e1000000-0000-4000-8000-000000000001', SYSUTCDATETIME(), N'สมชาย พริวิเลจ', N'สมชาย', N'พริวิเลจ', 0, N'1100200300401', '77001', N'LIC-2024-00101', N'0812345001'),
    ('e5000000-0000-4000-8000-000000000002', N'demo-mch-2', N'vprivilege.dist@demo.pol.local', 1, 'e1000000-0000-4000-8000-000000000001', SYSUTCDATETIME(), N'บริษัท วีพริวิเลจ ตัวแทนจำหน่าย จำกัด', N'-', N'-', 1, N'0105561000045', '77002', N'LIC-2024-00102', N'0812345002'),
    ('e5000000-0000-4000-8000-000000000003', N'demo-mch-3', N'wanida.k@demo.pol.local', 0, 'e1000000-0000-4000-8000-000000000001', SYSUTCDATETIME(), N'วนิดา คงพริวิเลจ', N'วนิดา', N'คงพริวิเลจ', 0, N'1100200300402', NULL, NULL, N'0812345003'),
    ('e5000000-0000-4000-8000-000000000004', N'demo-mch-4', N'pichit.s@demo.pol.local', 2, 'e1000000-0000-4000-8000-000000000001', SYSUTCDATETIME(), N'พิชิต แสงพริวิเลจ', N'พิชิต', N'แสงพริวิเลจ', 0, N'1100200300403', NULL, NULL, N'0812345004'),
    -- vcommerce (merchant e1...0002)
    ('e5000000-0000-4000-8000-000000000005', N'demo-mch-5', N'araya.c@demo.pol.local', 1, 'e1000000-0000-4000-8000-000000000002', SYSUTCDATETIME(), N'อารยา คอมเมิร์ซ', N'อารยา', N'คอมเมิร์ซ', 0, N'1100200300404', '77003', N'LIC-2024-00201', N'0823456001'),
    ('e5000000-0000-4000-8000-000000000006', N'demo-mch-6', N'vcommerce.hq@demo.pol.local', 1, 'e1000000-0000-4000-8000-000000000002', SYSUTCDATETIME(), N'บริษัท วีคอมเมิร์ซ โฮลดิ้ง จำกัด', N'-', N'-', 1, N'0105561000053', '77004', N'LIC-2024-00202', N'0823456002'),
    ('e5000000-0000-4000-8000-000000000007', N'demo-mch-7', N'natthapong.r@demo.pol.local', 3, 'e1000000-0000-4000-8000-000000000002', SYSUTCDATETIME(), N'ณัฐพงศ์ รุ่งคอมเมิร์ซ', N'ณัฐพงศ์', N'รุ่งคอมเมิร์ซ', 0, N'1100200300405', '77005', N'LIC-2024-00203', N'0823456003'),
    ('e5000000-0000-4000-8000-000000000008', N'demo-mch-8', N'suda.m@demo.pol.local', 0, 'e1000000-0000-4000-8000-000000000002', SYSUTCDATETIME(), N'สุดา มั่นคอมเมิร์ซ', N'สุดา', N'มั่นคอมเมิร์ซ', 0, N'1100200300406', NULL, NULL, N'0823456004'),
    -- vsouvenir (merchant e1...0003)
    ('e5000000-0000-4000-8000-000000000009', N'demo-mch-9', N'kanya.s@demo.pol.local', 1, 'e1000000-0000-4000-8000-000000000003', SYSUTCDATETIME(), N'กัญญา ซูวีเนียร์', N'กัญญา', N'ซูวีเนียร์', 0, N'1100200300407', '77006', N'LIC-2024-00301', N'0834567001'),
    ('e5000000-0000-4000-8000-00000000000a', N'demo-mch-10', N'vsouvenir.shop@demo.pol.local', 1, 'e1000000-0000-4000-8000-000000000003', SYSUTCDATETIME(), N'บริษัท วีซูวีเนียร์ ช็อป จำกัด', N'-', N'-', 1, N'0105561000061', '77001', N'LIC-2024-00302', N'0834567002'),
    ('e5000000-0000-4000-8000-00000000000b', N'demo-mch-11', N'thanawat.j@demo.pol.local', 0, 'e1000000-0000-4000-8000-000000000003', SYSUTCDATETIME(), N'ธนวัฒน์ จันทร์ซูวีเนียร์', N'ธนวัฒน์', N'จันทร์ซูวีเนียร์', 0, N'1100200300408', NULL, NULL, N'0834567003'),
    ('e5000000-0000-4000-8000-00000000000c', N'demo-mch-12', N'orawan.b@demo.pol.local', 2, 'e1000000-0000-4000-8000-000000000003', SYSUTCDATETIME(), N'อรวรรณ บุญซูวีเนียร์', N'อรวรรณ', N'บุญซูวีเนียร์', 0, N'1100200300409', NULL, NULL, N'0834567004');

-- merch.ExternalLogins (REQ-5.2): 1:1 with merch.Users, Subject matches, Provider = google.
INSERT INTO merch.ExternalLogins (Id, Provider, Subject, MerchantUserId)
VALUES
    ('e6000000-0000-4000-8000-000000000001', N'google', N'demo-mch-1',  'e5000000-0000-4000-8000-000000000001'),
    ('e6000000-0000-4000-8000-000000000002', N'google', N'demo-mch-2',  'e5000000-0000-4000-8000-000000000002'),
    ('e6000000-0000-4000-8000-000000000003', N'google', N'demo-mch-3',  'e5000000-0000-4000-8000-000000000003'),
    ('e6000000-0000-4000-8000-000000000004', N'google', N'demo-mch-4',  'e5000000-0000-4000-8000-000000000004'),
    ('e6000000-0000-4000-8000-000000000005', N'google', N'demo-mch-5',  'e5000000-0000-4000-8000-000000000005'),
    ('e6000000-0000-4000-8000-000000000006', N'google', N'demo-mch-6',  'e5000000-0000-4000-8000-000000000006'),
    ('e6000000-0000-4000-8000-000000000007', N'google', N'demo-mch-7',  'e5000000-0000-4000-8000-000000000007'),
    ('e6000000-0000-4000-8000-000000000008', N'google', N'demo-mch-8',  'e5000000-0000-4000-8000-000000000008'),
    ('e6000000-0000-4000-8000-000000000009', N'google', N'demo-mch-9',  'e5000000-0000-4000-8000-000000000009'),
    ('e6000000-0000-4000-8000-00000000000a', N'google', N'demo-mch-10', 'e5000000-0000-4000-8000-00000000000a'),
    ('e6000000-0000-4000-8000-00000000000b', N'google', N'demo-mch-11', 'e5000000-0000-4000-8000-00000000000b'),
    ('e6000000-0000-4000-8000-00000000000c', N'google', N'demo-mch-12', 'e5000000-0000-4000-8000-00000000000c');

-- merch.RoleAssignments (REQ-5.3): only Status=1 (Active) users — 2 per merchant, first=manager,
-- second=staff. RoleId from migration 20260712185912_SeedData.cs (no new iam.* rows). MerchantId
-- column IS present here (unlike admin.RoleAssignments — see T2 evidence).
INSERT INTO merch.RoleAssignments (Id, MerchantUserId, RoleId, MerchantId, AssignedById, AssignedAt)
VALUES
    ('e7000000-0000-4000-8000-000000000001', 'e5000000-0000-4000-8000-000000000001', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'e1000000-0000-4000-8000-000000000001', 'e5000000-0000-4000-8000-000000000001', SYSUTCDATETIME()),
    ('e7000000-0000-4000-8000-000000000002', 'e5000000-0000-4000-8000-000000000002', 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 'e1000000-0000-4000-8000-000000000001', 'e5000000-0000-4000-8000-000000000001', SYSUTCDATETIME()),
    ('e7000000-0000-4000-8000-000000000003', 'e5000000-0000-4000-8000-000000000005', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'e1000000-0000-4000-8000-000000000002', 'e5000000-0000-4000-8000-000000000005', SYSUTCDATETIME()),
    ('e7000000-0000-4000-8000-000000000004', 'e5000000-0000-4000-8000-000000000006', 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 'e1000000-0000-4000-8000-000000000002', 'e5000000-0000-4000-8000-000000000005', SYSUTCDATETIME()),
    ('e7000000-0000-4000-8000-000000000005', 'e5000000-0000-4000-8000-000000000009', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'e1000000-0000-4000-8000-000000000003', 'e5000000-0000-4000-8000-000000000009', SYSUTCDATETIME()),
    ('e7000000-0000-4000-8000-000000000006', 'e5000000-0000-4000-8000-00000000000a', 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 'e1000000-0000-4000-8000-000000000003', 'e5000000-0000-4000-8000-000000000009', SYSUTCDATETIME());

-- shop.Products is GONE (products-external-source-of-truth REQ-6.1 dropped the mirror catalogue). The
-- catalogue is now read live from the upstream SP per request, so there is nothing to seed here and
-- nothing downstream reads back from it. The cart/order rows below carry the upstream DocumentNo
-- directly (design.md "seed / demo"): every DocumentNo used is one the sim guarantees it emits AND
-- that the SP's mandatory search window keeps returning — the Non-Motor procedure always filters
-- StartDate >= DATEADD(month,-6,@today) (03-mammoth-sim.sql:238) with no parameter to disable it, so an
-- in-window row is required, not merely an existing one (26301/POL/000003 exists but sits at -245 days
-- and would resolve to 0 rows -> 409). Motor CMI/VMI come from 02-hippo-sim.sql, Non-Motor FIRE/MISC
-- from 03-mammoth-sim.sql, so a demo checkout resolves the document instead of 409-ing.

-- shop.Carts (REQ-6.1): 2 per merchant, string Status ('Open'/'CheckedOut' — CartConfiguration
-- uses HasConversion<string>() into nvarchar(16); an int here would violate the column mapping).
INSERT INTO shop.Carts (Id, MerchantId, Status, CreatedAt)
VALUES
    ('ea000000-0000-4000-8000-000000000001', 'e1000000-0000-4000-8000-000000000001', N'Open',       SYSUTCDATETIME()),
    ('ea000000-0000-4000-8000-000000000002', 'e1000000-0000-4000-8000-000000000001', N'CheckedOut', SYSUTCDATETIME()),
    ('ea000000-0000-4000-8000-000000000003', 'e1000000-0000-4000-8000-000000000002', N'Open',       SYSUTCDATETIME()),
    ('ea000000-0000-4000-8000-000000000004', 'e1000000-0000-4000-8000-000000000002', N'CheckedOut', SYSUTCDATETIME()),
    ('ea000000-0000-4000-8000-000000000005', 'e1000000-0000-4000-8000-000000000003', N'Open',       SYSUTCDATETIME()),
    ('ea000000-0000-4000-8000-000000000006', 'e1000000-0000-4000-8000-000000000003', N'Open',       SYSUTCDATETIME());

-- shop.CartItems (REQ-6.1): identified now by the upstream DocumentNo + SaleCode + ProductGroup
-- (products-external-source-of-truth REQ-2.2 — the ProductId Guid column was dropped with shop.Products).
-- Every DocumentNo below is a real sim row, UNPAID at source, IN the SP's mandatory search window
-- (Non-Motor: StartDate within the last 6 months — 03-mammoth-sim.sql:238; the window cannot be turned
-- off), under SaleCode 77001 (hippodb Motor: 69301/กธ|รย/*, mammothdb Non-Motor FIRE: 26301/POL/*), so a
-- demo checkout of these carts resolves the document instead of 409-ing. 26301/POL/000004 (FIRE RENEWAL,
-- -20 days) is used over its neighbour 000003 precisely because 000003 sits at -245 days, outside the window. Within one cart the DocumentNo values are distinct (Cart.AddItem refuses a
-- duplicate — REQ-9.4). UnitPriceAmount is the price frozen into the cart at add time (REQ-4.6) — it is the
-- demo figure carried over from before this cutover, not re-read from source. MerchantId is denormalized
-- onto this table (rls-to-query-filter task 8 — the EF global query filter reads it directly, no join
-- through CartId); it must match the parent Cart's MerchantId (checked by verify query below).
INSERT INTO shop.CartItems (Id, CartId, MerchantId, DocumentNo, SaleCode, ProductGroup, Quantity, UnitPriceAmount, UnitPriceCurrency)
VALUES
    -- cart ea…0001 (vprivilege, Open) — sum 23400.0000
    ('eb000000-0000-4000-8000-000000000001', 'ea000000-0000-4000-8000-000000000001', 'e1000000-0000-4000-8000-000000000001', N'69301/กธ/910001',  '77001', 'VMI',  1, 1200.0000,  'THB'),
    ('eb000000-0000-4000-8000-000000000002', 'ea000000-0000-4000-8000-000000000001', 'e1000000-0000-4000-8000-000000000001', N'69301/กธ/9100002', '77001', 'CMI',  2, 1850.0000,  'THB'),
    ('eb000000-0000-4000-8000-000000000003', 'ea000000-0000-4000-8000-000000000001', 'e1000000-0000-4000-8000-000000000001', N'26301/POL/000001', '77001', 'FIRE', 1, 18500.0000, 'THB'),
    -- cart ea…0002 (vprivilege, CheckedOut) — sum 56500.0000
    ('eb000000-0000-4000-8000-000000000004', 'ea000000-0000-4000-8000-000000000002', 'e1000000-0000-4000-8000-000000000001', N'69301/รย/910009',  '77001', 'VMI',  1, 32000.0000, 'THB'),
    ('eb000000-0000-4000-8000-000000000005', 'ea000000-0000-4000-8000-000000000002', 'e1000000-0000-4000-8000-000000000001', N'69301/กธ/800012',  '77001', 'VMI',  1, 24500.0000, 'THB'),
    -- cart ea…0003 (vcommerce, Open) — sum 12650.0000
    ('eb000000-0000-4000-8000-000000000006', 'ea000000-0000-4000-8000-000000000003', 'e1000000-0000-4000-8000-000000000002', N'69301/กธ/9100002', '77001', 'CMI',  3, 650.0000,   'THB'),
    ('eb000000-0000-4000-8000-000000000007', 'ea000000-0000-4000-8000-000000000003', 'e1000000-0000-4000-8000-000000000002', N'69301/กธ/8000011', '77001', 'CMI',  2, 450.0000,   'THB'),
    ('eb000000-0000-4000-8000-000000000008', 'ea000000-0000-4000-8000-000000000003', 'e1000000-0000-4000-8000-000000000002', N'26301/POL/000004', '77001', 'FIRE', 1, 9800.0000,  'THB'),
    -- cart ea…0004 (vcommerce, CheckedOut) — sum 21700.0000
    ('eb000000-0000-4000-8000-000000000009', 'ea000000-0000-4000-8000-000000000004', 'e1000000-0000-4000-8000-000000000002', N'69301/กธ/800012',  '77001', 'VMI',  1, 12800.0000, 'THB'),
    ('eb000000-0000-4000-8000-00000000000a', 'ea000000-0000-4000-8000-000000000004', 'e1000000-0000-4000-8000-000000000002', N'26301/POL/000001', '77001', 'FIRE', 1, 8900.0000,  'THB'),
    -- cart ea…0005 (vsouvenir, Open) — sum 3130.0000
    ('eb000000-0000-4000-8000-00000000000b', 'ea000000-0000-4000-8000-000000000005', 'e1000000-0000-4000-8000-000000000003', N'69301/กธ/910001',  '77001', 'VMI',  5, 390.0000,   'THB'),
    ('eb000000-0000-4000-8000-00000000000c', 'ea000000-0000-4000-8000-000000000005', 'e1000000-0000-4000-8000-000000000003', N'69301/กธ/9100002', '77001', 'CMI',  2, 590.0000,   'THB'),
    -- cart ea…0006 (vsouvenir, Open) — sum 3450.0000
    ('eb000000-0000-4000-8000-00000000000d', 'ea000000-0000-4000-8000-000000000006', 'e1000000-0000-4000-8000-000000000003', N'69301/กธ/8000011', '77001', 'CMI',  4, 450.0000,   'THB'),
    ('eb000000-0000-4000-8000-00000000000e', 'ea000000-0000-4000-8000-000000000006', 'e1000000-0000-4000-8000-000000000003', N'69301/กธ/800012',  '77001', 'VMI',  3, 550.0000,   'THB');

-- shop.CheckoutSessions (REQ-6.2): AmountAmount = SUM(Quantity * UnitPriceAmount) of the bound
-- cart. Both Confirmed rows point at the 2 CheckedOut carts; Started/Abandoned point at Open carts.
-- PaymentChannel/CustomerName/CustomerPhone/CustomerEmail (CheckoutOrderEnrichment migration,
-- purchase-flow-completion REQ-6.1/6.6/6.7) replace the dropped NotificationRecipient column — the
-- merchant picks a channel and the customer's contact is captured when checkout starts, so every row
-- carries them regardless of status (column is NOT NULL). The two Confirmed rows reuse the real
-- contact of the merch.Users row the old NotificationRecipient email pointed at (ec…0001 ->
-- merch.Users e5…0001, ec…0002 -> e5…0005); both are 'CARD' because they feed shop.Orders n=1/n=2
-- below, whose txn.PaymentSessions row is hardcoded Method='card' — a session cannot show a different
-- channel than the payment it settled with. ec…0003 (Started, no order/payment attached) carries the
-- demo variety instead ('PROMPTPAY_QR', vprivilege has it enabled); Abandoned reuses the column's own
-- DEFAULT ('CARD', also vsouvenir's only enabled method). Started/Abandoned both get
-- CustomerContact.Unspecified (name/phone/email = '(ไม่ระบุ)'/''/NULL).
INSERT INTO shop.CheckoutSessions (Id, MerchantId, CartId, Status, CreatedAt, PaymentChannel, CustomerName, CustomerPhone, CustomerEmail, AmountAmount, AmountCurrency)
VALUES
    ('ec000000-0000-4000-8000-000000000001', 'e1000000-0000-4000-8000-000000000001', 'ea000000-0000-4000-8000-000000000002', 1, SYSUTCDATETIME(), 'CARD',         N'สมชาย พริวิเลจ', '0812345001', N'somchai.p@demo.pol.local', 56500.0000, 'THB'),
    ('ec000000-0000-4000-8000-000000000002', 'e1000000-0000-4000-8000-000000000002', 'ea000000-0000-4000-8000-000000000004', 1, SYSUTCDATETIME(), 'CARD',         N'อารยา คอมเมิร์ซ',  '0823456001', N'araya.c@demo.pol.local',   21700.0000, 'THB'),
    ('ec000000-0000-4000-8000-000000000003', 'e1000000-0000-4000-8000-000000000001', 'ea000000-0000-4000-8000-000000000001', 0, SYSUTCDATETIME(), 'PROMPTPAY_QR', N'(ไม่ระบุ)',        '',           NULL,                        23400.0000, 'THB'),
    ('ec000000-0000-4000-8000-000000000004', 'e1000000-0000-4000-8000-000000000003', 'ea000000-0000-4000-8000-000000000005', 2, SYSUTCDATETIME(), 'CARD',         N'(ไม่ระบุ)',        '',           NULL,                         3130.0000, 'THB');

-- shop.Orders (REQ-6.3) + txn.PaymentSessions (REQ-6.4/6.5): generated from a number sequence
-- (GENERATE_SERIES — DB compat level 170 here, SQL Server 2022+ feature) instead of 40+36 hand-typed
-- rows (design §3's own suggested pattern). n=1/n=2 are pinned to the 2 Confirmed checkout sessions
-- above (same merchant, AmountAmount == that checkout's total). CreatedAt spreads back over 90 days
-- (n % 90); MerchantId rotates n % 3; Status cycles n % 8 (~5/8 Paid, ~2/8 AwaitingPayment, ~1/8
-- Cancelled) — 25 Paid / 10 AwaitingPayment / 5 Cancelled out of 40.
DECLARE @OrderSeed TABLE (
    n int NOT NULL PRIMARY KEY,
    Id uniqueidentifier NOT NULL,
    MerchantId uniqueidentifier NOT NULL,
    Status int NOT NULL,
    CreatedAt datetime2 NOT NULL,
    PaidAt datetime2 NULL,
    SummaryToken nvarchar(64) NOT NULL,
    SummaryTokenExpiresAt datetime2 NOT NULL,
    CheckoutSessionId uniqueidentifier NULL,
    AmountAmount decimal(19,4) NOT NULL,
    AmountCurrency char(3) NOT NULL
);

INSERT INTO @OrderSeed (n, Id, MerchantId, Status, CreatedAt, PaidAt, SummaryToken, SummaryTokenExpiresAt, CheckoutSessionId, AmountAmount, AmountCurrency)
SELECT
    g.n,
    CONVERT(uniqueidentifier, CONCAT('ed000000-0000-4000-8000-', RIGHT('000000000000' + CONVERT(varchar(12), g.n), 12))),
    CASE g.n % 3 WHEN 1 THEN 'e1000000-0000-4000-8000-000000000001'
                 WHEN 2 THEN 'e1000000-0000-4000-8000-000000000002'
                 ELSE       'e1000000-0000-4000-8000-000000000003' END,
    CASE WHEN g.n % 8 IN (0,1,2,3,4) THEN 1 WHEN g.n % 8 IN (5,6) THEN 0 ELSE 2 END,
    g.createdAt,
    CASE WHEN g.n % 8 IN (0,1,2,3,4) THEN DATEADD(hour, 2, g.createdAt) END,
    CONCAT(N'demo-ord-', RIGHT('00000' + CONVERT(varchar(5), g.n), 5)),
    DATEADD(day, 30, g.createdAt),
    CASE g.n WHEN 1 THEN 'ec000000-0000-4000-8000-000000000001' WHEN 2 THEN 'ec000000-0000-4000-8000-000000000002' END,
    CASE g.n WHEN 1 THEN 56500.0000 WHEN 2 THEN 21700.0000 ELSE CAST(300 + ((g.n * 733) % 47700) AS decimal(19,4)) END,
    'THB'
FROM (SELECT value AS n, DATEADD(day, -(value % 90), SYSUTCDATETIME()) AS createdAt FROM GENERATE_SERIES(1, 40)) AS g;

-- OrderNo (REQ-7.1) is minted from the same shop.OrderNoSeq sequence and formatted exactly as
-- OrderNoSequence.Format / the CheckoutOrderEnrichment migration's own backfill do — ORD + Buddhist
-- year (2 digits, taken at seed-run time, same as the backfill's YEAR(GETUTCDATE())) + the sequence
-- value padded to 8 — never hand-computed, or it would collide with a value the sequence hands out
-- later. PaymentChannel/CustomerName/CustomerPhone/CustomerEmail (REQ-6.1/6.6/6.7): n=1/n=2 are the
-- two orders a real CheckoutConfirmedConsumer run would have created from checkout sessions ec…0001/
-- ec…0002, so they carry that session's channel and contact verbatim (Order.Create copies both from
-- the notification); every other order predates contact capture and gets CustomerContact.Unspecified.
-- NotificationRecipient is no longer a literal value — it is CustomerContact.NotificationRecipient's
-- own rule (phone, else email, else NULL) applied to whichever contact the row ended up with.
INSERT INTO shop.Orders (Id, MerchantId, PaymentSessionId, CheckoutSessionId, Status, CreatedAt, PaidAt, SummaryToken, SummaryTokenExpiresAt, OrderNo, PaymentChannel, CustomerName, CustomerPhone, CustomerEmail, NotificationRecipient, AmountAmount, AmountCurrency)
SELECT
    s.Id, s.MerchantId, NULL, s.CheckoutSessionId, s.Status, s.CreatedAt, s.PaidAt, s.SummaryToken, s.SummaryTokenExpiresAt,
    CONCAT('ORD', RIGHT(CONVERT(varchar(4), YEAR(GETUTCDATE()) + 543), 2), FORMAT(NEXT VALUE FOR shop.OrderNoSeq, 'D8')),
    cs.PaymentChannel, ISNULL(cs.CustomerName, N'(ไม่ระบุ)'), ISNULL(cs.CustomerPhone, ''), cs.CustomerEmail,
    CASE WHEN ISNULL(cs.CustomerPhone, '') <> '' THEN ISNULL(cs.CustomerPhone, '') WHEN cs.CustomerEmail IS NOT NULL THEN cs.CustomerEmail END,
    s.AmountAmount, s.AmountCurrency
FROM @OrderSeed s
LEFT JOIN shop.CheckoutSessions cs ON cs.Id = s.CheckoutSessionId;

-- txn.PaymentSessions: 1 per order except 4 AwaitingPayment orders (n IN (29,30,37,38)) left with
-- no PSP attempt at all — matches the real flow where an order can exist before payment starts
-- (25 Paid + 5 Cancelled + 6 of the 10 AwaitingPayment = 36). MerchantId/AmountAmount/AmountCurrency
-- always copied from the parent order (REQ-6.4). Every Paid order's session is Status=2/Paid
-- (REQ-6.5). RowVersion is NOT listed — it's a `rowversion` column, SQL Server generates it.
-- Method is always 'card' (captive-payment-alignment REQ-6.5, 2026-07-26): a session must be payable
-- under the rules the code enforces, and every seeded connection enables 'card'. 2C2P now honours all
-- three methods (purchase-flow-completion REQ-6.1 — card/promptpay/installment map to the PGW channel
-- codes CC/QR/IPP); Omise still declares SupportedMethods = { card }, so an Omise session on another
-- method would be refused with a 409 rather than silently routed to a card page.
INSERT INTO txn.PaymentSessions (Id, MerchantId, OrderId, Method, Psp, Status, PspExternalChargeId, RedirectUrl, CreatedAt, UpdatedAt, AmountAmount, AmountCurrency)
SELECT
    CONVERT(uniqueidentifier, CONCAT('ee000000-0000-4000-8000-', RIGHT('000000000000' + CONVERT(varchar(12), s.n), 12))),
    s.MerchantId,
    s.Id,
    N'card',
    0,
    ss.SessionStatus,
    CASE WHEN ss.SessionStatus IN (2,3) THEN CONCAT(N'demo_chrg_', s.n) END,
    CASE WHEN ss.SessionStatus IN (1,2,3) THEN CONCAT(N'https://demo.psp.local/checkout/', s.n) END,
    DATEADD(minute, 5, s.CreatedAt),
    CASE WHEN ss.SessionStatus IN (2,3,4) THEN DATEADD(hour, 1, DATEADD(minute, 5, s.CreatedAt)) ELSE DATEADD(minute, 5, s.CreatedAt) END,
    s.AmountAmount,
    s.AmountCurrency
FROM @OrderSeed s
CROSS APPLY (SELECT CASE
        WHEN s.Status = 1 THEN 2
        WHEN s.Status = 2 THEN CASE (s.n / 8) % 2 WHEN 0 THEN 3 ELSE 4 END
        ELSE CASE s.n % 2 WHEN 0 THEN 0 ELSE 1 END
    END AS SessionStatus) ss
WHERE s.Status IN (1,2) OR (s.Status = 0 AND s.n NOT IN (29,30,37,38));

-- REQ-6.5: every Paid order points back at its (Status=Paid) payment session — no conflicting pair.
UPDATE o
SET o.PaymentSessionId = p.Id
FROM shop.Orders o
JOIN txn.PaymentSessions p ON p.OrderId = o.Id AND p.Status = 2
WHERE o.Id LIKE 'ed000000-%' AND o.Status = 1;

-- Not seeded (REQ-6.6): txn.OutboxMessages, txn.IdempotencyRecords, and every audit/session table
-- — those are runtime side effects, not starting data.

-- shop.OrderItems + shop.OrderItemPolicies (policy-reference-record REQ-5.1/5.2): 4 items on 3
-- EXISTING demo orders above (n=16 vprivilege/Paid, n=8 vcommerce/Paid, n=5 vcommerce/AwaitingPayment)
-- — no new shop.Orders rows needed. Items ef…0001/0002 share one order + the same insured person AND
-- ทะเบียนรถ to cover the "Voluntary + Compulsory, same vehicle" edge case (requirements.md Edge Cases,
-- row 1 vs row 6). Item ef…0004 deliberately gets NO OrderItemPolicies row below — REQ-1.7/4.7's
-- blank-external-column report case (a policy-less item, not a policy row full of nulls).
-- The document snapshot columns (DocumentNo/ProductGroup/DocumentType/PolicyNumber/StartDate/EndDate) are
-- frozen copies of the upstream document each item was bought from (products-external-source-of-truth
-- REQ-4.4) — the ProductId Guid column is gone (REQ-2.2). Every DocumentNo is a real sim row. The two
-- documents that sit on Paid orders (69301/อท/91000081 on n=16, 26301/POL/000009 on n=8) are deliberately
-- NOT reused by any sellable cart above, so the double-sell gate (REQ-5.1) never trips a demo checkout;
-- 69301/กธ/910001 on the AwaitingPayment order n=5 is fine to share with the carts because an unpaid order
-- does not mark a document sold (REQ-5.11).
INSERT INTO shop.OrderItems (Id, OrderId, MerchantId, Quantity, DocumentNo, ProductGroup, DocumentType, PolicyNumber, StartDate, EndDate, InsuredFirstName, InsuredLastName, InsuredIdNumber, InsuredDateOfBirth, UnitPriceAmount, UnitPriceCurrency)
VALUES
    -- Motor, ภาคสมัครใจ (Voluntary) — order ed…0016 (vprivilege, Paid); CMI ENDORSEMENT 69301/อท/91000081
    ('ef000000-0000-4000-8000-000000000001', 'ed000000-0000-4000-8000-000000000016', 'e1000000-0000-4000-8000-000000000001', 1, N'69301/อท/91000081', 'CMI', 'ENDORSEMENT', 'POL-2026-VP-000123', '2026-01-01', '2027-01-01', N'สมชาย', N'ใจดี', N'1103700123456', '1985-03-15', 15900.0000, 'THB'),
    -- Motor, ภาคบังคับ/พ.ร.บ. (Compulsory) — SAME order + SAME insured person + vehicle as above
    ('ef000000-0000-4000-8000-000000000002', 'ed000000-0000-4000-8000-000000000016', 'e1000000-0000-4000-8000-000000000001', 1, N'69301/อท/91000081', 'CMI', 'ENDORSEMENT', NULL, '2026-01-01', '2027-01-01', N'สมชาย', N'ใจดี', N'1103700123456', '1985-03-15', 645.2100, 'THB'),
    -- Non-motor (fire) — order ed…0008 (vcommerce, Paid); no InsuredObjectReference on its policy
    -- below (REQ-1.8 — field is generic to every insurance type, not just Motor); FIRE POLICY 26301/POL/000009
    ('ef000000-0000-4000-8000-000000000003', 'ed000000-0000-4000-8000-000000000008', 'e1000000-0000-4000-8000-000000000002', 1, N'26301/POL/000009', 'FIRE', 'POLICY', 'POL-2026-VC-000789', '2026-02-01', '2027-02-01', N'อารยา', N'รุ่งเรือง', N'1209900456789', '1990-07-22', 9800.0000, 'THB'),
    -- No policy data entered yet — order ed…0005 (vcommerce, AwaitingPayment); VMI POLICY 69301/กธ/910001
    ('ef000000-0000-4000-8000-000000000004', 'ed000000-0000-4000-8000-000000000005', 'e1000000-0000-4000-8000-000000000002', 1, N'69301/กธ/910001', 'VMI', 'POLICY', NULL, NULL, NULL, N'พิชิต', N'แสงทอง', N'1509900112233', '1978-11-02', 650.0000, 'THB');

INSERT INTO shop.OrderItemPolicies (Id, OrderItemId, MerchantId, InsuranceCategory, ReferenceNumberType, ReferenceNumber, EndorsementNumber, RenewalReminderNumber, InsuredObjectReference, NetPremiumAmount, NetPremiumCurrency, GrossPremiumAmount, GrossPremiumCurrency, PremiumRemittanceStatus, DeductedAt, CreatedAt, UpdatedAt)
VALUES
    -- Voluntary + PolicyNumber + Endorsement, premium ตัดชำระแล้ว (Deducted, past DeductedAt)
    ('f1000000-0000-4000-8000-000000000001', 'ef000000-0000-4000-8000-000000000001', 'e1000000-0000-4000-8000-000000000001', 0, 0, N'POL-2026-VP-000123', N'END-2026-0007', NULL, N'กข-1234 กรุงเทพมหานคร', 15000.0000, 'THB', 15900.0000, 'THB', 1, '2026-07-15', SYSUTCDATETIME(), SYSUTCDATETIME()),
    -- Compulsory + NotificationNumber, premium ยังไม่ตัดชำระ (NotApplicable, no DeductedAt)
    ('f1000000-0000-4000-8000-000000000002', 'ef000000-0000-4000-8000-000000000002', 'e1000000-0000-4000-8000-000000000001', 1, 1, N'NTF-2026-VP-000456', NULL, NULL, N'กข-1234 กรุงเทพมหานคร', 600.0000, 'THB', 645.2100, 'THB', 0, NULL, SYSUTCDATETIME(), SYSUTCDATETIME()),
    -- Voluntary + PolicyNumber + RenewalReminder, Net == Gross (valid — REQ-3.7 allows equal), Deducted
    ('f1000000-0000-4000-8000-000000000003', 'ef000000-0000-4000-8000-000000000003', 'e1000000-0000-4000-8000-000000000002', 0, 0, N'POL-2026-VC-000789', NULL, N'REM-2026-VC-045', NULL, 18500.0000, 'THB', 18500.0000, 'THB', 1, '2026-06-30', SYSUTCDATETIME(), SYSUTCDATETIME());

-- ============================================================================
-- (จ) Self-check: every table already seeded must have its expected demo row
-- count, or the whole seed is incomplete. T1 asserts only admin.Users; T2-T4
-- append their own tables to @counts as they add INSERTs.                REQ-1.6
-- ============================================================================
DECLARE @counts TABLE (TableName nvarchar(64) NOT NULL, Rows int NOT NULL);

INSERT INTO @counts (TableName, Rows) VALUES
    (N'admin.Users', (SELECT COUNT(*) FROM admin.Users WHERE Id LIKE 'e2000000-%')),
    (N'merch.Merchants', (SELECT COUNT(*) FROM merch.Merchants WHERE Id LIKE 'e1000000-%')),
    (N'txn.PspConnections', (SELECT COUNT(*) FROM txn.PspConnections WHERE Id LIKE 'e8000000-%')),
    (N'admin.MerchantAccess', (SELECT COUNT(*) FROM admin.MerchantAccess WHERE Id LIKE 'e3000000-%')),
    (N'admin.RoleAssignments', (SELECT COUNT(*) FROM admin.RoleAssignments WHERE Id LIKE 'e4000000-%')),
    (N'merch.Users', (SELECT COUNT(*) FROM merch.Users WHERE Id LIKE 'e5000000-%')),
    (N'merch.ExternalLogins', (SELECT COUNT(*) FROM merch.ExternalLogins WHERE Id LIKE 'e6000000-%')),
    (N'merch.RoleAssignments', (SELECT COUNT(*) FROM merch.RoleAssignments WHERE Id LIKE 'e7000000-%')),
    (N'shop.Carts', (SELECT COUNT(*) FROM shop.Carts WHERE Id LIKE 'ea000000-%')),
    (N'shop.CartItems', (SELECT COUNT(*) FROM shop.CartItems WHERE Id LIKE 'eb000000-%')),
    (N'shop.CheckoutSessions', (SELECT COUNT(*) FROM shop.CheckoutSessions WHERE Id LIKE 'ec000000-%')),
    (N'shop.Orders', (SELECT COUNT(*) FROM shop.Orders WHERE Id LIKE 'ed000000-%')),
    (N'txn.PaymentSessions', (SELECT COUNT(*) FROM txn.PaymentSessions WHERE Id LIKE 'ee000000-%')),
    (N'shop.OrderItems', (SELECT COUNT(*) FROM shop.OrderItems WHERE Id LIKE 'ef000000-%')),
    (N'shop.OrderItemPolicies', (SELECT COUNT(*) FROM shop.OrderItemPolicies WHERE Id LIKE 'f1000000-%'));

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

-- products-external-source-of-truth REQ-6.9: shop.Products is gone, so the old "every seeded product
-- carries a complete document / falls inside the search window" checks no longer apply — there is
-- nothing to count in a dropped table. The catalogue is validated live against the upstream now; what
-- this seed can still guarantee is that the identifier that REPLACED ProductId is populated on every
-- buy-path row it writes. A blank DocumentNo (or a cart row missing SaleCode/ProductGroup) would be a
-- row the live buy path could never resolve, so count those instead and fail loudly if any exist.
DECLARE @blankIdentifier int = (
    SELECT
        (SELECT COUNT(*) FROM shop.CartItems
         WHERE Id LIKE 'eb000000-%'
           AND (DocumentNo IS NULL OR LTRIM(RTRIM(DocumentNo)) = N''
                OR SaleCode IS NULL OR LTRIM(RTRIM(SaleCode)) = ''
                OR ProductGroup IS NULL OR LTRIM(RTRIM(ProductGroup)) = ''))
      + (SELECT COUNT(*) FROM shop.OrderItems
         WHERE Id LIKE 'ef000000-%'
           AND (DocumentNo IS NULL OR LTRIM(RTRIM(DocumentNo)) = N''
                OR ProductGroup IS NULL OR LTRIM(RTRIM(ProductGroup)) = '')));
IF @blankIdentifier > 0
    THROW 51000, N'seed-demo: cart/order rows with a blank DocumentNo/SaleCode/ProductGroup (the identifier that replaced ProductId).', 1;

COMMIT;
GO

PRINT N'seed-demo: OK.';
GO
