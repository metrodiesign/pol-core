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
the just-derived formula context; task 3 depends on both finishing first regardless.
