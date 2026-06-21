# Design — foundation-scaffold

> HOW โครงสร้างถูกสร้าง. อ้าง PLAN.md (16 decisions), ARCHITECTURE.md, CODING_STANDARDS.md,
> SECURITY_RULES.md, stack/dotnet.md. ทุก element อ้าง REQ-ID จาก requirements.md.
>
> สถานะ: solution **scaffold แล้ว** และ shared spine **compile เขียว**. เอกสารนี้บันทึก HOW ที่ถูก
> เลือก ไม่ใช่ข้อเสนอใหม่ — โมดูล/host ที่ยังไม่เต็มสร้างทับสัญญาที่นี่.

## D0. ที่มาของการตัดสินใจ (decision map)

decision หลักของ foundation มาจาก PLAN.md 16 decisions + module list, สรุป mapping:

| ด้าน | Decision (PLAN) | สะท้อนใน |
|---|---|---|
| รูปทรง | Modular Monolith + Clean Architecture + CQRS, 1 deployable | REQ-1, D1 |
| สื่อสารข้ามโมดูล | Contracts + Mediator `INotification` เท่านั้น | REQ-2, D2 |
| เงิน | `Money { long, ISO4217 }` ใน SharedKernel, ไม่มี decimal/float ที่ seam | REQ-3, D3 |
| isolation floor | SQL Server native RLS + `SESSION_CONTEXT` ต่อ connection | REQ-5, D5 |
| จ่ายไม่ซ้ำ / publish | idempotency multi-key + outbox | REQ-6, D6 |
| secret | vault write-only, envelope encryption (KMS backlog) | REQ-7, REQ-12, D7 |
| PSP | redirect-only (PCI SAQ A), 2 PSP × 3 ช่องทาง | REQ-8, D8 |
| webhook | source of truth, verify→claim→confirm→transition→emit ใน 1 tx | REQ-9, D9 |
| console | สอง host แยก authz scope | REQ-10, D10 |
| ฐาน enforcement | git hooks + CI + architecture test | REQ-11, D11 |

> **หมายเหตุ layout:** เพิ่ม `src/BuildingBlocks/{BuildingBlocks.Application,BuildingBlocks.Infrastructure}`
> เข้ามาในผังจาก stack/dotnet.md เดิม (ที่มีแค่ SharedKernel / Contracts / Modules / Hosts) เพื่อรวม
> cross-cutting spine (clock, UoW, idempotency, outbox, vault, RLS, DbContext) ไว้ที่เดียว ไม่กระจายซ้ำ
> ในทุกโมดูล. นี่คือ delta จาก ARCHITECTURE.md — ถ้าผัง canon ยังไม่สะท้อน ให้ /spec-retro sync.

## D1. Project graph + dependency direction (REQ-1)

```
SharedKernel  ← Contracts ← BuildingBlocks.Application ← BuildingBlocks.Infrastructure
                    ↑              ↑                              ↑
        <M>.Domain ←  <M>.Application  ←  <M>.Infrastructure  →  Hosts/{TenantConsole,AdminConsole}
```

- `<M>.Application` → เฉพาะ `<M>.Domain` + `Contracts` + `BuildingBlocks.Application` (REQ-1.3).
- `<M>.Infrastructure` → `<M>.Application` + `BuildingBlocks.Infrastructure` (REQ-1.4).
- Domain ไม่อ้าง EF/ASP.NET (REQ-1.2).
- โมดูลห้าม reference `*.Domain`/`*.Infrastructure` ของโมดูลอื่น → `Architecture.Tests` บังคับ (REQ-1.5).
- `dotnet build -warnaserror`, nullable-clean, 0 warning (REQ-1.6) — ฐานคือ TreatWarningsAsErrors.

## D2. Cross-module messaging (REQ-2)

- `Contracts` ถือ `PaymentPaid : INotification` (`SchemaVersion = "v1"`) — message เดียวที่โมดูลอื่น
  reference ได้.
- `Mediator.SourceGenerator` วางที่ Hosts (`PrivateAssets=all`); `Mediator.Abstractions` ที่ project ที่
  นิยาม message/handler. handler ลงทะเบียนอัตโนมัติผ่าน `AddMediator(...)` (REQ-2.3).
- build diagnostic เมื่อ request ไม่มี handler — ไม่ปิด (REQ-2.4).
- lifetime: `IMediator` Singleton ได้; handler/pipeline ที่พึ่ง `DbContext` ต้อง Scoped หรือ inject
  `IDbContextFactory`; `ValidateScopes=true` + DI validation test กัน captive dependency (REQ-2.5).
- pipeline behavior (เช่น `IdempotencyBehavior`, validation, tenant-scope guard) เพิ่มเองที่ Application.

## D3. Money pattern (REQ-3)

- `Money` ใน SharedKernel: `readonly record struct { long MinorUnits; string Currency; }`,
  `Of()` validate ISO4217 (THB/USD/JPY ผ่าน `Iso4217`) + non-negative, `Add` บังคับ same-currency.
- `MoneyJsonConverter` สำหรับ JSON camelCase.
- **EF mapping rule (สำคัญ — กัน friction กับ validating ctor):** ห้าม map `Money` เป็น owned/complex.
  entity เก็บสอง scalar: `long AmountMinorUnits` + `string AmountCurrency`, expose
  `public Money Amount => Money.Of(AmountMinorUnits, AmountCurrency);` ใน configuration เรียก
  `builder.Ignore(x => x.Amount)` + map สองคอลัมน์ (`AmountCurrency` HasMaxLength(3)) (REQ-3.6).
- ที่ Orders↔Payments seam: ใช้ `Money` ร่วม; Orders verify amount+currency ตอนรับ `PaymentPaid` (REQ-9.5).

## D4. BuildingBlocks spine (REQ-4)

`BuildingBlocks.Application` (refs Mediator + SharedKernel + Contracts) — abstraction:
`ITenantContext`, `IClock`, `IUnitOfWork`, `IIdempotencyStore`, `IOutbox`, `IVaultSecretStore`,
`ITenantScoped` (marker).

`BuildingBlocks.Infrastructure` (refs EF Core + ข้างบน) — implementation:
- `ProducerDbContext` (schema `producer`) เป็นเจ้าของตาราง Outbox / Idempotency / Vault และ discover
  `IEntityTypeConfiguration` ของแต่ละโมดูลจาก `ModuleAssemblies.Producer` ตอน model-build (REQ-4.4).
- `AdminDbContext` (schema `admin`).
- `AddBuildingBlocksInfrastructure()` ลงทะเบียน clock, RLS interceptor, `IUnitOfWork`,
  `IIdempotencyStore`, `IVaultSecretStore`, `IOutbox`, OutboxDispatcher (REQ-4.3).
- โมดูล handler query ผ่าน repository ที่ inject `ProducerDbContext` แล้วใช้ `producerDb.Set<TEntity>()`.

## D5. RLS isolation floor (REQ-5)

- floor = SQL Server native RLS + `SESSION_CONTEXT('TenantId')`; EF global query filter = ชั้นเสริม.
- `SESSION_CONTEXT` **per-connection** → set ตอน connection-open ผ่าน `DbConnectionInterceptor`
  (ไม่ใช่ต่อ query — pooled connection คนละตัวไม่เห็นค่า; spike 2026-06-21 ยืนยัน) (REQ-5.2).
- RLS SQL policy (security predicate + FUNCTION เทียบ `SESSION_CONTEXT('TenantId')`) ติดผ่าน migration
  ของ producer context (D11 / tasks).
- `ITenantScoped` command/query ที่ไม่มี tenant ใน context → ถูกปฏิเสธโดย pipeline guard (REQ-5.3).
- ban raw SQL ข้าม tenant + `IgnoreQueryFilters` ข้าม tenant; test พิสูจน์ leak ปิด รวม pooled
  connection ไม่ retain tenant เดิม (REQ-5.4, 5.6).
- admin cross-tenant = DB principal แยก + reason/correlation id → audit (REQ-5.5).

## D6. Idempotency + Outbox (REQ-6)

- `IIdempotencyStore.TryBeginAsync(keys, context)`: `true` = first delivery, `false` = replay (REQ-6.1).
- multi-key atomic upsert อย่างน้อย `(psp,eventId)` + `(psp,externalChargeId,normalizedStatus)` (REQ-6.2).
- `IOutbox.Enqueue(notification)` track row, commit พร้อม SaveChanges ของ handler (REQ-6.3) — ไม่ publish
  นอก transaction.
- OutboxDispatcher: poll + lock/lease + poison/DLQ; consumer idempotent (REQ-6.4).
- replay (`false`) → ไม่ transition, ไม่ enqueue ซ้ำ (REQ-6.5).

## D7. Vault (REQ-7, backlog REQ-12)

- เข้าถึง secret ผ่าน `IVaultSecretStore` เท่านั้น (Store/Reveal/Masked/Exists).
- secret write-only; อ่านกลับเพื่อแสดง = mask (REQ-7.3).
- ออกแบบรองรับ envelope encryption (per-tenant KEK ใน KMS/HSM, DEK ต่อ secret, key id+version +
  rotation) — **provider KMS จริง = backlog**; foundation มี provider พื้นฐานพอให้ flow ทำงาน, mark
  `// ponytail:` ระบุ upgrade path (REQ-7.4, 12.2).
- ห้าม hardcode/log secret → gate `check-secrets` + review (REQ-7.2).

## D8. PSP boundary — redirect-only (REQ-8)

- `IPspAdapter` คืน hosted redirect URL เท่านั้น — ไม่มี card field / Omise.js / hosted-fields /
  iframe / display-QR (REQ-8.1).
- `enum PspCode { TwoCTwoP, Omise }` code string `"2c2p"`/`"omise"`; method code `"card"`/`"promptpay"`/
  `"installment"`; เก็บค่า PSP verbatim (REQ-8.2).
- Omise PromptPay = Payment Links+ (hosted `transaction_url`) เท่านั้น ห้าม source+charge (REQ-8.3).
- **foundation:** adapter ยังไม่ยิง HTTP จริง — stub mark `// ponytail:` (REQ-12.1).
- Non-Goal guard: requirement ที่นำไปสู่ non-redirect / card field / settlement / billing / public
  onboarding / issuance → หยุดถามก่อน (REQ-8.4).

## D9. Webhook pipeline (REQ-9)

ลำดับบังคับ ใน `IUnitOfWork.ExecuteInTransactionAsync` เดียว:

```
route by PSP connection id / signed path (ยังไม่ trust tenant/PSP จาก raw path)
  → verify signature (secret จาก IVaultSecretStore)
  → claim idempotency (multi-key)
  → fetch-to-confirm กับ PSP
  → transition สถานะ
  → IOutbox.Enqueue(PaymentPaid)
```

- signature ไม่ผ่าน → ปฏิเสธ ไม่ transition (REQ-9.4).
- browser return = UX เท่านั้น (REQ-9.1).
- Orders รับ `PaymentPaid` → verify amount + currency ก่อน Paid (REQ-9.5).

## D10. Hosts (REQ-10)

- `TenantConsole` (public-facing, 3 บริษัทใช้ร่วม) + `AdminConsole` (internal-only) — คนละ deployable,
  backend/data ชุดเดียว.
- admin endpoint (cross-tenant/approve/config) เรียกผ่าน session Tenant Console ไม่ได้ (REQ-10.2).
- `Mediator.SourceGenerator` ที่ host; wire `ModuleAssemblies(producer, admin)` เข้า DI +
  `AddBuildingBlocksInfrastructure()` (REQ-10.3).

## D11. Enforcement + tests (REQ-11)

- ฐาน enforcement (Tier 1): `.githooks/` (pre-commit/pre-push) + CI gate (build + test + format) —
  gate ทุก agent/คนที่ commit/PR.
- test project: SharedKernel / BuildingBlocks / Payments / Orders / Architecture / Hosts (REQ-11.1).
- `Architecture.Tests` บังคับทิศ dependency (REQ-11.2).
- naming convention (REQ-11.3) — `Psp` ไม่ใช่ `PSP`, `Async` suffix, `I` prefix, `_camelCase`,
  `{Entity}Id`, `Utc` suffix, JSON camelCase.
- critical path (webhook/idempotency/money) → property-based test (REQ-11.5, `/spec-pbt`).
- stub ตั้งใจ → `// ponytail:` (REQ-11.6).

## D12. Migration baseline

- migration ต่อ context: `dotnet ef migrations add <PascalCaseName> --context <ProducerDbContext|AdminDbContext>
  --project src/Modules/<M>/<M>.Infrastructure` (หรือ BuildingBlocks.Infrastructure สำหรับ spine table).
- datetime เก็บ UTC, column `Utc` suffix.
- RLS SQL policy ติดผ่าน migration ของ producer context (security predicate FUNCTION + SECURITY POLICY)
  — เป็น raw SQL ใน migration `Up`/`Down` (ไม่ใช่ EF model) เพราะ RLS เป็น DDL.

## Open questions / สิ่งที่ตั้งใจเลื่อน

- real PSP HTTP integration (2C2P / Omise) — backlog, spec แยก (REQ-12.1).
- vault KMS/HSM provider จริง — backlog, spec แยก (REQ-12.2).
- โมดูลที่ยังไม่ build out เต็ม (Products/Cart/Checkout business logic) — spec ต่อ feature แยกออกจาก
  foundation นี้.
