---
name: spec-quick
description: Run the full spec workflow (requirements → design → tasks → implementation) end-to-end WITHOUT approval gates, for small, well-understood features. Use when specs should stay the source of truth but you do not need to review each phase.
argument-hint: <short description of the feature>
---

# Quick Spec (no gates)

The feature idea is: $ARGUMENTS

This is the gate-free path the constitution allows for SMALL, well-understood features.
Run every phase in ONE pass, in ONE session, without stopping for approval between them.
Still write every artifact — the spec files remain the durable source of truth — just
don't pause for review.

Right-size the ceremony to the risk. If, while working, you hit REAL ambiguity,
conflicting requirements, non-trivial architecture choices, or logic/compliance risk →
STOP and switch to the gated flow (`/spec-requirements` → `/spec-analyze` →
`/spec-design` → `/spec-tasks`). It is cheaper to resolve at requirements than in code.

## Steps (no approval STOP between steps 1-6 — the step 0 Q&A is the only wait)

0. If $ARGUMENTS already answers who/what/why/success criteria/edge cases/
   constraints, skip to step 1. Otherwise ask ALL missing questions in ONE
   batched message (in Thai), wait for the answers, then run steps 1-6 in one
   uninterrupted pass — this single Q&A round replaces every approval gate.
1. Create the spec folder `.ai/specs/<kebab-case-name>/`. Every artifact
   written below gets the header `> Status: approved <YYYY-MM-DD> (quick, no
   gates)` immediately — the constitution exempts this flow from gates.
2. `requirements.md` — EARS notation, atomic/testable, stable IDs (REQ-N). Keep it tight.
   Then self-check it inline against the FIVE /spec-analyze categories (logical
   inconsistencies, ambiguities, conflicting constraints, gaps, unstated
   assumptions) — fix what you find before writing design.md; no separate
   session, no stop.
3. `design.md` — minimal architecture only: data shapes, key functions, file list. No padding.
   Still include a minimal `## Requirement Traceability` table (REQ → file/function) —
   `scripts/spec-trace.sh` requires it and runs as a blocker in later steps.
4. `tasks.md` — the FEWEST cohesive, independently verifiable tasks (see `spec-tasks` sizing).
   Print the task list compactly — one line per task: title + REQ IDs (not the
   full file) — as a free interrupt point; do NOT stop or wait for approval.
5. Implement all tasks end-to-end with tests (see `spec-implement` conventions): pure logic
   in `lib/` + unit tests first, then wire UI. Mark each `- [x]` and note REQ IDs satisfied.
6. Verify: run the project's test / typecheck / build. Report the verify output, not just "done".

## When NOT to use

- Logic-heavy / compliance-sensitive / many architectural unknowns → use the gated flow
  plus `/spec-analyze`.
- Trivial one-sentence change → no spec needed at all; just make the edit.

At the end, suggest `/spec-retro` before `/clear` (the retro itself is skipped when there
were no code changes and no new lessons).
