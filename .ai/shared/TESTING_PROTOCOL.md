# Testing Protocol

> Vendor-neutral. How any agent proves a change is correct.
> Canonical source for the testing expectations in [TASK_PROTOCOL.md](TASK_PROTOCOL.md)
> and the test-report shape in [OUTPUT_FORMATS.md](OUTPUT_FORMATS.md).

## Runner and scope

- Test runner: **the project test runner** — declared via the `SDD_TEST_CMD` env var, or a
  `package.json` test script for a Node project. The framework does not assume a specific
  runner.
- Unit tests cover **pure logic** only, co-located with the logic under test in the project
  test directory; that is where headless tests go.
- A project has an integration-test tier against a real service only if it ships that boundary.
  Repo นี้มี `console/backend`, SQLite event logs, GitHub/provider ports และ Console SPA จึงมี
  backend/integration/fault tests จริง; external live provider calls ยังแยกเป็น manual conformance.

## Pure-logic-first

Extract testable logic (formulas, validation, formatting) into **pure functions** in the
project test directory, and get their unit tests GREEN before wiring any UI. Correctness
then does not get entangled with rendering, and the numeric / behavioral acceptance criteria
are closed before the component layer exists. Components call these functions; they never
embed the formula in the view layer. See the patterns in [LESSONS.md](LESSONS.md) and the
layering in [ARCHITECTURE.md](ARCHITECTURE.md).

## Test quality

- Each test maps to a REQ-ID (or F-ID / B-ID for a bugfix) — a test exists because a
  requirement demands it.
- Assert the **observable behavior** (returned value, computed result, rendered output,
  layout measurement), NEVER an implementation detail (which CSS class was used, which
  private function was called). Asserting implementation detail is a known way to pass
  while the real failure mode slips through.
- Cover the happy path AND the error/edge cases (`IF ... THEN` from
  [EARS.md](EARS.md)).
- For a bugfix, validation is three-dimensional: (a) a repro test that is RED before the
  fix and GREEN after (the F-IDs), (b) a 1:1 assertion for every B-ID, (c) each
  assertion checks the observable failure mode.
- No `.only` / `.skip` may be committed. Coverage must not fall below the project threshold.
  Repo นี้รัน tests ของทุก Node workspace ผ่าน `scripts/ci-test-scope.sh`; downstream project
  ยังใช้ `SDD_TEST_CMD`/`SDD_TYPECHECK_CMD` ผ่าน task gate. ดู exact CI และ required-check
  activation ใน [SECURITY_RULES.md](SECURITY_RULES.md).

## UI verification

If the project ships a UI, logic that cannot be tested headless under the project test
runner is verified in the project's target runtime. READ the project UI-verify reference
FIRST — it owns how to stand the UI up for verification (e.g. a production build rather than
a dev server) and what to confirm at each acceptance viewport before trusting a result. The
framework does not prescribe a specific UI framework or styling system.

Before ANY UI-based verification, READ the project UI-verify reference — it contains the
probe recipes, the viewport / scrollbar gotchas, the hydration check, and the
false-positive traps (SVG geometry, gradient backgrounds, focus-ring measurement):

`.claude/skills/spec-implement/references/browser-verify.md`

(That is the project UI-verify reference — currently the Claude browser-verify skill; it is
the canonical UI-verify reference for every agent until/unless it moves under `.ai/`.)

## Evidence block format

When a task is marked `- [x]` in `tasks.md`, append an `Evidence:` block in the SAME
edit — the checkbox and the evidence flip together. Record what you ACTUALLY ran and
observed, not the planned check. เขียนคำอธิบายตาม
[นโยบายภาษาของผลลัพธ์](TASK_PROTOCOL.md#ภาษาของผลลัพธ์) โดยคงคำสั่งและ raw output:

ใช้ระยะเยื้องสองช่องตามตัวอย่าง เว้นบรรทัดก่อนและหลัง `Evidence:` เพื่อแสดงหลักฐาน
เป็นย่อหน้าและรายการย่อย ห้ามเว้นบรรทัดระหว่างบรรทัดงานกับ `Satisfies:`/`Verify:`
เพราะตัวอ่านใช้บรรทัดว่างเป็นขอบเขตข้อมูล ตัวอย่างนี้ยังไม่ใช่หลักฐานว่างานเสร็จ:

```markdown
- [ ] 1. <ชื่องาน>
  Satisfies: REQ-1. Verify: <คำสั่งตรวจ>.

  Evidence:

  - test: `<คำสั่งจริง>` -> <ผลที่สังเกต>
  - viewports: <ผลที่วัดจริง; งานที่ไม่มีหน้าจอใช้ `n/a — logic-only`>
  - deviations: <ไม่มี | สิ่งที่ต่างจากข้อกำหนดพร้อมเหตุผล>
```

ก่อนส่งมอบ เปิดหน้าตัวอย่างเอกสาร ตรวจว่าแต่ละผลตรวจเป็นคนละรายการและไม่ต่อท้ายคำอธิบายงาน
พร้อมรันตัวตรวจหลักฐานและตัวตรวจการอ้างอิง เพื่อให้ทั้งหน้าตาและการอ่านข้อมูลถูกต้อง

- The command must be the exact one you ran, copy-pasteable.
- For a browser task, the viewport line records the measured `clientWidth` outcome at
  each acceptance viewport; never assert a pass you did not observe.
- If a check could not be run, say so explicitly in `deviations:` — do not leave it
  blank or claim a pass.

The task gate is env-driven, not stack-hardcoded: `.ai/bin/gate-task.sh` proves a flipped
`[x]` task green by running the project typecheck command (`SDD_TYPECHECK_CMD` env, or a
`package.json` typecheck script for a Node project) and the project test command
(`SDD_TEST_CMD` env, or a `package.json` test script for a Node project). When neither a
command nor a matching script exists, the code-green check is skipped and only this Evidence
gate applies.

Before marking the LAST task (or any assembly task), run the REQ-trace check; any
uncovered REQ it reports is a blocker, never skipped silently.
