---
name: spec-requirements
description: Generate the requirements.md artifact for the active feature spec using EARS notation. Use after /spec-new and after I've answered clarifying questions.
argument-hint: <feature folder name (optional)>
---

# Generate requirements.md

Resolve the target spec folder: use $ARGUMENTS if given; otherwise use the
feature folder created by /spec-new in this conversation. If neither identifies
one and `.ai/specs/` holds several features, list them and ask — never guess.

โหมด derive (Design-First) ใช้เมื่อ folder มี `design.md` แต่ยังไม่มี
`requirements.md` เท่านั้น หลัง resolve feature แล้วต้องรัน shared phase gate นี้ก่อน
เขียนหรือ advance `requirements.md` รวมถึงก่อน backfill `design.md`:

```bash
python3 scripts/spec_contract.py gate phase --feature <feature> --phase requirements --workflow design-first
```

คำสั่งต้องคืน exit `0` ก่อนจึงทำต่อได้ หาก `design.md` missing, malformed, unknown
หรือไม่ approved ให้หยุดตาม diagnostic ของ engine ทันที ห้ามใช้ conversation,
checkbox หรือ code existence แทน approval และห้ามแก้ status ของ upstream เพื่อข้าม gate.

เมื่อ gate ผ่าน ให้ derive requirements จาก design โดยแต่ละ REQ อ้าง section ต้นทาง
ระหว่าง derive ให้ sync ทางเดียว: design เป็น upstream; ถ้า requirement ที่ derive มา
ขัดกับ design ให้แก้ requirement หรือหยุดถามหาก design เองผิด การ derive นี้ต้อง backfill
`## Requirement Traceability` (design element → REQ-x.y), ปรับ `## Testing Strategy`
ให้ cite REQ IDs ใหม่ และคง canonical header ของ design เป็น
`> Status: approved <original date>` พร้อมเพิ่ม annotation แยกบรรทัดเป็น
`> Status-Note: amended <YYYY-MM-DD>` ในการเขียน draft รอบเดียวกัน ห้ามเลื่อนไปทำตอน
approval หาก requirements เปลี่ยนระหว่าง review ให้ปรับ table ให้ตรงก่อน approval.

Write `.ai/specs/<feature>/requirements.md` with this structure:

  # Requirements: <Feature Name>
  > Status: draft

  ## Overview
  <one paragraph tying this to product.md>

  ## REQ-1: <Capability, e.g. User Registration>
  **User Story:** As a <role>, I want <goal>, so that <benefit>.
  **Acceptance Criteria (EARS):**
  - 1.1  THE SYSTEM SHALL <behavior>                               (ubiquitous)
  - 1.2  WHEN <event> THE SYSTEM SHALL <behavior>                  (event-driven)
  - 1.3  WHILE <state> THE SYSTEM SHALL <behavior>                 (state-driven)
  - 1.4  WHERE <feature is included> THE SYSTEM SHALL <behavior>   (optional)
  - 1.5  IF <error condition> THEN THE SYSTEM SHALL <response>     (error handling)

  (repeat REQ-2, REQ-3, ...)

  ## Edge Cases & Open Questions
  <anything ambiguous>

Rules: every requirement is atomic, testable, and has a stable ID. One observable
behavior per criterion — split compound criteria joined by "and"; reject
subjective wording ("fast", "user-friendly", "looks good") unless quantified
with a measurable threshold. Cover the happy path AND error/edge cases (use
IF...THEN).

When done: STOP. Show me a summary and ask me to review. In derive mode the
design already exists, so suggest `/spec-tasks` next (or `/spec-analyze` first
for complex/sensitive features). Otherwise suggest `/spec-analyze` for complex
or sensitive features, else `/spec-design`.

When I explicitly approve (in a later turn), flip the header line in the artifact
to `> Status: approved <YYYY-MM-DD>` before starting the next phase — approval
must live in the file, not only in this conversation.
