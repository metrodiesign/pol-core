# ADR 0002: การลงทะเบียนตัวแทนใช้ (merchant, email) เป็น key และ bind Microsoft identity ทีหลังด้วย email match

- Status: accepted
- Date: 2026-09-19
- Branch: `feat/agent-anonymous-registration-otp` (pol-core), `feat/agent-register-otp-dashboard` (pol-merchant)
- Glossary: [CONTEXT.md](../../CONTEXT.md); product scope: `.ai/shared/PROJECT_CONTEXT.md`

## Context

flow เดิม (PR #268) บังคับให้ตัวแทนกด "ลงทะเบียน" ผ่าน Microsoft CIAM ก่อน แล้ว `AgentRegistration`
ถูก key ด้วย `ExternalIdentity` (Provider/TenantId/ExternalUserId) — ไม่มี anonymous registration และ
ไม่มี unique constraint บน email เลย.

โจทย์ใหม่: ให้ตัวแทนกรอกฟอร์มลงทะเบียน **ก่อน** โดยไม่ต้องผ่าน Microsoft (anonymous) + ยืนยันเบอร์ด้วย
OTP แล้วค่อยผูก Microsoft identity ตอน login ครั้งแรก. เกิดคำถามว่าอะไรคือ key ของเคส และผูก identity
เข้ากับเคส anonymous อย่างไร.

## Decision

1. **identity ของ `AgentRegistration` = (`MerchantId`, `EmailNormalized`)** โดย `EmailNormalized` =
   `LOWER(TRIM(Email))` (normalization เดียวกับ domain `NormalizeEmail`). `ExternalIdentity` เป็น
   nullable.
2. **Microsoft identity ถูก bind เข้ากับเคสด้วย email match อย่างเดียว** (normalize แล้วเทียบ) ไม่มีการ
   ยืนยันซ้ำ (OTP/challenge) ตอน bind. ลำดับ lookup ตอน callback เป็น **identity-first, email-fallback**:
   หา LoginAccount → หา registration ด้วย `ExternalIdentity` → ค่อย fallback หาด้วย
   (`MerchantId`, `EmailNormalized`).
3. **Anonymous registration นี้จำกัดเฉพาะ "ตัวแทนภายใต้ merchant ที่ระบบ pin ไว้"** ผ่าน
   `IdentityAccessOptions.AgentMerchantId` และยังต้องผ่าน reviewer approval — ไม่ใช่ self-serve merchant
   onboarding (Non-Goal 3 ใน `.ai/shared/PROJECT_CONTEXT.md:97` ยังคงอยู่).

## Considered Options

- **identity-keyed แบบเดิม + prefill จาก Microsoft**: ตัดทิ้งเพราะขัดโจทย์ (ต้องผ่าน Microsoft ก่อน).
- **bind ด้วย email + OTP ยืนยันซ้ำตอน bind**: ตัดทิ้ง เพราะ OTP ยืนยันเบอร์ไปแล้วในขั้น registration และ
  Microsoft ยืนยัน email อยู่แล้ว การซ้ำเพิ่ม UX friction โดยได้ security เพิ่มน้อย.

## Consequences

- **รับ impersonation risk แบบมีเงื่อนไข**: คนอื่นกรอก email ของเราลงทะเบียนแทนได้ก่อน. บรรเทาด้วย unique
  `(MerchantId, EmailNormalized)` (หนึ่ง email ต่อ merchant มีเคสเดียว) + reviewer ตรวจรูปบัตร. risk นี้
  business รับแล้ว.
- **"Approved" ไม่ได้แปลว่า login ได้**: เคส approved มี `Account` แต่ยังไม่มี `LoginAccount` จนกว่าจะ
  Microsoft login ด้วย email เดียวกัน (ดู [CONTEXT.md](../../CONTEXT.md) term Approved/LoginAccount).
- **ทางกลับของเคสที่ session anonymous หมดอายุ = Microsoft-only**: PUT email เดิมโดยไม่มี cookie ได้ 409
  `email_already_registered` ทางเดียวที่ไปต่อคือกด "เข้าสู่ระบบ" ด้วย Microsoft ที่ email ตรงกัน (ใช้กับทั้ง
  เคส pending และ rejected).
- **unique index บน email เป็นของใหม่บน data เดิมที่ไม่เคยมี constraint** — migration ต้อง fail-fast (THROW)
  ถ้าพบ email ซ้ำต่อ merchant ก่อนสร้าง index; operator แก้ข้อมูลก่อน rerun (ห้าม auto-dedupe).
