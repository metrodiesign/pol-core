# Design: External Sim DocumentNo Format Alignment

> Status: approved 2026-08-02

## Architecture Overview

Single-file change, `docker/bootstrap/02-external-sim.sql`, plus the two Integration.Tests
files that pin its output. No new components. The redesign flips one data-flow direction
inside the existing seed pipeline:

```
TODAY (backwards)                          NEW (forwards, REQ-5)
DocumentNo (hand/formula string)           SaleCode + DocumentType + SourceSystem
   │ CROSS APPLY parses it                    │ (already independent columns)
   ▼                                           ▼
v.Seq  ──derives──▶  PolicySequenceNo,     PolicySequenceNo/ReferenceNo (REQ-3.4: axis =
ReferenceNo, PolicyNumber, ...             hand literal; generated = 100+g.value, zero-
                                            padded per REQ-3.1/3.2 width)
                                               │
                                               ▼
                                            PolicyYear + ReferenceBranch (already
                                            independent, set in the same UPDATE, REQ-4/
                                            prior round) + DocumentType→abbreviation
                                            (REQ-2) compose ──▶  DocumentNo (REQ-1),
                                            PolicyNumber/ApplicationNumber/
                                            PreviousPolicyNumber/EndorsementNumber (REQ-9)
```

The pivot: **`PolicySequenceNo`/`ReferenceNo` become an INSERT-time value** (a literal for
axis rows, `100 + g.value` for generated rows) instead of a value parsed out of `DocumentNo`
after the fact. `DocumentNo` moves to the END of the derivation chain, alongside
`PolicyNumber`/`ApplicationNumber`/`PreviousPolicyNumber`/`EndorsementNumber` — all five are
now pure functions of columns already sitting on the row (`SaleCode`, `DocumentType`,
`SourceSystem`, `PolicyYear`, `ReferenceBranch`, `PolicySequenceNo`). No column is derived
from `DocumentNo` anywhere anymore (REQ-5.3).

Both `hippodb` and `mammothdb` get the identical restructuring, independently (they are
separate databases seeded by separate `INSERT`/`UPDATE` blocks in the same file today, and
stay that way).

## A discovered conflict this design resolves (read before implementing)

REQ-3.1/3.2 zero-pads the running number to 7 digits for `CMI` (พรบ.) and 6 digits for
everything else. hippodb's 14 axis rows mix `CMI` and `VMI` rows (e.g. row 2 is `CMI`, row 3
is `VMI`), so **axis rows on hippodb do not share a uniform running-number width** — unlike
today, where every row (axis and generated) used a flat 6-digit convention regardless of
`SourceSystem`.

This breaks `The_search_window_is_evaluated_per_row_when_the_document_type_is_ALL`
(`SpDocumentContractTests.cs:505`), which searches `@SearchText = side.SeqPrefix` (today
`"95000"`) and relies on that substring appearing in the DocumentNo of axis rows 1–9 but NOT
10–14 — a coincidence of the old uniform-width, sequential (`950001`..`950014`) numbering. A
naive port to "row index 1–14, zero-padded per REQ-3.1/3.2" breaks the coincidence: a
two-digit index on a `CMI` (7-digit) row still leaves 5 leading zeros (`0000011` contains
`00000`), while the same index on a `VMI` (6-digit) row does not (`000011` does not) — the
grouping that used to fall out of the index alone now depends on `SourceSystem` too, and rows
11 and 13 (both `CMI`) would leak into a `"00000"`-style prefix search that is supposed to
exclude them.

mammothdb never has this problem — its `SourceSystem` is always `FIRE`/`MISC`, never `CMI`,
so all 10 axis rows share the 6-digit width and a plain sequential 1–10 index reproduces the
old trick exactly.

**Resolution (hippodb only):** since REQ-3.4 already hands axis running-number VALUES to
design (not just their width), assign hippodb's axis rows a 2-digit MARKER prefix instead of
a bare sequential index, chosen by test membership rather than row order:

- Rows the smart-search-window test needs to MATCH `@SearchText` (1, 2, 3, 4, 5, 6, 7, 8, 9 —
  i.e. every row `The_search_window_is_evaluated_per_row_when_the_document_type_is_ALL` and
  the PAID-row test currently reach via `"95000"`): running number = `"91"` + the row's own
  2-digit position, zero-padded to the row's own width (6 for `VMI`, 7 for `CMI`). E.g. row 1
  (`VMI`) → `910001`; row 2 (`CMI`) → `9100002`; row 8 (`CMI`) → `9100008`.
- Rows that must NOT match (10, 11, 12, 13, 14 — the foreign-SaleCode, LIKE-metacharacter,
  boundary-date, and licence-plate axis probes): running number = `"80"` + the same 2-digit
  position, same per-row width. E.g. row 10 (`VMI`) → `800010`; row 11 (`CMI`) → `8000011`.
- New `SeqPrefix` for `MotorSide` = `"91"` (was `"95000"`). `SeqPrefixHits` becomes the
  updated literals for rows 1/2/4/9 under this scheme (exact strings worked out during
  `/spec-tasks` implementation by running the seed and reading the self-check/DB, same
  verification discipline as every prior round on this branch — not hand-derived here).
- Verified no other searchable field accidentally contains `"91"` for rows outside the
  hit-group or `"80"` for rows outside the non-hit-group (checked `LicensePlateNumber`,
  `PolicyNumber`'s `SaleCode` prefix, `EndorsementNumber`'s `E`-prefix — none collide, because
  none of those fields embed a bare `"91"`/`"80"` digit pair from any other source).

mammothdb's axis rows keep the simple scheme: running number = row's own 1-based index,
6-digit zero-padded (`000001`..`000010`), because there is no width split to defeat.

Generated rows on both sides need no marker games — no test smart-searches into a specific
generated-row substring, so `100 + g.value` zero-padded per REQ-3.1/3.2 (REQ-3.4 as already
written) is sufficient there.

## Sequence Diagrams

```mermaid
sequenceDiagram
    participant Insert as INSERT (axis VALUES / generated SELECT)
    participant Update as UPDATE ... SET (single pass, both row kinds)
    participant Self as Self-check block

    Insert->>Insert: axis row: literal PolicySequenceNo (hand-picked marker, REQ-3.4)
    Insert->>Insert: generated row: PolicySequenceNo = zero-pad(100+g.value, width(SourceSystem))
    Note over Insert: DocumentNo is NOT set at insert time anymore (removed from the column list)

    Insert->>Update: row now has SaleCode, DocumentType, SourceSystem, PolicySequenceNo, ReferenceNo
    Update->>Update: PolicyYear, ReferenceBranch, SaleFullName, BrokerCode/Name, PolicyBranch, PolicyType, premiums (unchanged — keyed on SaleCode, REQ-8.3)
    Update->>Update: Abbrev = f(SourceSystem, DocumentType)  (REQ-2)
    Update->>Update: DocumentNo = compose(PolicyYear, ReferenceBranch, Abbrev, PolicySequenceNo, DocumentType, SourceSystem)  (REQ-1)
    Update->>Update: PolicyNumber/ApplicationNumber/PreviousPolicyNumber/EndorsementNumber = compose(SaleCode, PolicyYear, ReferenceBranch, PolicySequenceNo)  (REQ-9)

    Update->>Self: 200 rows/side, 200 distinct DocumentNo/side (REQ-6.2)
    Self->>Self: every DocumentNo starts with the side's own PolicyYear literal (REQ-6.4)
    Self->>Self: Thai round-trip — hippodb via ShowName+DocumentNo (unchanged); mammothdb via ShowName+PolicyBranch (REQ-6.5)
    Self->>Self: default-search visible count still 42/39 (REQ-8.2, unaffected — filter is SaleCode+PaymentStatus+window, not DocumentNo)
```

## Data Models & Interfaces

### DocumentNo composition (REQ-1, REQ-2, REQ-3)

```
Abbrev(SourceSystem, DocumentType):
  Motor (CMI|VMI):     POLICY|RENEWAL -> 'กธ'   APPLICATION -> 'รย'   ENDORSEMENT -> 'อท'
  NonMotor (FIRE|MISC): POLICY|RENEWAL -> 'POL'  APPLICATION -> 'APP'  ENDORSEMENT -> 'END'

RunningWidth(SourceSystem):
  CMI -> 7 digits (พรบ.)      VMI|FIRE|MISC -> 6 digits

DocumentNo(row):
  base = PolicyYear + ReferenceBranch + '/' + Abbrev + '/' + PolicySequenceNo
  IF Motor AND DocumentType = 'ENDORSEMENT':        base + '1'          -- REQ-1.3, position 17/18
  ELIF NonMotor AND DocumentType = 'ENDORSEMENT':    '1-' + base         -- REQ-1.2
  ELSE:                                              base                -- REQ-1.1
```

`PolicySequenceNo` is already `RunningWidth(SourceSystem)`-digits wide by construction (set
at INSERT time), so no padding happens inside this formula — it just concatenates.

### Document-number-adjacent fields (REQ-9)

```
PolicyNumber         = SaleCode + '-' + PolicyYear + ReferenceBranch + '/' + PolicySequenceNo
                        (only when DocumentType <> 'APPLICATION')
ApplicationNumber    = same shape                    (only when DocumentType = 'APPLICATION')
PreviousPolicyNumber = SaleCode + '-' + PrevYear + ReferenceBranch + '/' + (PolicySequenceNo - 1)
                        (only when DocumentType IN ('RENEWAL','ENDORSEMENT'))
                        PrevYear = zero-pad(CAST(PolicyYear AS int) - 1, 2)   -- '69'->'68', '26'->'25'
EndorsementNumber    = 'E' + PolicySequenceNo         (only when DocumentType = 'ENDORSEMENT')
```

`PolicySequenceNo - 1` requires it as an integer at that point — cast, subtract, re-pad to
the row's own `RunningWidth`. This preserves the existing conditionality (REQ-9.3) and shape
family (REQ-9.2) exactly; only the year/branch/sequence SOURCE changes (columns instead of a
parsed string).

### PolicyYear / ReferenceBranch (REQ-4, REQ-6)

- `PolicyYear`: `'69'` on hippodb (unchanged), `'26'` on mammothdb (REQ-4.2's illustrative
  value, adopted as the actual literal — any distinct 2-digit value would satisfy REQ-4, `26`
  is picked here so it doesn't need re-deciding at task time).
- `ReferenceBranch`: unchanged from the prior round (`900`–`904`, keyed off `SaleCode`).
- Cross-catalog uniqueness (REQ-6.1): guaranteed structurally because `'69'` and `'26'` can
  never prefix-match each other or any generated/axis running number the same way, and each
  side's `UX_Documents_DocumentNo` already enforces no duplicate within a side (REQ-6.2) —
  no additional mechanism needed.

### Axis-row INSERT shape change

The 24 axis-row `INSERT ... VALUES` statements currently list `DocumentNo` as a column and
compute it inline (`'77001-69900/' + N'กธ' + '/950001-10'`). That column is REPLACED by
`PolicySequenceNo` carrying the hand-picked literal (marker-prefixed on hippodb per the
conflict resolution above, plain sequential on mammothdb) — `ReferenceNo` mirrors it, same as
today's `PolicySequenceNo`/`ReferenceNo` duplication. `DocumentNo` is dropped from the
`INSERT` column list entirely; the shared `UPDATE` computes it for every row (axis and
generated) in one pass.

### Generated-row SELECT shape change

Today's generated-row `INSERT ... SELECT` computes `DocumentNo` inline as one of the selected
expressions (`'77001-69900/' + N'กธ' + '/' + CONVERT(varchar(6), 950100 + g.value) + '-10'`).
That expression is replaced with a `PolicySequenceNo` expression:

```sql
RIGHT(REPLICATE('0', 7) + CONVERT(varchar(7), 100 + g.value),
      CASE WHEN <SourceSystem expression> = 'CMI' THEN 7 ELSE 6 END)
```

`<SourceSystem expression>` is the same `CASE WHEN g.value % 2 = 0 THEN 'VMI' ELSE 'CMI' END`
(hippo) / `CASE WHEN g.value % 5 < 3 THEN 'FIRE' ELSE 'MISC' END` (mammoth) already used
elsewhere in the same `SELECT` — T-SQL can't reference a sibling `SELECT`-list alias, so this
duplicates the existing expression rather than introducing a new dependency; both copies stay
byte-identical to avoid drift (a one-line comment marks the duplication and why).

## Technology Decisions

| Decision | Rationale |
|---|---|
| Compute-forward via a single shared `UPDATE` (REQ-5) instead of two per-side backward-parsing blocks | Matches how production actually generates the Reference (compose from components, per the photographed spec's own heading "เงื่อนไขการ Generate Reference"); also the only way to make `DocumentNo` stop being a fabricated string with `SaleCode` baked in |
| Marker-prefixed axis running numbers on hippodb (`91…`/`80…`) instead of bare sequential 1–14 | Sequential numbering silently breaks the smart-search-window test once `CMI`/`VMI` running numbers have different zero-padding widths (see "A discovered conflict" above) — the marker makes the grouping explicit and independent of width |
| `PolicySequenceNo` set at INSERT time (literal for axis, `100+g.value` for generated), `DocumentNo` computed after | Gives every derived field (`DocumentNo` and the REQ-9 quartet) one unambiguous source column instead of two competing ones |
| `PolicyYear` stays a fixed literal per side, not `GETDATE()`-derived | REQ-4.3 — keeps every pinned `DocumentNo`/`PolicyNumber` test literal stable across days/years |
| `Seqs()`/`DocumentNumbers()` test helpers become `DocumentType`/`SourceSystem`-aware | See Testing Strategy — needed once Motor `ENDORSEMENT` rows carry a trailing un-delimited `'1'` (REQ-1.3) |

## Error Handling Strategy (self-checks)

Both self-check blocks (hippodb, mammothdb) keep their existing THROW-with-actual-value
pattern (`sqlcmd -b` fails loudly on a mismatch) — only the assertions change:

- **REQ-6.4 — prefix check replaces the old `SaleCode` prefix check:**
  - hippodb: `IF EXISTS (... WHERE DocumentNo NOT LIKE '69%')` (every hippodb `DocumentNo`
    starts with `PolicyYear = '69'`; Motor has no `'1-'`-prefixed endorsement variant, REQ-1.3
    keeps the flag at the tail).
  - mammothdb: `IF EXISTS (... WHERE DocumentNo NOT LIKE '26%' AND DocumentNo NOT LIKE
    '1-26%')` (covers both REQ-1.1's plain form and REQ-1.2's endorsement `'1-'` prefix).
- **REQ-6.2 — within-side uniqueness:** already enforced live by `UX_Documents_DocumentNo`
  (an INSERT/UPDATE that produced a duplicate would fail the seed run itself); no additional
  self-check assertion needed beyond the existing row-count check (200 rows + a unique index
  that never threw = 200 distinct values, transitively).
- **REQ-6.5 — mammothdb Thai round-trip retarget:** change
  `WHERE ShowName LIKE N'%จำกัด%' AND DocumentNo LIKE N'%อค%'` to
  `WHERE ShowName LIKE N'%จำกัด%' AND PolicyBranch LIKE N'%สาขา%'` (both columns still carry
  Thai text post-change; `PolicyBranch` is nvarchar and never touched by this feature).
  hippodb's equivalent check is UNCHANGED (`DocumentNo LIKE N'%กธ%'` still holds — Motor keeps
  Thai abbreviations, REQ-2.1–2.3).
- **REQ-8.1/8.2 — row counts and default-search visible counts:** unchanged assertions
  (`@rows = 200`, `@visible = 42`/`39`) — the predicate they check (`SaleCode` +
  `PaymentStatus` + window) never references `DocumentNo`, so this feature cannot move them;
  kept as a regression tripwire, not something this feature edits.
- File-header comments (lines ~17–37, ~97–99, ~305–308) that describe the OLD prefix-based
  disjointness ("every DocumentNo starts '77'/'88'", M9) get rewritten to describe the NEW
  `PolicyYear`-based disjointness, plus a reference line to this spec alongside the existing
  `products-sp-gateway` reference.

## Testing Strategy

| Test file change | REQ(s) | Notes |
|---|---|---|
| `Seqs()`/`DocumentNumbers()` in both test files: strip the trailing `'1'` only `WHEN SourceSystem IN ('CMI','VMI') AND DocumentType = 'ENDORSEMENT'` (read from the already-fetched row dict — both columns are already selected by the SP) | REQ-1.3, REQ-5 | Length-based heuristics are ambiguous (a 7-char tail could be a plain `CMI` running number OR a `VMI` running number + flag) — must branch on the real `DocumentType`/`SourceSystem`, not string shape |
| `SpDocumentGatewayIntegrationTests.cs`: `AxisDocumentNo`, `AxisPolicyNumber`, `PaidPolicyNumber` literals recomputed for both sides | REQ-1, REQ-2, REQ-9 | New format per the formulas above; exact strings pinned during `/spec-tasks` implementation against the live self-check, same as every prior round |
| `SpDocumentContractTests.cs`: `Side.SeqPrefix` (`"91"` hippo / unchanged pattern mammoth), `SeqPrefixHits`, `RenewalDroppedSeqs`, `RenewalKeptSeq`, `ApplicationSeq`, `PolicySeq`, `LikeMetacharacterSeq`, `PaidSeqs`, `ForeignSaleCodeSeq` recomputed under the new running-number scheme | REQ-3.4, this design's marker resolution | mammothdb's values shift only in digit content (still 1–10 sequential, 6-digit); hippodb's shift in scheme (marker-prefixed) |
| `Motor_last_page_is_ordered_by_the_thai_letter_in_the_document_number` — **replaced**, not just re-pinned | REQ-8.5 | Old premise (exactly 2 axis rows carry the "high-sorting" abbreviation, every generated row carries the "low" one) no longer holds: generated `ENDORSEMENT` rows (≈1/3 of generated rows, `g.value % 3 = 0`) now correctly carry `'อท'` too (REQ-2 fixes the old bug where generated rows always displayed `'กธ'` regardless of `DocumentType`), so dozens of rows share the tail-sorting abbreviation, not two. New test: walk every page (reusing the existing full-walk helper from `The_pages_cut_one_document_number_ordered_list`), partition `DocumentNo` values by whether they contain `'อท'` (Motor) — assert every `'อท'`-bearing `DocumentNo`'s position in the walked list is greater than every non-`'อท'`-bearing one's position. Proves the identical property (Thai-letter grouping beats numeric value) without depending on an exact tail-count. Renamed to `Motor_endorsement_rows_sort_after_every_other_row_by_thai_letter` to match what it now asserts. |
| Self-check literal updates mirrored 1:1 (design already specifies the new assertions above) | REQ-6.4, REQ-6.5 | No test change — these are SQL-only assertions inside `02-external-sim.sql` |

Every REQ maps to at least one of: a self-check assertion, an existing Integration.Tests
method (re-pinned), or the one replaced test above — no REQ is left unverified.

## Requirement Traceability

| Design element | REQ(s) satisfied |
|---|---|
| `Abbrev(SourceSystem, DocumentType)` formula | REQ-2.1–2.7 |
| `RunningWidth(SourceSystem)` + INSERT-time `PolicySequenceNo` | REQ-3.1–3.4 |
| `PolicyYear` literals (`'69'`/`'26'`) | REQ-4.1–4.3 |
| Single shared `UPDATE` pass, no `DocumentNo` parsing anywhere | REQ-5.1–5.3 |
| Self-check prefix check replacement + uniqueness reasoning | REQ-6.1, REQ-6.2, REQ-6.4 |
| Self-check Thai round-trip retarget (mammothdb) | REQ-6.5 |
| `DocumentNo(row)` composition formula (REQ-1.1/1.2/1.3) | REQ-1.1–1.5 |
| Axis-row INSERT shape change + generated-row SELECT shape change, applied to both sides | REQ-7.1–7.4 |
| Regression-preserving assertions untouched by this feature (SaleCode/Broker/Branch/ShowName pairing, row/visible counts) | REQ-8.1–8.4 |
| Replaced ordering test (grouping property, not tail-count) | REQ-8.5 |
| `PolicyNumber`/`ApplicationNumber`/`PreviousPolicyNumber`/`EndorsementNumber` formulas | REQ-9.1–9.3 |
