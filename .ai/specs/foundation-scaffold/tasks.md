> Status: unknown
# Tasks — foundation-scaffold

> Checklist. `[x]` = เขียวพิสูจน์ได้ (build `dotnet build -warnaserror` 0 warning + test ผ่าน +
> Evidence). แต่ละ task มี `Satisfies:` อ้าง REQ-ID. flip เป็น `[x]` ผ่าน `.ai/bin/gate-task.sh`
> (`SDD_TYPECHECK_CMD="dotnet build -warnaserror"`, `SDD_TEST_CMD="dotnet test"`).
>
> สถานะปัจจุบัน: scaffold + shared spine เขียวแล้ว (`[x]` ด้านล่าง). โมดูล/host/migration ที่ยังไม่
> เต็ม = `[ ]`. backlog แยกท้ายไฟล์.

## กลุ่ม A — Scaffold + spine (เสร็จแล้ว)

- [x] A1. Solution graph + project references wired (SharedKernel, Contracts, BuildingBlocks×2,
  Modules×5×3, Hosts×2, tests×6) · Satisfies: REQ-1.1, REQ-1.3, REQ-1.4
  - Evidence: `find src -maxdepth 3 -type d` + `ls tests` ยืนยันครบทุก project ตามผัง (SharedKernel,
    Contracts, BuildingBlocks.Application/Infrastructure, Modules/{Products,Cart,Checkout,Orders,
    Payments}/{Domain,Application,Infrastructure}, Hosts/{TenantConsole,AdminConsole}, tests×6).
       - viewports: n/a (ไม่มี UI). Deviations: ไม่มี.
- [x] A2. Dependency direction + `-warnaserror` + nullable-clean (build เขียว 0 warning)
  · Satisfies: REQ-1.2, REQ-1.6
  - Evidence: brief ระบุ "shared spine ALREADY COMPILES GREEN" ภายใต้ TreatWarningsAsErrors; verify ด้วย
    `dotnet build -warnaserror` (0 warning). Viewports: n/a. Deviations: ไม่มี.
- [x] A3. SharedKernel spine — `Money`/`Money.Of`/`Add`/`Zero`/`SameCurrencyAs`, `Iso4217`
  (THB/USD/JPY), `IDomainEvent`, `Entity<TId>`, `AggregateRoot<TId>`, `MoneyJsonConverter`
  · Satisfies: REQ-3.1, REQ-3.2, REQ-3.3, REQ-3.4, REQ-3.5
  - Evidence: spine public API ใน brief ยืนยันทุก type มีอยู่และ compile เขียว; verify
    `dotnet test tests/SharedKernel.Tests`. Viewports: n/a. Deviations: ไม่มี.
- [x] A4. Contracts spine — `PaymentPaid : INotification` + `SchemaVersion = "v1"`
  · Satisfies: REQ-2.2
  - Evidence: brief ยืนยัน `PaymentPaid` record + `SchemaVersion="v1"` compile เขียวใน Contracts; verify
    `dotnet build -warnaserror`. Viewports: n/a. Deviations: ไม่มี.
- [x] A5. BuildingBlocks.Application abstractions — `ITenantContext`, `IClock`, `IUnitOfWork`,
  `IIdempotencyStore`, `IOutbox`, `IVaultSecretStore`, `ITenantScoped` · Satisfies: REQ-4.1
  - Evidence: brief ยืนยัน interface ทั้งเจ็ด compile เขียวใน BuildingBlocks.Application; verify
    `dotnet build -warnaserror`. Viewports: n/a. Deviations: ไม่มี.
- [x] A6. BuildingBlocks.Infrastructure spine — `ProducerDbContext`/`AdminDbContext`,
  `ModuleAssemblies`, `AddBuildingBlocksInfrastructure()` (clock, RLS interceptor, UoW, idempotency,
  vault, outbox, dispatcher), Outbox/Idempotency/Vault tables, model discovery จาก
  `ModuleAssemblies.Producer`; vault provider ออกแบบรองรับ envelope encryption (per-tenant KEK, DEK/secret,
  key id+version, rotation) · Satisfies: REQ-4.2, REQ-4.3, REQ-4.4, REQ-4.5, REQ-7.4
  - Evidence: `find src/BuildingBlocks/BuildingBlocks.Infrastructure` แสดงโฟลเดอร์ Outbox/Idempotency/
    Vault/Persistence; brief ยืนยัน spine compile เขียว; verify `dotnet build -warnaserror`.
       - viewports: n/a. Deviations: ไม่มี.

## กลุ่ม B — Architecture + naming guards

- [ ] B1. `Architecture.Tests` — บังคับทิศ dependency + ห้าม cross-module `*.Domain`/`*.Infrastructure`
  reference + Domain ไม่อ้าง EF/ASP.NET · Satisfies: REQ-1.2, REQ-1.5, REQ-11.2
- [ ] B2. naming convention test/lint — `Psp` ไม่ใช่ `PSP`, `Async` suffix, `I` prefix, `_camelCase`,
  `{Entity}Id`, `Utc` suffix, JSON camelCase · Satisfies: REQ-11.3
- [ ] B3. DI validation test — `ValidateScopes=true`, handler/pipeline ที่พึ่ง `DbContext` เป็น Scoped
  (กัน captive dependency) · Satisfies: REQ-2.5

## กลุ่ม C — Cross-cutting behavior (Application pipeline)

- [ ] C1. tenant-scope guard pipeline — reject command/query ที่ implement `ITenantScoped` เมื่อไม่มี
  tenant ใน context · Satisfies: REQ-5.3
- [ ] C2. `IdempotencyBehavior` / store — multi-key atomic claim `(psp,eventId)` +
  `(psp,externalChargeId,normalizedStatus)`; replay คืน `false` ไม่ transition/ไม่ enqueue ซ้ำ
  · Satisfies: REQ-6.1, REQ-6.2, REQ-6.5
- [ ] C3. Outbox enqueue + commit-with-SaveChanges + OutboxDispatcher (poll, lock/lease, poison/DLQ,
  idempotent consumer) · Satisfies: REQ-6.3, REQ-6.4

## กลุ่ม D — Modules (สร้างทับสัญญา spine)

- [ ] D1. Payments.Domain/Application/Infrastructure — payment session aggregate (Money ผ่าน 2 scalar
  + `Ignore(Amount)`), `IPspAdapter` (redirect-only), webhook handler (verify→claim→confirm→
  transition→emit ใน 1 tx) · Satisfies: REQ-3.6, REQ-8.1, REQ-8.2, REQ-9.1, REQ-9.2, REQ-9.3, REQ-9.4
- [ ] D2. PSP adapters — 2C2P + Omise/Opn, 3 ช่องทาง (card/promptpay/installment) redirect-only;
  Omise PromptPay = Payment Links+ hosted `transaction_url` (ห้าม source+charge); Non-Goal guard —
  requirement ที่นำไปสู่ non-redirect/card field/settlement/billing/onboarding/issuance → หยุดถามก่อน.
  **HTTP จริง = stub mark `// ponytail:`** ระบุ upgrade path · Satisfies: REQ-8.2, REQ-8.3, REQ-8.4,
  REQ-11.6 (real HTTP = backlog Z1)
- [ ] D3. Orders.Domain/Application/Infrastructure — order aggregate (`PendingPayment` → `Paid`),
  handler รับ `PaymentPaid` verify amount+currency (ไม่ใช่แค่ PaymentId) · Satisfies: REQ-9.5
- [ ] D4. Products / Cart / Checkout — domain/application/infrastructure ขั้นต้นพอให้ flow แกนทำงาน
  (business logic เต็ม = spec ต่อ feature) · Satisfies: REQ-1.1, REQ-2.1

## กลุ่ม E — Hosts

- [ ] E1. TenantConsole (public-facing) — `Mediator.SourceGenerator` (`PrivateAssets=all`) auto-register
  handler ผ่าน `AddMediator(...)` + build diagnostic เมื่อ request ไม่มี handler (ไม่ปิด warning), wire
  `ModuleAssemblies` + `AddBuildingBlocksInfrastructure()`, webhook endpoint (route by connection id /
  signed path) · Satisfies: REQ-2.3, REQ-2.4, REQ-9.2, REQ-10.1, REQ-10.3
- [ ] E2. AdminConsole (internal-only) — แยก authz scope; admin endpoint เรียกผ่าน session Tenant
  Console ไม่ได้; admin cross-tenant = DB principal แยก + reason/correlation id → audit
  · Satisfies: REQ-10.1, REQ-10.2, REQ-5.5

## กลุ่ม F — Tests

- [ ] F1. `SharedKernel.Tests` — Money validation (currency/non-negative/same-currency/JSON)
  · Satisfies: REQ-3.2, REQ-3.3, REQ-3.4, REQ-3.5, REQ-11.1
- [ ] F2. `BuildingBlocks.Tests` — idempotency multi-key, outbox commit-with-tx, vault mask/write-only,
  RLS interceptor set `SESSION_CONTEXT` ตอน connection-open · Satisfies: REQ-6.*, REQ-7.1, REQ-7.3,
  REQ-5.2, REQ-11.1
- [ ] F3. `Payments.Tests` — webhook pipeline order, signature reject, replay no-double-transition;
  property-based test (webhook/idempotency/money) · Satisfies: REQ-9.3, REQ-9.4, REQ-6.5, REQ-11.5
- [ ] F4. `Orders.Tests` — รับ `PaymentPaid` verify amount+currency · Satisfies: REQ-9.5, REQ-11.1
- [ ] F5. `Hosts.Tests` — tenant leak ปิด รวม pooled connection ไม่ retain tenant เดิม; admin scope
  separation · Satisfies: REQ-5.4, REQ-5.6, REQ-10.2, REQ-11.1
- [ ] F6. CI gate — `dotnet build -warnaserror` + `dotnet test` + `dotnet format --verify-no-changes` +
  `check-secrets` (ห้าม hardcode/log secret/PII) required; ไม่มี `[Fact(Skip=...)]`/`.only` ค้าง
  · Satisfies: REQ-11.4, REQ-1.6, REQ-7.2

## กลุ่ม G — Migrations + RLS SQL policy

- [ ] G1. EF migration baseline ต่อ context (`ProducerDbContext`/`AdminDbContext`) — spine tables
  (outbox/idempotency/vault) + module entity; UTC column `Utc` suffix; `AmountCurrency` HasMaxLength(3)
  · Satisfies: REQ-3.6, REQ-4.4
- [ ] G2. **RLS SQL policy migration** (producer context) — security predicate FUNCTION เทียบ
  `SESSION_CONTEXT('TenantId')` + `CREATE SECURITY POLICY` (raw SQL ใน `Up`/`Down`); admin cross-tenant
  ผ่าน DB principal แยก · Satisfies: REQ-5.1, REQ-5.2, REQ-5.5

## Backlog (กั้นออกจาก foundation — เปิด spec ของตัวเองเมื่อเริ่ม)

- [ ] Z1. **Real PSP HTTP integration** — ยิง 2C2P / Omise/Opn จริง แทน adapter stub (`// ponytail:`);
  เริ่มงานเมื่อเปิด spec ของตัวเอง (ไม่ทำใน foundation spec นี้) · Satisfies: REQ-12.1, REQ-12.3
- [ ] Z2. **Vault KMS/HSM provider** — envelope encryption จริง (per-tenant KEK ใน KMS/HSM, DEK ต่อ
  secret, key id+version, rotation runbook) แทน provider พื้นฐาน · Satisfies: REQ-12.2

## Definition of Done (foundation)

- กลุ่ม A เขียว (เสร็จแล้ว); B–G เขียวครบ; ทุก REQ มี task ที่อ้างถึงและ test คุ้ม.
- `dotnet build -warnaserror` 0 warning + `dotnet test` ผ่านทั้ง solution.
- stub ทุกตัว mark `// ponytail:` พร้อม upgrade path; backlog Z1/Z2 ไม่ถูกทำใน spec นี้.
- ไม่มี secret hardcode/log; ไม่มี non-redirect / card-field / settlement / billing / issuance หลุดเข้ามา.
