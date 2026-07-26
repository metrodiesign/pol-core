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

---

## Section 1 — task 1 (from: Claude Opus 5 teammate, 2026-07-26)

### Task Summary

task 1 ของ spec `captive-payment-alignment`: vocabulary + eligibility guard + adapter capability
(domain/ports ล้วน ไม่แตะ handler ใด ๆ). ปิด REQ-3.6 (eligibility รวมจุดเดียวบน `Connection` และ
`Connection.Supports` มี call site จริง) และ REQ-6.1 (adapter ประกาศ method ที่ honour ได้จริง ให้
Application layer อ่านได้).

### Current Status

- task 1 **เสร็จ** — `- [x]` + `Evidence:` ใน tasks.md แล้ว, commit บน `feat/captive-payment-alignment`.
- task 2-7 ยัง `- [ ]` ทั้งหมด. ยังไม่มี handler / endpoint / migration / config / docs ถูกแก้เลย.
- `Connection.EnsureEligible` และ `IPspAdapter.SupportedMethods` **ยังไม่มี production call site** —
  โดยเจตนา (task 2 = create-session, task 3 = start-redirect เป็นผู้เรียก). ถ้ารัน audit ตอนนี้จะเห็นว่า
  ทั้งคู่ถูกเรียกจาก test เท่านั้น อย่าเข้าใจผิดว่างานไม่เสร็จ.

### Files Changed

- `src/Modules/Payments/Payments.Domain/PaymentMethods.cs` — created — static class, const
  `Card`/`PromptPay`/`Installment`, `IsKnown(string?)`, `Normalize(string)`.
- `src/Modules/Payments/Payments.Domain/Psp/Connection.cs` — edited — เพิ่ม `EnsureEligible(string)`
  (วางไว้ก่อน `Supports`); `Supports` ไม่ถูกแก้เลย (signature + Ordinal compare เดิม).
- `src/Modules/Payments/Payments.Application/Ports/IPspAdapter.cs` — edited — เพิ่ม
  `IReadOnlySet<string> SupportedMethods { get; }`.
- `src/Modules/Payments/Payments.Infrastructure/Psp/PspAdapterBase.cs` — edited — declare
  `SupportedMethods` เป็น **abstract**.
- `src/Modules/Payments/Payments.Infrastructure/Psp/TwoCTwoPAdapter.cs` — edited — override
  `SupportedMethods` = `{ PaymentMethods.Card }`.
- `src/Modules/Payments/Payments.Infrastructure/Psp/OmiseAdapter.cs` — edited — override เหมือนกัน.
- `tests/Payments.Tests/PaymentMethodsTests.cs` — created — 20 tests (code literal pin + Normalize +
  IsKnown ทุก branch).
- `tests/Payments.Tests/ConnectionEligibilityTests.cs` — created — 9 tests.
- `tests/Payments.Tests/Psp/TwoCTwoPAdapterTests.cs` — edited — +1 `SupportedMethods_declares_card_only`.
- `tests/Payments.Tests/Psp/OmiseAdapterTests.cs` — edited — +1 test ชื่อเดียวกัน.
- `.ai/specs/captive-payment-alignment/tasks.md` — edited — flip task 1 + Evidence.
- `.ai/specs/captive-payment-alignment/HANDOFF.md` — edited — section นี้.

### Important Decisions

1. **`SupportedMethods` = abstract บน `PspAdapterBase` + override ต่อ adapter** (design D2 เปิดให้เลือก).
   base default `{ card }` สั้นกว่า 3 บรรทัด แต่ adapter ใหม่ที่ honour card ไม่ได้จะ inherit การเคลมว่าทำได้
   เงียบ ๆ = silent substitution ที่ REQ-6 มีไว้กันโดยตรง; abstract ยังตรงกับที่ base ประกาศสมาชิก
   `IPspAdapter` อื่น **ทุกตัว** เป็น abstract อยู่แล้ว. **ผลต่อ task ถัดไป: adapter ใหม่ทุกตัวถูกบังคับให้
   ประกาศ capability เอง ไม่มี default ให้ตก.**
2. **`Normalize` เรียก `IsKnown(code)` ต่อ** ไม่ inline vocabulary ซ้ำสองที่ (ARCHITECTURE §Anti-Patterns
   ห้าม duplicate magic constant). ค่า vocabulary อยู่ที่ const 3 ตัวที่เดียวจริง ๆ.
3. **`ConnectionEligibilityTests` ตั้ง `IsEnabled = false` ผ่าน reflection** (private setter) เพราะ
   `Connection` ไม่มี `Disable()` และไม่มีที่ใดใน `src/` เขียน `IsEnabled = false` เลย (ยืนยันด้วย grep) —
   สถานะนั้นเกิดได้ทางเดียวคือ EF materialise แถวที่ admin ปิดไว้. **ไม่** เพิ่ม `Disable()` เพราะจะเป็น
   production method ที่ไม่มีผู้เรียก + อยู่นอก scope task 1.
4. **`EnsureEligible` เช็ค `IsEnabled` ก่อน `Supports`** ตาม design D3 ตรงตัว และมี test pin ลำดับนี้ไว้ —
   เพื่อให้เหตุผลที่รายงานคือ "connection ปิด" ไม่ใช่ "method ไม่อยู่ในลิสต์" สำหรับ connection ที่ปิดอยู่.

### Constraints (เพิ่มจาก section 0 — ยังใช้ทุกข้อ)

- **`PaymentMethods.Normalize` = 400 (`ArgumentException`), `Connection.EnsureEligible` = 409
  (`InvalidOperationException`)** — เส้นแบ่งนี้ล็อกแล้วทั้ง code + test + doc comment. task 2/3 ที่เรียกสอง
  ตัวนี้ห้ามห่อ/แปลง exception ใหม่ ไม่งั้น status code เพี้ยนจาก REQ-3.4 vs REQ-3.1/3.2.
- **ห้ามแก้ `Connection.Supports`** — เป็น Ordinal compare กับ `EnabledMethods` ดิบ. ค่าที่ส่งเข้า
  `EnsureEligible` ต้อง normalize มาก่อนจาก handler (task 2/3) ไม่ใช่ให้ `Supports` มา lower ให้;
  มี test pin ว่า `"Card"` (ตัวใหญ่) ถูกปฏิเสธ.
- **ห้ามตั้งชื่อ type/ไฟล์ว่า `PspConnection*`** — retired token ของ rename gate (trap 1). ใช้
  `Connection*` หรือชื่อที่ไม่มี prefix นั้น.

### Tests Run

- `dotnet build pol-core.slnx -warnaserror` -> `ok dotnet build: 64 projects, 0 errors, 0 warnings`
  (baseline ก่อนแก้: เหมือนกันเป๊ะ).
- `dotnet test tests/Payments.Tests` -> 90 passed / 0 failed / 0 skipped (จาก baseline 59).
- `dotnet test pol-core.slnx --filter "Category!=Integration"` -> exit 0, **1128 passed / 0 failed**.
  **baseline ที่จดไว้ก่อนแก้อะไรเลย (ตัวเลขอ้างอิงของ task 2+):** Admins.Tests 95 · Architecture.Tests 215 ·
  BuildingBlocks.Tests 43 · Carts.Tests 15 · Checkouts.Tests 7 · Divisions.Tests 6 · Hosts.Tests 341 ·
  Iam.Tests 62 · Levels.Tests 6 · Merchants.Tests 115 · Offices.Tests 6 · Orders.Tests 68 ·
  Payments.Tests 59 · Positions.Tests 6 · Products.Tests 7 · SharedKernel.Tests 46 = **1097 passed /
  0 failed / 16 projects**. หลังแก้: ทุก project เท่าเดิม ยกเว้น Payments.Tests 59 -> 90.
  (หมายเหตุ: `tests/Integration.Tests` ไม่ถูกนับ — filter คัดออกหมด; `Identity.Tests`/`Producer.Tests`/
  `Tenant.Tests` มีโฟลเดอร์แต่ไม่อยู่ใน `pol-core.slnx` จึงไม่มีผลลัพธ์.)
- `bash scripts/check-rename-identifiers.sh` -> `OK — no retired identifier appears as a live-code token`.
- `bash scripts/spec-trace.sh captive-payment-alignment` -> `OK: ... เกณฑ์ 42 ข้อ ถูกอ้างครบ ... EARS lint
  ผ่านทุกข้อ`.
- **ไม่ได้รัน:** integration tests (ไม่มีเกณฑ์ของ task 1 แตะ DB), `docker compose config`
  (ไม่แตะ compose), migration (ไม่มี DDL เปลี่ยน).

### กับดักใหม่ที่เจอ (เพิ่มจาก traps 1-11)

12. **rename gate อ่านผ่าน `git ls-files`** (`scripts/check_rename_identifiers.py:161-162`) — ไฟล์ **untracked
    ถูกข้ามเงียบ ๆ** แล้ว gate ตอบ OK. รอบแรกที่รันตอนไฟล์ใหม่ 3 ไฟล์ยัง untracked ได้ OK แบบไม่ได้ตรวจอะไรเลย
    (false green คลาสเดียวกับ LESSONS "gate ที่ skip แล้ว exit 0"). **ต้อง `git add` ก่อน แล้วรัน gate ซ้ำ**
    ไม่งั้นผ่าน local แต่แดงบน CI.
13. **task-gate hook ยืนยันตามที่ trap 2 บอก แต่โดนจริงเพราะเข้าใจผิดว่า "flip ก่อนแล้วต่อ Evidence" ได้** —
    Edit ที่ flip `[x]` บรรทัดเดียวถูก block ทันที (PostToolUse) แม้จะตั้งใจ append Evidence ใน Edit ถัดไป.
    **ที่แก้ไขแล้วได้ผล:** `old_string` คร่อม **ทั้งบล็อก task** (ตั้งแต่บรรทัด `- [x] 1.` ถึงบรรทัดสุดท้ายของ
    `Verify:`) แล้ว `new_string` = บล็อกเดิม + `Evidence:` + bullets ต่อท้าย ใน Edit เดียว. หมายเหตุ: การ block
    **ไม่ revert ไฟล์** — `[x]` ที่ flip ไปแล้วยังอยู่ ต้องแก้ต่อจากสถานะนั้น (old_string ต้องใช้ `[x]`
    ไม่ใช่ `[ ]`).
14. **`rtk` hook ทำให้ `grep -rn ... --include="*.cs"` fail** (`no matches found: --include=*.cs` — zsh glob
    ตีความก่อน) และบีบ output ของ `git diff`/`dotnet build` เป็น summary. ใช้ `rtk proxy grep ...` สำหรับ
    การค้นแบบมี flag และอย่าเชื่อว่า `dotnet build` ที่ตอบ 1 บรรทัดคือไม่ได้ build (มัน build จริง).

### Next Recommended Agent

builder ตัวใหม่ (fresh context) สำหรับ task 2 — เป็น task ที่ใหญ่สุดของ spec (port + repository query +
command/handler 8 ขั้น + endpoint wire contract + ไฟล์ fakes ชุดแรกของ `tests/Payments.Tests`).

### Next Steps

1. อ่าน `.ai/shared/*` 5 ไฟล์ -> spec 3 ไฟล์ -> HANDOFF ทั้งไฟล์ (รวม section นี้).
2. baseline: `dotnet build pol-core.slnx -warnaserror` + `dotnet test pol-core.slnx --filter
   "Category!=Integration"` ต้องได้ 1128 passed / 0 failed (Payments.Tests 90) — ถ้าไม่ตรง หยุดแล้วรายงาน.
3. implement task 2 ตาม design D1 + D4 (ลำดับ 8 ขั้นห้ามสลับ — ลำดับคือสิ่งที่ REQ-1.2/1.3/3.3/3.4/6.2
   ผูกกับ status code). `PaymentMethods.Normalize` และ `connection.EnsureEligible` พร้อมใช้แล้ว ห้ามเขียน
   เงื่อนไข eligibility ซ้ำใน handler (REQ-3.6).
4. flip `- [x] 2.` + `Evidence:` ใน Edit เดียว (trap 2 + 13) -> `git add` -> รัน rename gate ซ้ำ (trap 12) ->
   commit -> append section 2 ท้ายไฟล์นี้.
</content>
