# Design: External Sim Shared Agent/Broker/Branch Network

> Status: approved 2026-08-02

## Architecture Overview

Single-file change, `docker/bootstrap/02-external-sim.sql`, plus `SpDocumentContractTests.cs` and
`SpDocumentGatewayIntegrationTests.cs`. No new components, no schema migration. `hippodb` and
`mammothdb` remain two separate SQL Server databases (still no FK, no cross-database transaction,
still simulating two separate back-office systems technically) — only the **business narrative and
the agent/broker/branch data both databases draw from** change, from "two independent rosters that
happen to share two hand-synced codes" to "one shared 6-agent master roster, applied identically on
both sides."

The master mapping (`SaleCode` → `ReferenceBranch`/`PolicyBranch`/`SaleFullName`/`BrokerCode`/
`BrokerName`) already exists verbatim on hippodb (`77001`-`77006`) — this feature does not invent a
new table, it **retires mammothdb's independent `90001`-`90006` mapping and duplicates hippodb's
existing CASE expressions into mammothdb's blocks**, byte-identical (each database block already
holds a full, self-contained identity-CASE set — hippodb's and mammothdb's — so this duplicates
hippodb's set into mammothdb's slot; T-SQL has no cross-database shared-expression mechanism this
feature would otherwise need). Hippodb's own CASE expressions also drop their now-dead `'90001'`
arm once axis row 10 is re-keyed off it (REQ-1.1's "exactly one master mapping" means a clean
6-entry table on both sides, not 6 live entries plus a dead 7th).

Four axis rows move off retired/foreign codes (hippodb row 10; mammothdb rows 1-8, 10); one stays
byte-identical in value (mammothdb row 9, already `77001`) with its role note updated. Every
generated row on both sides already lands on one of the 6-code roster once mammothdb's
generated-row `SaleCode` CASE is retargeted (REQ-3.1) — no new generation logic, only the six
target literals change.

## Sequence Diagrams

```mermaid
sequenceDiagram
    participant HippoInsert as hippodb INSERT (axis + generated)
    participant HippoUpdate as hippodb UPDATE ... SET
    participant MammothInsert as mammothdb INSERT (axis + generated)
    participant MammothUpdate as mammothdb UPDATE ... SET
    participant Self as mammothdb self-check block

    Note over HippoInsert: unchanged — already SaleCode 77001-77006 (axis: row 10 re-keyed off 90001)
    HippoInsert->>HippoUpdate: row has SaleCode from the 6-code master roster
    HippoUpdate->>HippoUpdate: ReferenceBranch/PolicyBranch/SaleFullName/BrokerCode/BrokerName = f(SaleCode) (unchanged CASE, REQ-1.1)

    Note over MammothInsert: axis rows 1-8,10 re-keyed to master roster (REQ-2.2); row 9 unchanged value (REQ-2.3); generated-row CASE retargeted 90001-90006 -> 77001-77006 (REQ-3.1)
    MammothInsert->>MammothUpdate: row has SaleCode from the SAME 6-code master roster
    MammothUpdate->>MammothUpdate: ReferenceBranch/PolicyBranch/SaleFullName/BrokerCode/BrokerName = f(SaleCode) (hippodb's CASE duplicated verbatim, REQ-1.2)
    MammothUpdate->>MammothUpdate: DocumentNo/PolicyNumber/... family recomputed from the new SaleCode-derived ReferenceBranch (REQ-7.3 — formula unchanged, values shift on re-keyed rows)

    MammothUpdate->>Self: existing DocumentNo-uniqueness cross-db check (unchanged, REQ-7.4)
    MammothUpdate->>Self: NEW cross-db identity check (REQ-4): hippodb.Documents JOIN mammothdb.Documents ON SaleCode, THROW on any SaleFullName/BrokerCode/BrokerName/ReferenceBranch/PolicyBranch mismatch, all 6 codes
    Self->>Self: row counts still 200/200 (REQ-7.1); hippodb default-search still 42 (REQ-7.2); mammothdb default-search freshly measured under SaleCode 77001 (REQ-6.1/6.2)
```

## Data Models & Interfaces

### Master roster (REQ-1.1) — reused verbatim from hippodb, no values change

```
SaleCode | ReferenceBranch | PolicyBranch   | SaleFullName              | BrokerCode | BrokerName
77001    | 900              | สำนักงานใหญ่    | นายกิตติพงศ์ อารีย์วงศ์      | 701        | บริษัท เอเซียรุ่งเรือง อินชัวรันส์ โบรกเกอร์ จำกัด
77002    | 901              | สาขาสีลม        | นางสาวสุนิสา วงศ์สว่าง       | 702        | บริษัท กรุงสยาม นายหน้าประกันภัย จำกัด
77003    | 902              | สาขาเชียงใหม่   | นายเอกรัตน์ ธีรวุฒิ           | 703        | บริษัท ธนบุรี อินชัวรันส์ โบรกเกอร์ จำกัด
77004    | 903              | สาขาหาดใหญ่     | นางสาวจิราพร คงเจริญ         | 704        | บริษัท ภูมิภาคประกันภัย นายหน้า จำกัด
77005    | 904              | สาขาขอนแก่น     | นายภาณุวัฒน์ สุขประเสริฐ      | 705        | บริษัท เอ็น พี ที อินชัวรันส์ โบรกเกอร์ จำกัด
77006    | 900              | สำนักงานใหญ่    | นางเบญจวรรณ ทองอยู่          | 701        | บริษัท เอเซียรุ่งเรือง อินชัวรันส์ โบรกเกอร์ จำกัด
```

mammothdb's `90001`-`90006` mapping (and its distinct `SaleFullName`/`ReferenceBranch` values) is
retired entirely — REQ-1.2. mammothdb's `UPDATE`'s `ReferenceBranch`/`PolicyBranch`/`SaleFullName`/
`BrokerCode`/`BrokerName` CASE expressions are replaced with hippodb's CASE expressions above,
verbatim, keyed the same way (`d.SaleCode`). hippodb's own CASE expressions lose their `'90001'`
arm (5 places — `PolicyBranch`, `SaleFullName`, `BrokerCode`, `BrokerName`, `ReferenceBranch`'s
`rb` CROSS APPLY) at the same time, so both sides end up with the identical 6-arm CASE, no dead
7th arm surviving on either side.

### Axis-row re-keying map (REQ-2)

| Side | Row | Old `SaleCode` | New `SaleCode` | Rationale |
|---|---|---|---|---|
| hippodb | 10 | `90001` (foreign probe, retired role) | `77002` | `77002` is the ONE master code that already resolves to `ReferenceBranch = '901'` — the exact branch `90001` used to resolve to (`02-external-sim.sql:511`, `WHEN d.SaleCode IN ('77002','90001') THEN '901'`). Picking it means this row's `ReferenceBranch`/`DocumentNo`/`PolicyNumber` values are UNCHANGED by the re-key (no re-pin needed for this specific row beyond the `SaleCode`/`SaleFullName`/`BrokerCode`/`BrokerName` columns themselves) — the lowest-risk choice, not an arbitrary one. It also happens to keep the row outside hippodb's `77001`-scoped default-search predicate (REQ-2.5/REQ-7.2's `42`), but that's true of any non-`77001` code — the branch-continuity property is why `77002` specifically was picked over `77003`-`77006`. |
| mammothdb | 1-8 | `90001`-`90006` (native roster, retired) | `77001` | **Not a stylistic/mechanical choice — required.** Rows 1, 4, 5, 6, 7, 8, 10 are the exact landmark rows mammothdb's own default-search-scoped tests key off today (`PolicySeq`=row 1, `RenewalKeptSeq`/`RenewalDroppedSeqs`=rows 4/5, `ApplicationSeq`=row 6, `PaidSeqs`=rows 7/8, `LikeMetacharacterSeq`=row 10, `Coverage_bounds_are_inclusive_on_both_ends`/`Document_type_filters_exactly`/`A_paid_date_bound_forces_the_result_to_paid_documents`/`Insured_name_escapes_like_metacharacters` in `SpDocumentContractTests.cs`). Once REQ-6.1 moves mammothdb's default-search probe to `77001`, every one of those tests only sees rows whose `SaleCode = '77001'` — any row left on a different code becomes invisible to its own test (silent vacuous pass or an outright failure), not merely "off-brand." |
| mammothdb | 9 | `77001` (foreign probe, retired role) | `77001` (unchanged value, REQ-2.3) | Already the master roster's main code — no value change, only its role note (comment) changes from "foreign probe" to "ordinary row." |
| mammothdb | 10 | `90001` | `77001` | Same requirement as rows 1-8 — it's `LikeMetacharacterSeq`'s landmark row, queried under the default probe. |

Net effect: all 10 mammothdb axis rows carry `SaleCode = '77001'` after this feature (row 9 already
did; rows 1-8 and 10 join it) — this is a direct consequence of REQ-6.1's probe choice, not an
independent design preference. No axis row anywhere retains a retired `9000x` code.

**Implementation must verify before finalizing this map** (per REQ-1.5, dismissed finding F-5 in
requirements.md): none of the re-keyed rows' preserved `ShowName` values (REQ-2.4) already belong
to a different `SaleCode` elsewhere in that row's own database — read every axis row's `ShowName`
against the live seeded data before assigning, not by inspection of this table alone. Axis-row
`ShowName`s are hand-written, distinctive test-scenario strings (not drawn from the generated-row
name pool), so a collision is unlikely but must be confirmed live, same discipline as REQ-7.6.

### Generated-row `SaleCode` CASE retargeting (REQ-3.1)

mammothdb's generated-row `SaleCode` expression keeps its existing `names.Idx`-keyed structure
(REQ-3.2) — only the six target literals change, made identical to hippodb's existing expression:

```sql
CASE WHEN names.Idx BETWEEN 0  AND 6  THEN '77001'   -- was '90001'
     WHEN names.Idx BETWEEN 7  AND 13 THEN '77002'   -- was '90002'
     WHEN names.Idx BETWEEN 14 AND 20 THEN '77003'   -- was '90003'
     WHEN names.Idx BETWEEN 21 AND 26 THEN '77004'   -- was '90004'
     WHEN names.Idx BETWEEN 27 AND 32 THEN '77005'   -- was '90005'
     ELSE                                  '77006' END  -- was '90006'
```

This is byte-identical to hippodb's existing generated-row `SaleCode` CASE. Each side still
computes its own `names.Idx` independently from its own `ShowName` pool (REQ-3.3 — no cross-side
row-count parity required), so no actual data collision — both sides simply apply the same
code-to-agent rule independently.

### Cross-database identity self-check (REQ-4)

Added to mammothdb's self-check block, immediately after the existing `DocumentNo`-uniqueness
cross-db check (`hippodb.dbo.Documents` JOIN `mammothdb.dbo.Documents` ON `DocumentNo`,
`02-external-sim.sql:1027-1029`). The file has `SET ANSI_NULLS ON`, so a plain `<>` chain would
silently miss any future drift where one side's `CASE` gains a `NULL` result the other side
doesn't (e.g. someone drops one `WHEN` arm on only one side) — `NULL <> 'x'` evaluates to
`UNKNOWN`, not `TRUE`, so the row would never enter the `IF EXISTS`. Use the `EXCEPT` idiom instead
(NULL-safe row comparison, standard SQL Server pattern), and capture the offending `SaleCode` into
a variable so the message satisfies REQ-4.2's "identifying the offending SaleCode" (all of the
file's other self-checks already do this via `CONCAT`, e.g. `@rowMsg`/`@visibleMsg` — a static
message would be inconsistent with the file's own established pattern):

```sql
DECLARE @identityDriftCode varchar(20) = (
    SELECT TOP 1 h.SaleCode
    FROM (SELECT DISTINCT SaleCode, SaleFullName, BrokerCode, BrokerName, ReferenceBranch, PolicyBranch
          FROM hippodb.dbo.Documents) h
    JOIN (SELECT DISTINCT SaleCode, SaleFullName, BrokerCode, BrokerName, ReferenceBranch, PolicyBranch
          FROM mammothdb.dbo.Documents) m ON m.SaleCode = h.SaleCode
    WHERE EXISTS (
        SELECT h.SaleFullName, h.BrokerCode, h.BrokerName, h.ReferenceBranch, h.PolicyBranch
        EXCEPT
        SELECT m.SaleFullName, m.BrokerCode, m.BrokerName, m.ReferenceBranch, m.PolicyBranch
    ));
IF @identityDriftCode IS NOT NULL
BEGIN
    DECLARE @identityMsg nvarchar(400) = CONCAT(
        N'02-external-sim: agent identity drifted between hippodb and mammothdb for SaleCode ',
        @identityDriftCode, N' — SaleFullName/BrokerCode/BrokerName/ReferenceBranch/PolicyBranch must match on both sides.');
    THROW 51002, @identityMsg, 1;
END
```

`51002` matches the file's actual convention: every existing self-check in this file reuses the
same `THROW 51002` code (it is NOT a sequential `5000x` range — that range, `50001`-`50009`, is the
SP's own runtime rejection-error space, mapped to HTTP 400 by `SpDocumentGateway` and pinned by
`Every_documented_rejection_throws_its_own_error_number`; a bootstrap self-check must not collide
with it).

Runs after both sides are fully seeded and after both sides' `UPDATE` passes complete (verified
against the file's actual structure: hippodb's block ends at line 529, its self-checks run at
531-579, mammothdb's block starts at 585, and the existing cross-db check already sits at
1027-1029 — well after mammothdb's own `UPDATE` completes at line 997), so it sees final data on
both sides — same ordering the existing `DocumentNo` cross-check already relies on. Cost is
bounded — the two `DISTINCT` subqueries collapse each side to at most 6 rows before the join, so
the comparison is O(6×6), not O(200×200).

### Roster-completeness self-check (REQ-4.3)

The identity check above only compares `SaleCode`s present on BOTH sides (an `INNER JOIN`) — it
would pass even if a future edit shrank one side's roster to fewer than 6 codes, as long as the
codes that remain still agree. Add one count assertion per side (in each side's own self-check
block, alongside the existing row-count check) so "all 6" is actually enforced, not just true by
the current data's coincidence:

```sql
IF (SELECT COUNT(DISTINCT SaleCode) FROM dbo.Documents) <> 6
BEGIN
    THROW 51002, N'02-external-sim: expected exactly 6 distinct SaleCode values (the shared master roster).', 1;
END
```

### ShowName→SaleCode invariant self-check (REQ-1.5, strengthens the axis-row re-key boundary)

REQ-1.5 requires the `ShowName`→`SaleCode` pairing invariant to keep holding, and the axis-row
re-keying in REQ-2 is exactly the point where it's at risk (a re-keyed row keeps its `ShowName`
per REQ-2.4, which could collide with a name already assigned elsewhere under the target
`SaleCode`). Today's data has no collision (every axis-row `ShowName` on both sides is disjoint
from the other 40-name generated-row pool), but nothing previously asserted this — add one
self-check per side, in the same block as the other within-side pairing checks:

```sql
IF EXISTS (SELECT 1 FROM dbo.Documents GROUP BY ShowName HAVING COUNT(DISTINCT SaleCode) > 1)
BEGIN
    THROW 51002, N'02-external-sim: a ShowName resolves to more than one SaleCode (ShowName->SaleCode pairing invariant violated).', 1;
END
```

The other REQ-1.5 invariants (`SaleCode`↔`SaleFullName`, `SaleCode`→`BrokerCode`/`BrokerName`,
`ReferenceBranch`↔`PolicyBranch`, `BrokerCode`→`ReferenceBranch`) remain guaranteed by construction
— every one of those columns is set by a `CASE`/`CROSS APPLY` keyed on the single column
`d.SaleCode`, so two different values for the same `SaleCode` are structurally impossible within a
side. `ShowName`→`SaleCode` is the one direction NOT structurally guaranteed (it depends on no two
independently-authored rows picking the same `ShowName` under different codes), which is why it
gets an explicit check and the others don't.

### Test changes (REQ-5, REQ-6, REQ-7.6)

`SpDocumentContractTests.cs`:
- Remove `Side.ForeignSaleCode` / `Side.ForeignSaleCodeSeq` (both `MotorSide` and `NonMotorSide`).
- Add `Side.SaleCodePrefixProbe = "7700"` and `Side.SaleCodeSubstringProbe = "7001"` (or a single
  shared constant, since both sides now share one roster — REQ-5's resolved open question) plus
  helper assertions.
- Rename `Sale_code_is_an_exact_scope_axis` → `Sale_code_does_not_match_by_prefix_or_substring`
  (covers REQ-5.1/5.2/5.5 in one `[Theory]`, two assertions per side: prefix probe → 0 rows,
  substring probe → 0 rows).
- Re-pin `NonMotorSide`'s `TotalRows`/`TotalPages`/`LastPageRows`, **`PolicyYearBranch`** (currently
  `"26901"` — must become `"26900"`, since mammothdb's axis rows move from `ReferenceBranch '901'`
  under retired `90001` to `'900'` under `77001`; this field is branch-derived, not seq-derived, so
  it is easy to miss if only the "seq-derived `Side` literals" wording of REQ-7.6 is followed
  literally — call it out explicitly here), and any `SeqPrefix`-family literals whose underlying
  row distribution shifted because mammothdb's roster changed — read from the live reseeded
  database (REQ-7.6), not derived here. `PolicyYearBranch` feeds `Policy_number_and_application_number_match_exactly`'s
  expected `PolicyNumber`/`ApplicationNumber` strings — missing it fails that test.
- `NonMotorSide.SaleCode` changes from `"90001"` to `"77001"` (REQ-6.1).

`SpDocumentGatewayIntegrationTests.cs`:
- `NonMotorSide.SaleCode` → `"77001"`; `AxisReferenceBranch` comment ("SaleCode 90001 -> broker 702
  -> branch 901") updated to reflect **mammothdb axis row 1** (the row `AxisPolicyNumber`/
  `AxisDocumentNo` actually target, seq `000001`) moving from branch `901` to `900` under the new
  roster — re-derive `AxisReferenceBranch`/`AxisPolicyNumber`/`AxisDocumentNo`/`PaidPolicyNumber`
  from the live database (REQ-7.6).
- The hardcoded `Assert.Equal(2L, result.Page.TotalPages)` in
  `A_default_search_maps_the_metadata_row_and_the_page` sits outside the `Side` record — REQ-7.6
  covers it too (any literal downstream of a changed row count, named or not, gets re-verified
  against the live DB, not just the ones enumerated as examples).

`docker/bootstrap/02-external-sim.sql` bootstrap `PRINT` statement (mammothdb's, currently "...39
in the default search window.") — also a literal downstream of the row-count change; update to the
freshly-measured count alongside the self-check assertion itself (REQ-6.2/7.6).

`docker/bootstrap/02-external-sim.sql` narrative comments (REQ-8) — **the whole file, not only the
file-header block.** The "two disjoint rosters / foreign probe" narrative is repeated inline at
multiple points beyond the header: the axis-row comments marking rows 10 (hippodb) and 9
(mammothdb) as proving cross-side scoping ("different SaleCode: proves `@SaleCode` is a hard scope
axis"), and the `CASE`-arm comments on both sides explaining that `'90001'`/`'77001'` "shows up
here only through the foreign-SaleCode axis probe row." Every one of these becomes false the
moment REQ-1/REQ-2 land (there is no more foreign probe, and hippodb's `'90001'` arm is removed
per F7 above) — rewrite each to describe the row's new ordinary role, not just the header
paragraph. Leave the `DocumentNo`-format narrative from `external-sim-documentno-format` untouched
(REQ-8.3). `SpDocumentContractTests.cs`'s own header comment ("each side spreads its 200 rows
across a 6-agent SaleCode roster") stays accurate in spirit but its `42`/`39` figures need the same
re-pin as REQ-7.6 above.

## Technology Decisions

| Decision | Rationale |
|---|---|
| Reuse hippodb's existing master table verbatim rather than inventing new agent data | REQ-1.1 — zero new identity data to design/verify; every downstream literal (`SaleFullName` Thai strings, broker names) already exists and is already proven correct by hippodb's own prior-round self-checks |
| Duplicate the master CASE expressions byte-identical into mammothdb's blocks (not a shared T-SQL function/view) | Each database block already holds its own full, self-contained identity-CASE set — this duplicates hippodb's set into mammothdb's slot; T-SQL has no cross-database shared-expression mechanism this feature would otherwise need |
| Collapse all 10 mammothdb axis rows onto `SaleCode = '77001'` | Required, not stylistic: rows 1, 4-8, 10 are the exact landmark rows mammothdb's own default-search-scoped tests already key off (`PolicySeq`/`RenewalKeptSeq`/`RenewalDroppedSeqs`/`ApplicationSeq`/`PaidSeqs`/`LikeMetacharacterSeq`) — once REQ-6.1 moves the default probe to `77001`, any of these left on a different code becomes invisible to its own test |
| hippodb axis row 10 moves to `'77002'` (not `'77001'` or another roster code) | `77002` is the one master code that already resolves to `ReferenceBranch = '901'` — the same branch `90001` used to resolve to — so this row's `ReferenceBranch`/`DocumentNo`/`PolicyNumber` are unchanged by the re-key; it also happens to keep the row outside hippodb's `77001`-scoped default-search window (REQ-2.5), but branch-continuity, not window-avoidance, is why `77002` specifically beats `77003`-`77006` |
| New cross-db self-check lives in mammothdb's block, next to the existing DocumentNo cross-check | Same ordering guarantee already relied upon (mammothdb seeds after hippodb in the same script, so hippodb's data is final by the time mammothdb's self-checks run) |
| Prefix probe (`7700`) AND substring probe (`7001`), not just one | REQ-5.5 / audit finding F-4 — the replaced test's original claim covered both prefix and partial matching; a single probe would prove strictly less |
| `EXCEPT`-based row comparison for the cross-db identity check, not a `<>` chain | `SET ANSI_NULLS ON` (file-wide) makes `<>` blind to NULL-vs-value drift; `EXCEPT` is SQL Server's standard NULL-safe row-comparison idiom and needs no new dependency |
| Reuse `THROW 51002` for every new self-check rather than inventing new numbers | Matches the file's actual, verified convention (every self-check reuses `51002`; the SP's own `50001`-`50009` range is a distinct, already-tested contract this file must not collide with) |

## Error Handling Strategy (self-checks)

All three new self-checks (cross-database identity, roster-completeness, `ShowName`→`SaleCode`)
follow the file's existing `THROW`-with-actual-value pattern (`sqlcmd -b` fails loudly on any
mismatch) — same posture as every other self-check in the file: fail the bootstrap outright rather
than seed inconsistent data silently. All three use `THROW 51002` (the file's actual convention —
every self-check reuses this one code; see "Reuse `THROW 51002`" in Technology Decisions), and the
identity check's message names the actual offending `SaleCode` via `CONCAT`, matching how
`@rowMsg`/`@visibleMsg` already report actual values rather than a generic string. All other
existing self-checks (row counts, `DocumentNo` prefix/uniqueness, Thai round-trip, default-search
visible counts) are unchanged in mechanism; only mammothdb's default-search literal target changes
(`SaleCode` parameter `90001` → `77001`, REQ-6.1), its `PRINT` message, and its expected count
becomes a freshly-measured value (REQ-6.2) filled in at implementation time from the live database,
not invented here.

## Testing Strategy

| Test change | REQ(s) | Notes |
|---|---|---|
| Remove `Side.ForeignSaleCode`/`ForeignSaleCodeSeq`; add prefix/substring probe constants | REQ-5.3 | Both sides can share one probe pair since the roster is now shared |
| `Sale_code_does_not_match_by_prefix_or_substring` (replaces `Sale_code_is_an_exact_scope_axis`) | REQ-5.1, 5.2, 5.5 | Two negative assertions per side (prefix → 0 rows, substring → 0 rows); no landmark row needed (REQ-5.4). Optionally strengthened with a positive assertion (`@SaleCode = '77002'` on hippodb → exactly row 10's seq `800010`, no seed changes needed since `77002` already carries a real row post-re-key) to restore the positive-match half of the original test's coverage that a pure negative-probe redesign would otherwise drop — include if convenient, not REQ-blocking. |
| `NonMotorSide.SaleCode` `"90001"` → `"77001"` in both test files | REQ-6.1 | |
| Re-pin `NonMotorSide.TotalRows`/`TotalPages`/`LastPageRows`, `PolicyYearBranch`, `SeqPrefix`-family literals, `AxisReferenceBranch`, `AxisPolicyNumber`, `AxisDocumentNo`, `PaidPolicyNumber`, the hardcoded `Assert.Equal(2L, result.Page.TotalPages)`, and the bootstrap `PRINT` literal | REQ-6.2, REQ-7.6 | Read from live reseeded DB at implementation time — not derived in this document; `PolicyYearBranch` and the hardcoded `TotalPages` assertion are easy to miss because they sit outside the obviously-`SaleCode`-derived literal names |
| New cross-db identity self-check (`EXCEPT`-based, SQL-only) | REQ-4.1, REQ-4.2 | Verified by re-running `docker compose up pol-db-init` after the roster change; message names the actual offending `SaleCode` |
| New roster-completeness self-check (`COUNT(DISTINCT SaleCode) = 6`, per side) | REQ-4.3 | Makes "all 6 codes" an enforced assertion, not an incidental fact of today's data |
| New `ShowName`→`SaleCode` self-check (per side) | REQ-1.5 (the one invariant not already guaranteed by construction) | Directly protects the axis-row re-key boundary (REQ-2) — the other REQ-1.5 invariants are structurally guaranteed by the `SaleCode`-keyed `CASE`/`CROSS APPLY` and need no separate check |
| Existing pairing-invariant tests / self-checks (unchanged code, re-verified against new data) | REQ-7.1, REQ-7.4, REQ-7.5 | Regression proof — these already exist from prior rounds |
| Narrative comment rewrite — file header AND inline axis-row/CASE-arm comments (SQL-only, no test) | REQ-8.1-8.3 | Reviewed by reading the file, not test-asserted; scope is the whole file per the audit finding, not just the header block |

Every REQ maps to at least one of: a self-check assertion, a re-pinned/renamed Integration.Tests
method, or a design-time table/comment change above — no REQ is left unverified.

## Requirement Traceability

| Design element | REQ(s) satisfied |
|---|---|
| Master roster reuse (hippodb's existing 6-entry table, applied to mammothdb; dead `'90001'` arm removed from both sides) | REQ-1.1, REQ-1.2, REQ-1.3 |
| `BranchCode` search parameter untouched | REQ-1.4 |
| 4 of 5 REQ-1.5 invariants guaranteed by construction (single `SaleCode`-keyed `CASE`/`CROSS APPLY`); new `ShowName`→`SaleCode` self-check covers the 5th | REQ-1.5 |
| Axis-row re-keying map (rationale: branch-continuity for hippodb row 10, landmark-preservation requirement for mammothdb rows) | REQ-2.1, REQ-2.2, REQ-2.3, REQ-2.4, REQ-2.5 |
| Generated-row `SaleCode` CASE retargeting | REQ-3.1, REQ-3.2, REQ-3.3 |
| New `EXCEPT`-based cross-database identity self-check (NULL-safe, names the offending `SaleCode`) | REQ-4.1, REQ-4.2 |
| New roster-completeness self-check (`COUNT(DISTINCT SaleCode) = 6` per side) | REQ-4.3 |
| Prefix + substring probe test redesign | REQ-5.1-5.5 |
| `NonMotorSide.SaleCode` → `77001`, freshly-measured count | REQ-6.1, REQ-6.2 |
| Unchanged row counts / formulas / prefixes; re-pin list includes `PolicyYearBranch`, hardcoded `TotalPages`, and the bootstrap `PRINT` literal | REQ-7.1-7.6 |
| Narrative rewrite — file header AND inline axis-row/CASE-arm comments throughout the file | REQ-8.1, REQ-8.2, REQ-8.3 |
