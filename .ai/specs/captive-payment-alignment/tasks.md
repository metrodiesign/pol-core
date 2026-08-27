# Implementation Tasks: Captive Intra-Group Payment Alignment

> Status: unknown
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
     REQ-3 (3.6), REQ-6 (6.1).

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

- [x] 3. **Redirect: ปฏิเสธก่อน claim + `MarkFailed` เมื่อ charge ล้ม (liveness)** — เรียง
     `StartRedirectHandler` ใหม่ตาม design D6: ย้าย resolve connection (`InvalidOperationException` 409
     ถ้าไม่มี) + `connection.EnsureEligible(session.Method)` ขึ้นมา **ก่อน** `BeginRedirect()` (แก้บั๊กที่มี
     อยู่แล้ว: วันนี้ throw หลัง claim -> session ค้าง `Redirected` + `RedirectUrl == null` -> 409 ถาวร);
     ครอบ `CreateRedirectChargeAsync` ด้วย try/catch ที่ `session.MarkFailed(reason, now)` + save แล้ว
     **rethrow** (reason ห้ามมี secret); ห้ามให้ session จบ request ในสถานะ `Redirected` โดย `RedirectUrl`
     เป็น null. **Done** = คำขอที่ถูกปฏิเสธไม่เปลี่ยนสถานะเลย และ charge ที่ล้มทำให้ order เปิด session
     ใหม่ได้.
     Satisfies: REQ-3 (3.5), REQ-7 (7.1, 7.2, 7.3, 7.4). Depends on: 2. Verify:

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
     REQ-2 (2.4, 2.5, 2.6). Depends on: 3.

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

- [x] 6. **fetch-to-confirm พายอดกลับมาแล้วเทียบก่อน MarkPaid** — `PspChargeConfirmation(PspChargeStatus
     Status, Money? Amount)` แทน return type ของ `IPspAdapter.FetchChargeAsync`; `TwoCTwoPAdapter` อ่าน
     `amount` + `currencyCode` จาก paymentInquiry, `OmiseAdapter` อ่าน `amount` (minor units) + `currency`
     แล้วแปลงกลับ major unit ด้วย `Iso4217.MinorUnitDigits`; field หาย/ผิดชนิด -> `Amount = null`
     (ห้าม throw); `HandlePspWebhookHandler` เทียบยอด+สกุลเงินกับ `session.Amount` **ก่อน** `MarkPaid` —
     ไม่ตรง -> `WebhookOutcome.Ignored` (ไม่เปลี่ยน state ไม่ enqueue ตอบ 200), `Amount == null` ->
     ยืนยันด้วยสถานะเหมือนเดิม. ห้ามเปลี่ยน idempotency key / ลำดับ transaction / สัญญา `PaymentPaid`.
     **Done** = ยอดที่ PSP รายงานถูกเทียบเมื่อมี และพฤติกรรมเดิมไม่เปลี่ยนเมื่อไม่มี.
     Satisfies: REQ-8 (8.1, 8.2, 8.3, 8.4, 8.5 — amended 2026-07-28, ดูข้อ (3) ของ Evidence). Depends on: 5.

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

- [x] 8. **แยก definitive จาก ambiguous ใน StartRedirect + สะสาง claim ที่ค้าง** — แก้ 2 P1 ที่เกี่ยวกัน
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
     REQ-7 (7.5, 7.6). Depends on: 3, 4, 5.

- [x] 9. **migration สะสางแถวซ้ำก่อนสร้าง unique index** — `CreateIndex` เปล่าใน
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
     REQ-2 (2.7). Depends on: 4.
         (`assert-fresh-db.sql` ไม่นับแถวของตารางนี้) และไม่มี order จริงผูกอยู่.
