---
name: spec-design
description: Generate the design.md artifact. Requirements-first (after requirements are approved) or design-first (architecture before requirements).
argument-hint: <feature folder name (optional)>
---

# Generate design.md

Resolve the target spec: use $ARGUMENTS if given; if `.ai/specs/` holds more
than one feature and none was named, list them and ask — never guess.

เลือก mode จาก artifact shape ที่อยู่บน disk แล้วเรียก shared phase gate ก่อนเขียนหรือ
advance `design.md` ทุกกรณี:

- Requirements-First: มี `requirements.md` และไม่มี `bugfix.md` ให้รัน

  ```bash
  python3 scripts/spec_contract.py gate phase --feature <feature> --phase design --workflow requirements-first
  ```

- Design-First: ยังไม่มี `requirements.md`, `tasks.md` หรือ `bugfix.md` และ flow นี้ถูกเลือกไว้
  ให้รัน

  ```bash
  python3 scripts/spec_contract.py gate phase --feature <feature> --phase design --workflow design-first
  ```

คำสั่งต้องคืน exit `0` ก่อนจึงทำต่อได้ หาก upstream missing, malformed, unknown หรือ
ไม่ approved ให้หยุดตาม diagnostic ของ engine ทันที ห้ามใช้ conversation, checkbox
หรือ code existence แทน approval และห้าม flip upstream status เพื่อข้าม gate.

ใน Requirements-First ให้อ่าน `requirements.md` พร้อม project rules
(@.ai/shared/CODING_STANDARDS.md, @.ai/shared/ARCHITECTURE.md) หลัง gate ผ่านเท่านั้น.

ใน Design-First ใช้คำตอบจาก `/spec-new` พร้อม project rules แล้วถามหนึ่งคำถามว่าเอา
high-level design (components + flows) หรือถึง module/interface level โหมดนี้ยังไม่มี
REQ IDs จึงไม่เขียน Requirement Traceability และให้ Testing Strategy map ไปยัง design
behaviors/sections ก่อน (`/spec-requirements` จะ backfill ภายหลัง) พร้อมเพิ่ม
`## Non-Functional Considerations` สำหรับ constraints ที่ทำให้เลือก Design-First
หาก artifact shape ไม่ตรงสองรูปนี้ให้หยุด ห้ามเดา mode.

Then write `.ai/specs/<feature>/design.md`:

  # Design: <Feature Name>
  > Status: draft

  ## Architecture Overview        — components and responsibilities
  ## Sequence Diagrams            — Mermaid for key flows
  ## Data Models & Interfaces     — schemas, types, API contracts
  ## Technology Decisions         — choices + rationale (prefer tech.md)
  ## Error Handling Strategy      — how each error case is handled
  ## Testing Strategy             — unit/integration/property; map to REQ IDs
  ## Requirement Traceability     — table: design element → REQ-x.y it satisfies

Sync mode: if design.md already exists and requirements.md changed after it was
written, do NOT regenerate the whole file — patch only the sections affected by
the changed REQs, preserving approved decisions, and update the traceability
table to match. If design.md was already approved, keep its canonical header as
`> Status: approved <original date>` and add the amendment separately as
`> Status-Note: amended <YYYY-MM-DD>`.

Produce design.md inline — this skill owns the section outline above (the single
source). When the design touches CORE domain logic (pure logic in the
project test directory, co-located with the logic under test), delegate a fresh-context adversarial critique to the
`spec-architect` subagent (its default mode = critique): it hunts unstated
assumptions, missing error paths, REQ coverage gaps, and infeasible choices.
Apply or explicitly rebut each finding before STOP. (For design-first CORE
production where no requirements.md exists yet, invoke spec-architect with
`mode=produce` instead, passing the /spec-new answers and stating that no
requirements.md exists.) When done: STOP for my review, then suggest
`/spec-tasks` (requirements-first) or `/spec-requirements` (design-first). When I
explicitly approve, flip the header to `> Status: approved <YYYY-MM-DD>` before
the next phase.
