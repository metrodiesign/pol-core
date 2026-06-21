> Canonical source for ALL agents (Claude loads via .claude/rules stub; Codex/OpenCode/Pi read directly).
> แก้ที่นี่ที่เดียว — single source of truth.

# Technology Stack

> Stack-neutral. This framework does not assume a language, runtime, UI framework, or test
> runner. The rules below are universal; concrete stack picks come from the project itself.

## Languages & Runtimes

- ใช้ภาษาและ runtime ที่โปรเจกต์ตั้งไว้แล้ว — adopt the established stack, อย่าแตกแนว
- เลือก statically-typed language เมื่อเริ่มใหม่ และเปิด type checking ให้เข้มที่สุดเท่าที่
  ภาษานั้นมี; กำหนด type ของ data/interface ให้ชัดเจน, เลี่ยง escape hatch แบบ dynamic/`any`
- ห้ามเพิ่มภาษา/runtime ใหม่เข้าโปรเจกต์โดยไม่มีเหตุผลที่บันทึกไว้ + ขออนุมัติก่อน

## Frameworks & Core Libraries

- ใช้ framework ที่โปรเจกต์ใช้อยู่แล้ว — ทำตาม convention ของ framework นั้น อย่าผสมหลายตัว
- การเพิ่ม library ใหม่ต้องมีเหตุผลที่บันทึกไว้ + ขออนุมัติก่อน (ดู Dependency rules ด้านล่าง);
  การ approve PR ที่บันทึกการเพิ่มนั้น = การอนุมัติ
- stack-specific guidance (UI framework, styling system, test-runner idioms) อยู่ใน profile
  เสริมแบบ optional ใต้ `.ai/shared/stack/` — เพิ่มไฟล์ของ stack ตัวเองเมื่อต้องการ;
  framework ไม่ bundle profile ใดมาให้โดย default

## Data Layer

- มี DB / backend ก็ต่อเมื่อโปรเจกต์ใช้จริง — ถ้าไม่มี ก็ใช้ข้อมูล mock แบบ typed แยกไฟล์
  พร้อม type ชัดเจน อย่าฝัง logic ไว้กับข้อมูล

## Tooling

- โปรเจกต์เป็นผู้ประกาศคำสั่ง typecheck / test / build ของตัวเอง — ไม่มี default ตายตัว
- task gate (`.ai/bin/gate-task.sh`) อ่านคำสั่งจาก env: `SDD_TYPECHECK_CMD` และ `SDD_TEST_CMD`
  (สำหรับ stack ใดก็ได้); สำหรับ Node ที่มี `package.json` จะ auto-detect script `typecheck` /
  `test` ให้เอง — ถ้าไม่มีทั้ง env และ script จะข้าม code-green check แล้วเหลือเพียง Evidence gate
- ตั้งค่า/รัน dev ด้วยคำสั่ง setup และ dev ของโปรเจกต์เอง

## Hard Constraints

- ห้าม hardcode secret ทุกชนิด — อ่านจาก environment variable หรือ secret manager เท่านั้น
  (ดู [SECURITY_RULES.md](SECURITY_RULES.md))
- ห้าม pin เวอร์ชันแบบ floating (`*` / `latest`) บน prod dependency; commit lock file เสมอ
  เมื่อใช้ package manager
- accessibility + semantic markup เป็นหลักการ ไม่ใช่ option: ทุกองค์ประกอบที่สื่อความต้องมี
  ป้ายกำกับ/`alt`, ใช้ semantic element, นำทางด้วยคีย์บอร์ดได้, contrast ผ่านเกณฑ์

## Rule

ใช้ stack ที่โปรเจกต์ตั้งไว้แล้วก่อนทางเลือกอื่น. ห้ามเพิ่ม library/ภาษา/runtime ใหม่
โดยไม่ระบุเหตุผลและขออนุมัติก่อน.
