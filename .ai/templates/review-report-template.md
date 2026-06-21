# Review Report: <pr-or-branch-or-task>

> Severity headers ตาม REVIEW_PROTOCOL.md (../shared/REVIEW_PROTOCOL.md)
> เรียง finding ตามความรุนแรง สูงสุดก่อน ลบหัวข้อ severity ที่ไม่มี finding ได้
> ทุก finding ต้องชี้ตำแหน่งจริง (`path:line`) + แก้อย่างไร ลบบรรทัด `<...>` ที่กรอกแล้ว

## Scope reviewed

- <ไฟล์ / diff / REQ IDs ที่ตรวจ>
- Commands run: `<typecheck command>`, `<test command>` -> <ผล>

## Verdict

<approve / request-changes / block> — <เหตุผลหนึ่งบรรทัด>

## Critical

> ต้องแก้ก่อน merge ทำให้พัง/ไม่ปลอดภัย/ข้อมูลเสียหาย

- [ ] <`path:line`> — <ปัญหา> -> <fix ที่แนะนำ>

## High

> ผิด requirement หรือ bug ชัดเจน ควรแก้ก่อน merge

- [ ] <`path:line`> — <ปัญหา> -> <fix>

## Medium

> ควรแก้ แต่ไม่บล็อก merge ได้ (correctness รอง / maintainability)

- [ ] <`path:line`> — <ปัญหา> -> <fix>

## Low

> ข้อเสนอแนะ / nit / สไตล์ ไม่บังคับ

- [ ] <`path:line`> — <ปัญหา> -> <fix>
