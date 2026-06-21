# 2. Automation — pane-loop

รันหลาย task ของ spec แบบ hands-free โดยขับ **interactive Claude TUI จริง** ทีละ task
(1 task = 1 pane iTerm ใหม่). controller พิมพ์ `/spec-implement` -> `/spec-retro` ->
`/clear` ให้เอง แล้วเปิด pane ถัดไป รัน sequential (แชร์ git tree).

> คู่มือเต็ม + ทุก flag/กับดัก: [`../scripts/pane-loop.md`](../scripts/pane-loop.md)
> ที่นี่คือสรุปใช้งานเร็ว.

## 2.1 ใช้งานเร็ว

```bash
scripts/pane-loop.sh                          # auto: ทุก task ที่ยัง [ ] (ต้องมี spec เดียว)
scripts/pane-loop.sh <feature-name>           # ระบุ feature, ทุก task ค้าง
scripts/pane-loop.sh <feature-name> 10 11     # เฉพาะ task 10,11 (sort + skip ที่ [x] ให้)
```

โหมดเลือก task (arg หลัง feature):

- **ต้องใส่ feature ก่อนเสมอ** เมื่อระบุ task id (`$1` = feature)
- auto-sort เลขน้อย->มาก + ตัดซ้ำ
- ข้าม id ที่เป็น `[x]` แล้ว หรือไม่พบ พร้อมเตือน

## 2.2 env ที่ใช้บ่อย

| ตัวแปร         | default                          | ใช้เมื่อ                                    |
| -------------- | -------------------------------- | ------------------------------------------- |
| `CLAUDE_FLAGS` | `--dangerously-skip-permissions` | ตั้ง `""` ถ้าอยากกด approve เองในแต่ละ pane |
| `STEP_TIMEOUT` | `2400`                           | เพิ่มถ้า task ใหญ่/เครื่องช้า               |

```bash
STEP_TIMEOUT=3600 scripts/pane-loop.sh <feature-name> 10
CLAUDE_FLAGS="" scripts/pane-loop.sh <feature-name> 8
```

## 2.3 loop ทำอะไร (ย่อ)

เปิด pane -> รอ TUI บูต -> พิมพ์ `/spec-implement N` -> รอ `- [x] N.` (timeout `STEP_TIMEOUT`)
-> `/spec-retro` -> รอ git HEAD เปลี่ยน (retro commit) -> `/clear` + `/exit` -> ปิด pane -> task ถัดไป.

## 2.4 ข้อควรระวังตอนรัน

- อย่าพิมพ์แข่งใน pane ที่ controller คุม
- อย่าแก้ `tasks.md` มือระหว่างรัน (ใช้ `[x]` เป็นสัญญาณจบ)
- pane ค้าง -> ปล่อย `STEP_TIMEOUT` ครบ script หยุดเอง เปิด pane ไว้ให้ตรวจ
- หยุดทั้ง loop -> kill controller; pane ที่เปิดอยู่ปิดเอง ไม่ -> ปิดมือ
- รันต่อหลังหยุด -> เรียก script ใหม่ (โหมด auto หยิบเฉพาะ `[ ]`)

## 2.5 ข้อจำกัด

- macOS + iTerm2 เท่านั้น (AppleScript), sequential ล้วน ห้ามรันขนาน
- ระบุ task id ต้องมาคู่ feature (auto-detect feature พร้อมส่ง id ไม่ได้)
- ลำดับ dependency เป็นความรับผิดชอบผู้สั่ง (sort เลขให้ แต่ไม่รู้ dependency จริง)

## 2.6 เกี่ยวข้อง

- บทเรียน automation: `../.claude/rules/lessons.md` (headless buffer, prod-build verify,
  resume-pane `cd` trap, rtk proxy ดู raw log)
- cost ของ session ที่ loop สร้าง: ดู [03-cost-and-retro.md](03-cost-and-retro.md)
- อีกครึ่งของ automation = ชั้น **hooks** (guardrail แบบ deterministic ที่ block/warn รอบ tool
  call): ดู [05-hooks.md](05-hooks.md)
