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

- [ ] 4. **DB floor: หนึ่ง open session ต่อ order** — เพิ่ม named filtered unique index
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

- [ ] 5. **Webhook callback URL ต่อ connection + paymentChannel จาก method + config surface** —
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

- [ ] 6. **fetch-to-confirm พายอดกลับมาแล้วเทียบก่อน MarkPaid** — `PspChargeConfirmation(PspChargeStatus
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

- [ ] 7. **Provisioning vocabulary + demo seed + as-built docs** — `ProvisionMerchantHandler.cs:63`
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
     เทียบโค้ดจริง + `dotnet test pol-core.slnx --filter "Category!=Integration"` (ไม่ถอย).
</content>
