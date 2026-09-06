# Task Protocol

> Vendor-neutral. Every agent (Claude, Codex, OpenCode, Pi, human) follows this flow.
> This is the canonical source. Harness-specific skills/commands are thin wrappers.

This project practices STRICT spec-driven development: **specifications come before
code, ALWAYS**. Do not jump to implementation for any non-trivial feature.

## ภาษาของผลลัพธ์

ทุก `spec-*` และทุก harness ใช้นโยบายนี้ร่วมกัน: สร้างหรือแก้ข้อความอธิบายเป็นภาษาไทย
ทั้ง requirements (เนื้อหา User Story และ Acceptance Criteria), design, tasks, bugfix,
implementation plan, review/test report, handoff, retro, ชื่อและเนื้อหา issue ที่ sync
รวมถึงข้อความที่สื่อสารกับผู้ใช้

template ภาษาอังกฤษกำหนดโครงสร้าง ไม่ใช่ภาษาของผลลัพธ์ เติมชื่อ feature/task และเนื้อหา
เป็นภาษาไทย คงหัวข้อบังคับตาม template เดิม ส่วนหัวข้อที่ตั้งเองใช้ภาษาไทย
เมื่อแก้ spec เดิม ให้ปรับข้อความเฉพาะ scope งาน ไม่แปลเอกสารเก่าย้อนหลังทั้งชุด

| ส่วน | สิ่งที่คงเดิม |
|---|---|
| ข้อมูลทางเทคนิค | code, identifier, command, path, technical term และ raw error/log |
| Requirement | REQ-ID / F-ID / B-ID และ EARS keywords: THE SYSTEM SHALL, WHEN, WHILE, WHERE, IF, THEN |
| สถานะและ schema | `Status:` พร้อมค่า enum เดิม, schema keys และ checkbox syntax |
| Task metadata | `Satisfies:`, `Depends on:`, `Verify:`, `Batch:` และ `Evidence:` พร้อม keys ภายใน |
| โครงเอกสาร | หัวข้อบังคับและคอลัมน์ตาราง เช่น Requirement Traceability, Design element, REQ, Section |

ค่าในคอลัมน์ `Section` ต้องตรงกับ heading จริงที่อ้างอิง แม้ heading นั้นเป็นภาษาไทย

ตัวอย่างรูปแบบภาษาไทย (task ยังไม่เสร็จ; ไม่ใช่หลักฐานว่า test ผ่าน):

```markdown
## REQ-1: การบันทึกฉบับร่าง
**Acceptance Criteria (EARS):**
- 1.1 WHEN ผู้ใช้กดบันทึก THE SYSTEM SHALL บันทึกเนื้อหาปัจจุบันเป็นฉบับร่าง

- [ ] 1. บันทึกฉบับร่าง — ผู้ใช้เรียกคืนเนื้อหาที่บันทึกไว้ได้
     Satisfies: REQ-1.1. Verify: ทดสอบบันทึกแล้วโหลดกลับและเปรียบเทียบเนื้อหา.
```

ก่อนส่งมอบ อ่านผลลัพธ์ซ้ำ: เนื้อหาที่สร้างหรือแก้เป็นภาษาไทย, machine contract คงเดิม,
traceability อ้างอิงได้จริง และ Evidence ระบุเฉพาะคำสั่งกับผลที่รันและสังเกตจริง

## The non-negotiable workflow

Every feature flows through three artifacts under `.ai/specs/<feature-name>/`, IN ORDER,
with an **APPROVAL GATE** after each:

1. `requirements.md` — WHAT the system must do (behavior, in [EARS notation](EARS.md))
2. `design.md` — HOW it will be built (architecture)
3. `tasks.md` — discrete, trackable implementation steps

(Design-First swaps 1 and 2 — same approval gates.)

After producing each artifact, **STOP and ask for review** before generating the next.
Wait for explicit approval ("approved" / "continue"). The only exception is a
quick-mode invocation that runs all phases without gates, used only for small,
well-understood features.

### Approval lives in the file, not the conversation

When an artifact is approved, flip its header line to
`> Status: approved <YYYY-MM-DD>` as part of that turn. A conversation is temporary
working memory; the artifact is the durable record. A downstream phase that finds
`> Status: draft` must warn and ask for confirmation before proceeding — never assume
approval.

## Phases

| Phase | Artifact | Gate after |
|---|---|---|
| Requirements | `requirements.md` (EARS, stable REQ-IDs) | review |
| Analyze (optional, for logic-heavy / sensitive features) | audit notes | — |
| Design | `design.md` (architecture, traceability table) | review |
| Tasks | `tasks.md` (cohesive checklist) | review |
| Implement | code + tests, Evidence per task | review at TASK boundaries |

A change in requirements PROPAGATES to design and tasks — keep specs in sync. If a
derived/downstream artifact conflicts with an upstream one, fix the downstream one;
if the upstream one looks wrong, STOP and ask.

## Task sizing

Size tasks as **cohesive, independently verifiable slices of behavior — NOT
micro-steps.** Assume you can hold the whole feature in context and implement a
complete task end-to-end in one pass, even when it spans many files.

- Task count is an outcome, not a quota. Many features land around **5-10 tasks**,
  but there is no minimum or maximum at the spec level.
- If the count exceeds 10, review whether the feature is too broad or tasks are
  micro-steps. Keep any count when every task remains cohesive, independently
  verifiable, traceable to requirements, and feasible to implement and verify in one
  pass. Never split cohesive behavior or merge unrelated behaviors solely to hit a
  target count.
- Do NOT pre-split a task into `1.1` / `1.2` sub-steps inside `tasks.md`. Decompose
  into working steps yourself at execution time using your own internal TODO list.
- Prefer **vertical slices** (model -> API -> validation -> tests) over horizontal
  layers that are useless alone.
- Logic-first: extract testable logic (formulas, validation) into pure functions
  with unit tests green BEFORE wiring UI. See [TESTING_PROTOCOL.md](TESTING_PROTOCOL.md).

## Steps every agent follows (per task)

1. **Read context** — the task plus its linked REQ-IDs in `requirements.md` (or
   F-IDs/B-IDs in `bugfix.md`), the relevant parts of `design.md`, and the project
   rules: [PROJECT_CONTEXT.md](PROJECT_CONTEXT.md), [ARCHITECTURE.md](ARCHITECTURE.md),
   [CODING_STANDARDS.md](CODING_STANDARDS.md), [LESSONS.md](LESSONS.md).
2. **Scope** — restate what this task does and does NOT cover. Batch any ambiguity
   into questions and ask BEFORE assuming.
3. **Identify affected files** — list every file you expect to create or edit. The
   filesystem is ground truth; checkboxes and git log can lie, and untracked files do
   not appear in `git diff --stat`. Reconcile `tasks.md` against reality first.
4. **Plan** — จัดทำ task-level implementation plan โดยใช้โครง canonical ใน
   [OUTPUT_FORMATS.md](OUTPUT_FORMATS.md) และ
   [implementation plan template](../templates/implementation-plan-template.md) แบบกรอกข้อมูลได้ คง
   execution steps เป็นรายละเอียดการทำงานภายใน approved cohesive task.
5. **Minimal change** — implement the WHOLE task in one cohesive pass. Touch only what
   the task requires. Match existing conventions exactly.
6. **Tests** — write or extend tests proving the task satisfies its IDs. See
   [TESTING_PROTOCOL.md](TESTING_PROTOCOL.md).
7. **Summary** — mark the task `- [x]` and, in the SAME edit, append an `Evidence:`
   block under that task line (box and evidence flip together). State which REQ-IDs
   are now satisfied. Record what you actually RAN and OBSERVED, not the planned check.
8. **Handoff** — when the session ends or context is about to be cleared/compacted,
   write current state into the spec files and a handoff note. See
   [AGENT_HANDOFF_PROTOCOL.md](AGENT_HANDOFF_PROTOCOL.md) and
   [CONTEXT_MANAGEMENT.md](CONTEXT_MANAGEMENT.md).
9. **Risks** — surface anything risky, deferred, or assumed in the summary (and a risk
   report when warranted — see [OUTPUT_FORMATS.md](OUTPUT_FORMATS.md)).

### Planning contract

task-level implementation plan ต้องระบุเป้าหมายและ `REQ-ID`/`F-ID`/`B-ID` ที่เชื่อมโยง,
scope boundary, ไฟล์ที่ได้รับผลกระทบพร้อมสิ่งที่จะทำและเหตุผล, load-bearing decisions,
reuse anchors, dependency-ordered steps พร้อม executable verification รวมถึง blockers,
open questions หรือ assumptions ทั้งหมด เพิ่ม risks เฉพาะเมื่อเกี่ยวข้อง ใช้โครง plan ใน
[OUTPUT_FORMATS.md](OUTPUT_FORMATS.md), กรอก
[implementation plan template](../templates/implementation-plan-template.md) และให้รายละเอียด
ด้าน test และ security เป็นไปตาม [TESTING_PROTOCOL.md](TESTING_PROTOCOL.md) และ
[SECURITY_RULES.md](SECURITY_RULES.md)

Execution steps เป็น working detail ภายใน approved cohesive task เดียว ส่วน review, checkbox
และ `Evidence:` ยังคงอยู่ที่ task boundary หาก plan มี migration, destructive หรือ
irreversible operation หรือ breaking external contract ให้บันทึก rollback และ recovery,
compatibility impact และ affected consumers ตาม owner docs ข้างต้น

Pause for confirmation at each TASK boundary (not after every file). Implement several
tasks in one go only when explicitly asked (a range or "all"), proceeding in
dependency order, stopping early only if a test fails or a requirement turns out
infeasible.

## Definition of Done (a task is done only when ALL hold)

- The whole task is implemented end-to-end, touching every file it needs.
- Tests prove every cited REQ-ID / F-ID / B-ID; tests pass.
- For the last task (or any assembly task), every REQ is traced to satisfying
  code/tests — no uncovered REQ. Cross-check the full section/behavior list in
  `requirements.md` against what exists; a section listed in REQ but in no task is a
  blocker to surface, not a silent skip.
- `tasks.md` checkbox is `- [x]` with an `Evidence:` block recording the exact command
  run and observed result.
- The change passes the enforcement floor: typecheck + tests + lint green; no secrets;
  branch/push rules respected. See [SECURITY_RULES.md](SECURITY_RULES.md).
- A summary states satisfied IDs, modified files, and any deviation with its reason.

## Explicit prohibitions

- Do **NOT** rewrite, reformat, or "improve" unrelated code, comments, or adjacent
  formatting. Every changed line must trace to the task.
- Do **NOT** invent requirements, behaviors, or scope not present in the spec. Missing
  scope is surfaced and approved, never silently added.
- Do **NOT** skip tests, commit `.only` / `.skip`, or assert a pass you did not
  observe.
- Do **NOT** write vague summaries ("done", "fixed", "should work"). Summaries cite
  IDs, files, exact commands, and observed results.
- Do **NOT** clear or compact context in the middle of an unfinished task whose state
  lives only in the conversation — persist it first.
- Do **NOT** commit or push unless explicitly asked. Never push to `main` / `develop`
  directly; everything goes through a PR.
