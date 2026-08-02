# Implementation Tasks: External Sim Shared Agent/Broker/Branch Network

> Status: draft

> Each task is a cohesive, independently verifiable slice. Implement a whole task
> in one pass (it may touch many files). Decompose into sub-steps yourself at
> execution time — do NOT pre-split tasks here.

- [x] 1. hippodb — retire the dead `'90001'` arm, re-key axis row 10, add the two
     new per-side self-checks, rewrite the file header + hippodb-side narrative
     Remove the `WHEN d.SaleCode = '90001'` (or `IN (..., '90001')`) arm from all 5
     hippodb identity `CASE`/`CROSS APPLY` expressions (`PolicyBranch`, `SaleFullName`,
     `BrokerCode`, `BrokerName`, `ReferenceBranch`'s `rb`) so the master table is a clean
     6-entry roster (design.md "Master roster" / "Architecture Overview"). Re-key axis
     row 10's `SaleCode` from `'90001'` to `'77002'` (design.md's "Axis-row re-keying
     map" — `77002` already resolves to `ReferenceBranch = '901'`, the same branch
     `90001` used to resolve to, so this row's `ReferenceBranch`/`DocumentNo`/
     `PolicyNumber` do not change; only `SaleCode`/`SaleFullName`/`BrokerCode`/
     `BrokerName` do). Preserve row 10's `DocumentType`, dates, `ShowName`,
     `TotalPremium`, `PaymentStatus`/`PaidDate`, `LicensePlateNumber`, `BranchCode`
     unchanged. Add hippodb's own copies of the two new self-checks from design.md
     ("Roster-completeness self-check" and "ShowName→SaleCode invariant self-check") to
     hippodb's self-check block. Rewrite the file's header commentary (currently
     describing two disjoint per-side rosters) to describe the shared 6-agent network
     one insurance company operates across both simulated systems; add a reference line
     to this spec. Rewrite hippodb-side inline comments that described row 10 and the
     `CASE`-arm `'90001'` entries as "foreign SaleCode probe" material (they become
     ordinary-row comments). Do not touch mammothdb's blocks in this task.
     Satisfies: REQ-1.1, REQ-1.3 (hippodb half), REQ-1.4 (leave the validate-only
     `BranchCode` search parameter untouched — nothing in this task touches it), REQ-1.5
     (hippodb half), REQ-2.1, REQ-2.4 (row 10), REQ-2.5, REQ-4.3 (hippodb half),
     REQ-7.1/7.2/7.4/7.5 (hippodb regression), REQ-8.1, REQ-8.2, REQ-8.3.
     Batch: B1.
     Verify: `docker compose up pol-db-init` prints `hippodb OK (200 documents, 42 in
     the default search window).` with no THROW; live query confirms all 6 master
     `SaleCode`s (`77001`-`77006`) resolve on hippodb, row 10 now reads `SaleCode =
     '77001+1'`-family... i.e. `'77002'` with `ReferenceBranch = '901'` unchanged from
     before, the two new self-checks pass (rerun with a deliberately broken `CASE` arm
     locally to confirm each one actually throws, then revert), and the REQ-8.1-8.3
     comment rewrite reads accurately against the new data.
     Evidence: Ran `docker compose up pol-db-init` (pol-db already healthy) — output:
     `02-external-sim: hippodb OK (200 documents, 42 in the default search window).` /
     `02-external-sim: mammothdb OK (200 documents, 39 in the default search window).` /
     `02-external-sim: OK.`, container exited 0, no THROW (mammothdb untouched by this
     task, still on its pre-existing `90001`-`90006` roster, expectedly). Live query via
     `docker exec pol-db /opt/mssql-tools18/bin/sqlcmd` against hippodb confirmed all 6
     master `SaleCode`s resolve with exactly design.md's "Master roster" table values
     (`77001`→`900`/สำนักงานใหญ่/นายกิตติพงศ์ อารีย์วงศ์/701, `77002`→`901`/สาขาสีลม/นางสาวสุนิสา
     วงศ์สว่าง/702, `77003`→`902`/สาขาเชียงใหม่/นายเอกรัตน์ ธีรวุฒิ/703, `77004`→`903`/สาขาหาดใหญ่/
     นางสาวจิราพร คงเจริญ/704, `77005`→`904`/สาขาขอนแก่น/นายภาณุวัฒน์ สุขประเสริฐ/705,
     `77006`→`900`/สำนักงานใหญ่/นางเบญจวรรณ ทองอยู่/701; broker names match verbatim too),
     `COUNT(DISTINCT SaleCode) = 6` (no dead 7th arm). Row 10 (`PolicySequenceNo=800010`)
     reads `SaleCode='77002'`, `ReferenceBranch='901'` (unchanged), `PolicyBranch=สาขาสีลม`,
     `DocumentNo='69901/กธ/800010'` (unchanged), `DocumentType=POLICY`,
     `ShowName='นายเอกรัตน์ ธีรวุฒิ'`, `TotalPremium=4100.00`, `PaymentStatus=UNPAID`,
     `PaidDate=NULL`, `LicensePlateNumber='1ฎฎ 1010'`, `BranchCode='100'` — all preserved
     fields unchanged as required. `ShowName->SaleCode` collision query returned 0 (F-5
     live-confirmed: no other row shares row 10's ShowName under a different SaleCode).
     Deliberate-break-then-revert drill (both new self-checks): (1) roster-completeness —
     temporarily changed its threshold from `<> 6` to `<> 5`; rerun `docker compose up
     pol-db-init` → `Msg 51002 ... hippodb expected exactly 6 distinct SaleCode values
     (the shared master roster).`, exit 1; reverted. (2) ShowName→SaleCode — temporarily
     changed `> 1` to `> 0`; rerun → `Msg 51002 ... hippodb a ShowName resolves to more
     than one SaleCode (ShowName->SaleCode pairing invariant violated).`, exit 1;
     reverted. Both reverts confirmed byte-identical to the pre-drill file via `diff`
     (exit 0) before the final clean rerun (green, shown above). Comment rewrite (header +
     inline) read back against the new data and confirmed accurate.
     Deviations from design.md: (a) design.md's "Axis-row re-keying map" claims row 10's
     `PolicyNumber` is unchanged by the re-key alongside `ReferenceBranch`/`DocumentNo` —
     live data shows this is only true for `ReferenceBranch`/`DocumentNo`.
     `PolicyNumber = CONCAT(d.SaleCode, '-69', rb.ReferenceBranch, '/',
     d.PolicySequenceNo)` embeds `d.SaleCode` directly, so row 10's `PolicyNumber`
     necessarily changed from `90001-69901/800010` to `77002-69901/800010`. No current
     test hardcodes this row's `PolicyNumber` (grepped both Integration.Tests files —
     only `MotorSide.ForeignSaleCode`/`ForeignSaleCodeSeq` = `"90001"`/`"800010"`
     reference this row, both slated for removal in task 3 per REQ-5.3), so this has no
     blast radius on this task, but task 3 should NOT assume row 10's `PolicyNumber` is
     the old `90001-...` value if any assertion ever needs it. (b) design.md's
     roster-completeness/ShowName→SaleCode self-check snippets use a fully generic THROW
     message (no db name); copied them into hippodb's block with `hippodb` inserted into
     the message text (e.g. `hippodb expected exactly 6 distinct SaleCode values...`) to
     match the file's own established convention that every other self-check names its
     database explicitly (`hippodb seeded...`, `hippodb default search sees...`) — purely
     a debugging-clarity improvement, no functional difference; per the task brief's
     instruction to adapt (not blind-copy) the snippet for hippodb specifically.

- [x] 2. mammothdb — retire its own roster, duplicate hippodb's master table, re-key
     axis rows, retarget generated rows, add the cross-db identity check + both new
     per-side self-checks, realign the default-search probe, rewrite mammothdb-side
     narrative
     Depends on: 1 (duplicates hippodb's now-final 6-entry `CASE` tables verbatim).
     Replace mammothdb's `90001`-`90006` `ReferenceBranch`/`PolicyBranch`/
     `SaleFullName`/`BrokerCode`/`BrokerName` `CASE`/`CROSS APPLY` expressions with
     hippodb's post-task-1 expressions, byte-identical, keyed the same way
     (`d.SaleCode`). Retarget mammothdb's generated-row `SaleCode` `CASE` from
     `90001`-`90006` to `77001`-`77006` (design.md "Generated-row SaleCode CASE
     retargeting" — same `names.Idx` bucket structure, only the six target literals
     change). Re-key axis rows 1-8 and 10 from `'90001'`-`'90006'` to `'77001'`
     (design.md's re-keying map — required, not stylistic: these are the exact landmark
     rows `PolicySeq`/`RenewalKeptSeq`/`RenewalDroppedSeqs`/`ApplicationSeq`/
     `PaidSeqs`/`LikeMetacharacterSeq` key off); leave row 9's `SaleCode` value
     (`'77001'`) unchanged, only its role comment. Preserve every re-keyed row's
     `DocumentType`, dates, `ShowName`, `TotalPremium`, `PaymentStatus`/`PaidDate`,
     `LicensePlateNumber`, `BranchCode` unchanged. Add the new cross-database identity
     self-check (design.md's `EXCEPT`-based snippet, `THROW 51002`, names the offending
     `SaleCode`) plus mammothdb's own copies of the roster-completeness and
     `ShowName`→`SaleCode` self-checks, all in mammothdb's self-check block after its
     existing `DocumentNo`-uniqueness cross-db check. Change the default-search
     self-check's probe `SaleCode` from `'90001'` to `'77001'` and update its `PRINT`
     message to the freshly-measured count (query live, do not guess). Rewrite
     mammothdb-side inline comments that described the retired roster / row 9 as
     "foreign SaleCode probe" material.
     Satisfies: REQ-1.2, REQ-1.3 (mammothdb half), REQ-1.5 (mammothdb half), REQ-2.2,
     REQ-2.3, REQ-2.4 (rows 1-8, 10), REQ-3.1, REQ-3.2, REQ-3.3, REQ-4.1, REQ-4.2,
     REQ-4.3 (mammothdb half), REQ-6.1, REQ-6.2, REQ-7.1/7.3/7.4/7.5 (mammothdb
     regression), REQ-8.1 (mammothdb half), REQ-8.3.
     Batch: B1.
     Verify: `docker compose up pol-db-init` prints `hippodb OK (200 documents, 42 in
     the default search window).` AND `mammothdb OK (200 documents, N in the default
     search window).` (N = the freshly-measured count, read from the live query, not
     assumed) with no THROW; live query confirms every one of the 6 master `SaleCode`s
     resolves identically on both databases (spot-check a few fields per code against
     hippodb's table), the new cross-db identity check and both new per-side checks
     pass (same deliberate-break-then-revert drill as task 1), row counts stay 200/200,
     and `DocumentNo` prefix/uniqueness invariants (within-side and cross-side) still
     hold.
     Evidence: Ran `docker compose up pol-db-init` (pol-db already healthy) — final
     output: `02-external-sim: hippodb OK (200 documents, 42 in the default search
     window).` / `02-external-sim: mammothdb OK (200 documents, 40 in the default
     search window).` / `02-external-sim: OK.`, container exited 0, no THROW. Live
     query (`docker exec pol-db /opt/mssql-tools18/bin/sqlcmd -d mammothdb`) confirmed
     all 6 master `SaleCode`s resolve to byte-identical `ReferenceBranch`/`PolicyBranch`/
     `SaleFullName`/`BrokerCode`/`BrokerName` as hippodb's own table (spot-checked every
     row, both sides queried side by side): `77001`->`900`/สำนักงานใหญ่/นายกิตติพงศ์
     อารีย์วงศ์/701, `77002`->`901`/สาขาสีลม/นางสาวสุนิสา วงศ์สว่าง/702, `77003`->`902`/
     สาขาเชียงใหม่/นายเอกรัตน์ ธีรวุฒิ/703, `77004`->`903`/สาขาหาดใหญ่/นางสาวจิราพร คงเจริญ/
     704, `77005`->`904`/สาขาขอนแก่น/นายภาณุวัฒน์ สุขประเสริฐ/705, `77006`->`900`/
     สำนักงานใหญ่/นางเบญจวรรณ ทองอยู่/701. All 10 mammothdb axis rows (`PolicySequenceNo`
     `000001`-`000010`) now read `SaleCode='77001'`, `ReferenceBranch='900'`,
     `PolicyYearBranch` prefix `26900` (confirms design.md's predicted `"26900"`), with
     every preserved field (`DocumentType`, dates, `ShowName`, `TotalPremium`,
     `PaymentStatus`/`PaidDate`, `LicensePlateNumber`) unchanged from before the re-key.
     Row 1: `DocumentNo='26900/POL/000001'`, `PolicyNumber='77001-26900/000001'`. Row 7
     (PAID ENDORSEMENT): `DocumentNo='1-26900/END/000007'`,
     `PolicyNumber='77001-26900/000007'`, `PaidDate='2026-07-28'`. `ShowName->SaleCode`
     collision query on mammothdb: 0 rows (F-5 live-confirmed). Default-search live
     measurement: `TotalRows=40`, `TotalPages=2`, `LastPageRows=15` (queried directly,
     not hand-derived) — self-check's expected literal updated from the design-time
     placeholder `39` to the measured `40`. Row counts: `mammothdb=200`,
     `hippodb=200`. `DocumentNo` invariants: 0 rows violate the `26%`/`1-26%` prefix, 0
     duplicate `DocumentNo` within mammothdb, 0 `DocumentNo` shared across
     hippodb/mammothdb. Deliberate-break-then-revert drill (all 3 new/changed
     self-checks on mammothdb): (1) roster-completeness — `<>6` to `<>5` -> `Msg 51002
     ... mammothdb expected exactly 6 distinct SaleCode values (the shared master
     roster).`, exit 1; reverted. (2) `ShowName`->`SaleCode` — `>1` to `>0` -> `Msg
     51002 ... mammothdb a ShowName resolves to more than one SaleCode (ShowName->
     SaleCode pairing invariant violated).`, exit 1; reverted. (3) new cross-db identity
     check — temporarily changed mammothdb's `BrokerCode` `WHEN '77003' THEN '703'` to
     `'799'` -> `Msg 51002 ... agent identity drifted between hippodb and mammothdb for
     SaleCode 77003 — SaleFullName/BrokerCode/BrokerName/ReferenceBranch/PolicyBranch
     must match on both sides.`, exit 1; reverted. All three reverts confirmed
     byte-identical to a pre-drill backup via `diff` (exit 0) before the final clean
     rerun (green, shown above).
     Deviations from design.md: none. design.md's re-keying map, generated-row CASE
     retarget, cross-db `EXCEPT` snippet, and roster/`ShowName` self-check snippets were
     all applied as specified (with `mammothdb` inserted into the THROW messages per
     task 1's established convention). The only design-time placeholder replaced with a
     live value was the default-search count (`39` -> measured `40`), which design.md
     explicitly left as "freshly measured, not assumed" (REQ-6.2).

- [x] 3. Integration.Tests — redesign the exact-match test, re-pin every literal the
     roster change moved
     Depends on: 1, 2.
     In `SpDocumentContractTests.cs`: remove `Side.ForeignSaleCode`/
     `ForeignSaleCodeSeq` from both `MotorSide` and `NonMotorSide`; add a shared
     prefix-probe constant (`"7700"`) and substring-probe constant (`"7001"`, or
     side-specific if a live-DB check finds a reason to differ from design.md's
     assumption that a single shared pair works). Replace
     `Sale_code_is_an_exact_scope_axis` with `Sale_code_does_not_match_by_prefix_or_substring`
     (or an equivalent name) asserting zero rows for both probes on both sides; consider
     adding the optional positive-match assertion design.md notes (`@SaleCode =
     '77002'` on hippodb → exactly row 10's seq). Change `NonMotorSide.SaleCode` from
     `"90001"` to `"77001"`. Re-pin `NonMotorSide.PolicyYearBranch` (→ `"26900"`),
     `TotalRows`/`TotalPages`/`LastPageRows`, and every `SeqPrefix`-family literal whose
     value shifted — read every one from the live reseeded database, do not hand-derive
     (same discipline as every prior round on this branch).
     In `SpDocumentGatewayIntegrationTests.cs`: change `NonMotorSide.SaleCode` to
     `"77001"`; re-pin `AxisReferenceBranch`, `AxisPolicyNumber`, `AxisDocumentNo`,
     `PaidPolicyNumber`, and the hardcoded `Assert.Equal(2L, result.Page.TotalPages)` in
     `A_default_search_maps_the_metadata_row_and_the_page`; update the
     `AxisReferenceBranch` comment to name the correct axis row and branch direction.
     Sweep both files for any other literal or comment downstream of the roster change
     that the enumerated list above doesn't already name — REQ-7.6 covers all of them,
     not only the named examples.
     Satisfies: REQ-5.1, REQ-5.2, REQ-5.3, REQ-5.4, REQ-5.5, REQ-6.1 (test-side),
     REQ-6.2, REQ-7.6, REQ-7.3 (test-side verification that formulas/shapes held).
     Verify: `source .env.integration && dotnet test
     tests/Integration.Tests/Integration.Tests.csproj --filter "Category=Integration"`
     — all green, no skipped/failed; `dotnet build pol-core.slnx` — 0 errors, 0
     warnings; full-solution `dotnet test pol-core.slnx` as a final belt-and-suspenders
     regression pass before the task is marked done.
     Evidence: `docker compose up pol-db-init` rerun clean first (`hippodb OK (200
     documents, 42...)` / `mammothdb OK (200 documents, 40...)` / `OK.`, exit 0) to
     guarantee the live DB matched the committed SQL before reading any literal.
     `SpDocumentContractTests.cs`: removed `Side.ForeignSaleCode`/`ForeignSaleCodeSeq`
     from the record and both instances; added class-level `SaleCodePrefixProbe =
     "7700"` / `SaleCodeSubstringProbe = "7001"` (single shared pair, not per-`Side` —
     confirmed live via direct `EXEC dbo.usp_Motor_SearchDocument`/
     `usp_NonMotor_SearchDocument @SaleCode='7700'|'7001'` on both catalogs: all four
     calls returned `TotalRows=0`, not rejected); replaced
     `Sale_code_is_an_exact_scope_axis` with `Sale_code_does_not_match_by_prefix_or_substring`
     asserting `Assert.Empty` for both probes on both sides. Did NOT add the optional
     positive-match assertion (`@SaleCode='77002'` on hippodb → row 10's seq) — live
     query (`SELECT ... WHERE SaleCode='77002'`) showed `77002` now resolves 35 rows on
     hippodb, not just row 10, so that design.md suggestion does not hold against live
     data (design.md flagged it as optional/non-blocking; correctly dropped here rather
     than asserted incorrectly). `NonMotorSide`: `SaleCode` `"90001"`->`"77001"`,
     `PolicyYearBranch` `"26901"`->`"26900"`, `TotalRows` 39->40, `LastPageRows` 14->15
     — all read from a live `EXEC dbo.usp_NonMotor_SearchDocument @SaleCode='77001'`
     call (page 1: `TotalRows=40 TotalPages=2`; page 2: 15 item rows counted directly).
     Header comment's `39`->`40` updated too. `SeqPrefixHits` changed from
     `["000001","000002","000004","000006"]` to `["000001","000002","000004","000006","000009"]`
     — caught by a real test failure, not by inspection: axis row 9 (`SaleCode` already
     `'77001'` pre- and post-feature, per REQ-2.3) was excluded from the old default
     scope (`'90001'`) but is now inside the new default scope (`'77001'`) and its
     `DocumentNo` contains the `"00000"` search-text prefix and passes the per-row
     window rule, so `The_search_window_is_evaluated_per_row_when_the_document_type_is_ALL`
     failed on first run (`Expected: [...4 items], Actual: [...5 items, +"000009"]`) —
     fixed by adding `"000009"` to the live-confirmed set (re-ran
     `EXEC ... @SearchText='00000'` directly, got exactly 5 rows:
     000001/000002/000004/000006/000009). All other `SeqPrefix`-family literals
     (`RenewalDroppedSeqs`, `RenewalKeptSeq`, `ApplicationSeq`, `PolicySeq`,
     `LikeMetacharacterSeq`, `PaidSeqs`) re-verified unchanged against a direct
     `SELECT PolicySequenceNo, DocumentType, SourceSystem, StartDate, EndDate, ShowName,
     PaymentStatus, PaidDate, LicensePlateNumber FROM dbo.Documents WHERE
     PolicySequenceNo BETWEEN '000001' AND '000010'` on mammothdb — role-per-row
     (POLICY/RENEWAL kept/RENEWAL dropped/APPLICATION/ENDORSEMENT PAID/POLICY PAID/
     ordinary/LIKE-metacharacter) matches every existing literal exactly, no other
     change needed. `SpDocumentGatewayIntegrationTests.cs`: `NonMotorSide.SaleCode`
     `"90001"`->`"77001"`, `TotalRows` 39->40, `AxisPolicyNumber`
     `"90001-26901/000001"`->`"77001-26900/000001"`, `AxisDocumentNo`
     `"26901/POL/000001"`->`"26900/POL/000001"`, `AxisReferenceBranch` `"901"`->`"900"`
     (comment updated to `"SaleCode 77001 -> broker 701 -> branch 900"`),
     `PaidPolicyNumber` `"90001-26901/000007"`->`"77001-26900/000007"` — all read from a
     direct `SELECT PolicySequenceNo, SaleCode, ReferenceBranch, DocumentNo,
     PolicyNumber, DocumentType, PaymentStatus, PaidDate, SourceSystem FROM
     dbo.Documents WHERE PolicySequenceNo IN ('000001','000007',...)` on mammothdb.
     `Assert.Equal(2L, result.Page.TotalPages)` swept and re-verified — still correct
     for both sides (42/25 and 40/25 both ceiling to 2 pages) — left unchanged, no
     stale value. Full sweep of both files for `90001`/`26901`/`ForeignSaleCode`/stale
     `39`/`14` via `grep` after edits: zero hits in `SpDocumentContractTests.cs`; only
     `Assert.Equal(2L, result.Page.TotalPages)` (already correct, see above) matched in
     `SpDocumentGatewayIntegrationTests.cs`, no `90001`/`26901` hits.
     Commands run + observed output: `dotnet build pol-core.slnx` → `64 projects, 0
     errors, 0 warnings`. `source .env.integration && dotnet test
     tests/Integration.Tests/Integration.Tests.csproj --filter "Category=Integration"`
     → first run: 1 failed (the `SeqPrefixHits` gap above), 112 passed; after the fix,
     rerun → `Passed! - Failed: 0, Passed: 113, Skipped: 0, Total: 113`. Full-solution
     `dotnet test pol-core.slnx` → every project green, including `Integration.Tests.dll`
     (113/113) and `Hosts.Tests.dll` (379/379) — 0 failures anywhere in the solution.
     REQ traceability cross-check (all of REQ-1 through REQ-8): every requirement maps
     to code/test evidence already recorded across this file's three tasks and
     HANDOFF.md's three sections — REQ-1/2/3 (SQL roster + axis-row + generated-row
     changes, tasks 1-2), REQ-4 (cross-db + per-side self-checks, tasks 1-2, drilled),
     REQ-5 (this task's `Sale_code_does_not_match_by_prefix_or_substring`, live-verified
     0-row probes both sides), REQ-6 (`NonMotorSide.SaleCode="77001"` +
     freshly-measured `40`, this task), REQ-7 (row counts/formulas/prefixes preserved
     per tasks 1-2's evidence + this task's live re-pin, including the SeqPrefixHits
     catch), REQ-8 (header + inline narrative rewrite, tasks 1-2 — confirmed no
     remaining "foreign"/"two disjoint" language via `grep -ni "foreign\|two disjoint"
     docker/bootstrap/02-external-sim.sql`, zero hits; spec reference line present).
     No REQ left uncovered.
     Deviations from design.md: (a) skipped the optional positive-match assertion
     (`@SaleCode='77002'` -> row 10's seq) — live data shows `77002` resolves 35 rows,
     not 1, so the suggestion doesn't hold (design.md marked it optional/non-blocking).
     (b) `NonMotorSide.SeqPrefixHits` needed a fifth entry (`"000009"`) that neither
     design.md nor HANDOFF.md's Task 2 section anticipated — HANDOFF.md explicitly
     flagged this family as "not currently referenced... re-verify live" rather than
     asserting it was unaffected, and live verification (via an actual failing test
     run, then a direct SQL re-check) is exactly what caught it.

## Suggested execution batches

Tightly coupled feature (the SQL roster redesign and its test literals cannot be
verified apart from each other) — run in ONE session: `scripts/pane-loop.sh
external-sim-shared-agent-network all-in-one` (or `/spec-implement all`). Tasks 1+2
are tagged `Batch: B1` because task 2 duplicates task 1's finished master table
verbatim and both share the same self-check patterns and narrative-rewrite work —
sequential within one session benefits from the shared context, same as the sibling
spec's Batch B1. Task 3 depends on both finishing first regardless.
