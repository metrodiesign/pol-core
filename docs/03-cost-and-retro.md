# 3. Cost Ledger + Retrospective

## 3.1 cost จริงมาจากไหน

cost ต่อ session/task ที่เชื่อถือได้ดึงจาก **`.cost.total_cost_usd`** (payload ของ statusLine
ที่ Claude Code ส่งให้) เท่านั้น — เก็บผ่าน ledger ที่ `statusline.sh` เขียนต่อ session.

- ledger อยู่ที่ `~/.claude/cost-sessions/<session-id>.json`
- skill อ่านเองได้ผ่าน env `$CLAUDE_CODE_SESSION_ID`
- **ห้าม** คูณ token จาก transcript เพื่อประมาณ cost: overcount 1.6-3.7x แม้ dedup แล้ว
  (เคสจริง task4 transcript $19.80 vs จริง $5.36)
- subscription ไม่คิดเงินจริง -> $ เป็น estimate; headless `/cost` โชว์แค่ "subscription"

> ledger ต้องมี **ก่อน** session เริ่ม. session ที่ปิดแล้ว resume จะ reset cost=0 กู้ไม่ได้
> (ใช้ `backfill-cost.sh` เพื่อ resume สั้น ๆ ให้ statusline เขียน ledger ของ session ที่ปิดไป).

## 3.2 breakdown ละเอียด (per-model / cache tier)

ถ้าต้องการ breakdown ที่มีแต่ใน transcript:

- อ่าน **token จริง** ได้ แต่ **dedup ด้วย `message.id` ไม่ใช่ `uuid`** (msg เดียวกันโผล่หลาย
  บรรทัดคนละ uuid = ต้นเหตุ overcount)
- **อย่า recompute cost ต่อชั้นตรง ๆ** (reconcile เพี้ยน -40%..+20%, บาง session ไม่มี usage)
  -> **ปันส่วน** total ที่ authoritative ตามสัดส่วน token x rate แทน (sum = total เป๊ะ, ติดป้าย
  "ประมาณ")
- insight แบบ ratio (cache-read ถูกกว่า input ~10x) robust แม้ rate absolute เพี้ยน

## 3.3 สคริปต์ cost

| สคริปต์                    | หน้าที่                                                                                               |
| -------------------------- | ----------------------------------------------------------------------------------------------------- |
| `scripts/cost_lib.py`      | helper กลาง: `ledger_for`, `session_breakdown`, `render_breakdown`, `task_costs` (แก้ที่เดียว)        |
| `scripts/session-cost.py`  | พิมพ์ cost ละเอียด (per-model + cache tier) ของ 1 session: `session-cost.py [sid] [--breakdown-only]` |
| `scripts/cost-summary.py`  | สรุป cost จริงต่อ task -> ตาราง markdown ที่ `retrospectives/cost-<feature>.md`                       |
| `scripts/inject-cost.py`   | ฉีด cost จริงเข้าท้ายไฟล์ retrospective ต่อ task (idempotent, มี marker แล้วข้าม)                     |
| `scripts/backfill-cost.sh` | populate ledger ของ session ที่ปิดแล้ว โดย resume สั้น ๆ ใน pane                                      |

## 3.4 Retrospective

- รัน `/spec-retro` **ตอนจบ session ก่อน `/clear`**
- เขียนไฟล์ที่ `retrospectives/YYYY-MM/DD/HH.MM_<scope-slug>.md`
- ภาษาไทยบังคับ, ไม่มี emoji, ครบทุก section ตาม template (AI Diary, Honest Feedback,
  Communication Dynamics, Co-Creation Map, Pre-Save Validation)
- ดึง session cost จาก ledger (`~/.claude/cost-sessions/$CLAUDE_CODE_SESSION_ID.json`) +
  breakdown จาก `session-cost.py --breakdown-only`
- commit `retrospectives/` + `lessons.md` ด้วยกัน

## 3.5 Promote บทเรียน

- บทเรียนที่ **reusable + กันความผิดพลาดจริง** เท่านั้น -> เพิ่มใน `../.claude/rules/lessons.md`
  (ไม่ใช่ CLAUDE.md)
- บันทึกเต็มอยู่ใน `retrospectives/` — `lessons.md` เก็บแค่ที่ตกผลึกแล้ว, prune ของเก่าที่ stale
- รูปแบบ: `**Pattern**: ... — **Why**: ...`

## 3.6 ใน automation loop

pane-loop เรียก `/spec-retro` ให้อัตโนมัติหลังแต่ละ task -> ledger ของ pane session นั้นถูกเขียน
ระหว่างทาง, retro commit เป็นสัญญาณให้ loop ไป task ถัดไป (ดู [02-automation.md](02-automation.md) §2.3).
