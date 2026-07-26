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

---

## Section 1b — Lead addendum (from: Opus lead, 2026-07-26, after task 1 verified)

### Lead-verified independently (ไม่ใช่คำบอกเล่าของ teammate)

- `dotnet build pol-core.slnx -warnaserror` -> `64 projects, 0 errors, 0 warnings`
- `bash scripts/check-rename-identifiers.sh` -> OK
- `bash scripts/spec-trace.sh captive-payment-alignment` -> OK 42 เกณฑ์
- `dotnet test tests/Payments.Tests --no-build` -> 90 passed / 0 failed
- baseline ของ task 1 ที่ทุก task ต้องไม่ทำถอย: **1097 passed ก่อนแก้ -> 1128 หลัง task 1**
  (Admins 95, Architecture 215, BuildingBlocks 43, Carts 15, Checkouts 7, Divisions 6, Hosts 341,
  Iam 62, Levels 6, Merchants 115, Offices 6, Orders 68, Payments 59->90, Positions 6, Products 7,
  SharedKernel 46)

### ข้อเท็จจริงที่ lead ตรวจไว้ล่วงหน้าให้ task 5 (อย่าเสียเวลาหาซ้ำ)

- `ProvisioningGuards` = `internal static class` **อยู่ใน `src/Hosts/Api/Program.cs:2235`** (ไม่ใช่ไฟล์แยก)
  มี `RejectSecretsInConfig`, `RequireInjectedCredential(string connectionString, string name)`,
  `RequireOidcProviders(IConfiguration, string sectionName, bool requireAtLeastOne)`.
  มี unit test อยู่แล้วที่ `tests/Hosts.Tests/ProvisioningGuardsTests.cs` -> เพิ่ม guard ตัวใหม่ที่นี่
  ทดสอบได้**โดยไม่ต้อง boot host**. call site = block `if (!builder.Environment.IsDevelopment())`
  ที่ `Program.cs:141-151`.
- `Configure<PspOptions>` อยู่ที่ `Program.cs:129`.
- **compose render check มี 2 ที่** ที่ต้องเพิ่ม placeholder ของ env ใหม่:
  `.github/workflows/ci.yml:118-126` (env block ของ job `docker-build`) และ
  `.gitlab-ci.yml:150-163` (inline env ก่อน `docker compose ... config -q`; ปัจจุบันบรรทัด 159-161 ตั้ง
  `PSP_TWOCTWOP_FRONTEND_RETURN_URL` / `PSP_TWOCTWOP_BACKEND_RETURN_URL` / `PSP_OMISE_RETURN_URI`).

### ข้อเท็จจริงที่ lead ตรวจไว้ล่วงหน้าให้ task 6

- ฝั่ง request ที่มีอยู่แล้วยืนยัน naming ของแต่ละ PSP: 2C2P ใช้ `amount` (decimal **major unit**) +
  `currencyCode` (`TwoCTwoPAdapterTests.cs:64-65`); Omise ใช้ `amount` (**minor unit** integer) +
  `currency` (`OmiseAdapterTests.cs:48-49`). ฝั่ง fetch ให้ใช้ชื่อ field ชุดเดียวกันตามเอกสารของแต่ละ PSP
  (2C2P paymentInquiry = major unit; Omise `GET /charges/{id}` = minor unit -> แปลงกลับด้วย
  `Iso4217.MinorUnitDigits`).
- fixture ของ `FetchCharge` ในเทสต์วันนี้มีแค่ `respCode` / `status` -> ต้องเพิ่ม amount ลง fixture เอง
  และต้องมี case ที่ field **ไม่มี** -> `Amount = null` (REQ-8.3) ห้าม throw.

---

## Section 2 — task 2 (from: Claude Opus 5 teammate, 2026-07-26)

### Task Summary

task 2 ของ spec `captive-payment-alignment`: create-session ตั้งราคาจาก **Order เท่านั้น** + eligibility
ของ connection + capability ของ adapter + idempotent ต่อ order. ปิด REQ-1 (1.1, 1.2, 1.3, 1.4, 1.5, 1.7),
REQ-2 (2.1, 2.2, 2.3), REQ-3 (3.1, 3.2, 3.3, 3.4), REQ-6 (6.2). ทำตาม design D1 + D4 ตรงตัว — ลำดับ 8 ขั้น
ไม่สลับ เพราะลำดับคือสิ่งที่กำหนด status code ต่อ REQ.

### Current Status

- task 2 **เสร็จ** — `- [x]` + `Evidence:` ใน tasks.md, commit บน `feat/captive-payment-alignment`.
- task 3-7 ยัง `- [ ]`. ยังไม่มี `StartRedirectHandler` / migration / config / adapter signature / docs ถูกแก้.
- `Connection.EnsureEligible` และ `IPspAdapter.SupportedMethods` **มี production call site แล้ว 1 จุด**
  (`CreateSessionHandler`) — task 3 จะเพิ่มจุดที่สองใน `StartRedirectHandler` (REQ-3.5).
- `Session.MarkFailed` **ยังไม่มี production caller** (task 3) ดังนั้นวันนี้ session ที่ลูกค้าทิ้งยังค้าง
  `Redirected` ตลอดกาล — แต่ create-session คืนใบเดิมให้ (REQ-2.1) จึงยังไม่ล็อกตาย.

### Files Changed

- `src/Modules/Payments/Payments.Application/Ports/IPayableOrderReader.cs` — created — `PayableOrder(Guid
  OrderId, Money Amount, bool IsAwaitingPayment)` + `IPayableOrderReader.GetAsync`.
- `src/Modules/Payments/Payments.Application/Ports/ISessionRepository.cs` — edited — เพิ่ม
  `GetOpenForOrderAsync(Guid orderId, CancellationToken)`.
- `src/Modules/Payments/Payments.Application/CreateSession/CreateSessionCommand.cs` — edited — ตัด `Amount`
  (เหลือ `OrderId, MerchantId, Method, Psp`), ตัด `using SharedKernel`.
- `src/Modules/Payments/Payments.Application/CreateSession/CreateSessionHandler.cs` — rewritten — 8 ขั้นตาม
  D4, deps เพิ่มจาก 3 -> 6 (`IPayableOrderReader`, `IConnectionRepository`, `IPspAdapterFactory`,
  `ISessionRepository`, `IUnitOfWork`, `IClock`).
- `src/Persistence/Persistence.MerchantRuntime/Payments/PayableOrderReader.cs` — created — `internal sealed`,
  `AsNoTracking()`, project scalar 3 ตัวแล้ว `Money.Of` ใน memory.
- `src/Persistence/Persistence.MerchantRuntime/Payments/SessionRepository.cs` — edited — เพิ่ม
  `GetOpenForOrderAsync` (ใช้ `||` ไม่ใช่ `is ... or` — expression tree ห้าม pattern matching).
- `src/Persistence/Persistence.MerchantRuntime/MerchantRuntimePersistenceRegistration.cs` — edited — register
  `IPayableOrderReader` ข้าง `ISessionRepository` (ไม่แก้ csproj — reference ครบแล้วจริง).
- `src/Hosts/Api/Program.cs` — edited — endpoint ส่ง 4 args, `CreatePaymentSessionRequest` ตัด `Amount`,
  `.WithDescription` ใหม่ (ไทย), `+ProducesProblem(404)` `+ProducesProblem(409)`.
- `tests/Payments.Tests/Fakes.cs` — created — ไฟล์ fakes ชุดแรกของ project นี้ (`FakePayableOrderReader`
  นับ `Calls`, `FakeConnectionRepository`, `FakePspAdapter` + `FakePspAdapterFactory`,
  `FakeSessionRepository` เปิด `Added`, `FakeUnitOfWork` นับ `SaveCount`, `FixedClock`).
- `tests/Payments.Tests/CreateSessionHandlerTests.cs` — created — 16 tests.
- `tests/Architecture.Tests/PaymentPricingQueryTests.cs` — created — 5 tests (SQLite, real provider).
- `tests/Hosts.Tests/CreatePaymentSessionContractTests.cs` — created — 3 tests (property-pin + stale-body
  bind + declared status codes).
- `.ai/specs/captive-payment-alignment/tasks.md` — edited — flip task 2 + Evidence.
- `.ai/specs/captive-payment-alignment/HANDOFF.md` — edited — section นี้.

### Important Decisions

1. **`FakePspAdapter` ทุก member ที่ไม่ใช่ `Psp`/`SupportedMethods` throw `NotSupportedException`** — handler
   test ที่ไปแตะ charge/webhook ได้ = หลุด guard ที่ test นั้นมีไว้พิสูจน์ ควรแดงทันที ไม่ใช่คืนค่าปลอม.
   **ผลต่อ task 3/5/6:** task 3 ต้องการ adapter ที่ charge ได้/throw ได้ตามสั่ง -> เพิ่ม hook (เช่น
   `Func<...>` หรือ flag) บน `FakePspAdapter` ตัวนี้ ไม่ต้องสร้างไฟล์ fakes ใหม่; task 5 เปลี่ยน signature
   `CreateRedirectChargeAsync` ต้องแก้ที่นี่ด้วย (เป็น fake ตัวเดียวในโค้ดเบส).
2. **ทุก test ของเส้นทางปฏิเสธ assert `Added` ว่าง + `SaveCount == 0`** (helper `AssertNothingWasPersisted`)
   ไม่ใช่แค่ assert exception — คำขอที่ถูกปฏิเสธห้ามทิ้ง session row ไว้ให้ unique index ของ task 4 ไปสะดุด.
3. **idempotent-return ไม่ save เลย** (ไม่เรียก `SaveChangesAsync`) — คืน id ใบเดิมล้วน ๆ. task 4 ที่ใส่
   unique filtered index ต้องไม่คาดหวังว่ามี write ในเส้นทางนี้.
4. **`PayableOrder.OrderId` ถูก echo กลับจาก row ที่อ่านได้ ไม่ใช่จาก argument** — fake จึงจำลอง "order ของ
   บริษัทอื่น" ด้วยการคืน null เมื่อ id ไม่ตรง เหมือน query filter จริง.
5. **ไม่เพิ่ม test ที่ยิง HTTP จริงเข้า endpoint** — ไม่มี harness ที่ boot host + live DB สำหรับ
   merchant-user session ใน `tests/Hosts.Tests` (ที่มีคือ metadata inspection + DTO binding) และการสร้างใหม่
   อยู่นอก scope task 2. wire contract ถูก pin ด้วย property-set ของ DTO + declared status codes แทน.

### Constraints (เพิ่มจาก section 0/1 — ยังใช้ทุกข้อ)

- **`CreateSessionCommand` ไม่มี `Amount` แล้ว** — ทุก call site ใหม่ต้องอ่านยอดจาก order ผ่าน
  `IPayableOrderReader` เท่านั้น. `CreateSessionHandler` เป็นที่เดียวที่บังคับ invariant นี้ (REQ-1.5)
  ห้ามย้ายการตรวจไปที่ endpoint.
- **ลำดับ 8 ขั้นใน `CreateSessionHandler` ห้ามสลับ** — มี test pin ว่า method ที่ไม่ใช่ vocabulary ถูก
  ปฏิเสธ **ก่อน** อ่าน order เลย (`FakePayableOrderReader.Calls == 0`).
- **ห้ามห่อ/แปลง exception จาก `PaymentMethods.Normalize` (400) หรือ `Connection.EnsureEligible` (409)** —
  ตามข้อจาก section 1 ยังใช้; handler ปล่อยทะลุขึ้นไปตรง ๆ ให้ ProblemDetails map.
- **`ConflictException` (409) สำหรับ "ช่องทางต่าง"** ไม่ใช่ `InvalidOperationException` — แยกให้ ops อ่าน
  log แล้วรู้ว่าเป็นเคส open-session ไม่ใช่ state/config ผิด (ทั้งคู่ออก 409 เหมือนกัน).

### Tests Run

- `dotnet build pol-core.slnx -warnaserror` -> `Build succeeded. 0 Warning(s) 0 Error(s)` (64 projects).
- baseline ก่อนแก้ (ยืนยันเองซ้ำ): `dotnet test pol-core.slnx --filter "Category!=Integration"` -> exit 0,
  **1128 passed / 0 failed** ตรงกับ section 1/1b.
- `dotnet test tests/Payments.Tests --no-build` -> **106 passed / 0 failed** (90 -> +16).
- `dotnet test tests/Architecture.Tests --no-build` -> **220 passed / 0 failed** (215 -> +5).
- `dotnet test tests/Hosts.Tests --no-build` -> **344 passed / 0 failed** (341 -> +3).
- `dotnet test pol-core.slnx --filter "Category!=Integration"` -> exit 0, **1152 passed / 0 failed**
  across 16 projects. **baseline ใหม่ของ task 3+:** Admins 95 · Architecture **220** · BuildingBlocks 43 ·
  Carts 15 · Checkouts 7 · Divisions 6 · Hosts **344** · Iam 62 · Levels 6 · Merchants 115 · Offices 6 ·
  Orders 68 · Payments **106** · Positions 6 · Products 7 · SharedKernel 46 = **1152**.
- `bash scripts/check-rename-identifiers.sh` -> OK (รันหลัง `git add` ตาม trap 12).
- `bash scripts/spec-trace.sh captive-payment-alignment` -> OK 42 เกณฑ์ (รันซ้ำหลังแก้ tasks.md).
- **ไม่ได้รัน:** integration tests (task 2 ไม่มีเกณฑ์แตะ DDL/DB จริง — index เป็นของ task 4),
  `docker compose config` (ไม่แตะ compose), migration (ไม่มี DDL เปลี่ยน).

### กับดักใหม่ที่เจอ (เพิ่มจาก traps 1-14)

15. **MSBuild up-to-date check แพ้ same-second race — test รันบน dll เก่าเงียบ ๆ**: `Api.dll` มี mtime
    **เท่ากับวินาทีเดียวกัน** กับการแก้ `Program.cs` ครั้งสุดท้าย -> MSBuild ถือว่า up-to-date แล้ว **ข้าม
    การ compile Api ทุกครั้งถัดไป** ทั้งที่ `Build succeeded` ทุกรอบ. อาการ: Hosts.Tests fail ด้วย
    `Actual: ["OrderId", "Amount", "Method", "Psp"]` ทั้งที่ source ไม่มี `Amount` แล้ว (ยืนยันด้วย grep).
    **วิธีจับ:** `stat -f "%m %N" <proj>/bin/Debug/net10.0/X.dll <source>.cs` แล้วเทียบ — เท่ากันคือเจอ.
    **วิธีแก้:** `touch <source>.cs` แล้ว build ซ้ำ. **บทเรียน:** test ที่แดงแบบ "ขัดกับ source ที่เพิ่งอ่าน"
    ให้สงสัย stale binary ก่อนสงสัย logic; และ `Build succeeded` ไม่ได้แปลว่า project นั้นถูก compile.
16. **`IsRowVersion()` + SQLite = insert ไม่ได้เลย** — EF ตัดคอลัมน์ออกจาก INSERT (store-generated,
    `BeforeSaveBehavior = Ignore` -> ตั้งค่าเองก็ถูกเมิน) แล้วชน `SQLite Error 19: NOT NULL constraint
    failed: PaymentSessions.RowVersion`. **ผลต่อ task 4:** อย่าวางแผนพิสูจน์พฤติกรรมของ `Session` ที่ต้อง
    **มีแถวจริง** บน harness SQLite ของ `Architecture.Tests` — offline proof ที่ REQ-2.6 ขอ ทำได้เฉพาะระดับ
    **model metadata** (ชื่อ index / `IsUnique` / filter string ผ่าน `db.Model.FindEntityType(...)`) ซึ่ง
    ตรงกับที่ design D5 เขียนไว้แล้ว; ส่วนพฤติกรรม (insert ใบที่สองแล้วได้ `ConflictException`,
    `Failed`/`Expired` ไม่ติด filter) ต้องเป็น integration test บน SQL Server จริงที่ `:11433`.
    ทางหนีถ้าจำเป็น: raw parameterized INSERT ใน test (BypassPrimitiveTests สแกนแค่ `src/` ไม่สแกน `tests/`).
17. **`ProducesProblem` metadata อ่านผ่าน `IProducesResponseTypeMetadata`** (namespace
    `Microsoft.AspNetCore.Http.Metadata`) จาก `EndpointDataSource` — ใช้ pattern factory เดียวกับ
    `PermissionGateSitesTests`/`RouteSchemeConventionTests` (Development + in-memory config, ไม่ต้องมี DB).
18. **DTO binding test ต้องใส่ converter ของ host เอง** — `JsonSerializerDefaults.Web` เปล่า ๆ อ่าน
    `"psp": "2c2p"` ไม่ได้ (ได้ `JsonException` จาก `EnumConverter`); host ลงทะเบียน
    `PspCodeJsonConverter` + `MoneyJsonConverter` ที่ `Program.cs:406-410`. ทั้งคู่ `internal` แต่
    `Hosts.Tests` เห็น (InternalsVisibleTo).

### Next Recommended Agent

builder ตัวใหม่ (fresh context) สำหรับ task 3 — `StartRedirectHandler` เรียงใหม่ตาม D6. ขนาดเล็กกว่า task 2
มาก (1 handler + tests) แต่ต้องระวังว่า test ต้องพิสูจน์ **fail-then-retry ทั้งเส้น** (เรียก
`CreateSessionHandler` ของ order เดิมหลัง `Failed` แล้วได้ id ใหม่) ซึ่งตอนนี้ทำได้แล้วเพราะ handler ของ
task 2 พร้อม + `FakeSessionRepository` มีอยู่แล้ว (test
`A_terminal_session_does_not_block_a_fresh_attempt` เป็นครึ่งเส้นของกรณีนั้นอยู่แล้ว).

### Next Steps

1. อ่าน `.ai/shared/*` 5 ไฟล์ -> spec 3 ไฟล์ -> HANDOFF ทั้งไฟล์ (รวม section 2 นี้).
2. baseline: `dotnet build pol-core.slnx -warnaserror` + `dotnet test pol-core.slnx --filter
   "Category!=Integration"` ต้องได้ **1152 passed / 0 failed** — ถ้าไม่ตรง หยุดแล้วรายงาน.
3. implement task 3 ตาม design D6 (ย้าย resolve connection + `EnsureEligible` ขึ้นก่อน `BeginRedirect`;
   ครอบ `CreateRedirectChargeAsync` ด้วย try/catch ที่ `MarkFailed` + save + **rethrow**; reason ห้ามมี secret).
4. flip `- [x] 3.` + `Evidence:` (trap 2/13) -> `git add` -> rename gate ซ้ำ (trap 12) -> commit ->
   append section 3.

---

## Section 3 — task 3 (from: Claude Opus 5 teammate, 2026-07-26)

### Task Summary

task 3 ของ spec `captive-payment-alignment`: liveness ของ `StartRedirectHandler` ตาม design D6 — ย้าย
resolve connection + `EnsureEligible` ขึ้น**ก่อน** claim, และ `MarkFailed` + save + **rethrow** เมื่อ charge
ที่ PSP ล้ม. ปิด REQ-3 (3.5), REQ-7 (7.1, 7.2, 7.3, 7.4). ไฟล์ production ที่แตะมีไฟล์เดียว
(`StartRedirectHandler.cs`) — ที่เหลือเป็น test + fakes.

### Current Status

- task 3 **เสร็จ** — `- [x]` + `Evidence:` ใน tasks.md, commit บน `feat/captive-payment-alignment`.
- task 4-7 ยัง `- [ ]`. ยังไม่มี index/migration / `PspOptions` / `paymentChannel` / `FetchChargeAsync` /
  provisioning / docs ถูกแก้.
- `Session.MarkFailed` **มี production caller แล้ว 1 จุด** (`StartRedirectHandler` ขั้น 7);
  `Session.MarkExpired` **ยังไม่มี** (Non-Goal 4 — sweeper เป็นสเปกแยก) ถ้า audit เจอว่าเรียกจาก test
  เท่านั้น นั่นถูกต้องตามเจตนา.
- `Connection.EnsureEligible` มี production call site **2 จุด** ตามที่ REQ-3.6 ต้องการครบแล้ว
  (`CreateSessionHandler` + `StartRedirectHandler`).

### Files Changed

- `src/Modules/Payments/Payments.Application/StartRedirect/StartRedirectHandler.cs` — edited — ย้าย block
  resolve connection + `EnsureEligible` ขึ้นก่อน `BeginRedirect()`; ครอบ `CreateRedirectChargeAsync` ด้วย
  try/catch ที่ `MarkFailed` -> `PersistFailureAsync()` -> `throw`; เพิ่ม private helper
  `PersistFailureAsync()`; เติมย่อหน้าใน class XML doc ว่าลำดับขั้นเป็น contract. **ไม่แตะ** ลำดับ
  idempotent re-entry / status guard / claim / concurrency-loser และไม่แตะ signature ใด ๆ.
- `tests/Payments.Tests/Fakes.cs` — edited — 3 อย่าง: `FakePspAdapter.OnCreateCharge`
  (`Func<Session, PspCharge>?`, null = throw `NotSupportedException` เหมือนเดิม), `FakeUnitOfWork.SaveFails`
  (`Func<int, Exception?>?` รับเลข save 1-based), และ class ใหม่ `FakeVaultSecretStore` ที่นับ `Reveals`
  (member อื่นทั้งหมด throw). **เพิ่มสมาชิกล้วน ไม่แก้พฤติกรรมเดิม** — `CreateSessionHandlerTests` 16 ตัวเดิม
  ยังเขียวโดยไม่ต้องแก้แม้บรรทัดเดียว.
- `tests/Payments.Tests/StartRedirectHandlerTests.cs` — created — 13 tests.
- `.ai/specs/captive-payment-alignment/tasks.md` — edited — flip task 3 + Evidence.
- `.ai/specs/captive-payment-alignment/HANDOFF.md` — edited — section นี้.

### Important Decisions

1. **save ของ `MarkFailed` อยู่ใน helper `PersistFailureAsync()` ที่ใช้ `CancellationToken.None` และกลืน
   exception ของตัวเอง.** เหตุผลสองชั้น: (ก) ต้นเหตุเดิม (PSP ปฏิเสธ) ต้องเป็นคำตอบที่ caller เห็น ไม่ใช่
   "DB พัง" — ถ้าปล่อย exception ของ save ทะลุ status code จะเปลี่ยนและสาเหตุจริงหาย; (ข)
   `CancellationToken.None` เพราะเคสที่ REQ-7.2 สำคัญที่สุดคือ caller ที่ยกเลิกกลางทาง (ลูกค้าปิดแท็บ) —
   charge throw `TaskCanceledException` แล้ว save ใต้ token เดิมที่ cancel แล้วจะล้มทันที -> ได้ session ค้าง
   `Redirected`+`RedirectUrl == null` ตรงกับสิ่งที่ห้ามเป๊ะ. **ผลข้างเคียงที่ยอมรับ:** ถ้า store ปฏิเสธ write
   จริง ๆ session จะยังค้าง `Redirected` (นั่นคือ DB-availability incident ไม่ใช่การตัดสินใจของ handler).
2. **catch `Exception` กว้าง ๆ (ไม่ filter ชนิด) รอบ `CreateRedirectChargeAsync`** — ทุกความล้มเหลวของการสร้าง
   charge คือ "attempt นี้จบแล้ว" รวมถึง cancellation. เหตุผลที่ไม่กลัว double-charge: adapter คืน **hosted
   redirect URL** และเราคืน URL ให้ลูกค้าเฉพาะตอนสำเร็จ -> charge ที่กำพร้าที่ PSP ไม่มีใครเปิดจ่ายได้.
3. **ไม่แตะ endpoint / `ProducesProblem`** — status code ที่เป็นไปได้ของ `POST .../redirect` ไม่เปลี่ยน
   (404/409 เดิม) แค่ย้ายจุดที่ 409 เกิดให้มาก่อน claim.
4. **ไม่เพิ่ม test ที่ assert ตัวสตริง `reason`** — เป็นไปไม่ได้: `Session.MarkFailed` **ไม่เก็บ `reason`**
   ไว้ที่ใดเลย (validate ว่าไม่ว่างแล้วทิ้ง). ดู "ข้อค้นพบ" ข้างล่าง.

### Constraints (เพิ่มจาก section 0/1/2 — ยังใช้ทุกข้อ)

- **`connection` ถูก resolve ก่อน claim แล้ว** และตัวแปรมีชีวิตยาวถึงจุดเรียก charge -> **task 5 ส่ง
  `connection.Id` เข้า `CreateRedirectChargeAsync` ได้ทันทีโดยไม่ต้อง query ซ้ำ** (design D7 อ้าง
  `StartRedirectHandler.cs:83` ซึ่งเป็นเลขบรรทัดเก่า — บรรทัดขยับแล้ว ให้หาที่
  `adapter.CreateRedirectChargeAsync` ใน try block).
- **ลำดับ 8 ขั้นใน `StartRedirectHandler` ห้ามสลับ** — มี test pin 3 ชั้น: re-entry ตอบ**ก่อน** eligibility
  recheck (URL เดิมของลูกค้าที่อยู่หน้า PSP ต้องไม่ถูกเพิกถอนเพราะ admin ปิด connection ทีหลัง), eligibility
  ปฏิเสธ**ก่อน** claim + ก่อน vault, charge ล้ม -> `Failed` ก่อน rethrow.
- **`FakePspAdapter` ที่ไม่ตั้ง `OnCreateCharge` จะ throw `NotSupportedException`** — test เส้นทางปฏิเสธต้อง
  `Assert.ThrowsAsync<InvalidOperationException>` (ระบุชนิด) ไม่ใช่ `ThrowsAnyAsync<Exception>` ไม่งั้นการที่
  handler หลุดไปเรียก charge จะ "ผ่าน" เงียบ ๆ.
- **task 5 ที่เปลี่ยน signature `CreateRedirectChargeAsync` และ task 6 ที่เปลี่ยน return type
  `FetchChargeAsync` ต้องแก้ `FakePspAdapter` ด้วย** (เป็น fake ตัวเดียวในโค้ดเบส) — `OnCreateCharge` เป็น
  `Func<Session, PspCharge>` ไม่ผูกกับ argument ใหม่ จึงไม่ต้องแก้ signature ของ hook.

### Tests Run

- `dotnet build pol-core.slnx -warnaserror` -> `ok dotnet build: 64 projects, 0 errors, 0 warnings`
  (baseline ก่อนแก้: เหมือนกันเป๊ะ).
- baseline ก่อนแก้ (ยืนยันเองซ้ำ): `dotnet test pol-core.slnx --filter "Category!=Integration"` -> exit 0,
  **1152 passed / 0 failed** ตรงกับ section 2.
- `dotnet test tests/Payments.Tests` -> **119 passed / 0 failed** (106 -> +13).
- **RED proof**: `git stash push -- src/.../StartRedirectHandler.cs` แล้วรันเฉพาะคลาสใหม่ ->
  `Failed: 6, Passed: 7, Total: 13` (6 ที่แดง = ทุกเกณฑ์ใหม่ของ task นี้; 7 ที่เขียว = พฤติกรรมเดิมที่ห้ามถอย)
  -> `git stash pop` -> build -> 119 passed.
- `dotnet test pol-core.slnx --filter "Category!=Integration"` -> exit 0, **1165 passed / 0 failed** across
  16 projects. **baseline ใหม่ของ task 4+:** Admins 95 · Architecture 220 · BuildingBlocks 43 · Carts 15 ·
  Checkouts 7 · Divisions 6 · Hosts 344 · Iam 62 · Levels 6 · Merchants 115 · Offices 6 · Orders 68 ·
  Payments **119** · Positions 6 · Products 7 · SharedKernel 46 = **1165**.
- `bash scripts/check-rename-identifiers.sh` -> OK (รันหลัง `git add` ตาม trap 12).
- `bash scripts/spec-trace.sh captive-payment-alignment` -> OK 42 เกณฑ์ (รันซ้ำหลังแก้ tasks.md).
- **ไม่ได้รัน:** integration tests (task 3 ไม่แตะ DDL/DB — index เป็นของ task 4), `docker compose config`
  (ไม่แตะ compose), migration (ไม่มี DDL เปลี่ยน).

### ข้อค้นพบที่ต้องให้ lead ตัดสิน (ไม่ได้แก้ในงานนี้)

- **`Session.MarkFailed(reason, ...)` ทิ้ง `reason` ทั้งดุ้น** (`Session.cs:157-167`): validate ว่าไม่ว่าง
  แล้วไม่เก็บลง field/column ใดเลย. ผลดี = ข้อห้าม "reason ห้ามมี secret" ปลอดภัยโดยโครงสร้าง; ผลเสีย = ไม่มี
  ใครอ่านได้ว่าทำไม session ล้ม (ops ต้องเดาจาก log ของ HTTP layer) และ **ไม่มีทาง unit-test ค่า reason ได้เลย**.
  การเก็บจริงต้องเพิ่ม column + migration -> อยู่นอก scope task 3 และไม่มี REQ รองรับ. เสนอ: ตัดสินตอน task 4
  (ที่มี migration อยู่แล้ว) หรือปล่อยเป็น gap ที่บันทึกใน task 7.

### กับดักใหม่ที่เจอ (เพิ่มจาก traps 1-18)

19. **test ที่พิสูจน์ "ลำดับ" เป็น false green ได้ง่ายที่สุดในสเปกนี้** — assertion ว่า "throw" อย่างเดียวผ่านทั้ง
    บนโค้ดเก่าและใหม่ (โค้ดเก่าก็ throw เหมือนกัน แค่ throw **หลัง** claim). สิ่งที่แยกสองโลกออกจากกันคือ
    **side effect**: `session.Status`, `RedirectUrl`, `vault.Reveals`, `SaveCount`. วิธียืนยันว่า test กัดจริง
    ที่ถูกและถูกที่สุด: `git stash push -- <ไฟล์ production ไฟล์เดียว>` -> รันเฉพาะคลาสใหม่ -> ต้องเห็นแดง ->
    `git stash pop` -> build -> รันซ้ำ. (ทำแล้วได้ 6 แดง/7 เขียว ซึ่งยังบอกอีกว่า 7 ตัวนั้นเป็น regression net
    ของพฤติกรรมเดิมจริง ๆ ไม่ใช่ test ที่เขียนตามโค้ดใหม่.)
20. **`git stash push -- <path>` ปลอดภัยกับ staged file ในงานนี้ แต่ pop ต้องตามด้วย `dotnet build`** — dll ที่
    ค้างอยู่คือของโค้ดเก่า (RED run เพิ่ง build ไป) และ `--no-build` หลัง pop จะรัน binary เก่าเงียบ ๆ = false
    green คลาสเดียวกับ trap 15.
21. **`catch (Exception)` เปล่า ๆ ไม่เป็น warning ใต้ `-warnaserror`** — repo ไม่ได้เปิด CA analyzer
    (`Directory.Build.props` มีแค่ `TreatWarningsAsErrors`), CA1031 เลย silent. ถ้าจะกลืน exception โดยตั้งใจ
    ต้องเขียนเหตุผลใน comment เอง ไม่มี gate ไหนบังคับให้.

### Next Recommended Agent

builder ตัวใหม่ (fresh context) สำหรับ task 4 — DDL/migration + offline model assertion + integration test.
ต้องมี Docker + SQL Server `:11433` พร้อมใช้ (task 1-3 ไม่ต้องพึ่งเลย task 4 พึ่งเต็มตัว) และต้องอ่าน trap 3
(migration timestamp), 4 (`.env.integration` ใน Bash call เดียวกัน), 9 (สอง `SessionConfiguration`), 16
(SQLite insert session ไม่ได้ -> offline proof ได้แค่ระดับ model metadata).

### Next Steps

1. อ่าน `.ai/shared/*` 5 ไฟล์ -> spec 3 ไฟล์ -> HANDOFF ทั้งไฟล์ (รวม section 3 นี้).
2. baseline: `dotnet build pol-core.slnx -warnaserror` + `dotnet test pol-core.slnx --filter
   "Category!=Integration"` ต้องได้ **1165 passed / 0 failed** (Payments 119) — ถ้าไม่ตรง หยุดแล้วรายงาน.
3. implement task 4 ตาม design D5 (named filtered unique index ทั้งสองไฟล์ + migration timestamp >
   `20260723160500` + apply กับ `:11433` จริง + `sys.indexes` output ลง Evidence).
4. flip `- [x] 4.` + `Evidence:` ใน Edit เดียว (trap 2/13) -> `git add` -> rename gate ซ้ำ (trap 12) ->
   commit -> append section 4.

---

## Section 4 — task 4 (from: Claude Opus 5 teammate, 2026-07-26)

### Task Summary

task 4 ของ spec `captive-payment-alignment`: DB floor "หนึ่ง open session ต่อ order" — named filtered unique
index ในทั้งสองไฟล์ `SessionConfiguration` + migration + apply กับ SQL Server จริง + offline model assertion +
integration test. ปิด REQ-2 (2.4, 2.5, 2.6). ไม่แตะ handler / adapter / config / docs เลย.

### Current Status

- task 4 **เสร็จ** — `- [x]` + `Evidence:` ใน tasks.md, commit บน `feat/captive-payment-alignment`.
- task 5-7 ยัง `- [ ]`. ยังไม่มี `PspOptions` / `WebhookUrlFor` / `paymentChannel` / `FetchChargeAsync` /
  provisioning / seed / docs ถูกแก้.
- **dev DB `:11433` ถูก migrate ไปข้างหน้าแล้ว** — head = `20260726151538_OneOpenPaymentSessionPerOrder`.
  ใครก็ตามที่ pull branch นี้แล้วใช้ DB ตัวเก่าอยู่ต้อง apply migration ก่อน ไม่งั้น integration test ใหม่แดง
  (และ `has-pending-model-changes` จะยังบอกว่าไม่มี diff เพราะมันเทียบ model กับ snapshot ไม่ใช่กับ DB).
- แถว `txn.PaymentSessions` ที่ integration test insert **ค้างอยู่ใน dev DB** (ลบไม่ได้ ดู trap 25) —
  ไม่กระทบ gate ใด แต่ถ้านับแถวด้วยมือแล้วเห็นเกิน 36 นั่นคือสาเหตุ.

### Files Changed

- `src/Modules/Payments/Payments.Infrastructure/Persistence/SessionConfiguration.cs` — edited — เพิ่ม
  `HasIndex(x => x.OrderId, "IX_PaymentSessions_OrderId_Open").IsUnique().HasFilter("[Status] IN (0, 1)")`
  ต่อท้าย `HasIndex(x => x.OrderId)` เดิม (ไฟล์ของ PolDbContext = migration owner).
- `src/Persistence/Persistence.MerchantRuntime/Payments/SessionConfiguration.cs` — edited — index ใบเดียวกัน
  เป๊ะ (runtime context).
- `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/20260726151538_OneOpenPaymentSessionPerOrder.cs`
  — created (generated) — `CreateIndex`/`DropIndex` เท่านั้น + คอมเมนต์อธิบายว่าทำไม 0/1 อยู่ในตัวกรองและ
  2601/2627 -> 409 อยู่แล้ว.
- `...Migrations/20260726151538_OneOpenPaymentSessionPerOrder.Designer.cs` — created (generated).
- `...Migrations/PolDbContextModelSnapshot.cs` — edited (generated) — +4 บรรทัด.
- `tests/Architecture.Tests/OpenSessionIndexTests.cs` — created — 3 tests (offline model proof, REQ-2.6).
- `tests/Integration.Tests/OpenSessionIndexIntegrationTests.cs` — created — 3 tests
  (`[Trait("Category","Integration")]`, raw SQL).
- `.ai/specs/captive-payment-alignment/tasks.md` — edited — flip task 4 + Evidence.
- `.ai/specs/captive-payment-alignment/HANDOFF.md` — edited — section นี้.

### Important Decisions

1. **ไม่เพิ่ม `InternalsVisibleTo("Integration.Tests")` เพื่อ assert `ConflictException` ตรง ๆ.**
   `Persistence.MerchantRuntime.csproj` ให้ grant แบบ**ระบุราย consumer พร้อมเหตุผล** และเขียนไว้ว่า
   `Persistence.Provisioning` คือ "the ONE narrow, design-sanctioned exception"; คอมเมนต์ที่
   `VaultAuditAppender.cs:29` ระบุตรง ๆ ว่า Integration.Tests **จงใจ** ไม่มี grant นั้น (สวีตนี้ขับ raw
   connection ล้วน). แทนที่จะเปิด boundary ที่ไม่มี REQ ข้อใดขอ integration test pin **เลข SQL error ที่
   translator ผูกอยู่จริง** (2627/2601) + **ชื่อ index ในข้อความ error** (กันไม่ให้ unique index อีกใบบน
   ตารางเดียวกันถูกนับเป็นผ่าน). hop สุดท้าย -> `ConflictException` -> 409 เป็นโค้ดเดิมที่ไม่ถูกแตะ.
   **นี่คือ deviation ข้อเดียวที่ต้องให้ lead ตัดสิน** (บันทึกใน Evidence deviations ข้อ 1 ด้วย).
2. **offline proof มี 3 fact ไม่ใช่ 2** — เพิ่ม `The_plain_OrderId_lookup_index_survives_in_both_contexts`
   เพราะกับดัก "named overload" มี failure mode ที่ต่างจาก "ใส่ไฟล์เดียว": ถ้าใช้ overload ผิด index ใหม่จะ
   **ไม่เกิดใบใหม่** แต่ไป**แปลง lookup index เดิมให้ unique** ซึ่ง assertion ที่มองแค่ index ชื่อใหม่จับไม่ได้เลย.
   พิสูจน์แล้วว่าเกิดจริง (RED proof ข้อ ข).
3. **catalog assertion ใช้ `SaConn`** — `filter_definition` อ่านกลับเป็น NULL สำหรับ `pol_app` (trap 23).
   assertion นี้ถามว่า "DDL ที่ apply แล้วหน้าตาอย่างไร" ไม่ใช่ "runtime principal เห็นอะไร".
4. **ไม่ reset dev DB** — ตรวจก่อนว่าไม่มี order ใดถือ open session ซ้ำ (`GROUP BY OrderId HAVING
   COUNT(*) > 1` -> 0 rows) จึงไม่เข้าเงื่อนไข Risk 3 ของ design; `docker compose down -v` จะเป็นการทำลาย
   state ที่ไม่จำเป็น.

### Constraints (เพิ่มจาก section 0/1/2/3 — ยังใช้ทุกข้อ)

- **ห้ามเพิ่ม `HasIndex(x => x.OrderId)` (ไม่มีชื่อ) ในไฟล์ใดของ `Session` อีก** — จะไป mutate ทั้ง lookup
  index เดิมหรือชนกับ index ใหม่. index ใหม่ทุกใบบน property-set นี้ต้องใช้ overload ที่มีชื่อ.
- **สองไฟล์ `SessionConfiguration` ต้องเหมือนกันเรื่อง index เสมอ** — `OpenSessionIndexTests` บังคับเฉพาะ
  index ของ task นี้; ถ้า task ถัดไปเพิ่ม index/คอลัมน์ ต้องใส่ทั้งสองไฟล์เองเหมือนกัน (ไม่มี gate ทั่วไป
  ที่เทียบสองไฟล์นี้ทั้งใบ).
- **migration head ขยับแล้ว** — migration ใบถัดไป (ถ้ามี) ต้อง timestamp > `20260726151538` ไม่ใช่
  `20260723160500` อีกต่อไป.
- **ห้ามลบ/ย่อ filter `[Status] IN (0, 1)`** — `Paid`(2)/`Failed`(3)/`Expired`(4) ต้องอยู่นอกตัวกรอง ไม่งั้น
  REQ-7.4 (retry) ตาย และ order ที่จ่ายสำเร็จแล้วจะบล็อกตัวเอง.

### Tests Run

- `dotnet build pol-core.slnx -warnaserror` -> `ok dotnet build: 64 projects, 0 errors, 0 warnings`
  (ยืนยัน compile จริงด้วย `stat -f "%m %N"` เทียบ dll กับ source ตาม trap 15).
- baseline ก่อนแก้: `dotnet test pol-core.slnx --filter "Category!=Integration"` -> **1165 passed / 0 failed**
  ตรงกับ section 3.
- `dotnet test tests/Architecture.Tests` -> **223 passed / 0 failed** (220 -> +3).
- RED proof 2 ทาง (stash ไฟล์ runtime -> 1 แดง; เปลี่ยนเป็น unnamed overload -> 2 แดง รวม lookup-index test)
  — รายละเอียดใน Evidence ของ tasks.md.
- `source .env.integration && dotnet test pol-core.slnx --filter "Category=Integration"` ->
  **47 passed / 0 failed** (44 -> +3). ใบที่สองที่ยัง open ถูกปฏิเสธด้วย **SQL 2601** พร้อมชื่อ index.
- `dotnet test pol-core.slnx --filter "Category!=Integration"` -> exit 0, **1168 passed / 0 failed** across
  16 projects. **baseline ใหม่ของ task 5+:** Admins 95 · Architecture **223** · BuildingBlocks 43 · Carts 15 ·
  Checkouts 7 · Divisions 6 · Hosts 344 · Iam 62 · Levels 6 · Merchants 115 · Offices 6 · Orders 68 ·
  Payments 119 · Positions 6 · Products 7 · SharedKernel 46 = **1168**. Integration.Tests = **47** (แยก filter).
- `dotnet ef migrations has-pending-model-changes --context PolDbContext` -> ไม่มี diff.
- `bash scripts/check-migration-lineage.sh` -> OK (ต้องมี DB + `POL_DESIGN_SQL`).
- `bash scripts/check-rename-identifiers.sh` -> OK (หลัง `git add`, trap 12);
  `bash scripts/spec-trace.sh captive-payment-alignment` -> OK 42 เกณฑ์; loop ทุก spec -> ไม่มีแดง;
  `SECRET_GUARD_SKIP='' .ai/bin/check-secrets.sh --all` -> exit 0.
- **ไม่ได้รัน:** `docker compose config` (ไม่แตะ compose — เป็นของ task 5).

### กับดักใหม่ที่เจอ (เพิ่มจาก traps 1-21)

22. **`HasIndex(x => x.OrderId)` ครั้งที่สอง mutate index เดิมจริง — ยืนยันด้วยการทดลอง ไม่ใช่ทฤษฎี**: แทน
    named overload ด้วย unnamed แล้วรัน -> `The_plain_OrderId_lookup_index_survives_in_both_contexts` แดงด้วย
    `Assert.False() Failure` (lookup index กลายเป็น `IsUnique == true` + มี filter) และ **ไม่มี** index ชื่อ
    `IX_PaymentSessions_OrderId_Open` เกิดขึ้นเลย. ผลลัพธ์ที่ร้ายกว่าคือ DDL จะ "ทำงาน" แต่ทุก read ธรรมดา
    ตาม order วิ่งบน unique+filtered index แทน. **assertion ที่มองแค่ index ใบใหม่จับเคสนี้ไม่ได้** — ต้อง
    assert ใบเดิมด้วย.
23. **`sys.indexes.filter_definition` ถูก mask จาก `pol_app`** — SQL Server metadata-visibility ปิด
    definition column ให้ principal ที่มีแค่ SELECT/INSERT/UPDATE: อ่านกลับ **NULL** (ได้
    `SqlNullValueException: Data is Null` ในรอบแรกจริง) ขณะที่ `is_unique`/`has_filter` ผ่านปกติ. งานตรวจ
    DDL-level ใน Integration.Tests ให้ใช้ `IntegrationDb.SaConn`; งานตรวจ runtime behaviour ใช้ `AppConn`.
24. **EF ตั้ง migration timestamp เป็น UTC ไม่ใช่เวลาเครื่อง** — local 2026-07-26 22:15 ICT ออกมาเป็น
    `20260726151538`. ถ้าคาดชื่อไฟล์จากเวลาท้องถิ่นจะหาไฟล์ไม่เจอ และถ้ารันช่วงหัวค่ำใกล้เที่ยงคืน UTC
    วันที่ในชื่อไฟล์จะเป็น "เมื่อวาน" ของเวลาไทย — กติกา "timestamp ต้อง > X" ยังผ่านอยู่ แต่ต้องอ่านค่าจริงจาก
    `ls` ไม่ใช่เดา.
25. **`pol_app` ไม่มี grant `DELETE` บน `txn.PaymentSessions`** (มีแค่ SELECT/INSERT/UPDATE — ตรวจจาก
    `sys.database_permissions`) -> integration test ที่ insert แถวไว้ **cleanup เองไม่ได้**. ใช้
    `Guid.NewGuid()` ต่อรอบแล้วปล่อยแถวค้าง (pattern เดียวกับ `OrderSummaryReaderIntegrationTests`);
    `assert-fresh-db.sql` ไม่นับแถวของตารางนี้จึงไม่แตก. ถ้า task ถัดไปต้องลบจริงต้องใช้ `SaConn`.
26. **zsh: `status` เป็น read-only variable** — ก็อป loop จาก `ci.yml` (`status=0; ... || status=1`) มารัน
    local จะได้ `(eval):1: read-only variable: status` แล้ว **ทั้งคำสั่งตาย** โดยที่ผลของคำสั่งก่อนหน้าใน
    บรรทัดเดียวกันดูเหมือน exit 1 (ผมอ่านเป็น "secret scan แดง" ไปหนึ่งรอบ ทั้งที่มันเขียว). ใช้ชื่ออื่น
    (`bad`, `rc`) เมื่อรัน CI snippet ใน zsh.
27. **`hook-bypass-guard.sh` block คำสั่งที่มี `SECRET_GUARD_SKIP` แม้ตั้งเป็นค่าว่าง** ซึ่งเป็นรูปแบบที่
    `ci.yml` ใช้เองเพื่อ **force-clear** ตัวแปร (`env: SECRET_GUARD_SKIP: ''`). ก็อปคำสั่งจาก CI มารัน local
    จะโดน block แล้ว **ทั้ง compound command ตาย** (LESSONS: PreToolUse block ฆ่าทั้งก้อน — `git add` ที่มัด
    มาในก้อนเดียวกันไม่รัน). รัน `.ai/bin/check-secrets.sh --all` เปล่า ๆ พอ.
28. **`grep --include=...` ยัง fail ใต้ zsh แม้ผ่าน `rtk proxy`** (trap 14 ครอบแค่บางเคส) — `rtk proxy grep
    -rn ... --include=*.cs` ให้ `no matches found: --include=*.cs` เพราะ zsh ขยาย glob ก่อน. ใส่ quote
    (`--include="*.cs"`) หรือเลี่ยง flag นี้ไปเลย (`grep -rn pat dir | grep -v obj`).

### ข้อค้นพบที่ต้องให้ lead ตัดสิน (ไม่ได้แก้ในงานนี้)

- **REQ-2.5 ถูกพิสูจน์ถึงระดับ "SQL คืน 2601 + ชื่อ index ถูกต้อง" ไม่ใช่ "ได้ `ConflictException` จริง"** —
  เหตุผลเชิง boundary อยู่ใน Important Decisions ข้อ 1. ถ้า lead ต้องการ end-to-end จริง งานคือ: เพิ่ม
  `ProjectReference` + `InternalsVisibleTo("Integration.Tests")` เข้า `Persistence.MerchantRuntime` แล้ว
  ประกอบ `MerchantRuntimeDbContext` + `MerchantRuntimeUnitOfWork` ใน Integration.Tests ด้วย fake ของ
  `IActorContext`/`IWriteAuthorizer`/`ISecurityTelemetry` (Architecture.Tests มี `FakeActorContext`/
  `FakeWriteAuthorizer` ให้ลอก) — ประมาณ 1 ไฟล์ fakes + 1 test, แต่เป็นการเปิด boundary ที่ csproj เขียนว่า
  sanction ราย consumer.
- **`Session.MarkFailed` ยังทิ้ง `reason`** (ข้อค้นพบของ section 3) — task 4 มี migration อยู่ในมือแล้วแต่
  **ไม่ได้เพิ่ม column** ให้ เพราะไม่มี REQ รองรับ และ migration ของ task นี้ต้องเป็น DDL ใบเดียวที่ trace
  กลับไป REQ-2.4 ได้ (การพ่วง column ที่ไม่มี REQ = scope creep ที่ตรวจสอบย้อนกลับไม่ได้). ยังเปิดอยู่ ->
  task 7 บันทึกเป็น gap หรือสเปกแยก.

### Next Recommended Agent

builder ตัวใหม่ (fresh context) สำหรับ task 5 — ใหญ่สุดที่เหลือ: `PspOptions`/`WebhookUrlFor`/signature ของ
`CreateRedirectChargeAsync` (13 positional call ใน adapter tests) + `paymentChannel` mapping + config surface
4 ที่ (`appsettings.json`, `ProvisioningGuards`, `docker-compose.prod.yml`, `.env.prod.example`) + env ของ CI
**สองไฟล์** (`.github/workflows/ci.yml:118-126` และ `.gitlab-ci.yml:150-163`). ข้อเท็จจริงที่ lead ตรวจไว้ให้
แล้วอยู่ใน section 1b — อย่าหาซ้ำ. `FakePspAdapter` ใน `tests/Payments.Tests/Fakes.cs` เป็น fake ตัวเดียวใน
โค้ดเบสที่ต้องแก้ตาม signature ใหม่.

### Next Steps

1. อ่าน `.ai/shared/*` 5 ไฟล์ -> spec 3 ไฟล์ -> HANDOFF ทั้งไฟล์ (รวม section 4 นี้).
2. baseline: `dotnet build pol-core.slnx -warnaserror` + `dotnet test pol-core.slnx --filter
   "Category!=Integration"` ต้องได้ **1168 passed / 0 failed** (Architecture 223) — ถ้าไม่ตรง หยุดแล้วรายงาน.
3. implement task 5 ตาม design D7 (ห้าม `ValidateOnStart`; placeholder ใน `appsettings.json` +
   `ProvisioningGuards` เฉพาะ non-Development; ลบ `PSP_TWOCTWOP_BACKEND_RETURN_URL` ให้ครบทุกที่ รวม CI
   สองไฟล์ ไม่งั้น compose render check แดง — trap 5).
4. flip `- [x] 5.` + `Evidence:` ใน Edit เดียว (trap 2/13) -> `git add` -> rename gate ซ้ำ (trap 12) ->
   commit -> append section 5.
