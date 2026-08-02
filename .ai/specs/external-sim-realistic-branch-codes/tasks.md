# Implementation Tasks: External Sim Realistic Branch Codes

> Status: approved 2026-08-02

> Each task is a cohesive, independently verifiable slice. Implement a whole task
> in one pass (it may touch many files). Decompose into sub-steps yourself at
> execution time — do NOT pre-split tasks here.

- [x] 1. `02-external-sim.sql` — swap `ReferenceBranch` values, remove the dead
     `BranchCode` column, both databases
     In the `ReferenceBranch` `CROSS APPLY` CASE expression on both hippodb
     (`:519-524`) and mammothdb (`:1000-1004`), replace the five output literals
     `900/901/902/903/904` with `301/315/220/335/450` (design.md "ReferenceBranch
     value scheme") — keep every `WHEN` condition, the `IN ('77001','77006')`
     pairing, and the column alias unchanged; do not touch `PolicyBranch`/
     `SaleFullName`/`BrokerCode`/`BrokerName` in the same block. Remove
     `dbo.Documents.BranchCode` from both `CREATE TABLE` statements (`:76`
     hippodb, `:615` mammothdb) and from all 4 INSERT sites (axis-row `VALUES`
     and generated-row `SELECT`, both databases) — drop the column from each
     INSERT's column list and drop the paired value expression (per-row literal
     in axis rows, the `CASE g.value % 4 ...` expression in generated rows).
     Rewrite header comment "DELIBERATE DEVIATIONS" #2 (`:32-35`) per design.md's
     "BranchCode column removal" section — state that `dbo.Documents` has no
     `BranchCode` column because §5.2's output contract has none, that
     `@BranchCode` stays a required validated input parameter, and that this
     spec supersedes `products-sp-gateway` REQ-2.11 by reference. Leave
     `@BranchCode` parameter declare/trim/`THROW 50004` (`:119/144/158-159`
     hippodb, `:655/679/690-691` mammothdb) and `ReferencePre`/`SaleCode`/
     `PolicyYear` literals untouched.
     Satisfies: REQ-1 (all criteria), REQ-2 (all criteria), REQ-3.1, REQ-4 (all
     criteria), REQ-5 (all criteria — non-regression), REQ-6 (all criteria —
     non-regression), REQ-7 (all criteria — self-check regression).
     Verify: `docker compose up pol-db-init` prints `hippodb OK (200 documents,
     42 in the default search window).` and `mammothdb OK (200 documents, 40 in
     the default search window).` with no `THROW`; live query
     (`docker exec pol-db /opt/mssql-tools18/bin/sqlcmd`) confirms all 6 master
     `SaleCode`s resolve to the new `ReferenceBranch` values on both databases,
     `INFORMATION_SCHEMA.COLUMNS` shows no `BranchCode` row for `dbo.Documents`
     on either database, cross-database identity self-check and
     roster-completeness/`ShowName`→`SaleCode` self-checks still pass with no
     SQL edit to the checks themselves, and a spot-check `@BranchCode = ""`
     call still `THROW 50004`.
     Evidence:
       - test: `docker compose up pol-db-init` -> exit 0, printed `02-external-sim:
         hippodb OK (200 documents, 42 in the default search window).` and
         `02-external-sim: mammothdb OK (200 documents, 40 in the default search
         window).`, no `THROW`
       - live query: `SELECT DISTINCT SaleCode, ReferenceBranch FROM
         {hippodb|mammothdb}.dbo.Documents` -> all 6 `SaleCode`s (77001-77006)
         resolve to `301/315/220/335/450` identically on both databases (77001 and
         77006 both `301`); `INFORMATION_SCHEMA.COLUMNS` for `dbo.Documents` on
         both databases returns 0 rows for `BranchCode`; sample row confirms
         `DocumentNo = '69301/กธ/910001'` / `PolicyNumber = '77001-69301/910001'`
         for `SaleCode = '77001'` (matches design.md's worked example); `EXEC
         hippodb.dbo.usp_Motor_SearchDocument @BranchCode = '', @SaleCode =
         '77001'` -> `Msg 50004 ... BranchCode is required.`
       - viewports: n/a — logic-only (SQL/DB, no UI)
       - deviations: two issues found and fixed during this task, both confirmed
         with the user before applying:
         (1) the persistent `pol-db-data` docker volume already had
         `dbo.Documents` from a prior bootstrap run, so `CREATE TABLE ... IF
         OBJECT_ID(...) IS NULL` did not re-run and `BranchCode` survived the
         first `docker compose up pol-db-init` (data-only reseed, stale schema —
         confirmed via `INFORMATION_SCHEMA.COLUMNS` still showing the column).
         Fixed with a scoped `ALTER TABLE {hippodb|mammothdb}.dbo.Documents DROP
         COLUMN BranchCode` (not `DROP DATABASE` — that hits the repo's
         destructive-guard hook and would also nuke the unrelated `VCentralPay`
         DB sharing the same volume), then reran `docker compose up pol-db-init`
         clean. One-time local-dev volume reconciliation; a genuinely fresh
         volume (CI, new clone) needs no such step since the corrected `CREATE
         TABLE` in the script already omits `BranchCode`.
         (2) design.md's original `101`/`115` choice for SaleCode `77001`/`77006`/
         `77002` collided with the `'91'` `PolicySequenceNo` search marker
         `external-sim-documentno-format` embeds in axis rows (`'69'+'101'` =
         `'69101'` contains `'91'`) — caught by task 2's
         `SpDocumentContractTests.The_search_window_is_evaluated_per_row_when_the_document_type_is_ALL`
         (Motor) failing with every SaleCode-77001 row false-matching
         `SearchText='91'` instead of just the 4 intended axis rows. Amended
         `requirements.md` (new REQ-2.4) and `design.md` to `301`/`315` (leading
         digit `3`, no `'91'`/`'80'` collision at the `PolicyYear+ReferenceBranch`
         boundary or internally), re-applied here, and re-pinned the two
         Integration.Tests files in task 2. See task 2's Evidence for the
         corrected-value test run.

- [x] 2. Integration.Tests — re-pin literals/comments the value swap and column
     removal moved
     Depends on: 1.
     In `SpDocumentContractTests.cs`: re-pin `MotorSide.PolicyYearBranch` and
     `NonMotorSide.PolicyYearBranch` from the live reseeded database (expect
     `"69301"`/`"26301"` per design.md's table, post REQ-2.4 correction — confirm
     live, do not hand-derive). Update the comment in `Branch_code_is_validated_but_never_filters`
     (`:390-393`) to drop the "seed spreads rows over branches 100/200/300/400"
     claim (no column exists to spread over anymore) — leave the `@BranchCode`
     values (`"100"`/`"400"`/`"999"`) and assertion logic unchanged. In
     `SpDocumentGatewayIntegrationTests.cs`: re-pin `AxisReferenceBranch`,
     `AxisPolicyNumber`, `AxisDocumentNo`, `PaidPolicyNumber` (`MotorSide`/
     `NonMotorSide`, `:54-79`) and their `// SaleCode ... -> branch ...` comments
     from the live database; update the comment in
     `The_branch_code_is_sent_from_options_and_only_validates` (`:198-199`) to
     match the new reality (no column to match against). Do not change
     `TotalRows`/`TotalPages`/`LastPageRows` (unaffected by `ReferenceBranch`).
     Sweep both files for any other literal downstream of the value swap the
     list above doesn't already name.
     Satisfies: REQ-8 (all criteria).
     Verify: `source .env.integration && dotnet test
     tests/Integration.Tests/Integration.Tests.csproj --filter
     "Category=Integration"` — all green, no skipped/failed.
     Evidence:
       - test: `source .env.integration && dotnet test
         tests/Integration.Tests/Integration.Tests.csproj --filter
         "Category=Integration"` -> `Passed! - Failed: 0, Passed: 113, Skipped: 0,
         Total: 113` (first run against the initial `101`/`115` values failed 1:
         `The_search_window_is_evaluated_per_row_when_the_document_type_is_ALL(key:
         "Motor")` — see task 1's deviations §2 for the root cause and the
         `301`/`315` spec correction; rerun after the fix is the 113/113 result
         above)
       - live re-pin source: `SELECT DocumentNo, PolicyNumber, ReferenceBranch
         FROM {hippodb|mammothdb}.dbo.Documents WHERE PolicySequenceNo IN
         ('910001','910007','000001','000007')` confirmed
         `69301/กธ/910001`/`77001-69301/910001` (hippodb axis),
         `26301/POL/000001`/`77001-26301/000001` (mammothdb axis), and the PAID
         rows' `PolicyNumber` (`77001-69301/910007`, `77001-26301/000007`) before
         pinning `PolicyYearBranch`/`AxisReferenceBranch`/`AxisPolicyNumber`/
         `AxisDocumentNo`/`PaidPolicyNumber` in both test files
       - viewports: n/a — logic-only (no UI)
       - deviations: none beyond the REQ-2.4 value correction recorded in task 1
         (both test files' literals reflect the corrected `301`/`315`, not the
         original `101`/`115`)

- [x] 3. Docs + closed-spec footnote + final DoD gate
     Depends on: 1, 2.
     Update the `DocumentNo` example (`docs/reference/products.md:160`) and the
     standalone `ReferenceBranch` example row (`:162`, currently `001`) from the
     live reseeded database. Append a footnote to
     `products-sp-gateway/HANDOFF.md` (append-only, same pattern as commit
     `9868cf4`) pointing to this spec as the current-state reference for
     `ReferenceBranch`/`BranchCode`. Do not edit
     `products-sp-gateway/requirements.md`, `design.md`, or `tasks.md`.
     Satisfies: REQ-9 (all criteria), REQ-10 (all criteria).
     Verify: `dotnet build pol-core.slnx -warnaserror` — 0 errors, 0 warnings;
     `dotnet test pol-core.slnx` (full solution) — all green; `bash
     scripts/spec-trace.sh external-sim-realistic-branch-codes` — prints `OK:`;
     `docker compose up pol-db-init` — clean rerun, no `THROW`.
     Evidence:
       - test: `dotnet build pol-core.slnx -warnaserror` -> `64 projects, 0 errors,
         0 warnings`; `dotnet test pol-core.slnx` -> every project `Passed!`
         (Carts.Tests 15, BuildingBlocks.Tests 43, Merchants.Tests 120,
         Orders.Tests 76, Payments.Tests 162, Products.Tests 137, Admins.Tests 95,
         Iam.Tests 61, Architecture.Tests 200, Integration.Tests 113,
         Hosts.Tests 379 — 0 failed across all); `bash scripts/spec-trace.sh
         external-sim-realistic-branch-codes` -> `OK: 'external-sim-realistic-
         branch-codes' เกณฑ์ 34 ข้อ ถูกอ้างครบใน design.md และ tasks.md, EARS
         lint ผ่านทุกข้อ`; `docker compose up pol-db-init` -> exit 0, both
         `hippodb OK`/`mammothdb OK` lines, no `THROW`
       - docs: `docs/reference/products.md:160` `DocumentNo` example ->
         `69301/กธ/910001`; `:162` `ReferenceBranch` example -> `301` (both from
         live reseeded DB, matches SaleCode `77001` axis row); footnote appended
         to `products-sp-gateway/HANDOFF.md`'s existing current-state blockquote
         (append-only, same spot commit `9868cf4` touched) pointing to this spec
         for `ReferenceBranch`/`BranchCode`; `products-sp-gateway/requirements.md`,
         `design.md`, `tasks.md` untouched (verified via `git status` scope)
       - viewports: n/a — logic-only (SQL/DB/docs, no UI)
       - deviations: none

## Suggested execution batches

Coupled feature (test literals and docs in tasks 2-3 can only be read correctly
after task 1's live reseed lands) — run in ONE session:
`scripts/pane-loop.sh external-sim-realistic-branch-codes all-in-one` (or
`/spec-implement all`). No `Batch:` tags — each task is its own
foundational/dependent step, not a cluster of same-type small edits.
