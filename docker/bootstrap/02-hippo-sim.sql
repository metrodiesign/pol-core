-- pol-core simulated upstream database hippodb (idempotent). Runs as sa, on its OWN SQL Server
-- instance (external-sim-separate-containers) — 01-principals.sql does NOT run here, so this
-- script creates its OWN login, `hippo_app` (sim-db-separate-logins: an upstream we do not own would
-- never hand us the same credential as VCentralPay's pol_app, so this side gets its own principal and
-- its own password). Independent of the EF migration chain — hippodb stands in
-- for a system we do NOT own, so it must never enter PolDbContext's lineage. Contains NO secrets;
-- takes ONE sqlcmd variable (this instance's own principal password — deliberately NOT named
-- POL_APP_PASSWORD, so a caller that forgot to update fails loudly on an undefined sqlcmd variable
-- instead of quietly reusing the core credential):
--   sqlcmd -S <hippo-server> -U sa -P <pw> -N -b -v HIPPO_APP_PASSWORD=<pw> -i 02-hippo-sim.sql
-- `-b` = sqlcmd exits non-zero when the self-checks at the bottom THROW.
-- Spec: .ai/specs/external-sim-separate-containers/{requirements,design}.md (topology, REQ-1, REQ-2).
-- Spec: .ai/specs/products-sp-gateway/{requirements,design}.md (REQ-1, REQ-2, REQ-3 — SP contract).
-- Spec: .ai/specs/external-sim-documentno-format/{requirements,design}.md (DocumentNo layout, REQ-1-REQ-9).
-- Spec: .ai/specs/external-sim-shared-agent-network/{requirements,design}.md (shared 6-agent/broker/
--   branch roster, REQ-1-REQ-8).
-- Contract: docs/reference/vcentralpay-sp-quick-reference.pdf v1.0 (§1-§6).
--
-- WHAT THIS SIMULATES
--   hippodb <- motordb on server hippo -> dbo.usp_Motor_SearchDocument (CMI | VMI)
-- The simulated name deliberately differs from the real catalogue name so nobody mistakes one for
-- the other; cutover to the real upstream changes a connection string only (Server + InitialCatalog),
-- never code — the SP name and the output contract are identical. mammothdb (the Non-Motor side)
-- lives in 03-mammoth-sim.sql on its own instance — see that file's header for its half of this note.
--
-- AGENT / BROKER / BRANCH NETWORK (external-sim-shared-agent-network)
--   hippodb and mammothdb represent ONE insurance company's shared sales network, not two
--   independent ones: the same 6-agent/broker/branch master roster (SaleCode 77001-77006) resolves
--   to identical SaleFullName/BrokerCode/BrokerName/ReferenceBranch/PolicyBranch on both databases —
--   an agent selling Motor policies through hippodb is the same person, under the same broker and
--   branch, selling Non-Motor policies through mammothdb. The two databases remain on separate
--   servers (no FK, no cross-database transaction, no linked server) — only the agent/broker/branch
--   data they draw from is shared by construction (byte-identical CASE expressions in both files).
--   The invariant that keeps them in sync is now proved by
--   tests/Integration.Tests/SimCrossInstanceConsistencyTests.cs, not by a SQL cross-database query —
--   see external-sim-separate-containers REQ-3 for why (querying across two SQL Server instances
--   needs a linked server; opening two ordinary connections from the test runner does not).
--
-- DELIBERATE DEVIATIONS (all decided in design.md, do not "fix" them here)
--   1. dbo.Documents has NO InsuranceType column (F6): the SP returns a constant ('Motor') for this
--      side. dbo.Documents also has NO BranchCode column — §5.2's 32-field output contract has no
--      such field, only ReferenceBranch (varchar(3)) — so there is nothing for a seeded column to
--      back. @BranchCode (§2) stays a required, validated input parameter (REQ-5 of
--      external-sim-realistic-branch-codes, supersedes REQ-2.11 of products-sp-gateway); if a real
--      filter is ever added it targets ReferenceBranch, not a separate column.
--   2. §5.2 spells the field `previousPolicyNumber`; this sim uses PascalCase `PreviousPolicyNumber`
--      (MINOR-8). The adapter resolves columns via GetOrdinal, which is case-insensitive.
--   3. Enum-valued parameters are compared under COLLATE Latin1_General_BIN2 = case-SENSITIVE (M5),
--      one notch stricter than the CI database default, so the SP can never be laxer than the HTTP
--      boundary and contract tests stay deterministic.
--   4. The database is created COLLATE Thai_100_CI_AS. §5.2 types are honoured exactly (DocumentNo is
--      varchar(150), not nvarchar), and real document numbers embed Thai abbreviations — under the
--      instance default (CP1252) every Thai character in a varchar column would silently become '?'.
--      The self-checks assert a Thai string round-trips, so a mis-encoded run fails loudly instead.
--   5. design.md sketches the page as `SELECT TOP (@PageSize + 1) ... OFFSET ...`; T-SQL rejects TOP
--      and OFFSET in one query expression, so the materialisation uses
--      `OFFSET ... ROWS FETCH NEXT (@PageSize + 1) ROWS ONLY` — same semantics.
--   6. CREATE DATABASE has to be the only statement in its batch, so it goes through EXEC() exactly
--      like 01-principals.sql does.
SET NOCOUNT ON;
SET ANSI_NULLS ON;
-- dbo.Documents carries a FILTERED unique index; DML against such a table requires
-- QUOTED_IDENTIFIER ON, and sqlcmd's default session does not set it (same trap as seed-demo.sql).
-- It is also the setting the procedure gets compiled with.
SET QUOTED_IDENTIFIER ON;
GO

-- WRONG-INSTANCE GUARD (sim-db-separate-logins). This script DROPs the legacy pol_app principal
-- below; pol_app is the sole runtime principal of VCentralPay, so running this file against the core
-- instance by mistake would tear the whole app's login down. Nothing destructive may precede this
-- check. ASSUMPTION: the core catalog carries its default name — a deployment that renamed it via
-- DB_NAME (docker-compose.prod.yml) is not covered by this guard, so it stays reliant on pointing the
-- script at the right server in the first place.
IF DB_ID(N'VCentralPay') IS NOT NULL
    THROW 51002, N'02-hippo-sim: refusing to run — this instance hosts VCentralPay. This script belongs to the hippodb sim instance ONLY.', 1;
GO

IF DB_ID(N'hippodb') IS NULL
    EXEC(N'CREATE DATABASE [hippodb] COLLATE Thai_100_CI_AS');
GO

-- CUTOVER (sim-db-separate-logins): earlier revisions of this file created a pol_app LOGIN/USER here,
-- sharing VCentralPay's credential with a simulated third-party upstream. Drop that legacy principal
-- before creating the new one — USER first, LOGIN second (a login cannot be dropped while a database
-- user is still mapped to it). Idempotent: a fresh instance simply has nothing to drop.
USE [hippodb];
GO
IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'pol_app')
    DROP USER pol_app;
GO
USE [master];
GO
IF EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = N'pol_app')
    DROP LOGIN pol_app;
GO

-- This instance never runs 01-principals.sql (it is a separate SQL Server process from pol-db), so
-- it needs its own hippo_app LOGIN — same pattern as 01-principals.sql:29-31. LOGIN is server-level,
-- so this runs in the master context (the cutover block above already switched back to [master]).
IF NOT EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = N'hippo_app')
    CREATE LOGIN hippo_app WITH PASSWORD = N'$(HIPPO_APP_PASSWORD)', CHECK_POLICY = ON;
GO

-- ############################################################################
-- hippodb — simulated motordb@hippo (Motor: CMI | VMI)
-- ############################################################################
USE [hippodb];
GO

IF OBJECT_ID(N'dbo.Documents', N'U') IS NULL
CREATE TABLE dbo.Documents (
    DocumentId           int IDENTITY PRIMARY KEY,  -- sim-internal key + ordering tie-break; never returned
    SourceSystem         varchar(10)   NOT NULL,    -- CMI | VMI
    DocumentType         varchar(20)   NULL,
    DocumentNo           varchar(150)  NULL,
    PolicyYear           varchar(2)    NULL,
    ReferenceBranch      varchar(3)    NULL,
    ReferencePre         varchar(20)   NULL,
    PolicySequenceNo     varchar(30)   NULL,
    ReferenceYear        varchar(2)    NULL,
    ReferenceNo          varchar(30)   NULL,
    PolicyBranch         nvarchar(250) NULL,
    PolicyType           nvarchar(250) NULL,
    SaleCode             varchar(20)   NULL,
    SaleFullName         nvarchar(500) NULL,
    BrokerCode           varchar(20)   NULL,
    BrokerName           nvarchar(500) NULL,
    PolicyNumber         varchar(150)  NULL,
    ApplicationNumber    varchar(150)  NULL,
    PreviousPolicyNumber varchar(150)  NULL,
    EndorsementNumber    varchar(150)  NULL,
    StartDate            datetime2(0)  NULL,
    EndDate              datetime2(0)  NULL,
    ShowName             nvarchar(500) NULL,
    NetPremium           decimal(19,2) NULL,
    Stamp                decimal(19,2) NULL,
    TaxVat               decimal(19,2) NULL,
    TotalPremium         decimal(19,2) NULL,
    CommissionPercent    decimal(19,6) NULL,
    CommissionAmount     decimal(19,2) NULL,
    PaidDate             datetime2(0)  NULL,
    LicensePlateNumber   nvarchar(100) NULL,
    PaymentStatus        varchar(10)   NULL);
GO

-- shop.Products.IX_Products_DocumentNo is unique across the WHOLE local catalogue, so the two
-- simulated sides must never mint the same DocumentNo (M9). This index enforces it per side; the
-- self-checks enforce the disjoint PolicyYear literals ('69…' here, '26…' in mammothdb, REQ-4.1/4.2)
-- that keep the sides apart (external-sim-documentno-format REQ-6.1).
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'UX_Documents_DocumentNo' AND object_id = OBJECT_ID(N'dbo.Documents'))
    CREATE UNIQUE INDEX UX_Documents_DocumentNo ON dbo.Documents(DocumentNo) WHERE DocumentNo IS NOT NULL;
GO

CREATE OR ALTER PROCEDURE dbo.usp_Motor_SearchDocument
    @BranchCode        varchar(3),
    @SaleCode          varchar(20),
    @SearchText        nvarchar(100) = NULL,
    @InsuredName       nvarchar(200) = NULL,
    @CoverageStartFrom date          = NULL,
    @CoverageStartTo   date          = NULL,
    @CoverageEndFrom   date          = NULL,
    @CoverageEndTo     date          = NULL,
    @PaymentStatus     varchar(10)   = NULL,
    @DocumentType      varchar(20)   = NULL,
    @ProductGroup      varchar(10)   = NULL,
    @PolicyNo          varchar(30)   = NULL,
    @ApplicationNo     varchar(30)   = NULL,
    @PaidDateFrom      datetime2(0)  = NULL,
    @PaidDateTo        datetime2(0)  = NULL,
    @PageNo            int           = NULL,
    @PageSize          int           = NULL,
    @CountMode         varchar(10)   = NULL
AS
BEGIN
    -- NOCOUNT is load-bearing: the caller reads exactly two result sets in order, and a stray
    -- rows-affected count from the materialisation below would desynchronise NextResult().
    SET NOCOUNT ON;

    ------------------------------------------------------------------ (1) trim + default-fill (§2)
    SET @BranchCode = NULLIF(LTRIM(RTRIM(ISNULL(@BranchCode, ''))), '');
    SET @SaleCode   = NULLIF(LTRIM(RTRIM(ISNULL(@SaleCode,   ''))), '');
    SET @PaymentStatus = ISNULL(@PaymentStatus, 'UNPAID');
    SET @DocumentType  = ISNULL(@DocumentType,  'ALL');
    SET @ProductGroup  = ISNULL(@ProductGroup,  'ALL');
    SET @CountMode     = ISNULL(@CountMode,     'EXACT');
    IF @PageNo IS NULL OR @PageNo < 1 SET @PageNo = 1;
    IF @PageSize IS NULL OR @PageSize < 1 SET @PageSize = 25;
    IF @PageSize > 25 SET @PageSize = 25;

    ------------------------------------------------------- (2) validate — FIXED order (§6, M1/M2)
    -- §6 is a table of numbers, not an order. This order is a decision of the products-sp-gateway
    -- spec and is pinned by contract tests with multi-invalid inputs: identity/scope first, then
    -- enums in parameter order, then range inversions.
    IF @BranchCode IS NULL
        THROW 50004, N'BranchCode is required.', 1;
    IF @SaleCode IS NULL
        THROW 50005, N'SaleCode is required.', 1;
    IF @DocumentType COLLATE Latin1_General_BIN2 NOT IN ('APPLICATION', 'POLICY', 'RENEWAL', 'ENDORSEMENT', 'ALL')
        THROW 50001, N'Invalid DocumentType.', 1;
    IF @ProductGroup COLLATE Latin1_General_BIN2 NOT IN ('CMI', 'VMI', 'ALL')
        THROW 50002, N'Invalid ProductGroup.', 1;
    IF @PaymentStatus COLLATE Latin1_General_BIN2 NOT IN ('UNPAID', 'PAID', 'ALL')
        THROW 50007, N'Invalid PaymentStatus.', 1;
    IF @CountMode COLLATE Latin1_General_BIN2 NOT IN ('EXACT', 'FAST')
        THROW 50006, N'Invalid CountMode.', 1;
    IF @PaidDateFrom IS NOT NULL AND @PaidDateTo IS NOT NULL AND @PaidDateFrom > @PaidDateTo
        THROW 50003, N'PaidDateFrom is after PaidDateTo.', 1;
    IF @CoverageStartFrom IS NOT NULL AND @CoverageStartTo IS NOT NULL AND @CoverageStartFrom > @CoverageStartTo
        THROW 50008, N'CoverageStartFrom is after CoverageStartTo.', 1;
    IF @CoverageEndFrom IS NOT NULL AND @CoverageEndTo IS NOT NULL AND @CoverageEndFrom > @CoverageEndTo
        THROW 50009, N'CoverageEndFrom is after CoverageEndTo.', 1;

    ---------------------------------------------------------- (3) force PAID, AFTER validation (§2)
    -- Doing this before 50007 would swallow an invalid @PaymentStatus whenever a paid-date filter
    -- happens to be present (M1).
    DECLARE @EffectivePaymentStatus varchar(10) = @PaymentStatus;
    IF @PaidDateFrom IS NOT NULL OR @PaidDateTo IS NOT NULL
        SET @EffectivePaymentStatus = 'PAID';

    -- LIKE inputs are escaped inside the SP (MINOR-5) with the same convention as
    -- BuildingBlocks.Application.SfsLike.Escape: backslash first, then % _ [ ; escape char '\'.
    DECLARE @SearchPattern nvarchar(410) =
        CASE WHEN NULLIF(@SearchText, N'') IS NOT NULL
             THEN N'%' + REPLACE(REPLACE(REPLACE(REPLACE(@SearchText,
                      N'\', N'\\'), N'%', N'\%'), N'_', N'\_'), N'[', N'\[') + N'%' END;
    DECLARE @InsuredPattern nvarchar(810) =
        CASE WHEN NULLIF(@InsuredName, N'') IS NOT NULL
             THEN N'%' + REPLACE(REPLACE(REPLACE(REPLACE(@InsuredName,
                      N'\', N'\\'), N'%', N'\%'), N'_', N'\_'), N'[', N'\[') + N'%' END;

    DECLARE @today date = CAST(GETDATE() AS date);

    ------------------------------------------- (4-6) predicate + per-row window + smart search
    -- The whole matching set lands in #match FIRST so the predicate is written exactly once: the
    -- EXACT count and the page below both read it, and they can never drift apart.
    CREATE TABLE #match (DocumentId int NOT NULL PRIMARY KEY);

    INSERT INTO #match (DocumentId)
    SELECT d.DocumentId
    FROM dbo.Documents d
    WHERE d.SaleCode = @SaleCode                                        -- scope axis, exact (§2)
      -- Search window is evaluated PER ROW, never chosen from @DocumentType (M3): 'ALL' mixes
      -- RENEWAL and non-RENEWAL documents in one result set, and they obey different rules.
      AND ((d.DocumentType = 'RENEWAL'
                AND d.EndDate >= @today
                AND d.EndDate < DATEADD(month, 2, @today))
           OR (d.DocumentType <> 'RENEWAL'
                AND d.StartDate >= DATEADD(month, -6, @today)))
      AND (@DocumentType = 'ALL' OR d.DocumentType = @DocumentType)
      AND (@ProductGroup = 'ALL' OR d.SourceSystem = @ProductGroup)
      AND (@EffectivePaymentStatus = 'ALL' OR d.PaymentStatus = @EffectivePaymentStatus)
      AND (@PolicyNo IS NULL OR d.PolicyNumber = @PolicyNo)
      AND (@ApplicationNo IS NULL OR d.ApplicationNumber = @ApplicationNo)
      -- Coverage bounds are `date` while the columns are datetime2(0): the upper bounds compare
      -- against the following midnight so "inclusive" covers the whole named day, not 00:00:00.
      AND (@CoverageStartFrom IS NULL OR d.StartDate >= @CoverageStartFrom)
      AND (@CoverageStartTo   IS NULL OR d.StartDate <  DATEADD(day, 1, @CoverageStartTo))
      AND (@CoverageEndFrom   IS NULL OR d.EndDate   >= @CoverageEndFrom)
      AND (@CoverageEndTo     IS NULL OR d.EndDate   <  DATEADD(day, 1, @CoverageEndTo))
      AND (@PaidDateFrom IS NULL OR d.PaidDate >= @PaidDateFrom)
      AND (@PaidDateTo   IS NULL OR d.PaidDate <= @PaidDateTo)
      AND (@InsuredPattern IS NULL OR d.ShowName LIKE @InsuredPattern ESCAPE N'\')
      -- Motor smart search includes the licence plate (§3); Non-Motor does not (§4).
      AND (@SearchPattern IS NULL
           OR d.DocumentNo         LIKE @SearchPattern ESCAPE N'\'
           OR d.PolicyNumber       LIKE @SearchPattern ESCAPE N'\'
           OR d.ApplicationNumber  LIKE @SearchPattern ESCAPE N'\'
           OR d.EndorsementNumber  LIKE @SearchPattern ESCAPE N'\'
           OR d.LicensePlateNumber LIKE @SearchPattern ESCAPE N'\');

    ------------------------------------------------- (7) materialise, then two result sets (§5, M4)
    DECLARE @TotalRows bigint = NULL, @TotalPages bigint = NULL;
    IF @CountMode = 'EXACT'
    BEGIN
        SELECT @TotalRows = COUNT_BIG(*) FROM #match;
        SET @TotalPages = CAST(CEILING(@TotalRows / CAST(@PageSize AS decimal(19,0))) AS bigint);
    END

    -- One row past the page: that extra row is how HasNextPage stays correct in FAST mode, where
    -- there is no total to compare against. It is dropped again by the TOP in result set 2 (F5).
    -- The offset is computed in bigint so a hand-crafted @PageNo near int.MaxValue cannot overflow.
    CREATE TABLE #page (DocumentId int NOT NULL PRIMARY KEY);

    INSERT INTO #page (DocumentId)
    SELECT d.DocumentId
    FROM #match m
    JOIN dbo.Documents d ON d.DocumentId = m.DocumentId
    ORDER BY d.DocumentNo, d.DocumentId
    OFFSET (CAST(@PageNo - 1 AS bigint) * @PageSize) ROWS
    FETCH NEXT (@PageSize + 1) ROWS ONLY;

    DECLARE @HasNextPage bit = CASE WHEN (SELECT COUNT(*) FROM #page) > @PageSize THEN 1 ELSE 0 END;

    -- Result set 1 — pagination metadata (§5.1). Always exactly one row, always first.
    SELECT @TotalRows                                                     AS TotalRows,
           @TotalPages                                                    AS TotalPages,
           @PageNo                                                        AS PageNo,
           @PageSize                                                      AS PageSize,
           @HasNextPage                                                   AS HasNextPage,
           CASE WHEN @PageNo > 1 THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS HasPreviousPage,
           @CountMode                                                     AS CountMode,
           CAST(6 AS int)                                                 AS SearchWindowMonths;

    -- Result set 2 — document items (§5.2), same order the page was cut in. InsuranceType is a
    -- constant per side, not a stored column (F6).
    SELECT TOP (@PageSize)
           CAST(N'Motor' AS nvarchar(20)) AS InsuranceType,
           d.SourceSystem,
           d.DocumentType,
           d.DocumentNo,
           d.PolicyYear,
           d.ReferenceBranch,
           d.ReferencePre,
           d.PolicySequenceNo,
           d.ReferenceYear,
           d.ReferenceNo,
           d.PolicyBranch,
           d.PolicyType,
           d.SaleCode,
           d.SaleFullName,
           d.BrokerCode,
           d.BrokerName,
           d.PolicyNumber,
           d.ApplicationNumber,
           d.PreviousPolicyNumber,
           d.EndorsementNumber,
           d.StartDate,
           d.EndDate,
           d.ShowName,
           d.NetPremium,
           d.Stamp,
           d.TaxVat,
           d.TotalPremium,
           d.CommissionPercent,
           d.CommissionAmount,
           d.PaidDate,
           d.LicensePlateNumber,
           d.PaymentStatus
    FROM #page p
    JOIN dbo.Documents d ON d.DocumentId = p.DocumentId
    ORDER BY d.DocumentNo, d.DocumentId;
END
GO

-- REQ-3.1: EXECUTE on the search procedure. The procedure's own read of dbo.Documents rides
-- ownership chaining (dbo -> dbo), which is exactly the shape §4.3 describes for the real login.
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'hippo_app')
    CREATE USER hippo_app FOR LOGIN hippo_app;
GO
GRANT EXECUTE ON dbo.usp_Motor_SearchDocument TO hippo_app;
GO
-- Plus direct SELECT on the table, so hippo_app behaves like pol_app does on VCentralPay: the
-- credential in .env is the ONE a developer has, and without this SQL Server's metadata visibility
-- hides dbo.Documents from sys.tables entirely — the table does not merely fail to read, it stops
-- appearing in a GUI client's object tree at all, which reads as "the seed is missing".
-- Trade-off accepted deliberately: the real upstream login is EXECUTE-only, so this grant makes the
-- sim looser than the system it stands in for. Nothing in production code SELECTs this table (the
-- gateway only EXECs the procedure); RawConnectionTests still pins that seam.
GRANT SELECT ON dbo.Documents TO hippo_app;
GO

-- ---------------------------------------------------------------------------
-- Seed (REQ-1.4). Deterministic, relative to GETDATE(), and INVENTED — the shapes imitate real
-- documents, the values are ours (same rule as demo-seed-data). Idempotent by full reload: this
-- database exists only to be simulated upstream, so there is nothing else in it to preserve.
-- Every DocumentNo starts with PolicyYear '69' here and '26' in mammothdb (M9); the self-check
-- enforces it (external-sim-documentno-format REQ-4.1/4.2/6.1).
-- ---------------------------------------------------------------------------
DELETE FROM dbo.Documents;
GO

DECLARE @today date = CAST(GETDATE() AS date);

-- The hand-written rows are the axis rows: each exists to make one contract rule observable.
-- PolicySequenceNo is marker-prefixed rather than a bare sequential index: rows 1-9 (the ones the
-- smart-search-window test must MATCH @SearchText = "91") get prefix '91', rows 10-14 (must NOT
-- match) get prefix '80', each zero-padded to this row's own width (7 digits CMI, 6 VMI). A bare
-- index would collide across the two widths — a 2-digit index leaves a different number of leading
-- zeros per width (design.md "A discovered conflict this design resolves").
INSERT INTO dbo.Documents (SourceSystem, DocumentType, PolicySequenceNo, SaleCode,
                           StartDate, EndDate, ShowName, TotalPremium, PaymentStatus, PaidDate,
                           LicensePlateNumber)
VALUES
    -- in the 6-month window, UNPAID — the plain happy-path rows
    ('VMI', 'POLICY',      '910001', '77001',
        DATEADD(day, -30, @today), DATEADD(day, 335, @today), N'นายสมชาย ใจดีมงคล',      12500.00, 'UNPAID', NULL, N'1กก 1001'),
    ('CMI', 'POLICY',      '9100002', '77001',
        DATEADD(day, -60, @today), DATEADD(day, 305, @today), N'นางสาวปรียานุช แสงทองดี',   645.21, 'UNPAID', NULL, NULL),
    -- OUTSIDE the 6-month window (StartDate 245 days back) — must never come back
    ('VMI', 'POLICY',      '910003', '77001',
        DATEADD(day, -245, @today), DATEADD(day, 120, @today), N'นายวีรพงษ์ ตันติเจริญ',   9800.00, 'UNPAID', NULL, N'3คค 1003'),
    -- RENEWAL inside the 2-month EndDate window — included even though StartDate is a year back
    ('VMI', 'RENEWAL',     '910004', '77001',
        DATEADD(day, -335, @today), DATEADD(day, 30, @today), N'นางอรพรรณ ศรีสุขเกษม',    15900.00, 'UNPAID', NULL, N'4งง 1004'),
    -- RENEWAL expiring beyond 2 months — excluded
    ('CMI', 'RENEWAL',     '9100005', '77001',
        DATEADD(day, -265, @today), DATEADD(day, 100, @today), N'นายธนกฤต พงษ์พิพัฒน์',     720.00, 'UNPAID', NULL, N'5จจ 1005'),
    -- RENEWAL already expired — excluded (window is [today, today + 2 months))
    ('VMI', 'RENEWAL',     '910006', '77001',
        DATEADD(day, -375, @today), DATEADD(day, -10, @today), N'นางสาวชนิสรา บุญมาก',     8900.00, 'UNPAID', NULL, N'6ฉฉ 1006'),
    -- PAID with a PaidDate, both sides of @PaymentStatus / @PaidDateFrom-@PaidDateTo
    ('VMI', 'POLICY',      '910007', '77001',
        DATEADD(day, -20, @today), DATEADD(day, 345, @today), N'นางสาวพิมพ์ชนก เลิศวัฒนา', 24500.00, 'PAID', DATEADD(day, -7, @today), N'7ชช 1007'),
    ('CMI', 'ENDORSEMENT', '9100008', '77001',
        DATEADD(day, -15, @today), DATEADD(day, 350, @today), N'นายกิตติพงศ์ อารีย์วงศ์',   6200.00, 'PAID', DATEADD(day, -3, @today), NULL),
    -- APPLICATION never pairs with CMI (§1.2) — VMI only on the Motor side
    ('VMI', 'APPLICATION', '910009', '77001',
        DATEADD(day, -10, @today), DATEADD(day, 355, @today), N'นางสาวสุนิสา วงศ์สว่าง',   32000.00, 'UNPAID', NULL, N'9ญญ 1009'),
    -- Ordinary VMI POLICY row under agent 77002 (สาขาสีลม) — same shape as row 1, different agent/branch.
    ('VMI', 'POLICY',      '800010', '77002',
        DATEADD(day, -25, @today), DATEADD(day, 340, @today), N'นายเอกรัตน์ ธีรวุฒิ',      4100.00, 'UNPAID', NULL, N'1ฎฎ 1010'),
    -- ShowName carries literal LIKE metacharacters: @InsuredName = '100%' must match THIS row only
    ('CMI', 'POLICY',      '8000011', '77001',
        DATEADD(day, -5, @today), DATEADD(day, 360, @today), N'บริษัท 100%_มงคลยานยนต์ จำกัด', 590.00, 'UNPAID', NULL, NULL),
    ('VMI', 'POLICY',      '800012', '77001',
        DATEADD(day, -1, @today), DATEADD(day, 364, @today), N'นายภาณุวัฒน์ สุขประเสริฐ',    480.00, 'UNPAID', NULL, N'2ฐฐ 1012'),
    -- StartDate exactly 6 months back: the window boundary is inclusive
    ('CMI', 'POLICY',      '8000013', '77001',
        DATEADD(month, -6, @today), DATEADD(day, 180, @today), N'นางเบญจวรรณ ทองอยู่',      390.00, 'UNPAID', NULL, N'3ฑฑ 1013'),
    -- Distinctive plate for the Motor-only smart-search test (mammothdb seeds '8ฮฮ 8888', which its
    -- SP must NOT find)
    ('VMI', 'ENDORSEMENT', '800014', '77001',
        DATEADD(day, -45, @today), DATEADD(day, 320, @today), N'นายจักรพงษ์ วิริยะกุล',    1200.00, 'UNPAID', NULL, N'9ฮฮ 9999');

-- 186 more in-window UNPAID rows (bringing hippodb to 200 total) so a default search overflows the
-- 25-row page cap many times over and HasNextPage / mid-range pages / FAST mode all have something
-- to prove. StartDate/EndDate offsets are bound via DaysBack (never exceeding 170 days) so every
-- generated row stays inside the 6-month window (181 days is the calendar minimum) — same intent as
-- the original 20-row block, just scaled.
INSERT INTO dbo.Documents (SourceSystem, DocumentType, PolicySequenceNo, SaleCode,
                           StartDate, EndDate, ShowName, TotalPremium, PaymentStatus, PaidDate,
                           LicensePlateNumber)
SELECT
    CASE WHEN g.value % 2 = 0 THEN 'VMI' ELSE 'CMI' END,
    CASE WHEN g.value % 3 = 0 THEN 'ENDORSEMENT' ELSE 'POLICY' END,
    -- REQ-3.4: 100 + g.value, zero-padded to this row's own width (7 CMI, 6 VMI). SourceSystem is
    -- duplicated from the first SELECT-list expression above — T-SQL can't reference a sibling
    -- SELECT-list alias (design.md "Generated-row SELECT shape change").
    RIGHT(REPLICATE('0', 7) + CONVERT(varchar(7), 100 + g.value),
          CASE WHEN CASE WHEN g.value % 2 = 0 THEN 'VMI' ELSE 'CMI' END = 'CMI' THEN 7 ELSE 6 END),
    -- SaleCode is an agent's own code, so a 6-agent roster needs 6 distinct codes, not one shared
    -- literal (see the SaleFullName CASE in the UPDATE below, keyed off this same code). Keyed on
    -- names.Idx, not g.value, so every ShowName always sells through the same one agent (an insured
    -- party doesn't jump agents from policy to policy) — a contiguous 7/7/7/6/6/7 split of the 40-name
    -- pool below, in Idx order.
    CASE WHEN names.Idx BETWEEN 0  AND 6  THEN '77001'
         WHEN names.Idx BETWEEN 7  AND 13 THEN '77002'
         WHEN names.Idx BETWEEN 14 AND 20 THEN '77003'
         WHEN names.Idx BETWEEN 21 AND 26 THEN '77004'
         WHEN names.Idx BETWEEN 27 AND 32 THEN '77005'
         ELSE                                  '77006' END,
    DATEADD(day, -o.DaysBack, @today),
    DATEADD(day, 365 - o.DaysBack, @today),
    names.ShowName,
    CAST(500 + g.value * 137.25 AS decimal(19,2)),
    'UNPAID',
    NULL,
    CASE WHEN g.value % 2 = 0
         THEN CONCAT(g.value % 9 + 1, N'กท', N' ', CONVERT(varchar(4), 2000 + g.value)) END
FROM GENERATE_SERIES(1, 186) g
-- g.value % 170 = 29 -> DaysBack 30 would collide with axis row #1 (StartDate -30 / EndDate +335),
-- the exact-single-day landmark Coverage_bounds_are_inclusive_on_both_ends expects as a singleton
-- match; remap that one occurrence to DaysBack 1 (a value no exact-boundary test queries for).
CROSS APPLY (SELECT CASE WHEN g.value % 170 = 29 THEN 1 ELSE 1 + (g.value % 170) END AS DaysBack) o
-- 40-name pool so no ShowName repeats more than 5 times across the 186 generated rows
-- (186 = 40*4 + 26 -> 26 names appear 5 times, 14 appear 4 times).
JOIN (VALUES
    (0,  N'นายอดิศักดิ์ เรืองสุวรรณ'),      (1,  N'นางสาวศิริพร แก้วมณี'),
    (2,  N'นายณัฐพงษ์ บุญเลิศ'),            (3,  N'นางสาวปิยะดา ศรีสวัสดิ์'),
    (4,  N'นายชัยวัฒน์ ทิพย์มณี'),          (5,  N'นางสาวธัญญารัตน์ พูลสวัสดิ์'),
    (6,  N'นายสุรเดช วงศ์ไพศาล'),           (7,  N'นางสาวกัญญารัตน์ โพธิ์ทอง'),
    (8,  N'นายวิสุทธิ์ ชูเกียรติ'),         (9,  N'นางสาวอรุณี สวัสดิ์รักษา'),
    (10, N'นายประพันธ์ เกษมสุข'),           (11, N'นางสาวนภัสสร วัฒนกุล'),
    (12, N'นายกฤษฎา อินทรสุวรรณ'),          (13, N'นางสาวสมฤทัย จันทรังษี'),
    (14, N'นายธีรพล คำแก้ว'),               (15, N'นางสาวลลิตา สายทอง'),
    (16, N'นายสมพงษ์ พิทักษ์กุล'),          (17, N'นางสาวชนากานต์ รักษ์ดี'),
    (18, N'นายไพรัช สุขสมบูรณ์'),           (19, N'นางสาวเบญจมาศ ทองสุข'),
    (20, N'นายพีรพัฒน์ วิเชียรเพริศ'),      (21, N'นางสาวสุพัตรา แจ่มใส'),
    (22, N'นายอนันต์ ศรีบุญเรือง'),         (23, N'นางสาวขวัญใจ อยู่สบาย'),
    (24, N'นายรัฐพล เจนจบ'),                (25, N'นางสาวมณีรัตน์ ทวีสุข'),
    (26, N'นายเกียรติศักดิ์ ประเสริฐกุล'),  (27, N'นางสาวปวีณา ศิริวัฒนา'),
    (28, N'นายบรรจง หาญกล้า'),              (29, N'นางสาวรุ่งนภา แสงอรุณ'),
    (30, N'นายศักดิ์สิทธิ์ บุญมี'),         (31, N'นางสาวจริยา เพชรรัตน์'),
    (32, N'นายวรวุฒิ ทองแดง'),              (33, N'นางสาวพรทิพย์ อ่อนละมัย'),
    (34, N'นายณรงค์ชัย ปิ่นทอง'),           (35, N'นางสาวสุกัญญา ไชยวงศ์'),
    (36, N'นายเฉลิมพล กาญจนวงศ์'),          (37, N'นางสาวอัจฉรา บัวขาว'),
    (38, N'นายทวีศักดิ์ มีสุข'),            (39, N'นางสาวธนพร ศรีสมบูรณ์')
) AS names(Idx, ShowName) ON names.Idx = g.value % 40;
GO

-- Fill the derived document fields for every seeded row in one pass (same idea as seed-demo.sql):
-- PolicySequenceNo already carries the row's running number (set at INSERT time, REQ-3.4/REQ-5), so
-- this pass composes DocumentNo and the REQ-9 policy-number family FORWARD from it and the row's own
-- SaleCode/DocumentType/SourceSystem — nothing here parses DocumentNo back out (REQ-5.3).
-- Premium components are derived backwards from TotalPremium: net + 0.4% stamp + 7% VAT, with
-- TaxVat as the residual so the three always add up exactly.
UPDATE d
SET PolicyYear           = '69',
    ReferenceYear        = '69',
    -- ReferenceBranch/PolicyBranch is one branch office, code + name — the same paired-set rule as
    -- SaleCode/SaleFullName. A broker's home branch is fixed, and BrokerCode is itself pinned to
    -- SaleCode above, so this chains off d.SaleCode too (same partition as the BrokerCode CASE below,
    -- just naming the branch instead of the broker). Sourced from the rb CROSS APPLY below (not an
    -- inline CASE here) because DocumentNo/PolicyNumber/etc. also need this value and a SET clause
    -- cannot see another SET clause's result within the same UPDATE.
    ReferenceBranch      = rb.ReferenceBranch,
    ReferencePre         = CASE WHEN d.DocumentType = 'ENDORSEMENT' THEN '900' END,
    ReferenceNo          = d.PolicySequenceNo,
    PolicyBranch         = CASE WHEN d.SaleCode IN ('77001', '77006') THEN N'สำนักงานใหญ่'
                                 WHEN d.SaleCode =    '77002'          THEN N'สาขาสีลม'
                                 WHEN d.SaleCode =    '77003'          THEN N'สาขาเชียงใหม่'
                                 WHEN d.SaleCode =    '77004'          THEN N'สาขาหาดใหญ่'
                                 WHEN d.SaleCode =    '77005'          THEN N'สาขาขอนแก่น' END,
    PolicyType           = CASE WHEN d.SourceSystem = 'VMI' THEN N'90' END,
    -- SaleCode is the agent's own code; SaleFullName is that same agent's name — one code always names
    -- the same agent, so this is keyed on d.SaleCode, not the running number. This is the shared
    -- 6-agent/broker/branch master roster (external-sim-shared-agent-network) — mammothdb's block
    -- below duplicates this same CASE verbatim, since both databases represent one insurance
    -- company's sales network.
    SaleFullName         = CASE d.SaleCode
                               WHEN '77001' THEN N'นายกิตติพงศ์ อารีย์วงศ์'
                               WHEN '77002' THEN N'นางสาวสุนิสา วงศ์สว่าง'
                               WHEN '77003' THEN N'นายเอกรัตน์ ธีรวุฒิ'
                               WHEN '77004' THEN N'นางสาวจิราพร คงเจริญ'
                               WHEN '77005' THEN N'นายภาณุวัฒน์ สุขประเสริฐ'
                               WHEN '77006' THEN N'นางเบญจวรรณ ทองอยู่' END,
    -- BrokerCode/BrokerName is the broker firm an agent works under — one agent always sits under the
    -- same broker, so this is keyed on d.SaleCode too (same 5-broker pool mammothdb's block below uses,
    -- since a broker firm can carry both Motor and Non-Motor agents).
    BrokerCode           = CASE d.SaleCode
                               WHEN '77001' THEN '701' WHEN '77002' THEN '702' WHEN '77003' THEN '703'
                               WHEN '77004' THEN '704' WHEN '77005' THEN '705' WHEN '77006' THEN '701' END,
    BrokerName           = CASE d.SaleCode
                               WHEN '77001' THEN N'บริษัท เอเซียรุ่งเรือง อินชัวรันส์ โบรกเกอร์ จำกัด'
                               WHEN '77002' THEN N'บริษัท กรุงสยาม นายหน้าประกันภัย จำกัด'
                               WHEN '77003' THEN N'บริษัท ธนบุรี อินชัวรันส์ โบรกเกอร์ จำกัด'
                               WHEN '77004' THEN N'บริษัท ภูมิภาคประกันภัย นายหน้า จำกัด'
                               WHEN '77005' THEN N'บริษัท เอ็น พี ที อินชัวรันส์ โบรกเกอร์ จำกัด'
                               WHEN '77006' THEN N'บริษัท เอเซียรุ่งเรือง อินชัวรันส์ โบรกเกอร์ จำกัด' END,
    -- DocumentNo (REQ-1): PolicyYear + ReferenceBranch + '/' + Abbrev + '/' + PolicySequenceNo, with
    -- Motor's ENDORSEMENT variant appending an un-delimited '1' after the running number (REQ-1.3).
    DocumentNo           = CASE WHEN d.DocumentType = 'ENDORSEMENT'
                                THEN CONCAT('69', rb.ReferenceBranch, '/', ab.Abbrev, '/', d.PolicySequenceNo, '1')
                                ELSE CONCAT('69', rb.ReferenceBranch, '/', ab.Abbrev, '/', d.PolicySequenceNo) END,
    PolicyNumber         = CASE WHEN d.DocumentType <> 'APPLICATION'
                                THEN CONCAT(d.SaleCode, '-69', rb.ReferenceBranch, '/', d.PolicySequenceNo) END,
    ApplicationNumber    = CASE WHEN d.DocumentType = 'APPLICATION'
                                THEN CONCAT(d.SaleCode, '-69', rb.ReferenceBranch, '/', d.PolicySequenceNo) END,
    -- PrevYear is PolicyYear - 1 ('69' -> '68', a fixed literal per REQ-4.3); the running number is
    -- re-derived as an integer, decremented, and re-padded to this row's own RunningWidth (REQ-9.2).
    PreviousPolicyNumber = CASE WHEN d.DocumentType IN ('RENEWAL', 'ENDORSEMENT')
                                THEN CONCAT(d.SaleCode, '-68', rb.ReferenceBranch, '/',
                                     RIGHT(REPLICATE('0', 7) + CONVERT(varchar(7), CAST(d.PolicySequenceNo AS int) - 1),
                                           CASE WHEN d.SourceSystem = 'CMI' THEN 7 ELSE 6 END)) END,
    EndorsementNumber    = CASE WHEN d.DocumentType = 'ENDORSEMENT' THEN CONCAT('E', d.PolicySequenceNo) END,
    NetPremium           = m.Net,
    Stamp                = m.Stamp,
    TaxVat               = d.TotalPremium - m.Net - m.Stamp,
    CommissionPercent    = m.Pct,
    CommissionAmount     = ROUND(m.Net * m.Pct / 100, 2)
FROM dbo.Documents d
CROSS APPLY (
    SELECT CASE WHEN d.SaleCode IN ('77001', '77006') THEN '301'
                WHEN d.SaleCode =    '77002'          THEN '315'
                WHEN d.SaleCode =    '77003'          THEN '220'
                WHEN d.SaleCode =    '77004'          THEN '335'
                WHEN d.SaleCode =    '77005'          THEN '450' END AS ReferenceBranch
) rb
-- Abbrev(SourceSystem, DocumentType) — hippodb is Motor-only (CMI|VMI), so only the Motor half of
-- design.md's table applies: POLICY/RENEWAL share 'กธ' (REQ-2.7), APPLICATION -> 'รย', ENDORSEMENT -> 'อท'.
CROSS APPLY (
    SELECT CASE WHEN d.DocumentType = 'ENDORSEMENT' THEN N'อท'
                WHEN d.DocumentType = 'APPLICATION' THEN N'รย'
                ELSE N'กธ' END AS Abbrev
) ab
CROSS APPLY (SELECT ROUND(d.TotalPremium / 1.07428, 2) AS Net) n
CROSS APPLY (
    SELECT n.Net AS Net,
           ROUND(n.Net * 0.004, 2) AS Stamp,
           CAST(CASE CAST(d.PolicySequenceNo AS int) % 3 WHEN 0 THEN 10 WHEN 1 THEN 12 ELSE 15 END AS decimal(19,6)) AS Pct
) m;
GO

-- Self-check (REQ-1.5): objects, grant, row counts, DocumentNo prefix, collation, and Thai round-trip.
-- Anything off THROWs, which makes `sqlcmd -b` exit non-zero and the bootstrap container fail.
-- Collation is checked by name, not just by the Thai round-trip below: Thai_CI_AS and
-- Thai_100_CI_AS share code page 874, so a round-trip alone cannot tell an old DB apart from one
-- actually recreated under Thai_100_CI_AS (the idempotency guard above skips CREATE DATABASE once
-- the database already exists, so a stale collation would otherwise go undetected forever).
IF ISNULL(CONVERT(nvarchar(128), DATABASEPROPERTYEX(N'hippodb', N'Collation')), N'') <> N'Thai_100_CI_AS'
    THROW 51002, N'02-hippo-sim: hippodb collation is not Thai_100_CI_AS.', 1;
IF OBJECT_ID(N'dbo.Documents', N'U') IS NULL
    THROW 51002, N'02-hippo-sim: hippodb.dbo.Documents is missing.', 1;
IF OBJECT_ID(N'dbo.usp_Motor_SearchDocument', N'P') IS NULL
    THROW 51002, N'02-hippo-sim: hippodb.dbo.usp_Motor_SearchDocument is missing.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'UX_Documents_DocumentNo' AND object_id = OBJECT_ID(N'dbo.Documents'))
    THROW 51002, N'02-hippo-sim: hippodb UX_Documents_DocumentNo is missing.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'hippo_app')
    THROW 51002, N'02-hippo-sim: hippodb has no hippo_app user.', 1;
IF NOT EXISTS (SELECT 1
               FROM sys.database_permissions p
               JOIN sys.database_principals u ON u.principal_id = p.grantee_principal_id
               WHERE u.name = N'hippo_app' AND p.permission_name = N'EXECUTE' AND p.state = 'G'
                 AND p.major_id = OBJECT_ID(N'dbo.usp_Motor_SearchDocument'))
    THROW 51002, N'02-hippo-sim: hippo_app lacks EXECUTE on hippodb.dbo.usp_Motor_SearchDocument.', 1;
-- Cutover completeness (sim-db-separate-logins): the legacy shared principal must be gone from BOTH
-- levels, or this instance would still accept VCentralPay's credential.
IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'pol_app')
    THROW 51002, N'02-hippo-sim: hippodb still has a pol_app user (cutover to hippo_app did not complete).', 1;
IF EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = N'pol_app')
    THROW 51002, N'02-hippo-sim: this instance still has a pol_app LOGIN (cutover to hippo_app did not complete).', 1;

DECLARE @rows int = (SELECT COUNT(*) FROM dbo.Documents);
IF @rows <> 200
BEGIN
    DECLARE @rowMsg nvarchar(200) = CONCAT(N'02-hippo-sim: hippodb seeded ', @rows, N' documents, expected 200.');
    THROW 51002, @rowMsg, 1;
END

-- Roster-completeness (REQ-4.3): the shared 6-agent master roster must actually be fully present,
-- not merely true by the current data's coincidence.
IF (SELECT COUNT(DISTINCT SaleCode) FROM dbo.Documents) <> 6
BEGIN
    THROW 51002, N'02-hippo-sim: hippodb expected exactly 6 distinct SaleCode values (the shared master roster).', 1;
END

-- ShowName->SaleCode pairing invariant (REQ-1.5) — the one REQ-1.5 invariant not already guaranteed
-- by construction (every other identity column is set by a SaleCode-keyed CASE/CROSS APPLY).
IF EXISTS (SELECT 1 FROM dbo.Documents GROUP BY ShowName HAVING COUNT(DISTINCT SaleCode) > 1)
BEGIN
    THROW 51002, N'02-hippo-sim: hippodb a ShowName resolves to more than one SaleCode (ShowName->SaleCode pairing invariant violated).', 1;
END

IF EXISTS (SELECT 1 FROM dbo.Documents WHERE DocumentNo IS NULL OR DocumentNo NOT LIKE '69%')
    THROW 51002, N'02-hippo-sim: hippodb DocumentNo must always start with 69 (PolicyYear literal).', 1;

-- A varchar column under a non-Thai collation, or a sqlcmd input code page that is not UTF-8, turns
-- Thai text into '?' silently. Fail here instead of shipping mojibake into every downstream test.
IF NOT EXISTS (SELECT 1 FROM dbo.Documents WHERE ShowName LIKE N'%มงคล%' AND DocumentNo LIKE N'%กธ%')
    THROW 51002, N'02-hippo-sim: hippodb Thai text did not round-trip (collation or sqlcmd input code page).', 1;

-- The count the SP returns for the default search. Pinned because the contract tests build on it.
DECLARE @today date = CAST(GETDATE() AS date);
DECLARE @visible int = (
    SELECT COUNT(*) FROM dbo.Documents
    WHERE SaleCode = '77001' AND PaymentStatus = 'UNPAID'
      AND ((DocumentType = 'RENEWAL' AND EndDate >= @today AND EndDate < DATEADD(month, 2, @today))
        OR (DocumentType <> 'RENEWAL' AND StartDate >= DATEADD(month, -6, @today))));
IF @visible <> 42
BEGIN
    DECLARE @visibleMsg nvarchar(200) = CONCAT(
        N'02-hippo-sim: hippodb default search sees ', @visible, N' documents, expected 42.');
    THROW 51002, @visibleMsg, 1;
END

PRINT N'02-hippo-sim: hippodb OK (200 documents, 42 in the default search window).';
GO
