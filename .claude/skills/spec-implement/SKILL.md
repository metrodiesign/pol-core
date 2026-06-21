---
name: spec-implement
description: Implement one or more cohesive tasks from the active spec's tasks.md, end-to-end with tests, following project conventions.
argument-hint: <task id, range like 1-3, or "all">
---

# Implement task(s): $ARGUMENTS

Resolve $ARGUMENTS to the target task(s): a single id (e.g. 2), a range (1-3), or
all incomplete tasks. For multiple tasks, work in dependency order.

Resolve the active spec first: if `.ai/specs/` holds more than one feature and
the conversation does not name one, pick the single folder whose tasks.md still has
the requested task id unchecked (`- [ ]`); for a range or "all", pick the folder
with any unchecked tasks. If more than one folder qualifies: interactive → list
them and ask, never guess; unattended (pane-loop/CI) → require the feature name in
the argument and stop with that message instead of waiting.

If tasks.md is still `> Status: draft`: interactive → warn in Thai and ask for
confirmation (if I confirm, flip it to `> Status: approved <YYYY-MM-DD>` as part
of that confirmation); unattended → treat it as a hard stop, state the reason and
stop immediately — never sit waiting for an answer no one will give.

Once, before the loop: run `scripts/spec-state.sh <feature>` and reconcile tasks.md
with the filesystem for the target tasks and their dependencies. The filesystem is
ground truth — checkboxes and git log can lie, and untracked files never appear in
`git diff --stat`. If a checkbox contradicts reality (marked [x] but artifacts
missing, or [ ] but already built), fix the checkbox and note the reconciliation
in tasks.md before implementing.

For EACH task:

1. Read the task plus its linked IDs in requirements.md (or bugfix.md with its
   F-IDs/B-IDs, for a bugfix spec), the relevant parts of design.md (if present
   — bugfix specs have none), and @.ai/shared/ARCHITECTURE.md.
2. Plan the task with your own internal TODO list, then implement the WHOLE task in
   one cohesive pass. It may span many files — that is expected; keep the entire
   task in context rather than splitting it across turns.
3. Write or extend tests proving it satisfies its REQ IDs (or F-IDs/B-IDs for a
   bugfix spec).
4. Mark the task "- [x]" in tasks.md, state which IDs are now satisfied, AND in
   the SAME edit append an `Evidence:` block directly under that task line — the
   box and the evidence flip together. Record what you actually ran and observed
   (not the planned `Verify:` line):
       Evidence:
         - test: `<exact command>` -> <result, e.g. 47 passed / 0 failed>
         - viewports: 375 OK | 768 OK | 1440 OK   (browser tasks; else `n/a — logic-only`)
         - deviations: <none | what differed from design/requirements and why>
   For a browser task you must have Read references/browser-verify.md and verified
   `clientWidth === target` at each viewport — record the values, never assert a
   pass you did not observe; if a check could not be run, say so in `deviations:`.
   Before marking the LAST task (or any assembly task), run
   `scripts/spec-trace.sh <feature>` — any uncovered REQ it reports is a blocker,
   never skip it silently.
5. Give me the exact command to verify (test / build / run). Before any
   browser-based verification, Read
   `.claude/skills/spec-implement/references/browser-verify.md` first.

Pause for my confirmation at each TASK boundary (not after every file). When I
asked for a range or "all", continue to the next task after reporting, stopping
early only if a test fails or a requirement turns out to be infeasible.

For unattended / CI runs: the DEFAULT for a COUPLED feature (tasks share
primitives/data/lib) is ALL tasks in ONE session, in dependency order —
`/spec-implement all` or `scripts/pane-loop.sh <feature> all-in-one`. Separate
sessions do not share cache, so each re-pays cold context acquisition (measured
~30-40% more expensive). Split into per-task sessions ONLY for genuinely
independent tasks (no shared state) or to isolate a CORE domain's accuracy from
long-context drift — a conscious accuracy trade, not a cost win. If one long
session risks drift, persist state (active task id, decisions, next step) into
tasks.md at each task boundary before continuing.
