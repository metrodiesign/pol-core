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

**approval อยู่ในไฟล์ ไม่ใช่ในบทสนทนา** — แต่ละ artifact มี header
`> Status: draft|approved <YYYY-MM-DD>`; gate = flip `draft` -> `approved` **ใน turn เดียวกัน**
กับที่ได้รับอนุมัติ (บทสนทนาเป็น working memory ชั่วคราว artifact คือบันทึกถาวร). phase ถัดไป
ที่เจอ `> Status: draft` ต้อง **เตือนและถามยืนยันก่อนไปต่อ — ห้ามเดาว่า approve แล้ว**.
Status นี้เป็นสัญญาณที่ `spec-edit-guard` hook และ `/spec-tasks` ใช้ตัดสิน (ดู [05-hooks.md](05-hooks.md)).

การเปลี่ยน requirements **propagate ลงไป design + tasks** เสมอ. ถ้า artifact ปลายน้ำขัดกับต้นน้ำ
ให้แก้ปลายน้ำ; ถ้าต้นน้ำดูผิด ให้ **STOP แล้วถาม**.

## 1.2 EARS notation (บังคับใน requirements)

ทุก functional requirement เขียนด้วยแพตเทิร์นใดแพตเทิร์นหนึ่ง พร้อม id เสถียร (`REQ-1.2`):

| แพตเทิร์น    | รูปแบบ                                             |
| ------------ | -------------------------------------------------- |
| ubiquitous   | THE SYSTEM SHALL `<behavior>`                      |
| event-driven | WHEN `<trigger>` THE SYSTEM SHALL `<behavior>`     |
| state-driven | WHILE `<state>` THE SYSTEM SHALL `<behavior>`      |
| optional     | WHERE `<feature>` THE SYSTEM SHALL `<behavior>`    |
| error        | IF `<unwanted>` THEN THE SYSTEM SHALL `<response>` |

requirement ต้อง **atomic** (1 พฤติกรรมสังเกตได้ต่อ 1 criterion — เจอ "and" ให้แยก id),
**ไม่กำกวม** (อ่านได้ทางเดียว; คำอัตวิสัยอย่าง "เร็ว"/"ใช้ง่าย" ต้องมี threshold วัดได้),
**ทดสอบได้** (map ไป test ที่ pass/fail ชัด — ถ้าอธิบาย test ไม่ได้ แปลว่ายังเขียนไม่ดีพอ),
**ครบ** (happy path + error/edge — ทุก error condition ใช้ `IF ... THEN`) และ **traceable**
(ทุก REQ-ID ถูกอ้างโดย design element + `Satisfies:` ของ task + test อย่างน้อย 1 ตัว;
REQ ที่ไม่มีอะไร cover ตอนจบ implement = blocker).

id เสถียร: ตั้งแล้วห้าม renumber (design/tasks/test อ้างอยู่). bugfix spec ใช้ `F-<n>`
(พฤติกรรมที่แก้) + `B-<n>` (พฤติกรรมเดิมที่ต้องไม่พัง) ด้วยหลักเดียวกัน.
ต้นทางเต็ม: `../.ai/shared/EARS.md`.

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
| `/spec-quick <idea>`               | รันทุก phase (requirements -> design -> tasks -> implement) **ไม่มี gate** — เฉพาะฟีเจอร์เล็กที่เข้าใจดีแล้ว |
| `/spec-sync-github <feature>`      | mirror tasks ขึ้น GitHub เป็น Epic + sub-issue แบบ idempotent (คง REQ spine) |
| `/spec-retro`                      | retrospective — รันตอนจบ session ก่อน `/clear` |

นิยามจริงอยู่ `../.claude/skills/spec-*/SKILL.md` (11 skill).

> `spec-architect` เป็น **agent** (ไม่ใช่ slash command) — fresh-context **adversarial reviewer**
> (default mode = critique). `/spec-design` delegate ให้ critique `design.md` ก่อน STOP;
> `/spec-analyze` audit `requirements.md`. produce mode (เขียน architecture จริง) เฉพาะ
> design-first ที่ขอ explicit. persona ตัวจริง (vendor-neutral) อยู่ `../.ai/roles/spec-architect.md`;
> `../.claude/agents/spec-architect.md` เป็น wrapper บาง ๆ ที่กำหนดแค่ tools/model แล้ว delegate
> ไป role นั้น. เช่นเดียวกับ `bug-investigator` / `pbt-runner`.

## 1.5 working agreements สำคัญ

- implement ทั้ง task (อาจหลายไฟล์) รวม test -> mark `- [x]` + ระบุ REQ id ที่ปิด -> pause ที่
  task boundary (ไม่ใช่หลังทุกไฟล์). ใน **edit เดียวกัน** กับที่ flip `[x]` ต้อง append
  `Evidence:` block ใต้ task: test command + result **ที่รันจริงและเห็นจริง** (ไม่ใช่ check ที่วางแผนไว้),
  UI verify (ถ้าโปรเจกต์ ship UI: verify ใน target runtime ของโปรเจกต์ ดู project UI-verify
  reference; ไม่ใช่ -> `n/a — logic-only`), deviations. `task-gate` hook บังคับ green + มี Evidence
  **ต่อ task** (Evidence ของ task อื่นใช้แทนไม่ได้) ก่อนยอมให้ `[x]` ผ่าน — โดย green พิสูจน์ผ่าน
  `.ai/bin/gate-task.sh` ที่อ่าน `SDD_TYPECHECK_CMD` / `SDD_TEST_CMD` (auto-detect package.json
  script เฉพาะโปรเจกต์ Node). repo นี้เป็น .NET ไม่มี package.json — **ไม่ประกาศ env สองตัวนี้
  = ข้าม code-green เหลือแค่ Evidence gate** ฉะนั้นตั้งเองก่อนรัน
  (`SDD_TYPECHECK_CMD="dotnet build -warnaserror"`, `SDD_TEST_CMD="dotnet test"`)
  (ดู [05-hooks.md](05-hooks.md))
- implement หลาย task รวดเดียวทำได้ **เฉพาะเมื่อสั่ง explicit** (ระบุช่วง หรือ "all") โดยไล่ตาม
  dependency order และหยุดกลางทางเมื่อ test แดงหรือ requirement ทำไม่ได้จริง
- ก่อน design STOP: delegate critique ให้ `spec-architect` (adversarial reviewer) — apply หรือ
  rebut ทุก finding ก่อนขอ approve
- /spec-analyze ก่อน design คุ้มเสมอสำหรับฟีเจอร์ที่มี logic (จับ conflict ที่ทำให้ test เขียนไม่ได้)
- pure-logic-first: แยก logic ทดสอบได้ (สูตร/validation) เป็น pure function วางใน project test
  directory ที่ co-located กับ logic ที่ทดสอบ เขียน unit test เขียวก่อนแตะ UI

**Definition of Done** — task จบก็ต่อเมื่อครบ **ทุกข้อ** (canon: `../.ai/shared/TASK_PROTOCOL.md`):

1. implement ครบทั้ง task end-to-end แตะทุกไฟล์ที่ต้องแตะ
2. test พิสูจน์ทุก REQ-ID / F-ID / B-ID ที่อ้าง และ test ผ่าน
3. task สุดท้าย (หรือ task assemble): ทุก REQ trace ไปถึงโค้ด/test ที่ปิดมันได้ — REQ ที่อยู่ใน
   `requirements.md` แต่ไม่มี task ไหนรับ = **blocker ที่ต้อง surface ไม่ใช่ข้ามเงียบ**
4. checkbox เป็น `- [x]` พร้อม `Evidence:` ที่บันทึกคำสั่งที่รันจริง + ผลที่เห็นจริง
5. ผ่าน enforcement floor: typecheck + test + lint เขียว, ไม่มี secret, เคารพกฎ branch/push
6. summary ระบุ ID ที่ปิด, ไฟล์ที่แก้, และ deviation พร้อมเหตุผล

**ข้อห้าม**: ห้าม "ปรับปรุง"/reformat โค้ดที่ไม่เกี่ยว (ทุกบรรทัดที่เปลี่ยนต้อง trace กลับไปที่ task
ได้); ห้ามคิด requirement/scope เพิ่มเอง (scope ที่ขาดให้ surface แล้วขออนุมัติ); ห้ามข้าม test,
ห้าม commit `.only`/`.skip`, ห้ามอ้างว่าผ่านทั้งที่ไม่ได้เห็นผลจริง; ห้าม summary กำกวม
("done", "fixed", "น่าจะได้"); ห้าม commit/push ถ้าไม่ได้สั่ง และห้าม push `main`/`develop` ตรง
(ดู [04-git-pr-and-rules.md](04-git-pr-and-rules.md)).

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
