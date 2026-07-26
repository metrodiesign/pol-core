# Handoff: Captive Intra-Group Payment Alignment

> Rolling handoff. **Lead appends the brief before each task; the teammate who finishes a task appends
> its own section below.** Newest section last. Read the WHOLE file before starting — the traps
> compound.

---

## Section 0 — Lead brief (from: Opus lead, 2026-07-26, before task 1)

### Task Summary

spec `.ai/specs/captive-payment-alignment/` — ไม่เพิ่ม feature ใหม่ ปิดช่องที่ **as-built ไม่ตรง canon
captive intra-group payment** (บริษัทในเครือรับชำระผ่าน PSP ที่ถือใบอนุญาต, redirect-only, ไม่ถือเงิน).
8 divergence ที่ยืนยันกับโค้ดจริงแล้ว (ตาราง A-H ใน requirements.md) -> 8 REQ / 42 เกณฑ์ / 7 task.

### Current Status

- spec ทั้ง 3 ไฟล์เขียนเสร็จและผ่าน `bash scripts/spec-trace.sh captive-payment-alignment`
  (42 เกณฑ์, EARS lint ผ่าน).
- ผ่าน adversarial review ด้วย `spec-architect` (fresh context) 1 รอบ: verdict รอบแรก `REVISE` +
  blocker 4 ข้อ -> verify ทีละข้อกับโค้ดจริง -> ยกมาเป็น REQ-6/7/8 และแก้ REQ-2/3/4 ในเวอร์ชันปัจจุบัน.
- branch `feat/captive-payment-alignment` สร้างแล้วจาก `develop` @ `ab5d6dd`. ยังไม่มีโค้ดถูกแก้.
- task 1-7 ยัง `- [ ]` ทั้งหมด.

### Files Changed (ถึงตอนนี้)

- `.ai/specs/captive-payment-alignment/requirements.md` — created
- `.ai/specs/captive-payment-alignment/design.md` — created
- `.ai/specs/captive-payment-alignment/tasks.md` — created
- `.ai/specs/captive-payment-alignment/HANDOFF.md` — created (ไฟล์นี้)

### Important Decisions (พร้อมเหตุผล — ห้าม re-litigate)

1. **create-session ที่เจอ open session ของ order เดิมด้วยช่องทางเดียวกัน = คืน id ใบเดิม (200) ไม่ใช่
   409.** เพราะ `Session.MarkFailed`/`MarkExpired` **ไม่มี production caller** (verify แล้ว: call site
   เดียวคือ `tests/Payments.Tests/PaymentSessionTests.cs:164`) -> session ที่ลูกค้าทิ้งค้าง `Redirected`
   ตลอดกาล. ถ้าตอบ 409 ทุกกรณี + ใส่ unique index = **order นั้นจ่ายไม่ได้ตลอดกาล** ซึ่งแย่กว่าโรคเดิม.
2. **eligibility/connection failure = `InvalidOperationException` (409) ไม่ใช่ `ArgumentException` (400).**
   เส้นแบ่ง: input ที่ client เขียนผิด = 400 (method นอก vocabulary); สถานะ/config ฝั่ง server = 409.
3. **capability ของ adapter เป็นชั้นแยกจาก `EnabledMethods`.** seed จริงเปิด promptpay/installment บน
   2C2P (`seed-demo.sql:102-105`) แต่ `TwoCTwoPAdapter.cs:47` hardcode `paymentChannel=["CC"]` ->
   เช็คแค่ `EnabledMethods` = ความมั่นใจปลอม ลูกค้าถูกส่งไปจ่ายด้วยบัตรเงียบ ๆ.
4. **ห้ามใช้ `AddOptions<PspOptions>().ValidateOnStart()`** สำหรับ `Psp:PublicBaseUrl` — `appsettings.json`
   ไม่มี section `Psp` เลย และมี **17 ไฟล์ใน `tests/Hosts.Tests` ที่ boot host จริงโดยไม่มี shared
   harness** -> จะล้มทั้ง job `dotnet` ของ CI. ใช้ placeholder ใน `appsettings.json` + fail fast เฉพาะ
   non-Development ผ่าน `ProvisioningGuards` ใน block `Program.cs:141-151` (pattern ที่ repo ใช้อยู่แล้ว
   กับ blank DB password + OIDC providers).
5. **ไม่ทำ Omise webhook HMAC ในสเปกนี้** — Omise/Opn **มี** ลายเซ็น (`Omise-Signature`) จริง; เหตุผลที่
   deferred คือ seam ไม่พา header/timestamp (`Program.cs:566` อ่านแค่ `X-Signature`) + ยังไม่ verify กับ
   sandbox. **ห้ามเขียนในเอกสารว่า Opn ไม่มีลายเซ็น** (ข้อมูลผิด).
6. **`FetchChargeAsync` ต้องพายอดกลับมาเทียบ** เพราะหลังปิด A แล้ว `session.Amount == order.Amount` โดย
   โครงสร้าง ทำให้การเทียบใน `Order.MarkPaid` เป็น tautology — ไม่มีชั้นไหนเทียบกับยอดที่ PSP เก็บจริง.
   `Amount == null` (PSP ไม่ส่งมา) -> ยืนยันด้วยสถานะเหมือนเดิม **ห้าม fail-closed** บน contract ที่ยัง
   ไม่ verify.

### Constraints (ทุก teammate ต้องเคารพ)

- **ทำเฉพาะ task ที่ได้รับมอบ** — ห้ามล้ำ task ถัดไป ห้ามแก้โค้ดที่ไม่ trace กลับไปหาเกณฑ์ของ task ตัวเอง.
- อ่านตามลำดับ: `.ai/shared/PROJECT_CONTEXT.md`, `.ai/shared/CODING_STANDARDS.md`,
  `.ai/shared/ARCHITECTURE.md`, `.ai/shared/LESSONS.md`, `.ai/shared/TASK_PROTOCOL.md` แล้วค่อย spec
  3 ไฟล์ + HANDOFF นี้ทั้งไฟล์.
- **filesystem เป็นความจริง** — checkbox/handoff บอกได้แค่เจตนา; reconcile กับของจริงก่อนเชื่อ.
- ห้าม push `develop`, ห้าม merge, ห้าม force push, ห้าม `--no-verify`, ห้าม commit secret.
- commit เฉพาะไฟล์ของ task ตัวเอง (+ `tasks.md`/`HANDOFF.md`) ด้วย conventional commit ภาษาอังกฤษ.
- **7 Non-Goals ของ PROJECT_CONTEXT** และ Non-Goals ของ spec นี้ = เส้นห้ามข้าม.
- Traps 1-11 ที่หัว `tasks.md` — อ่านทุกข้อ ทุกข้อมาจากความผิดพลาดจริงในอดีต.

### Tests Run (baseline ที่ lead ยืนยันแล้ว)

- `bash scripts/spec-trace.sh captive-payment-alignment` -> `OK: เกณฑ์ 42 ข้อ ถูกอ้างครบ ... EARS lint ผ่านทุกข้อ`
- ยังไม่ได้รัน `dotnet build`/`dotnet test` — **teammate ของ task 1 ต้องรันเป็น baseline ก่อนแก้อะไร**
  แล้วบันทึกตัวเลข (จำนวน test ต่อ project) ลง section ของตัวเองเพื่อให้ task ถัดไปเทียบว่าไม่ถอย.

### Known Issues / ที่ตั้งใจไม่ทำ

- Omise webhook signature (Non-Goal 1), promptpay/installment จริง (Non-Goal 3), session expiry sweeper
  (Non-Goal 4), เปลี่ยน method/PSP หลัง redirect เริ่มแล้ว (Non-Goal 4).
- job `dotnet-integration` ของ CI ถูกข้ามเมื่อไม่มี secret `MSSQL_SA_PASSWORD` -> DB floor ของ REQ-2
  ต้องมี proof ชั้น offline ด้วย (REQ-2.6) ไม่พึ่ง integration test อย่างเดียว.

### Next Steps

1. teammate task 1: อ่าน spec + section 0 นี้ -> รัน baseline `dotnet build pol-core.slnx -warnaserror`
   และ `dotnet test pol-core.slnx --filter "Category!=Integration"` บันทึกตัวเลข -> implement task 1.
2. flip `- [x]` + `Evidence:` ใน Edit เดียว -> commit -> append section ของตัวเองที่ท้ายไฟล์นี้.
3. task ถัดไปตามลำดับ 2 -> 3 -> 4 -> 5 -> 6 -> 7 (dependency ตรงตาม `Depends on:` ใน tasks.md).
</content>
