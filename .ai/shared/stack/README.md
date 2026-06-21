> Optional extension point. Empty by default — nothing here is bundled with the framework.

# Stack profiles (optional)

Framework core เป็น stack-neutral. The neutral canon — `../CODING_STANDARDS.md`,
`../ARCHITECTURE.md`, `../TESTING_PROTOCOL.md` — holds for every project regardless of
language, test runner, or UI framework. โครงหลักไม่ผูกกับ stack ใดเป็นพิเศษ.

เดิมมี `nextjs.md` profile อยู่ที่นี่; ถูกถอดออกตอนที่ framework กลายเป็น stack-agnostic.

## วิธีใช้

วาง `<stack>.md` profile ลงในไดเรกทอรีนี้ (เช่น `nextjs.md`, `django.md`, `rust.md`)
เพื่อบันทึก stack-specific lessons / idioms ของโปรเจกต์คุณ — test-runner quirks,
UI-framework patterns, build/tooling gotchas ที่ไม่เข้ากับ canon ที่เป็นกลาง.

- Agents อ่าน profile **เฉพาะเมื่อมีไฟล์อยู่จริง** — ไม่มี → ไม่โหลด, ไม่มี default ใด bundle มาให้.
- A profile **complements** the neutral canon — it does not replace it. เพิ่มสิ่งที่เจาะจง stack
  ที่นี่; กฎทั่วไปคงไว้ที่ `../CODING_STANDARDS.md` / `../ARCHITECTURE.md` / `../TESTING_PROTOCOL.md`.
