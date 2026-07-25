# 2. Automation — pane-loop

รันหลาย task ของ spec แบบ hands-free โดยขับ **interactive Claude TUI จริง**. controller
พิมพ์ `/spec-implement` -> `/spec-retro` -> `/clear` ให้เอง แล้วเปิด pane ถัดไป รัน sequential
(แชร์ git tree).

หน่วยของการรันคือ **group = 1 pane/session** ไม่ใช่ task เดี่ยวเสมอไป: 1 group อาจมีหลาย task
(implement ไล่ทีละตัว แล้ว `/spec-retro` + `/clear` **ครั้งเดียว** ตอนจบกลุ่ม). เลือกโหมดตาม
**coupling** ของ feature — ดู [2.1a](#21a-เลือกโหมด-all-in-one-vs-แยก-pane).

> คู่มือเต็ม + ทุก flag/กับดัก: [`../scripts/pane-loop.md`](../scripts/pane-loop.md)
> ที่นี่คือสรุปใช้งานเร็ว. เรียกผ่าน slash command ได้: `/pane-loop <feature> [all-in-one | ids...]`
> (นิยาม: `../.claude/commands/pane-loop.md` — มี pre-flight check ว่า `tasks.md` ต้อง
> `> Status: approved` ก่อนเปิด pane).

## 2.1 ใช้งานเร็ว

```bash
scripts/pane-loop.sh                            # auto: ทุก task ที่ยัง [ ] (ต้องมี spec เดียว)
scripts/pane-loop.sh <feature-name>             # ทุก task ค้าง, auto-group ตาม `Batch:` tag ใน tasks.md
scripts/pane-loop.sh <feature-name> all-in-one  # ทุก task ค้าง รวมใน 1 pane/session เดียว
scripts/pane-loop.sh <feature-name> 10 11       # task 10 และ 11 — คนละ pane (fresh session ต่อ task)
scripts/pane-loop.sh <feature-name> 1 2+3 4+5   # 3 pane: [1], [2,3], [4,5] — '+' = batch ใน session เดียว
```

โหมดเลือก task (arg หลัง feature):

- **ต้องใส่ feature ก่อนเสมอ** เมื่อระบุ task id (`$1` = feature)
- id คั่นด้วย **ช่องว่าง** = คนละ group/pane; คั่นด้วย **`+`** = group เดียวกัน (1 retro/1 clear)
- **ไม่มี sort** — รันตามลำดับ arg ที่ให้มาเป๊ะ ๆ (dependency order เป็นความรับผิดชอบผู้สั่ง)
- ข้าม id ที่เป็น `[x]` แล้ว หรือไม่ pending/ไม่พบ พร้อมเตือน
- ไม่ใส่ id เลย = ทุก task ค้าง auto-group ตาม `Batch:` tag ใน `tasks.md` (tag เดียวกัน = pane เดียว,
  ไม่มี tag = อันละ pane); manual arg ทับ `Batch:` tag เสมอ

### 2.1a เลือกโหมด: all-in-one vs แยก pane

**default ที่แนะนำสำหรับ feature ที่ task พึ่งกัน (shared primitives/data/lib) = `all-in-one`**
— ทุก pending task อยู่ใน pane/session เดียว. เหตุผลตรง ๆ จาก `../.claude/commands/pane-loop.md`:

> เลือกโหมดตาม coupling: feature ที่ task พึ่งกัน (shared primitives/data/lib) -> `all-in-one`
> (ทุก task ใน session เดียว) เป็น default — ถูกกว่า ~30-40% เพราะไม่ต้อง re-acquire context
> ต่อ session. แยก pane ต่อ task เฉพาะงานอิสระจริง หรือต้อง isolate CORE domain เพื่อ accuracy.

เหตุที่ถูกกว่า: context ถูก acquire ครั้งเดียวแล้วใช้ซ้ำผ่าน **cache-read (~10x ถูกกว่า input เต็ม)**
แทนที่จะจ่าย cold context ใหม่ทุก session — session แยกกันไม่แชร์ cache.

| โหมด | เมื่อไหร่ | แลกกับ |
| ---- | -------- | ------ |
| `all-in-one` | task coupled หนัก + งานไม่ใหญ่จนล้น context | context ยาวขึ้น อาจ drift (accuracy) |
| `a+b` batch | slice ที่ coupled แต่แยกเรื่องได้ (logic+data, assemble+audit) | ทางสายกลาง |
| 1 task = 1 pane | task อิสระจริง หรือต้อง isolate CORE domain | จ่าย cold context ซ้ำทุก session |

ตัวเลขวัดจริง: แยก 7 session **$22.44** vs all-in-one 1 session **$16.16** — multi แพงกว่า ~39%.
ต้นทุนจริงของการแยกคือการ re-acquire shared context ทุก session. ที่มา:
`../.claude/skills/spec-retro/references/cost-accounting.md` (หัวข้อ Multi-session run — พร้อมกฎว่า
cost ของ multi run = **sum ของ ledger แต่ละ impl session** ไม่ใช่ ledger ของ session ที่สั่ง
pane-loop) และ `../.ai/shared/CONTEXT_MANAGEMENT.md` (หัวข้อ token-efficiency rules — "multi-session
ไม่ใช่ cost win สำหรับ feature ที่ coupled", วัดได้ ~30-40%).

> **การแยก session ไม่ใช่การประหยัด** — เป็น trade-off ด้าน accuracy ที่ตั้งใจจ่าย.
> แยกเมื่อ task อิสระจริง หรือจงใจกัน long-context drift ออกจาก core domain เท่านั้น.

## 2.2 env ที่ใช้บ่อย

| ตัวแปร         | default | ใช้เมื่อ                                          |
| -------------- | ------- | ------------------------------------------------- |
| `CLAUDE_FLAGS` | ว่าง    | เพิ่ม flag ให้ `claude` ที่เปิดในแต่ละ pane เอง   |
| `STEP_TIMEOUT` | `2400`  | เพิ่มถ้า task ใหญ่/เครื่องช้า (วินาที ต่อ 1 task) |

`claude` **ไม่รับ** `--dangerously-skip-permissions` (นั่นเป็น flag ของ Claude Code) — permission
คุมผ่าน project config/policy แทน. หมายเหตุ: `.claude/settings.json` ที่ commit ไว้ตอนนี้มีแต่
`hooks` **ไม่ได้ตั้ง `defaultMode`** ฉะนั้น pane จะยังถาม approve เว้นแต่ตั้ง permission mode
ไว้ใน config ฝั่งเครื่องเอง.

```bash
STEP_TIMEOUT=3600 scripts/pane-loop.sh <feature-name> 10
```

## 2.3 loop ทำอะไร (ย่อ)

ต่อ 1 group: เปิด pane -> รอ TUI บูต 12s -> พิมพ์ `/spec-implement N` -> รอ `- [x] N.` ใน
`tasks.md` (timeout `STEP_TIMEOUT`) -> ทำซ้ำจนครบทุก id ในกลุ่ม -> `/spec-retro` **ครั้งเดียว**
-> รอ git HEAD เปลี่ยน (retro commit, timeout 360s แล้วไปต่อ ไม่ใช่ error) -> `/clear` + `/exit`
-> ปิด pane -> group ถัดไป.

## 2.4 ข้อควรระวังตอนรัน

- อย่าพิมพ์แข่งใน pane ที่ controller คุม
- อย่าแก้ `tasks.md` มือระหว่างรัน (ใช้ `[x]` เป็นสัญญาณจบ)
- pane ค้าง -> ปล่อย `STEP_TIMEOUT` ครบ script หยุดเอง เปิด pane ไว้ให้ตรวจ
- หยุดทั้ง loop -> kill controller; pane ที่เปิดอยู่ปิดเอง ไม่ -> ปิดมือ
- รันต่อหลังหยุด -> เรียก script ใหม่ (โหมด auto หยิบเฉพาะ `[ ]`)

## 2.5 ข้อจำกัด

- macOS + iTerm2 เท่านั้น (AppleScript ใช้ `current session of current window` — ต้องเปิด iTerm2 ค้างไว้),
  sequential ล้วน ห้ามรันขนาน
- ระบุ task id ต้องมาคู่ feature (auto-detect feature พร้อมส่ง id ไม่ได้)
- task id เป็น **space-separated เท่านั้น** (`12 13 14`) — ไม่รองรับ range (`12-15`)
- ลำดับ dependency เป็นความรับผิดชอบผู้สั่ง — script รันตามลำดับ arg ที่ให้มา ไม่ sort ไม่รู้ dependency
- `tasks.md` ต้อง `> Status: approved` ก่อนเปิด pane — pane ที่เปิดแล้วไม่มีคนตอบคำถามยืนยัน
  จะค้างจน timeout (slash command `/pane-loop` เช็คให้ก่อน)

## 2.6 เกี่ยวข้อง

- บทเรียน automation: `../.ai/shared/LESSONS.md` (headless buffer, prod-build verify,
  resume-pane `cd` trap, rtk proxy ดู raw log)
- cost ของ session ที่ loop สร้าง + วิธีนับให้ถูก: ดู [03-cost-and-retro.md](03-cost-and-retro.md)
  และ `../.claude/skills/spec-retro/references/cost-accounting.md`
- เหตุผลเชิง context/cache ที่ทำให้ all-in-one ถูกกว่า: `../.ai/shared/CONTEXT_MANAGEMENT.md`
- อีกครึ่งของ automation = ชั้น **hooks** (guardrail แบบ deterministic ที่ block/warn รอบ tool
  call): ดู [05-hooks.md](05-hooks.md)
