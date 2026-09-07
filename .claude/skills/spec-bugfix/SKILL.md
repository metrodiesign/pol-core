---
name: spec-bugfix
description: Run a root-cause-first bugfix spec. Use for bugs in critical paths, recurring regressions, or unclear root causes.
argument-hint: <bug description>
---

# Bugfix Spec

Bug: $ARGUMENTS

ก่อนสร้างหรือแก้ artifact ให้ใช้
[นโยบายภาษาของผลลัพธ์](../../../.ai/shared/TASK_PROTOCOL.md#ภาษาของผลลัพธ์)

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

  # Bugfix: <ชื่อแบบสั้น>
  > Status: draft

  ## Current Behavior (Defect)
  เมื่อ<เงื่อนไขที่ทำให้เกิดปัญหา> <พฤติกรรมที่ผิด> — พร้อม repro step ที่รันได้จริง
  (page / viewport / command / ค่าที่วัดได้) ไม่ใช่คำอธิบายอย่างเดียว

  ## Expected Behavior
  - F1  ระบบต้อง<พฤติกรรมที่ถูกต้อง>   (EARS; ใช้ stable F-ID และหนึ่ง criterion
        ต่อหนึ่งผลแก้ที่สังเกตได้ โดย task อ้าง ID ในบรรทัด Satisfies:)
  - F2  ...

  ## Unchanged Behavior
  - B1  เมื่อ<เงื่อนไข> ระบบต้องคง<พฤติกรรมเดิม>
  - B2  ... (ใช้ stable B-ID และครอบคลุม regression risk ทุกข้อที่พบใน Phase 1
        รวมทุกอย่างใน do-not-modify list)

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
