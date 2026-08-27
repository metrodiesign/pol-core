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
foundational/dependent step, not a cluster of same-type small edits.
