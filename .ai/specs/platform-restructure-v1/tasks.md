# Implementation Tasks: รื้อ POL Platform รุ่นแรกสำหรับทีมเล็ก

> Status: approved 2026-09-09

ทำตามลำดับ 10 ช่วง แต่ละช่วงส่งมอบพฤติกรรมพร้อม tests ของตัวเอง ยังไม่มี task ใดเริ่ม implementation หรือผ่าน runtime gate

## ลำดับงาน

- [ ] 1. รวม packaging และเก็บ baseline — ย้าย source/test เข้า 4/3 projects โดยคงพฤติกรรมเดิม พร้อม inventory จุดเรียก DI/generated wiring jobs/config และข้อมูลที่ต้องย้าย
  Satisfies: REQ-1.1, REQ-1.2, REQ-1.4, REQ-1.7, REQ-1.8, REQ-11.1, REQ-12.1, REQ-12.5
  Verify: dotnet build pol-core.slnx && dotnet test pol-core.slnx.

- [ ] 2. Account, Access และ login ครบเส้นทาง — Employee JIT, pending registration session, human PKCE/BFF, SYSTEM private_key_jwt, account/client kill switch และ scoped authorization ใช้ OAuth state เจ้าของเดียว
  Satisfies: REQ-2, REQ-3.1, REQ-3.2, REQ-3.3, REQ-3.4, REQ-3.5, REQ-3.6, REQ-3.7, REQ-3.8, REQ-3.10, REQ-3.11, REQ-3.12
  Depends on: 1
  Verify: dotnet test pol-core.slnx --filter "Capability=IdentityAccess".

- [ ] 3. Merchant และตั้งค่า PSP แบบ maker-checker — master data, routing/method eligibility, credential versions, connection test และ emergency stop พร้อม immutable context สำหรับรายการเดิม
  Satisfies: REQ-5
  Depends on: 2
  Verify: dotnet test pol-core.slnx --filter "Capability=MerchantConfiguration".

- [ ] 4. สมัครตัวแทนถึงผลตัดสิน — draft/submit/ประวัติ, reviewer contact evidence, approve/reject แข่งกันได้ผลเดียว และสร้าง Account/Access/outbox ใน commit เดียว
  Satisfies: REQ-4
  Depends on: 2, 3
  Verify: dotnet test pol-core.slnx --filter "Capability=Registration".

- [ ] 5. Order และ PaymentLink — trusted pricing, nullable ownership, draft/issue/cancel, frozen items, link rotation และ customer-safe summary โดยไม่มี Payment aggregate
  Satisfies: REQ-6, REQ-7.1, REQ-7.2, REQ-7.3, REQ-7.4, REQ-7.5
  Depends on: 2, 3
  Verify: dotnet test pol-core.slnx --filter "Capability=OrdersLinks".

- [ ] 6. Checkout และ Transaction — per-tab confirm, persist-before-PSP, 2C2P/Omise adapters, callback/return/inquiry, same-reference recovery และ late/duplicate-success handling
  Satisfies: REQ-7.6, REQ-7.7, REQ-7.8, REQ-7.9, REQ-7.10, REQ-8
  Depends on: 3, 5
  Verify: dotnet test pol-core.slnx --filter "Capability=CheckoutTransactions".

- [ ] 7. Notification และงานค้าง — materialize Email/SMS จาก control outbox, SMTP reuse, SMS contract, signed business webhook, delivery snapshots, dedupe/retry และ review notes
  Satisfies: REQ-9
  Depends on: 4, 6
  Verify: dotnet test pol-core.slnx --filter "Capability=Notifications".

- [ ] 8. ตรวจ API และคู่มือใช้งานทั้งระบบ — 111 operations, deferred routes ปิด, child/history/export isolation, errors/idempotency/ETag/audit/health และคู่มือดูแลชุดเดียว
  Satisfies: REQ-3.9, REQ-10, REQ-12.4
  Depends on: 2, 3, 4, 5, 6, 7
  Verify: dotnet test pol-core.slnx --filter "Capability=ApiOperations".

- [ ] 9. เครื่องมือย้ายและซ้อม cutover — deterministic ID mapping, conflict report, backfill ไม่มี external side effect, callback recovery และ forward-safe rollback จาก sanitized backup
  Satisfies: REQ-11.2, REQ-11.3, REQ-11.4, REQ-11.5, REQ-11.6, REQ-11.7, REQ-11.8, REQ-11.9, REQ-11.11, REQ-12.2, REQ-12.3
  Depends on: 8
  Verify: dotnet test pol-core.slnx --filter "Capability=MigrationReadiness".

- [ ] 10. ถอด legacy และปิดเกณฑ์ส่งมอบ — retire เฉพาะรายการที่มีหลักฐาน, เหลือ 2 runtime contexts/mapping เจ้าของเดียว, ยืนยัน dependency/ownership และ baseline ไม่ถดถอย
  Satisfies: REQ-1.3, REQ-1.5, REQ-1.6, REQ-11.10, REQ-12.6
  Depends on: 9
  Verify: dotnet build pol-core.slnx && dotnet test pol-core.slnx && python3 scripts/spec_contract.py check --feature platform-restructure-v1 --strict && bash scripts/spec-trace.sh platform-restructure-v1.

## สิ่งที่ต้องพิสูจน์ในแต่ละช่วง

| Task | เกณฑ์จบที่ต้องมีหลักฐานจริง |
|---|---|
| 1 | นับได้ 4 source/3 tests, baseline เดิมถูกย้ายครบ, ไม่ลบ tests เพื่อให้เขียว, compiler/architecture guard ยังจับ dependency ผิดได้; runtime contexts เดิมอยู่ชั่วคราวจน task 10 |
| 2 | SQL Server JIT race, cross-Merchant guards, no auto-access, replay jti, revoked/expired key, stale authz version, BFF refresh/logout, account-self/platform/merchant policy, CSRF ทุก cookie mutation, revoke-then-commit lease race; pin OpenIddict/EF ตาม design และ restore/build จริง |
| 3 | cross-Merchant FK/write guard, maker ไม่ใช่ checker, stale BaseVersion, secret masking, active config เปลี่ยนแต่ inquiry ของเก่ายังใช้ pinned context |
| 4 | draft ไม่มี Attempt, submit replay ไม่ซ้อน, approve/reject race, Sale เปลี่ยน/ถูกผูกซ้ำแล้ว approve ไม่ผ่าน, applicant มองไม่เห็น internal note; ตรวจ outbox payload ของทั้งสองช่องทาง |
| 5 | ราคาไม่เชื่อ browser, money boundary/rounding, issue atomic, stale ETag, cancelled/paid matrix, rotate/replay link และไม่เปิด raw snapshot |
| 6 | สอง tabs/สอง Idempotency-Keys ไม่สร้าง charge ซ้อน, crash ก่อน/หลังส่ง PSP, timeout ไม่มี failover, callback replay/out-of-order, mismatch, success หลัง cancel และ success จริงสองแถวไม่สูญหาย, cancel/confirm serialize ผ่าน Order เดียว และ terminal result ไม่ถูก downgrade |
| 7 | control-outbox → commerce-inbox แบบ atomic/dedupe, Email/SMS failure ไม่ย้อน approval, recipient/template คงเดิม, accepted ไม่เท่ากับ delivered, timeout เป็น unknown, SSRF/redirect ถูกปฏิเสธ |
| 8 | โหลด inventory เทียบ HTTP route metadata จริง, exact write DTO request/response schema tests และ SFS เดิม, authorization ของทุก operation, child/export ไม่หลุด Merchant, GET status ไม่มี PSP call, audit ไม่หลุด secret/PII |
| 9 | count/mapping/reference/ยอดแยก currency เท่ากัน, ไม่มี fabricated Sale/identity, external-call capture เป็นศูนย์ตอน backfill, replay events หลัง watermark, conflict ทำให้ซ้อมล้มเหลว, ไม่แตะ production |
| 10 | legacy consumers/config/jobs และ callbacks หมดจริงก่อนลบ, architecture tests จับ owner/context ซ้ำ, full suite ผ่านหรือ pre-existing failure มี root-cause evidence พร้อมผลกระทบชัด |

Task 2 ใช้ Merchant/Sale fixtures ที่มาจาก contract จริงใน SQL Server เพื่อพิสูจน์ scope ก่อน Merchant management APIs ของ task 3 พร้อม Task 5 ใช้ transaction-state fixtures สำหรับ cancellation guard และ task 6 ต้องทดสอบ race กับ workflow จริงซ้ำ

Task 7 ส่งมอบ orchestration และสถานะ `BLOCKED_NOT_CONFIGURED` ได้ก่อนทราบ SMS vendor แต่ห้ามบันทึกว่า SMS live-ready ต้องเพิ่ม adapter/contract evidence ของ vendor ที่เลือกก่อนเปิดส่งจริง

## วิธีรันและเก็บหลักฐาน

คำสั่งใน Verify เป็นคำสั่งสำหรับ implementation หลัง task 1 สร้าง target projects แล้ว ไม่ใช่ผลทดสอบของรอบเขียน spec นี้ ใช้ SQL Server test database แยก, clock ที่ควบคุมได้ และ adapters ที่บันทึกจำนวน external calls

กำหนด xUnit trait `Capability` ตามชื่อ filter ในแต่ละ task และ trace `REQ-x.y` ใน test cases ให้ตรง branch ที่ขับจริง หาก filter ไม่พบ test ให้ถือว่าไม่ผ่าน ห้ามใช้ exit code 0 ของ test runner ที่ไม่พบ tests เป็นหลักฐาน

```sh
dotnet build pol-core.slnx
dotnet test tests/Pol.UnitTests/Pol.UnitTests.csproj
dotnet test tests/Pol.ArchitectureTests/Pol.ArchitectureTests.csproj
dotnet test tests/Pol.IntegrationTests/Pol.IntegrationTests.csproj
python3 scripts/spec_contract.py check --feature platform-restructure-v1 --strict
bash scripts/spec-trace.sh platform-restructure-v1
```

เมื่อจบแต่ละ task บันทึกคำสั่ง, exit code, จำนวน tests, environment, AC ที่พิสูจน์ และข้อจำกัดใน Evidence ตาม protocol แล้วผ่าน review/test gates ก่อน checkpoint บน feature branch งานที่ไม่ได้รันต้องระบุว่าไม่ได้รัน

## วิธีดำเนินงานและเงื่อนไขภายนอก

ทำต่อใน task เดิมตามลำดับเพื่อรักษาบริบท ไม่สร้าง parallel sessions โดยอัตโนมัติ งาน migration เป็นโค้ดภายใน 4 projects เดิม ไม่เพิ่ม migration service/framework ใหม่

| Input | ต้องพร้อมเมื่อ | ถ้ายังไม่มี |
|---|---|---|
| Entra tenant/issuer/app role และ client registrations | เปิด human login ในสภาพแวดล้อมจริง | ทดสอบ local protocol ได้ แต่ปิด live capability |
| PSP account/credentials/contract sandbox evidence | เปิดช่องทางรับเงินจริง | ทดสอบ reducer/orchestration ได้ แต่ไม่แสดง method ว่า live |
| SMS vendor/API และ sender credentials | เปิด SMS dispatch จริง | เก็บ delivery แบบ blocked และระบุข้อจำกัด |
| master identity/Merchant/Sale/Branch mappings | cutover rehearsal | รายงาน conflict ไม่เดาจาก email |
| backup, recovery evidence และ production authorization | ก่อนเปลี่ยน production | ไม่ทำ production action จาก spec นี้ |

การเริ่ม implementation ไม่เท่ากับการอนุมัติ deployment ห้ามถอด compatibility ที่ยังรองรับ PSP callback/customer link ค้างเพียงเพื่อให้จำนวนไฟล์ถึงเป้าหมาย
