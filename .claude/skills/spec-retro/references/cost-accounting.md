# Cost accounting — กติกาและข้อห้าม

> โหลดเฉพาะตอน retro (spec-retro อ้างไฟล์นี้) — ย้ายมาจาก lessons.md เพื่อไม่แบก
> รายละเอียดนี้ใน prefix ทุก turn. kernel ที่ต้องรู้เสมออยู่ใน lessons.md บรรทัดเดียว:
> cost จริงอ่านจาก ledger เท่านั้น ห้าม recompute.

## แหล่งความจริง

- per-task/session cost จริงดึงจาก `.cost.total_cost_usd` (payload statusLine ของ
  Claude Code) เท่านั้น — เก็บผ่าน ledger ที่ `statusline.sh` เขียนต่อ session
  (`~/.claude/cost-sessions/<session-id>.json`), skill อ่านเองผ่าน env
  `$CLAUDE_CODE_SESSION_ID`
- ledger ต้องมีก่อน session เริ่ม — session ปิดแล้ว resume จะ reset cost=0 กู้ไม่ได้
- subscription ไม่คิดเงินจริงเป็น $ — ตัวเลขเป็น estimate; headless `/cost` โชว์แค่
  "subscription"

## ข้อห้าม recompute

- อย่าคูณ token จาก transcript: overcount 1.6-3.7x แม้ dedup แล้ว
  (พิสูจน์: task4 transcript $19.80 vs จริง $5.36)
- อยาก breakdown ละเอียด (per-model / cache tier) ซึ่งมีแต่ใน transcript: อ่าน
  **token จริง** ได้ แต่ **dedup ด้วย `message.id` ไม่ใช่ `uuid`** — message เดียวกัน
  โผล่หลายบรรทัด คนละ uuid = ต้นเหตุ overcount
- cost ต่อชั้นอย่า recompute ตรงๆ (พิสูจน์ซ้ำ: reconcile completed session เพี้ยน
  -40%..+20%, บาง session transcript ไม่มี usage = 0) — ให้**ปันส่วน** total ที่
  authoritative ตามสัดส่วน token×rate แทน (sum = total เป๊ะ, ติดป้าย "ประมาณ")
- insight แบบ ratio (cache-read ถูกกว่า input ~10x) robust แม้ rate absolute เพี้ยน

## Multi-session run

- cost ของ "multi run" (pane-loop หลาย session) = **sum ของ ledger แต่ละ impl
  session** (คนละ `$CLAUDE_CODE_SESSION_ID`) — ไม่ใช่ ledger ของ session ที่สั่ง
  pane-loop
- ตัวเลขวัดจริง (ตัวอย่างเชิงวัดผล): 7-session $22.44 vs all-in-one 1-session $16.16
  (multi แพงกว่า ~39%); ต้นทุนจริงของการแยก = re-acquire shared context ทุก session
  (เช่น task5 interactive ที่ wire lib+data+primitives = $6.21 เพราะแบก context
  กลับเข้ามาใหม่) — kernel การตัดสินใจ all-in-one vs แยก pane อยู่ที่
  `.claude/commands/pane-loop.md` และ spec-tasks/SKILL.md (Suggested execution batches)

## Helper

- `scripts/cost_lib.py` — `session_breakdown` / `render_breakdown`
- `scripts/session-cost.py <sid>` — standalone; `--breakdown-only` ใช้ใน retro template
