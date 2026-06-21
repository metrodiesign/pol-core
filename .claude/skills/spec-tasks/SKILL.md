---
name: spec-tasks
description: Generate the tasks.md implementation checklist from the approved design. Use after design is approved.
argument-hint: <feature folder name (optional)>
---

# Generate tasks.md

Resolve the target spec: use $ARGUMENTS if given; if `.ai/specs/` holds more
than one feature and none was named, list them and ask — never guess.

Read the active spec's design.md and requirements.md. If either upstream
artifact is still `> Status: draft` (design.md always; requirements.md too when
it exists — e.g. derived in design-first), warn in Thai and ask for
confirmation first — and if I confirm, flip the draft one(s) to
`> Status: approved <YYYY-MM-DD>`. Then write
`.ai/specs/<feature>/tasks.md`. Size tasks for a large-context, high-effort
model: each task is a COHESIVE, INDEPENDENTLY VERIFIABLE slice that you can
implement end-to-end in one pass, even if it spans many files.

# Implementation Tasks: <Feature Name>
> Status: draft

> Each task is a cohesive, independently verifiable slice. Implement a whole task
> in one pass (it may touch many files). Decompose into sub-steps yourself at
> execution time — do NOT pre-split tasks here.

- [ ] 1. <Cohesive capability> — <one line: scope + what "done" means>
     Satisfies: REQ-1 (all criteria). Verify: <test / command>.
- [ ] 2. <Cohesive capability> — <scope + done>
     Satisfies: REQ-2. Depends on: 1. Verify: <test / command>.
- [ ] 3. <Cohesive capability> [optional] — <scope + done>
     Satisfies: REQ-3. Batch: B1.
- [ ] 4. <Cohesive capability> — <scope + done>
     Satisfies: REQ-4. Batch: B1.

## Suggested execution batches

> DEFAULT for a COUPLED feature (tasks share primitives/data/lib): run ALL tasks in
> ONE session — `scripts/pane-loop.sh <feature> all-in-one` (or `/spec-implement all`).
> Separate sessions do NOT share cache, so each one re-pays the cold cache-write to
> re-acquire shared context — measured ~30-40% more expensive for coupled work.
> Split into separate sessions/panes ONLY for accuracy: a genuinely INDEPENDENT task
> (no shared state), or to isolate a CORE domain (e.g. pricing logic) from long-context
> drift — a conscious accuracy trade, not a cost win.
> `Batch:` tags still group small same-type tasks for finer control; feed with `+`
> (`scripts/pane-loop.sh <feature> 3+4`).

Rules:

- Aim for the FEWEST tasks that keep each one independently verifiable. A typical
  feature is ~5-10 tasks, not 20-30. If a "task" can't be verified on its own,
  fold it into the task it serves.
- Each task is ONE coherent behavior / vertical slice (e.g. "user registration
  end-to-end: model → endpoint → validation → tests"), never a horizontal layer
  ("create the model", "create the repository") that does nothing alone.
- Map each task to a whole REQ or a tightly-related group; list the REQ IDs.
- Before STOP, run a reverse coverage check: every REQ-N in requirements.md must
  appear on the Satisfies: line of at least one task — run `scripts/spec-trace.sh
  <feature>` to verify deterministically. List any uncovered REQ loudly as a
  blocker; never skip silently. A REQ may stay uncovered only if explicitly
  declared out of scope and approved.
- Do NOT write 1.1/1.2 sub-tasks — the implementing model handles micro-sequencing
  internally with its own TODO list.
- Order coarsely: shared/foundational tasks first. Note a dependency only when real.
- Mark [optional] for non-essential tasks.
- Tag `Batch: <id>` ONLY on tasks that are ALL of: small, the same type (e.g. several
  data-only files, several static sections, a cluster of UI-polish fixes), touch the
  same area, and BENEFIT from shared context. Same tag = same execution session.
  Do NOT batch big/foundational/distinct-domain tasks — those want a fresh, focused
  session (more accurate). Batching is an EXECUTION hint only: it never merges tasks
  or changes their independent verifiability / REQ mapping. When unsure, leave untagged.

Sync mode: if tasks.md already exists and requirements.md or design.md changed
after it was written, do NOT regenerate — patch only the affected tasks,
preserving completed `- [x]` entries and their notes (including any appended
`Evidence:` block — never strip it). If tasks.md was already
approved, re-stamp its header: `> Status: approved <original date>, amended
<YYYY-MM-DD>`.

When done: STOP for my review. Then ask whether to implement a specific task
(`/spec-implement <n>`), a range (`/spec-implement 1-3`), or everything
(`/spec-implement all`); and note any `Batch:` groups so the orchestrator can run
them in one session (`scripts/pane-loop.sh <feature> 3+4`). When I explicitly
approve, flip the header to `> Status: approved <YYYY-MM-DD>` before
implementation starts.
