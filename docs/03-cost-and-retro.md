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

> รายละเอียดเต็ม (ข้อห้าม recompute, การปันส่วน, ข้อจำกัด ledger, cost ของ multi-session
> pane-loop) อยู่ที่ `../.claude/skills/spec-retro/references/cost-accounting.md` — โหลดตอน
> retro เท่านั้น. ที่นี่คือฉบับย่อ ห้ามให้ขัดกัน.

## 3.3 สคริปต์ cost

| สคริปต์                    | หน้าที่                                                                                               |
| -------------------------- | ----------------------------------------------------------------------------------------------------- |
| `scripts/cost_lib.py`      | helper กลาง: `ledger_for`, `session_breakdown`, `render_breakdown`, `task_costs` (แก้ที่เดียว)        |
| `scripts/session-cost.py`  | พิมพ์ cost ละเอียด (per-model + cache tier) ของ 1 session: `session-cost.py [sid] [--breakdown-only]` |
| `scripts/cost-summary.py`  | สรุป cost จริงต่อ task -> ตาราง markdown ที่ `retrospectives/cost-<feature>.md`                       |
| `scripts/inject-cost.py`   | ฉีด cost จริงเข้าท้ายไฟล์ retrospective ต่อ task (idempotent, มี marker แล้วข้าม)                     |
| `scripts/backfill-cost.sh` | populate ledger ของ session ที่ปิดแล้ว โดย resume สั้น ๆ ใน pane                                      |

## 3.4 Retrospective

- รัน `/spec-retro` **ตอนจบ session ก่อน `/clear`** — ข้ามทั้งหมด (ไม่มีไฟล์ ไม่มี commit)
  ถ้า session นี้ไม่ได้แก้ไฟล์ **และ** ไม่มีบทเรียนใหม่ที่ durable; อย่าปั้น retro เปล่า
- เขียนไฟล์ที่ `retrospectives/YYYY-MM/DD/HH.MM_<scope-slug>.md` (`<scope-slug>` บังคับ —
  ชื่อ `HH.MM_retrospective.md` เปล่า ๆ แยกไม่ออกจากไฟล์อื่น)
- ภาษาไทยบังคับ, ไม่มี emoji
- template = **5 section เท่านั้น**: Session Cost, Summary, Files Changed, Lessons Learned,
  Next Steps. section พิธีกรรม (AI Diary, Co-Creation Map, Communication Dynamics, Seeds,
  Teaching Moments) **ถูกถอดออกโดยตั้งใจ** เพื่อลด output cost (เป้า ~6-9k token ไม่ใช่ 20k)
  — อย่าเติมกลับ (ดู `../.claude/skills/spec-retro/SKILL.md` ที่เป็นต้นฉบับ template)
- ดึง session cost จาก ledger (`~/.claude/cost-sessions/$CLAUDE_CODE_SESSION_ID.json`) +
  breakdown จาก `session-cost.py --breakdown-only` (แปะ markdown ที่มันพิมพ์ verbatim)
- **Steering sync ก่อน commit**: เทียบ ground truth กับ steering canon — dependency จริงของ
  project vs `.ai/shared/CODING_STANDARDS.md`, ไฟล์ใหม่ใน source tree vs
  `.ai/shared/ARCHITECTURE.md`, `paths:` glob ของ stub ใน `.claude/rules/` vs path จริง —
  แก้ drift ใน commit เดียวกัน
- commit ให้ครอบทุก dir ที่ขั้น promote/sync แตะ:
  `git add retrospectives/ .ai/shared/ .claude/rules/ .claude/skills/spec-implement/references/ .claude/skills/spec-retro/references/`
  (`.ai/shared/` ต้องติดไปด้วยเสมอ — บทเรียนที่ promote ลงที่นั่น ไม่ใช่ stub ใน `.claude/rules/`)

## 3.5 Promote บทเรียน

- บทเรียนที่ **reusable + กันความผิดพลาดจริง** เท่านั้น -> เพิ่มใน `../.ai/shared/LESSONS.md`
  (ไม่ใช่ CLAUDE.md; `.claude/rules/lessons.md` เป็นแค่ stub ชี้มาที่ไฟล์นี้ — อย่าเขียนลง stub)
- route ตาม scope: universal (process / workflow / git / CC tooling) ->
  `.ai/shared/LESSONS.md`; stack-specific implementation pattern ->
  `.ai/shared/stack/<stack>.md`; browser-verify / probe recipe ->
  `.claude/skills/spec-implement/references/browser-verify.md`; กลไก cost accounting ->
  `.claude/skills/spec-retro/references/cost-accounting.md`
- บันทึกเต็มอยู่ใน `retrospectives/` — `LESSONS.md` เก็บแค่ที่ตกผลึกแล้ว, prune ของเก่าที่ stale
- รูปแบบ: `**Pattern**: ... — **Why**: ...`

## 3.6 ใน automation loop

pane-loop เรียก `/spec-retro` ให้อัตโนมัติหลังแต่ละ task -> ledger ของ pane session นั้นถูกเขียน
ระหว่างทาง, retro commit เป็นสัญญาณให้ loop ไป task ถัดไป (ดู [02-automation.md](02-automation.md) §2.3).
