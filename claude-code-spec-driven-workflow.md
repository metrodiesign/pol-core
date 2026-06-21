# Spec-Driven Development บน Claude Code (สไตล์ Kiro)

> เอกสารนี้แปลง workflow ของ [Kiro](https://kiro.dev) มาใช้กับ **Claude Code** และ **Claude Code CLI** โดยตรง — ใช้ฟีเจอร์เนทีฟทั้งหมด (CLAUDE.md, `.claude/rules/`, skills, subagents, hooks, settings.json, คำสั่ง CLI) ไม่ต้องลงเครื่องมือเสริมใด ๆ
>
> ข้อมูลทั้งหมดอ้างอิงจากเอกสารทางการของ Claude Code (ดูลิงก์ท้ายไฟล์) ตรวจสอบ ณ มิ.ย. 2026 — Claude Code อัปเดตบ่อย ถ้าคำสั่งไหนไม่ตรง ให้พิมพ์ `/help` ดูเวอร์ชันจริง

---

## 0. Kiro → Claude Code: ตารางแปลงโดยตรง

นี่คือหัวใจของเอกสารนี้ ทุกอย่างใน Kiro มี "ของจริง" ใน Claude Code:

| Kiro                                         | Claude Code (เนทีฟ)                          | ไฟล์/คำสั่ง                                                      |
| -------------------------------------------- | -------------------------------------------- | ---------------------------------------------------------------- |
| Master prompt / behavior                     | **CLAUDE.md** (รัฐธรรมนูญที่อ่านทุก session) | `./CLAUDE.md`, `~/.claude/CLAUDE.md`                             |
| Steering (always)                            | CLAUDE.md + `@import`                        | `@docs/tech.md` ใน CLAUDE.md                                     |
| Steering (fileMatch)                         | **Rules แบบมีเงื่อนไข**                      | `.claude/rules/*.md` + frontmatter `paths:`                      |
| Steering (manual)                            | Skill ที่ปิด auto-invoke                     | `.claude/skills/<x>/SKILL.md` + `disable-model-invocation: true` |
| Steering (auto)                              | Skill ที่ match จาก description              | `.claude/skills/<x>/SKILL.md`                                    |
| Steering foundation (product/tech/structure) | Rules + import                               | `.claude/rules/product.md` ฯลฯ                                   |
| Spec workflow phases                         | **Slash commands (เป็น skills)**             | `.claude/skills/spec-*/SKILL.md` → `/spec-*`                     |
| Hooks (File Save → lint)                     | **Hook** `PostToolUse` matcher `Edit\|Write` | `.claude/settings.json`                                          |
| Hooks (Agent Stop → test)                    | **Hook** `Stop`                              | `.claude/settings.json`                                          |
| Hooks (Pre/Post Task Execution)              | **Hook** `TaskCreated` / `TaskCompleted`     | `.claude/settings.json`                                          |
| Hooks (User Prompt Submit)                   | **Hook** `UserPromptSubmit`                  | `.claude/settings.json`                                          |
| Skills                                       | **Skills** (มาตรฐานเดียวกัน)                 | `.claude/skills/`                                                |
| Custom agents / Autopilot                    | **Subagents**                                | `.claude/agents/*.md`                                            |
| `#spec` ในแชต                                | `@` อ้างไฟล์ + รัน slash command             | `@.ai/specs/<feature>/`                                      |
| Run all Tasks (parallel)                     | รันทีละ task หรือทั้งชุดตามลำดับ dependency  | `/spec-implement all`                                            |

**สรุปสั้น:** CLAUDE.md = พฤติกรรม, `.claude/rules/` = มาตรฐาน, `.claude/skills/spec-*` = ขั้นตอน, `.claude/settings.json` = automation, `.claude/agents/` = ผู้เชี่ยวชาญเฉพาะทาง

---

## 1. โครงสร้างโฟลเดอร์ทั้งหมด

ตั้งค่าครั้งเดียวต่อโปรเจกต์ (ทุกอย่างใน `.claude/` commit ขึ้น git ได้ → ทั้งทีมได้ workflow เดียวกัน):

```
your-project/
├── CLAUDE.md                          # รัฐธรรมนูญ spec-driven (commit)
├── CLAUDE.local.md                    # override ส่วนตัว (gitignore)
├── .claude/
│   ├── settings.json                  # hooks + permissions (commit)
│   ├── settings.local.json            # ส่วนตัว (gitignore)
│   ├── rules/                         # = Steering ของ Kiro
│   │   ├── product.md                 #   inclusion: always
│   │   ├── tech.md                    #   inclusion: always
│   │   ├── structure.md               #   inclusion: always
│   │   ├── api-design.md              #   paths: → เฉพาะไฟล์ API (fileMatch)
│   │   ├── components.md              #   paths: → เฉพาะ *.tsx
│   │   └── lessons.md                 #   บทเรียนที่ promote จาก retrospective (คัด+ตัด)
│   ├── skills/                        # = workflow phases + skills
│   │   ├── spec-new/SKILL.md          #   /spec-new
│   │   ├── spec-requirements/SKILL.md #   /spec-requirements
│   │   ├── spec-analyze/SKILL.md      #   /spec-analyze
│   │   ├── spec-design/SKILL.md       #   /spec-design
│   │   ├── spec-tasks/SKILL.md        #   /spec-tasks
│   │   ├── spec-implement/SKILL.md    #   /spec-implement
│   │   ├── spec-bugfix/SKILL.md       #   /spec-bugfix
│   │   ├── spec-pbt/SKILL.md          #   /spec-pbt
│   │   └── spec-retro/SKILL.md        #   /spec-retro (รันก่อน /clear)
│   ├── agents/                        # = custom agents ของ Kiro
│   │   ├── spec-architect.md
│   │   ├── bug-investigator.md
│   │   └── pbt-runner.md
│   └── specs/                         # artifact ของแต่ละ feature
│       ├── user-authentication/
│       │   ├── requirements.md
│       │   ├── design.md
│       │   └── tasks.md
│       └── shopping-cart/
├── retrospectives/                    # บันทึก retrospective ราย session (commit)
├── scripts/                           # automation
│   ├── pane-loop.sh                   #   orchestrator: 1 task = 1 iTerm pane (ดู §7)
│   └── *cost*.py / *cost*.sh          #   cost ledger ที่ /spec-retro อ่าน (ดู §11)
└── src/
```

---

## 2. CLAUDE.md — รัฐธรรมนูญของ Agent

> CLAUDE.md ถูกอ่านทุก session อัตโนมัติ เทียบเท่า "system prompt + Steering(always)" ของ Kiro รวมกัน เริ่มเร็วที่สุดด้วยคำสั่ง `/init` แล้วค่อยวางเนื้อหาด้านล่างทับ

วางไฟล์นี้ที่ **root ของโปรเจกต์** ชื่อ `CLAUDE.md`:

```markdown
# Spec-Driven Development Constitution

This project practices STRICT spec-driven development. Specifications come before
code, ALWAYS. Do not jump to implementation for any non-trivial feature.

## The non-negotiable workflow

Every feature flows through three artifacts under `.ai/specs/<feature-name>/`,
in order, with an APPROVAL GATE after each:

1. requirements.md — WHAT the system must do (behavior, in EARS notation)
2. design.md — HOW it will be built (architecture)
3. tasks.md — discrete, trackable implementation steps

After producing each artifact, STOP and ask me to review before generating the
next. Wait for explicit approval ("approved" / "continue"). The only exception is
when I invoke `/spec-quick`, which runs all phases without gates.

## How to run each phase

Use the project slash commands — do not improvise the structure:
/spec-new <idea> choose a workflow and ask clarifying questions
/spec-requirements generate requirements.md (EARS)
/spec-analyze audit requirements for gaps/conflicts before design
/spec-design generate design.md
/spec-tasks generate tasks.md
/spec-implement <id|range|all> implement one or more cohesive tasks, end-to-end
/spec-bugfix <bug> root-cause-first bug workflow
/spec-pbt extract properties and write property-based tests
/spec-retro session retrospective — run at END of session, BEFORE /clear

## EARS notation (mandatory for requirements)

Write every functional requirement using one of these patterns, each with a
stable ID (REQ-1.2):

- THE SYSTEM SHALL <behavior> (ubiquitous)
- WHEN <trigger> THE SYSTEM SHALL <behavior> (event-driven)
- WHILE <state> THE SYSTEM SHALL <behavior> (state-driven)
- WHERE <feature included> THE SYSTEM SHALL <behavior> (optional)
- IF <unwanted condition> THEN THE SYSTEM SHALL <response> (error handling)
  Requirements must be atomic, unambiguous, and testable.

## Project standards

See @.claude/rules/product.md for what we're building and why.
See @.claude/rules/tech.md for the tech stack you MUST prefer.
See @.claude/rules/structure.md for file organization and conventions.

## Task sizing (this project runs a large-context, high-effort model)

Size tasks as cohesive, independently verifiable slices of behavior — NOT micro-steps.
Assume you can hold the whole feature in context and implement a complete task
end-to-end in one pass, even when it spans many files. A typical feature is about
5-10 tasks, not 20-30. Do NOT pre-split a task into 1.1/1.2 sub-steps inside
tasks.md; decompose into working steps yourself at execution time using your own
internal TODO list. Prefer vertical slices (model → API → validation → tests) over
horizontal layers that are useless alone.

## Working agreements

- Keep specs in sync: a change in requirements propagates to design and tasks.
- Implement a whole task (it may touch many files) end-to-end, including its tests,
  then mark "- [x]" and state which REQ IDs are now satisfied. Pause for review at
  TASK boundaries, not after every file. Implement several tasks in one go only when
  I ask (a range or "all"), proceeding in dependency order.
- Match the conventions in structure.md exactly.
- When something is ambiguous, batch your questions and ask before assuming.
- Be concise and engineering-focused.
```

### `~/.claude/CLAUDE.md` (ระดับ global ส่วนตัว — ทุกโปรเจกต์)

ใส่ค่านิยมส่วนตัวที่ใช้กับทุกงาน เช่น:

```markdown
# Personal defaults

- I prefer TDD: write tests first, then implementation.
- Comments explain WHY, not WHAT.
- When you finish a turn, tell me the exact command to verify the result.
- Default to spec-driven for any feature larger than a single function.
```

### กลไกสำคัญของ CLAUDE.md

| กลไก                | คำอธิบาย                                                                                                                                                                                               |
| ------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| **ลำดับชั้น**       | Claude อ่าน CLAUDE.md โดยไล่ขึ้นไปตามต้นไม้ไดเรกทอรีจาก working directory ไฟล์ที่อยู่สูงกว่ามีลำดับความสำคัญและโหลดก่อน ลำดับ: managed (องค์กร) → user (`~/.claude/`) → project → subdirectory         |
| **`@import`**       | ดึงไฟล์อื่นเข้ามาด้วย `@path/to/file` รองรับทั้ง path สัมพัทธ์และสัมบูรณ์ เช่น `@.claude/rules/tech.md` หรือ `@README.md` import ซ้อนได้ลึกสุด 5 ชั้น และ import ที่อยู่ใน code block จะไม่ถูกประมวลผล |
| **CLAUDE.local.md** | สำหรับ preference ส่วนตัวต่อโปรเจกต์ที่ไม่อยากcommit ให้สร้าง CLAUDE.local.md ที่ root มันโหลดคู่กับ CLAUDE.md (อย่าลืมใส่ใน .gitignore)                                                               |
| **`/init`**         | คำสั่ง /init เป็นวิธีเร็วที่สุดในการตั้งค่า project memory มันสร้างไฟล์ CLAUDE.md พร้อมเอกสารโครงสร้างพื้นฐานของโปรเจกต์                                                                               |
| **`/memory`**       | เปิดดู/แก้ไฟล์ memory ทั้งหมดที่โหลดอยู่ และเช็กว่าไฟล์ไหนถูกโหลดบ้าง                                                                                                                                  |

---

## 3. Steering → `.claude/rules/`

> Kiro มี 4 inclusion modes — Claude Code ทำได้ครบด้วย `.claude/rules/` + frontmatter และ skills

### 3.1 Foundation files (โหมด `always`)

สร้าง 3 ไฟล์นี้ใน `.claude/rules/` แล้ว `@import` จาก CLAUDE.md (ดูข้อ 2)

**`.claude/rules/product.md`**

```markdown
# Product Overview

## Purpose

<ผลิตภัณฑ์นี้แก้ปัญหาอะไร — หนึ่งย่อหน้า>

## Target Users

- <ผู้ใช้ + ความต้องการ>

## Key Features

- <ฟีเจอร์หลัก>

## Business Objectives

- <metric ที่วัดความสำเร็จ>

## Non-Goals

- <สิ่งที่จงใจไม่ทำ — กัน scope creep>
```

**`.claude/rules/tech.md`**

```markdown
# Technology Stack

## Languages & Runtimes

- <เช่น TypeScript 5.x, Node.js 20 LTS>

## Frameworks & Core Libraries

- <framework + เหตุผล>

## Data Layer

- <database, ORM, caching>

## Tooling

- <test runner, linter, formatter, build, CI>

## Hard Constraints

- <ข้อห้ามฝ่าฝืน เช่น latency < 200ms, ต้อง compliant PDPA>

## Rule

Prefer this stack over alternatives. Do not introduce a new library without
stating why and asking for approval.
```

**`.claude/rules/structure.md`**

```markdown
# Project Structure

## Folder Layout

<โครงโฟลเดอร์ + อธิบายแต่ละส่วน>

## Naming Conventions

- ไฟล์ / คอมโพเนนต์ / ฟังก์ชัน

## Import Ordering

1. external 2. internal absolute 3. relative

## Architectural Patterns

- <เช่น ทุก API ต้องผ่าน validation layer>

## Anti-Patterns

- <เช่น ห้ามเรียก DB ตรงจาก component>
```

### 3.2 Conditional rules (โหมด `fileMatch`)

ใช้ frontmatter `paths:` ให้ rule โหลดเฉพาะตอนแตะไฟล์ที่ตรงแพทเทิร์น — เทียบเท่า `fileMatch` ของ Kiro เป๊ะ ๆ

**`.claude/rules/components.md`**

```markdown
---
paths:
  - "src/components/**/*.tsx"
  - "src/components/**/*.jsx"
---

# Component Standards

- ทุก component เป็น function component + hooks
- props ต้องมี type ชัดเจน ห้าม any
- <กฎเฉพาะ component อื่น ๆ>
```

**`.claude/rules/api-design.md`**

```markdown
---
paths:
  - "src/app/api/**/*"
  - "src/server/**/*"
---

# API Design Standards

- ทุก endpoint return รูปแบบ error เดียวกัน: { error: { code, message } }
- ใช้ HTTP status code ตามมาตรฐาน
- validate input ที่ชั้น handler ก่อนเสมอ
```

> `.claude/rules/*.md` ที่**ไม่มี** frontmatter `paths:` จะโหลดทุก session (เหมือน `always`)

### 3.3 โหมด `manual` และ `auto`

ทำผ่าน **skills** (ดูข้อ 4):

- **manual** = ใส่ `disable-model-invocation: true` ใน frontmatter → เรียกได้เฉพาะตอนพิมพ์ `/ชื่อ` เอง
- **auto** = ใส่ `description` ที่ชัดเจน → Claude โหลดเองเมื่อ request ตรงกับ description

---

## 4. Slash Commands (เป็น Skills) — Workflow แต่ละเฟส

> จุดสำคัญ: custom commands ถูกรวมเข้ากับ skills แล้ว ไฟล์ที่ .claude/commands/deploy.md กับ skill ที่ .claude/skills/deploy/SKILL.md ต่างก็สร้าง /deploy และทำงานเหมือนกัน ไฟล์ .claude/commands/ เดิมยังใช้ได้ ส่วน skills เพิ่มฟีเจอร์: โฟลเดอร์สำหรับไฟล์ประกอบ, frontmatter ควบคุมว่าใครเรียก, และให้ Claude โหลดอัตโนมัติเมื่อเกี่ยวข้อง
>
> เราจะใช้รูปแบบ skill (`.claude/skills/<name>/SKILL.md`) เพราะได้ทั้ง `/ชื่อ` และ auto-invoke

**frontmatter ที่ใช้ได้:** `name` (ต้องตรงชื่อโฟลเดอร์), `description` (≤1024 ตัว ใช้ตัดสินใจ auto-invoke), `argument-hint`, `allowed-tools`, `model`, `disable-model-invocation`, `context: fork`, และ `hooks`
**ตัวแปร arguments:** `$1 $2 ...` หรือ `$ARGUMENTS` (ทั้งหมด) · แทรกผล shell ด้วย `!cmd` · อ้างไฟล์ด้วย `@path`

### `/spec-new` — เริ่ม feature + เลือก workflow

`.claude/skills/spec-new/SKILL.md`

```markdown
---
name: spec-new
description: Start a new feature spec. Use at the beginning of any non-trivial feature to choose a workflow and gather requirements before coding.
argument-hint: <short description of the feature>
---

# Start a Feature Spec

The feature idea is: $ARGUMENTS

Step 1 — Recommend ONE workflow and explain why in two sentences:

- Requirements-First (Requirements → Design → Tasks): I know the behavior I want;
  architecture is flexible. Best for product/customer-driven features.
- Design-First (Design → Requirements → Tasks): I have an architecture in mind or
  strict non-functional constraints (latency, compliance).
- Quick (`/spec-quick`): well-understood feature, no approval gates wanted.

Step 2 — Create the spec folder at `.ai/specs/<kebab-case-name>/`.

Step 3 — Ask me ALL clarifying questions you need in a single message:
who the user is, what they want, why, success criteria, edge cases, constraints.

Do NOT generate any artifact yet. Wait for my answers, then tell me to run
`/spec-requirements` (or `/spec-design` for Design-First).
```

### `/spec-requirements` — สร้าง requirements.md

`.claude/skills/spec-requirements/SKILL.md`

```markdown
---
name: spec-requirements
description: Generate the requirements.md artifact for the active feature spec using EARS notation. Use after /spec-new and after I've answered clarifying questions.
argument-hint: <feature folder name (optional)>
---

# Generate requirements.md

Write `.ai/specs/<feature>/requirements.md` with this structure:

# Requirements: <Feature Name>

## Overview

  <one paragraph tying this to product.md>

## REQ-1: <Capability, e.g. User Registration>

**User Story:** As a <role>, I want <goal>, so that <benefit>.
**Acceptance Criteria (EARS):**

- 1.1 WHEN <event> THE SYSTEM SHALL <behavior>
- 1.2 IF <error condition> THEN THE SYSTEM SHALL <response>
- 1.3 WHILE <state> THE SYSTEM SHALL <behavior>

(repeat REQ-2, REQ-3, ...)

## Edge Cases & Open Questions

  <anything ambiguous>

Rules: every requirement is atomic, testable, and has a stable ID. Cover the happy
path AND error/edge cases (use IF...THEN).

When done: STOP. Show me a summary and ask me to review. Suggest I run
`/spec-analyze` next for complex or sensitive features, otherwise `/spec-design`.
```

### `/spec-analyze` — ตรวจคุณภาพ requirements

`.claude/skills/spec-analyze/SKILL.md`

```markdown
---
name: spec-analyze
description: Audit the requirements.md of the active spec for logical issues before moving to design. Use for complex features or compliance-sensitive domains.
---

# Analyze Requirements

Read the active spec's requirements.md and report issues in FOUR categories:

1. Logical inconsistencies — requirements that contradict each other
2. Ambiguities — statements open to more than one interpretation
3. Conflicting constraints — requirements that cannot all hold at once
4. Gaps — missing scenarios, unhandled edge cases, undefined error behavior

For each issue: cite the REQ ID, explain the problem, propose a concrete fix.
Do NOT silently edit the file — present the analysis and let me decide what to apply.
```

### `/spec-design` — สร้าง design.md

`.claude/skills/spec-design/SKILL.md`

```markdown
---
name: spec-design
description: Generate the design.md artifact from approved requirements. Use after requirements are approved.
---

# Generate design.md

First read the active spec's requirements.md and the project rules
(@.claude/rules/tech.md, @.claude/rules/structure.md). Then write
`.ai/specs/<feature>/design.md`:

# Design: <Feature Name>

## Architecture Overview — components and responsibilities

## Sequence Diagrams — Mermaid for key flows

## Data Models & Interfaces — schemas, types, API contracts

## Technology Decisions — choices + rationale (prefer tech.md)

## Error Handling Strategy — how each error case is handled

## Testing Strategy — unit/integration/property; map to REQ IDs

## Requirement Traceability — table: design element → REQ-x.y it satisfies

For a deeper architectural pass, consider delegating to the `spec-architect`
subagent. When done: STOP for my review, then suggest `/spec-tasks`.
```

### `/spec-tasks` — สร้าง tasks.md

`.claude/skills/spec-tasks/SKILL.md`

```markdown
---
name: spec-tasks
description: Generate the tasks.md implementation checklist from the approved design. Use after design is approved.
---

# Generate tasks.md

Read the active spec's design.md and requirements.md, then write
`.ai/specs/<feature>/tasks.md`. Size tasks for a large-context, high-effort
model: each task is a COHESIVE, INDEPENDENTLY VERIFIABLE slice that you can
implement end-to-end in one pass, even if it spans many files.

# Implementation Tasks: <Feature Name>

> Each task is a cohesive, independently verifiable slice. Implement a whole task
> in one pass (it may touch many files). Decompose into sub-steps yourself at
> execution time — do NOT pre-split tasks here.

- [ ] 1. <Cohesive capability> — <one line: scope + what "done" means>
     Satisfies: REQ-1 (all criteria). Verify: <test / command>.
- [ ] 2. <Cohesive capability> — <scope + done>
     Satisfies: REQ-2. Depends on: 1. Verify: <test / command>.
- [ ] 3. <Cohesive capability> [optional] — <scope + done>
     Satisfies: REQ-3.

Rules:

- Aim for the FEWEST tasks that keep each one independently verifiable. A typical
  feature is ~5-10 tasks, not 20-30. If a "task" can't be verified on its own,
  fold it into the task it serves.
- Each task is ONE coherent behavior / vertical slice (e.g. "user registration
  end-to-end: model → endpoint → validation → tests"), never a horizontal layer
  ("create the model", "create the repository") that does nothing alone.
- Map each task to a whole REQ or a tightly-related group; list the REQ IDs.
- Do NOT write 1.1/1.2 sub-tasks — the implementing model handles micro-sequencing
  internally with its own TODO list.
- Order coarsely: shared/foundational tasks first. Note a dependency only when real.
- Mark [optional] for non-essential tasks.

When done: STOP for my review. Then ask whether to implement a specific task
(`/spec-implement <n>`), a range (`/spec-implement 1-3`), or everything
(`/spec-implement all`).
```

### `/spec-implement` — ลงมือทำ task

`.claude/skills/spec-implement/SKILL.md`

```markdown
---
name: spec-implement
description: Implement one or more cohesive tasks from the active spec's tasks.md, end-to-end with tests, following project conventions.
argument-hint: <task id, range like 1-3, or "all">
---

# Implement task(s): $ARGUMENTS

Resolve $ARGUMENTS to the target task(s): a single id (e.g. 2), a range (1-3), or
all incomplete tasks. For multiple tasks, work in dependency order.

For EACH task:

1. Read the task plus its linked REQ IDs in requirements.md and the relevant parts
   of design.md and @.claude/rules/structure.md.
2. Plan the task with your own internal TODO list, then implement the WHOLE task in
   one cohesive pass. It may span many files — that is expected; keep the entire
   task in context rather than splitting it across turns.
3. Write or extend tests proving it satisfies its REQ IDs.
4. Mark the task "- [x]" in tasks.md and state which REQ IDs are now satisfied.
5. Give me the exact command to verify (test / build / run).

Pause for my confirmation at each TASK boundary (not after every file). When I
asked for a range or "all", continue to the next task after reporting, stopping
early only if a test fails or a requirement turns out to be infeasible.

For unattended runs, do NOT run "all" in one session (context grows per task and a
session cannot /clear itself). Instead drive one cohesive task per fresh session —
implement, then `/spec-retro`, then clear — via `scripts/pane-loop.sh` (see §7).
```

### `/spec-bugfix` และ `/spec-pbt` — ดูข้อ 5 และ 6 (มี subagent ช่วย)

> เพิ่ม `/spec-quick` (รวด 3 เฟสรวดเดียว ไม่มี approval gate) ได้ด้วยรูปแบบเดียวกัน — ส่วน "รันทุก task" ใช้ `/spec-implement all` ได้เลย ไม่ต้องมีคำสั่งแยก

**บันทึก:** ถ้ามี skill กับ command ชื่อซ้ำกัน skill จะมาก่อน และ ถ้ามี skill เยอะจน description โดนตัด ให้เพิ่มค่า SLASH_COMMAND_TOOL_CHAR_BUDGET (ค่าเริ่มต้นคือ 1% ของ context window หรือ fallback 8,000 ตัวอักษร)

---

## 5. Subagents — Custom Agents (เทียบเท่า Kiro Autopilot)

> Kiro มี "advanced agents / Autopilot" — Claude Code มี **subagents** ที่รันในคอนเทกซ์แยก มี tools/permission ของตัวเอง subagent อยู่เป็นไฟล์ markdown ใน .claude/agents/ (ระดับโปรเจกต์) หรือ ~/.claude/agents/ (ส่วนตัว) รูปแบบคล้าย skill: YAML frontmatter + คำสั่งระบบ

**frontmatter:** `name`, `description`, `tools` (จำกัด tool ที่ใช้ได้), `model` · จัดการด้วย `/agents`

### `spec-architect` — ออกแบบสถาปัตยกรรมเชิงลึก

`.claude/agents/spec-architect.md`

```markdown
---
name: spec-architect
description: Senior architect for spec-driven design. Use to produce or stress-test the design.md of a feature against requirements and project constraints.
tools: Read, Grep, Glob, WebSearch
model: opus
---

You are a senior software architect. You work from an approved requirements.md.

When invoked:

1. Read the spec's requirements.md and project rules (tech.md, structure.md).
2. Produce or critique the architecture: components, data flow, interfaces,
   sequence diagrams (Mermaid), error handling, and a testing strategy.
3. Map every design element back to the REQ IDs it satisfies.
4. Flag any requirement that is technically infeasible or expensive, with options.

Return a clear design document. Do not write implementation code.
```

### `bug-investigator` — หา root cause (ใช้กับ `/spec-bugfix`)

`.claude/agents/bug-investigator.md`

```markdown
---
name: bug-investigator
description: Root-cause analysis specialist. Use to investigate a bug and identify its true cause before any fix is proposed.
tools: Read, Grep, Glob, Bash
model: opus
---

You are a debugging specialist. Your ONLY job is root-cause analysis — never fix.

When invoked:

1. Reproduce the reported behavior mentally from the codebase.
2. Trace the actual cause (not the symptom). Cite specific files and lines.
3. Identify behaviors that must NOT change while fixing (regression risks).
4. Report: root cause, affected code paths, and a list of "must-not-break"
   behaviors written as: WHEN <condition> THEN THE SYSTEM SHALL CONTINUE TO <behavior>.

Stop after the analysis. Do not edit any file.
```

`.claude/skills/spec-bugfix/SKILL.md` (เรียก subagent ข้างบน)

```markdown
---
name: spec-bugfix
description: Run a root-cause-first bugfix spec. Use for bugs in critical paths, recurring regressions, or unclear root causes.
argument-hint: <bug description>
---

# Bugfix Spec

Bug: $ARGUMENTS

Phase 1 — Delegate root-cause analysis to the `bug-investigator` subagent.
Present its findings to me and STOP. Wait for me to confirm the root cause.

Phase 2 (after I confirm) — Create `.ai/specs/bugfix-<short>/` with a fix spec
that documents the fix AND captures unchanged behavior:
WHEN <condition> THEN THE SYSTEM SHALL CONTINUE TO <existing behavior>

Phase 3 — Produce tasks plus regression/property tests that validate BOTH
(a) the bug is fixed and (b) the "SHALL CONTINUE TO" behaviors still hold.
```

### `pbt-runner` — ดูข้อ 6

> **ข้อควรระวัง:** subagent มีคอนเทกซ์แยก = main agent มองไม่เห็นรายละเอียดข้างใน ใช้เมื่อ "การแยกคอนเทกซ์มีประโยชน์จริง" เช่น สำรวจ codebase, รีวิวอิสระ, หรือรันงานคู่ขนาน อย่าใช้พร่ำเพรื่อจน main agent reason ภาพรวมไม่ได้

---

## 6. Hooks — Automation ใน `.claude/settings.json`

> Kiro hooks = Claude Code hooks เป๊ะ ๆ hook จะ fire ที่จุดเฉพาะระหว่าง session เมื่อ event เกิดและ matcher ตรง Claude Code จะส่ง JSON context ของ event ให้ handler ทาง stdin ที่สำคัญ Claude Code มี event `TaskCreated`/`TaskCompleted` ที่ตรงกับ Pre/Post Task Execution ของ Kiro

### ตำแหน่งไฟล์ & กฎพื้นฐาน

| ไฟล์                          | ขอบเขต      | commit?         |
| ----------------------------- | ----------- | --------------- |
| `~/.claude/settings.json`     | ทุกโปรเจกต์ | ไม่ (ส่วนตัว)   |
| `.claude/settings.json`       | โปรเจกต์นี้ | ใช่ (แชร์ทีม)   |
| `.claude/settings.local.json` | โปรเจกต์นี้ | ไม่ (gitignore) |

Exit code 2 = block. PreToolUse ที่ exit 2 จะหยุด tool, Stop ที่ exit 2 จะบังคับให้ Claude ทำงานต่อ · ชื่อ tool case-sensitive: "Bash" ใช้ได้ "bash" ไม่ได้ · ดู/ตรวจด้วย `/hooks`

### ชุด hook สำหรับ spec-driven (ก๊อปลง `.claude/settings.json`)

```json
{
  "hooks": {
    "PostToolUse": [
      {
        "matcher": "Edit|Write",
        "hooks": [
          {
            "type": "command",
            "command": "FILE=$(jq -r '.tool_input.file_path // empty'); [ -n \"$FILE\" ] && npx prettier --write \"$FILE\" 2>/dev/null; true"
          }
        ]
      }
    ],
    "Stop": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "<project test command> 2>&1 | tail -20 || echo 'Tests failed — fix before continuing' >&2; exit 0"
          }
        ]
      }
    ],
    "TaskCompleted": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "<project typecheck command> && <project test command> || echo 'Verify the completed task: typecheck/tests not green' >&2"
          }
        ]
      }
    ],
    "PreToolUse": [
      {
        "matcher": "Bash",
        "hooks": [
          {
            "type": "command",
            "if": "Bash(rm *)",
            "command": "C=$(jq -r '.tool_input.command'); echo \"$C\" | grep -qE 'rm -rf' && { echo 'Blocked: destructive command' >&2; exit 2; }; exit 0"
          }
        ]
      }
    ],
    "SessionStart": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "echo \"{\\\"hookSpecificOutput\\\":{\\\"hookEventName\\\":\\\"SessionStart\\\",\\\"additionalContext\\\":\\\"Branch: $(git branch --show-current 2>/dev/null). Active specs: $(ls .ai/specs 2>/dev/null | tr '\\n' ' ')\\\"}}\""
          }
        ]
      }
    ]
  }
}
```

แต่ละ hook ทำอะไร (เทียบ Kiro):

| Event                       | ทำงานเมื่อ          | ทำอะไร                       | = Kiro                    |
| --------------------------- | ------------------- | ---------------------------- | ------------------------- |
| `PostToolUse` (Edit\|Write) | หลังเขียน/แก้ไฟล์   | format ไฟล์ด้วย prettier     | File Save → lint          |
| `Stop`                      | Claude ตอบจบ turn   | รันเทสต์ รายงานถ้าพัง        | Agent Stop → compile/test |
| `TaskCompleted`             | task ถูก mark เสร็จ | typecheck + test ตรวจความถูกต้อง  | Post Task Execution       |
| `PreToolUse` (Bash)         | ก่อนรันคำสั่ง       | บล็อก `rm -rf`               | Pre Tool Use (block)      |
| `SessionStart`              | เปิด session        | inject branch + รายชื่อ spec | โหลด dev context          |

### ทางเลือก: hook แบบ prompt/agent (ใช้ AI ตัดสิน)

นอกจาก `command` แล้ว Claude Code รองรับ hook 5 ชนิด: command, prompt, agent, http, mcp_tool เช่นให้ AI รีวิว edit ที่แตะ auth:

```json
{
  "hooks": {
    "PreToolUse": [
      {
        "matcher": "Edit|Write",
        "hooks": [
          {
            "type": "prompt",
            "prompt": "Review this change: $ARGUMENTS. If it modifies authentication or payment code without a corresponding spec task, respond with a deny decision; otherwise allow."
          }
        ]
      }
    ]
  }
}
```

### Hook ใน frontmatter ของ skill/agent

ผูก hook กับ skill ได้โดยตรง (ทำงานเฉพาะตอน skill นั้น active) เช่น once: true เพื่อรันครั้งเดียวต่อ session — เหมาะกับ setup เฉพาะ workflow

---

## 7. Claude Code CLI — เดินงานจริงตั้งแต่ต้นจนจบ

### คำสั่ง CLI หลักที่ต้องรู้

| คำสั่ง                          | ทำอะไร                                               |
| ------------------------------- | ---------------------------------------------------- |
| `claude`                        | เริ่ม interactive REPL                               |
| `claude "query"`                | เริ่ม REPL พร้อม prompt แรก                          |
| `claude -p "query"`             | รันแบบ headless ผ่าน SDK แล้วออก (ใช้กับ CI/สคริปต์) |
| `cat file \| claude -p "query"` | ประมวลผลเนื้อหาที่ pipe เข้ามา                       |
| `claude -c`                     | ทำงานต่อจากบทสนทนาล่าสุด                             |
| `claude -r "<id>" "query"`      | resume session ตาม ID                                |
| `claude --continue`             | โหลดบทสนทนาล่าสุดใน directory ปัจจุบัน               |
| `claude update`                 | อัปเดตเป็นเวอร์ชันล่าสุด                             |
| `claude mcp`                    | ตั้งค่า MCP server                                   |
| `claude --output-format json`   | output เป็น JSON สำหรับสคริปต์                       |

### ขั้นตอนตั้งค่าโปรเจกต์ (ครั้งเดียว)

```bash
# 1. เข้าโปรเจกต์แล้วเปิด Claude Code
cd your-project
claude

# 2. ใน session: สร้าง CLAUDE.md เริ่มต้น
/init

# 3. ออกมาวางไฟล์ทั้งหมด (CLAUDE.md, .claude/rules/, .claude/skills/, .claude/agents/, .claude/settings.json)
#    ตามข้อ 2-6 — หรือบอก Claude ให้สร้างให้: "สร้าง .claude/skills/spec-requirements/SKILL.md ตามสเปกนี้ ..."

# 4. commit
git add CLAUDE.md .claude/ && git commit -m "chore: add spec-driven workflow"
```

### Flow ทำ feature (interactive)

```text
claude
> /spec-new ระบบล็อกอินด้วย email/password กันการ brute-force
  → Claude แนะนำ Requirements-First แล้วถามคำถาม
> <ตอบคำถาม>
> /spec-requirements
  → ได้ requirements.md (EARS) → review → "approved"
> /spec-analyze            (ถ้าซับซ้อน)
> /spec-design
  → ได้ design.md → review → "approved"
> /spec-tasks
  → ได้ tasks.md เป็น cohesive slice ~5-10 อัน → review → "approved"
> /spec-implement 1        (หรือ `all` เพื่อไล่ทั้งชุดตามลำดับ)
  → ทำ task 1 ทั้งก้อน (หลายไฟล์) + เทสต์, hook auto-format + รันเทสต์ → ยืนยันที่ขอบเขต task → task ถัดไป
> /spec-pbt                (ตรวจความถูกต้องเชิง property)
```

### Flow แบบอัตโนมัติ — pane orchestrator (`scripts/pane-loop.sh`)

ตอนแรกออกแบบเป็น headless (`claude -p` แยกครั้งต่อเฟส) แต่ headless = รันแบบมองไม่เห็น: TUI ไม่โชว์
ความคืบหน้า, project dev server ที่นี่เคยพังเงียบ, และเคยเผลอฆ่า process ที่ยังทำงานอยู่เพราะ pane ว่าง.
ปัจจุบันจึงขับด้วย **iTerm pane จริงที่เห็นได้** — 1 cohesive task = 1 pane interactive สด:

```bash
scripts/pane-loop.sh <feature-name>   # ละ feature ได้ถ้ามี spec เดียว
```

`pane-loop.sh` วนทำ task ค้างใน `tasks.md` (เรียงตามลำดับในไฟล์ = dependency) ทีละตัว:

1. เปิด iTerm split รัน `claude $CLAUDE_FLAGS` (**interactive ไม่ใช่ `-p`**) — เห็น TUI เต็ม
2. พิมพ์ `/spec-implement N` เข้า pane → poll จน task N ขึ้น `- [x]` ใน `tasks.md` (timeout `STEP_TIMEOUT`, ดีฟอลต์ 2400s)
3. พิมพ์ `/spec-retro` → รอจน `git HEAD` เปลี่ยน (retro commit เสมอ → detect แบบไม่ผูก path)
4. พิมพ์ `/clear` + `/exit` ปิด pane → task ถัดไปเปิด pane **ใหม่สด** = context รีเซ็ตโดยธรรมชาติ

- จบ pane = `/clear` โดยธรรมชาติ ไม่ต้องสั่ง `/clear` ตัวเองกลาง session (ซึ่งทำไม่ได้อยู่แล้ว)
- `retro` รันใน pane เดียวกับ implement → เห็นงาน task นั้นครบ (ไม่ต้องพึ่ง `-c`)
- hands-free: `CLAUDE_FLAGS` ดีฟอลต์ `--dangerously-skip-permissions` (สโคปแค่ repo นี้) กัน
  permission prompt ของ npm/build ค้างเงียบ; อยากกด allow เองตั้ง `CLAUDE_FLAGS=""`
- task ไหน implement ไม่จบใน `STEP_TIMEOUT` → ลูปหยุด เปิด pane ค้างไว้ให้ตรวจ (ไม่ลุยต่อบน state ที่พัง)
- เช็กความคืบหน้าที่ **ของจริงบน disk** (`tasks.md` checkbox, `git HEAD`) ไม่ใช่ดู pane ว่าง/ไม่ว่าง

> **ทางเลือก headless ล้วน** (`claude -p "/spec-implement N"`) ยังทำได้และ agentic เต็มรูป (tool/loop/test);
> **Batch API ใช้ไม่ได้** เพราะไม่มี agent loop/tool use. แต่โปรเจกต์นี้เลือก pane ที่เห็นได้เพื่อเฝ้างาน
> และกัน false-negative จาก dev server — ถ้าโปรเจกต์มี UI ให้ verify ใน project target runtime (ดู project UI-verify reference) — ดู `.claude/rules/lessons.md` (headless pane buffers)

> **ปลอดภัยไว้ก่อน:** อย่าใช้ `--dangerously-skip-permissions` โดยไม่มีเหตุผล และอย่าใส่ความลับ (API key) ใน CLAUDE.md หรือ chat — hooks/rules ทั้งหมดอยู่ใน git

---

## 8. ตัวอย่าง end-to-end: ระบบ User Authentication

1. **ตั้งค่า** — `/init` → วาง CLAUDE.md + rules + skills + agents + settings.json → commit
2. **`/spec-new`** "ระบบล็อกอิน email/password, reset password, กัน brute-force"
   → Claude เลือก Requirements-First (greenfield + รู้พฤติกรรม) แล้วถามคำถาม
3. **`/spec-requirements`** → ได้ `.ai/specs/user-authentication/requirements.md`:
   ```
   REQ-1: User Registration
     1.1 WHEN a user submits valid registration data
         THE SYSTEM SHALL create a new account
     1.2 IF the email already exists
         THEN THE SYSTEM SHALL display "Email already registered"
   REQ-2: Brute-Force Protection
     2.1 IF login fails 5 times within 10 minutes for one account
         THEN THE SYSTEM SHALL lock the account for 15 minutes
   ```
   → `/spec-analyze` → แก้ช่องโหว่ → "approved"
4. **`/spec-design`** → `spec-architect` เลือก JWT + bcrypt + rate-limiter, วาด sequence diagram, traceability กลับ REQ → "approved"
5. **`/spec-tasks`** → ได้ ~6 task แบบ vertical slice (เช่น "1. Registration ครบวงจร", "2. Login + session", "3. Brute-force lockout", "4. Password reset", "5. Logout", "6. Email validation") แต่ละอัน map กับ REQ และทดสอบได้ในตัว → "approved"
6. **`/spec-implement 1`** (หรือ `all`) → ทำทั้ง task เป็นก้อนเดียว (model → endpoint → validation → tests, หลายไฟล์) → hook `PostToolUse` format อัตโนมัติ → hook `Stop` รันเทสต์ → ยืนยันที่ขอบเขต task → task ถัดไป
7. **`/spec-pbt`** → ดึง property "for any email already in the system, registration SHALL be rejected" → เขียน property-based test → เจอ counter-example → ถามว่าจะแก้ตรงไหน

---

## 9. Property-Based Testing (`/spec-pbt`)

`.claude/skills/spec-pbt/SKILL.md`

```markdown
---
name: spec-pbt
description: Extract testable properties from requirements and write property-based tests. Use to validate correctness across the whole input space, not just examples.
---

# Property-Based Testing

Step 1 — From the active spec's requirements.md, extract PROPERTIES: universal
statements that must hold for ALL valid inputs. Express each as:
"For any <inputs> where <precondition>, THE SYSTEM SHALL <invariant>"
Link each to its REQ ID and note the input space / generators needed. Present the
list and let me choose which to test.

Step 2 — For the chosen properties, write property-based tests using the project's
framework (fast-check / Hypothesis / jqwik / proptest). Generate wide input ranges
including edge cases (empty, max, special characters). Each test cites its REQ ID.

Step 3 — When a test finds a counter-example, report the minimal failing ("shrunk")
input, then ask whether to fix the implementation, the test, or the requirement.

For heavy generation/execution, consider delegating to the `pbt-runner` subagent.
```

`.claude/agents/pbt-runner.md`

```markdown
---
name: pbt-runner
description: Property-based testing specialist. Use to author and run property-based tests and triage counter-examples in an isolated context.
tools: Read, Grep, Glob, Bash, Edit, Write
model: sonnet
---

You author and run property-based tests from EARS-derived properties.
Generate wide input spaces, run the suite, and when a property fails, report the
shrunk counter-example and the candidate fixes (implementation / test / spec).
Do not change requirements without surfacing it for approval.
```

---

## 10. Token & Context Discipline (ประหยัด token โดยไม่ลดคุณภาพ)

> หัวใจ: ประหยัดด้วยการตัด **noise** ออกจาก context — ไม่ใช่ตัด **ความสามารถ** ห้ามลด effort หรือหด context window เพื่อประหยัดเงิน เพราะนั่นคือการลดคุณภาพ และเพราะ spec อยู่ในไฟล์ (`.ai/specs/`) อยู่แล้ว การ `/clear` หรือ compact จึงทำได้แบบ "ไม่สูญข้อมูล" ถ้ายึดหลักเดียว: **เซฟลงไฟล์ก่อน แล้วค่อยล้าง**

### หลักกำกับ (อ่านก่อนทุกอย่าง)

1. **Correctness > token เสมอ** — ถ้าการประหยัดเสี่ยงทำให้ผลลัพธ์ผิด อย่าประหยัด แล้วบอกผู้ใช้แทน
2. **ไฟล์ spec = ความทรงจำถาวร, บทสนทนา = ความจำชั่วคราว** — สิ่งที่ต้องไม่หาย (decision + เหตุผล, task ที่ทำอยู่, อะไรเสร็จแล้ว, ขั้นถัดไป) ต้องอยู่ใน `requirements/design/tasks.md` ไม่ใช่อยู่แค่ในแชต
3. **Context สะอาด = ทั้งถูกและแม่นขึ้น** — คุณภาพของโมเดลตกเมื่อ context เต็ม ฉะนั้น "การล้าง noise" ส่วนใหญ่ทำให้ประหยัดและแม่นไปพร้อมกัน จุดที่สวนทางกันมีแค่ไม่กี่จุด (ระดับ 🟡/🔴 ด้านล่าง) จัดการด้วย guardrail

### สามระดับของการประหยัด

| ระดับ                   | คืออะไร                   | เงื่อนไข                 |
| ----------------------- | ------------------------- | ------------------------ |
| 🟢 ทำได้เลย             | ประหยัด **และ** แม่นขึ้น  | ไม่ต้องระวังเป็นพิเศษ    |
| 🟡 ทำได้แต่มี guardrail | ประหยัดแต่เสี่ยงข้อมูลหาย | ต้องทำตามเงื่อนไขกันพลาด |
| 🔴 ห้ามแลกกับ token     | จะทำให้ AI พลาด           | ห้ามทำเด็ดขาด            |

**🟢 ทำได้เลย**

- `/clear` ระหว่างงานคนละเรื่อง หรือเมื่อจบ task หนึ่ง — รีเซ็ต context ทั้งหมด (เซฟ spec ไว้แล้ว resume ทีหลังได้)
- **1 cohesive task = 1 session** ทำ task ให้จบเป็นก้อน → `/clear` → task ถัดไป โหลดบริบทกลับด้วย `@.ai/specs/<feature>/`
- ใช้ subagents (`spec-architect`, `bug-investigator`, `pbt-runner`) กับงานที่ต้องอ่านไฟล์เยอะ — มันทำในคอนเทกซ์แยกแล้วส่งกลับแค่สรุป main context จึงสะอาด
- ใช้ rules แบบ `paths:` (fileMatch) แทน `always` เท่าที่ปลอดภัย — โหลดเฉพาะตอนแตะไฟล์ที่เกี่ยว
- ดู `/context` ว่าอะไรกินที่ + เฝ้า % ใน status line · ใช้ `/btw` ถามคำถามแทรกที่ไม่อยากให้เข้า context
- ใช้ CLI (`gh`, `aws`, …) แทน MCP ที่ schema ใหญ่ — กิน context น้อยกว่า
- **1 task = 1 pane/session สด** ผ่าน `scripts/pane-loop.sh` (ดู §7) — จบ pane = context รีเซ็ตโดยธรรมชาติ ไม่แบกประวัติ task ก่อนหน้า → ตัด cache read ที่สะสม; แต่ละ session `@` อ่าน spec จากไฟล์เอง ราคา **standard ปกติ (ไม่ลด)** ยัง agentic เต็มรูป (tool/loop/test). headless `claude -p` แยกครั้งให้ผลแบบเดียวกันแต่มองไม่เห็นงาน

**🟡 ทำได้แต่ต้องมี guardrail**

- **Compaction** — ตั้ง instruction ให้เก็บ state สำคัญไว้ก่อน (ดู block ด้านล่าง) และ **ห้าม compact กลาง task ที่ยังไม่เสร็จ**
- **ตัด CLAUDE.md ให้สั้น** — ตัดได้เฉพาะสิ่งที่ "ลบแล้วไม่ทำให้พลาด" เท่านั้น กฎที่กันความผิดพลาด (รวม EARS และ approval gates) ต้องอยู่ต่อ
- **ย้ายเนื้อหาไป skill** — ย้ายได้เฉพาะของที่ใช้เป็นครั้งคราว (คู่มือเฉพาะกิจ) ไม่ใช่กฎหลักของ workflow เช่น EARS reference ฉบับเต็มเก็บไว้ใน skill `/spec-requirements` ได้ (โหลดตอนสร้าง requirements พอดี) โดยคง EARS ฉบับย่อไว้ใน CLAUDE.md
- **Batch API** — ลด **50%** ทั้ง input/output (ซ้อน prompt caching ได้ ~90%) แต่เป็น endpoint แยก (`/v1/messages/batches`), async (poll เอง, ภายใน 24 ชม.), **ไม่มี agent loop = ไม่มี tool use** และต้องเป็น **API key** (แผน Pro/Max ใช้ไม่ได้) → **รัน `/spec-implement` ไม่ได้** (มันต้องอ่าน/เขียนไฟล์ + รันเทสต์ + วนแก้). คุ้มเฉพาะงาน bulk N prompt อิสระที่ไม่ใช้ tool และรอได้ เช่น ร่าง requirements/design หลาย feature พร้อมกัน, triage บั๊กจำนวนมาก, สกัด property list จาก requirements หลายไฟล์

**🔴 ห้ามแลกกับ token เด็ดขาด**

- ห้าม `/clear` หรือ `/compact` กลาง task ที่ยังไม่เสร็จและ state อยู่แค่ในแชต — **เขียนลง tasks.md/design.md ก่อนเสมอ**
- ห้ามลด **effort** หรือหด **context window 1M** เพื่อประหยัดเงิน — นั่นคือการลดความสามารถ ไม่ใช่ตัด noise
- ห้ามลบกฎหรือบริบทที่กันความผิดพลาดออกจาก CLAUDE.md/rules
- ห้ามข้ามการเขียน spec หรือ test ก่อนล้าง context
- ห้ามใช้ subagent จน main agent มองภาพรวมไม่ออก — ใช้เฉพาะงาน explore / review / งานคู่ขนานที่การแยกคอนเทกซ์มีประโยชน์จริง

### Block ที่ควรเพิ่มใน CLAUDE.md

วางต่อท้าย `CLAUDE.md` เพื่อบังคับให้การล้าง/บีบอัด context ไม่ทำข้อมูลสำคัญหาย:

```markdown
## Context discipline (save tokens WITHOUT losing correctness)

- The spec files in `.ai/specs/<feature>/` are the durable source of truth;
  this conversation is temporary working memory. Before I run /clear, or before
  compaction triggers, make sure the current state — active task ID, decisions and
  their rationale, what's done, and the next step — is written into tasks.md /
  design.md. NEVER clear or compact in the middle of an unfinished task whose state
  lives only in this conversation.
- When compaction runs, ALWAYS preserve: the active spec and task ID, the list of
  modified files, the exact test/build/run commands, and every architectural
  decision with its rationale. Do not drop these even to save space.
- Prefer a fresh session per cohesive task (reload context by reading the spec with
  @) over one long session. A clean, focused context is also more accurate.
- Keep this file lean, but NEVER remove a rule that prevents a real mistake.
  Correctness outranks token savings: if economizing would risk a wrong result,
  do not economize — tell me instead.
```

> เสริมได้ด้วย hook `PreCompact` (ดูข้อ 6) เพื่อสำรอง state ก่อน compaction — แต่ instruction ใน CLAUDE.md ข้างบนคือกลไกหลักที่ความเสี่ยงต่ำสุดและไม่มี dependency

### เลือกอย่างไร: clear / compact / ปล่อยให้สะสม

| สถานการณ์                                          | ทำ                                                                        |
| -------------------------------------------------- | ------------------------------------------------------------------------- |
| งานใหม่ ไม่เกี่ยวกับของเดิม                        | `/clear` (เซฟ spec ก่อน)                                                  |
| ยังอยู่ในงานเดิม แต่ context รก/ใกล้เต็ม (~80-85%) | `/compact <โฟกัส>` พร้อมเก็บ state                                        |
| กำลังเจาะปัญหาซับซ้อนเดียว ประวัติมีค่า            | **ปล่อยให้สะสม** — บางครั้งควรเก็บประวัติไว้                              |
| แก้ผิดซ้ำเกิน 2 ครั้งในเรื่องเดิม                  | `/clear` แล้วเริ่มใหม่ด้วย prompt ที่ดีกว่า (context เต็มไปด้วยทางที่ผิด) |

> กฎง่าย ๆ: **ถ้ายังไม่แน่ใจว่าล้างแล้วจะเสียอะไรไหม แปลว่ายังไม่ควรล้าง** — เซฟลงไฟล์ให้ครบก่อน

---

## 11. Session Retrospective (เมื่อจบ session)

> ทำเมื่อ "งานใน session จบ" และต้องทำ **ก่อน `/clear` หรือ `/compact` เสมอ** เพราะ retrospective ต้องสะท้อนจากประวัติ session ที่ยังอยู่ใน context — ถ้าล้างไปก่อนจะเขียนไม่ได้ มองอีกมุม นี่คือ external memory อีกชั้นที่เก็บ "บทเรียน" ลงไฟล์เพื่อให้ session ถัดไปดีขึ้น สอดคล้องกับหลัก "อยู่ในไฟล์ ไม่ใช่ในแชต" ของข้อ 10

### ลำดับและการ trigger (ผูกกับข้อ 10)

- เป็น skill แบบ **manual** (`disable-model-invocation: true`) — ผู้ใช้พิมพ์ `/spec-retro` เอง ไม่ให้โมเดล trigger เอง
- รัน **ก่อน** ล้าง/บีบอัด context เสมอ (ขณะ context ยังเต็ม)
- ผลเป็นไฟล์ใน `retrospectives/YYYY-MM/DD/` → commit → session หน้าค่อยอ่านเฉพาะที่ต้องใช้ด้วย `@`
- เสริมได้ด้วย hook `SessionEnd`/`Stop` ที่แค่ **เตือน** ว่า "ยังไม่ได้ทำ retrospective" แต่ **ห้าม auto-run ตัว retrospective จาก SessionEnd** เพราะตอนนั้น context ใกล้หมด/หมดแล้ว — ตัว generate ต้องรันตอนประวัติยังอยู่

### ⚠️ จุดที่ปรับจาก template เดิม เพื่อไม่ให้ขัดข้อ 10

template ดั้งเดิมสั่ง "append บทเรียนเข้า CLAUDE.md ทุก session" — แต่ข้อ 10 ระบุว่า CLAUDE.md โหลดทุก session และต้อง lean ไม่งั้นโมเดลจะเพิกเฉยกฎ (= ทำให้ AI พลาด) จึงเปลี่ยน step 3 เป็น:

- บทเรียนฉบับเต็มอยู่ใน retrospective archive อยู่แล้ว (ไม่หาย)
- promote เฉพาะบทเรียนที่ "ใช้ซ้ำได้จริงและกันพลาด" เข้า `.claude/rules/lessons.md` แบบ **คัดแล้วและตัดของเก่าที่ไม่เกี่ยวออก** ไม่ใช่ต่อท้ายไม่รู้จบ
- ถ้า `lessons.md` เริ่มยาว ให้แปลงเป็น rule แบบ `paths:` หรือ skill โหลด on-demand เพื่อไม่ให้กิน context ทุก session

### Skill: `/spec-retro` — สรุป session ก่อนล้าง context

`.claude/skills/spec-retro/SKILL.md` คือ source of truth ของ template (ยาว — ไม่ paste ซ้ำที่นี่
กัน drift). สรุปสิ่งที่เวอร์ชันปัจจุบันทำ:

1. **เก็บข้อมูล session** — `git diff --name-only` / `git log` + timestamp GMT+7 และ **cost จริง
   ของ session** จาก ledger (authoritative — field `.cost.total_cost_usd` ของ statusline payload
   ของ Claude Code เอง): `cat ~/.claude/cost-sessions/$CLAUDE_CODE_SESSION_ID.json` แล้ว breakdown
   ต่อ model/cache-tier ด้วย `python3 scripts/session-cost.py --breakdown-only "$CLAUDE_CODE_SESSION_ID"`
   (token นับจาก transcript, cost **ปันส่วน** จาก total ของ ledger — ไม่ recompute เอง เพราะ
   recompute แล้วเพี้ยนเสมอ; subscription ไม่คิดเงินจริงต่อ token)
2. **เขียนไฟล์** `retrospectives/YYYY-MM/DD/HH.MM_<scope-slug>.md` — **scope-slug บังคับ** (เช่น
   `task4-header-nav`) ไม่งั้นแยก session จากชื่อไฟล์ไม่ออก. เนื้อหา **เป็นภาษาไทยทั้งหมด** (คง EN
   เฉพาะ code/path/command/error/technical term) และ **ห้าม emoji** — ใช้ป้ายข้อความในวงเล็บแทน
   (เช่น `[สมมติ → เรียนรู้]`, `[ไม่เวิร์ก]`), อนุญาตลูกศร `→`. template บังคับครบทุก section:
   Session Cost, AI Diary (≥150 คำ), What Went Well/Improve, Honest Feedback (≥100 คำ),
   Co-Creation Map (5 แถวตายตัว), Intent vs Interpretation + adversarial check, Communication
   Dynamics, Seeds Planted, Teaching Moments, Lessons Learned, Next Steps, และ Pre-Save
   Validation (HARD STOP ถ้ากรอกช่องไม่ครบ)
3. **Promote บทเรียน (token-safe — ดู §10)** — ห้าม append เข้า CLAUDE.md; ใส่เฉพาะบทเรียนที่
   "ใช้ซ้ำได้จริงและกันพลาด" เข้า `.claude/rules/lessons.md` แบบคัด+ตัดของเก่า
4. **Commit** — `git add retrospectives/ .claude/rules/lessons.md && git commit -m "docs: session retrospective ..."`

frontmatter: `disable-model-invocation: true` (manual เท่านั้น ผู้ใช้พิมพ์ `/spec-retro` เอง),
`allowed-tools: Bash, Read, Write, Glob`. (อยากใช้ชื่อ `/rrr` ตามดีไซน์เดิม ตั้งชื่อโฟลเดอร์เป็น `rrr` ได้)

> **Cost ledger** ที่ retro อ่าน มาจาก subsystem ใน `scripts/`: `cost_lib.py` (core,
> `session_breakdown`/`render_breakdown`), `session-cost.py` (standalone), `inject-cost.py` /
> `backfill-cost.sh` (เขียน ledger ต่อ session ผ่าน statusline). ledger **ต้องมีก่อน session เริ่ม** —
> session ปิดแล้ว resume จะ reset cost=0 กู้ไม่ได้. อย่าคูณ token จาก transcript เป็น cost (overcount
> 1.6–3.7x) — ดู `.claude/rules/lessons.md`

---

## สรุปการแปลง

ถ้าจำได้แค่ไม่กี่อย่าง:

- **CLAUDE.md** = พฤติกรรม spec-driven + กฎ EARS + ลิงก์ไป rules (อ่านทุก session)
- **`.claude/rules/`** = Steering (ไม่มี `paths:` = always, มี `paths:` = fileMatch)
- **`.claude/skills/spec-*`** = เฟสของ workflow เรียกด้วย `/spec-*` หรือ Claude เรียกเอง
- **`.claude/agents/`** = ผู้เชี่ยวชาญเฉพาะทาง (architect, debugger, PBT) ในคอนเทกซ์แยก
- **`.claude/settings.json`** = hooks (`TaskCompleted`/`Stop`/`PostToolUse` = Pre/Post Task & lint/test ของ Kiro)
- **CLI** = `claude` ทำงาน interactive, `claude -p` headless, `-c`/`-r` ทำงานต่อ — ทุกอย่าง commit ขึ้น git แชร์ทั้งทีมได้
- **Token discipline** = ตัด noise ไม่ตัดความสามารถ · 1 task = 1 session แล้ว `/clear` โดยมี spec ในไฟล์เป็นความทรงจำถาวร · correctness สำคัญกว่า token เสมอ — ห้ามล้าง/บีบ context กลาง task ที่ state ยังอยู่แค่ในแชต
- **Session Retrospective** = `/spec-retro` รัน **ก่อน `/clear`** (ตอนประวัติยังอยู่) เก็บลง `retrospectives/` · บทเรียนที่ใช้ซ้ำได้ promote เข้า `.claude/rules/lessons.md` แบบคัด+ตัด ไม่ยัดเข้า CLAUDE.md (กัน bloat ตามข้อ 10)

---

## อ้างอิงเอกสารทางการ

- Claude Code overview — https://docs.claude.com/en/docs/claude-code/overview
- Memory / CLAUDE.md — https://code.claude.com/docs/en/memory
- Skills — https://code.claude.com/docs/en/skills
- Subagents — https://code.claude.com/docs/en/sub-agents
- Hooks reference — https://code.claude.com/docs/en/hooks
- Hooks guide — https://code.claude.com/docs/en/hooks-guide
- CLI reference — https://docs.anthropic.com/en/docs/claude-code/cli-usage
- Kiro (ต้นแบบ workflow) — https://kiro.dev/docs
