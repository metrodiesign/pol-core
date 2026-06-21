---
name: spec-requirements
description: Generate the requirements.md artifact for the active feature spec using EARS notation. Use after /spec-new and after I've answered clarifying questions.
argument-hint: <feature folder name (optional)>
---

# Generate requirements.md

Resolve the target spec folder: use $ARGUMENTS if given; otherwise use the
feature folder created by /spec-new in this conversation. If neither identifies
one and `.ai/specs/` holds several features, list them and ask — never guess.

Derive mode (Design-First): trigger when the folder has a design.md but no
requirements.md. If that design.md is still `> Status: draft`, warn in Thai and
ask for confirmation first — and if I confirm, flip it to
`> Status: approved <YYYY-MM-DD>` before deriving. Then derive the requirements
FROM the design — each REQ cites the design section it comes from. While
deriving, sync is one-way: design is upstream; if a derived requirement
conflicts with the design, fix the requirement — or stop and ask if the design
itself looks wrong. (Once both artifacts exist, normal two-way sync resumes per
the constitution.) The derivation already maps each REQ to its design section,
so backfill design.md AS PART OF WRITING the draft requirements — do NOT defer
to approval: add the `## Requirement Traceability` table (design element →
REQ-x.y), update `## Testing Strategy` to cite the new REQ IDs, and re-stamp
design.md's header `> Status: approved <original date>, amended <YYYY-MM-DD>`.
Backfilling at draft time (not approval) guarantees that a downstream
`/spec-tasks` — which may be the skill that flips requirements.md to approved —
finds the table so `scripts/spec-trace.sh` passes; otherwise it hard-fails with
no skill authorized to create it. If the derived requirements change during
review, update the table to match before approval.

Write `.ai/specs/<feature>/requirements.md` with this structure:

  # Requirements: <Feature Name>
  > Status: draft

  ## Overview
  <one paragraph tying this to product.md>

  ## REQ-1: <Capability, e.g. User Registration>
  **User Story:** As a <role>, I want <goal>, so that <benefit>.
  **Acceptance Criteria (EARS):**
  - 1.1  THE SYSTEM SHALL <behavior>                               (ubiquitous)
  - 1.2  WHEN <event> THE SYSTEM SHALL <behavior>                  (event-driven)
  - 1.3  WHILE <state> THE SYSTEM SHALL <behavior>                 (state-driven)
  - 1.4  WHERE <feature is included> THE SYSTEM SHALL <behavior>   (optional)
  - 1.5  IF <error condition> THEN THE SYSTEM SHALL <response>     (error handling)

  (repeat REQ-2, REQ-3, ...)

  ## Edge Cases & Open Questions
  <anything ambiguous>

Rules: every requirement is atomic, testable, and has a stable ID. One observable
behavior per criterion — split compound criteria joined by "and"; reject
subjective wording ("fast", "user-friendly", "looks good") unless quantified
with a measurable threshold. Cover the happy path AND error/edge cases (use
IF...THEN).

When done: STOP. Show me a summary and ask me to review. In derive mode the
design already exists, so suggest `/spec-tasks` next (or `/spec-analyze` first
for complex/sensitive features). Otherwise suggest `/spec-analyze` for complex
or sensitive features, else `/spec-design`.

When I explicitly approve (in a later turn), flip the header line in the artifact
to `> Status: approved <YYYY-MM-DD>` before starting the next phase — approval
must live in the file, not only in this conversation.
