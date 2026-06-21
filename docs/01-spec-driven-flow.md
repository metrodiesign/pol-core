# 1. Spec-Driven Flow + Gates

โปรเจกต์นี้ทำ **strict spec-driven development**: spec มาก่อนโค้ดเสมอ ห้ามกระโดดไป implement
ฟีเจอร์ที่ไม่ trivial. ต้นทาง: `../CLAUDE.md`.

## 1.1 สาม artifact + approval gate

ทุกฟีเจอร์ไหลผ่าน 3 ไฟล์ใต้ `.ai/specs/<feature>/` ตามลำดับ มี **gate หยุดให้ review หลังทุกขั้น**:

```
requirements.md   WHAT — พฤติกรรมระบบ (EARS notation)
      |  (review -> "approved"/"continue")
design.md         HOW — สถาปัตยกรรม
      |  (review)
tasks.md          discrete implementation steps
      |  (review)
implement         ลงมือทีละ task
```

หลังสร้างแต่ละ artifact -> **STOP** ขอ review ก่อนไปขั้นถัดไป. ข้อยกเว้นเดียว: `/spec-quick`
(รันทุก phase ไม่มี gate — ระบุใน CLAUDE.md).

แต่ละ artifact มี header `> Status: draft|approved <YYYY-MM-DD>` — gate = flip `draft` ->
`approved` เมื่อได้รับอนุมัติ. Status นี้เป็นสัญญาณที่ `spec-edit-guard` hook และ `/spec-tasks`
ใช้ตัดสิน (ดู [05-hooks.md](05-hooks.md)).

## 1.2 EARS notation (บังคับใน requirements)

ทุก functional requirement เขียนด้วยแพตเทิร์นใดแพตเทิร์นหนึ่ง พร้อม id เสถียร (`REQ-1.2`):

| แพตเทิร์น    | รูปแบบ                                             |
| ------------ | -------------------------------------------------- |
| ubiquitous   | THE SYSTEM SHALL `<behavior>`                      |
| event-driven | WHEN `<trigger>` THE SYSTEM SHALL `<behavior>`     |
| state-driven | WHILE `<state>` THE SYSTEM SHALL `<behavior>`      |
| optional     | WHERE `<feature>` THE SYSTEM SHALL `<behavior>`    |
| error        | IF `<unwanted>` THEN THE SYSTEM SHALL `<response>` |

requirement ต้อง atomic, ไม่กำกวม, ทดสอบได้.

## 1.3 การ size task (โมเดล context ใหญ่)

- task = สไลซ์พฤติกรรมที่ **cohesive + verify เองได้** ไม่ใช่ micro-step
- 1 ฟีเจอร์ทั่วไป ~5-10 task ไม่ใช่ 20-30
- **ห้าม** pre-split เป็น 1.1/1.2 ใน tasks.md — โมเดลแตกขั้นย่อยเองตอน execute ด้วย TODO ภายใน
- เน้น vertical slice (model -> API -> validation -> tests) ไม่ใช่ horizontal layer ที่ใช้เดี่ยวไม่ได้

## 1.4 Slash command ทั้งหมด

| คำสั่ง                             | หน้าที่                                        |
| ---------------------------------- | ---------------------------------------------- |
| `/spec-new <idea>`                 | เลือก workflow + ถามคำถามชี้แจง                |
| `/spec-requirements`               | สร้าง `requirements.md` (EARS)                 |
| `/spec-analyze`                    | ตรวจ requirements หา gap/conflict ก่อน design  |
| `/spec-design`                     | สร้าง `design.md`                              |
| `/spec-tasks`                      | สร้าง `tasks.md`                               |
| `/spec-implement <id\|range\|all>` | ลงมือ task เดียว/ช่วง/ทั้งหมด end-to-end       |
| `/spec-bugfix <bug>`               | workflow แก้บั๊กแบบ root-cause-first           |
| `/spec-pbt`                        | สกัด property + เขียน property-based test      |
| `/spec-retro`                      | retrospective — รันตอนจบ session ก่อน `/clear` |

นิยามจริงอยู่ `../.claude/skills/spec-*/SKILL.md`.

> `spec-architect` เป็น **agent** (ไม่ใช่ slash command) — fresh-context **adversarial reviewer**
> (default mode = critique). `/spec-design` delegate ให้ critique `design.md` ก่อน STOP;
> `/spec-analyze` audit `requirements.md`. produce mode (เขียน architecture จริง) เฉพาะ
> design-first ที่ขอ explicit. นิยาม: `../.claude/agents/spec-architect.md`.

## 1.5 working agreements สำคัญ

- spec sync กัน: เปลี่ยน requirements -> propagate ไป design + tasks
- implement ทั้ง task (อาจหลายไฟล์) รวม test -> mark `- [x]` + ระบุ REQ id ที่ปิด -> pause ที่
  task boundary (ไม่ใช่หลังทุกไฟล์). ใน **edit เดียวกัน** กับที่ flip `[x]` ต้อง append
  `Evidence:` block ใต้ task: test command + result, UI verify (ถ้าโปรเจกต์ ship UI: verify ใน
  target runtime ของโปรเจกต์ ดู project UI-verify reference; ไม่ใช่ -> `n/a — logic-only`),
  deviations. `task-gate` hook บังคับ green + มี Evidence ก่อนยอมให้ `[x]` ผ่าน — โดย green
  พิสูจน์ผ่าน `.ai/bin/gate-task.sh` ที่อ่าน `SDD_TYPECHECK_CMD` / `SDD_TEST_CMD`
  (auto-detect package.json script สำหรับโปรเจกต์ Node) (ดู [05-hooks.md](05-hooks.md))
- ก่อน design STOP: delegate critique ให้ `spec-architect` (adversarial reviewer) — apply หรือ
  rebut ทุก finding ก่อนขอ approve
- /spec-analyze ก่อน design คุ้มเสมอสำหรับฟีเจอร์ที่มี logic (จับ conflict ที่ทำให้ test เขียนไม่ได้)
- pure-logic-first: แยก logic ทดสอบได้ (สูตร/validation) เป็น pure function วางใน project test
  directory ที่ co-located กับ logic ที่ทดสอบ เขียน unit test เขียวก่อนแตะ UI

## 1.6 context discipline

- spec files = source of truth ถาวร; conversation = working memory ชั่วคราว
- ก่อน `/clear` หรือ compaction: เขียน active task id + decision + rationale + next step ลง
  tasks.md/design.md — **ห้าม clear/compact กลาง task ที่ state อยู่แต่ในแชต**. `precompact-persist`
  (PreCompact hook) inject เตือน persist state อัตโนมัติก่อน compact — แต่ best-effort เท่านั้น
  โมเดลยังเป็นคนเขียน (ดู [05-hooks.md](05-hooks.md))
- prefer fresh session ต่อ task (reload ด้วย `@` อ่าน spec) ดีกว่า session ยาว

## 1.7 ตัวอย่าง spec ของจริง

- วงจรเดิมรองรับทั้งฟีเจอร์ใหม่และรอบ enhancement: เพิ่ม task ผ่านลำดับเดิม
  (requirements -> design -> tasks -> implement) เสมอ
- browse `.ai/specs/<feature>/` เพื่อดูตัวอย่าง spec ของโปรเจกต์เอง (demo spec ถูกถอดออกแล้ว)
