# Requirements: External Sim Shared Agent/Broker/Branch Network

> Status: approved 2026-08-02, amended 2026-08-02

## Overview

`docker/bootstrap/02-external-sim.sql` seeds two simulated upstream SP databases — `hippodb`
(Motor, CMI/VMI) and `mammothdb` (Non-Motor, FIRE/MISC) — that stand in for the real production
systems `products-sp-gateway` talks to. Both databases currently draw their sales-agent identity
(`SaleCode`, `SaleFullName`, `BrokerCode`, `BrokerName`, `ReferenceBranch`, `PolicyBranch`) from
two disjoint rosters (`77001-77006` on hippodb, `90001-90006` on mammothdb), with only two codes
made to overlap by hand as "foreign SaleCode" test probes. In reality both simulated databases
represent the **same insurance company's** sales network selling across both product lines — an
agent who sells Motor policies through hippodb is the same person, under the same broker and
branch, who sells Non-Motor policies through mammothdb. This feature replaces the two disjoint
rosters with one shared 6-agent master roster used identically by both databases, adds a
cross-database self-check that keeps that identity consistent going forward, and replaces the
now-meaningless "foreign SaleCode" exact-match test with a prefix-probe design that does not
depend on siloed rosters. This is a follow-on to `external-sim-documentno-format` (closed) on the
same branch (`data/expand-sim-seed-200-per-side`) and preserves every row-count/format invariant
that feature and its predecessors locked in, except where explicitly superseded below.

## REQ-1: Shared Agent/Broker/Branch Master Roster

**User Story:** As a developer testing against the simulated upstream SPs, I want the same
6-agent roster (SaleCode, SaleFullName, BrokerCode, BrokerName, ReferenceBranch, PolicyBranch) to
resolve identically on both hippodb and mammothdb, so that the simulator reflects one insurance
company's shared sales network instead of two coincidentally-similar but independent ones.

**Acceptance Criteria (EARS):**
- 1.1  THE SYSTEM SHALL define exactly one master mapping from `SaleCode` to
  `ReferenceBranch`/`PolicyBranch`/`SaleFullName`/`BrokerCode`/`BrokerName`, reusing hippodb's
  existing 6-entry table (`SaleCode` values `77001`-`77006`) verbatim as that master.
- 1.2  THE SYSTEM SHALL apply the master mapping from 1.1 on mammothdb in place of mammothdb's
  current independent 6-entry table (`90001`-`90006`), which SHALL be retired.
- 1.3  THE SYSTEM SHALL resolve any master `SaleCode` that appears on a row — axis or generated,
  either side — to identical `ReferenceBranch`/`PolicyBranch`/`SaleFullName`/`BrokerCode`/
  `BrokerName` values on BOTH hippodb and mammothdb (i.e. querying `SaleCode = '77003'` on either
  database returns the same agent identity). This does NOT require every master code to appear
  among a given side's axis rows — the generated-row distribution (REQ-3.1) already puts all 6
  codes on both sides.
- 1.4  THE SYSTEM SHALL leave the validate-only `BranchCode` search parameter (values
  `100`/`200`/`300`/`400`, unrelated to agent identity) unchanged by this feature — it is out of
  scope.
- 1.5  THE SYSTEM SHALL preserve the existing within-side pairing invariants (`SaleCode` ↔
  `SaleFullName`, `SaleCode` → `BrokerCode`/`BrokerName`, `ShowName` → `SaleCode`,
  `ReferenceBranch` ↔ `PolicyBranch`, `BrokerCode` → `ReferenceBranch`) for every row on both
  sides under the new shared roster.

## REQ-2: Axis-Row Migration Off the Retired Rosters

**User Story:** As a maintainer, I want every hand-written axis row currently keyed to a
retired/foreign `SaleCode` to be re-keyed onto the shared roster, so no row is left pointing at
data that no longer exists.

**Acceptance Criteria (EARS):**
- 2.1  THE SYSTEM SHALL re-key hippodb's axis row 10 (currently `SaleCode = '90001'`, seeded as
  the "foreign SaleCode" probe) to one of the 6 shared master `SaleCode` values, becoming an
  ordinary row with no special probing role.
- 2.2  THE SYSTEM SHALL re-key mammothdb's axis rows 1-8 and 10 (currently native to the retired
  `90001`-`90006` roster) to `SaleCode` values from the shared master roster.
- 2.3  THE SYSTEM SHALL leave mammothdb's axis row 9 (currently `SaleCode = '77001'`, seeded as
  the "foreign SaleCode" probe) unchanged in `SaleCode` value, its role changing from "foreign
  probe" to "ordinary row under the shared roster" only.
- 2.4  THE SYSTEM SHALL preserve every re-keyed axis row's existing `DocumentType`,
  `SourceSystem`, dates, `ShowName`, `TotalPremium`, `PaymentStatus`/`PaidDate`,
  `LicensePlateNumber`, and `BranchCode` unchanged — only the `SaleCode` value, its
  shared-roster-derived identity fields, and the values derived from them through the unchanged
  formulas (per REQ-7.3) change.
- 2.5  WHERE hippodb's axis row 10's new `SaleCode` assignment would change whether that row is
  visible to hippodb's default-search probe (`@SaleCode = '77001'`), THE SYSTEM SHALL choose the
  specific master `SaleCode` assigned to it such that hippodb's default-search visible count in
  REQ-7.2 holds. (mammothdb's re-keyed rows are NOT bound by this criterion — that side's count is
  freshly measured per REQ-6.2.)

## REQ-3: Generated-Row SaleCode Assignment Uses the Shared Roster

**User Story:** As a maintainer, I want the generated (non-axis) rows on both sides to be
distributed across the same 6-agent roster, so the bulk of each side's 200 rows also reflects the
shared company-wide sales network, not just the hand-written axis rows.

**Acceptance Criteria (EARS):**
- 3.1  THE SYSTEM SHALL replace mammothdb's generated-row `SaleCode` CASE expression (currently
  mapping to `90001`-`90006`) with one mapping to the same 6 shared master `SaleCode` values used
  by hippodb's generated rows (`77001`-`77006`).
- 3.2  THE SYSTEM SHALL keep mammothdb's existing `names.Idx`-keyed partitioning scheme (one
  `ShowName` always resolves to the same agent) unchanged in mechanism — only the target
  `SaleCode` values change, per REQ-1.5.
- 3.3  THE SYSTEM SHALL NOT require mammothdb's per-agent row distribution to numerically match
  hippodb's per-agent row distribution — each side independently distributes its own ~200 rows
  across the shared roster; only the agent *identity* (REQ-1.3) must match, not row counts per
  agent.

## REQ-4: Cross-Database Identity Consistency Self-Check

**User Story:** As the maintainer of this seed file, I want a self-check that fails loudly if the
two sides' agent identity ever drifts apart again, so the invariant REQ-1 establishes cannot
silently break in a future edit the way it could before this feature (no such check existed).

**Acceptance Criteria (EARS):**
- 4.1  THE SYSTEM SHALL add a cross-database self-check, alongside the existing
  `hippodb.dbo.Documents`/`mammothdb.dbo.Documents` `DocumentNo`-uniqueness check, that joins the
  two `Documents` tables on `SaleCode` and compares `SaleFullName`, `BrokerCode`, `BrokerName`,
  `ReferenceBranch`, and `PolicyBranch`.
- 4.2  IF any joined pair of rows shares a `SaleCode` but differs in any field named in 4.1 THEN
  THE SYSTEM SHALL `THROW` with a message identifying the offending `SaleCode` and field(s), using
  the same `THROW`-with-actual-value pattern as the file's other self-checks.
- 4.3  THE SYSTEM SHALL run this self-check for all 6 shared `SaleCode` values, not just the 2 that
  previously overlapped as foreign probes.

## REQ-5: SaleCode Exact-Match Verification Without Siloed Rosters

**User Story:** As a developer relying on the SP contract, I want proof that `@SaleCode` performs
an exact match (not a prefix/partial match) that does not depend on one side's roster being
"foreign" to the other, so the test survives the roster unification in REQ-1.

**Acceptance Criteria (EARS):**
- 5.1  THE SYSTEM SHALL replace the "foreign SaleCode" exact-match proof
  (`Sale_code_is_an_exact_scope_axis` in `SpDocumentContractTests.cs`) with a prefix-probe design:
  querying `@SaleCode` with a value that is a strict prefix of one or more real 5-digit master
  `SaleCode`s (e.g. `7700`, a prefix of all of `77001`-`77006`) but is not itself a real
  `SaleCode`.
- 5.2  THE SYSTEM SHALL assert that querying with the prefix value from 5.1 returns zero rows on
  both hippodb and mammothdb, proving `@SaleCode` does not perform prefix/`LIKE` matching.
- 5.3  THE SYSTEM SHALL rename the replaced test to reflect the new proof mechanism (no longer
  "foreign" — e.g. `Sale_code_does_not_match_by_prefix` or equivalent chosen at implementation
  time) and remove the `Side.ForeignSaleCode`/`ForeignSaleCodeSeq` fields that no longer apply.
- 5.4  THE SYSTEM SHALL NOT require seeding any dedicated landmark row to support this test — the
  probe values' non-existence as real `SaleCode`s is sufficient on its own.
- 5.5  THE SYSTEM SHALL additionally assert that querying `@SaleCode` with a value that is a
  strict non-prefix substring of one or more real master `SaleCode`s (e.g. the suffix `7001` of
  `77001`) but is not itself a real `SaleCode` returns zero rows on both databases — the replaced
  test's comment claimed proof against "partial or prefix match", and a prefix-only probe would
  prove strictly less than the original foreign-code mechanism did.

## REQ-6: Default-Search Probe Realignment

**User Story:** As a developer maintaining the integration tests, I want mammothdb's default-search
test probe to use a `SaleCode` from the new shared roster, so the test still exercises a real,
current agent identity.

**Acceptance Criteria (EARS):**
- 6.1  THE SYSTEM SHALL change mammothdb's default-search test probe `SaleCode` from the retired
  `90001` to `77001` — the same value hippodb's default-search probe already uses.
- 6.2  THE SYSTEM SHALL treat the resulting mammothdb default-search visible count as a freshly
  measured value (read from the live reseeded database), not a carry-over of the prior `39`, since
  the underlying roster and row distribution both changed.

## REQ-7: Regression Safety — Prior Invariants Preserved

**User Story:** As the owner of PR #160, I want every seed-data invariant already locked in on
this branch to keep holding after this feature lands, so this change is additive, not a
regression.

**Acceptance Criteria (EARS):**
- 7.1  THE SYSTEM SHALL keep each side's total row count at 200.
- 7.2  THE SYSTEM SHALL keep hippodb's default-search visible count at 42 (its probe `SaleCode`,
  `77001`, and the row-count math behind it, are unaffected by this feature per REQ-2.5).
- 7.3  THE SYSTEM SHALL keep every `DocumentNo`/`PolicyNumber`/`ApplicationNumber`/
  `PreviousPolicyNumber`/`EndorsementNumber` FORMULA and string shape from
  `external-sim-documentno-format` unchanged — this feature only changes which `SaleCode` (and
  its dependent identity fields) a row carries, never the `DocumentNo` family's derivation. The
  concrete VALUES on re-keyed rows legitimately change (a new `SaleCode` feeds new
  `ReferenceBranch`/`PolicyNumber` components through the unchanged formulas); that is expected
  behavior, not a violation of this criterion.
- 7.4  THE SYSTEM SHALL keep both sides' `DocumentNo` prefix invariants (`69%` hippodb, `26%`/
  `1-26%` mammothdb) and the existing within-database `DocumentNo` uniqueness self-checks
  unchanged.
- 7.5  THE SYSTEM SHALL keep the `SaleCode` column itself (not any derived string) as the sole
  5-digit agent identifier used for search scoping (`@SaleCode`), unchanged in role by this
  feature.
- 7.6  THE SYSTEM SHALL re-pin every integration-test literal derived from a re-keyed row
  (`AxisPolicyNumber`, `AxisDocumentNo`, `AxisReferenceBranch`, `PaidPolicyNumber`, seq-derived
  `Side` literals, and mammothdb's `TotalRows`/`TotalPages`/`LastPageRows`) against the live
  reseeded database — never by hand-derivation — same discipline as every prior round on this
  branch.

## REQ-8: Seed-File Narrative Alignment

**User Story:** As a future maintainer reading the seed file, I want its header commentary to
describe the shared-network model this feature establishes, so the file's own documentation does
not contradict its data — the same stale-narrative failure mode Codex review just caught on PR
#160 against the `products-sp-gateway` handoff.

**Acceptance Criteria (EARS):**
- 8.1  THE SYSTEM SHALL rewrite the file-header commentary that currently describes two disjoint
  per-side agent rosters so it describes the single shared 6-agent/broker/branch network operated
  by one insurance company across both simulated systems (the two databases remain separate
  systems on separate servers — only the ownership/roster narrative changes).
- 8.2  THE SYSTEM SHALL add a reference line pointing at this spec
  (`external-sim-shared-agent-network`) alongside the file's existing spec reference lines.
- 8.3  THE SYSTEM SHALL leave header entries that remain true (e.g. the `DocumentNo`-format notes
  from `external-sim-documentno-format`) untouched.

## Edge Cases & Open Questions

- **RESOLVED — scope of "BranchCode".** Confirmed with the user this refers to the
  `ReferenceBranch`/`PolicyBranch` identity pair, not the literal `BranchCode` search parameter
  column (validate-only, unrelated to agent identity) — REQ-1.4 makes this explicit.
- **RESOLVED — fate of the two former foreign-probe axis rows.** Confirmed with the user they
  become ordinary rows under the shared roster (REQ-2.1, 2.3) rather than being deleted or kept as
  special cases — axis row count stays 14 (hippodb) / 10 (mammothdb).
- **RESOLVED — mammothdb's new default-search probe.** Confirmed with the user it becomes `77001`,
  matching hippodb's, rather than a different roster member (REQ-6.1).
- **DEFERRED TO DESIGN — exact `SaleCode` assignment per re-keyed row.** REQ-2.2 and REQ-2.5
  require specific `SaleCode` choices per re-keyed axis row (and the generated-row distribution
  shape) sufficient to satisfy REQ-7.2's exact `42` figure and to produce a defensible `39`-or-new
  figure for mammothdb (REQ-6.2) — `/spec-design` picks the concrete mapping; `/spec-tasks`/
  implementation verifies the resulting counts against the live database, not by hand-derivation
  (same discipline as every prior round on this branch).
- **RESOLVED (F-4, 2026-08-02 audit) — probe literals.** A single shared prefix probe (`7700`)
  works identically on both databases since the roster is now shared; the audit additionally added
  a non-prefix substring probe (REQ-5.5, e.g. `7001`) so the replaced test's full "no partial or
  prefix match" claim survives. `/spec-design` confirms both exact literals (or picks alternatives
  if a collision with any seeded searchable field is found).

### Requirements audit findings — 2026-08-02 (anchor `fe218d7`; file not yet committed at audit time)

| # | Finding | Decision |
|---|---|---|
| F-1 | REQ-1.3 as first written ("for every row on both sides") was readable as requiring all 6 master codes to appear among each side's axis rows — unverifiable-by-design for hippodb, whose 13 untouched axis rows nearly all use `77001` | ACCEPTED (option ก): reworded to bind identity resolution to codes that appear; noted generated rows already spread all 6 codes on both sides via REQ-3.1 |
| F-2 | REQ-2.5 cited REQ-7.2 (hippodb-only count) while covering all re-keyed rows, contradicting REQ-6.2's freshly-measured mammothdb count | ACCEPTED (option ก): scoped REQ-2.5 to hippodb axis row 10 explicitly; mammothdb rows released to REQ-6.2 |
| F-3 | REQ-7.3 "formula and value shape unchanged" was readable as freezing concrete literals, impossible for re-keyed rows whose `SaleCode`-derived components legitimately change | ACCEPTED (option ก): clarified formulas/shapes vs values; added REQ-7.6 mandating live-DB re-pin of every literal derived from a re-keyed row |
| F-4 | REQ-5's prefix-only probe proved strictly less than the replaced test's "no partial or prefix match" claim (substring/`%value%` bugs uncovered) | ACCEPTED (option ก): added REQ-5.5 non-prefix substring probe (e.g. `7001`); Open Question updated to RESOLVED |
| F-5 | Re-keying hippodb axis row 10 while keeping its `ShowName` (REQ-2.4) could collide with the ShowName→SaleCode invariant if that ShowName already maps to a different agent | DISMISSED (option ข): REQ-1.5 already binds the invariant "for every row on both sides" — a separate criterion would duplicate it; design/implementation must pick row 10's code accordingly and the seed's own pairing checks would catch a violation |
| F-6 | File-header narrative ("two disjoint rosters, separate owners") contradicts this feature's premise — same stale-doc failure mode Codex just flagged on PR #160 | ACCEPTED (option ก): added REQ-8 (header rewrite + spec reference line, true entries untouched) |

Post-amendment verify round (fresh-context adversarial pass, same anchor): PASS, no blockers.
Two wording fixes applied — REQ-2.5's WHERE clause retargeted from "falls inside the default
search window" (vacuous: re-keying never moves dates) to "is visible to hippodb's default-search
probe"; REQ-2.4's "only the SaleCode changes" cross-referenced to REQ-7.3 so derived-value changes
on re-keyed rows don't read as a violation. Two remarks accepted as-is: REQ-5.5 carries a short
inline rationale (kept — it records why one probe is insufficient); REQ-7.6's "never by
hand-derivation" is a process requirement, verifiable only as "pinned literals match the live DB"
(consistent with every prior round on this branch).
