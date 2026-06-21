---
name: spec-bugfix
description: Run a root-cause-first bugfix spec. Use for bugs in critical paths, recurring regressions, or unclear root causes.
argument-hint: <bug description>
---

# Bugfix Spec

Bug: $ARGUMENTS

Phase 0 — Intake. If $ARGUMENTS does not already answer them, ask me these in
ONE batched message (in Thai):
  1. Repro steps — the exact page/viewport/command/input that shows the bug
  2. Current (defective) behavior — what actually happens, stated measurably
  3. Expected behavior — what should happen instead
  4. Constraints — files or behaviors that must NOT be touched. The
     do-not-modify list is HARD scope: no task may edit those files.

Phase 1 — Delegate root-cause analysis to the `bug-investigator` subagent,
passing the intake answers. Present its findings to me and STOP. Wait for me to
confirm the root cause.

Phase 2 (after I confirm) — Create `.ai/specs/bugfix-<short>/bugfix.md`:

  # Bugfix: <short title>
  > Status: draft

  ## Current Behavior (Defect)
  WHEN <repro condition> THEN <defective behavior> — with repro steps that
  actually run (page / viewport / command / measured value), not prose.

  ## Expected Behavior
  - F1  THE SYSTEM SHALL <correct behavior>   (EARS; stable F-IDs, one criterion
        per observable fix — tasks cite these on their Satisfies: line)
  - F2  ...

  ## Unchanged Behavior
  - B1  WHEN <condition> THE SYSTEM SHALL CONTINUE TO <existing behavior>
  - B2  ... (stable B-IDs; cover every regression risk Phase 1 identified,
        including everything on the do-not-modify list)

STOP for my review. On explicit approval, flip the header to
`> Status: approved <YYYY-MM-DD>` before Phase 3.

Phase 3 — Produce tasks.md (same checkbox format as feature specs). Stamp its
header `> Status: approved <YYYY-MM-DD>` at creation — the Phase 2 gate already
covered this spec, so /spec-implement can run it unattended. Every F-ID and
B-ID must be cited on some task's `Satisfies:` line. A task that edits a
do-not-modify file is a spec conflict — stop and ask, never widen scope
silently. Validation is three-dimensional; ALL of:
  (a) a repro test that is RED before the fix and GREEN after it (covers the
      F-IDs — the defect → expected transition);
  (b) every B-ID has a 1:1 assertion;
  (c) every assertion checks the OBSERVABLE failure mode (rendered output,
      computed value, layout measurement) — not internal implementation detail.
      Anti-pattern: a regression test that asserts the implementation detail
      (e.g. which internal helper or style token was used) instead of the
      observable result misses the actual failure mode. Where the observable
      mode can only be seen in a UI, follow
      `.claude/skills/spec-implement/references/browser-verify.md`; tests that
      run under the project test runner (declared via `SDD_TEST_CMD`, or a
      `package.json` test script for a Node project), co-located with the logic
      under test, go there.
No placeholder values (`?`) may be committed in any artifact.
