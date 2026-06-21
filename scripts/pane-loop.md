# คู่มือ pane-loop.sh

ตัวรันอัตโนมัติของ spec-driven workflow ที่ขับ **interactive Claude TUI จริง** (ไม่ใช่ headless
`claude -p`) ทีละ task: 1 task = 1 pane iTerm ใหม่ที่เปิด `claude` แบบโต้ตอบ แล้ว controller
พิมพ์คำสั่งเข้าไปให้เอง (`/spec-implement` -> `/spec-retro` -> `/clear` -> ปิด pane) ก่อนเปิด
pane ถัดไป รันแบบ sequential (ทุก task แชร์ git tree เดียวกัน).

---

## 1. ทำไมเป็น interactive pane ไม่ใช่ headless

- เห็นงานจริงใน TUI ตามเวลาจริง ตรวจ/แทรกได้ (ตรงกับ working preference)
- headless `claude -p` buffer output จน job จบ -> pane ว่าง/CPU ต่ำ แยกไม่ออกว่า "ค้าง" หรือ
  "กำลังทำ"; และ `--permission-mode acceptEdits` ครอบแค่ Edit ไม่ครอบ Bash -> setup/scaffold
  ติด permission เงียบ
- pane จริง + `--dangerously-skip-permissions` (ดู §4) -> hands-free ไม่มี prompt คั่น

---

## 2. ข้อกำหนดเบื้องต้น (prerequisites)

- macOS + **iTerm2** (ใช้ AppleScript/`osascript` คุม session; Terminal.app ใช้ไม่ได้)
- `claude` CLI อยู่ใน PATH (เปิดด้วย `/bin/zsh -lc` -> อ่าน profile ปกติ)
- รันจากที่ไหนก็ได้ (script `cd` เข้า repo เอง โดยอิงตำแหน่งไฟล์)
- มี spec อย่างน้อย 1 อันใน `.ai/specs/<feature>/tasks.md` ที่มี checkbox `- [ ] N.`
- iTerm2 ควรเป็นหน้าต่าง active (split จะเกิดใน current window/current session)

---

## 3. วิธีใช้ (usage)

```bash
scripts/pane-loop.sh [feature-name] [task-id ...]
```

### โหมดที่ 1 — รันทุก task ที่ยังค้าง (auto)

```bash
scripts/pane-loop.sh                    # auto-detect feature (ต้องมี spec เดียวเท่านั้น)
scripts/pane-loop.sh <feature-name> # ระบุ feature ตรง ๆ
```

- ไม่ส่ง task id -> เลือก **ทุก task ที่เป็น `- [ ]`** ใน `tasks.md` เรียงตามลำดับในไฟล์
- ถ้าไม่ส่ง feature และมี spec มากกว่า 1 อัน -> script หยุดและบอกให้ระบุชื่อ

### โหมดที่ 2 — รันเฉพาะบาง task (เลือกเอง)

```bash
scripts/pane-loop.sh <feature-name> 11       # เฉพาะ task 11
scripts/pane-loop.sh <feature-name> 10 11    # task 10 แล้ว 11
scripts/pane-loop.sh <feature-name> 11 10    # ยัง sort เป็น 10 -> 11 ให้อัตโนมัติ
```

กฎของโหมด 2:

- **ต้องใส่ feature เป็น argument แรกเสมอ** เมื่อจะระบุ task id (`$1` ถูกตีเป็น feature เสมอ)
  -> `scripts/pane-loop.sh 11` ผิด (จะหา feature ชื่อ `11`); ต้องเป็น
  `scripts/pane-loop.sh <feature-name> 11`
- **(a) auto-sort + dedup**: id ที่ส่งมาถูกเรียงเลขน้อย -> มาก และตัดซ้ำ กัน dependency พลาด
- **(b) guard**: ถ้า id ที่ส่งมาเป็น `- [x]` อยู่แล้ว -> ข้าม + เตือน (กัน implement งานที่เสร็จแล้วซ้ำ);
  ถ้า id ไม่พบ/ไม่ใช่ pending -> ข้าม + เตือน
- ถ้าหลัง guard ไม่เหลือ task ที่รันได้เลย -> ออกเงียบ ๆ (`ไม่มี task ที่จะรัน`) ไม่เปิด pane

---

## 4. ตัวแปรสภาพแวดล้อม (env)

| ตัวแปร         | ค่า default                      | ความหมาย                                                                                               |
| -------------- | -------------------------------- | ------------------------------------------------------------------------------------------------------ |
| `CLAUDE_FLAGS` | `--dangerously-skip-permissions` | flag ที่ส่งให้ `claude` ใน pane. ตั้ง `CLAUDE_FLAGS=""` ถ้าต้องการกด approve เครื่องมือเองในแต่ละ pane |
| `STEP_TIMEOUT` | `2400` (วินาที)                  | เวลาสูงสุดที่รอ 1 task ขึ้น `[x]` ก่อนถือว่า fail                                                      |

ตัวอย่าง:

```bash
STEP_TIMEOUT=3600 scripts/pane-loop.sh <feature-name> 10     # ให้เวลามากขึ้น
CLAUDE_FLAGS="" scripts/pane-loop.sh <feature-name> 11       # approve เองในแต่ละ pane
```

> ความปลอดภัย: `--dangerously-skip-permissions` ปิด prompt ทั้งหมด (รวม Bash) -> ขอบเขตจำกัด
> แค่ไดเรกทอรี repo นี้. ถ้าไม่มั่นใจกับ task ใด ตั้ง `CLAUDE_FLAGS=""` แล้วคุมเอง.

---

## 5. ทำงานทีละขั้น (loop ต่อ task)

สำหรับแต่ละ id ที่เลือก:

1. เปิด pane ใหม่ (`split vertically`) รัน `cd <repo> && claude $CLAUDE_FLAGS`
2. `sleep 12` รอ Claude TUI บูต
3. จำ git HEAD ปัจจุบัน (`head_before`) ไว้ตรวจ retro commit ทีหลัง
4. พิมพ์ `/spec-implement N` เข้า pane
5. poll ทุก 5s รอจน `tasks.md` มี `- [x] N.` (timeout = `STEP_TIMEOUT`)
   - ถ้าครบ timeout แล้วยังไม่ `[x]` -> **หยุดทั้ง loop**, เปิด pane นั้นทิ้งไว้ให้ตรวจ, exit 1
6. พิมพ์ `/spec-retro` เข้า pane
7. poll รอ git HEAD เปลี่ยนจาก `head_before` (timeout 360s) = retro ถูก commit แล้ว
   (ถ้า timeout -> เตือนแล้วไปต่อ ไม่หยุด)
8. `sleep 6` ให้ agent จบ turn -> พิมพ์ `/clear` -> `sleep 3` -> `/exit` -> ปิด pane
9. ไป id ถัดไป

จบทุก id -> พิมพ์ `เสร็จทุก task ค้างของ <feature>`

---

## 6. การควบคุม/แทรกระหว่างรัน

- **ดูเฉย ๆ**: ปล่อยให้ controller พิมพ์เอง — อย่าพิมพ์แข่งใน pane ที่มันคุมอยู่ (ชนกับ text ที่ส่ง)
- **อย่าแก้ `tasks.md` มือระหว่างรัน**: script ใช้ `- [x] N.` เป็นสัญญาณจบ task — แก้มั่วทำให้
  loop เข้าใจผิด
- **หยุดทั้ง loop**: kill process ของ controller (ถ้ารันผ่าน background ของ Claude Code ให้สั่งหยุด
  จาก session หลัก). pane ที่เปิดอยู่จะไม่ปิดเอง -> ตรวจ/ปิดมือได้
- **รันต่อหลังหยุด**: เรียก script ใหม่ — โหมด 1 จะหยิบเฉพาะ task ที่ยัง `- [ ]` ให้อัตโนมัติ
- **pane ค้าง**: ปล่อยให้ `STEP_TIMEOUT` ครบ -> script หยุดเอง เปิด pane ไว้ให้ดูสาเหตุ

---

## 7. ข้อจำกัด / กับดักที่รู้แล้ว

- ผูกกับ iTerm2 + macOS เท่านั้น (ใช้ AppleScript)
- sequential ล้วน — ออกแบบให้ task แชร์ tree เดียว ห้ามรันขนาน (จะชนไฟล์)
- โหมด 2: ระบุ task id ต้องมาคู่กับ feature เสมอ (ดู §3) — auto-detect feature พร้อมส่ง id ไม่ได้
- ลำดับ dependency เป็นความรับผิดชอบของผู้สั่ง: sort เป็นเลขน้อย->มากให้ แต่ถ้า dependency จริง
  ไม่ตรงกับลำดับเลข ต้องส่ง id ให้ถูกชุด/ถูกลำดับเอง
- ตรวจ retro ด้วย "git HEAD เปลี่ยน" — ถ้า retro ไม่ได้ commit (เช่น skill เปลี่ยนพฤติกรรม)
  จะ timeout 360s แล้วไปต่อ (ไม่ถือว่า error)
- `sleep` ที่ตั้งไว้ (12/6/3/2s) เป็นค่าโดยประมาณสำหรับบูต/จบ turn — เครื่องช้าอาจต้องเพิ่ม

---

## 8. ตัวอย่างครบวงจร

```bash
# รันทุก task ค้างของ spec เดียวในโปรเจกต์
scripts/pane-loop.sh

# รันรอบ enhancement เฉพาะ task 10 และ 11 ให้เวลาต่อ task 1 ชม.
STEP_TIMEOUT=3600 scripts/pane-loop.sh <feature-name> 10 11

# รัน task เดียว โดยกด approve เครื่องมือเอง (ไม่ skip permission)
CLAUDE_FLAGS="" scripts/pane-loop.sh <feature-name> 8
```

---

## 9. ที่เกี่ยวข้อง

- รัฐธรรมนูญ workflow: `CLAUDE.md` (approval gate ต่อ artifact, task sizing)
- รายละเอียด spec-driven บน Claude Code: `claude-code-spec-driven-workflow.md`
- skill ที่ถูกพิมพ์เข้า pane: `.claude/skills/spec-implement/`, `.claude/skills/spec-retro/`
- บทเรียนสะสม: `.claude/rules/lessons.md` (มีเคส headless buffer, prod-build verify ฯลฯ)
