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

เลือก workflow จาก canonical artifact shape บน disk เท่านั้น:

- มี `bugfix.md` และไม่มี `requirements.md`/`design.md` → `bugfix`
- มี `requirements.md` กับ `design.md` และไม่มี `bugfix.md` → feature shape ที่
  Requirements-First และ Design-First converge แล้ว (`requirements-first` กับ
  `design-first` ใช้ phase contract เดียวกันสำหรับ implement); ใช้
  `requirements-first` เป็น canonical label ของ shape นี้โดยไม่เดาประวัติจาก prose
- shape อื่น → หยุด เพราะ missing หรือ ambiguous

ก่อนอ่าน implementation context, แก้ source หรือแก้ `tasks.md` ให้รัน shared phase gate:

```bash
python3 scripts/spec_contract.py gate phase --feature <feature> --phase implement --workflow <workflow>
```

คำสั่งต้องคืน exit `0` ก่อนจึงทำต่อได้ หาก artifact missing, malformed, unknown หรือ
ไม่ approved ให้หยุดตาม diagnostic ของ engine ทันที ห้ามใช้ conversation, checkbox
หรือ code existence แทน approval และห้าม flip upstream status เพื่อข้าม gate.

Resolve selector ผ่าน shared engine ให้เป็น exact `<task-id>` ตาม file order โดยอ่านเฉพาะ
ID ที่ CLI คืน ห้ามอ่าน task body ก่อน slice.

หาก `$ARGUMENTS == all` ให้เลือกเฉพาะ pending task IDs:

```bash
python3 scripts/spec_contract.py task-ids --feature <feature> --pending --format lines
```

หาก `$ARGUMENTS` เป็น exact ID หรือ numeric range ให้ใช้ selector เดิม:

```bash
python3 scripts/spec_contract.py task-ids --feature <feature> --selector "$ARGUMENTS" --format lines
```

ทั้งสอง branch ต้องคืน exit `0` ก่อนเข้า loop; selector unknown หรือคำสั่งคืน non-zero ให้หยุด
ตาม diagnostic ทันที. นำทุก ID ที่ CLI คืนเข้า loop ด้านล่างตาม file order โดยไม่ข้าม ID.

For EACH exact task ID:

0. รัน slice ก่อนอ่าน implementation context อื่น:

   ```bash
   scripts/spec-slice.sh <feature> <task-id>
   ```

   ถ้า `spec-slice.sh` คืน non-zero ให้หยุดทันที ใช้ output ที่ exit `0` เป็น initial slice
   และห้ามแทนด้วย grep หรือ parser ใน skill.

   หาก output มี `MISSING:` ให้ full-read upstream artifacts ทั้งหมดตาม workflow:

   - feature: `requirements.md`, `design.md` และ `tasks.md`
   - bugfix: `bugfix.md` และ `tasks.md`

   หลัง full-read ให้รัน gate ซ้ำด้วย workflow เดิม:

   ```bash
   python3 scripts/spec_contract.py gate phase --feature <feature> --phase implement --workflow <workflow>
   ```

   gate ซ้ำต้องคืน exit `0` ก่อนทำต่อ ห้ามเดา mapping ที่หาย หาก full-read พบ artifact
   missing, malformed, unknown หรือไม่ approved ให้หยุดโดยไม่แก้ source หรือ `tasks.md`.

1. หลัง slice, fallback และ gate ซ้ำเสร็จแล้ว จึงรัน `scripts/spec-state.sh <feature>` และ
   reconcile target task กับ dependencies เทียบ filesystem โดยถือ filesystem เป็น ground
   truth; checkbox กับ git log อาจผิด และ untracked files ไม่อยู่ใน `git diff --stat`.
   ถ้าพบความขัดแย้ง ให้หยุดรายงานก่อนแก้ `tasks.md`. จากนั้นอ่าน task slice กับ supplemental
   context ที่ slice ระบุ พร้อม @.ai/shared/ARCHITECTURE.md โดย slice เป็น context เริ่มต้นที่
   authoritative; full-read ใช้เฉพาะ fallback `MISSING:` ข้างต้น.
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
