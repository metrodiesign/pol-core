---
name: spec-design
description: Generate the design.md artifact. Requirements-first (after requirements are approved) or design-first (architecture before requirements).
argument-hint: <feature folder name (optional)>
---

# Generate design.md

Resolve the target spec: use $ARGUMENTS if given; if `.ai/specs/` holds more
than one feature and none was named, list them and ask — never guess.

Mode — requirements-first (default, requirements.md exists): read it plus the
project rules (@.ai/shared/CODING_STANDARDS.md, @.ai/shared/ARCHITECTURE.md). If it is
still `> Status: draft`, warn in Thai and ask for confirmation before
proceeding — and if I confirm, flip requirements.md to
`> Status: approved <YYYY-MM-DD>` as part of that confirmation.

Mode — design-first (requirements.md does NOT exist and /spec-new chose
Design-First): inputs are the /spec-new answers plus the project rules. Ask me
ONE question first: high-level design only (components + flows), or down to
module/interface level? In this mode there are no REQ IDs yet, so SKIP the
Requirement Traceability section and, in Testing Strategy, map tests to design
behaviors/sections instead of REQ IDs (/spec-requirements backfills both after
deriving). ADD a `## Non-Functional Considerations` section covering the
constraints that motivated Design-First (latency, compliance, a11y, ...). If
requirements.md is missing and Design-First was never chosen, stop and ask —
never guess the mode.

Then write `.ai/specs/<feature>/design.md`:

  # Design: <Feature Name>
  > Status: draft

  ## Architecture Overview        — components and responsibilities
  ## Sequence Diagrams            — Mermaid for key flows
  ## Data Models & Interfaces     — schemas, types, API contracts
  ## Technology Decisions         — choices + rationale (prefer tech.md)
  ## Error Handling Strategy      — how each error case is handled
  ## Testing Strategy             — unit/integration/property; map to REQ IDs
  ## Requirement Traceability     — table: design element → REQ-x.y it satisfies

Sync mode: if design.md already exists and requirements.md changed after it was
written, do NOT regenerate the whole file — patch only the sections affected by
the changed REQs, preserving approved decisions, and update the traceability
table to match. If design.md was already approved, re-stamp its header:
`> Status: approved <original date>, amended <YYYY-MM-DD>`.

Produce design.md inline — this skill owns the section outline above (the single
source). When the design touches CORE domain logic (pure logic in the
project test directory, co-located with the logic under test), delegate a fresh-context adversarial critique to the
`spec-architect` subagent (its default mode = critique): it hunts unstated
assumptions, missing error paths, REQ coverage gaps, and infeasible choices.
Apply or explicitly rebut each finding before STOP. (For design-first CORE
production where no requirements.md exists yet, invoke spec-architect with
`mode=produce` instead, passing the /spec-new answers and stating that no
requirements.md exists.) When done: STOP for my review, then suggest
`/spec-tasks` (requirements-first) or `/spec-requirements` (design-first). When I
explicitly approve, flip the header to `> Status: approved <YYYY-MM-DD>` before
the next phase.
