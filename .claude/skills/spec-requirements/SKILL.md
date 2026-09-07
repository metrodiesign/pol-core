---
name: spec-requirements
description: Generate the requirements.md artifact for the active feature spec using EARS notation. Use after /spec-new and after I've answered clarifying questions.
argument-hint: <feature folder name (optional)>
---

# Generate requirements.md

ก่อนสร้างหรือแก้ผลลัพธ์ อ่านและใช้ [นโยบายภาษาของผลลัพธ์](../../../.ai/shared/TASK_PROTOCOL.md#ภาษาของผลลัพธ์)

Resolve the target spec folder: use $ARGUMENTS if given; otherwise use the
feature folder created by /spec-new in this conversation. If neither identifies
one and `.ai/specs/` holds several features, list them and ask — never guess.

Derive mode (Design-First): trigger when the folder has a design.md but no
requirements.md. If that design.md is still `> Status: draft`, warn in Thai and
ask for confirmation first — and if I confirm, flip it to
`> Status: approved <YYYY-MM-DD>` before deriving. Then derive the requirements
FROM the design — each REQ cites the design section it comes from. While
deriving, sync is one-way: design is upstream; if a derived requirement
conflicts with the design, fix the requirement — or stop and ask if the design
itself looks wrong. (Once both artifacts exist, normal two-way sync resumes per
the constitution.) The derivation already maps each REQ to its design section,
so backfill design.md AS PART OF WRITING the draft requirements — do NOT defer
to approval: add the `## Requirement Traceability` table with columns
`Design element | REQ | Section`, make every `Section` value exactly match a real
`##` heading in the same design, update `## Testing Strategy` to cite the new REQ IDs, and re-stamp
design.md's header `> Status: approved <original date>, amended <YYYY-MM-DD>`.
Backfilling at draft time (not approval) guarantees that a downstream
`/spec-tasks` — which may be the skill that flips requirements.md to approved —
finds the table so `scripts/spec-trace.sh` passes; otherwise it hard-fails with
no skill authorized to create it. If the derived requirements change during
review, update the table to match before approval.

Write `.ai/specs/<feature>/requirements.md` with this structure:

  # ข้อกำหนด: <ชื่อฟีเจอร์>
  > Status: draft

  ## ภาพรวม

  <หนึ่งย่อหน้าเชื่อมโยงงานกับ product.md>

  ## REQ-1: <ความสามารถ เช่น การลงทะเบียนผู้ใช้>

  **ความต้องการของผู้ใช้:** ในฐานะ<บทบาท> ฉันต้องการ<เป้าหมาย> เพื่อให้<ประโยชน์>

  **เกณฑ์การยอมรับ:**

  - 1.1  ระบบต้อง<พฤติกรรม>                                  (ข้อกำหนดทั่วไป)
  - 1.2  เมื่อ<เหตุการณ์> ระบบต้อง<พฤติกรรม>                  (ตอบสนองต่อเหตุการณ์)
  - 1.3  ขณะที่<สถานะ> ระบบต้อง<พฤติกรรม>                     (ระหว่างอยู่ในสถานะ)
  - 1.4  ในกรณีที่<เปิดใช้คุณสมบัตินี้> ระบบต้อง<พฤติกรรม>              (เมื่อเปิดใช้คุณสมบัติ)
  - 1.5  หาก<เงื่อนไขผิดพลาด> ระบบต้อง<การตอบสนอง>        (จัดการข้อผิดพลาด)

  (เพิ่ม REQ-2, REQ-3, ... ตามความสามารถที่ต้องการ)

  ## กรณีพิเศษและคำถามที่ยังไม่ยุติ

  <ประเด็นที่ยังไม่ชัดเจน>

Rules: every requirement is atomic, testable, and has a stable ID. One observable
behavior per criterion — split compound criteria joined by "and"; reject
subjective wording ("fast", "user-friendly", "looks good") unless quantified
with a measurable threshold. Cover the happy path AND error/edge cases (use
รูปประโยค “หาก… ระบบต้อง…” ตาม EARS.md).

When done: STOP. Show me a summary and ask me to review. In derive mode the
design already exists, so suggest `/spec-tasks` next (or `/spec-analyze` first
for complex/sensitive features). Otherwise suggest `/spec-analyze` for complex
or sensitive features, else `/spec-design`.

When I explicitly approve (in a later turn), flip the header line in the artifact
to `> Status: approved <YYYY-MM-DD>` before starting the next phase — approval
must live in the file, not only in this conversation.
