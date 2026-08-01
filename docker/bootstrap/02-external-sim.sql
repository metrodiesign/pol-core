-- pol-core simulated upstream databases (idempotent). Runs as sa, AFTER 01-principals.sql
-- (it needs the pol_app LOGIN that script creates) and independently of the EF migration chain —
-- hippodb/mammothdb stand in for systems we do NOT own, so they must never enter PolDbContext's
-- lineage. Contains NO secrets and takes no sqlcmd variables:
--   sqlcmd -S <server> -U sa -P <pw> -C -b -i 02-external-sim.sql
-- `-b` = sqlcmd exits non-zero when the self-checks at the bottom THROW.
-- Spec: .ai/specs/products-sp-gateway/{requirements,design}.md (REQ-1, REQ-2, REQ-3).
-- Contract: docs/reference/vcentralpay-sp-quick-reference.pdf v1.0 (§1-§6).
--
-- WHAT THIS SIMULATES
--   hippodb   <- motordb   on server hippo    -> dbo.usp_Motor_SearchDocument     (CMI | VMI)
--   mammothdb <- centerdb  on server mammoth  -> dbo.usp_NonMotor_SearchDocument  (FIRE | MISC)
-- The simulated names deliberately differ from the real catalogue names so nobody mistakes one for
-- the other; cutover to the real upstream changes a connection string only (InitialCatalog), never
-- code — the SP names and the output contract are identical.
--
-- DELIBERATE DEVIATIONS (all decided in design.md, do not "fix" them here)
--   1. mammothdb uses ONE dbo.Documents table instead of the real centerdb + firewebdb + miscwebdb
--      topology (REQ-1.3). The contract we owe is the SP's OUTPUT, not mammoth's internals.
--   2. dbo.Documents has NO InsuranceType column (F6): each SP returns a constant for its side
--      ('Motor' / 'NonMotor'). BranchCode IS a column but is NOT a predicate — §2 makes it required
--      but never defines filter semantics, so the SP validates it and stops there (REQ-2.11); the
--      column is seeded so a future WHERE has data to bite on.
--   3. §5.2 spells the field `previousPolicyNumber`; this sim uses PascalCase `PreviousPolicyNumber`
--      (MINOR-8). The adapter resolves columns via GetOrdinal, which is case-insensitive.
--   4. Enum-valued parameters are compared under COLLATE Latin1_General_BIN2 = case-SENSITIVE (M5),
--      one notch stricter than the CI database default, so the SP can never be laxer than the HTTP
--      boundary and contract tests stay deterministic.
--   5. Both databases are created COLLATE Thai_CI_AS. §5.2 types are honoured exactly (DocumentNo is
--      varchar(150), not nvarchar), and real document numbers embed Thai abbreviations — under the
--      instance default (CP1252) every Thai character in a varchar column would silently become '?'.
--      The self-checks assert a Thai string round-trips, so a mis-encoded run fails loudly instead.
--   6. design.md sketches the page as `SELECT TOP (@PageSize + 1) ... OFFSET ...`; T-SQL rejects TOP
--      and OFFSET in one query expression, so the materialisation uses
--      `OFFSET ... ROWS FETCH NEXT (@PageSize + 1) ROWS ONLY` — same semantics.
--   7. CREATE DATABASE has to be the only statement in its batch, so it goes through EXEC() exactly
--      like 01-principals.sql does.
SET NOCOUNT ON;
SET ANSI_NULLS ON;
-- dbo.Documents carries a FILTERED unique index; DML against such a table requires
-- QUOTED_IDENTIFIER ON, and sqlcmd's default session does not set it (same trap as seed-demo.sql).
-- It is also the setting the two procedures get compiled with.
SET QUOTED_IDENTIFIER ON;
GO

IF DB_ID(N'hippodb') IS NULL
    EXEC(N'CREATE DATABASE [hippodb] COLLATE Thai_CI_AS');
GO

IF DB_ID(N'mammothdb') IS NULL
    EXEC(N'CREATE DATABASE [mammothdb] COLLATE Thai_CI_AS');
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
    BranchCode           varchar(3)    NULL,        -- validated only, never filtered (REQ-2.11)
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
-- self-checks enforce the disjoint prefixes ('77…' here, '88…' in mammothdb) that keep the sides apart.
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

-- REQ-3.1: EXECUTE only. SELECT on dbo.Documents rides ownership chaining (dbo -> dbo), which is
-- exactly the shape §4.3 describes for the real login.
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'pol_app')
    CREATE USER pol_app FOR LOGIN pol_app;
GO
GRANT EXECUTE ON dbo.usp_Motor_SearchDocument TO pol_app;
GO

-- ---------------------------------------------------------------------------
-- Seed (REQ-1.4). Deterministic, relative to GETDATE(), and INVENTED — the shapes imitate real
-- documents, the values are ours (same rule as demo-seed-data). Idempotent by full reload: this
-- database exists only to be simulated upstream, so there is nothing else in it to preserve.
-- Every DocumentNo starts '77' here and '88' in mammothdb (M9); the self-check enforces it.
-- ---------------------------------------------------------------------------
DELETE FROM dbo.Documents;
GO

DECLARE @today date = CAST(GETDATE() AS date);

-- The hand-written rows are the axis rows: each exists to make one contract rule observable.
INSERT INTO dbo.Documents (SourceSystem, BranchCode, DocumentType, DocumentNo, SaleCode,
                           StartDate, EndDate, ShowName, TotalPremium, PaymentStatus, PaidDate,
                           LicensePlateNumber)
VALUES
    -- in the 6-month window, UNPAID — the plain happy-path rows
    ('VMI', '100', 'POLICY',      '77001-69900/' + N'กธ' + '/950001-10', '77001',
        DATEADD(day, -30, @today), DATEADD(day, 335, @today), N'นายสมชาย ใจดีมงคล',      12500.00, 'UNPAID', NULL, N'1กก 1001'),
    ('CMI', '100', 'POLICY',      '77001-69900/' + N'กธ' + '/950002-10', '77001',
        DATEADD(day, -60, @today), DATEADD(day, 305, @today), N'นางสาวปรียานุช แสงทองดี',   645.21, 'UNPAID', NULL, NULL),
    -- OUTSIDE the 6-month window (StartDate 245 days back) — must never come back
    ('VMI', '200', 'POLICY',      '77001-69900/' + N'กธ' + '/950003-10', '77001',
        DATEADD(day, -245, @today), DATEADD(day, 120, @today), N'นายวีรพงษ์ ตันติเจริญ',   9800.00, 'UNPAID', NULL, N'3คค 1003'),
    -- RENEWAL inside the 2-month EndDate window — included even though StartDate is a year back
    ('VMI', '100', 'RENEWAL',     '77001-69900/' + N'ตอ' + '/950004-10', '77001',
        DATEADD(day, -335, @today), DATEADD(day, 30, @today), N'นางอรพรรณ ศรีสุขเกษม',    15900.00, 'UNPAID', NULL, N'4งง 1004'),
    -- RENEWAL expiring beyond 2 months — excluded
    ('CMI', '200', 'RENEWAL',     '77001-69900/' + N'ตอ' + '/950005-10', '77001',
        DATEADD(day, -265, @today), DATEADD(day, 100, @today), N'นายธนกฤต พงษ์พิพัฒน์',     720.00, 'UNPAID', NULL, N'5จจ 1005'),
    -- RENEWAL already expired — excluded (window is [today, today + 2 months))
    ('VMI', '300', 'RENEWAL',     '77001-69900/' + N'ตอ' + '/950006-10', '77001',
        DATEADD(day, -375, @today), DATEADD(day, -10, @today), N'นางสาวชนิสรา บุญมาก',     8900.00, 'UNPAID', NULL, N'6ฉฉ 1006'),
    -- PAID with a PaidDate, both sides of @PaymentStatus / @PaidDateFrom-@PaidDateTo
    ('VMI', '100', 'POLICY',      '77001-69900/' + N'กธ' + '/950007-10', '77001',
        DATEADD(day, -20, @today), DATEADD(day, 345, @today), N'นางสาวพิมพ์ชนก เลิศวัฒนา', 24500.00, 'PAID', DATEADD(day, -7, @today), N'7ชช 1007'),
    ('CMI', '200', 'ENDORSEMENT', '77001-69900/' + N'ปช' + '/950008',    '77001',
        DATEADD(day, -15, @today), DATEADD(day, 350, @today), N'นายกิตติพงศ์ อารีย์วงศ์',   6200.00, 'PAID', DATEADD(day, -3, @today), NULL),
    -- APPLICATION never pairs with CMI (§1.2) — VMI only on the Motor side
    ('VMI', '300', 'APPLICATION', '77001-69900/' + N'กธ' + '/950009-10', '77001',
        DATEADD(day, -10, @today), DATEADD(day, 355, @today), N'นางสาวสุนิสา วงศ์สว่าง',   32000.00, 'UNPAID', NULL, N'9ญญ 1009'),
    -- Different SaleCode: proves @SaleCode is a hard scope axis, not a hint
    ('VMI', '100', 'POLICY',      '77001-69900/' + N'กธ' + '/950010-10', 'S001',
        DATEADD(day, -25, @today), DATEADD(day, 340, @today), N'นายเอกรัตน์ ธีรวุฒิ',      4100.00, 'UNPAID', NULL, N'1ฎฎ 1010'),
    -- ShowName carries literal LIKE metacharacters: @InsuredName = '100%' must match THIS row only
    ('CMI', '100', 'POLICY',      '77001-69900/' + N'กธ' + '/950011-10', '77001',
        DATEADD(day, -5, @today), DATEADD(day, 360, @today), N'บริษัท 100%_มงคลยานยนต์ จำกัด', 590.00, 'UNPAID', NULL, NULL),
    ('VMI', '400', 'POLICY',      '77001-69900/' + N'กธ' + '/950012-10', '77001',
        DATEADD(day, -1, @today), DATEADD(day, 364, @today), N'นายภาณุวัฒน์ สุขประเสริฐ',    480.00, 'UNPAID', NULL, N'2ฐฐ 1012'),
    -- StartDate exactly 6 months back: the window boundary is inclusive
    ('CMI', '100', 'POLICY',      '77001-69900/' + N'กธ' + '/950013-10', '77001',
        DATEADD(month, -6, @today), DATEADD(day, 180, @today), N'นางเบญจวรรณ ทองอยู่',      390.00, 'UNPAID', NULL, N'3ฑฑ 1013'),
    -- Distinctive plate for the Motor-only smart-search test (mammothdb seeds '8ฮฮ 8888', which its
    -- SP must NOT find)
    ('VMI', '200', 'ENDORSEMENT', '77001-69900/' + N'ปช' + '/950014-10', '77001',
        DATEADD(day, -45, @today), DATEADD(day, 320, @today), N'นายจักรพงษ์ วิริยะกุล',    1200.00, 'UNPAID', NULL, N'9ฮฮ 9999');

-- 186 more in-window UNPAID rows (bringing hippodb to 200 total) so a default search overflows the
-- 25-row page cap many times over and HasNextPage / mid-range pages / FAST mode all have something
-- to prove. StartDate/EndDate offsets are bound via DaysBack (never exceeding 170 days) so every
-- generated row stays inside the 6-month window (181 days is the calendar minimum) — same intent as
-- the original 20-row block, just scaled.
INSERT INTO dbo.Documents (SourceSystem, BranchCode, DocumentType, DocumentNo, SaleCode,
                           StartDate, EndDate, ShowName, TotalPremium, PaymentStatus, PaidDate,
                           LicensePlateNumber)
SELECT
    CASE WHEN g.value % 2 = 0 THEN 'VMI' ELSE 'CMI' END,
    CASE g.value % 4 WHEN 0 THEN '100' WHEN 1 THEN '200' WHEN 2 THEN '300' ELSE '400' END,
    CASE WHEN g.value % 3 = 0 THEN 'ENDORSEMENT' ELSE 'POLICY' END,
    '77001-69900/' + N'กธ' + '/' + CONVERT(varchar(6), 950100 + g.value) + '-10',
    '77001',
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
-- the sequence embedded in DocumentNo drives everything, so a re-run reproduces identical values.
-- Premium components are derived backwards from TotalPremium: net + 0.4% stamp + 7% VAT, with
-- TaxVat as the residual so the three always add up exactly.
UPDATE d
SET PolicyYear           = '69',
    ReferenceYear        = '69',
    ReferenceBranch      = '900',
    ReferencePre         = CASE WHEN d.DocumentType = 'ENDORSEMENT' THEN '900' END,
    PolicySequenceNo     = CONVERT(varchar(30), v.Seq),
    ReferenceNo          = CONVERT(varchar(30), v.Seq),
    PolicyBranch         = CASE v.Seq % 6
                               WHEN 0 THEN N'สำนักงานใหญ่'
                               WHEN 1 THEN N'สาขาสีลม'
                               WHEN 2 THEN N'สาขาเชียงใหม่'
                               WHEN 3 THEN N'สาขาหาดใหญ่'
                               WHEN 4 THEN N'สาขาขอนแก่น'
                               ELSE        N'สาขาพระราม 9' END,
    PolicyType           = CASE WHEN d.SourceSystem = 'VMI' THEN N'90' END,
    SaleFullName         = CASE v.Seq % 6
                               WHEN 0 THEN N'นายกิตติพงศ์ อารีย์วงศ์'
                               WHEN 1 THEN N'นางสาวสุนิสา วงศ์สว่าง'
                               WHEN 2 THEN N'นายเอกรัตน์ ธีรวุฒิ'
                               WHEN 3 THEN N'นางสาวจิราพร คงเจริญ'
                               WHEN 4 THEN N'นายภาณุวัฒน์ สุขประเสริฐ'
                               ELSE        N'นางเบญจวรรณ ทองอยู่' END,
    BrokerCode           = CASE v.Seq % 5
                               WHEN 0 THEN '701' WHEN 1 THEN '702' WHEN 2 THEN '703'
                               WHEN 3 THEN '704' ELSE '705' END,
    BrokerName           = CASE v.Seq % 5
                               WHEN 0 THEN N'บริษัท เอเซียรุ่งเรือง อินชัวรันส์ โบรกเกอร์ จำกัด'
                               WHEN 1 THEN N'บริษัท กรุงสยาม นายหน้าประกันภัย จำกัด'
                               WHEN 2 THEN N'บริษัท ธนบุรี อินชัวรันส์ โบรกเกอร์ จำกัด'
                               WHEN 3 THEN N'บริษัท ภูมิภาคประกันภัย นายหน้า จำกัด'
                               ELSE        N'บริษัท เอ็น พี ที อินชัวรันส์ โบรกเกอร์ จำกัด' END,
    PolicyNumber         = CASE WHEN d.DocumentType <> 'APPLICATION'
                                THEN CONCAT(d.SaleCode, '-69900/', v.Seq) END,
    ApplicationNumber    = CASE WHEN d.DocumentType = 'APPLICATION'
                                THEN CONCAT(d.SaleCode, '-69900/', v.Seq) END,
    PreviousPolicyNumber = CASE WHEN d.DocumentType IN ('RENEWAL', 'ENDORSEMENT')
                                THEN CONCAT(d.SaleCode, '-68900/', v.Seq - 1) END,
    EndorsementNumber    = CASE WHEN d.DocumentType = 'ENDORSEMENT' THEN CONCAT('E', v.Seq) END,
    NetPremium           = m.Net,
    Stamp                = m.Stamp,
    TaxVat               = d.TotalPremium - m.Net - m.Stamp,
    CommissionPercent    = m.Pct,
    CommissionAmount     = ROUND(m.Net * m.Pct / 100, 2)
FROM dbo.Documents d
CROSS APPLY (
    SELECT CAST(REPLACE(RIGHT(d.DocumentNo, CHARINDEX('/', REVERSE(d.DocumentNo)) - 1), '-10', '') AS int) AS Seq
) v
CROSS APPLY (SELECT ROUND(d.TotalPremium / 1.07428, 2) AS Net) n
CROSS APPLY (
    SELECT n.Net AS Net,
           ROUND(n.Net * 0.004, 2) AS Stamp,
           CAST(CASE v.Seq % 3 WHEN 0 THEN 10 WHEN 1 THEN 12 ELSE 15 END AS decimal(19,6)) AS Pct
) m;
GO

-- Self-check (REQ-1.5): objects, grant, row counts, DocumentNo prefix, and Thai round-trip.
-- Anything off THROWs, which makes `sqlcmd -b` exit non-zero and the bootstrap container fail.
IF OBJECT_ID(N'dbo.Documents', N'U') IS NULL
    THROW 51002, N'02-external-sim: hippodb.dbo.Documents is missing.', 1;
IF OBJECT_ID(N'dbo.usp_Motor_SearchDocument', N'P') IS NULL
    THROW 51002, N'02-external-sim: hippodb.dbo.usp_Motor_SearchDocument is missing.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'UX_Documents_DocumentNo' AND object_id = OBJECT_ID(N'dbo.Documents'))
    THROW 51002, N'02-external-sim: hippodb UX_Documents_DocumentNo is missing.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'pol_app')
    THROW 51002, N'02-external-sim: hippodb has no pol_app user.', 1;
IF NOT EXISTS (SELECT 1
               FROM sys.database_permissions p
               JOIN sys.database_principals u ON u.principal_id = p.grantee_principal_id
               WHERE u.name = N'pol_app' AND p.permission_name = N'EXECUTE' AND p.state = 'G'
                 AND p.major_id = OBJECT_ID(N'dbo.usp_Motor_SearchDocument'))
    THROW 51002, N'02-external-sim: pol_app lacks EXECUTE on hippodb.dbo.usp_Motor_SearchDocument.', 1;

DECLARE @rows int = (SELECT COUNT(*) FROM dbo.Documents);
IF @rows <> 200
BEGIN
    DECLARE @rowMsg nvarchar(200) = CONCAT(N'02-external-sim: hippodb seeded ', @rows, N' documents, expected 200.');
    THROW 51002, @rowMsg, 1;
END

IF EXISTS (SELECT 1 FROM dbo.Documents WHERE DocumentNo IS NULL OR DocumentNo NOT LIKE '77%')
    THROW 51002, N'02-external-sim: hippodb DocumentNo must always start with 77 (mammothdb owns 88).', 1;

-- A varchar column under a non-Thai collation, or a sqlcmd input code page that is not UTF-8, turns
-- Thai text into '?' silently. Fail here instead of shipping mojibake into every downstream test.
IF NOT EXISTS (SELECT 1 FROM dbo.Documents WHERE ShowName LIKE N'%มงคล%' AND DocumentNo LIKE N'%กธ%')
    THROW 51002, N'02-external-sim: hippodb Thai text did not round-trip (collation or sqlcmd input code page).', 1;

-- The count the SP returns for the default search. Pinned because the contract tests build on it.
DECLARE @today date = CAST(GETDATE() AS date);
DECLARE @visible int = (
    SELECT COUNT(*) FROM dbo.Documents
    WHERE SaleCode = '77001' AND PaymentStatus = 'UNPAID'
      AND ((DocumentType = 'RENEWAL' AND EndDate >= @today AND EndDate < DATEADD(month, 2, @today))
        OR (DocumentType <> 'RENEWAL' AND StartDate >= DATEADD(month, -6, @today))));
IF @visible <> 194
BEGIN
    DECLARE @visibleMsg nvarchar(200) = CONCAT(
        N'02-external-sim: hippodb default search sees ', @visible, N' documents, expected 194.');
    THROW 51002, @visibleMsg, 1;
END

PRINT N'02-external-sim: hippodb OK (200 documents, 194 in the default search window).';
GO

-- ############################################################################
-- mammothdb — simulated centerdb@mammoth (Non-Motor: FIRE | MISC)
-- Deviation REQ-1.3: one dbo.Documents table stands in for centerdb + firewebdb + miscwebdb.
-- ############################################################################
USE [mammothdb];
GO

IF OBJECT_ID(N'dbo.Documents', N'U') IS NULL
CREATE TABLE dbo.Documents (
    DocumentId           int IDENTITY PRIMARY KEY,
    SourceSystem         varchar(10)   NOT NULL,    -- FIRE | MISC
    BranchCode           varchar(3)    NULL,
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
    -- Stored so the seed can prove the Non-Motor SP neither searches nor returns it (§4/§5.2).
    LicensePlateNumber   nvarchar(100) NULL,
    PaymentStatus        varchar(10)   NULL);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'UX_Documents_DocumentNo' AND object_id = OBJECT_ID(N'dbo.Documents'))
    CREATE UNIQUE INDEX UX_Documents_DocumentNo ON dbo.Documents(DocumentNo) WHERE DocumentNo IS NOT NULL;
GO

CREATE OR ALTER PROCEDURE dbo.usp_NonMotor_SearchDocument
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
    -- Same shape as usp_Motor_SearchDocument; the four differences are marked NON-MOTOR below.
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
    IF @BranchCode IS NULL
        THROW 50004, N'BranchCode is required.', 1;
    IF @SaleCode IS NULL
        THROW 50005, N'SaleCode is required.', 1;
    IF @DocumentType COLLATE Latin1_General_BIN2 NOT IN ('APPLICATION', 'POLICY', 'RENEWAL', 'ENDORSEMENT', 'ALL')
        THROW 50001, N'Invalid DocumentType.', 1;
    -- NON-MOTOR (1/4): the allowlist is FIRE | MISC | ALL
    IF @ProductGroup COLLATE Latin1_General_BIN2 NOT IN ('FIRE', 'MISC', 'ALL')
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
    DECLARE @EffectivePaymentStatus varchar(10) = @PaymentStatus;
    IF @PaidDateFrom IS NOT NULL OR @PaidDateTo IS NOT NULL
        SET @EffectivePaymentStatus = 'PAID';

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
    CREATE TABLE #match (DocumentId int NOT NULL PRIMARY KEY);

    INSERT INTO #match (DocumentId)
    SELECT d.DocumentId
    FROM dbo.Documents d
    WHERE d.SaleCode = @SaleCode
      -- NON-MOTOR (2/4): one window for every DocumentType — StartDate within the last 6 months.
      -- The 2-month RENEWAL rule is documented for Motor only (§3 vs §4).
      AND d.StartDate >= DATEADD(month, -6, @today)
      AND (@DocumentType = 'ALL' OR d.DocumentType = @DocumentType)
      AND (@ProductGroup = 'ALL' OR d.SourceSystem = @ProductGroup)
      AND (@EffectivePaymentStatus = 'ALL' OR d.PaymentStatus = @EffectivePaymentStatus)
      AND (@PolicyNo IS NULL OR d.PolicyNumber = @PolicyNo)
      AND (@ApplicationNo IS NULL OR d.ApplicationNumber = @ApplicationNo)
      AND (@CoverageStartFrom IS NULL OR d.StartDate >= @CoverageStartFrom)
      AND (@CoverageStartTo   IS NULL OR d.StartDate <  DATEADD(day, 1, @CoverageStartTo))
      AND (@CoverageEndFrom   IS NULL OR d.EndDate   >= @CoverageEndFrom)
      AND (@CoverageEndTo     IS NULL OR d.EndDate   <  DATEADD(day, 1, @CoverageEndTo))
      AND (@PaidDateFrom IS NULL OR d.PaidDate >= @PaidDateFrom)
      AND (@PaidDateTo   IS NULL OR d.PaidDate <= @PaidDateTo)
      AND (@InsuredPattern IS NULL OR d.ShowName LIKE @InsuredPattern ESCAPE N'\')
      -- NON-MOTOR (3/4): no LicensePlateNumber in the smart search (§4).
      AND (@SearchPattern IS NULL
           OR d.DocumentNo        LIKE @SearchPattern ESCAPE N'\'
           OR d.PolicyNumber      LIKE @SearchPattern ESCAPE N'\'
           OR d.ApplicationNumber LIKE @SearchPattern ESCAPE N'\'
           OR d.EndorsementNumber LIKE @SearchPattern ESCAPE N'\');

    ------------------------------------------------- (7) materialise, then two result sets (§5, M4)
    DECLARE @TotalRows bigint = NULL, @TotalPages bigint = NULL;
    IF @CountMode = 'EXACT'
    BEGIN
        SELECT @TotalRows = COUNT_BIG(*) FROM #match;
        SET @TotalPages = CAST(CEILING(@TotalRows / CAST(@PageSize AS decimal(19,0))) AS bigint);
    END

    CREATE TABLE #page (DocumentId int NOT NULL PRIMARY KEY);

    INSERT INTO #page (DocumentId)
    SELECT d.DocumentId
    FROM #match m
    JOIN dbo.Documents d ON d.DocumentId = m.DocumentId
    ORDER BY d.DocumentNo, d.DocumentId
    OFFSET (CAST(@PageNo - 1 AS bigint) * @PageSize) ROWS
    FETCH NEXT (@PageSize + 1) ROWS ONLY;

    DECLARE @HasNextPage bit = CASE WHEN (SELECT COUNT(*) FROM #page) > @PageSize THEN 1 ELSE 0 END;

    SELECT @TotalRows                                                     AS TotalRows,
           @TotalPages                                                    AS TotalPages,
           @PageNo                                                        AS PageNo,
           @PageSize                                                      AS PageSize,
           @HasNextPage                                                   AS HasNextPage,
           CASE WHEN @PageNo > 1 THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS HasPreviousPage,
           @CountMode                                                     AS CountMode,
           CAST(6 AS int)                                                 AS SearchWindowMonths;

    SELECT TOP (@PageSize)
           CAST(N'NonMotor' AS nvarchar(20)) AS InsuranceType,
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
           -- NON-MOTOR (4/4): §5.2 declares LicensePlateNumber NULL on this side. The column exists
           -- and is seeded, so a value leaking out here would be a visible contract break.
           CAST(NULL AS nvarchar(100)) AS LicensePlateNumber,
           d.PaymentStatus
    FROM #page p
    JOIN dbo.Documents d ON d.DocumentId = p.DocumentId
    ORDER BY d.DocumentNo, d.DocumentId;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'pol_app')
    CREATE USER pol_app FOR LOGIN pol_app;
GO
GRANT EXECUTE ON dbo.usp_NonMotor_SearchDocument TO pol_app;
GO

DELETE FROM dbo.Documents;
GO

DECLARE @today date = CAST(GETDATE() AS date);

INSERT INTO dbo.Documents (SourceSystem, BranchCode, DocumentType, DocumentNo, SaleCode,
                           StartDate, EndDate, ShowName, TotalPremium, PaymentStatus, PaidDate,
                           LicensePlateNumber)
VALUES
    ('FIRE', '100', 'POLICY',      '88001-69900/' + N'อค' + '/960001', 'S001',
        DATEADD(day, -30, @today), DATEADD(day, 335, @today), N'บริษัท เจริญทรัพย์ พร็อพเพอร์ตี้ จำกัด', 18500.00, 'UNPAID', NULL, NULL),
    ('MISC', '200', 'POLICY',      '88001-69900/' + N'บต' + '/960002', 'S001',
        DATEADD(day, -60, @today), DATEADD(day, 305, @today), N'บริษัท ไทยรุ่งเรือง โลจิสติกส์ จำกัด',   32000.00, 'UNPAID', NULL, NULL),
    -- outside the 6-month window
    ('FIRE', '300', 'POLICY',      '88001-69900/' + N'อค' + '/960003', 'S001',
        DATEADD(day, -245, @today), DATEADD(day, 120, @today), N'ห้างหุ้นส่วนจำกัด สหมิตรการช่าง',      9800.00, 'UNPAID', NULL, NULL),
    -- RENEWAL, StartDate in window, EndDate far past 2 months: INCLUDED here, and the Motor rule
    -- would have dropped it — the pair below is what makes the Non-Motor window rule observable
    ('FIRE', '100', 'RENEWAL',     '88001-69900/' + N'อค' + '/960004', 'S001',
        DATEADD(day, -20, @today), DATEADD(day, 345, @today), N'บริษัท บูรพา อุตสาหกรรมอาหาร จำกัด',    3500.00, 'UNPAID', NULL, NULL),
    -- RENEWAL, StartDate out of window, EndDate inside 2 months: EXCLUDED here (Motor would keep it)
    ('MISC', '200', 'RENEWAL',     '88001-69900/' + N'บต' + '/960005', 'S001',
        DATEADD(day, -245, @today), DATEADD(day, 30, @today), N'บริษัท พนาไพร รีสอร์ท จำกัด',           4100.00, 'UNPAID', NULL, NULL),
    ('MISC', '200', 'APPLICATION', '88001-69900/' + N'บต' + '/960006', 'S001',
        DATEADD(day, -10, @today), DATEADD(day, 355, @today), N'บริษัท ศรีนครินทร์ เรียลเอสเตท จำกัด',  12800.00, 'UNPAID', NULL, NULL),
    ('FIRE', '100', 'ENDORSEMENT', '88001-69900/' + N'อค' + '/960007', 'S001',
        DATEADD(day, -15, @today), DATEADD(day, 350, @today), N'บริษัท อุดมโชค เท็กซ์ไทล์ จำกัด',        2100.00, 'PAID', DATEADD(day, -5, @today), NULL),
    ('MISC', '300', 'POLICY',      '88001-69900/' + N'บต' + '/960008', 'S001',
        DATEADD(day, -25, @today), DATEADD(day, 340, @today), N'บริษัท สินไทยพาณิชย์ จำกัด',             990.00, 'PAID', DATEADD(day, -2, @today), NULL),
    -- different SaleCode
    ('FIRE', '100', 'POLICY',      '88001-69900/' + N'อค' + '/960009', '77001',
        DATEADD(day, -12, @today), DATEADD(day, 353, @today), N'บริษัท ราชพฤกษ์ คลังสินค้า จำกัด',       450.00, 'UNPAID', NULL, NULL),
    -- literal LIKE metacharacters in ShowName + a stored plate the SP must neither search nor return
    ('MISC', '400', 'POLICY',      '88001-69900/' + N'บต' + '/960010', 'S001',
        DATEADD(day, -5, @today), DATEADD(day, 360, @today), N'ห้างหุ้นส่วนจำกัด 100%_บูรพาการช่าง',      550.00, 'UNPAID', NULL, N'8ฮฮ 8888');

-- 190 more in-window UNPAID rows (bringing mammothdb to 200 total). Same DaysBack-bound + name-pool
-- scaling as hippodb's block above.
INSERT INTO dbo.Documents (SourceSystem, BranchCode, DocumentType, DocumentNo, SaleCode,
                           StartDate, EndDate, ShowName, TotalPremium, PaymentStatus, PaidDate,
                           LicensePlateNumber)
SELECT
    -- SourceSystem is keyed on mod 5, NOT mod 2 like the DocumentNo abbreviation two lines down:
    -- ORDER BY DocumentNo sorts 'บต' (MISC-only under the old mod-2 coupling) entirely before 'อค', so
    -- tying both to the same parity made every early page a single SourceSystem once the seed grew past
    -- one page — Omitting_every_optional_parameter_applies_the_documented_defaults expects a mix.
    CASE WHEN g.value % 5 < 3 THEN 'FIRE' ELSE 'MISC' END,
    CASE g.value % 4 WHEN 0 THEN '100' WHEN 1 THEN '200' WHEN 2 THEN '300' ELSE '400' END,
    CASE WHEN g.value % 3 = 0 THEN 'ENDORSEMENT' ELSE 'POLICY' END,
    '88001-69900/' + CASE WHEN g.value % 2 = 0 THEN N'อค' ELSE N'บต' END
        + '/' + CONVERT(varchar(6), 960100 + g.value),
    'S001',
    DATEADD(day, -o.DaysBack, @today),
    DATEADD(day, 365 - o.DaysBack, @today),
    names.ShowName,
    CAST(500 + g.value * 137.25 AS decimal(19,2)),
    'UNPAID',
    NULL,
    NULL
FROM GENERATE_SERIES(1, 190) g
-- Same collision guard as hippodb: g.value % 170 = 29 would otherwise mint DaysBack 30, matching
-- axis row #1's exact StartDate -30 / EndDate +335 landmark.
CROSS APPLY (SELECT CASE WHEN g.value % 170 = 29 THEN 1 ELSE 1 + (g.value % 170) END AS DaysBack) o
-- 40-name pool so no ShowName repeats more than 5 times across the 190 generated rows
-- (190 = 40*4 + 30 -> 30 names appear 5 times, 10 appear 4 times).
JOIN (VALUES
    (0,  N'บริษัท ทองไพศาล วิศวกรรม จำกัด'),          (1,  N'ห้างหุ้นส่วนจำกัด รุ่งอรุณ การช่าง'),
    (2,  N'บริษัท สยามภัณฑ์ อุตสาหกรรม จำกัด'),        (3,  N'บริษัท เพชรบุรี ก่อสร้าง จำกัด'),
    (4,  N'ห้างหุ้นส่วนจำกัด แสงเจริญ พาณิชย์'),       (5,  N'บริษัท นวธานี ดีเวลลอปเมนท์ จำกัด'),
    (6,  N'บริษัท ไพบูลย์ศรี อุตสาหกรรมเหล็ก จำกัด'),  (7,  N'ห้างหุ้นส่วนจำกัด ทิพย์วรรณ การเกษตร'),
    (8,  N'บริษัท เมืองทอง ขนส่งด่วน จำกัด'),          (9,  N'บริษัท ศิลาแลง วัสดุก่อสร้าง จำกัด'),
    (10, N'ห้างหุ้นส่วนจำกัด บางกอกน้อย เฟอร์นิเจอร์'),(11, N'บริษัท อรัญวารี ฟู้ดส์ จำกัด'),
    (12, N'บริษัท จันทบูรณ์ ผลไม้ส่งออก จำกัด'),       (13, N'ห้างหุ้นส่วนจำกัด รัตนโกสินทร์ การพิมพ์'),
    (14, N'บริษัท วนาสวรรค์ รีสอร์ทแอนด์สปา จำกัด'),   (15, N'บริษัท เกียรตินคร ยานยนต์อะไหล่ จำกัด'),
    (16, N'ห้างหุ้นส่วนจำกัด สุขสมบูรณ์ ค้าข้าว'),     (17, N'บริษัท ปิยะมิตร แพคเกจจิ้ง จำกัด'),
    (18, N'บริษัท ธารทิพย์ น้ำดื่ม จำกัด'),            (19, N'ห้างหุ้นส่วนจำกัด โกลเด้นแลนด์ อสังหาริมทรัพย์'),
    (20, N'บริษัท วิไลวรรณ สิ่งทอ จำกัด'),             (21, N'บริษัท ชลบุรี ปิโตรเคมีภัณฑ์ จำกัด'),
    (22, N'ห้างหุ้นส่วนจำกัด อินทนิล เฟอร์นิเจอร์'),   (23, N'บริษัท สยามอินทรีย์ เกษตรภัณฑ์ จำกัด'),
    (24, N'บริษัท เพิ่มพูนทรัพย์ ลิสซิ่ง จำกัด'),      (25, N'ห้างหุ้นส่วนจำกัด นครินทร์ ขนส่ง'),
    (26, N'บริษัท ทวีทรัพย์ วัสดุอุตสาหกรรม จำกัด'),   (27, N'บริษัท หริภุญชัย เซรามิค จำกัด'),
    (28, N'ห้างหุ้นส่วนจำกัด บุญประเสริฐ การช่าง'),    (29, N'บริษัท มหานคร ซัพพลายเชน จำกัด'),
    (30, N'บริษัท อุบลรัตน์ อุตสาหกรรมกระดาษ จำกัด'),  (31, N'ห้างหุ้นส่วนจำกัด ไพลิน วัสดุภัณฑ์'),
    (32, N'บริษัท ปทุมวดี คลังสินค้า จำกัด'),          (33, N'บริษัท ระยองไพศาล ปิโตรเลียม จำกัด'),
    (34, N'ห้างหุ้นส่วนจำกัด ธนพัฒน์ การค้า'),         (35, N'บริษัท สินทวีชัย เหล็กกล้า จำกัด'),
    (36, N'บริษัท กาญจนบุรี อุตสาหกรรมไม้ จำกัด'),     (37, N'ห้างหุ้นส่วนจำกัด ศรีสยาม ขนส่งสินค้า'),
    (38, N'บริษัท วัฒนาพร เคมีภัณฑ์ จำกัด'),           (39, N'บริษัท เอกภาพ โลจิสติกส์ จำกัด')
) AS names(Idx, ShowName) ON names.Idx = g.value % 40;
GO

UPDATE d
SET PolicyYear           = '69',
    ReferenceYear        = '69',
    ReferenceBranch      = '900',
    ReferencePre         = CASE WHEN d.DocumentType = 'ENDORSEMENT' THEN '900' END,
    PolicySequenceNo     = CONVERT(varchar(30), v.Seq),
    ReferenceNo          = CONVERT(varchar(30), v.Seq),
    PolicyBranch         = CASE v.Seq % 6
                               WHEN 0 THEN N'สำนักงานใหญ่'
                               WHEN 1 THEN N'สาขาสีลม'
                               WHEN 2 THEN N'สาขาเชียงใหม่'
                               WHEN 3 THEN N'สาขาหาดใหญ่'
                               WHEN 4 THEN N'สาขาขอนแก่น'
                               ELSE        N'สาขาพระราม 9' END,
    PolicyType           = NULL,   -- product-type code is a Motor/VMI concept in this catalogue
    SaleFullName         = CASE v.Seq % 6
                               WHEN 0 THEN N'นายกิตติพงศ์ อารีย์วงศ์'
                               WHEN 1 THEN N'นางสาวสุนิสา วงศ์สว่าง'
                               WHEN 2 THEN N'นายเอกรัตน์ ธีรวุฒิ'
                               WHEN 3 THEN N'นางสาวจิราพร คงเจริญ'
                               WHEN 4 THEN N'นายภาณุวัฒน์ สุขประเสริฐ'
                               ELSE        N'นางเบญจวรรณ ทองอยู่' END,
    BrokerCode           = CASE v.Seq % 5
                               WHEN 0 THEN '701' WHEN 1 THEN '702' WHEN 2 THEN '703'
                               WHEN 3 THEN '704' ELSE '705' END,
    BrokerName           = CASE v.Seq % 5
                               WHEN 0 THEN N'บริษัท เอเซียรุ่งเรือง อินชัวรันส์ โบรกเกอร์ จำกัด'
                               WHEN 1 THEN N'บริษัท กรุงสยาม นายหน้าประกันภัย จำกัด'
                               WHEN 2 THEN N'บริษัท ธนบุรี อินชัวรันส์ โบรกเกอร์ จำกัด'
                               WHEN 3 THEN N'บริษัท ภูมิภาคประกันภัย นายหน้า จำกัด'
                               ELSE        N'บริษัท เอ็น พี ที อินชัวรันส์ โบรกเกอร์ จำกัด' END,
    PolicyNumber         = CASE WHEN d.DocumentType <> 'APPLICATION'
                                THEN CONCAT(d.SaleCode, '-69900/', v.Seq) END,
    ApplicationNumber    = CASE WHEN d.DocumentType = 'APPLICATION'
                                THEN CONCAT(d.SaleCode, '-69900/', v.Seq) END,
    PreviousPolicyNumber = CASE WHEN d.DocumentType IN ('RENEWAL', 'ENDORSEMENT')
                                THEN CONCAT(d.SaleCode, '-68900/', v.Seq - 1) END,
    EndorsementNumber    = CASE WHEN d.DocumentType = 'ENDORSEMENT' THEN CONCAT('E', v.Seq) END,
    NetPremium           = m.Net,
    Stamp                = m.Stamp,
    TaxVat               = d.TotalPremium - m.Net - m.Stamp,
    CommissionPercent    = m.Pct,
    CommissionAmount     = ROUND(m.Net * m.Pct / 100, 2)
FROM dbo.Documents d
CROSS APPLY (
    SELECT CAST(REPLACE(RIGHT(d.DocumentNo, CHARINDEX('/', REVERSE(d.DocumentNo)) - 1), '-10', '') AS int) AS Seq
) v
CROSS APPLY (SELECT ROUND(d.TotalPremium / 1.07428, 2) AS Net) n
CROSS APPLY (
    SELECT n.Net AS Net,
           ROUND(n.Net * 0.004, 2) AS Stamp,
           CAST(CASE v.Seq % 3 WHEN 0 THEN 10 WHEN 1 THEN 12 ELSE 15 END AS decimal(19,6)) AS Pct
) m;
GO

IF OBJECT_ID(N'dbo.Documents', N'U') IS NULL
    THROW 51002, N'02-external-sim: mammothdb.dbo.Documents is missing.', 1;
IF OBJECT_ID(N'dbo.usp_NonMotor_SearchDocument', N'P') IS NULL
    THROW 51002, N'02-external-sim: mammothdb.dbo.usp_NonMotor_SearchDocument is missing.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'UX_Documents_DocumentNo' AND object_id = OBJECT_ID(N'dbo.Documents'))
    THROW 51002, N'02-external-sim: mammothdb UX_Documents_DocumentNo is missing.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'pol_app')
    THROW 51002, N'02-external-sim: mammothdb has no pol_app user.', 1;
IF NOT EXISTS (SELECT 1
               FROM sys.database_permissions p
               JOIN sys.database_principals u ON u.principal_id = p.grantee_principal_id
               WHERE u.name = N'pol_app' AND p.permission_name = N'EXECUTE' AND p.state = 'G'
                 AND p.major_id = OBJECT_ID(N'dbo.usp_NonMotor_SearchDocument'))
    THROW 51002, N'02-external-sim: pol_app lacks EXECUTE on mammothdb.dbo.usp_NonMotor_SearchDocument.', 1;

DECLARE @rows int = (SELECT COUNT(*) FROM dbo.Documents);
IF @rows <> 200
BEGIN
    DECLARE @rowMsg nvarchar(200) = CONCAT(N'02-external-sim: mammothdb seeded ', @rows, N' documents, expected 200.');
    THROW 51002, @rowMsg, 1;
END

IF EXISTS (SELECT 1 FROM dbo.Documents WHERE DocumentNo IS NULL OR DocumentNo NOT LIKE '88%')
    THROW 51002, N'02-external-sim: mammothdb DocumentNo must always start with 88 (hippodb owns 77).', 1;

IF EXISTS (SELECT 1 FROM hippodb.dbo.Documents h
           JOIN mammothdb.dbo.Documents m ON m.DocumentNo = h.DocumentNo)
    THROW 51002, N'02-external-sim: a DocumentNo exists on both simulated sides — local IX_Products_DocumentNo would collide.', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.Documents WHERE ShowName LIKE N'%จำกัด%' AND DocumentNo LIKE N'%อค%')
    THROW 51002, N'02-external-sim: mammothdb Thai text did not round-trip (collation or sqlcmd input code page).', 1;

DECLARE @today date = CAST(GETDATE() AS date);
DECLARE @visible int = (
    SELECT COUNT(*) FROM dbo.Documents
    WHERE SaleCode = 'S001' AND PaymentStatus = 'UNPAID'
      AND StartDate >= DATEADD(month, -6, @today));
IF @visible <> 195
BEGIN
    DECLARE @visibleMsg nvarchar(200) = CONCAT(
        N'02-external-sim: mammothdb default search sees ', @visible, N' documents, expected 195.');
    THROW 51002, @visibleMsg, 1;
END

PRINT N'02-external-sim: mammothdb OK (200 documents, 195 in the default search window).';
GO

PRINT N'02-external-sim: OK.';
GO
