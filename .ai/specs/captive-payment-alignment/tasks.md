# Implementation Tasks: Captive Intra-Group Payment Alignment

> Status: approved-for-implementation 2026-07-26 (delegated autonomous run — PENDING HUMAN REVIEW
> 2026-07-27; see requirements.md "Gate note")

> Each task is a cohesive, independently verifiable slice. Implement a whole task in one pass (it may
> touch many files). Decompose into sub-steps yourself at execution time — do NOT pre-split tasks here.

> Branch: `feat/captive-payment-alignment` (created off `develop`). ห้าม push `develop`, ห้าม merge,
> ห้าม `--no-verify`. commit ต่อ task. PR เปิดโดย lead ตอนท้าย.

## Traps ที่ยืนยันแล้ว (อ่านก่อนเริ่มทุก task)

1. **rename gate** — `scripts/check-rename-identifiers.sh` แดงถ้ามี token เกษียณโผล่เป็น identifier:
   `MerchantUser`, `PlatformUser`, `AdminRole`, `AdminPermission`, `PaymentSession`, `CartItem`,
   `CheckoutSession`, `PspConnection`, `MasterData*`, `OrderLine`, **`Line` เปล่า ๆ**. word-bounded
   (`PaymentSessionId` ผ่าน). string literal + comment ถูก strip ก่อน match.
2. **task gate** — flip `- [ ]` -> `- [x]` **และ** เขียน `Evidence:` ของ task นั้นใน **Edit เดียวกัน**.
   บรรทัด `Evidence:` ต้องไม่มี `-` นำหน้า (มี `-` = gate มองไม่เห็น -> block).
3. **migration timestamp** ต้อง > `20260723160500` ที่
   `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/` ไม่งั้น seed/DDL ก่อนหน้า
   ถูก revert เงียบ ๆ. ห้าม `dotnet ef database update` แบบไม่ระบุ target บน dev DB.
4. **integration tests** — `source .env.integration` ใน **Bash call เดียวกัน** กับ `dotnet test`;
   DB = container ของ repo นี้ที่ `:11433` (`:11434` เป็น orphan เก่า ห้ามใช้).
5. **compose required var ใหม่** — `${VAR:?...}` ใน `docker-compose.prod.yml` ต้องมี placeholder ใน env
   ของ CI job ที่รัน `docker compose config` **ทุก workflow** (`.github/workflows/ci.yml` และ
   `.gitlab-ci.yml`) ไม่งั้น CI แดง.
6. **`.env*` ถูก deny ทั้ง Read/Edit และ raw Bash file utils** — `.env.prod.example` commit ได้และต้องแก้
   ตาม REQ-4.2; ถ้า tool ปฏิเสธ ใช้ git blob swap (`hash-object` + `update-index` + `checkout-index`).
7. **`dotnet build pol-core.slnx -warnaserror`** — warning เดียวก็แดง CI.
8. **`Payments.Domain` ห้าม reference Application/Infrastructure** — `PaymentMethods` อยู่ Domain ได้
   เพราะเป็น vocabulary ล้วน; `SupportedMethods` อยู่บน `IPspAdapter` ใน Application/Ports.
9. **`SessionConfiguration` ของ Payments มี 2 ไฟล์** — `Payments.Infrastructure/Persistence/` (PolDbContext
   + migration) และ `Persistence.MerchantRuntime/Payments/` (runtime). แก้ใบเดียว = DDL ไม่ตรง แต่ test เขียว.
10. **ห้าม `Find`/`FindAsync`** บน runtime context (`RuntimeContextFindBanTests`) และห้าม
    `IgnoreQueryFilters`/`ExecuteUpdate`/`ExecuteDelete`/raw SQL นอก allowlist (`BypassPrimitiveTests`).
11. **ไม่มี fake/test double ของ `IPspAdapter` ในโค้ด** — `tests/Payments.Tests` ยังไม่มีไฟล์ fakes เลย
    (เทียบ `tests/Carts.Tests/Fakes.cs` เป็นแบบ). adapter tests เรียก adapter จริงผ่าน `PspTestHttp`.

---

- [x] 1. **Vocabulary + eligibility guard + adapter capability (domain/ports, isolated)** — เพิ่ม
     `Payments.Domain.PaymentMethods` (const `Card`/`PromptPay`/`Installment` = `"card"`/`"promptpay"`/
     `"installment"`, `IsKnown`, `Normalize` ที่ trim+lower แล้ว throw `ArgumentException` ถ้าไม่รู้จัก);
     `Connection.EnsureEligible(string method)` ที่ throw **`InvalidOperationException`** (409) เมื่อ
     `!IsEnabled` หรือ `!Supports(method)` — guard เดียวที่ทั้งสอง flow เรียก (design D3); และ
     `IPspAdapter.SupportedMethods` (`IReadOnlySet<string>`) ที่ทั้ง 2 adapter คืน `{ PaymentMethods.Card }`
     ตามความสามารถจริงวันนี้ (design D2). **ยังไม่แก้ handler ใด ๆ ใน task นี้.**
     **Done** = domain/ports ล้วน + unit tests ครบทุก branch, solution build เขียว, suite เดิมไม่ถอย.
     Satisfies: REQ-3 (3.6), REQ-6 (6.1). Verify: `dotnet test tests/Payments.Tests` — case:
     `Normalize` กับ `"CARD"`/`" card "`/`"paypal"`/`""`/null; `EnsureEligible` กับ connection ที่ปิด,
     method ไม่อยู่ในลิสต์, method อยู่ในลิสต์ (ผ่าน); `SupportedMethods` ของทั้ง 2 adapter.
     บวก `dotnet build pol-core.slnx -warnaserror` + `bash scripts/check-rename-identifiers.sh`.
     Evidence:
       - test: `dotnet build pol-core.slnx -warnaserror` -> `ok dotnet build: 64 projects, 0 errors, 0 warnings`
         (baseline before any edit: identical — 64 projects, 0 errors, 0 warnings).
       - test: `dotnet test tests/Payments.Tests` -> 90 passed / 0 failed / 0 skipped (baseline 59 -> +31 new:
         20 `PaymentMethodsTests`, 9 `ConnectionEligibilityTests`, 1 per adapter capability test).
       - test: `dotnet test pol-core.slnx --filter "Category!=Integration"` -> exit 0, 1128 passed / 0 failed
         across 16 test projects. **Baseline (captured BEFORE any edit, for task 2+ to compare):** Admins 95,
         Architecture 215, BuildingBlocks 43, Carts 15, Checkouts 7, Divisions 6, Hosts 341, Iam 62, Levels 6,
         Merchants 115, Offices 6, Orders 68, Payments 59, Positions 6, Products 7, SharedKernel 46 = 1097
         passed / 0 failed. After: every project identical except Payments.Tests 59 -> 90 (no regression).
       - test: `bash scripts/check-rename-identifiers.sh` -> `OK — no retired identifier appears as a live-code
         token in src/ or tests/`. Rerun AFTER `git add` — the gate reads `git ls-files`, so the 3 new files were
         invisible to the first (pre-`add`) run.
       - test: `bash scripts/spec-trace.sh captive-payment-alignment` -> `OK: ... เกณฑ์ 42 ข้อ ถูกอ้างครบใน
         design.md และ tasks.md, EARS lint ผ่านทุกข้อ`.
       - viewports: n/a — logic-only (domain vocabulary + guard + capability declaration), no browser surface.
       - deviations: `SupportedMethods` เป็น **abstract บน `PspAdapterBase` + override ต่อ adapter** ไม่ใช่ default
         ค่า `{ card }` บน base (design D2 ให้เลือก). base default ใหญ่กว่า 3 บรรทัดที่ประหยัดได้: adapter ใหม่ที่
         honour card ไม่ได้จะ **inherit การเคลมว่าทำได้** เงียบ ๆ = silent substitution ที่ REQ-6 มีไว้กันโดยตรง;
         และ abstract ตรงกับที่ base ประกาศสมาชิก `IPspAdapter` อื่นทุกตัวเป็น abstract อยู่แล้ว.
         `ConnectionEligibilityTests` ตั้ง `IsEnabled = false` ผ่าน reflection (private setter) เพราะ `Connection`
         ไม่มี `Disable()` — สถานะนั้นเกิดได้ทางเดียวคือ EF materialise แถวที่ admin ปิดไว้; การเพิ่ม `Disable()`
         เป็น production method ที่ไม่มีผู้เรียกและอยู่นอก scope task นี้. ชื่อไฟล์/คลาสเป็น
         `ConnectionEligibilityTests` (ไม่ใช่ `PspConnectionTests`) เพราะ `PspConnection` เป็น retired token
         ของ rename gate (trap 1).

- [x] 2. **Create-session ตั้งราคาจาก Order + eligibility + capability + idempotent** — port
     `Payments.Application/Ports/IPayableOrderReader.cs` (+ record `PayableOrder`) และ impl
     `Persistence.MerchantRuntime/Payments/PayableOrderReader.cs` (`internal sealed`, `AsNoTracking`,
     **project scalar แล้วประกอบ `Money.Of` ใน memory** ห้าม project complex `Money`) + register;
     `ISessionRepository.GetOpenForOrderAsync` + impl (คืน session ที่ `Status is Created or Redirected`
     ของ order นั้น หรือ null); `CreateSessionCommand` ตัด `Amount` ออก; `CreateSessionHandler` ทำ 8 ขั้น
     **ตามลำดับใน design D4** (1 normalize method 400 -> 2 order 404 -> 3 ไม่ใช่ AwaitingPayment 409 ->
     4 ไม่มี connection 409 -> 5 `EnsureEligible` 409 -> 6 adapter ไม่ support 409 -> 7 open session:
     ช่องทางเดิม = **คืน id ใบเดิม 200**, ช่องทางต่าง = `ConflictException` 409 -> 8 `Session.Create`
     ด้วย `order.Amount`); endpoint + `CreatePaymentSessionRequest` ตัด `amount` ออกจาก wire contract
     พร้อมอัปเดต `.WithDescription` + `ProducesProblem(400/404/409)`; สร้างไฟล์ fakes ของ
     `tests/Payments.Tests` (ยังไม่มี — ดู trap 11). ห้ามใช้ `GetOrderDetailCommand` (เขียน reveal-audit
     + คืน PII). **Done** = ยอดที่เข้า session มาจาก order เท่านั้น และทุกเส้นทางปฏิเสธมี test.
     Satisfies: REQ-1 (1.1, 1.2, 1.3, 1.4, 1.5, 1.7), REQ-2 (2.1, 2.2, 2.3), REQ-3 (3.1, 3.2, 3.3, 3.4),
     REQ-6 (6.2). Depends on: 1. Verify: `dotnet test tests/Payments.Tests` (8 ขั้น: 400/404/409x4 +
     idempotent-return + happy path amount == order.Amount) + `dotnet test tests/Hosts.Tests`
     (wire contract ใหม่ + status code) + `dotnet test pol-core.slnx --filter "Category!=Integration"`
     (ไม่ถอย) + `dotnet build pol-core.slnx -warnaserror`.
     Evidence:
       - test: `dotnet build pol-core.slnx -warnaserror` -> `Build succeeded. 0 Warning(s) 0 Error(s)`
         (64 projects).
       - test: `dotnet test tests/Payments.Tests --no-build` -> 106 passed / 0 failed / 0 skipped
         (baseline 90 -> +16 `CreateSessionHandlerTests`: 3 method-vocabulary 400 cases, 404 order,
         409 order-not-awaiting, 409 no-connection, 409 disabled-connection, 409 method-not-enabled,
         409 adapter-cannot-honour, idempotent-return บน Created และบน Redirected, 409 ช่องทางต่าง
         (method ต่าง + psp ต่าง), Failed ไม่บล็อกใบใหม่, happy-path amount+currency == order,
         canonical method code).
       - test: `dotnet test tests/Architecture.Tests --no-build` -> 220 passed / 0 failed
         (baseline 215 -> +5 `PaymentPricingQueryTests`).
       - test: `dotnet test tests/Hosts.Tests --no-build` -> 344 passed / 0 failed
         (baseline 341 -> +3 `CreatePaymentSessionContractTests`).
       - test: `dotnet test pol-core.slnx --filter "Category!=Integration"` -> exit 0,
         **1152 passed / 0 failed** across 16 test projects (baseline 1128). ทุก project เท่าเดิมยกเว้น
         Payments 90->106, Architecture 215->220, Hosts 341->344.
       - test: `bash scripts/check-rename-identifiers.sh` -> `OK — no retired identifier appears as a
         live-code token in src/ or tests/` (รันหลัง `git add` ตาม trap 12).
       - test: `bash scripts/spec-trace.sh captive-payment-alignment` -> `OK: ... เกณฑ์ 42 ข้อ ถูกอ้างครบใน
         design.md และ tasks.md, EARS lint ผ่านทุกข้อ`.
       - viewports: n/a — backend only (port + handler + wire contract), ไม่มี browser surface.
       - deviations: (1) เพิ่ม `tests/Architecture.Tests/PaymentPricingQueryTests.cs` (5 tests) นอกเหนือ
         รายการ test ของ task — การ project scalar ของ complex type `Money` แล้วประกอบ `Money.Of` ใน memory
         พังได้แค่ตอน EF **translate** ซึ่ง fake repository จับไม่ได้เลย; ไฟล์เดียวกันยัง pin REQ-1.2 ที่ชั้นที่
         บังคับจริง (query filter -> null สำหรับ order ของบริษัทอื่น ไม่ใช่ 403). (2) **semantics ของ
         `GetOpenForOrderAsync` ยังไม่ถูกพิสูจน์บน provider จริง** — `Session.RowVersion` map `IsRowVersion()`
         ซึ่ง SQLite generate ไม่ได้ -> EF ตัดคอลัมน์ออกจาก INSERT แล้วได้ `SQLite Error 19: NOT NULL
         constraint failed: PaymentSessions.RowVersion`, ดังนั้น harness offline insert session ไม่ได้เลย;
         test offline พิสูจน์ได้แค่ว่า query translate + รันผ่าน (คืน null บนตารางว่าง) ส่วน
         Created/Redirected = open และ Failed/Expired != open พิสูจน์ที่ระดับ handler + ต้องได้ proof บน SQL
         Server จริงใน task 4 (อยู่ใน Verify ของ task 4 แล้ว). (3) Hosts wire-pin bind ผ่าน
         `PspCodeJsonConverter` ของ host เอง (`"psp": "2c2p"`) ไม่ใช่ `JsonSerializerDefaults.Web` เปล่า ๆ
         เพื่อให้ตรงกับ request จริง.

- [x] 3. **Redirect: ปฏิเสธก่อน claim + `MarkFailed` เมื่อ charge ล้ม (liveness)** — เรียง
     `StartRedirectHandler` ใหม่ตาม design D6: ย้าย resolve connection (`InvalidOperationException` 409
     ถ้าไม่มี) + `connection.EnsureEligible(session.Method)` ขึ้นมา **ก่อน** `BeginRedirect()` (แก้บั๊กที่มี
     อยู่แล้ว: วันนี้ throw หลัง claim -> session ค้าง `Redirected` + `RedirectUrl == null` -> 409 ถาวร);
     ครอบ `CreateRedirectChargeAsync` ด้วย try/catch ที่ `session.MarkFailed(reason, now)` + save แล้ว
     **rethrow** (reason ห้ามมี secret); ห้ามให้ session จบ request ในสถานะ `Redirected` โดย `RedirectUrl`
     เป็น null. **Done** = คำขอที่ถูกปฏิเสธไม่เปลี่ยนสถานะเลย และ charge ที่ล้มทำให้ order เปิด session
     ใหม่ได้.
     Satisfies: REQ-3 (3.5), REQ-7 (7.1, 7.2, 7.3, 7.4). Depends on: 2. Verify:
     `dotnet test tests/Payments.Tests` — case: ineligible -> throw โดย status ยัง `Created` **และ
     `IVaultSecretStore` ไม่ถูกเรียกเลย** (fake ที่นับ call); adapter throw -> session `Failed` + save +
     exception ทะลุออก; fail-then-retry: หลัง `Failed` เรียก `CreateSessionHandler` ของ order เดิมได้ id
     ใหม่ (พิสูจน์ทั้งเส้น ไม่ใช่ประกอบ session `Failed` ในหน่วยความจำ) + `dotnet build pol-core.slnx
     -warnaserror`.
     Evidence:
       - test: `dotnet build pol-core.slnx -warnaserror` -> `ok dotnet build: 64 projects, 0 errors, 0 warnings`
         (baseline ก่อนแก้: เหมือนกันเป๊ะ).
       - test: `dotnet test tests/Payments.Tests` -> 119 passed / 0 failed / 0 skipped (baseline 106 -> +13
         `StartRedirectHandlerTests`: unknown session 404; idempotent re-entry คืน URL เดิม; re-entry ตอบก่อน
         eligibility recheck (connection ถูกปิดทีหลัง URL เดิมยังใช้ได้); `Failed` เริ่ม redirect ใหม่ไม่ได้;
         ไม่มี connection / connection ปิด / method ที่ connection เลิกเปิด -> throw โดย status ยัง `Created`,
         `RedirectUrl` null, vault `Reveals == 0`, `SaveCount == 0`; concurrency loser คืน URL ผู้ชนะ; loser ที่
         ยังไม่มี URL -> retry-shortly; happy path bind charge ครั้งเดียว (`SaveCount == 2`, vault 1 ครั้ง);
         charge ล้ม -> `Failed` + `SaveCount == 2` + `Assert.Same` ว่า exception เดิมทะลุออก; save ของ
         `MarkFailed` ล้มเอง -> exception เดิมยังชนะ; fail-then-retry ทั้งเส้น create -> redirect ล้ม -> create
         ได้ id ใหม่).
       - test: **RED proof ว่า test ชุดใหม่กัดจริง** — `git stash push -- <StartRedirectHandler.cs>` แล้ว
         `dotnet test tests/Payments.Tests --filter "FullyQualifiedName~StartRedirectHandlerTests"` ->
         `Failed! - Failed: 6, Passed: 7, Total: 13`. 6 ที่แดงบนโค้ดเก่า =
         `A_missing_connection_is_refused_before_anything_is_claimed`,
         `A_connection_disabled_between_create_and_redirect_is_refused_before_anything_is_claimed`,
         `A_method_the_connection_stopped_enabling_is_refused_before_anything_is_claimed`,
         `A_charge_the_psp_refuses_fails_the_session_and_rethrows`,
         `Failing_to_record_the_failure_does_not_hide_the_psp_refusal`,
         `A_failed_charge_lets_the_same_order_open_a_fresh_session`. 7 ที่เขียวทั้งสองฝั่ง = พฤติกรรมเดิมที่
         task นี้ห้ามทำถอย (404, re-entry x2, terminal status, concurrency loser x2, happy path).
         `git stash pop` แล้ว build + รันซ้ำ -> 119 passed.
       - test: `dotnet test pol-core.slnx --filter "Category!=Integration"` -> exit 0, **1165 passed / 0 failed**
         across 16 test projects (baseline 1152). ทุก project เท่าเดิมยกเว้น Payments 106->119: Admins 95 ·
         Architecture 220 · BuildingBlocks 43 · Carts 15 · Checkouts 7 · Divisions 6 · Hosts 344 · Iam 62 ·
         Levels 6 · Merchants 115 · Offices 6 · Orders 68 · Payments 119 · Positions 6 · Products 7 ·
         SharedKernel 46.
       - test: `bash scripts/check-rename-identifiers.sh` -> `OK — no retired identifier appears as a live-code
         token in src/ or tests/` (รันหลัง `git add` ตาม trap 12).
       - test: `bash scripts/spec-trace.sh captive-payment-alignment` -> `OK: ... เกณฑ์ 42 ข้อ ถูกอ้างครบใน
         design.md และ tasks.md, EARS lint ผ่านทุกข้อ`.
       - viewports: n/a — Application handler ล้วน ไม่มี browser surface (endpoint/wire contract ไม่ถูกแตะ).
       - deviations: (1) save ของ `MarkFailed` ใช้ `CancellationToken.None` และ **กลืน** exception ของตัวเอง
         (helper `PersistFailureAsync`) — ต้นเหตุเดิม (PSP) ชนะเสมอตามที่ brief สั่ง; เหตุผลที่ไม่ใช้ token ของ
         request: caller ที่ยกเลิกกลางทางคือเคสที่ REQ-7.2 สำคัญที่สุด แต่ save ใต้ token ที่ถูก cancel แล้วจะ
         ล้มทันที -> session ค้าง `Redirected`+null พอดี. repo ไม่มี `CancellationToken.None` ที่อื่นใน `src/`
         (ตรวจแล้ว) จึงเป็น idiom ใหม่ 1 จุด. (2) **`Session.MarkFailed` ทิ้ง `reason` ทั้งดุ้น** — ไม่มี column/
         field เก็บ (ดู `Session.cs:157-167`) มันเป็นแค่ argument ที่ถูก validate ว่าไม่ว่าง; ผลคือข้อห้าม
         "reason ห้ามมี secret" ปลอดภัยโดยโครงสร้าง (ไม่มีที่ให้รั่ว) แต่ก็ **ไม่มี test ที่ assert ค่า reason ได้**
         และ ops อ่านสาเหตุที่ล้มจากที่ไหนไม่ได้เลย — surface ไว้ให้ lead, ไม่แก้ในงานนี้ (ต้องมี column +
         migration = นอก scope task 3). (3) เพิ่ม hook 2 ตัวบน fakes ของ task 2 แทนสร้างไฟล์ใหม่ (ตามที่
         section 2 สั่ง): `FakePspAdapter.OnCreateCharge` และ `FakeUnitOfWork.SaveFails(saveNumber)`; บวก
         `FakeVaultSecretStore` ที่นับ `Reveals` (ยังไม่มีที่ใดใน `tests/` fake `IVaultSecretStore` เลย).

- [x] 4. **DB floor: หนึ่ง open session ต่อ order** — เพิ่ม named filtered unique index
     `builder.HasIndex(x => x.OrderId, "IX_PaymentSessions_OrderId_Open").IsUnique()
     .HasFilter("[Status] IN (0, 1)")` ใน **ทั้งสองไฟล์** `SessionConfiguration` (trap 9) — ต้องใช้
     overload ที่ตั้งชื่อ ไม่ใช่ `HasIndex(x => x.OrderId)` ซ้ำ (EF จะไป mutate index เดิมแล้ว lookup index
     หาย); migration ใหม่ (timestamp > `20260723160500`) แล้ว **apply กับ SQL Server จริงที่ `:11433`**
     พร้อม query `sys.indexes` ยืนยัน `is_unique` + `has_filter` + `filter_definition`; assertion ชั้น
     **offline** ใน `tests/Architecture.Tests` ว่า model ของทั้งสอง context มี index ชื่อนั้น unique +
     filter ตรงตัว (เพราะ CI ข้าม job integration เมื่อไม่มี secret); integration test ที่ insert session
     ใบที่สอง (open) ของ order เดิมแล้วได้ `ConflictException` ผ่าน translator เดิมใน
     `MerchantRuntimeUnitOfWork`; ยืนยันว่า session `Failed`/`Expired` ไม่ติด filter (retry เปิดใบใหม่ได้).
     **Done** = double-charge ปิดทั้งชั้น handler และชั้น DB โดยไม่ล็อก retry.
     Satisfies: REQ-2 (2.4, 2.5, 2.6). Depends on: 3. Verify: `dotnet test tests/Architecture.Tests` +
     `source .env.integration && dotnet test tests/Integration.Tests --filter Category=Integration` +
     `dotnet ef migrations has-pending-model-changes` -> ไม่มี diff + output `sys.indexes` จริงใน Evidence.
     Evidence:
       - test: `dotnet build pol-core.slnx -warnaserror` -> `ok dotnet build: 64 projects, 0 errors, 0 warnings`
         (dll mtime > source mtime ยืนยันว่า compile จริง ไม่ใช่ up-to-date skip ตาม trap 15).
       - migration: `20260726151538_OneOpenPaymentSessionPerOrder` (EF ตั้ง timestamp เป็น **UTC** — local
         22:15 ICT = 15:15 UTC; `20260726151538` > `20260723160500` ตาม trap 3). `Up` มีแค่ `CreateIndex`
         ใบเดียว, snapshot diff = **4 บรรทัด** (`git diff --stat` -> `1 file changed, 4 insertions(+)`) และ
         `b.HasIndex("OrderId")` เดิมยังอยู่เหนือมันในไฟล์เดียวกัน.
       - test: apply ด้วย target ที่ระบุชัด (ห้าม update เปล่า ตาม trap 3):
         `dotnet ef database update 20260726151538_OneOpenPaymentSessionPerOrder --context PolDbContext` ->
         `Applying migration '20260726151538_OneOpenPaymentSessionPerOrder'. Done.` (dev DB `:11433` container
         `pol-db`, head ก่อนหน้า = `20260723160500_GrantOrderItemPolicyTables`). ตรวจก่อน apply ว่าไม่มี order
         ที่มี open session ซ้ำ (`GROUP BY OrderId HAVING COUNT(*) > 1` -> 0 rows จาก 36 session) จึง **ไม่ต้อง**
         `docker compose down -v` (Risk 3 ของ design ไม่เกิด).
       - test: `sys.indexes` + `sys.index_columns` จริงหลัง apply (sqlcmd, `-W -s" | "`) —
         `IX_PaymentSessions_OrderId | 0 | 0 | NULL | OrderId | 0`;
         `IX_PaymentSessions_OrderId_Open | 1 | 1 | ([Status] IN ((0), (1))) | OrderId | 0`;
         `IX_PaymentSessions_Psp_PspExternalChargeId | 1 | 1 | ([PspExternalChargeId] IS NOT NULL) | Psp | 0`
         + `... | PspExternalChargeId | 0`; `PK_PaymentSessions | 1 | 0 | NULL | Id | 0`.
         (คอลัมน์ = `is_unique`, `has_filter`, `filter_definition`, `key_column`, `is_included_column`.)
         SQL Server รับ predicate `[Status] IN (0, 1)` จริงและ normalize เก็บเป็น `([Status] IN ((0), (1)))` —
         ไม่ต้อง rewrite. lookup index ธรรมดายังอยู่ครบ (`is_unique = 0`, ไม่มี filter).
       - test: `dotnet ef migrations has-pending-model-changes --context PolDbContext` ->
         `No changes have been made to the model since the last migration.` (exit 0) — Designer/snapshot ตรง.
       - test: `bash scripts/check-migration-lineage.sh` -> `Migration lineage gate OK — all 4 existing
         migration IDs discoverable via PolDbContext.`
       - test: `dotnet test tests/Architecture.Tests --filter "FullyQualifiedName~OpenSessionIndexTests"` ->
         3 passed / 0 failed (offline model proof, REQ-2.6: owner context, runtime context, และ lookup index
         ที่ห้ามถูก mutate); `dotnet test tests/Architecture.Tests` -> **223 passed / 0 failed** (baseline 220).
       - test: **RED proof ว่า offline assertion กัดจริง 2 ทาง** — (ก) `git stash push -- <runtime
         SessionConfiguration.cs>` -> build -> รันเฉพาะคลาสใหม่ -> `Failed: 1, Passed: 2` (ตัวที่แดง =
         `The_runtime_context_declares_the_identical_index`, `Single()` ไม่เจอ index) -> `git stash pop`.
         (ข) แทน named overload ในไฟล์ owner ด้วย `HasIndex(x => x.OrderId)` เปล่า -> build -> `Failed: 2,
         Passed: 1` โดยตัวที่แดงคือ `The_plain_OrderId_lookup_index_survives_in_both_contexts`
         (`Assert.False() Failure` = lookup index กลายเป็น unique จริง) + owner assertion —
         **ยืนยันว่ากับดัก "mutate index เดิม" ของ trap 9 เป็นเรื่องจริง ไม่ใช่ข้อสันนิษฐาน** แล้ว restore ไฟล์.
       - test: `source .env.integration && dotnet test tests/Integration.Tests --filter
         "FullyQualifiedName~OpenSessionIndexIntegrationTests"` -> 3 passed / 0 failed (Bash call เดียวกัน
         ตาม trap 4). ใบที่สองที่ยัง open (`Created` แล้วต่อด้วย `Redirected` ของ order เดิม) ถูกปฏิเสธด้วย
         **SQL 2601** พร้อมข้อความที่ระบุชื่อ `IX_PaymentSessions_OrderId_Open`; `Failed`+`Expired`+`Paid`
         ของ order เดียวกันอยู่ร่วมกันได้แล้วยังเปิด `Created` ใบใหม่ได้ (REQ-7.4).
       - test: `source .env.integration && dotnet test pol-core.slnx --filter "Category=Integration"` ->
         **47 passed / 0 failed** (Integration.Tests; baseline 44 -> +3).
       - test: `dotnet test pol-core.slnx --filter "Category!=Integration"` -> exit 0, **1168 passed /
         0 failed** across 16 test projects (baseline 1165). ทุก project เท่าเดิมยกเว้น Architecture 220->223:
         Admins 95 · Architecture 223 · BuildingBlocks 43 · Carts 15 · Checkouts 7 · Divisions 6 · Hosts 344 ·
         Iam 62 · Levels 6 · Merchants 115 · Offices 6 · Orders 68 · Payments 119 · Positions 6 · Products 7 ·
         SharedKernel 46.
       - test: `bash scripts/check-rename-identifiers.sh` -> `OK — no retired identifier appears as a live-code
         token in src/ or tests/` (รันหลัง `git add` ตาม trap 12); `bash scripts/spec-trace.sh
         captive-payment-alignment` -> OK 42 เกณฑ์; loop spec-trace ทุก spec ใต้ `.ai/specs/` -> ไม่มี spec ใดแดง;
         `.ai/bin/check-secrets.sh --all` -> exit 0 (รันแบบไม่มี env prefix — `hook-bypass-guard.sh` block
         คำสั่งที่มี `SECRET_GUARD_SKIP` แม้ตั้งเป็นค่าว่างแบบที่ CI ทำ).
       - viewports: n/a — DDL + model metadata + live-SQL assertions ล้วน ไม่มี browser surface.
       - deviations: (1) **integration test ไม่ assert `ConflictException` ในโปรเซส** — `MerchantRuntimeUnitOfWork`
         เป็น `internal sealed` และ `Persistence.MerchantRuntime.csproj` ให้ `InternalsVisibleTo` แบบระบุราย
         consumer พร้อมเหตุผล (Architecture.Tests / Persistence.Provisioning / Hosts.Tests — "the ONE narrow,
         design-sanctioned exception") โดย **Integration.Tests ตั้งใจไม่มี** (ระบุไว้ในคอมเมนต์ของ
         `VaultAuditAppender.cs:29` และ csproj ของ Integration.Tests เอง: สวีตนี้ขับ raw connection ล้วน).
         การเพิ่ม grant ใบที่ 4 = แก้ boundary ที่ design sanction ไว้ ซึ่งไม่มี REQ ข้อใดขอ. แทนที่จะทำอย่างนั้น
         test pin **เลข error ที่ translator ผูกอยู่จริง** (`ex.Number is 2627 or 2601` =
         `MerchantRuntimeUnitOfWork.IsUniqueViolation`) **บวกชื่อ index ในข้อความ error** เพื่อไม่ให้ unique index
         อีกใบบนตารางเดียวกัน (`Psp`,`PspExternalChargeId`) ถูกนับเป็นผ่าน; hop สุดท้าย 2627/2601 ->
         `ConflictException` -> 409 เป็นโค้ดเดิมที่ task นี้ไม่ได้แตะ. **ให้ lead ตัดสินว่าพอไหม** ถ้าต้องการ
         end-to-end จริงต้องเพิ่ม `InternalsVisibleTo` + fakes ของ 3 dependency ใน Integration.Tests.
         (2) test catalog-level (`filter_definition`) ต่อด้วย `IntegrationDb.SaConn` ไม่ใช่ `AppConn` —
         metadata-visibility ของ SQL Server mask `sys.indexes.filter_definition` (definition column) จาก
         principal ที่มีแค่ SELECT/INSERT/UPDATE: อ่านกลับเป็น NULL (`SqlNullValueException` ในรอบแรกจริง) ขณะที่
         `is_unique`/`has_filter` ผ่านปกติ. assertion นี้เป็นเรื่อง DDL ที่ apply แล้ว ไม่ใช่สิทธิ์ของ runtime
         principal จึงใช้ identity ของ DDL (เหตุผลเดียวกับที่ vault-audit applock tests ใช้ `sa`).
         (3) แถวที่ integration test insert **ค้างอยู่ใน dev DB** — `pol_app` ไม่มี grant `DELETE` บน
         `txn.PaymentSessions` (มีแค่ SELECT/INSERT/UPDATE) จึง cleanup ไม่ได้; ใช้ `Guid.NewGuid()` ต่อรอบ
         (pattern เดียวกับ `OrderSummaryReaderIntegrationTests`) และ `assert-fresh-db.sql` ไม่นับจำนวนแถวของ
         ตารางนี้เลย จึงไม่กระทบ gate ใด. (4) offline test สร้าง `PolDbContext` ด้วย `ModuleAssemblies` ที่มี
         **แค่ `Payments.Infrastructure`** (ไม่ใช่ 5-12 assembly เหมือน `MoneyColumnMappingTests`/
         `ModelDisjointnessTests`) — พอสำหรับ entity ที่ assert และ `EnableServiceProviderCaching(false)` กัน
         model-cache ปนกับ test class อื่นตามคอมเมนต์ที่ `MoneyColumnMappingTests` เขียนไว้แล้ว.

- [x] 5. **Webhook callback URL ต่อ connection + paymentChannel จาก method + config surface** —
     `PspOptions` เพิ่ม `PublicBaseUrl` และ **ลบ** `TwoCTwoPOptions.BackendReturnUrl`;
     `PspAdapterBase.WebhookUrlFor(Guid pspConnectionId)`; `IPspAdapter.CreateRedirectChargeAsync` รับ
     `Guid pspConnectionId` เพิ่ม (แก้ call site ตามรายการใน design D7 — **ไม่มี fake ให้หา**, แต่มี
     positional call ใน adapter tests 13 จุด); `TwoCTwoPAdapter` ใช้ `backendReturnUrl =
     WebhookUrlFor(pspConnectionId)` และ `paymentChannel` มาจาก `session.Method` ผ่าน mapping
     (`card -> "CC"`, อื่น -> throw ระบุ method) แทน hardcode `["CC"]`; `StartRedirectHandler` ส่ง
     `connection.Id`; **config (ทางที่ไม่ทำให้ test เดิมล้ม — design D7 ข้อ config):** `appsettings.json`
     เพิ่ม `"Psp": { "PublicBaseUrl": "" }` placeholder + fail fast **เฉพาะ non-Development** ผ่าน
     `ProvisioningGuards` ใน block `Program.cs:141-151` (ห้ามใช้ `ValidateOnStart` — จะทำให้ 17 ไฟล์ของ
     `tests/Hosts.Tests` ที่ boot host จริงล้มทั้งชุด); อัปเดต `docker-compose.prod.yml`,
     `.env.prod.example`, env ของ CI ที่รัน `docker compose config` ใน `.github/workflows/ci.yml`
     **และ** `.gitlab-ci.yml`; เพิ่ม test ที่ pin wire contract + ยืนยันยอดที่ adapter ประกอบ == ยอด order.
     **Done** = URL ที่ส่งให้ 2C2P พา `pspConnectionId` ไปจริง, ช่องทางไม่ถูก substitute, deployment
     non-Development ที่ไม่ตั้ง `Psp:PublicBaseUrl` ไม่ boot, และ suite เดิมยังเขียวเท่าเดิม.
     Satisfies: REQ-1 (1.6), REQ-4 (4.1, 4.2, 4.3, 4.4, 4.5, 4.6), REQ-6 (6.3, 6.4). Depends on: 4.
     Verify: `dotnet test tests/Payments.Tests` (2C2P claims: `backendReturnUrl`, `amount`,
     `paymentChannel`) + `dotnet test tests/Hosts.Tests` (`ProvisioningGuards` + wire pin, และนับผลว่า
     **ไม่ต่ำกว่าเดิม**) + `docker compose -f docker-compose.prod.yml config` ด้วย placeholder ชุด CI +
     `grep -rn PSP_TWOCTWOP_BACKEND_RETURN_URL` -> ไม่เหลือที่ใด + `dotnet build pol-core.slnx -warnaserror`.
     Evidence:
       - test: `dotnet build pol-core.slnx -warnaserror` -> `ok dotnet build: 64 projects, 0 errors, 0 warnings`
         (ยืนยัน compile จริงด้วย `stat -f "%m %Sm %N"` เทียบ dll กับ source ทั้ง 4 คู่ที่แก้ ตาม trap 15 —
         dll ใหม่กว่าทุกคู่).
       - test: baseline ก่อนแก้ (ยืนยันเองซ้ำ): `dotnet test pol-core.slnx --filter "Category!=Integration"` ->
         **1168 passed / 0 failed** ตรงกับ section 4 (Payments 119, Architecture 223, Hosts 344).
       - test: `dotnet test tests/Payments.Tests --no-build` -> **126 passed / 0 failed** (119 -> +7:
         `charges_the_amount_the_session_carries` (REQ-1.6),
         `points_the_backend_notification_at_the_connection_being_charged` (REQ-4.1 + REQ-4.4 pin ว่า
         `frontendReturnUrl` ยัง global), `does_not_double_the_slash_of_a_public_base_url`,
         `derives_the_payment_channel_from_the_session_method` (REQ-6.3),
         `refuses_a_method_it_cannot_honour_rather_than_substituting_a_card_channel` x2 theory (REQ-6.4),
         Omise `sends_no_callback_url_because_omise_takes_its_webhook_from_the_dashboard` (REQ-4.5)).
       - test: `dotnet test tests/Hosts.Tests --no-build` -> **353 passed / 0 failed** (344 -> +9 =
         6 fail-fast case + 3 pass case ของ `ProvisioningGuards.RequirePublicBaseUrl`) — **ไม่ต่ำกว่า 344**
         คือหลักฐานว่า config guard ไม่ทำ host พัง (REQ-4.6: 17 ไฟล์ที่ boot host จริงยังเขียวทั้งหมด,
         ทุกไฟล์ pin `UseEnvironment(Environments.Development)` — ตรวจแล้วไม่มีไฟล์ใดปล่อย default).
       - test: **RED proof ว่า assertion ชุดใหม่กัดจริง** — mutate `TwoCTwoPAdapter` กลับเป็นพฤติกรรมก่อน task
         (`paymentChannel = new[] { "CC" }` + `backendReturnUrl` เป็น URL คงที่แบบ global) -> build ->
         `dotnet test tests/Payments.Tests --no-build --filter "FullyQualifiedName~TwoCTwoPAdapterTests"` ->
         `Failed! - Failed: 4, Passed: 25, Total: 29`. 4 ที่แดง =
         `points_the_backend_notification_at_the_connection_being_charged`,
         `does_not_double_the_slash_of_a_public_base_url`,
         `refuses_a_method_it_cannot_honour_rather_than_substituting_a_card_channel` (promptpay + installment)
         -> restore ไฟล์ -> build -> 126 passed. (`derives_the_payment_channel...` เขียวทั้งสองฝั่งตามคาด
         เพราะ card -> "CC" เหมือนกัน — มันคือ regression net ของ mapping ไม่ใช่ proof ของการเปลี่ยน.)
       - test: `dotnet test pol-core.slnx --filter "Category!=Integration"` -> exit 0, **1184 passed /
         0 failed / 0 skipped** across 16 test projects. **baseline ใหม่ของ task 6+:** Admins 95 ·
         Architecture 223 · BuildingBlocks 43 · Carts 15 · Checkouts 7 · Divisions 6 · Hosts **353** ·
         Iam 62 · Levels 6 · Merchants 115 · Offices 6 · Orders 68 · Payments **126** · Positions 6 ·
         Products 7 · SharedKernel 46 = **1184**.
       - test: compose render ทั้ง 2 ที่ที่ CI รัน ด้วย placeholder ชุดเดียวกับ CI เป๊ะ (trap 5) —
         (ก) GitHub `ci.yml` job `docker-build`: `env PSP_PUBLIC_BASE_URL=https://ci-api.example.com
         MSSQL_SA_PASSWORD=ci-placeholder DB_SERVER=ci-db.example.internal
         MERCHANT_USER_OIDC_CLIENT_ID=ci-placeholder ADMIN_OIDC_CLIENT_ID=ci-placeholder
         MERCHANT_USER_FRONTEND_ORIGIN=https://ci.example.com ADMIN_FRONTEND_ORIGIN=https://ci-admin.example.com
         PSP_TWOCTWOP_FRONTEND_RETURN_URL=https://ci.example.com/return
         PSP_OMISE_RETURN_URI=https://ci.example.com/return docker compose -f docker-compose.prod.yml config -q`
         -> exit 0 (แบบไม่ `-q` เห็น `Psp__PublicBaseUrl: https://ci-api.example.com` และ **ไม่มี**
         `Psp__TwoCTwoP__BackendReturnUrl` เหลือ); (ข) GitLab `.gitlab-ci.yml`: ชุด env เดียวกัน +
         `REGISTRY_IMAGE`/`IMAGE_TAG` กับ `docker compose -f docker-compose.prod.yml -f
         docker-compose.registry.yml config -q` -> exit 0.
       - test: negative proof ว่า `${...:?}` ของ var ใหม่บังคับจริง — ถอด `PSP_PUBLIC_BASE_URL` ออกจากชุด
         placeholder แล้ว render -> exit 1 พร้อม `error while interpolating
         services.api.environment.Psp__PublicBaseUrl: required variable PSP_PUBLIC_BASE_URL is missing a
         value: set PSP_PUBLIC_BASE_URL in .env` (ยืนยันว่า CI จะแดงถ้าลืม placeholder — ไม่ใช่ผ่านเงียบ).
       - test: `git grep -n PSP_TWOCTWOP_BACKEND_RETURN_URL -- ':!.ai/specs' ':!docs'` -> เหลือ **1 บรรทัด**
         คือ comment เตือน ops ใน `.env.prod.example:45` (deviation 2); ไม่มี live config/โค้ดที่ใดอ้างถึงแล้ว.
         `git grep -n BackendReturnUrl` -> เหลือเฉพาะ spec ของงานนี้ + `.ai/specs/production-hardening/tasks.md`
         (Evidence ประวัติศาสตร์ของ PR เก่า ห้ามแก้).
       - test: `bash scripts/check-rename-identifiers.sh` -> `OK — no retired identifier appears as a live-code
         token in src/ or tests/` (รันหลัง `git add` ตาม trap 12); `.ai/bin/check-secrets.sh --all` -> exit 0
         (รันเปล่าไม่มี env prefix ตาม trap 27); `bash scripts/spec-trace.sh captive-payment-alignment` ->
         `OK: ... เกณฑ์ 42 ข้อ ถูกอ้างครบ ... EARS lint ผ่านทุกข้อ`.
       - viewports: n/a — backend + config/ops surface ล้วน (adapter request claims, boot guard, compose/CI env,
         runbook) ไม่มี browser surface ถูกแตะ.
       - deviations: (1) **guard บังคับ scheme `http`/`https` ไม่ใช่แค่ "absolute URI" ตามตัวอักษรของ REQ-4.3** —
         เจอตอน test แดงจริง: บน Unix `Uri.TryCreate("/api/v1", UriKind.Absolute, out _)` คืน **true** (ตีเป็น
         `file://`) ดังนั้นเกณฑ์ "absolute" ล้วนจะปล่อยค่าที่ PSP POST กลับมาไม่ได้เลยผ่าน boot. เพิ่มเงื่อนไข
         scheme = การ implement เจตนาของ REQ-4.3 ให้ถูก ไม่ใช่ขยาย scope (ค่าที่ REQ อยากบล็อกคือค่าที่ใช้ไม่ได้).
         (2) **คงคำว่า `PSP_TWOCTWOP_BACKEND_RETURN_URL` ไว้ 1 จุดใน `.env.prod.example` เป็น comment** ("เลิกใช้
         แล้ว ให้ลบบรรทัดนี้ออกจาก `.env` แล้วตั้ง `PSP_PUBLIC_BASE_URL` แทน") + ใน runbook — ไฟล์ `.env` จริงของ
         ทุกเครื่อง/ทุก deploy เป็น gitignored blind spot (LESSONS: rename config key แล้ว CI เขียวหมดแต่ค่าเก่า
         ค้างทุกเครื่อง) และ `.env.prod.example` คือไฟล์ที่ operator diff กับ `.env` ของตัวเอง. ลบ token ออกจาก
         comment ของ `docker-compose.prod.yml` แล้ว (machine file — ชี้ไป runbook แทน) เพื่อให้ config surface
         สะอาดตาม REQ-4.2. (3) เพิ่ม 2 test นอกรายการของ task: trailing-slash ของ `PublicBaseUrl` (double slash
         ทำให้ route ไม่ match = miss เงียบแบบเดียวกับไม่มี URL เลย ซึ่งเป็นโรคที่ REQ-4 รักษา) และ Omise
         `sends_no_callback_url...` (pin เหตุผลของ REQ-4.5 ว่าทำไม Omise ไม่ใช้ `WebhookUrlFor` — กันคนหลังเติม
         field ที่ Omise ไม่มีแล้วเข้าใจว่าปิด gap แล้ว). (4) `FakePspAdapter` เพิ่ม `ChargedConnectionId`
         (บันทึก id ที่ถูกส่งเข้ามา) ตอนแก้ signature — ยังไม่มี test ใดอ้าง แต่เป็น seam ที่ task 6 ใช้ได้ทันที
         ถ้าต้องพิสูจน์ว่า handler ส่ง `connection.Id` ใบถูก; ถ้า reviewer ถือว่าเป็น dead member ลบได้ 3 บรรทัด
         โดยไม่กระทบ test ใด. (5) ไม่แตะ `OmiseAdapter` method switch (`promptpay -> NotSupportedException`) —
         REQ-6.3/6.4 เป็นเรื่องของ 2C2P `paymentChannel`; switch ของ Omise เป็น backstop ชั้นสองที่ REQ-6.2
         (create-session) กันไว้ก่อนแล้ว และมี test เดิม pin อยู่.

- [x] 6. **fetch-to-confirm พายอดกลับมาแล้วเทียบก่อน MarkPaid** — `PspChargeConfirmation(PspChargeStatus
     Status, Money? Amount)` แทน return type ของ `IPspAdapter.FetchChargeAsync`; `TwoCTwoPAdapter` อ่าน
     `amount` + `currencyCode` จาก paymentInquiry, `OmiseAdapter` อ่าน `amount` (minor units) + `currency`
     แล้วแปลงกลับ major unit ด้วย `Iso4217.MinorUnitDigits`; field หาย/ผิดชนิด -> `Amount = null`
     (ห้าม throw); `HandlePspWebhookHandler` เทียบยอด+สกุลเงินกับ `session.Amount` **ก่อน** `MarkPaid` —
     ไม่ตรง -> `WebhookOutcome.Ignored` (ไม่เปลี่ยน state ไม่ enqueue ตอบ 200), `Amount == null` ->
     ยืนยันด้วยสถานะเหมือนเดิม. ห้ามเปลี่ยน idempotency key / ลำดับ transaction / สัญญา `PaymentPaid`.
     **Done** = ยอดที่ PSP รายงานถูกเทียบเมื่อมี และพฤติกรรมเดิมไม่เปลี่ยนเมื่อไม่มี.
     Satisfies: REQ-8 (8.1, 8.2, 8.3, 8.4). Depends on: 5. Verify: `dotnet test tests/Payments.Tests` —
     adapter: response มี amount -> คืนค่าถูก, ไม่มี/ผิดชนิด -> null; webhook handler: amount ตรง ->
     `Processed` + enqueue, amount ต่าง -> `Ignored` + ไม่ `MarkPaid` + ไม่ enqueue, amount null ->
     `Processed` + `dotnet test pol-core.slnx --filter "Category!=Integration"` (ไม่ถอย).
     Evidence:
       - test: `dotnet build pol-core.slnx -warnaserror` -> `Build succeeded. 0 Warning(s) 0 Error(s)`
         (64 projects). Verified it really COMPILED, not skipped, with `stat -f "%Sm %N"`: every touched
         project's dll is strictly newer than its source (Payments.Tests.dll 23:06:50 vs
         HandlePspWebhookHandlerTests.cs 23:06:44; Payments.Application.dll 23:04:25 vs handler 23:04:22).
       - test: `dotnet test tests/Payments.Tests --no-build` -> **150 passed / 0 failed / 0 skipped**
         (baseline 126 -> +24: TwoCTwoPAdapterTests +8 cases, OmiseAdapterTests +9 cases,
         HandlePspWebhookHandlerTests 7 new).
       - test: `dotnet test pol-core.slnx --filter "Category!=Integration"` -> `EXIT=0`, **1208 passed /
         0 failed / 0 skipped**, all **16** `Passed!` banners present (counted per trap 30). Per project:
         Admins 95, Architecture 223, BuildingBlocks 43, Carts 15, Checkouts 7, Divisions 6, Hosts 353,
         Iam 62, Levels 6, Merchants 115, Offices 6, Orders 68, **Payments 150**, Positions 6, Products 7,
         SharedKernel 46 — identical to the task-5 baseline (1184) except Payments.Tests 126 -> 150.
       - test: RED proof A (the amount check itself) — deleted the `if (confirmed.Amount is { } collected
         ...)` block, rebuilt, ran the new class: `Failed: 3, Passed: 4`. Red =
         `An_amount_that_differs_from_the_session_is_ignored_and_publishes_nothing` (`Expected: Ignored /
         Actual: Processed`), `An_amount_collected_in_a_different_currency_is_ignored`,
         `A_redelivery_after_a_mismatch_reports_Duplicate_...`. The 4 that stayed green are the
         unchanged-behavior regression net (status gate, null amount, scale-only difference, happy path).
         Restored -> rebuilt -> 150 passed.
       - test: RED proof B (the two adapter reads, mutated together) — 2C2P `Amount` hardcoded to `null`
         + Omise using `TryReadMajorUnitMoney` instead of `TryReadMinorUnitMoney` (the realistic
         "forgot the unit conversion" bug) -> `Failed: 4, Passed: 60`: both 2C2P
         `FetchCharge_reports_the_major_unit_amount_the_psp_collected` cases and Omise's two **THB**
         conversion cases. Omise's **JPY** case stayed GREEN because minor == major at 0 decimal digits —
         a JPY-only fixture would not have caught the missing conversion, which is why the THB cases are
         the load-bearing ones. Restored -> rebuilt -> 150 passed.
       - test: `bash scripts/check-rename-identifiers.sh` -> `OK — no retired identifier appears as a
         live-code token in src/ or tests/` (run AFTER `git add`, trap 12).
       - test: `bash scripts/spec-trace.sh captive-payment-alignment` -> `OK: ... เกณฑ์ 42 ข้อ ถูกอ้างครบใน
         design.md และ tasks.md, EARS lint ผ่านทุกข้อ`; `.ai/bin/check-secrets.sh --all` -> exit 0.
       - viewports: n/a — no browser surface (adapter/port contract + one Application handler branch).
       - deviations: (1) the comparison is written `collected != session.Amount` rather than design D8's
         `paid.Amount == session.Amount.Amount && paid.SameCurrencyAs(session.Amount)`. `Money` is a
         `readonly record struct`, so the generated `!=` compares BOTH the decimal amount (value-based, so
         250.0900 == 250.09) and the currency (ordinal) — behaviourally identical to the spelled-out form
         and it cannot drift out of sync with `Money`. Pinned by
         `An_amount_differing_only_in_decimal_scale_still_matches` +
         `An_amount_collected_in_a_different_currency_is_ignored`. (2) The two null-safe readers
         (`TryReadMajorUnitMoney` / `TryReadMinorUnitMoney`) live on `PspAdapterBase` next to the
         `FormatMajor/MinorUnitAmount` pair they invert, not inline in each adapter — both adapters need the
         identical "never throw, null means status-only" contract and D8 states it once for both.
         (3) NOT CHANGED, pre-existing, deliberately left alone (REQ-8.4 forbids touching the idempotency
         keys): the multi-key claim is taken BEFORE the fetch, so a redelivery of an event whose amount
         mismatched reports `Duplicate` even though nothing was ever marked paid. The `Ignored`-on-not-yet-
         Paid path has behaved exactly this way since it was written — the amount check inherits the
         property, it does not introduce it. Pinned as-is by
         `A_redelivery_after_a_mismatch_reports_Duplicate_because_the_claim_was_already_spent`; moving the
         claim would change replay semantics for the entire webhook path and needs its own requirement.

- [x] 7. **Provisioning vocabulary + demo seed + as-built docs** — `ProvisionMerchantHandler.cs:63`
     เปลี่ยน `Trim()` เป็น `PaymentMethods.Normalize(m)` ต่อรายการ (ค่าไม่รู้จัก -> `ArgumentException`
     400) + test; `docker/bootstrap/seed-demo.sql` ให้ session ที่ seed ใช้ `card` เท่านั้น (`:387`)
     โดย `EnabledMethods` ของ connection คงเดิม; อัปเดต `docs/reference/payment-orchestration-modules.md`
     ทุกย่อหน้า `[as-built ...]` ที่ล้าสมัย (create-session ที่รับ amount จาก body, method router /
     `Connection.Supports` ที่ "ไม่มี call site", backend URL global, `MarkFailed` ที่ไม่มีผู้เรียก,
     `FetchChargeAsync` ที่คืนแค่ status) พร้อมวันที่; อัปเดตทะเบียน gap ใน
     `docs/reference/platform-modules.md` — ปิดเฉพาะที่ปิดจริง และ **คงไว้พร้อมเหตุผล + next step** สำหรับ
     3 เรื่องที่ยังเปิด: (ก) Omise webhook signature (มีลายเซ็น `Omise-Signature` จริง — เหตุผลที่ยัง
     ไม่ทำคือ seam ไม่พา header/timestamp + ยังไม่ verify กับ sandbox, **ห้าม** เขียนว่า Opn ไม่มีลายเซ็น),
     (ข) promptpay/installment ที่ adapter ยังทำไม่ได้, (ค) การเทียบยอดกรณี PSP ไม่ส่งยอดกลับ;
     `docs/runbooks/deploy-self-host.md` (ตั้ง `Psp:PublicBaseUrl`; ตั้ง webhook URL ต่อ connection ใน
     dashboard ของ Omise; `PSP_TWOCTWOP_BACKEND_RETURN_URL` เลิกใช้); ตรวจ
     `.ai/shared/PROJECT_CONTEXT.md`/`SECURITY_RULES.md` ว่ายังตรง (ถ้าตรงแล้วบันทึกว่าตรวจแล้ว ไม่ต้องแก้).
     ห้าม emoji ใน `.md`. **Done** = ไม่มีย่อหน้า as-built ที่ขัดกับโค้ดหลัง task 1-6 และไม่มี gap ที่ยัง
     เปิดถูกเขียนว่าปิดแล้ว.
     Satisfies: REQ-3 (3.7), REQ-5 (5.1, 5.2, 5.3), REQ-6 (6.5). Depends on: 2, 3, 4, 5, 6. Verify:
     `dotnet test tests/Merchants.Tests` (vocabulary ที่ provisioning) + `bash scripts/spec-trace.sh
     captive-payment-alignment` -> OK + อ่านทุกย่อหน้า `[as-built` ในสองไฟล์ reference ที่แตะ payment flow
     เทียบโค้ดจริง + `dotnet test pol-core.slnx --filter "Category!=Integration"` (ไม่ถอย)
     Evidence:
       - test: `dotnet build pol-core.slnx -warnaserror` -> `Build succeeded. 0 Warning(s) 0 Error(s)`
         (64 projects). ยืนยัน compile จริงด้วย `stat -f "%Sm %N"` (trap 15/33): `Merchants.Application.dll`
         23:29:37 vs `ProvisionMerchantHandler.cs` 23:17:12; `Merchants.Tests.dll` 23:29:38 vs
         `ProvisionMerchantHandlerTests.cs` 23:17:23 — dll ใหม่กว่า source ทั้งสองคู่.
       - test: `dotnet test tests/Merchants.Tests --no-build` -> **120 passed / 0 failed / 0 skipped**
         (baseline 115 -> +5: `Rejects_an_enabled_method_outside_the_canonical_vocabulary` 4 InlineData
         (`"CC"`, `"paypal"`, `""`, `"   "`) + `Stores_enabled_methods_as_canonical_codes`).
       - test: **RED proof ว่า test ชุดใหม่กัดจริง** — `git stash push -- <ProvisionMerchantHandler.cs>`
         (คืนเป็น `Trim()` ของก่อน task, compile ผ่าน 0 error ตาม trap 33) -> build -> `dotnet test
         tests/Merchants.Tests --no-build --filter "FullyQualifiedName~ProvisionMerchantHandlerTests"` ->
         `Failed! - Failed: 3, Passed: 10, Total: 13`. 3 ที่แดง = `...vocabulary(method: "CC")`,
         `...vocabulary(method: "paypal")`, `Stores_enabled_methods_as_canonical_codes`.
         **2 InlineData ที่เขียวทั้งสองฝั่งโดยไม่ได้พิสูจน์อะไรของ task นี้** = `""` และ `"   "` —
         โค้ดเก่า `.Where(m => m.Length > 0)` ทิ้ง blank แล้วตกไปโดนเช็ค "must enable at least one method"
         ที่มีอยู่ก่อนแล้ว จึง throw `ArgumentException` เหมือนกัน; เก็บไว้เป็น regression net ไม่ใช่ proof.
         `git stash pop` -> build -> 120 passed.
       - test: `dotnet test pol-core.slnx --filter "Category!=Integration"` -> `EXIT=0`,
         **1213 passed / 0 failed / 0 skipped**, `Passed!` banner ครบ **16** ตัว, `Failed!` banner = 0
         (นับตาม trap 30, รัน background เขียนลงไฟล์). Per project: Admins 95 · Architecture 223 ·
         BuildingBlocks 43 · Carts 15 · Checkouts 7 · Divisions 6 · Hosts 353 · Iam 62 · Levels 6 ·
         **Merchants 120** · Offices 6 · Orders 68 · Payments 150 · Positions 6 · Products 7 ·
         SharedKernel 46 — เท่ากับ baseline ของ task 6 (1208) ทุก project ยกเว้น Merchants 115 -> 120.
       - test: **demo seed รันจริงกับ SQL Server ที่ `:11433`** (ไม่ใช่แค่อ่านไฟล์): `set -a && source
         .env.integration && set +a && bash scripts/seed-demo.sh` -> `seed-demo: OK.` + `SEED_EXIT=0`
         (self-check ท้ายสคริปต์ THROW + `sqlcmd -b` จะ exit non-zero ถ้าไม่ผ่าน). count ที่สคริปต์พิมพ์:
         `txn.PaymentSessions = 36` (เท่าเดิม). ยืนยันข้อมูลหลัง seed ด้วย query:
         `SELECT Method, COUNT(*) ... WHERE Id LIKE 'ee000000-%' GROUP BY Method` -> **`card 36`** (แถวเดียว
         ไม่มี promptpay/installment เหลือ) และ `SELECT COUNT(*) FROM (... Status IN (0,1) GROUP BY OrderId
         HAVING COUNT(*)>1)` -> **0** (ไม่ชน unique index ของ task 4).
       - test: `bash scripts/spec-trace.sh captive-payment-alignment` -> `OK: 'captive-payment-alignment'
         เกณฑ์ 42 ข้อ ถูกอ้างครบใน design.md และ tasks.md, EARS lint ผ่านทุกข้อ`.
       - test: `bash scripts/check-rename-identifiers.sh` (หลัง `git add` ตาม trap 12) -> `OK — no retired
         identifier appears as a live-code token in src/ or tests/`; `bash .ai/bin/check-secrets.sh --all`
         -> exit 0 (รันเปล่าไม่มี env prefix ตาม trap 27).
       - test: **emoji gate ของ `.md`** — สแกน **บรรทัดที่ task นี้เพิ่ม** (`git diff` ของ `*.md` เฉพาะ
         บรรทัด `+`) ด้วย python regex ครอบ `U+1F000-U+1FAFF` + regional indicator + `✅❌❤⚠⭐❗❓‼⁉️⃣`
         -> `added md lines = 238 | emoji hits = 0`. (รอบแรกใช้ regex กว้างเกินไปจนจับ `→`/`↔`/`⇒` 362 จุด
         ซึ่งเป็นลูกศร typographic ที่มีอยู่ทั่วทุกไฟล์เดิมของ repo ไม่ใช่ emoji — แก้ regex แล้วสแกนใหม่.)
       - viewports: n/a — provisioning handler 1 บรรทัด + SQL seed + เอกสาร ไม่มี browser surface.
       - **REQ trace ทั้ง 42 เกณฑ์ (ไล่ทีละข้อกับโค้ด/เทสต์จริง ไม่ใช่แค่ spec-trace ที่ตรวจการอ้างอิงในเอกสาร):**

         | เกณฑ์ | โค้ด/เทสต์ที่รองรับจริง | ผล |
         |---|---|---|
         | 1.1 | `CreateSessionHandler.cs:88` ส่ง `order.Amount` เข้า `Session.Create`; test happy-path เทียบ amount+currency กับ order | OK |
         | 1.2 | `CreateSessionHandler.cs:53-54` -> `NotFoundException` (404); `PaymentPricingQueryTests` พิสูจน์ query filter คืน null สำหรับ order ของ merchant อื่น (ไม่ใช่ 403) | OK |
         | 1.3 | `CreateSessionHandler.cs:56-58` -> `InvalidOperationException` (409); test order-not-awaiting | OK |
         | 1.4 | `CreateSessionCommand` ไม่มี token `Amount` เลย (grep = 0); `CreatePaymentSessionRequest` ตัดออก; `CreatePaymentSessionContractTests` pin property set + stale-body bind | OK |
         | 1.5 | การตรวจทั้ง 8 ขั้นอยู่ใน `CreateSessionHandler` (Application) ไม่ใช่ endpoint; test pin ว่า method นอก vocabulary ถูกปฏิเสธก่อนอ่าน order (`FakePayableOrderReader.Calls == 0`) | OK |
         | 1.6 | `CreatePaymentSessionContractTests` (wire) + `charges_the_amount_the_session_carries` (ยอดที่ adapter ประกอบ == ยอด session/order) | OK |
         | 1.7 | port `Payments.Application/Ports/IPayableOrderReader.cs` + impl `Persistence.MerchantRuntime/Payments/PayableOrderReader.cs`; grep `GetOrderDetailCommand` ใน `src/Modules/Payments` = **0**; ไม่มี ProjectReference Payments->Orders | OK |
         | 2.1 | `CreateSessionHandler.cs:77-78` คืน id ใบเดิม; test idempotent-return ทั้งบน `Created` และ `Redirected` | OK |
         | 2.2 | `CreateSessionHandler.cs:81-82` -> `ConflictException` (409); test method ต่าง + psp ต่าง | OK |
         | 2.3 | บังคับผ่าน 1.3 (order ที่จ่ายแล้วไม่ใช่ `AwaitingPayment`); มี test เส้นทางนั้นจริง | OK |
         | 2.4 | index `IX_PaymentSessions_OrderId_Open` อยู่ใน **ทั้งสอง** `SessionConfiguration` + migration `20260726151538` + snapshot; apply จริงกับ `:11433` แล้ว (`sys.indexes`: `is_unique=1`, `has_filter=1`, `([Status] IN ((0),(1)))`) | OK |
         | 2.5 | `MerchantRuntimeUnitOfWork` แปลง SQL 2627/2601 -> `ConflictException`; `OpenSessionIndexIntegrationTests` pin เลข error **และชื่อ index** ในข้อความ (ไม่ assert `ConflictException` ในโปรเซส — boundary deviation ของ task 4, บันทึกแล้ว) | OK (ดูหมายเหตุ) |
         | 2.6 | `tests/Architecture.Tests/OpenSessionIndexTests.cs` 3 tests (owner context, runtime context, lookup index ห้ามถูก mutate) — offline ไม่พึ่ง integration job | OK |
         | 3.1 | `Connection.EnsureEligible` -> `Supports(method)`; เรียกก่อนแตะ PSP ทั้ง 2 flow; `ConnectionEligibilityTests` | OK |
         | 3.2 | `EnsureEligible` เช็ค `!IsEnabled` **ก่อน** `Supports` -> 409; test pin ลำดับเหตุผล | OK |
         | 3.3 | `CreateSessionHandler.cs:60-62` -> 409 ที่ขั้น create (ไม่รอพังตอน redirect); test | OK |
         | 3.4 | `PaymentMethods.Normalize` -> `ArgumentException` (400); `PaymentMethodsTests` (24 case) + handler test 3 case | OK |
         | 3.5 | `StartRedirectHandler.cs:67-71` resolve connection + `EnsureEligible` **ก่อน** `BeginRedirect()`; test ยืนยัน status ยัง `Created`, `RedirectUrl` null, `vault.Reveals == 0`, `SaveCount == 0` | OK |
         | 3.6 | logic อยู่จุดเดียวบน `Payments.Domain.Psp.Connection.EnsureEligible`; production call site = **2** (`CreateSessionHandler:66`, `StartRedirectHandler:71`) -> `Supports` มีผู้เรียกจริง | OK |
         | 3.7 | `ProvisionMerchantHandler.cs:67` `.Select(PaymentMethods.Normalize)` (**task นี้**); 5 tests ใหม่ + RED proof 3 แดงบนโค้ดเก่า | OK |
         | 4.1 | `PspAdapterBase.WebhookUrlFor(Guid)` -> `{PublicBaseUrl}/api/v1/webhooks/{id}`; `TwoCTwoPAdapter` ใช้ค่านี้; test `points_the_backend_notification_at_the_connection_being_charged` + trailing-slash test | OK |
         | 4.2 | `git grep BackendReturnUrl\|PSP_TWOCTWOP_BACKEND_RETURN_URL` บน live surface (`src`, `docker-compose.prod.yml`, `.github`, `.gitlab-ci.yml`) = **0**; เหลือ 2 จุดเป็น **deprecation note สำหรับ operator** เท่านั้น (`.env.prod.example:45`, runbook) ตามที่ task 5 ตัดสินไว้ (ไฟล์ `.env` จริงเป็น gitignored blind spot) | OK (ดูหมายเหตุ) |
         | 4.3 | `ProvisioningGuards.RequirePublicBaseUrl` เรียกใน block `if (!IsDevelopment())`; `ProvisioningGuardsTests` 9 case (บังคับ scheme http/https ด้วย — `Uri.TryCreate("/api/v1", Absolute)` คืน true บน Unix) | OK |
         | 4.4 | `PspOptions.TwoCTwoP.FrontendReturnUrl` + `Omise.ReturnUri` ยังอยู่ครบ; adapter test pin ว่า `frontendReturnUrl` ยัง global | OK |
         | 4.5 | `docs/runbooks/deploy-self-host.md:98` section "ตั้ง webhook URL ต่อ connection ที่ฝั่ง PSP" (4 ขั้น) + Omise test `sends_no_callback_url_because_omise_takes_its_webhook_from_the_dashboard` | OK |
         | 4.6 | `appsettings.json` มี `"Psp": { "PublicBaseUrl": "" }` placeholder (ไม่ใช้ `ValidateOnStart`); `Hosts.Tests` 353 passed (17 ไฟล์ที่ boot host จริงยังเขียว) | OK |
         | 5.1 | **task นี้** — `payment-orchestration-modules.md`: banner ลงวันที่ 2026-07-26 + แก้ §3.1 create/redirect/return/webhook, §3.2 method router, ภาค 4 ตาราง `IPspAdapter`, §4.1, §4.2, §5.1, ตารางท้ายไฟล์ | OK |
         | 5.2 | **task นี้** — `platform-modules.md`: ปิดข้อ 10 (strike-through) + ปิดข้อ 1 **เฉพาะชั้น connection** + แถวใน §9/§11/§12; ไม่ปิดข้อใดที่ยังไม่ปิดจริง | OK |
         | 5.3 | **task นี้** — 3 เรื่องที่ยังเปิดคงอยู่พร้อมเหตุผล + next step: (ก) ข้อ 9 Omise HMAC — เขียนชัดว่า **Opn มี `Omise-Signature` จริง** เหตุผลคือ seam ไม่พา header/timestamp + ยังไม่ verify กับ sandbox; (ข) ข้อ 8 promptpay/installment (Payment Links+ / SAQ A); (ค) ข้อ **23 ใหม่** PSP ไม่ส่งยอดกลับ. บวกข้อ **24 ใหม่** (เปลี่ยน method/PSP กลางคัน) + หมายเหตุในข้อ 12 (`MarkExpired` ยังไม่มีผู้เรียก) | OK |
         | 6.1 | `IPspAdapter.SupportedMethods` (`IReadOnlySet<string>`), **abstract** บน `PspAdapterBase`, override ทั้ง 2 adapter = `{ card }`; test ต่อ adapter | OK |
         | 6.2 | `CreateSessionHandler.cs:68-70` -> 409; test adapter-cannot-honour | OK |
         | 6.3 | `TwoCTwoPAdapter.PaymentChannelFor(session.Method)` (`card -> "CC"`); test `derives_the_payment_channel_from_the_session_method` | OK |
         | 6.4 | ด่านหลัก = 6.2 ที่ create-session; adapter throw `NotSupportedException` ระบุ method เป็น backstop; test theory 2 case (promptpay/installment) ว่าไม่ substitute เป็นบัตร | OK |
         | 6.5 | **task นี้** — `seed-demo.sql` session Method = `N'card'` ล้วน (CASE ต่อ merchant ถูกถอด), `EnabledMethods` ของ connection คงเดิมโดยเจตนา; seed รันจริงผ่าน + query ยืนยัน `card 36` | OK |
         | 7.1 | `StartRedirectHandler.cs:101-109` catch -> `MarkFailed` -> save -> **rethrow**; test `A_charge_the_psp_refuses_fails_the_session_and_rethrows` (`Assert.Same` ว่า exception เดิมทะลุออก) | OK |
         | 7.2 | ทั้ง 7.1 (charge ล้ม) และ 7.3 (ปฏิเสธก่อน claim) ปิดสองทางที่ทำให้เกิด `Redirected`+null; test assert `RedirectUrl` null + status ยัง `Created` / `Failed` | OK |
         | 7.3 | `StartRedirectHandler.cs:67-71` ก่อน `BeginRedirect()`; 3 tests (ไม่มี connection / connection ปิด / method ที่เลิกเปิด) assert `SaveCount == 0` | OK |
         | 7.4 | filter ของ index ไม่รวม `Failed`(3); test `A_failed_charge_lets_the_same_order_open_a_fresh_session` เดินทั้งเส้น create -> redirect ล้ม -> create ได้ id ใหม่ (ไม่ใช่ประกอบ `Failed` ในหน่วยความจำ) + integration test ยืนยันบน SQL Server จริง | OK |
         | 8.1 | `PspChargeConfirmation(PspChargeStatus, Money?)` ใน `PspContracts.cs:26`; 2C2P อ่าน `amount`+`currencyCode` (major), Omise อ่าน `amount`+`currency` (minor -> major ด้วย `Iso4217.MinorUnitDigits`); adapter tests + RED proof B | OK |
         | 8.2 | `HandlePspWebhookHandler.cs:95-96` `collected != session.Amount` -> `Ignored` ก่อน `MarkPaid`; 3 tests (ยอดต่าง, สกุลต่าง, redelivery) + RED proof A | OK |
         | 8.3 | `Amount == null` -> ไม่เข้าเงื่อนไข -> ยืนยันด้วยสถานะเดิม; test null-amount + 6 unusable-response case ต่อ adapter (ห้าม throw); **บันทึกเป็น gap ข้อ 23 ตาม REQ-5.3** | OK |
         | 8.4 | diff ของ `HandlePspWebhookHandler` = **+8/-1** บรรทัด ไม่แตะ idempotency key ทั้ง 2 คีย์, ลำดับ/ขอบเขต transaction, `_outbox.Enqueue`, หรือ record `PaymentPaid`; `Orders.Tests` 68 เท่าเดิม | OK |

         **สรุป trace: 42/42 เกณฑ์มีโค้ด/เทสต์รองรับจริง — ไม่มีเกณฑ์ใดค้าง (0 blocker).** 2 ข้อมี
         หมายเหตุขอบเขตที่บันทึกไว้แล้วและ **ไม่ใช่** เกณฑ์ที่ไม่ผ่าน: **2.5** พิสูจน์ถึงระดับ "SQL คืน
         2601 + ชื่อ index ถูก" ไม่ใช่ `ConflictException` ในโปรเซส (เพราะ `Integration.Tests` ตั้งใจไม่มี
         `InternalsVisibleTo` — hop สุดท้ายเป็นโค้ดเดิมที่ไม่ถูกแตะ, ให้ lead ตัดสินว่าพอไหม) และ **4.2**
         เหลือชื่อ env เก่าไว้ 2 จุดในฐานะ deprecation note ของ operator ไม่ใช่ config ที่ยังใช้งาน.
       - deviations: (1) **`"Card"` ไม่ถูกปฏิเสธ แต่ถูก normalize เป็น `"card"`** — ตอนแรกเขียน test ว่าต้อง
         throw แล้วมันแดง; อ่าน `PaymentMethods.Normalize` ซ้ำแล้วพบว่ามัน `ToLowerInvariant()` ก่อนเช็ค
         `IsKnown` ดังนั้น method ที่ **รู้จักแต่ผิด case** คือเคสที่ต้อง normalize (นี่คือสิ่งที่ปิดช่อง
         ordinal-compare ของ REQ-3.7 พอดี) ไม่ใช่เคสที่ต้องปฏิเสธ — **test ผิด ไม่ใช่โค้ดผิด** จึงย้าย
         `"Card"` ออกจาก theory ที่คาด throw ไปอยู่ใต้ `Stores_enabled_methods_as_canonical_codes`
         (` CARD ` -> `card`) แล้วเขียนเหตุผลไว้เป็นคอมเมนต์เหนือ theory. (2) **ไม่ dedupe และไม่เรียงลำดับ
         `EnabledMethods`** — `["card","card"]` ยังเก็บเป็น `"card,card"` ได้ (พฤติกรรมเดิม, `Supports`
         split แล้วเทียบทีละตัวจึงไม่กระทบ); REQ-3.7 ขอแค่ normalize + reject ค่าไม่รู้จัก การเพิ่ม dedupe
         = scope creep ที่ไม่มี REQ รองรับ. (3) **แก้ `.ai/shared/PROJECT_CONTEXT.md` 1 bullet**
         (§Business Objectives "จ่ายไม่ผิด/ไม่ซ้ำ") เพราะ task 2+6 ทำให้ข้อความเดิมที่บอกว่าแนวกันคือ
         "verify ตอน Orders รับ `PaymentPaid`" **ล้าสมัยจริง** (หลังยอดมาจาก order การเทียบนั้นเทียบค่า
         เดียวกับตัวเอง) — แก้น้อยที่สุดคือชี้ว่ายอดมาจากแถว order + เทียบกับยอดที่ PSP รายงาน และระบุว่า
         การเทียบฝั่ง Orders เป็น defence-in-depth. **`.ai/shared/SECURITY_RULES.md` ตรวจแล้วยังตรงทุกข้อ
         ไม่ต้องแก้** (webhook = source of truth + verify/idempotent/fetch-to-confirm, idempotency 2 คีย์,
         vault, isolation floor, captive allowlist, provisioning saga — งานนี้ไม่ขัดข้อใด และไม่ได้ทำให้
         ข้อใดล้าสมัย). (4) **ไม่แก้ 2 จุดที่ล้าสมัยอยู่ก่อนแล้ว** และไม่ใช่ผลของ task 1-6 (surgical-change
         rule) — รายงานให้ lead แทน: `platform-modules.md` §9 บรรทัด `endpoints:` ยังเขียน route เก่า
         `POST /payment-sessions` + "tenant Bearer" ที่ retire ไปตั้งแต่ rf1, และ `SECURITY_RULES.md`
         อ้าง seam ชื่อ `IWebhookVerifier` ซึ่งของจริงคือ `IPspAdapter.VerifyWebhook`..
</content>

---

## Codex review round 1 (#4782168269, reviewed `ec7f4b174f`) — 3x P1, all verified REAL by lead

- [ ] 8. **แยก definitive จาก ambiguous ใน StartRedirect + สะสาง claim ที่ค้าง** — แก้ 2 P1 ที่เกี่ยวกัน
     (design **D6a**). (ก) `catch (Exception)` ที่ `StartRedirectHandler.cs:101` `MarkFailed` ทุกกรณี รวม
     timeout/cancel/transport/parse ที่ PSP **อาจรับ charge ไปแล้ว** -> create-session เปิดใบใหม่ ->
     `Session.Id` ใหม่ = idempotency key ใหม่ที่ PSP = **จ่ายซ้ำ** + charge แรกไม่มี session ผูก
     `PspExternalChargeId` -> `GetByExternalChargeAsync` คืน null -> webhook throw = poison. (ข)
     `_vault.RevealAsync` (`:91`) อยู่หลัง claim commit แต่ **นอก** try -> vault ล่ม = session ค้าง
     `Redirected` + `RedirectUrl == null` = 409 ถาวร (สภาพที่ REQ-7.2 ห้าม) และตอนนี้ index ของ task 4
     บล็อกการเปิดใบแทนด้วย.
     ต้องทำ: exception ใหม่ `PspRejectedException` (definitive) ใน `Payments.Application/Ports`;
     `PspAdapterBase.SendOnceAsync` map 4xx ยกเว้น 408/429 -> definitive, ที่เหลือ -> ambiguous; throw ที่เกิด
     ก่อนส่ง HTTP (amount ไม่ representable / method ที่ adapter ไม่รองรับ / key-environment mismatch) ->
     definitive; signature-verify ไม่ผ่าน หรืออ่าน field ไม่ได้ -> **ambiguous**; ย้าย `RevealAsync` เข้า
     เส้นทาง definitive; เปลี่ยน re-entry ที่หัว handler ให้ `Redirected` + `RedirectUrl == null` เดินขั้น
     reveal->create->bind **ซ้ำ** โดยไม่ `BeginRedirect` ใหม่. **Done** = ผลกำกวมไม่เคยกลายเป็น `Failed`,
     และ claim ที่ค้างสะสางได้เองด้วย idempotency key เดิม ไม่มี charge ใบที่สอง.
     Satisfies: REQ-7 (7.5, 7.6). Depends on: 3, 4, 5. Verify: `dotnet test tests/Payments.Tests` — case:
     `TaskCanceledException`/`HttpRequestException`/5xx/parse-fail -> status ยัง `Redirected`, **ไม่** `Failed`;
     `PspRejectedException`/4xx/vault throw -> `Failed`; re-entry ของ `Redirected`+null URL -> เรียก adapter
     ซ้ำ **ครั้งเดียว** แล้วผูก charge (fake นับ call = 1 ต่อการเรียกซ้ำ, ไม่ `BeginRedirect` ซ้ำ);
     fail-then-retry เดิม (definitive) ยังผ่าน + `dotnet build pol-core.slnx -warnaserror`.

- [ ] 9. **migration สะสางแถวซ้ำก่อนสร้าง unique index** — `CreateIndex` เปล่าใน
     `20260726151538_OneOpenPaymentSessionPerOrder` จะ **fail กลาง migration chain** บนฐานข้อมูลที่มี open
     session ซ้ำต่อ order อยู่ก่อน (สภาพที่ create-session เวอร์ชันก่อนหน้าอนุญาต; Risk 3 ของ design ยอมรับ
     เองแต่สั่งแค่ reset ด้วยมือ). เพิ่ม `migrationBuilder.Sql` นำหน้า `CreateIndex` ตาม design **D5a**:
     เลือกผู้ชนะต่อ `OrderId` (ให้ `PspExternalChargeId IS NOT NULL` มาก่อน แล้ว `CreatedAt DESC`, tiebreak
     `Id`) -> `UPDATE` ผู้แพ้ที่ `PspExternalChargeId IS NULL` เป็น `Status = 4` (`Expired`) + `UpdatedAt` ->
     ถ้ายังเหลือ `OrderId` ที่มีหลายใบ (แปลว่ามีใบผูก charge หลายใบ) `RAISERROR` ระบุ `OrderId` แล้วหยุด
     (expire ใบที่มี charge จริงทำให้ webhook `MarkPaid` throw ตลอดไป = ต้องให้คนตัดสิน) -> `CreateIndex`.
     ห้ามแตะแถวที่มี charge ผูกไว้. `Down` คง `DropIndex` เดิมพร้อมคอมเมนต์ว่าทำไมไม่ย้อน `Expired`.
     **Done** = migration รันผ่านบน DB ที่มีแถวซ้ำแบบไม่มี charge, และหยุดพร้อมข้อความที่ใช้งานได้จริงบน DB
     ที่มีแถวซ้ำแบบมี charge.
     Satisfies: REQ-2 (2.7). Depends on: 4. Verify: สร้างสถานการณ์จริงบน `pol-db` `:11433` — (ก) เพาะ 2 open
     session (ไม่มี charge) ของ order เดียว แล้ว re-apply migration -> ผ่าน + ใบที่แพ้เป็น `Expired` + index
     ถูกสร้าง; (ข) เพาะ 2 open session ที่ **มี** `PspExternalChargeId` ทั้งคู่ -> migration หยุดพร้อม
     `OrderId` ในข้อความ + index ไม่ถูกสร้าง; แปะ SQL + output จริงทั้งสองเคสลง Evidence. บวก
     `dotnet ef migrations has-pending-model-changes` -> ไม่มี diff และ `dotnet test pol-core.slnx --filter
     "Category!=Integration"` ไม่ถอย.
