# Handoff log — external-sim-shared-agent-network

> Rolling handoff between orchestrated teammates. Each teammate appends ONE section
> below when their task is done (or when blocked), newest at the bottom. Read every
> prior section (plus requirements.md / design.md / tasks.md) before starting your task.

## How to use this file

- Read this whole file (plus requirements.md / design.md / tasks.md) before starting.
- When your task is done, append a new `## Task N — <name> — done by <your name>`
  section: state what changed (files), exact commands run + observed output, any
  deviation from design.md and why, and anything the next teammate needs to know
  (esp. exact literal values read from the live DB — never hand-derive).
- Do NOT edit a previous teammate's section. Append only.
- Do NOT commit or push (repo rule, `.ai/shared/TASK_PROTOCOL.md` line 119) — the lead
  reviews and commits after all 3 tasks land.

## Known state before Task 1 starts

- `docker/bootstrap/02-external-sim.sql` and both Integration.Tests files already carry
  **uncommitted** working-tree changes from the predecessor spec
  (`external-sim-documentno-format`, all 3 tasks `[x]` done, evidence recorded in that
  spec's own `tasks.md`/`HANDOFF.md`). That predecessor work is NOT part of this spec —
  leave it as-is, build this spec's edits on top of the current working tree, do not try
  to revert or re-derive it.
- Branch `data/expand-sim-seed-200-per-side`, PR #160 (open). 5 prior "fix(seed):"
  commits already on this branch/pushed — none of those are this spec's work either.
- design.md already contains the exact master roster table, axis re-keying map (with
  rationale), generated-row CASE retarget snippet, and the cross-db self-check SQL
  verbatim — copy from there, do not re-derive.

## Task 1 — hippodb — done by sim-hippodb

Files changed: `docker/bootstrap/02-external-sim.sql` only (hippodb block + file header;
mammothdb's block untouched, confirmed by `grep -n "90001"` still showing all 15
occurrences inside mammothdb's block only, none in hippodb's).

What changed:
- File header: added a `Spec: .ai/specs/external-sim-shared-agent-network/...` reference
  line and a new "AGENT / BROKER / BRANCH NETWORK" section describing the shared 6-agent
  network one insurance company operates across both simulated databases (REQ-8.1/8.2).
  The pre-existing DELIBERATE DEVIATIONS / DocumentNo-format narrative was left untouched
  (REQ-8.3) — there was no prior "two disjoint rosters" paragraph in the file header
  itself to rewrite (that narrative lived only in inline comments, handled below), so
  REQ-8.1 was satisfied by adding the accurate description rather than literally
  rewriting a contradictory one.
- Removed the `'90001'` arm from all 5 hippodb identity `CASE`/`CROSS APPLY`
  expressions: `PolicyBranch`, `SaleFullName`, `BrokerCode`, `BrokerName`, and the `rb`
  CROSS APPLY that derives `ReferenceBranch`. Each now ends cleanly after the `'77006'`
  arm — no dead 7th arm.
- Re-keyed axis row 10 (`PolicySequenceNo='800010'`) from `SaleCode='90001'` to
  `SaleCode='77002'`. Rewrote its inline comment from "Different SaleCode: proves
  @SaleCode is a hard scope axis, not a hint" to an ordinary-row note.
- Rewrote the inline comment above the `SaleFullName` CASE (previously explained
  `'90001'` as "mammothdb's own default agent... foreign-SaleCode axis probe row") to
  describe the shared master roster instead.
- Added hippodb's own copies of the two new self-checks to hippodb's self-check block
  (placed right after the existing row-count check, before the `DocumentNo` prefix
  check): roster-completeness (`COUNT(DISTINCT SaleCode) <> 6` → THROW 51002) and
  `ShowName`→`SaleCode` invariant (`GROUP BY ShowName HAVING COUNT(DISTINCT SaleCode) >
  1` → THROW 51002).

Final hippodb identity CASE expressions (copy verbatim into mammothdb's block per
design.md's "duplicate hippodb's master CASE expressions byte-identical" instruction —
these are the exact, final, post-edit text, read from the file, not re-derived):

```sql
PolicyBranch         = CASE WHEN d.SaleCode IN ('77001', '77006') THEN N'สำนักงานใหญ่'
                             WHEN d.SaleCode =    '77002'          THEN N'สาขาสีลม'
                             WHEN d.SaleCode =    '77003'          THEN N'สาขาเชียงใหม่'
                             WHEN d.SaleCode =    '77004'          THEN N'สาขาหาดใหญ่'
                             WHEN d.SaleCode =    '77005'          THEN N'สาขาขอนแก่น' END,
...
SaleFullName         = CASE d.SaleCode
                           WHEN '77001' THEN N'นายกิตติพงศ์ อารีย์วงศ์'
                           WHEN '77002' THEN N'นางสาวสุนิสา วงศ์สว่าง'
                           WHEN '77003' THEN N'นายเอกรัตน์ ธีรวุฒิ'
                           WHEN '77004' THEN N'นางสาวจิราพร คงเจริญ'
                           WHEN '77005' THEN N'นายภาณุวัฒน์ สุขประเสริฐ'
                           WHEN '77006' THEN N'นางเบญจวรรณ ทองอยู่' END,
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
...
CROSS APPLY (
    SELECT CASE WHEN d.SaleCode IN ('77001', '77006') THEN '900'
                WHEN d.SaleCode =    '77002'          THEN '901'
                WHEN d.SaleCode =    '77003'          THEN '902'
                WHEN d.SaleCode =    '77004'          THEN '903'
                WHEN d.SaleCode =    '77005'          THEN '904' END AS ReferenceBranch
) rb
```

Commands run + observed output (all commands, verbatim, exact output — see tasks.md
item 1's `Evidence:` block for the full transcript including the deliberate-break drill):
- `docker compose up pol-db-init` (final, clean state) → `02-external-sim: hippodb OK
  (200 documents, 42 in the default search window).` / `02-external-sim: mammothdb OK
  (200 documents, 39 in the default search window).` / `02-external-sim: OK.`, exit 0,
  no THROW. (mammothdb's block is still on its own pre-existing `90001`-`90006` roster
  at this point — that is task 2's job, not a regression from this task.)
- Live query (`docker exec pol-db /opt/mssql-tools18/bin/sqlcmd -d hippodb`) confirmed
  the 6 distinct `SaleCode` rows match design.md's master roster table exactly (values
  reproduced in the CASE expressions above — these ARE the live values, not
  hand-derived).
- Row 10 live values: `SaleCode='77002'`, `ReferenceBranch='901'` (unchanged),
  `PolicyBranch='สาขาสีลม'`, `DocumentNo='69901/กธ/800010'` (unchanged),
  `PolicyNumber='77002-69901/800010'` (CHANGED from the old `90001-69901/800010` — see
  deviation note below), `DocumentType='POLICY'`, `ShowName='นายเอกรัตน์ ธีรวุฒิ'`,
  `TotalPremium=4100.00`, `PaymentStatus='UNPAID'`, `PaidDate=NULL`,
  `LicensePlateNumber='1ฎฎ 1010'`, `BranchCode='100'`.
- `ShowName`→`SaleCode` collision check (F-5): 0 rows — row 10's `ShowName` does not
  collide with any other `SaleCode` in hippodb.
- Deliberate-break-then-revert drill: roster-completeness threshold flipped `<>6`→`<>5`
  → THROW 51002 fired with the expected message, exit 1; reverted. `ShowName`→`SaleCode`
  condition flipped `>1`→`>0` → THROW 51002 fired with the expected message, exit 1;
  reverted. Both reverts verified byte-identical via `diff` against a pre-drill backup
  before the final clean rerun.

Deviations from design.md (both minor, both documented in tasks.md item 1's Evidence
block too):
1. design.md's axis-row re-keying map claims row 10's `PolicyNumber` is unchanged by
   the re-key, same as `ReferenceBranch`/`DocumentNo`. This is only true for
   `ReferenceBranch`/`DocumentNo` — `PolicyNumber`'s own formula embeds `d.SaleCode` as
   its literal prefix (`CONCAT(d.SaleCode, '-69', rb.ReferenceBranch, '/',
   d.PolicySequenceNo)`), so it necessarily changed from `90001-69901/800010` to
   `77002-69901/800010`. **Task 3: do not assume row 10's old `PolicyNumber` — if any
   assertion ever needs it, the live value is `77002-69901/800010`.** No current test
   hardcodes this (only `MotorSide.ForeignSaleCode`/`ForeignSaleCodeSeq` reference row
   10, both removed per REQ-5.3).
2. design.md's roster-completeness/`ShowName`→`SaleCode` self-check snippets use a
   generic THROW message with no db name. Adapted both for hippodb by inserting
   `hippodb` into the message text, matching the file's existing convention that every
   other self-check names its database explicitly. **Task 2: do the same for
   mammothdb** (insert `mammothdb` into the equivalent messages) for consistency.

No other deviations. Task 2 (mammothdb) can proceed: duplicate the CASE expressions
above verbatim into mammothdb's block, re-key its axis rows, retarget its generated-row
`SaleCode` CASE, add the cross-db identity self-check plus mammothdb's own
roster-completeness/`ShowName`→`SaleCode` self-checks, and realign its default-search
probe — per design.md and tasks.md item 2.

## Task 2 — mammothdb — done by sim-mammothdb

Files changed: `docker/bootstrap/02-external-sim.sql` only (mammothdb's block; hippodb's
block untouched — final `grep -n "90001\|90002\|90003\|90004\|90005\|90006"` shows zero
occurrences anywhere in the file, confirming the retired roster is gone from both sides).

What changed:
- Axis rows 1-8 and 10 (`PolicySequenceNo` `000001`-`000008`, `000010`): `SaleCode`
  literal `'90001'` → `'77001'`. Row 9 (`000009`) kept its value `'77001'` unchanged;
  its comment ("-- different SaleCode") rewritten to describe it as an ordinary row now
  that every mammothdb axis row shares the same code.
- Generated-row `SaleCode` CASE (the `names.Idx`-keyed block): retargeted
  `'90001'`-`'90006'` → `'77001'`-`'77006'`, same bucket boundaries, byte-identical to
  hippodb's own generated-row CASE.
- Replaced all 5 mammothdb identity `CASE`/`CROSS APPLY` expressions
  (`PolicyBranch`, `SaleFullName`, `BrokerCode`, `BrokerName`, the `rb` CROSS APPLY for
  `ReferenceBranch`) with hippodb's post-task-1 expressions, copied byte-identical from
  HANDOFF.md's Task 1 section, keyed the same way (`d.SaleCode`). Rewrote the inline
  comments above each (previously explaining the two-roster overlap / "foreign SaleCode
  axis probe" narrative) to describe the shared master roster duplicated from hippodb's
  block instead.
- Added the new cross-database identity self-check (design.md's `EXCEPT`-based
  snippet verbatim, `THROW 51002`, names the offending `SaleCode`) immediately after
  the existing `DocumentNo`-uniqueness cross-db check in mammothdb's self-check block.
- Added mammothdb's own copies of the roster-completeness and `ShowName`→`SaleCode`
  self-checks (same pattern as hippodb's task 1, `mammothdb` inserted into both THROW
  messages), placed right after the row-count check, before the `DocumentNo` prefix
  check — mirrors hippodb's placement exactly.
- Changed the default-search self-check's probe `SaleCode` from `'90001'` to `'77001'`
  and updated the expected-count literal + `PRINT` message from the design-time
  placeholder `39` to the freshly-measured `40` (see Evidence below — measured live,
  not guessed).

Commands run + observed output (all verbatim):
- `docker compose up pol-db-init` (first run, after all SQL edits but before fixing the
  placeholder count) → `mammothdb default search sees 40 documents, expected 39.`,
  `Msg 51002`, exit 1 — this told us the live count, not a bug; fixed the literal to
  `40` and reran.
- `docker compose up pol-db-init` (after the fix) → `02-external-sim: hippodb OK (200
  documents, 42 in the default search window).` / `02-external-sim: mammothdb OK (200
  documents, 40 in the default search window).` / `02-external-sim: OK.`, exit 0, no
  THROW.
- Live query (`docker exec pol-db /opt/mssql-tools18/bin/sqlcmd -d mammothdb`)
  confirmed all 6 master `SaleCode`s resolve identically to hippodb's own table (full
  side-by-side `SELECT DISTINCT SaleCode, ReferenceBranch, PolicyBranch, SaleFullName,
  BrokerCode, BrokerName` on both databases) — values match the "Master roster" table
  in design.md exactly, same as hippodb.
- `ShowName`→`SaleCode` collision query on mammothdb: 0 rows (F-5 live-confirmed for
  this side too).
- Row counts: `mammothdb.dbo.Documents` = 200, `hippodb.dbo.Documents` = 200.
  `DocumentNo` invariants: 0 rows violate the `26%`/`1-26%` prefix rule, 0 duplicate
  `DocumentNo` within mammothdb, 0 `DocumentNo` shared across hippodb/mammothdb.
- Deliberate-break-then-revert drill (all 3 new/changed self-checks): (1)
  roster-completeness `<>6`→`<>5` → THROW fired (`mammothdb expected exactly 6 distinct
  SaleCode values...`), exit 1; reverted. (2) `ShowName`→`SaleCode` `>1`→`>0` → THROW
  fired (`mammothdb a ShowName resolves to more than one SaleCode...`), exit 1;
  reverted. (3) new cross-db identity check — temporarily changed mammothdb's
  `BrokerCode` `WHEN '77003' THEN '703'` to `'799'` → THROW fired (`agent identity
  drifted between hippodb and mammothdb for SaleCode 77003 —
  SaleFullName/BrokerCode/BrokerName/ReferenceBranch/PolicyBranch must match on both
  sides.`), exit 1; reverted. All three reverts confirmed byte-identical to a
  pre-drill backup via `diff` (exit 0) before the final clean rerun (green, shown
  above).

**Live values Task 3 (Integration.Tests) needs — read from the live reseeded database,
not hand-derived, per REQ-7.6:**
- mammothdb default-search visible count (`NonMotorSide.TotalRows` in
  `SpDocumentContractTests.cs` / `Side.TotalRows` in
  `SpDocumentGatewayIntegrationTests.cs`): **40** (was 39). `TotalPages` at `PageSize=25`
  stays **2**. `LastPageRows`: **15** (= 40 - 25), queried live via the same predicate as
  the self-check's `@visible`, not hand-derived.
- mammothdb axis row 1 (`PolicySequenceNo='000001'`, the row
  `AxisPolicyNumber`/`AxisDocumentNo` target): `SaleCode='77001'`,
  `ReferenceBranch='900'`, `DocumentNo='26900/POL/000001'`,
  `PolicyNumber='77001-26900/000001'`. So `AxisReferenceBranch: "900"` (was `"901"`),
  `AxisPolicyNumber: "77001-26900/000001"` (was `"90001-26901/000001"`),
  `AxisDocumentNo: "26900/POL/000001"` (was `"26901/POL/000001"`). The comment on this
  field ("SaleCode 90001 -> broker 702 -> branch 901") needs updating to "SaleCode
  77001 -> broker 701 -> branch 900".
- mammothdb row 7 (`PolicySequenceNo='000007'`, PAID ENDORSEMENT, the row
  `PaidPolicyNumber` targets): `PolicyNumber='77001-26900/000007'`,
  `PaidDate='2026-07-28'` (relative to today = 2026-08-02). So
  `PaidPolicyNumber: "77001-26900/000007"` (was `"90001-26901/000007"`).
  `PolicyNumber` for the other PAID row (000008) similarly follows the same
  `77001-26900/000008` pattern if any test needs it — not currently referenced by name
  in either test file (grepped both), but re-verify live if a future assertion adds it.
  ENDORSEMENT's own `DocumentNo` is `1-26900/END/000007` (unchanged shape, new branch).
- `NonMotorSide.PolicyYearBranch` (`SpDocumentContractTests.cs`): confirmed **`"26900"`**
  live (design.md predicted this exactly) — was `"26901"`.
- `NonMotorSide.SaleCode`: `"77001"` (was `"90001"`) — REQ-6.1.
- `SeqPrefix`/`SeqPrefixHits`/`RenewalDroppedSeqs`/`RenewalKeptSeq`/`ApplicationSeq`/
  `PolicySeq`/`LikeMetacharacterSeq`/`PaidSeqs` in `SpDocumentContractTests.cs`: these
  key off `PolicySequenceNo` (the `000001`-`000010` tails), which this task did NOT
  change — only `SaleCode` and its dependent identity fields changed on those rows. The
  seq literals themselves (`"00000"`, `["000001","000002","000004","000006"]`, etc.)
  are expected to still be correct as-is, but task 3 should still re-verify live per
  REQ-7.6's discipline rather than trust this note alone — this task did not run those
  specific assertions.
- `Side.ForeignSaleCode`/`ForeignSaleCodeSeq` fields (both `MotorSide` and
  `NonMotorSide`): still present in both test files, unchanged by this task (task 2 is
  SQL-only) — task 3 removes them per REQ-5.3.

Deviations from design.md: none. Every element (axis re-key map, generated-row CASE
retarget, duplicated master CASE expressions, cross-db `EXCEPT` self-check, per-side
roster-completeness/`ShowName`→`SaleCode` self-checks) was applied exactly as
design.md specified, with `mammothdb` inserted into the two adapted THROW messages per
task 1's established convention (same deviation type task 1 already flagged, applied
consistently here — not a new deviation). The only value not fixed in design.md and
filled in here was the default-search count itself (design.md explicitly deferred this
to "freshly measured" per REQ-6.2) — measured live as **40**, not assumed.

Task 3 (Integration.Tests) can proceed: remove `Side.ForeignSaleCode`/
`ForeignSaleCodeSeq`, add the prefix/substring probe constants, redesign
`Sale_code_is_an_exact_scope_axis`, and re-pin every literal listed above (plus sweep
both files for any other literal REQ-7.6 covers) against the live database — the seed
data described here is the final state both test files must match.

## Task 3 — Integration.Tests — done by sim-tests

Files changed: `tests/Integration.Tests/SpDocumentContractTests.cs`,
`tests/Integration.Tests/SpDocumentGatewayIntegrationTests.cs` only. No SQL file
touched (task 1/2 territory) — confirmed the file already had zero `90001`-`90006`
occurrences anywhere before starting (`grep -n "90001\|90002\|90003\|90004\|90005\|90006"
docker/bootstrap/02-external-sim.sql`, zero hits) and reran `docker compose up
pol-db-init` clean first (`hippodb OK (200 documents, 42...)` / `mammothdb OK (200
documents, 40...)` / `OK.`, exit 0) so every literal below was read against the exact
state task 2 left behind, not a stale local DB.

What changed:
- `SpDocumentContractTests.cs`: removed `Side.ForeignSaleCode`/`ForeignSaleCodeSeq`
  from the record and both `MotorSide`/`NonMotorSide` instances. Added two
  class-level `const string` probes (not per-`Side` fields, since the roster is now
  shared): `SaleCodePrefixProbe = "7700"`, `SaleCodeSubstringProbe = "7001"`. Replaced
  `Sale_code_is_an_exact_scope_axis` with `Sale_code_does_not_match_by_prefix_or_substring`
  — `Assert.Empty` on both probes, both sides (4 assertions total across the
  `[Theory]`). `NonMotorSide`: `SaleCode` `"90001"`->`"77001"`, `PolicyYearBranch`
  `"26901"`->`"26900"`, `TotalRows` 39->40, `LastPageRows` 14->15 (`TotalPages` stayed
  2). Header comment's `39`->`40`. `SeqPrefixHits`
  `["000001","000002","000004","000006"]` -> `["000001","000002","000004","000006","000009"]`
  (see "What I found beyond the plan" below — this one was NOT anticipated by
  design.md or Task 2's handoff notes).
- `SpDocumentGatewayIntegrationTests.cs`: `NonMotorSide.SaleCode` `"90001"`->`"77001"`,
  `TotalRows` 39->40, `AxisPolicyNumber` `"90001-26901/000001"`->
  `"77001-26900/000001"`, `AxisDocumentNo` `"26901/POL/000001"`->
  `"26900/POL/000001"`, `AxisReferenceBranch` `"901"`->`"900"` (comment ->
  `"SaleCode 77001 -> broker 701 -> branch 900"`), `PaidPolicyNumber`
  `"90001-26901/000007"`->`"77001-26900/000007"`. Swept `Assert.Equal(2L,
  result.Page.TotalPages)` — re-verified still correct (both 42/25 and 40/25 ceiling
  to 2), left as-is.

Live values read (all via `docker exec pol-db /opt/mssql-tools18/bin/sqlcmd`, either
calling the actual stored procedures or querying `dbo.Documents` directly — never
hand-derived):
- `EXEC dbo.usp_NonMotor_SearchDocument @BranchCode='100', @SaleCode='77001'` (page 1)
  -> `TotalRows=40 TotalPages=2`; page 2 (`@PageNo=2`) -> 15 item rows counted
  directly from the raw result set. Confirms `TotalRows=40`, `TotalPages=2`,
  `LastPageRows=15`.
- `SELECT PolicySequenceNo, SaleCode, ReferenceBranch, DocumentNo, PolicyNumber,
  DocumentType, PaymentStatus, PaidDate, SourceSystem FROM dbo.Documents WHERE
  PolicySequenceNo IN ('000001','000007','000008','000009','000010')` on mammothdb ->
  row `000001`: `SaleCode=77001 ReferenceBranch=900 DocumentNo='26900/POL/000001'
  PolicyNumber='77001-26900/000001'`; row `000007` (PAID ENDORSEMENT):
  `PolicyNumber='77001-26900/000007' PaidDate=2026-07-28
  DocumentNo='1-26900/END/000007'`. Both match HANDOFF's Task 2 predictions exactly.
- `SELECT PolicySequenceNo, DocumentType, SourceSystem, StartDate, EndDate, ShowName,
  PaymentStatus, PaidDate, LicensePlateNumber FROM dbo.Documents WHERE
  PolicySequenceNo BETWEEN '000001' AND '000010'` on mammothdb -> confirmed every
  `SeqPrefix`-family role (`PolicySeq`=1 POLICY, `RenewalKeptSeq`=4 RENEWAL in-window,
  `RenewalDroppedSeqs`=[5] RENEWAL out-of-window, `ApplicationSeq`=6 APPLICATION,
  `PaidSeqs`=[7,8] PAID, `LikeMetacharacterSeq`=10 with `%`/`_` in `ShowName` and
  `LicensePlateNumber='8ฮฮ 8888'`) still matches every existing test literal — no
  change needed to any of those beyond `SeqPrefixHits` (below).
- `EXEC dbo.usp_Motor_SearchDocument @BranchCode='100', @SaleCode='7700'` /
  `'7001'` and the mammothdb equivalents (`usp_NonMotor_SearchDocument`) — all 4 calls
  returned `TotalRows=0` (not a rejection), confirming design.md's prefix/substring
  probe pair (`"7700"`/`"7001"`) is valid and shared across both sides as predicted.
- `SELECT PolicySequenceNo, DocumentType, DocumentNo, SaleCode FROM dbo.Documents
  WHERE SaleCode='77002'` on hippodb -> **35 rows**, not 1. This disproves design.md's
  optional positive-match suggestion (`@SaleCode='77002'` on hippodb -> exactly row
  10's seq `800010`) — `77002` is now a live, populated roster code with its own
  generated-row share, not a code that only row 10 carries. Skipped that optional
  assertion rather than asserting something false; design.md itself marked it
  optional/non-REQ-blocking.

What I found beyond the plan (the one real test failure this task hit): first
`dotnet test --filter "Category=Integration"` run was **1 failed, 112 passed** —
`The_search_window_is_evaluated_per_row_when_the_document_type_is_ALL(NonMotor)`
expected `["000001","000002","000004","000006"]` but got
`["000001","000002","000004","000006","000009"]`. Root cause: axis row 9 kept its
`SaleCode='77001'` value unchanged by task 2 (REQ-2.3), but its *visibility* changed —
under the OLD default scope (`SaleCode='90001'`) row 9 was excluded (wrong code);
under the NEW default scope (`SaleCode='77001'`, this task's own REQ-6.1 change) row 9
is now in-scope, its `DocumentNo` contains the `"00000"` search-text prefix, and it
passes the per-row window rule (POLICY type, `StartDate` inside range) — so it now
legitimately appears in this test's result set. Confirmed via a direct
`EXEC dbo.usp_NonMotor_SearchDocument @BranchCode='100', @SaleCode='77001',
@SearchText='00000'` -> exactly 5 rows (000001/000002/000004/000006/000009), matching
the corrected `SeqPrefixHits`. Neither design.md nor Task 2's HANDOFF section
predicted this specific shift (Task 2 explicitly flagged the `SeqPrefix`-family as
"not currently referenced... task 3 should still re-verify live... this task did not
run those specific assertions" — this is exactly that gap surfacing). After the fix,
rerun was clean: **113 passed, 0 failed**.

Commands run + observed output (verbatim):
- `dotnet build pol-core.slnx` -> `ok dotnet build: 64 projects, 0 errors, 0 warnings`.
- `source .env.integration && dotnet test tests/Integration.Tests/Integration.Tests.csproj
  --filter "Category=Integration"` -> first run: `Failed! - Failed: 1, Passed: 112,
  Skipped: 0, Total: 113`; after the `SeqPrefixHits` fix, rerun -> `Passed! - Failed:
  0, Passed: 113, Skipped: 0, Total: 113, Duration: 12 s`.
- `source .env.integration && dotnet test pol-core.slnx` -> every project passed,
  including `Integration.Tests.dll` (113/113, 12s) and `Hosts.Tests.dll` (379/379,
  17s); no failures anywhere in the solution (Carts.Tests 15/15, BuildingBlocks.Tests
  43/43, Checkouts.Tests 13/13, Products.Tests 137/137, Merchants.Tests 120/120,
  Payments.Tests 162/162, Orders.Tests 76/76, Admins.Tests 95/95, Iam.Tests 61/61,
  Architecture.Tests 200/200).

REQ traceability cross-check (Definition of Done step 4 — all of REQ-1 through REQ-8,
not just this task's own REQs): every requirement has code/test evidence.
REQ-1/REQ-2/REQ-3 — SQL roster reuse, axis-row re-keying, generated-row CASE retarget
(tasks 1-2, live-verified in their own Evidence blocks). REQ-4 — cross-db `EXCEPT`
identity check + per-side roster-completeness/`ShowName`->`SaleCode` checks (tasks
1-2, deliberate-break-then-revert drilled). REQ-5 — this task's
`Sale_code_does_not_match_by_prefix_or_substring`, live-verified 0-row probes on both
sides. REQ-6 — `NonMotorSide.SaleCode="77001"` (this task) + freshly-measured `40`
(task 2's self-check + this task's independent live re-confirmation). REQ-7 — row
counts/formulas/prefixes preserved (tasks 1-2's Evidence) plus this task's live re-pin
sweep, which is what caught the `SeqPrefixHits` gap REQ-7.6 exists to catch. REQ-8 —
header + inline narrative rewrite (tasks 1-2); re-confirmed this task via
`grep -ni "foreign\|two disjoint" docker/bootstrap/02-external-sim.sql` -> zero hits,
and `grep -n "external-sim-shared-agent-network" docker/bootstrap/02-external-sim.sql`
-> 5 hits (header + both database blocks' inline comments), spec reference line
present. **No REQ found uncovered.**

Deviations from design.md: (a) skipped the optional positive-match assertion
(`@SaleCode='77002'` on hippodb -> row 10's seq) — live data shows `77002` resolves 35
rows now, not 1, so the suggestion is factually wrong against the post-re-key data;
design.md itself marked it optional/non-blocking, so this is a correct omission, not a
gap. (b) `NonMotorSide.SeqPrefixHits` needed a fifth entry (`"000009"`) neither
design.md nor Task 2's handoff anticipated — caught by an actual failing test run
before this task's edits were considered done, then confirmed by a direct SQL
re-query, exactly the live-verification discipline REQ-7.6 mandates.

Per repo rule (`.ai/shared/TASK_PROTOCOL.md` line 119) and this file's own instructions
at the top: **no commit, no push** — working tree left as-is for the lead to review
and commit after all 3 tasks are confirmed.
