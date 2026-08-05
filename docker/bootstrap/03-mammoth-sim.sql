-- pol-core simulated upstream database mammothdb (idempotent). Runs as sa, on its OWN SQL Server
-- instance (external-sim-separate-containers) — 01-principals.sql does NOT run here, so this
-- script creates its OWN login, `mammoth_app` (sim-db-separate-logins: an upstream we do not own would
-- never hand us the same credential as VCentralPay's pol_app, so this side gets its own principal and
-- its own password). Independent of the EF migration chain — mammothdb stands in
-- for a system we do NOT own, so it must never enter PolDbContext's lineage. Contains NO secrets;
-- takes ONE sqlcmd variable (this instance's own principal password — deliberately NOT named
-- POL_APP_PASSWORD, so a caller that forgot to update fails loudly on an undefined sqlcmd variable
-- instead of quietly reusing the core credential):
--   sqlcmd -S <mammoth-server> -U sa -P <pw> -N -b -v MAMMOTH_APP_PASSWORD=<pw> -i 03-mammoth-sim.sql
-- `-b` = sqlcmd exits non-zero when the self-checks at the bottom THROW.
-- Spec: .ai/specs/external-sim-separate-containers/{requirements,design}.md (topology, REQ-1, REQ-2, REQ-3).
-- Spec: .ai/specs/products-sp-gateway/{requirements,design}.md (REQ-1, REQ-2, REQ-3 — SP contract).
-- Spec: .ai/specs/external-sim-documentno-format/{requirements,design}.md (DocumentNo layout, REQ-1-REQ-9).
-- Spec: .ai/specs/external-sim-shared-agent-network/{requirements,design}.md (shared 6-agent/broker/
--   branch roster, REQ-1-REQ-8).
-- Contract: docs/reference/vcentralpay-sp-quick-reference.pdf v1.0 (§1-§6).
--
-- WHAT THIS SIMULATES
--   mammothdb <- centerdb on server mammoth -> dbo.usp_NonMotor_SearchDocument (FIRE | MISC)
-- The simulated name deliberately differs from the real catalogue name so nobody mistakes one for
-- the other; cutover to the real upstream changes a connection string only (Server + InitialCatalog),
-- never code — the SP name and the output contract are identical. hippodb (the Motor side) lives in
-- 02-hippo-sim.sql on its own instance — see that file's header for its half of this note.
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
--   tests/Integration.Tests/SimCrossInstanceConsistencyTests.cs, not by a SQL cross-database query
--   (this file's self-check used to JOIN hippodb.dbo.Documents/mammothdb.dbo.Documents directly when
--   both lived on one instance — that query is impossible across two separate SQL Server instances
--   without a linked server, so external-sim-separate-containers REQ-3 moved it to the integration
--   test instead).
--
-- DELIBERATE DEVIATIONS (all decided in design.md, do not "fix" them here)
--   1. mammothdb uses ONE dbo.Documents table instead of the real centerdb + firewebdb + miscwebdb
--      topology (REQ-1.3). The contract we owe is the SP's OUTPUT, not mammoth's internals.
--   2. dbo.Documents has NO InsuranceType column (F6): the SP returns a constant ('NonMotor') for
--      this side. dbo.Documents also has NO BranchCode column — §5.2's 32-field output contract has
--      no such field, only ReferenceBranch (varchar(3)) — so there is nothing for a seeded column to
--      back. @BranchCode (§2) stays a required, validated input parameter (REQ-5 of
--      external-sim-realistic-branch-codes, supersedes REQ-2.11 of products-sp-gateway); if a real
--      filter is ever added it targets ReferenceBranch, not a separate column.
--   3. §5.2 spells the field `previousPolicyNumber`; this sim uses PascalCase `PreviousPolicyNumber`
--      (MINOR-8). The adapter resolves columns via GetOrdinal, which is case-insensitive.
--   4. Enum-valued parameters are compared under COLLATE Latin1_General_BIN2 = case-SENSITIVE (M5),
--      one notch stricter than the CI database default, so the SP can never be laxer than the HTTP
--      boundary and contract tests stay deterministic.
--   5. The database is created COLLATE Thai_100_CI_AS. §5.2 types are honoured exactly (DocumentNo is
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
    THROW 51002, N'03-mammoth-sim: refusing to run — this instance hosts VCentralPay. This script belongs to the mammothdb sim instance ONLY.', 1;
GO

IF DB_ID(N'mammothdb') IS NULL
    EXEC(N'CREATE DATABASE [mammothdb] COLLATE Thai_100_CI_AS');
GO

-- CUTOVER (sim-db-separate-logins): earlier revisions of this file created a pol_app LOGIN/USER here,
-- sharing VCentralPay's credential with a simulated third-party upstream. Drop that legacy principal
-- before creating the new one — USER first, LOGIN second (a login cannot be dropped while a database
-- user is still mapped to it). Idempotent: a fresh instance simply has nothing to drop.
USE [mammothdb];
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
-- it needs its own mammoth_app LOGIN — same pattern as 01-principals.sql:29-31. LOGIN is server-level,
-- so this runs in the master context (the cutover block above already switched back to [master]).
IF NOT EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = N'mammoth_app')
    CREATE LOGIN mammoth_app WITH PASSWORD = N'$(MAMMOTH_APP_PASSWORD)', CHECK_POLICY = ON;
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

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'mammoth_app')
    CREATE USER mammoth_app FOR LOGIN mammoth_app;
GO
GRANT EXECUTE ON dbo.usp_NonMotor_SearchDocument TO mammoth_app;
GO

DELETE FROM dbo.Documents;
GO

DECLARE @today date = CAST(GETDATE() AS date);

-- The hand-written rows are the axis rows: each exists to make one contract rule observable.
-- PolicySequenceNo is a plain 1-based index, 6-digit zero-padded — mammothdb's SourceSystem is
-- always FIRE/MISC, never CMI, so unlike hippodb there is no width split to defeat with a marker
-- scheme (design.md "A discovered conflict this design resolves").
INSERT INTO dbo.Documents (SourceSystem, DocumentType, PolicySequenceNo, SaleCode,
                           StartDate, EndDate, ShowName, TotalPremium, PaymentStatus, PaidDate,
                           LicensePlateNumber)
VALUES
    ('FIRE', 'POLICY',      '000001', '77001',
        DATEADD(day, -30, @today), DATEADD(day, 335, @today), N'บริษัท เจริญทรัพย์ พร็อพเพอร์ตี้ จำกัด', 18500.00, 'UNPAID', NULL, NULL),
    ('MISC', 'POLICY',      '000002', '77001',
        DATEADD(day, -60, @today), DATEADD(day, 305, @today), N'บริษัท ไทยรุ่งเรือง โลจิสติกส์ จำกัด',   32000.00, 'UNPAID', NULL, NULL),
    -- outside the 6-month window
    ('FIRE', 'POLICY',      '000003', '77001',
        DATEADD(day, -245, @today), DATEADD(day, 120, @today), N'ห้างหุ้นส่วนจำกัด สหมิตรการช่าง',      9800.00, 'UNPAID', NULL, NULL),
    -- RENEWAL, StartDate in window, EndDate far past 2 months: INCLUDED here, and the Motor rule
    -- would have dropped it — the pair below is what makes the Non-Motor window rule observable
    ('FIRE', 'RENEWAL',     '000004', '77001',
        DATEADD(day, -20, @today), DATEADD(day, 345, @today), N'บริษัท บูรพา อุตสาหกรรมอาหาร จำกัด',    3500.00, 'UNPAID', NULL, NULL),
    -- RENEWAL, StartDate out of window, EndDate inside 2 months: EXCLUDED here (Motor would keep it)
    ('MISC', 'RENEWAL',     '000005', '77001',
        DATEADD(day, -245, @today), DATEADD(day, 30, @today), N'บริษัท พนาไพร รีสอร์ท จำกัด',           4100.00, 'UNPAID', NULL, NULL),
    ('MISC', 'APPLICATION', '000006', '77001',
        DATEADD(day, -10, @today), DATEADD(day, 355, @today), N'บริษัท ศรีนครินทร์ เรียลเอสเตท จำกัด',  12800.00, 'UNPAID', NULL, NULL),
    ('FIRE', 'ENDORSEMENT', '000007', '77001',
        DATEADD(day, -15, @today), DATEADD(day, 350, @today), N'บริษัท อุดมโชค เท็กซ์ไทล์ จำกัด',        2100.00, 'PAID', DATEADD(day, -5, @today), NULL),
    ('MISC', 'POLICY',      '000008', '77001',
        DATEADD(day, -25, @today), DATEADD(day, 340, @today), N'บริษัท สินไทยพาณิชย์ จำกัด',             990.00, 'PAID', DATEADD(day, -2, @today), NULL),
    -- Ordinary POLICY row under the shared roster's default agent 77001 — same code as every other
    -- mammothdb axis row now that both sides draw from one roster.
    ('FIRE', 'POLICY',      '000009', '77001',
        DATEADD(day, -12, @today), DATEADD(day, 353, @today), N'บริษัท ราชพฤกษ์ คลังสินค้า จำกัด',       450.00, 'UNPAID', NULL, NULL),
    -- literal LIKE metacharacters in ShowName + a stored plate the SP must neither search nor return
    ('MISC', 'POLICY',      '000010', '77001',
        DATEADD(day, -5, @today), DATEADD(day, 360, @today), N'ห้างหุ้นส่วนจำกัด 100%_บูรพาการช่าง',      550.00, 'UNPAID', NULL, N'8ฮฮ 8888');

-- 190 more in-window UNPAID rows (bringing mammothdb to 200 total). Same DaysBack-bound + name-pool
-- scaling as hippodb's block above.
INSERT INTO dbo.Documents (SourceSystem, DocumentType, PolicySequenceNo, SaleCode,
                           StartDate, EndDate, ShowName, TotalPremium, PaymentStatus, PaidDate,
                           LicensePlateNumber)
SELECT
    -- SourceSystem is keyed on mod 5, NOT mod 3 like DocumentType (ENDORSEMENT vs POLICY) two lines
    -- down: the UPDATE below derives Abbrev from DocumentType, so tying SourceSystem to the same
    -- modulus would make ORDER BY DocumentNo sort every early page into a single SourceSystem again —
    -- Omitting_every_optional_parameter_applies_the_documented_defaults expects a mix.
    CASE WHEN g.value % 5 < 3 THEN 'FIRE' ELSE 'MISC' END,
    CASE WHEN g.value % 3 = 0 THEN 'ENDORSEMENT' ELSE 'POLICY' END,
    -- REQ-3.4: 100 + g.value, zero-padded to 6 digits. mammothdb's SourceSystem is always FIRE/MISC
    -- (never CMI), so unlike hippodb's generated-row expression there is no width CASE to duplicate.
    RIGHT('000000' + CONVERT(varchar(6), 100 + g.value), 6),
    -- SaleCode is an agent's own code, so a 6-agent roster needs 6 distinct codes, not one shared
    -- literal (see the SaleFullName CASE in the UPDATE below, keyed off this same code). Keyed on
    -- names.Idx, not g.value, so every ShowName always sells through the same one agent — same
    -- contiguous 7/7/7/6/6/7 split of the 40-name pool below as hippodb's block above.
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
SET PolicyYear           = '26',
    ReferenceYear        = '26',
    -- ReferenceBranch/PolicyBranch is one branch office, code + name — the same paired-set rule as
    -- SaleCode/SaleFullName. This is the shared 6-agent/broker/branch master roster
    -- (external-sim-shared-agent-network), duplicated verbatim from hippodb's block above — both
    -- databases represent one insurance company's sales network. Sourced from the rb CROSS APPLY
    -- below (not an inline CASE here) because DocumentNo/PolicyNumber/etc. also need this value and a
    -- SET clause cannot see another SET clause's result within the same UPDATE.
    ReferenceBranch      = rb.ReferenceBranch,
    ReferencePre         = CASE WHEN d.DocumentType = 'ENDORSEMENT' THEN '900' END,
    ReferenceNo          = d.PolicySequenceNo,
    PolicyBranch         = CASE WHEN d.SaleCode IN ('77001', '77006') THEN N'สำนักงานใหญ่'
                                 WHEN d.SaleCode =    '77002'          THEN N'สาขาสีลม'
                                 WHEN d.SaleCode =    '77003'          THEN N'สาขาเชียงใหม่'
                                 WHEN d.SaleCode =    '77004'          THEN N'สาขาหาดใหญ่'
                                 WHEN d.SaleCode =    '77005'          THEN N'สาขาขอนแก่น' END,
    PolicyType           = NULL,   -- product-type code is a Motor/VMI concept in this catalogue
    -- SaleCode is the agent's own code; SaleFullName is that same agent's name — one code always names
    -- the same agent, so this is keyed on d.SaleCode, not the running number. This is the shared
    -- 6-agent/broker/branch master roster (external-sim-shared-agent-network), duplicated verbatim
    -- from hippodb's block above — both databases represent one insurance company's sales network.
    SaleFullName         = CASE d.SaleCode
                               WHEN '77001' THEN N'นายกิตติพงศ์ อารีย์วงศ์'
                               WHEN '77002' THEN N'นางสาวสุนิสา วงศ์สว่าง'
                               WHEN '77003' THEN N'นายเอกรัตน์ ธีรวุฒิ'
                               WHEN '77004' THEN N'นางสาวจิราพร คงเจริญ'
                               WHEN '77005' THEN N'นายภาณุวัฒน์ สุขประเสริฐ'
                               WHEN '77006' THEN N'นางเบญจวรรณ ทองอยู่' END,
    -- BrokerCode/BrokerName is the broker firm an agent works under — one agent always sits under the
    -- same broker, so this is keyed on d.SaleCode too. Same shared master roster, duplicated verbatim
    -- from hippodb's block above.
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
    -- Non-Motor's ENDORSEMENT variant prefixing '1-' before the whole base instead of appending a
    -- trailing digit (REQ-1.2 — a different position from Motor's trailing '1' in REQ-1.3, do not swap).
    DocumentNo           = CASE WHEN d.DocumentType = 'ENDORSEMENT'
                                THEN CONCAT('1-26', rb.ReferenceBranch, '/', ab.Abbrev, '/', d.PolicySequenceNo)
                                ELSE CONCAT('26', rb.ReferenceBranch, '/', ab.Abbrev, '/', d.PolicySequenceNo) END,
    PolicyNumber         = CASE WHEN d.DocumentType <> 'APPLICATION'
                                THEN CONCAT(d.SaleCode, '-26', rb.ReferenceBranch, '/', d.PolicySequenceNo) END,
    ApplicationNumber    = CASE WHEN d.DocumentType = 'APPLICATION'
                                THEN CONCAT(d.SaleCode, '-26', rb.ReferenceBranch, '/', d.PolicySequenceNo) END,
    -- PrevYear is PolicyYear - 1 ('26' -> '25', a fixed literal per REQ-4.3); the running number is
    -- re-derived as an integer, decremented, and re-padded to 6 digits (REQ-9.2).
    PreviousPolicyNumber = CASE WHEN d.DocumentType IN ('RENEWAL', 'ENDORSEMENT')
                                THEN CONCAT(d.SaleCode, '-25', rb.ReferenceBranch, '/',
                                     RIGHT('000000' + CONVERT(varchar(6), CAST(d.PolicySequenceNo AS int) - 1), 6)) END,
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
-- Abbrev(SourceSystem, DocumentType) — mammothdb is Non-Motor-only (FIRE|MISC), so only the
-- Non-Motor half of design.md's table applies: POLICY/RENEWAL share 'POL' (REQ-2.7),
-- APPLICATION -> 'APP', ENDORSEMENT -> 'END'.
CROSS APPLY (
    SELECT CASE WHEN d.DocumentType = 'ENDORSEMENT' THEN 'END'
                WHEN d.DocumentType = 'APPLICATION' THEN 'APP'
                ELSE 'POL' END AS Abbrev
) ab
CROSS APPLY (SELECT ROUND(d.TotalPremium / 1.07428, 2) AS Net) n
CROSS APPLY (
    SELECT n.Net AS Net,
           ROUND(n.Net * 0.004, 2) AS Stamp,
           CAST(CASE CAST(d.PolicySequenceNo AS int) % 3 WHEN 0 THEN 10 WHEN 1 THEN 12 ELSE 15 END AS decimal(19,6)) AS Pct
) m;
GO

-- Collation check by name (see the hippodb self-check above for why the Thai round-trip alone
-- cannot catch a stale Thai_CI_AS database — same reasoning applies here).
IF ISNULL(CONVERT(nvarchar(128), DATABASEPROPERTYEX(N'mammothdb', N'Collation')), N'') <> N'Thai_100_CI_AS'
    THROW 51002, N'03-mammoth-sim: mammothdb collation is not Thai_100_CI_AS.', 1;
IF OBJECT_ID(N'dbo.Documents', N'U') IS NULL
    THROW 51002, N'03-mammoth-sim: mammothdb.dbo.Documents is missing.', 1;
IF OBJECT_ID(N'dbo.usp_NonMotor_SearchDocument', N'P') IS NULL
    THROW 51002, N'03-mammoth-sim: mammothdb.dbo.usp_NonMotor_SearchDocument is missing.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'UX_Documents_DocumentNo' AND object_id = OBJECT_ID(N'dbo.Documents'))
    THROW 51002, N'03-mammoth-sim: mammothdb UX_Documents_DocumentNo is missing.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'mammoth_app')
    THROW 51002, N'03-mammoth-sim: mammothdb has no mammoth_app user.', 1;
IF NOT EXISTS (SELECT 1
               FROM sys.database_permissions p
               JOIN sys.database_principals u ON u.principal_id = p.grantee_principal_id
               WHERE u.name = N'mammoth_app' AND p.permission_name = N'EXECUTE' AND p.state = 'G'
                 AND p.major_id = OBJECT_ID(N'dbo.usp_NonMotor_SearchDocument'))
    THROW 51002, N'03-mammoth-sim: mammoth_app lacks EXECUTE on mammothdb.dbo.usp_NonMotor_SearchDocument.', 1;
-- Cutover completeness (sim-db-separate-logins): the legacy shared principal must be gone from BOTH
-- levels, or this instance would still accept VCentralPay's credential.
IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'pol_app')
    THROW 51002, N'03-mammoth-sim: mammothdb still has a pol_app user (cutover to mammoth_app did not complete).', 1;
IF EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = N'pol_app')
    THROW 51002, N'03-mammoth-sim: this instance still has a pol_app LOGIN (cutover to mammoth_app did not complete).', 1;

DECLARE @rows int = (SELECT COUNT(*) FROM dbo.Documents);
IF @rows <> 200
BEGIN
    DECLARE @rowMsg nvarchar(200) = CONCAT(N'03-mammoth-sim: mammothdb seeded ', @rows, N' documents, expected 200.');
    THROW 51002, @rowMsg, 1;
END

-- Roster-completeness (REQ-4.3): the shared 6-agent master roster must actually be fully present,
-- not merely true by the current data's coincidence.
IF (SELECT COUNT(DISTINCT SaleCode) FROM dbo.Documents) <> 6
BEGIN
    THROW 51002, N'03-mammoth-sim: mammothdb expected exactly 6 distinct SaleCode values (the shared master roster).', 1;
END

-- ShowName->SaleCode pairing invariant (REQ-1.5) — the one REQ-1.5 invariant not already guaranteed
-- by construction (every other identity column is set by a SaleCode-keyed CASE/CROSS APPLY).
IF EXISTS (SELECT 1 FROM dbo.Documents GROUP BY ShowName HAVING COUNT(DISTINCT SaleCode) > 1)
BEGIN
    THROW 51002, N'03-mammoth-sim: mammothdb a ShowName resolves to more than one SaleCode (ShowName->SaleCode pairing invariant violated).', 1;
END

IF EXISTS (SELECT 1 FROM dbo.Documents
           WHERE DocumentNo IS NULL OR (DocumentNo NOT LIKE '26%' AND DocumentNo NOT LIKE '1-26%'))
    THROW 51002, N'03-mammoth-sim: mammothdb DocumentNo must always start with 26 (PolicyYear literal, plain or 1- ENDORSEMENT prefix).', 1;

-- mammothdb's DocumentNo is ASCII-only under REQ-2.4-2.6 (POL/APP/END), so the Thai round-trip check
-- retargets at PolicyBranch instead (REQ-6.5) — hippodb's equivalent check stays on DocumentNo
-- because Motor keeps Thai abbreviations (REQ-2.1-2.3).
IF NOT EXISTS (SELECT 1 FROM dbo.Documents WHERE ShowName LIKE N'%จำกัด%' AND PolicyBranch LIKE N'%สาขา%')
    THROW 51002, N'03-mammoth-sim: mammothdb Thai text did not round-trip (collation or sqlcmd input code page).', 1;

DECLARE @today date = CAST(GETDATE() AS date);
DECLARE @visible int = (
    SELECT COUNT(*) FROM dbo.Documents
    WHERE SaleCode = '77001' AND PaymentStatus = 'UNPAID'
      AND StartDate >= DATEADD(month, -6, @today));
IF @visible <> 40
BEGIN
    DECLARE @visibleMsg nvarchar(200) = CONCAT(
        N'03-mammoth-sim: mammothdb default search sees ', @visible, N' documents, expected 40.');
    THROW 51002, @visibleMsg, 1;
END

PRINT N'03-mammoth-sim: mammothdb OK (200 documents, 40 in the default search window).';
GO
