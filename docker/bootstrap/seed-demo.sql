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
INSERT INTO merch.Users (Id, Subject, Email, Status, MerchantId, CreatedAt, DisplayName, FirstName, LastName, PersonType, IdNumber, ProducerCode, LicenseNumber, Phone)
VALUES
    -- vprivilege (merchant e1...0001)
    ('e5000000-0000-4000-8000-000000000001', N'demo-mch-1', N'somchai.p@demo.pol.local', 1, 'e1000000-0000-4000-8000-000000000001', SYSUTCDATETIME(), N'สมชาย พริวิเลจ', N'สมชาย', N'พริวิเลจ', 0, N'1100200300401', N'PRD-VP-001', N'LIC-2024-00101', N'0812345001'),
    ('e5000000-0000-4000-8000-000000000002', N'demo-mch-2', N'vprivilege.dist@demo.pol.local', 1, 'e1000000-0000-4000-8000-000000000001', SYSUTCDATETIME(), N'บริษัท วีพริวิเลจ ตัวแทนจำหน่าย จำกัด', N'-', N'-', 1, N'0105561000045', N'PRD-VP-002', N'LIC-2024-00102', N'0812345002'),
    ('e5000000-0000-4000-8000-000000000003', N'demo-mch-3', N'wanida.k@demo.pol.local', 0, 'e1000000-0000-4000-8000-000000000001', SYSUTCDATETIME(), N'วนิดา คงพริวิเลจ', N'วนิดา', N'คงพริวิเลจ', 0, N'1100200300402', NULL, NULL, N'0812345003'),
    ('e5000000-0000-4000-8000-000000000004', N'demo-mch-4', N'pichit.s@demo.pol.local', 2, 'e1000000-0000-4000-8000-000000000001', SYSUTCDATETIME(), N'พิชิต แสงพริวิเลจ', N'พิชิต', N'แสงพริวิเลจ', 0, N'1100200300403', NULL, NULL, N'0812345004'),
    -- vcommerce (merchant e1...0002)
    ('e5000000-0000-4000-8000-000000000005', N'demo-mch-5', N'araya.c@demo.pol.local', 1, 'e1000000-0000-4000-8000-000000000002', SYSUTCDATETIME(), N'อารยา คอมเมิร์ซ', N'อารยา', N'คอมเมิร์ซ', 0, N'1100200300404', N'PRD-VC-001', N'LIC-2024-00201', N'0823456001'),
    ('e5000000-0000-4000-8000-000000000006', N'demo-mch-6', N'vcommerce.hq@demo.pol.local', 1, 'e1000000-0000-4000-8000-000000000002', SYSUTCDATETIME(), N'บริษัท วีคอมเมิร์ซ โฮลดิ้ง จำกัด', N'-', N'-', 1, N'0105561000053', N'PRD-VC-002', N'LIC-2024-00202', N'0823456002'),
    ('e5000000-0000-4000-8000-000000000007', N'demo-mch-7', N'natthapong.r@demo.pol.local', 3, 'e1000000-0000-4000-8000-000000000002', SYSUTCDATETIME(), N'ณัฐพงศ์ รุ่งคอมเมิร์ซ', N'ณัฐพงศ์', N'รุ่งคอมเมิร์ซ', 0, N'1100200300405', N'PRD-VC-003', N'LIC-2024-00203', N'0823456003'),
    ('e5000000-0000-4000-8000-000000000008', N'demo-mch-8', N'suda.m@demo.pol.local', 0, 'e1000000-0000-4000-8000-000000000002', SYSUTCDATETIME(), N'สุดา มั่นคอมเมิร์ซ', N'สุดา', N'มั่นคอมเมิร์ซ', 0, N'1100200300406', NULL, NULL, N'0823456004'),
    -- vsouvenir (merchant e1...0003)
    ('e5000000-0000-4000-8000-000000000009', N'demo-mch-9', N'kanya.s@demo.pol.local', 1, 'e1000000-0000-4000-8000-000000000003', SYSUTCDATETIME(), N'กัญญา ซูวีเนียร์', N'กัญญา', N'ซูวีเนียร์', 0, N'1100200300407', N'PRD-VS-001', N'LIC-2024-00301', N'0834567001'),
    ('e5000000-0000-4000-8000-00000000000a', N'demo-mch-10', N'vsouvenir.shop@demo.pol.local', 1, 'e1000000-0000-4000-8000-000000000003', SYSUTCDATETIME(), N'บริษัท วีซูวีเนียร์ ช็อป จำกัด', N'-', N'-', 1, N'0105561000061', N'PRD-VS-002', N'LIC-2024-00302', N'0834567002'),
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

-- shop.Products (REQ-5.4): 100 rows total, 34/33/33 across the three merchants. A Product is now a
-- sellable insurance document (VCentralPay SP guide) - the first 24 (ids ...01-...18 hex) are the
-- hand-written flagship documents below; shop.CartItems references them by id, so their ids are
-- load-bearing and must not move. The remaining 76 (ids ...19-...64 hex) are generated right after.
-- 1 already-sold document per hand-written block plus every 7th generated row carries
-- PaymentStatus = 'PAID' + a PaidDate, so both sides of the sellability gate are represented:
-- cart add-item / checkout accept UNPAID only (products-sp-53-alignment REQ-2.1/2.4 — this used to
-- be the IsActive = 0 axis, which no longer exists as a column).
-- DocumentNo is unique per merchant (IX_Products_MerchantId_DocumentNo). Optional document fields
-- stay NULL here - the EF migration seed ('e6000000-%') carries the fully-populated samples.
-- TotalPremium is decimal(19,2) — Product.Create rejects a 3rd decimal place, it does not round.
INSERT INTO shop.Products (Id, MerchantId, ProductGroup, DocumentType, DocumentNo, SaleCode,
                           ShowName, TotalPremium, PaymentStatus, PaidDate)
VALUES
    -- vprivilege
    ('e9000000-0000-4000-8000-000000000001', 'e1000000-0000-4000-8000-000000000001', 'VMI',  'POLICY',      N'00098-69100/กธ/900001-10', '00098', N'ผู้เอาประกันตัวอย่าง 01', 1200.00,  'UNPAID', NULL),
    ('e9000000-0000-4000-8000-000000000002', 'e1000000-0000-4000-8000-000000000001', 'CMI',  'POLICY',      N'00098-69100/กธ/900002-10', '00098', N'ผู้เอาประกันตัวอย่าง 02', 1850.00,  'UNPAID', NULL),
    ('e9000000-0000-4000-8000-000000000003', 'e1000000-0000-4000-8000-000000000001', 'FIRE', 'POLICY',      N'S001-69100/อค/900003',     'S001',  N'ผู้เอาประกันตัวอย่าง 03', 18500.00, 'UNPAID', NULL),
    ('e9000000-0000-4000-8000-000000000004', 'e1000000-0000-4000-8000-000000000001', 'MISC', 'APPLICATION', N'S001-69100/บต/900004',     'S001',  N'ผู้เอาประกันตัวอย่าง 04', 32000.00, 'UNPAID', NULL),
    ('e9000000-0000-4000-8000-000000000005', 'e1000000-0000-4000-8000-000000000001', 'VMI',  'RENEWAL',     N'00098-68100/ตอ/900005-10', '00098', N'ผู้เอาประกันตัวอย่าง 05', 24500.00, 'UNPAID', NULL),
    ('e9000000-0000-4000-8000-000000000006', 'e1000000-0000-4000-8000-000000000001', 'CMI',  'ENDORSEMENT', N'69100/สล/900006',          '00098', N'ผู้เอาประกันตัวอย่าง 06', 15900.00, 'UNPAID', NULL),
    ('e9000000-0000-4000-8000-000000000007', 'e1000000-0000-4000-8000-000000000001', 'FIRE', 'RENEWAL',     N'S001-68100/อค/900007',     'S001',  N'ผู้เอาประกันตัวอย่าง 07', 3500.00,  'UNPAID', NULL),
    ('e9000000-0000-4000-8000-000000000008', 'e1000000-0000-4000-8000-000000000001', 'MISC', 'POLICY',      N'S001-69100/บต/900008',     'S001',  N'ผู้เอาประกันตัวอย่าง 08', 350.00,   'PAID',   DATEADD(day, -7, SYSUTCDATETIME())),
    -- vcommerce
    ('e9000000-0000-4000-8000-000000000009', 'e1000000-0000-4000-8000-000000000002', 'VMI',  'POLICY',      N'00098-69100/กธ/900009-10', '00098', N'ผู้เอาประกันตัวอย่าง 09', 650.00,   'UNPAID', NULL),
    ('e9000000-0000-4000-8000-00000000000a', 'e1000000-0000-4000-8000-000000000002', 'CMI',  'POLICY',      N'00098-69100/กธ/900010-10', '00098', N'ผู้เอาประกันตัวอย่าง 10', 450.00,   'UNPAID', NULL),
    ('e9000000-0000-4000-8000-00000000000b', 'e1000000-0000-4000-8000-000000000002', 'FIRE', 'POLICY',      N'S001-69100/อค/900011',     'S001',  N'ผู้เอาประกันตัวอย่าง 11', 9800.00,  'UNPAID', NULL),
    ('e9000000-0000-4000-8000-00000000000c', 'e1000000-0000-4000-8000-000000000002', 'MISC', 'APPLICATION', N'S001-69100/บต/900012',     'S001',  N'ผู้เอาประกันตัวอย่าง 12', 12800.00, 'UNPAID', NULL),
    ('e9000000-0000-4000-8000-00000000000d', 'e1000000-0000-4000-8000-000000000002', 'VMI',  'RENEWAL',     N'00098-68100/ตอ/900013-10', '00098', N'ผู้เอาประกันตัวอย่าง 13', 8900.00,  'UNPAID', NULL),
    ('e9000000-0000-4000-8000-00000000000e', 'e1000000-0000-4000-8000-000000000002', 'CMI',  'ENDORSEMENT', N'69100/สล/900014',          '00098', N'ผู้เอาประกันตัวอย่าง 14', 6200.00,  'UNPAID', NULL),
    ('e9000000-0000-4000-8000-00000000000f', 'e1000000-0000-4000-8000-000000000002', 'FIRE', 'RENEWAL',     N'S001-68100/อค/900015',     'S001',  N'ผู้เอาประกันตัวอย่าง 15', 4100.00,  'UNPAID', NULL),
    ('e9000000-0000-4000-8000-000000000010', 'e1000000-0000-4000-8000-000000000002', 'MISC', 'POLICY',      N'S001-69100/บต/900016',     'S001',  N'ผู้เอาประกันตัวอย่าง 16', 990.00,   'PAID',   DATEADD(day, -5, SYSUTCDATETIME())),
    -- vsouvenir
    ('e9000000-0000-4000-8000-000000000011', 'e1000000-0000-4000-8000-000000000003', 'VMI',  'POLICY',      N'00098-69100/กธ/900017-10', '00098', N'ผู้เอาประกันตัวอย่าง 17', 390.00,   'UNPAID', NULL),
    ('e9000000-0000-4000-8000-000000000012', 'e1000000-0000-4000-8000-000000000003', 'CMI',  'POLICY',      N'00098-69100/กธ/900018-10', '00098', N'ผู้เอาประกันตัวอย่าง 18', 590.00,   'UNPAID', NULL),
    ('e9000000-0000-4000-8000-000000000013', 'e1000000-0000-4000-8000-000000000003', 'FIRE', 'POLICY',      N'S001-69100/อค/900019',     'S001',  N'ผู้เอาประกันตัวอย่าง 19', 450.00,   'UNPAID', NULL),
    ('e9000000-0000-4000-8000-000000000014', 'e1000000-0000-4000-8000-000000000003', 'MISC', 'APPLICATION', N'S001-69100/บต/900020',     'S001',  N'ผู้เอาประกันตัวอย่าง 20', 550.00,   'UNPAID', NULL),
    ('e9000000-0000-4000-8000-000000000015', 'e1000000-0000-4000-8000-000000000003', 'VMI',  'RENEWAL',     N'00098-68100/ตอ/900021-10', '00098', N'ผู้เอาประกันตัวอย่าง 21', 480.00,   'UNPAID', NULL),
    ('e9000000-0000-4000-8000-000000000016', 'e1000000-0000-4000-8000-000000000003', 'CMI',  'ENDORSEMENT', N'69100/สล/900022',          '00098', N'ผู้เอาประกันตัวอย่าง 22', 720.00,   'UNPAID', NULL),
    ('e9000000-0000-4000-8000-000000000017', 'e1000000-0000-4000-8000-000000000003', 'FIRE', 'RENEWAL',     N'S001-68100/อค/900023',     'S001',  N'ผู้เอาประกันตัวอย่าง 23', 2100.00,  'UNPAID', NULL),
    ('e9000000-0000-4000-8000-000000000018', 'e1000000-0000-4000-8000-000000000003', 'MISC', 'POLICY',      N'S001-69100/บต/900024',     'S001',  N'ผู้เอาประกันตัวอย่าง 24', 48000.00, 'PAID',   DATEADD(day, -3, SYSUTCDATETIME()));

-- The remaining 76 documents (ids ...19-...64 hex), taking the catalogue to 100. Deterministic
-- throughout - the id is the row number rendered as hex (offset by the 24 above) and DocumentNo
-- embeds the same sequence, so a re-run reproduces the exact same 76 rows and the
-- DELETE ... LIKE 'e9000000-%' in step (ค) still reclaims every one of them. ProductGroup rotates
-- through all four values; DocumentType rotates POLICY/RENEWAL/ENDORSEMENT only (CMI never pairs
-- with APPLICATION per the SP guide's support matrix, and this generator keeps the invariant by
-- simply never emitting APPLICATION).
;WITH seq AS (
    SELECT TOP (76) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS Seq
    FROM sys.all_objects
)
INSERT INTO shop.Products (Id, MerchantId, ProductGroup, DocumentType, DocumentNo, SaleCode,
                           ShowName, TotalPremium, PaymentStatus, PaidDate)
SELECT
    CONVERT(uniqueidentifier, 'e9000000-0000-4000-8000-'
        + RIGHT('000000000000'
            + LOWER(CONVERT(varchar(20), CONVERT(varbinary(4), 24 + Seq), 2)), 12)),
    CONVERT(uniqueidentifier, 'e1000000-0000-4000-8000-'
        + RIGHT('000000000000'
            + CONVERT(varchar(12), CASE WHEN Seq <= 26 THEN 1 WHEN Seq <= 51 THEN 2 ELSE 3 END), 12)),
    CASE Seq % 4 WHEN 0 THEN 'CMI' WHEN 1 THEN 'VMI' WHEN 2 THEN 'FIRE' ELSE 'MISC' END,
    CASE Seq % 3 WHEN 0 THEN 'POLICY' WHEN 1 THEN 'RENEWAL' ELSE 'ENDORSEMENT' END,
    CONCAT(N'00098-69100/กธ/', RIGHT('000000' + CONVERT(varchar(6), 910000 + Seq), 6), N'-10'),
    '00098',
    CONCAT(N'ผู้เอาประกันตัวอย่าง ', 24 + Seq),
    CAST(500 + Seq * 137.25 AS decimal(19,2)),
    CASE WHEN Seq % 7 = 0 THEN 'PAID' ELSE 'UNPAID' END,
    CASE WHEN Seq % 7 = 0 THEN DATEADD(day, -Seq, SYSUTCDATETIME()) END
FROM seq;

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

-- shop.CartItems (REQ-6.1): ProductId always belongs to the same merchant as the parent cart.
-- MerchantId is denormalized onto this table (rls-to-query-filter task 8 — the EF global query
-- filter reads it directly, no join through CartId); must match the parent Cart's MerchantId, a
-- cross-merchant value would still be a data bug (checked by verify query below).
INSERT INTO shop.CartItems (Id, CartId, MerchantId, ProductId, Quantity, UnitPriceAmount, UnitPriceCurrency)
VALUES
    -- cart ea…0001 (vprivilege, Open) — sum 23400.0000
    ('eb000000-0000-4000-8000-000000000001', 'ea000000-0000-4000-8000-000000000001', 'e1000000-0000-4000-8000-000000000001', 'e9000000-0000-4000-8000-000000000001', 1, 1200.0000,  'THB'),
    ('eb000000-0000-4000-8000-000000000002', 'ea000000-0000-4000-8000-000000000001', 'e1000000-0000-4000-8000-000000000001', 'e9000000-0000-4000-8000-000000000002', 2, 1850.0000,  'THB'),
    ('eb000000-0000-4000-8000-000000000003', 'ea000000-0000-4000-8000-000000000001', 'e1000000-0000-4000-8000-000000000001', 'e9000000-0000-4000-8000-000000000003', 1, 18500.0000, 'THB'),
    -- cart ea…0002 (vprivilege, CheckedOut) — sum 56500.0000
    ('eb000000-0000-4000-8000-000000000004', 'ea000000-0000-4000-8000-000000000002', 'e1000000-0000-4000-8000-000000000001', 'e9000000-0000-4000-8000-000000000004', 1, 32000.0000, 'THB'),
    ('eb000000-0000-4000-8000-000000000005', 'ea000000-0000-4000-8000-000000000002', 'e1000000-0000-4000-8000-000000000001', 'e9000000-0000-4000-8000-000000000005', 1, 24500.0000, 'THB'),
    -- cart ea…0003 (vcommerce, Open) — sum 12650.0000
    ('eb000000-0000-4000-8000-000000000006', 'ea000000-0000-4000-8000-000000000003', 'e1000000-0000-4000-8000-000000000002', 'e9000000-0000-4000-8000-000000000009', 3, 650.0000,   'THB'),
    ('eb000000-0000-4000-8000-000000000007', 'ea000000-0000-4000-8000-000000000003', 'e1000000-0000-4000-8000-000000000002', 'e9000000-0000-4000-8000-00000000000a', 2, 450.0000,   'THB'),
    ('eb000000-0000-4000-8000-000000000008', 'ea000000-0000-4000-8000-000000000003', 'e1000000-0000-4000-8000-000000000002', 'e9000000-0000-4000-8000-00000000000b', 1, 9800.0000,  'THB'),
    -- cart ea…0004 (vcommerce, CheckedOut) — sum 21700.0000
    ('eb000000-0000-4000-8000-000000000009', 'ea000000-0000-4000-8000-000000000004', 'e1000000-0000-4000-8000-000000000002', 'e9000000-0000-4000-8000-00000000000c', 1, 12800.0000, 'THB'),
    ('eb000000-0000-4000-8000-00000000000a', 'ea000000-0000-4000-8000-000000000004', 'e1000000-0000-4000-8000-000000000002', 'e9000000-0000-4000-8000-00000000000d', 1, 8900.0000,  'THB'),
    -- cart ea…0005 (vsouvenir, Open) — sum 3130.0000
    ('eb000000-0000-4000-8000-00000000000b', 'ea000000-0000-4000-8000-000000000005', 'e1000000-0000-4000-8000-000000000003', 'e9000000-0000-4000-8000-000000000011', 5, 390.0000,   'THB'),
    ('eb000000-0000-4000-8000-00000000000c', 'ea000000-0000-4000-8000-000000000005', 'e1000000-0000-4000-8000-000000000003', 'e9000000-0000-4000-8000-000000000012', 2, 590.0000,   'THB'),
    -- cart ea…0006 (vsouvenir, Open) — sum 3450.0000
    ('eb000000-0000-4000-8000-00000000000d', 'ea000000-0000-4000-8000-000000000006', 'e1000000-0000-4000-8000-000000000003', 'e9000000-0000-4000-8000-000000000013', 4, 450.0000,   'THB'),
    ('eb000000-0000-4000-8000-00000000000e', 'ea000000-0000-4000-8000-000000000006', 'e1000000-0000-4000-8000-000000000003', 'e9000000-0000-4000-8000-000000000014', 3, 550.0000,   'THB');

-- shop.CheckoutSessions (REQ-6.2): AmountAmount = SUM(Quantity * UnitPriceAmount) of the bound
-- cart. Both Confirmed rows point at the 2 CheckedOut carts; Started/Abandoned point at Open carts.
INSERT INTO shop.CheckoutSessions (Id, MerchantId, CartId, Status, CreatedAt, NotificationRecipient, AmountAmount, AmountCurrency)
VALUES
    ('ec000000-0000-4000-8000-000000000001', 'e1000000-0000-4000-8000-000000000001', 'ea000000-0000-4000-8000-000000000002', 1, SYSUTCDATETIME(), N'somchai.p@demo.pol.local', 56500.0000, 'THB'),
    ('ec000000-0000-4000-8000-000000000002', 'e1000000-0000-4000-8000-000000000002', 'ea000000-0000-4000-8000-000000000004', 1, SYSUTCDATETIME(), N'araya.c@demo.pol.local',   21700.0000, 'THB'),
    ('ec000000-0000-4000-8000-000000000003', 'e1000000-0000-4000-8000-000000000001', 'ea000000-0000-4000-8000-000000000001', 0, SYSUTCDATETIME(), NULL,                        23400.0000, 'THB'),
    ('ec000000-0000-4000-8000-000000000004', 'e1000000-0000-4000-8000-000000000003', 'ea000000-0000-4000-8000-000000000005', 2, SYSUTCDATETIME(), NULL,                         3130.0000, 'THB');

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

INSERT INTO shop.Orders (Id, MerchantId, PaymentSessionId, CheckoutSessionId, Status, CreatedAt, PaidAt, SummaryToken, SummaryTokenExpiresAt, NotificationRecipient, AmountAmount, AmountCurrency)
SELECT Id, MerchantId, NULL, CheckoutSessionId, Status, CreatedAt, PaidAt, SummaryToken, SummaryTokenExpiresAt, NULL, AmountAmount, AmountCurrency
FROM @OrderSeed;

-- txn.PaymentSessions: 1 per order except 4 AwaitingPayment orders (n IN (29,30,37,38)) left with
-- no PSP attempt at all — matches the real flow where an order can exist before payment starts
-- (25 Paid + 5 Cancelled + 6 of the 10 AwaitingPayment = 36). MerchantId/AmountAmount/AmountCurrency
-- always copied from the parent order (REQ-6.4). Every Paid order's session is Status=2/Paid
-- (REQ-6.5). RowVersion is NOT listed — it's a `rowversion` column, SQL Server generates it.
-- Method is always 'card' (captive-payment-alignment REQ-6.5, 2026-07-26): a session must be
-- payable under the rules the code now enforces, and 'card' is the only method the adapters honour
-- today (2C2P and Omise both declare SupportedMethods = { card }). The seeded connections above
-- deliberately still enable promptpay/installment — that is the commercial arrangement, and
-- create-session refuses those methods with a 409 rather than silently routing them to a card page.
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
-- The document snapshot columns (DocumentNo/ProductGroup/DocumentType/PolicyNumber/StartDate/EndDate)
-- are frozen copies of the shop.Products row each item references, taken at purchase time.
INSERT INTO shop.OrderItems (Id, OrderId, MerchantId, ProductId, Quantity, DocumentNo, ProductGroup, DocumentType, PolicyNumber, StartDate, EndDate, InsuredFirstName, InsuredLastName, InsuredIdNumber, InsuredDateOfBirth, UnitPriceAmount, UnitPriceCurrency)
VALUES
    -- Motor, ภาคสมัครใจ (Voluntary) — order ed…0016 (vprivilege, Paid); product e9…0006
    ('ef000000-0000-4000-8000-000000000001', 'ed000000-0000-4000-8000-000000000016', 'e1000000-0000-4000-8000-000000000001', 'e9000000-0000-4000-8000-000000000006', 1, N'69100/สล/900006', 'CMI', 'ENDORSEMENT', 'POL-2026-VP-000123', '2026-01-01', '2027-01-01', N'สมชาย', N'ใจดี', N'1103700123456', '1985-03-15', 15900.0000, 'THB'),
    -- Motor, ภาคบังคับ/พ.ร.บ. (Compulsory) — SAME order + SAME insured person + vehicle as above
    ('ef000000-0000-4000-8000-000000000002', 'ed000000-0000-4000-8000-000000000016', 'e1000000-0000-4000-8000-000000000001', 'e9000000-0000-4000-8000-000000000006', 1, N'69100/สล/900006', 'CMI', 'ENDORSEMENT', NULL, '2026-01-01', '2027-01-01', N'สมชาย', N'ใจดี', N'1103700123456', '1985-03-15', 645.2100, 'THB'),
    -- Non-motor (health) — order ed…0008 (vcommerce, Paid); no InsuredObjectReference on its policy
    -- below (REQ-1.8 — field is generic to every insurance type, not just Motor); product e9…000b
    ('ef000000-0000-4000-8000-000000000003', 'ed000000-0000-4000-8000-000000000008', 'e1000000-0000-4000-8000-000000000002', 'e9000000-0000-4000-8000-00000000000b', 1, N'S001-69100/อค/900011', 'FIRE', 'POLICY', 'POL-2026-VC-000789', '2026-02-01', '2027-02-01', N'อารยา', N'รุ่งเรือง', N'1209900456789', '1990-07-22', 9800.0000, 'THB'),
    -- No policy data entered yet — order ed…0005 (vcommerce, AwaitingPayment); product e9…0009
    ('ef000000-0000-4000-8000-000000000004', 'ed000000-0000-4000-8000-000000000005', 'e1000000-0000-4000-8000-000000000002', 'e9000000-0000-4000-8000-000000000009', 1, N'00098-69100/กธ/900009-10', 'VMI', 'POLICY', NULL, NULL, NULL, N'พิชิต', N'แสงทอง', N'1509900112233', '1978-11-02', 650.0000, 'THB');

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
    (N'shop.Products', (SELECT COUNT(*) FROM shop.Products WHERE Id LIKE 'e9000000-%')),
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

COMMIT;
GO

PRINT N'seed-demo: OK.';
GO
