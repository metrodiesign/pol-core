# Implementation Tasks: External Sim DocumentNo Format Alignment

> Status: approved 2026-08-02

> Each task is a cohesive, independently verifiable slice. Implement a whole task
> in one pass (it may touch many files). Decompose into sub-steps yourself at
> execution time — do NOT pre-split tasks here.

- [x] 1. hippodb — compute-forward DocumentNo/PolicyNumber family (Motor)
     Rewrite `docker/bootstrap/02-external-sim.sql`'s hippodb block: swap the 14 axis-row
     `INSERT ... VALUES`' `DocumentNo` column for a `PolicySequenceNo` literal (marker-prefixed
     per design.md's "91"-hit-group / "80"-non-hit-group scheme, zero-padded to 6 digits for
     `VMI` rows and 7 for `CMI` rows); swap the generated-row `INSERT ... SELECT`'s `DocumentNo`
     expression for a `PolicySequenceNo` expression (`100 + g.value`, zero-padded per the same
     `CMI`=7/`VMI`=6 rule, duplicating the existing `SourceSystem` CASE inline per design.md's
     note on why); remove the `CROSS APPLY` that parses `v.Seq` out of the old `DocumentNo`; add
     the `Abbrev(SourceSystem, DocumentType)` CASE and the `DocumentNo`/`PolicyNumber`/
     `ApplicationNumber`/`PreviousPolicyNumber`/`EndorsementNumber` formulas from design.md's
     Data Models section to the shared `UPDATE ... SET`; replace the self-check's `DocumentNo
     NOT LIKE '77%'` assertion with `DocumentNo NOT LIKE '69%'` (hippodb's `PolicyYear` prefix).
     hippodb's Thai round-trip self-check (`DocumentNo LIKE N'%กธ%'`) stays as-is.
     Satisfies: REQ-1 (1.1, 1.3, 1.4, 1.5), REQ-2 (2.1, 2.2, 2.3, 2.7), REQ-3 (3.1, 3.2, 3.3, 3.4),
     REQ-4.1, REQ-5 (all), REQ-6 (6.1, 6.2, 6.4 hippo half), REQ-7 (7.1, 7.3 hippo half, 7.4),
     REQ-9 (all, hippo formulas).
     Verify: `docker compose up pol-db-init` prints `hippodb OK (200 documents, 42 in the
     default search window).` with no THROW; live query confirms every hippodb `DocumentNo`
     starts `69`, no two rows share a `DocumentNo`, and the SaleCode/SaleFullName/BrokerCode/
     BrokerName/ReferenceBranch/PolicyBranch pairing invariants from prior rounds still hold
     (REQ-8.1–8.4 regression check).
     Evidence: Ran `docker compose up pol-db-init` (pol-db already healthy) — output:
     `02-external-sim: hippodb OK (200 documents, 42 in the default search window).` /
     `02-external-sim: mammothdb OK (200 documents, 39 in the default search window).` /
     `02-external-sim: OK.`, container exited 0, no THROW. Ran live verification queries via
     `docker exec pol-db /opt/mssql-tools18/bin/sqlcmd` against `hippodb`: `BadPrefixCount=0`,
     `DupDocumentNoCount=0`, `TotalRows=200`; SaleCode→SaleFullName, SaleCode→BrokerCode/
     BrokerName, ReferenceBranch↔PolicyBranch, and BrokerCode→ReferenceBranch pairing queries
     each returned 0 violating rows. All 14 axis rows read back with the exact predicted
     `PolicySequenceNo`/`DocumentNo`/`PolicyNumber`/`PreviousPolicyNumber`/`EndorsementNumber`
     values (e.g. row 2 CMI POLICY -> `PolicySequenceNo='9100002'`, `DocumentNo='69900/กธ/9100002'`;
     row 8 CMI ENDORSEMENT -> `PolicySequenceNo='9100008'`, `DocumentNo='69900/อท/91000081'`,
     `EndorsementNumber='E9100008'`, `PreviousPolicyNumber='77001-68900/9100007'`; row 10 VMI
     POLICY foreign SaleCode `90001` -> `PolicySequenceNo='800010'`,
     `DocumentNo='69901/กธ/800010'` — `ReferenceBranch` correctly resolves to `901` for that
     SaleCode). Sampled generated rows confirm the `100+g.value` zero-pad-by-SourceSystem rule
     and the trailing `'1'` on CMI `ENDORSEMENT` rows (e.g. `PolicySequenceNo='0000259'` ->
     `DocumentNo='69900/อท/00002591'`). Full marker table for all 14 axis rows recorded in
     `HANDOFF.md` ("Task 1 — hippodb") for task 3's literal re-pinning.

- [x] 2. mammothdb — compute-forward DocumentNo/PolicyNumber family (Non-Motor) + shared file header
     Same restructuring as task 1, applied to mammothdb's block: axis rows get a plain
     sequential 1–10 `PolicySequenceNo` literal (6-digit zero-padded — mammothdb never has the
     `CMI` width split, so no marker scheme needed); generated rows get `100 + g.value` zero-
     padded to 6 digits; `Abbrev` maps to `POL`/`APP`/`END`; `DocumentNo` gets the `'1-'`-prefix
     variant for `ENDORSEMENT` rows (REQ-1.2, not the Motor-style trailing-`'1'` variant);
     `PolicyYear` becomes the literal `'26'` (was the shared `'69'` constant); self-check prefix
     assertion becomes `DocumentNo NOT LIKE '26%' AND DocumentNo NOT LIKE '1-26%'`; retarget the
     Thai round-trip self-check from `DocumentNo LIKE N'%อค%'` to `PolicyBranch LIKE N'%สาขา%'`
     (mammothdb's `DocumentNo` becomes ASCII-only). Also rewrite the file's shared header
     comment block (the "DELIBERATE DEVIATIONS" list and the M9 prefix-disjointness note) to
     describe the new `PolicyYear`-based disjointness instead of the retired `SaleCode`-prefix
     one, and add a reference line to this spec alongside the existing `products-sp-gateway`
     reference.
     Satisfies: REQ-1 (1.1, 1.2, 1.4, 1.5), REQ-2 (2.4, 2.5, 2.6, 2.7), REQ-3 (3.1, 3.2, 3.3, 3.4),
     REQ-4 (4.2, 4.3), REQ-5 (all), REQ-6 (6.1, 6.2, 6.4 mammoth half, 6.5), REQ-7 (7.2, 7.3
     mammoth half, 7.4), REQ-9 (all, mammoth formulas).
     Batch: B1.
     Verify: `docker compose up pol-db-init` prints `mammothdb OK (200 documents, 39 in the
     default search window).` with no THROW; live query confirms every mammothdb `DocumentNo`
     starts `26` or `1-26`, no two rows share a `DocumentNo`, cross-catalog (hippodb ∪
     mammothdb) `DocumentNo` values never collide, and the same REQ-8.1–8.4 pairing invariants
     hold on this side too.
     Evidence: Ran `docker compose up pol-db-init` (pol-db already healthy) — output:
     `02-external-sim: hippodb OK (200 documents, 42 in the default search window).` /
     `02-external-sim: mammothdb OK (200 documents, 39 in the default search window).` /
     `02-external-sim: OK.`, container exited 0, no THROW. Ran live verification via
     `docker exec pol-db /opt/mssql-tools18/bin/sqlcmd` against `mammothdb`: `BadPrefixCount=0`,
     `DupDocumentNoCount=0`, `TotalRows=200`, `CrossCatalogDup=0` (hippodb ∪ mammothdb `DocumentNo`
     join); SaleCode→SaleFullName, SaleCode→BrokerCode, ReferenceBranch↔PolicyBranch, and
     BrokerCode→ReferenceBranch pairing queries each returned 0 violating rows. All 10 axis rows
     read back with the exact predicted values (e.g. row 1 FIRE POLICY SaleCode `90001` ->
     `PolicySequenceNo='000001'`, `ReferenceBranch='901'`, `DocumentNo='26901/POL/000001'`,
     `PolicyNumber='90001-26901/000001'`; row 7 FIRE ENDORSEMENT -> `PolicySequenceNo='000007'`,
     `DocumentNo='1-26901/END/000007'`, `EndorsementNumber='E000007'`,
     `PreviousPolicyNumber='90001-25901/000006'`; row 9 FIRE POLICY foreign SaleCode `77001` ->
     `PolicySequenceNo='000009'`, `ReferenceBranch='900'`, `DocumentNo='26900/POL/000009'`).
     Sampled generated rows confirm the `100+g.value` zero-pad-6 rule and the `1-` ENDORSEMENT
     prefix (e.g. `PolicySequenceNo='000220'` ENDORSEMENT -> `DocumentNo='1-26901/END/000220'`).
     Full axis-row table recorded in `HANDOFF.md` ("Task 2 — mammothdb") for task 3's literal
     re-pinning. No deviations from design.md.

- [x] 3. Integration.Tests — re-pin literals, DocumentType-aware Seq helper, replace the ordering test
     Update `Seqs()`/`DocumentNumbers()` in `SpDocumentContractTests.cs` to strip the trailing
     un-delimited `'1'` only when the row's own `SourceSystem IN ('CMI','VMI')` AND
     `DocumentType = 'ENDORSEMENT'` (read from the already-fetched row dictionary — do not use a
     string-length heuristic, per design.md's ambiguity note). Recompute every Seq-derived
     literal in both `Side` records in `SpDocumentContractTests.cs` (`SeqPrefix`,
     `SeqPrefixHits`, `RenewalDroppedSeqs`, `RenewalKeptSeq`, `ApplicationSeq`, `PolicySeq`,
     `LikeMetacharacterSeq`, `PaidSeqs`, `ForeignSaleCodeSeq`) against the live seeded DB from
     tasks 1–2 — do not hand-derive them; read them back from a real query/self-check run, same
     discipline as every prior round on this branch. Recompute `AxisDocumentNo`,
     `AxisPolicyNumber`, `PaidPolicyNumber` in `SpDocumentGatewayIntegrationTests.cs` the same
     way. Replace `Motor_last_page_is_ordered_by_the_thai_letter_in_the_document_number` with
     the grouping-property test design.md specifies (walk every page, assert every `'อท'`-
     bearing `DocumentNo`'s position comes after every non-`'อท'`-bearing one's position),
     renamed `Motor_endorsement_rows_sort_after_every_other_row_by_thai_letter`. Refresh the
     `DocumentNo`/`SaleCode` example row in `docs/reference/products.md` (~line 160) to the new
     format.
     Depends on: 1, 2.
     Satisfies: REQ-1, REQ-2, REQ-3, REQ-9 (test-side verification of all three), REQ-8.5
     (replaced test), REQ-8.1–8.4 (regression — these tests are the proof the pairing
     invariants from prior rounds still hold).
     Verify: `source .env.integration && dotnet test tests/Integration.Tests/Integration.Tests.csproj
     --filter "Category=Integration"` — all green, no skipped/failed; `dotnet build
     pol-core.slnx` — 0 errors, 0 warnings; full-solution `dotnet test pol-core.slnx` as a final
     belt-and-suspenders regression pass (mirrors the check every prior round on this branch
     ended with) before the task is marked done.
     Evidence: `dotnet build pol-core.slnx` -> `64 projects, 0 errors, 0 warnings`. Re-queried the
     live seeded DB directly (not hand-derived) for all 14 hippodb + 10 mammothdb axis rows and the
     hippodb smart-search-window/`SeqPrefix` match set (emulated the SP's own predicate in SQL) —
     every value matched HANDOFF.md's tables from tasks 1/2 exactly, then re-pinned
     `SpDocumentContractTests.cs`'s two `Side` records (`SeqPrefix "91"`/`"00000"`, `SeqPrefixHits`,
     `RenewalDroppedSeqs`, `RenewalKeptSeq`, `ApplicationSeq`, `PolicySeq`, `LikeMetacharacterSeq`,
     `PaidSeqs`, `ForeignSaleCodeSeq`) and `SpDocumentGatewayIntegrationTests.cs`'s two `Side`
     records (`AxisDocumentNo`, `AxisPolicyNumber`, `PaidPolicyNumber`) from those live values.
     `Seqs()` rewritten to branch the trailing-`'1'` strip on the row's own
     `SourceSystem`/`DocumentType` (no length heuristic); `Motor_last_page_is_ordered_by_the_thai_
     letter_in_the_document_number` replaced with `Motor_endorsement_rows_sort_after_every_other_
     row_by_thai_letter` (walks every page via a new shared `AllPagesAsync` helper, asserts every
     `'อท'`-bearing `DocumentNo` position > every non-`'อท'`-bearing position — verified empirically
     against a live query first: `firstEndorsementPos=31 > lastNonEndorsementPos=30` on the 42-row
     default Motor set). `docs/reference/products.md`'s DocumentNo example row (line 160) updated to
     `69900/กธ/910001`.
     First `source .env.integration && dotnet test ... --filter "Category=Integration"` run surfaced
     4 failures; root-caused (not guessed) each one before touching anything:
     (a) `Omitting_every_optional_parameter_applies_the_documented_defaults(Motor)` — REAL, caused by
     this feature: REQ-1.1 dropped the `SaleCode` prefix, so `DocumentNo` now sorts by its embedded
     abbreviation first (REQ-2), and Motor's default 42-row page 1 is legitimately all-`POLICY` by
     that sort (confirmed via a direct top-25 query). Fixed by asserting the `@DocumentType`/
     `@ProductGroup`-unfiltered property across the full walked result (`AllPagesAsync`) instead of
     just page 1, preserving the test's original intent without relying on a sampling assumption the
     new format invalidates.
     (b)/(c)/(d) `Coverage_bounds_are_inclusive_on_both_ends` (both sides) and
     `Motor_coverage_start_window_includes_the_row_sitting_exactly_six_months_back` — pre-existing,
     UNRELATED to this feature: proved via direct clock comparison
     (`date` vs `docker exec pol-db date` vs `SELECT GETDATE()`) that the host is UTC+7 while the
     `pol-db` container is UTC, and the run landed in the ~07:00-Thai-time daily window where
     `DateTime.Today` (host-local) and the seed's `GETDATE()`-derived `@today` (container-UTC)
     disagree by a full calendar day — these three tests hardcode `DateTime.Today`/`.AddMonths(-6)`
     and predate this feature entirely (their date logic and literals were untouched by this task).
     Reproduced identically 3 times at the same wall-clock moment, then confirmed the theory by
     re-running only those 3 tests under `TZ=UTC` (matching the container and how a UTC CI runner
     would see it) — all 3 passed with no code change. Left as-is: fixing a dev-machine/container
     timezone mismatch is out of this feature's scope (no REQ covers it) and touching shared
     date-handling test infrastructure has a blast radius well beyond DocumentNo formatting.
     Full re-run after the one real fix: `source .env.integration && dotnet test
     tests/Integration.Tests/Integration.Tests.csproj --filter "Category=Integration"` (verbatim, no
     TZ override) at a moment past the skew window ->
     `Passed! - Failed: 0, Passed: 113, Skipped: 0, Total: 113` (also independently reproduced under
     `TZ=UTC` earlier). `dotnet test pol-core.slnx` (full solution, `TZ=UTC`) -> every project green:
     `BuildingBlocks.Tests 43/43`, `Carts.Tests 15/15`, `Checkouts.Tests 13/13`,
     `Products.Tests 137/137`, `Orders.Tests 76/76`, `Iam.Tests 61/61`, `Merchants.Tests 120/120`,
     `Payments.Tests 162/162`, `Admins.Tests 95/95`, `Integration.Tests 113/113`,
     `Architecture.Tests 200/200`, `Hosts.Tests 379/379`, `Levels.Tests 6/6`, `Positions.Tests 6/6`
     — 0 failed anywhere.
     REQ traceability cross-check (Definition of Done, last task of this spec): every REQ-1 through
     REQ-9 (all sub-items) maps to a satisfying task per design.md's own "Requirement Traceability"
     table (tasks 1/2 for the SQL-side REQs, this task's re-pinned/replaced tests for the
     test-verification REQs) — no REQ found uncovered.
     Deviation from design.md: none in the SQL/formula work (tasks 1/2 already covered that); one
     test file change beyond what design.md's Testing Strategy table listed —
     `Omitting_every_optional_parameter_applies_the_documented_defaults`'s DocumentType/SourceSystem
     diversity check needed broadening to the full result set for the reason in (a) above; design.md
     did not anticipate this specific test, but the root cause is the same REQ-1.1/REQ-2 sort-order
     change design.md already reasoned about for the replaced ordering test.

## Suggested execution batches

Tightly coupled feature (SQL redesign and its test literals cannot be verified apart from each
other) — run in ONE session: `scripts/pane-loop.sh external-sim-documentno-format all-in-one`
(or `/spec-implement all`). Tasks 1+2 are tagged `Batch: B1` because they are the same
mechanical restructuring applied to two near-identical file sections and benefit from sharing
the just-derived formula context; task 3 depends on both finishing first regardless.
