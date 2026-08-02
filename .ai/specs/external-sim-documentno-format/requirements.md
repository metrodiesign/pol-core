# Requirements: External Sim DocumentNo Format Alignment

> Status: approved 2026-08-02

## Overview

`docker/bootstrap/02-external-sim.sql` seeds two simulated upstream SP databases
(`hippodb` = Motor CMI/VMI, `mammothdb` = Non-Motor FIRE/MISC) that stand in for the real
production insurance systems `products-sp-gateway` talks to. The simulator's `DocumentNo`
values are currently a self-invented format (`{SaleCode}-{PolicyYear}{ReferenceBranch}/
{abbrev}/{RunningNo}[-10]`) chosen only so the seed script could parse its own numbers back
out into separate columns. It does not match the real production Reference-number
convention the user supplied (a photographed spec table from the upstream system), so any
QA/demo work that cross-references simulator output against real documents, and any future
feature that parses `DocumentNo` the way production does, would be working against fiction.
This feature re-derives `DocumentNo` to mirror the real convention's character positions,
abbreviation vocabulary, and running-number length rule, while preserving every other
seed-data invariant already locked in on `data/expand-sim-seed-200-per-side` (PR #160):
row counts (200/200), default-search visible counts (42/39), and the SaleCode/SaleFullName,
SaleCode→BrokerCode/BrokerName, ShowName→SaleCode, and ReferenceBranch/PolicyBranch/
BrokerCode pairing rules already built in prior rounds.

## REQ-1: DocumentNo Character Layout

**User Story:** As a developer testing against the simulated upstream SP, I want
`DocumentNo` to have the same character layout as the real production Reference number,
so that the simulator is a faithful stand-in and nothing downstream has to unlearn a
fictional format.

**Acceptance Criteria (EARS):**
- 1.1  THE SYSTEM SHALL compose `DocumentNo` as `{PolicyYear}{ReferenceBranch}/{Abbrev}/{RunningNo}` for every non-endorsement row, with no `SaleCode` prefix and no separator between `{PolicyYear}` and `{ReferenceBranch}` (the two concatenate directly, e.g. `69900`, matching the reference photo's `68301`).
- 1.2  THE SYSTEM SHALL compose `DocumentNo` as `1-{PolicyYear}{ReferenceBranch}/{Abbrev}/{RunningNo}` for every `ENDORSEMENT` row on `mammothdb` (Non-Motor), matching the "type 1 endorsement, year moves after the dash" rule shown in the reference spec.
- 1.3  THE SYSTEM SHALL compose `DocumentNo` as `{PolicyYear}{ReferenceBranch}/{Abbrev}/{RunningNo}1` for every `ENDORSEMENT` row on `hippodb` (Motor) — i.e. the same layout as 1.1 with the "type 1" digit appended after the running number, per the reference spec's Motor (non-CTP) endorsement position rule.
- 1.4  THE SYSTEM SHALL use `PolicyYear` (2 digits) and `ReferenceBranch` (3 digits) exactly as already stored in those columns for the row (no independent recomputation), so the string stays consistent with the columns the SP already returns.
- 1.5  THE SYSTEM SHALL use `varchar(150)` for `DocumentNo` unchanged (no column-length migration needed — the new layout is shorter than the current one).

## REQ-2: DocumentType → Abbreviation Vocabulary

**User Story:** As a developer, I want the abbreviation embedded in `DocumentNo` to reflect
the row's real `DocumentType`, using the vocabulary the production system uses, so that
ordering/search behavior tested against the simulator matches production.

**Acceptance Criteria (EARS):**
- 2.1  WHERE `SourceSystem` is `CMI` or `VMI` (Motor / hippodb) THE SYSTEM SHALL use the Thai abbreviation `กธ` for `DocumentType IN ('POLICY', 'RENEWAL')`.
- 2.2  WHERE `SourceSystem` is `CMI` or `VMI` THE SYSTEM SHALL use the Thai abbreviation `รย` for `DocumentType = 'APPLICATION'`.
- 2.3  WHERE `SourceSystem` is `CMI` or `VMI` THE SYSTEM SHALL use the Thai abbreviation `อท` for `DocumentType = 'ENDORSEMENT'`.
- 2.4  WHERE `SourceSystem` is `FIRE` or `MISC` (Non-Motor / mammothdb) THE SYSTEM SHALL use the English abbreviation `POL` for `DocumentType IN ('POLICY', 'RENEWAL')`.
- 2.5  WHERE `SourceSystem` is `FIRE` or `MISC` THE SYSTEM SHALL use the English abbreviation `APP` for `DocumentType = 'APPLICATION'`.
- 2.6  WHERE `SourceSystem` is `FIRE` or `MISC` THE SYSTEM SHALL use the English abbreviation `END` for `DocumentType = 'ENDORSEMENT'`.
- 2.7  THE SYSTEM SHALL treat `RENEWAL` as sharing its parent `POLICY` abbreviation on both sides (no distinct abbreviation), since a renewal is identified by `PreviousPolicyNumber`, not by a distinct `DocumentNo` shape.

## REQ-3: Running-Number Length

**User Story:** As a developer, I want the running-number segment's digit count to follow
the real "CTP policies get 7 digits, everything else gets 6" rule, so that length-sensitive
parsing/testing against the simulator matches production.

**Acceptance Criteria (EARS):**
- 3.1  WHERE `SourceSystem = 'CMI'` (the compulsory/พรบ. product) THE SYSTEM SHALL zero-pad the running number to 7 digits.
- 3.2  WHERE `SourceSystem` is `VMI`, `FIRE`, or `MISC` THE SYSTEM SHALL zero-pad the running number to 6 digits.
- 3.3  THE SYSTEM SHALL derive the running-number value from `PolicySequenceNo` (or another column already independently assigned to the row), not from a value parsed out of `DocumentNo` itself.
- 3.4  THE SYSTEM SHALL assign running-number values as: axis rows use their hand-written per-row index (1–14 on hippodb, 1–10 on mammothdb); generated rows use `100 + g.value` (101–286 hippodb, 101–290 mammothdb). The two ranges stay disjoint because year, branch, and abbreviation CAN coincide between an axis row and a generated row — the running number is the only component guaranteed to differ, so overlapping ranges would violate each side's `UX_Documents_DocumentNo`.

## REQ-4: PolicyYear Value Rule

**User Story:** As a developer, I want `PolicyYear` to visibly differ in style between the
two sides (Buddhist-era-looking on Motor, Gregorian-looking on Non-Motor) the way the real
spec requires, while staying deterministic across seed runs so pinned test literals do not
go stale on a date rollover.

**Acceptance Criteria (EARS):**
- 4.1  WHERE `SourceSystem` is `CMI` or `VMI` THE SYSTEM SHALL set `PolicyYear` to a fixed 2-digit literal styled as a Buddhist-era year (e.g. `69`), unchanged from the current seed.
- 4.2  WHERE `SourceSystem` is `FIRE` or `MISC` THE SYSTEM SHALL set `PolicyYear` to a fixed 2-digit literal styled as a Gregorian year, distinct from the Motor-side value, instead of the current shared constant `69`. The value `26` is illustrative only — `/spec-design` picks the exact literal; any 2-digit Gregorian-styled value different from Motor's satisfies this criterion.
- 4.3  THE SYSTEM SHALL keep `PolicyYear` a fixed literal per side (not derived from `GETDATE()`), so re-running the seed on a different date does not change any pinned `DocumentNo`/`PolicySequenceNo` test literal.

## REQ-5: Derivation Direction

**User Story:** As a maintainer of the seed script, I want `DocumentNo` computed forward
from the columns that already independently determine its parts, so the script mirrors how
production actually generates the Reference number (compose from components) instead of
the current backwards flow (parse a fabricated string to populate the columns).

**Acceptance Criteria (EARS):**
- 5.1  THE SYSTEM SHALL compute `PolicySequenceNo`, `ReferenceNo`, `SaleFullName`, `BrokerCode`, `BrokerName`, `ReferenceBranch`, `PolicyBranch`, `PolicyType`, and the premium components from a per-row identity that does not depend on parsing `DocumentNo` (e.g. the `GENERATE_SERIES` value directly, for generated rows; a literal per-row identity for axis rows).
- 5.2  THE SYSTEM SHALL compute `DocumentNo` as the last step, by concatenating the already-set `PolicyYear`, `ReferenceBranch`, `DocumentType`-derived abbreviation, and `PolicySequenceNo`/`ReferenceNo`-derived running number, per REQ-1.
- 5.3  THE SYSTEM SHALL NOT parse `DocumentNo` (no `RIGHT`/`CHARINDEX`/`REPLACE` over it) to recover a row's identity anywhere in the file once this feature lands. `CROSS APPLY` blocks that derive OTHER values (e.g. the premium components computed from `TotalPremium`) are out of scope and stay as they are.

## REQ-6: Cross-Catalog Uniqueness

**User Story:** As the owner of `shop.Products` (the central catalog both simulated SPs feed
into via `UpsertByDocumentNoAsync`), I want `DocumentNo` to stay collision-free across BOTH
`hippodb` and `mammothdb`, even after the `SaleCode` prefix that used to guarantee this
(`77`/`88`) is removed, so ingestion never violates `IX_Products_DocumentNo`.

**Acceptance Criteria (EARS):**
- 6.1  THE SYSTEM SHALL give Motor and Non-Motor two DIFFERENT fixed `PolicyYear` literals (per REQ-4.1/4.2). The Buddhist-vs-Gregorian styling is documentation flavor for realism — the actual cross-side uniqueness guarantee is simply that the two fixed literals differ, since both are hand-picked constants, not computed calendar values.
- 6.2  THE SYSTEM SHALL verify, in each side's own self-check block, that its own 200 rows have 200 distinct `DocumentNo` values (uniqueness within the side).

  > `6.3` retired by the 2026-08-02 requirements audit (F-6, see findings log below): redundant
  > once REQ-4.2 + REQ-6.1 already make cross-side collision structurally impossible via the
  > year literal alone. ID intentionally skipped, not reused.

- 6.4  THE SYSTEM SHALL replace the two now-invalid self-check assertions that pin the old `SaleCode` prefix (`DocumentNo ... NOT LIKE '77%'` / `NOT LIKE '88%'`) with assertions that verify the new layout instead (left to `/spec-design` to define the replacement check).
- 6.5  THE SYSTEM SHALL retarget mammothdb's Thai-text round-trip self-check (currently `ShowName LIKE N'%จำกัด%' AND DocumentNo LIKE N'%อค%'`) at columns that still carry Thai text after this change (e.g. `ShowName` alone), because mammothdb's `DocumentNo` becomes ASCII-only under REQ-2.4–2.6 and the current assertion would fail forever.

## REQ-7: Scope — Axis Rows and Generated Rows, Both Sides

**User Story:** As a maintainer, I want every row in both databases — the 24 hand-written
axis rows and the 386 generated rows — to use the new `DocumentNo` format, so the simulator
does not mix a fictional format on some rows with the real format on others.

**Acceptance Criteria (EARS):**
- 7.1  THE SYSTEM SHALL rewrite all 14 hippodb axis-row `DocumentNo` literals to the new format.
- 7.2  THE SYSTEM SHALL rewrite all 10 mammothdb axis-row `DocumentNo` literals to the new format.
- 7.3  THE SYSTEM SHALL rewrite the 186 hippodb and 190 mammothdb generated-row `DocumentNo` expressions to the new format.
- 7.4  THE SYSTEM SHALL preserve every axis row's existing `DocumentType`, `SourceSystem`, `SaleCode`, dates, `ShowName`, `TotalPremium`, `PaymentStatus`/`PaidDate`, and `LicensePlateNumber` values unchanged — only the `DocumentNo` string shape changes.

## REQ-8: Regression Safety — Prior Invariants Preserved

**User Story:** As the owner of PR #160, I want every seed-data invariant already locked in
on this branch to keep holding after this feature lands, so this change is additive to the
existing work, not a regression of it.

**Acceptance Criteria (EARS):**
- 8.1  THE SYSTEM SHALL keep each side's total row count at 200.
- 8.2  THE SYSTEM SHALL keep the default-search visible count at 42 (hippodb) and 39 (mammothdb).
- 8.3  THE SYSTEM SHALL keep the SaleCode↔SaleFullName, SaleCode→BrokerCode/BrokerName, ShowName→SaleCode, ReferenceBranch↔PolicyBranch, and BrokerCode→ReferenceBranch pairing invariants from prior rounds intact for every row (axis and generated).
- 8.4  THE SYSTEM SHALL keep the `SaleCode` column itself (not the `DocumentNo` string) as the sole 5-digit agent identifier used for search scoping (`@SaleCode`), unchanged by this feature.
- 8.5  WHEN the Thai-collation last-page ordering test is re-derived for the new abbreviation vocabulary (`กธ` < `รย` < `อท`, still ascending under `Thai_CI_AS` since ก < ร < อ) THE SYSTEM SHALL preserve the same "prove ordering is by Thai letter, not by number" property the current test proves, updating only the pinned literal values.

## REQ-9: Document-Number-Adjacent Fields Survive the Parsing Removal

**User Story:** As a maintainer, I want the four policy-number fields that today are derived
from the same parsed sequence as `DocumentNo` to keep being populated correctly after the
parsing flow is removed, so removing the backwards derivation does not silently null or
corrupt them.

**Acceptance Criteria (EARS):**
- 9.1  THE SYSTEM SHALL derive `PolicyNumber`, `ApplicationNumber`, `PreviousPolicyNumber`, and `EndorsementNumber` from the row's own already-set columns (`SaleCode`, `PolicyYear`, `ReferenceBranch`, `PolicySequenceNo`/`ReferenceNo`), not from any value parsed out of `DocumentNo`.
- 9.2  THE SYSTEM SHALL keep each of those four fields' existing string shapes (`{SaleCode}-{Year}{Branch}/{Seq}` for policy/application, `{SaleCode}-{PrevYear}{Branch}/{Seq-1}` for previous-policy, `E{Seq}` for endorsement) with the sequence value equal to the row's new running number per REQ-3.4 — redesigning those shapes is out of scope for this feature.
- 9.3  THE SYSTEM SHALL keep the existing conditionality of those fields unchanged (`PolicyNumber` for non-APPLICATION rows, `ApplicationNumber` for APPLICATION rows, `PreviousPolicyNumber` for RENEWAL/ENDORSEMENT rows, `EndorsementNumber` for ENDORSEMENT rows).

## Edge Cases & Open Questions

- **OPEN — "-570" suffix on Non-Motor endorsement examples.** The reference photo's example
  `1-25213/END/000173-570` has a trailing `-570` that does not fit any position the spec
  table itself defines (positions 1-18 only). Decision for this round: do not replicate it —
  treat it as a display-screen artifact outside the Reference registry, not part of
  `DocumentNo`. Flagged here for the user to confirm against the full spec later; not a
  blocker for this feature.
- **OPEN — `69304/กธ/E021653` example.** One Motor reference example in the photo has a
  running number starting with a non-digit (`E`), which contradicts the "6 running digits"
  rule stated in the same table. Treated as an uncovered edge case, not mirrored by this
  feature (all running numbers stay zero-padded numeric per REQ-3).
- **RESOLVED — `RENEWAL` abbreviation.** No distinct abbreviation exists in the source
  photo for `RENEWAL` on either side; REQ-2.7 resolves this by reusing the `POLICY`
  abbreviation, matching the business reasoning that a renewal is a new policy year
  identified via `PreviousPolicyNumber`, not via `DocumentNo` shape.
- **RESOLVED — `SourceSystem = 'CMI'` as the พรบ. (CTP) signal.** No explicit flag exists in
  the schema; `CMI` (compulsory motor insurance) is used as the พรบ. indicator per REQ-3.
  `VMI` (voluntary) and both Non-Motor `SourceSystem` values use the 6-digit running number.
- **DEFERRED TO DESIGN — the replacement self-check assertion** (REQ-6.4) and the exact
  Non-Motor `PolicyYear` literal (REQ-4.2): both need a concrete SQL design, not just a
  requirement.

### Requirements audit findings — 2026-08-02 (anchor `fe218d7`; file not yet committed at audit time)

| # | Finding | Decision |
|---|---|---|
| F-1 | REQ-5.3 as first written banned every `CROSS APPLY` in the file, which would force ripping out the premium-derivation block (`Net`/`Stamp`/`Pct`) that has nothing to do with `DocumentNo` — conflicting with REQ-8's don't-touch-unrelated-things intent | ACCEPTED (1ก): narrowed 5.3 to DocumentNo-parsing only; other `CROSS APPLY` blocks explicitly out of scope |
| F-2 | Running-number VALUES were undefined — REQ-3 fixed only digit counts, while the old source (`v.Seq` parsed from `DocumentNo`) is being removed | ACCEPTED (2ก) **with amendment**: the option's original premise ("axis 1-14 and generated 1-186 need no range separation because they can't collide") was WRONG — year+branch+abbrev CAN coincide between an axis row and a generated row (verified: hippo axis #1 and `g.value=1` would both produce `69900/กธ/000001`), so REQ-3.4 keeps the ranges disjoint: axis 1–14/1–10, generated `100+g.value` |
| F-3 | `PolicyNumber`/`ApplicationNumber`/`PreviousPolicyNumber`/`EndorsementNumber` all derive from the same parsed `v.Seq` and would break silently with the parsing removal, but no REQ covered them | ACCEPTED (3ก): added REQ-9 |
| F-4 | mammothdb's Thai round-trip self-check pins `DocumentNo LIKE N'%อค%'`, but Non-Motor abbreviations become ASCII-only (`POL`/`APP`/`END`) — the check would fail forever | ACCEPTED (4ก): added REQ-6.5 retargeting the check at Thai-bearing columns |
| F-5 | REQ-6.1 justified cross-side uniqueness with real-calendar math (543-year offset), but REQ-4.3 makes both years fixed literals — the calendar argument doesn't apply to hand-picked constants | ACCEPTED (5ก): reworded 6.1 — the guarantee is simply two different literals |
| F-6 | REQ-6.3's IF-condition can never be true once 4.2+6.1 guarantee distinct year literals | ACCEPTED (6ก): removed, ID reserved |
| F-7 | REQ-4.2's "e.g. `26`" ambiguous — example or mandate? | ACCEPTED (7ก): explicitly an example; design picks the literal |
| F-8 | REQ-1.1's `{PolicyYear}{ReferenceBranch}` juxtaposition — deliberate no-separator, or accidental omission of the old `-`? | ACCEPTED (8ก): deliberate, matches photo `68301`; wording made explicit |
