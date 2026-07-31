# โครงสร้าง `src/` — pol-core (insurance sales platform)

> เอกสารอ้างอิงโครงสร้างจริงของโค้ดใน `src/` (file-by-file role map).
> ground truth คือไฟล์จริง; เอกสารนี้สรุปบทบาท ไม่ใช่ spec. canonical architecture: [`.ai/shared/ARCHITECTURE.md`](../../.ai/shared/ARCHITECTURE.md) · product canon: [`.ai/shared/PROJECT_CONTEXT.md`](../../.ai/shared/PROJECT_CONTEXT.md) · isolation floor: [`db-connection-and-rls.md`](db-connection-and-rls.md) · โมดูลเชิงลึก: [`platform-modules.md`](platform-modules.md)

## ภาพรวม

รูปทรง: **Modular Monolith** ตามแนว **Clean Architecture + CQRS** — 1 codebase, แยกเป็น 46 `.csproj`, deploy เป็น **host เดียว (Api)**. command/query แยกผ่าน Mediator; โมดูลคุยข้ามกันด้วย `INotification` ผ่าน transactional outbox ไม่อ้างถึงกันตรง.

- TargetFramework: `net10.0` ทุก project · `LangVersion 14.0` · `Nullable enable` (จาก `Directory.Build.props` กลาง)
- package version จัดกลางที่ `Directory.Packages.props` (Central Package Management)
- Mediator: source-generated (`Mediator.SourceGenerator`) — handler ถูก discover ตอน compile
- DB: SQL Server 2025 catalog `VCentralPay`, 7 schema (`shop`/`txn`/`admin`/`merch`/`iam`/`cfg`/`dbo` — `SchemaNames.cs`)

### Dependency rule (ทิศชี้เข้า domain)

```
Hosts (Api)                     composition root — ผูกทุกอย่างเข้าด้วยกัน
   │  ลงไปได้ทุกชั้น
   ▼
Persistence.* (runtime context) + Infrastructure (per-module + BuildingBlocks)
   │  ขึ้นกับ                     EF context/repo/adapter, PSP adapter, vault
   ▼
Application (per-module + BuildingBlocks)       command/query/handler + ports (interface)
   │  ขึ้นกับ
   ▼
Domain (per-module) + SharedKernel + Contracts  entity/value object/event บริสุทธิ์ ไม่มี dependency นอก
```

กฎ: Domain ไม่ขึ้นกับใคร (นอกจาก SharedKernel). Application รู้จัก Domain + ประกาศ **port** (interface) ที่ Infrastructure/Persistence ไป implement. Host เป็นที่เดียวที่ประกอบ concrete เข้า interface.

### โครงสร้าง top-level

```
src/
  SharedKernel/                 domain primitive ใช้ร่วมทุกโมดูล (Money decimal, Entity, ISO4217)
  Contracts/                    integration event ข้ามโมดูล (PaymentPaid, CheckoutConfirmed, ...)
  BuildingBlocks/
    BuildingBlocks.Application/    abstraction กลาง (actor, outbox, idempotency, vault port, SFS, exception)
    BuildingBlocks.Infrastructure/ migration-owner + write-guard base + outbox/idempotency/vault/observability
    BuildingBlocks.Web/           cross-cutting HTTP (cors, health, problem-details, correlation id)
  Persistence/                  runtime data-plane — 3 context แยกตาม cluster + provisioning
    Persistence.ControlPlane/     admin/iam/cfg cluster
    Persistence.MerchantUsers/    merch identity/session cluster
    Persistence.MerchantRuntime/  shop/txn + merchant data cluster (isolation floor จริง)
    Persistence.Provisioning/     cross-context UoW จุดเดียวในระบบ
  Modules/                      12 โมดูลธุรกิจ × 3 ชั้น (Domain/Application/Infrastructure)
    Products/ Carts/ Checkouts/ Orders/ Payments/     ← funnel การขาย
    Merchants/ Admins/ Iam/                           ← identity + RBAC
    Divisions/ Levels/ Offices/ Positions/            ← master data (schema cfg)
  Hosts/
    Api/                          host เดียว — HTTP + webhook ingest + background outbox dispatch
```

ลำดับโดเมนตาม flow ธุรกิจ: **Products → Carts → Checkouts → Orders → Payments**.

> **ไม่มี host `Worker` แล้ว** — โปรเจกต์ถูกลบทั้งตัว (spec `multi-tier-deployment`, 2026-07-22); outbox dispatcher รันเป็น `IHostedService` ใน Api process เดียวกัน (§5). ถ้าเจอโฟลเดอร์ `src/Hosts/Worker/` หรือ `src/Modules/MasterData/` บนเครื่อง = **ซาก `obj/` ที่ไม่ได้ track** ลบทิ้งได้.

---

## 1. Foundation — `SharedKernel/` + `Contracts/`

ชั้นล่างสุด ไม่มี dependency ภายนอก. ทุกโมดูล reference ได้.

### SharedKernel (`SharedKernel.csproj` — ไม่มี ProjectReference)

| ไฟล์ | บทบาท | key types |
|------|-------|-----------|
| `Entity.cs` | base class ของ DDD: เทียบ identity ด้วย type+Id | `Entity<TId>`, `AggregateRoot<TId>` (ถือ `IDomainEvent` collection), `IDomainEvent` marker |
| `Money.cs` | value object เงิน non-negative — แกนของ "ไม่มี float/double ที่ cross-module seam" | `readonly record struct Money { decimal Amount; string Currency }`; **decimal scale ≤ 4** (supersede minor-units เดิม ตั้งแต่ v5/rf1 REQ-6); factory `Of()` validate currency + non-negative + scale; `Add()` กัน currency ผิด; `default(Money)` ใช้ไม่ได้ (throw) |
| `Iso4217.cs` | registry สกุลเงินขั้นต่ำ (THB/USD/JPY) | `IsSupported(code)`, `MinorUnitDigits(code)` (THB/USD=2, JPY=0) — throw เมื่อไม่รู้จัก |
| `MoneyJsonConverter.cs` | (de)serialize `Money` แล้ว re-validate ผ่าน `Money.Of()` | `JsonConverter<Money>` — ใช้ใน outbox payload + JSON API |

### Contracts (`Contracts.csproj` → SharedKernel, Mediator.Abstractions)

| ไฟล์ | บทบาท |
|------|-------|
| `PaymentPaid.cs` | integration event ที่ Payments emit เมื่อ PSP ยืนยันจ่ายแล้ว; Orders consume แบบ idempotent + re-verify amount/currency |
| `CheckoutConfirmed.cs` | Checkouts → Orders: checkout ถูก confirm แล้ว เปิด order ได้ (ปิด TODO cross-module เดิม) |
| `CustomerOrderNotification.cs` | Orders → notification sink: ส่งลิงก์สรุป order ให้ลูกค้า |
| `MerchantUserRegistrationSubmitted.cs` | Merchants identity → downstream: มีคำขอสมัคร merchant-user เข้ามา |

> `Money` ข้าม seam เป็น value object เสมอ (ไม่ใช่ `long`/`decimal` ดิบ) — ปิด seam ที่ ARCHITECTURE.md เตือนไว้.

---

## 2. BuildingBlocks — โครงสร้างพื้นฐานข้ามโมดูล

### 2.1 BuildingBlocks.Application (`→ SharedKernel, Contracts, Mediator.Abstractions`)

abstraction ที่ระดับ application ใช้ร่วม — transactional seam, actor/merchant isolation, SFS, exception, vault/idempotency **port** (interface เปล่า ไม่มี impl).

| ไฟล์ | บทบาท |
|------|-------|
| `IClock.cs` | source เวลา UTC แบบ test ได้ |
| `IUnitOfWork.cs` | commit transaction โดย handler ไม่ต้องเห็น DbContext — ห่อ idempotency-claim + state change + outbox enqueue เป็นก้อนเดียว |
| `IOutbox.cs` | เขียน integration event ลง outbox ใน tx เดียวกับ state (at-least-once) |
| `IIdempotencyStore.cs` | ledger กันซ้ำ multi-key (payment/webhook) — claim หลาย key อะตอม |
| `IActorContext.cs` / `IActorScope.cs` | **แกน isolation**: ambient merchant ต่อ request (จาก principal ไม่ใช่ URL) + explicit binding สำหรับ entry ที่ไม่มี auth (webhook, dispatcher). `HasActor=false` = ไม่ผูก merchant |
| `IMerchantScoped.cs` / `MerchantGuardBehavior.cs` | marker + **Mediator `IPipelineBehavior`** — กัน message `IMerchantScoped` ที่ไม่มี actor ผูก; throw ก่อนเข้า handler + ยิง `UnboundActor` telemetry |
| `IWriteAuthorizer.cs` / `WriteOperation.cs` | **write floor port** — `CanWrite(entityType, operation, targetMerchant)` default-deny; impl อยู่ที่ host (§5) |
| `ISecurityTelemetry.cs` | `DenialEvent`/`DenialCategory` (11 ค่า) — ทุก denial/anomaly ยิงเข้า channel เดียวไป Seq |
| `CorrelationId.cs` | correlation id จาก `Activity.Current` (host-agnostic — ใช้ได้ทั้ง HTTP request และ background dispatch) |
| `IVaultSecretStore.cs`, `IVaultMaintenance.cs`, `IVaultRevealAuditWriter.cs`, `IVaultRevealAuditVerifier.cs` | custody secret PSP: store/reveal/mask, re-wrap DEK ตอน rotate, tamper-evident reveal audit + verifier |
| `IWebhookMerchantResolver.cs` | map `pspConnectionId` → merchant สำหรับ webhook ที่ไม่มี auth |
| `IProvisioningWriter.cs` | port ของ cross-context provisioning (impl = `Persistence.Provisioning`) |
| `ISessionByTokenHash.cs` | lookup session จาก token hash (ใช้ร่วมทั้ง admin + merchant-user BFF) |
| `PagedQuery.cs`, `PagedResult.cs`, `FilterOption.cs`, `FilterOperator.cs`, `SortOption.cs`, `SortDirection.cs`, `SearchOption.cs`, `SfsLike.cs` | **SFS convention** (search/filter/sort/page) ใช้ร่วมทุก list endpoint — ดู [`search-filter-sort.md`](search-filter-sort.md) |
| `ConcurrencyConflictException.cs` → 409 · `ConflictException.cs` → 409 · `NotFoundException.cs` → 404 · `GoneException.cs` → 410 · `WriteGuardException.cs` · `MerchantBindingException.cs` | exception ที่ไม่ผูก EF; map เป็น HTTP status ที่ `ProblemDetailsExceptionHandler` |

### 2.2 BuildingBlocks.Infrastructure (`→ BuildingBlocks.Application`, EF Core SqlServer)

data-plane ที่ **ไม่ผูก cluster ใด cluster หนึ่ง**: migration ownership, write-guard base class, outbox/idempotency/vault entity + vault crypto, observability pipeline. (adapter/repo ที่ผูก cluster อยู่ที่ `src/Persistence/*` — §3)

**root**

| ไฟล์ | บทบาท |
|------|-------|
| `BuildingBlocksInfrastructureRegistration.cs` | DI กลาง — ผูก outbox/idempotency/vault/clock เข้า service collection |
| `SystemClock.cs` | impl `IClock` = `DateTime.UtcNow` |

**Persistence/** — migration ownership + write floor (ไม่ใช่ runtime context)

| ไฟล์ | บทบาท |
|------|-------|
| `PolDbContext.cs` | **migration-owner ตัวเดียวของทั้งระบบ** — `sealed`, ถือ full relational model (cross-context FK จริง), discover entity config ของทุกโมดูลจาก `ModuleAssemblies` ตอน `OnModelCreating`. **ไม่ registered ที่ runtime เลย** (`dotnet ef migrations add` ชี้มาที่นี่เท่านั้น) |
| `GuardedRuntimeDbContext.cs` | **write floor** — abstract base ของ 3 runtime context; **seal ทั้ง 4 `SaveChanges` overload** ผ่าน `GuardPendingChanges()` ตัวเดียว (derived เขียนทับไม่ได้). ต่อ tracked entry: append-only reject Modified/Deleted → tenant key ห้าม `Guid.Empty` + immutable-after-insert (ยกเว้น NULL→value ครั้งเดียว) → `IWriteAuthorizer.CanWrite` default-deny. ทุก denial ยิง `ISecurityTelemetry` |
| `AppendOnlyDescriptor.cs` | annotation mark entity ที่เป็น audit trail (guard reject Modified/Deleted) |
| `TenantKeyDescriptor.cs` | annotation ระบุว่า entity มี tenant key คอลัมน์ไหน (ใช้ทั้ง read filter + write guard; arch test เช็ค deny-by-omission) |
| `AmbientActor.cs` | holder (Scoped) ของ explicit actor binding (`IActorScope`) — 1 binding ต่อ scope, ไม่ nest |
| `ModuleAssemblies.cs` | singleton ถือ list assembly ของโมดูล → ใช้ apply `IEntityTypeConfiguration` ของทุกโมดูล |
| `SchemaNames.cs` | ชื่อ schema เป็น const เดียว: `shop`/`txn`/`admin`/`merch`/`iam`/`cfg`/`dbo` |

**Persistence/Migrations/** (EF generated — 19 migration, ห้ามแก้มือ `*.Designer.cs` / `PolDbContextModelSnapshot.cs`)

| migration | ผล |
|-----------|-----|
| `20260712185344_InitialSchema` | schema layout v5 + ตารางทั้งระบบ (big-bang reset — ไม่มี migration ก่อนหน้านี้เหลือ) |
| `20260712185646_SecurityObjects` · `20260712185912_SeedData` | security object + seed catalog (permission/role/master data) |
| `20260719081817_RlsTeardownAndOnePrincipal` | **ถอด RLS ทั้งระบบ** (security policy / predicate fn / bypass proc / `pol_admin`+`pol_worker`+`pol_rls_bypass`) เหลือ principal เดียว `pol_app` |
| `20260720044409_DropEmptySecSchema` | ลบ schema `sec` ที่ว่างหลัง teardown |
| `20260720163732_InsuranceProductFields` · `20260720165648_InsuranceProductSeed` | insurance-pivot: `SumInsured`/`CoverageDurationDays`/`Insurer` บน Product + seed |
| `20260720171458_OrderLinesAndCheckoutSessionLines` · `20260720180545_GrantInsuranceLineTables` | line table ต่อผู้เอาประกัน + GRANT ให้ `pol_app` |
| `20260720175721_RevealAudits` | reveal audit ของ PII ผู้เอาประกัน |
| `20260723122929_RenameOrderLinesToOrderItems` | rename OrderLine → OrderItem (code + DB) |
| `20260723150000_SeedPolicyPermissions` · `20260723160000_OrderItemPolicies` · `20260723160500_GrantOrderItemPolicyTables` | policy-reference-record: permission + ตาราง `ItemPolicy`/`ItemPolicyAudit` + GRANT |
| `20260726151538_OneOpenPaymentSessionPerOrder` | filtered unique index: 1 payment session ที่ยังเปิดอยู่ต่อ order |
| `20260730072057_ProductsInsuranceDocument` | pivot `Product` เป็นเอกสารประกัน (recreate `shop.Products` + seed 6 แถว) |
| `20260730081227_CheckoutChainDocumentFields` | snapshot chain Cart -> Checkout -> Order ใช้ field เอกสารแทน field แผนประกัน |
| `20260730113459_ProductsSp52Alignment` | products-sp-53-alignment: DROP 8 คอลัมน์ (`BranchCode`/`IsActive`/`CreatedAt` + currency ×5), rename ×4 ให้ตรง §5.2, `decimal(19,2)` |
| `20260730143112_ProductsCentralCatalogue` | ถอด `MerchantId` ออกจาก `shop.Products` — เอกสารประกันเป็นแคตตาล็อกกลาง ไม่ผูก merchant อีกต่อไป; index เปลี่ยนจาก `MerchantId`-prefixed เป็น `DocumentNo` unique ทั้งระบบ + `SaleCode`+`PaymentStatus`; `Down()` restore แค่ shape (ownership เดิมกู้คืนไม่ได้) |

> **กับดัก**: ตารางใหม่ทุกตารางต้องมี `GRANT` ให้ `pol_app` เป็น statement แยก — SQLite unit test จับ grant ที่หายไปไม่ได้.

**Outbox/**, **Idempotency/**, **Vault/**, **DataProtection/**, **Notifications/**, **Observability/**, **Provisioning/**

| โฟลเดอร์ | บทบาท |
|------|-------|
| `Outbox/` | `OutboxMessage` + `MerchantUserOutbox` entity + config + `OutboxSerializer` (camelCase + `MoneyJsonConverter`). Id = UUIDv7 (เรียงตามเวลา) |
| `Idempotency/` | `IdempotencyRecord` entity + config — claim = insert 1 row ต่อ key, ดัก duplicate PK (SqlException 2627/2601) = สัญญาณ replay |
| `Vault/` | envelope encryption: `VaultEnvelope` (HKDF-SHA256 derive KEK ต่อ merchant, AES-256-GCM), `VaultKeyring`/`VaultKeyringFactory` (fail-fast ตอน boot, `ResolveOrNull` fail-closed), `VaultOptions`, `VaultSecretBlob` + `VaultRevealAudit` entity (hash chain, genesis = 32 zero bytes) |
| `DataProtection/` | `DataProtectionKey` entity + config — key ring ของ OIDC correlation cookie เก็บลง DB |
| `Notifications/` | sink ของ `CustomerOrderNotification` |
| `Observability/` | `ISecurityTelemetry` impl — bounded channel (10k, non-blocking) + `BackgroundService` POST CLEF JSON ไป **Seq**, retry 3x แล้ว fallback log (ไม่ดรอปเงียบ) |
| `Provisioning/` | `ProvisioningOperation` entity (idempotency ledger ของ provisioning) + `ProvisioningGuards` (fail-fast credential/OIDC ตอน boot) |

### 2.3 BuildingBlocks.Web (`→ BuildingBlocks.Application, BuildingBlocks.Infrastructure`; FrameworkReference AspNetCore)

cross-cutting HTTP — observability, cors, health, error. (auth/OIDC **ไม่อยู่ที่นี่แล้ว** — ย้ายไป `Hosts/Api/Admins/` + `Hosts/Api/Merchants/` เพราะเป็น per-provider BFF)

| ไฟล์ | บทบาท |
|------|-------|
| `CorrelationIdMiddleware.cs` | stamp `X-Correlation-ID` ต่อ request (reuse ถ้า well-formed ไม่งั้น mint) + ดัน correlation/merchant id เข้า logging scope (id เท่านั้น ไม่ PII); `AddJsonConsoleLogging()`, `UseCorrelationId()` |
| `CorsExtensions.cs` | per-request policy provider — admin plane (`/api/v1/admins/*`) credentialed, merchant plane default; origins จาก `Cors:AllowedOrigins`, **ไม่** `AllowAnyOrigin`; origins ว่าง = ปิด cross-origin (safe default) |
| `HealthChecks.cs` | `AddReadinessHealthChecks` (DB `CanConnectAsync` + vault active key) + `MapPolHealthChecks` → `/health/live` (เปล่า), `/health/ready` (tag `ready`), body สั้นไม่หลุด topology |
| `ProblemDetailsExceptionHandler.cs` | map exception → RFC7807: `NotFound`→404, `Concurrency`/`Conflict`→409, `Gone`→410, `ArgumentException`→400, `MerchantBinding`/`WriteGuard`→500 opaque, อื่น→500. Detail เป็น string คงที่ต่อ bucket (ไม่ใช้ `exception.Message`), log เต็มฝั่ง server |

---

## 3. Persistence — runtime data-plane (4 assembly)

> ชั้นนี้ **ไม่มีในเอกสารเวอร์ชันก่อน** — เกิดจาก spec `rls-to-query-filter` (2026-07-19) ที่ถอด SQL RLS ออกทั้งระบบแล้วย้าย isolation floor มาไว้ที่ app layer. ราย­ละเอียดเต็ม + flow A-E: [`db-connection-and-rls.md`](db-connection-and-rls.md).

**สถาปัตยกรรม 4 context**: `PolDbContext` (§2.2) เป็น migration-owner อย่างเดียว ไม่ registered ที่ runtime; runtime ใช้ **3 context แยกตาม co-commit cluster** ซึ่งทุกตัวเป็น `internal sealed` (host เห็น type ตรง ๆ ไม่ได้) และสืบทอด `GuardedRuntimeDbContext` ตัวเดียวกัน.

| Assembly / Context | schema ที่คุม | read filter | หมายเหตุ |
|---|---|---|---|
| `Persistence.ControlPlane` / `ControlPlaneDbContext` | `admin`, `iam`, `cfg`, `dbo.DataProtectionKeys` | **ไม่มี** (control plane ไม่มี merchant dimension) | admin user/session/audit/role assignment + IAM catalog + master data 4 ตัว + provisioning ledger |
| `Persistence.MerchantUsers` / `MerchantUserDbContext` | `merch` (identity/session) | เฉพาะ `Users`/`RoleAssignments` (`CurrentMerchant`) | merchant-user identity, external login, registration audit/notice, user outbox |
| `Persistence.MerchantRuntime` / `MerchantRuntimeDbContext` | `shop`, `txn`, `merch` (data) | **ทุก entity** — `MerchantId == CurrentMerchant` | **นี่คือ isolation floor จริง**: cart/checkout/order/product/payment/psp connection/vault/outbox |
| `Persistence.Provisioning` / `ProvisioningCoordinator` | ข้าม 2 context | — | **จุดเดียวในระบบ** ที่ 2 runtime context แชร์ connection+transaction เดียวกัน |

ทุก context รับ `(options, [IActorContext], IWriteAuthorizer, ISecurityTelemetry)` — adapter ของ port ใน Application layer **ต้องอยู่ assembly เดียวกับ context ที่มันแตะ** เสมอ.

**ไฟล์สำคัญต่อ assembly**

| ไฟล์ | บทบาท |
|------|-------|
| `Persistence.ControlPlane/ControlPlaneDbContext.cs` + `ControlPlanePersistenceRegistration.cs` + `ControlPlaneDbContextFactory.cs` | context + DI extension (`AddControlPlanePersistence`) + design-time factory |
| `Persistence.ControlPlane/Admins/*` | repo/store/reader ฝั่ง admin: `UserRepository`, `SessionStore`, `RoleRepository`, `ControlPlaneUnitOfWork`, `AuthorizationLease` (Super recheck), `AdminResolveLoginBySubject`, `UserSfs` |
| `Persistence.ControlPlane/Iam/*` · `Divisions|Levels|Offices|Positions/*` | IAM catalog store + `RoleSfs`; master-data store 4 ตัวใน schema `cfg` |
| `Persistence.MerchantUsers/MerchantUserDbContext.cs` + `Users/*` + `Outbox/*` | context + repo/session store + **pre-bind escape hatch** (`MerchantResolveLoginBySubject`, `MerchantRegistrationWriter/SubmitWriter` — ต้องอ่าน/เขียนก่อนรู้ `MerchantId`) + user outbox dispatcher/drain |
| `Persistence.MerchantRuntime/MerchantRuntimeDbContext.cs` + `MerchantRuntimeUnitOfWork.cs` + registration | context (query filter ทุก entity) + UoW + DI extension |
| `Persistence.MerchantRuntime/{Carts,Checkouts,Orders,Products,Payments,Merchants}/*` | EF config + repository ของโมดูลธุรกิจ (config **ไม่ได้อยู่ในโมดูล** — โมดูลถือแค่ domain/application/ports) |
| `Persistence.MerchantRuntime/Orders/Items/*` | policy-reference-record: `ItemPolicyRepository`, `AdminItemPolicyReader/Writer` (admin cross-merchant), `PolicyReportRepository` + `PolicyReportSfs`, `RevealAuditWriter` |
| `Persistence.MerchantRuntime/Outbox/OutboxDispatcher.cs` | `internal sealed : BackgroundService` — poll + lease outbox แล้ว publish ผ่าน Mediator; bind merchant ต่อ message ด้วย `IActorScope.Begin()` |
| `Persistence.MerchantRuntime/Vault/*` | impl vault store/maintenance/verifier + `VaultAuditAppender` (`sp_getapplock` ต่อ merchant กัน race บน hash chain) |
| `Persistence.MerchantRuntime/Webhooks/WebhookMerchantResolver.cs` · `Orders/OrderSummaryReader.cs` | escape-hatch: resolve merchant จาก connection id / resolve anonymous summary token (ทั้งคู่ต้องอ่านก่อนรู้ merchant) |
| `Persistence.Provisioning/ProvisioningCoordinator.cs` + `ProvisioningRegistration.cs` | Super-recheck `WITH (UPDLOCK, HOLDLOCK)` in-transaction + idempotency ledger + เขียน 2 context ใน tx เดียว |

**Escape-hatch allowlist**: `IgnoreQueryFilters()`/`ExecuteUpdate`/`ExecuteDelete`/raw SQL ทำได้เฉพาะไฟล์ที่อยู่ใน `tests/Architecture.Tests/BypassPrimitiveTests.AllowedPorts` — call site ใหม่นอก allowlist = **red CI ทันที** ไม่ต้องรอ code review จับ.

---

## 4. Modules — 12 โมดูลธุรกิจ

ทุกโมดูลรูปทรงเดียวกัน 3 ชั้น: **Domain** (entity/value/event บริสุทธิ์ → SharedKernel เท่านั้น) · **Application** (command/query/handler + repository **port** → Domain, Contracts, BuildingBlocks.Application, Mediator) · **Infrastructure** (EF config + `Add<Module>Module()`).

> **สำคัญ**: หลังแยกชั้น `Persistence.*` (§3) repository impl ส่วนใหญ่ **ย้ายออกจากโมดูลไปแล้ว** — `Add<Module>Module()` ของเกือบทุกโมดูลจึงเหลือเป็น **marker เปล่า** (`=> services`) ที่มีไว้ให้ `HostModuleAssemblies.All` อ้าง assembly ถึงได้เท่านั้น; มีแค่ `AddMerchantsModule()`/`AddPaymentsModule()` ที่ยังมี body จริง (photo store, PSP adapter/HttpClient). `Divisions`/`Levels`/`Offices`/`Positions`/`Iam` **ไม่ถูกเรียกใน `Program.cs` เลย** — entity config ของมันมาทาง `HostModuleAssemblies.All` และ store impl มาทาง `AddControlPlanePersistence`/`AddIamRoleManagement`.

แพทเทิร์นร่วม: entity เก็บเงินเป็น scalar (`*Amount:decimal` + `*Currency:string`) แล้ว map กลับเป็น `Money`; command/query ที่เป็นของ merchant เดียวเป็น `IMerchantScoped`; repository **ไม่มี** method save (UoW commit); isolation พึ่ง **EF query filter + write guard** (ไม่ filter merchant ในมือทุก query).

> **การตั้งชื่อ (hierarchical naming L1-L8)**: ชื่อ type ไม่ซ้ำ prefix ของโมดูล — `Checkouts.Domain.Session`, `Payments.Domain.Session`, `Orders.Domain.Items.Item` ฯลฯ. ที่ Program.cs จึง import ด้วย alias ชัดเจนแทน blanket `using`.

**หน้าที่/บทบาท + ผู้ดำเนินการต่อโมดูล** (quick reference — verify ตรงกับ `RequireAuthorization`/`RequirePermission` จริงใน `Program.cs`; รายละเอียดเชิงลึกดู [platform-modules.md](platform-modules.md)):

| Module | หน้าที่ / บทบาท | ผู้ดำเนินการ (actor) |
|---|---|---|
| Products | แคตตาล็อกเอกสารประกันกลาง (ไม่มี `MerchantId`) — list อ่านอย่างเดียวผ่าน HTTP, กรองด้วย `SaleCode` บังคับ; สร้างเอกสารผ่าน migration/seed เท่านั้น ณ ตอนนี้ (`POST /products` ถูกถอด; importer จาก SP ต้นทางยังไม่มี) | Merchant-user (list/read) |
| Carts | ตะกร้าเก็บ line ก่อน checkout — เพิ่ม/แก้/ลบ line, คำนวณ subtotal | Merchant-user |
| Checkouts | ล็อกราคา (จาก cart subtotal) + snapshot เงื่อนไขกรมธรรม์/ข้อมูลผู้เอาประกัน ณ เวลาซื้อ ก่อนเปิด order | Merchant-user |
| Orders | คำสั่งซื้อ + item snapshot + policy-reference record ที่แก้ทีหลังได้ + summary link + reconciliation/policy report | Merchant-user (สร้าง/list/แก้ policy ของ merchant ตัวเอง) · Admin (cross-merchant, tag Admin Orders) · ลูกค้าปลายทาง (อ่าน summary ผ่าน capability link — anonymous) |
| Payments | สร้าง payment session + redirect ไปหน้าจ่ายของ PSP + รับ webhook ยืนยันผลจ่าย (source of truth) | Merchant-user (สร้าง session/redirect) · PSP (webhook, server-to-server — ไม่มี human actor) |
| Merchants | merchant (tenant) entity + merchant-user identity ทั้งวงจร (สมัคร → approve/reject → login) | Admin (provision merchant, approve/reject merchant-user) · Merchant-user เอง (สมัคร/login ตัวเอง) |
| Admins | admin staff identity + session + ขอบเขต merchant ที่ admin คนนั้นเข้าถึงได้ | Admin เอง (login ตัวเอง) · Admin tier Super (สร้าง/จัดการบัญชี admin คนอื่น) |
| Iam | central RBAC catalog (permission/role) ใช้ร่วมทั้ง 2 plane | Admin (จัดการ role ฝั่ง admin) · Merchant-user ที่มีสิทธิ์ `roles.manage` (จัดการ role ฝั่ง merchant ตัวเอง) |
| Positions | reference data: รายชื่อตำแหน่งงาน (FK บน `AdminAccount`) | Admin (gate `user.manage`) |
| Offices | reference data: รายชื่อสำนักงาน/สาขา | Admin (gate `user.manage`) |
| Levels | reference data: รายชื่อระดับตำแหน่ง | Admin (gate `user.manage`) |
| Divisions | reference data: รายชื่อสายงาน/ฝ่าย | Admin (gate `user.manage`) |

> `MasterData` ไม่อยู่ใน 12 โมดูลนี้ — ซาก `obj/` ที่ไม่ได้ track จากก่อน masterdata-split (ดูหมายเหตุใน "โครงสร้าง top-level" ด้านบน), ไม่มี source เหลือ

### 4.1 Products — แคตตาล็อกเอกสารประกัน

| ไฟล์ | ชั้น | บทบาท |
|------|------|-------|
| `Product.cs` | Domain | aggregate = mirror ของ §5.2 ใน `docs/reference/vcentralpay-sp-quick-reference.pdf`: DocumentNo/ProductGroup/DocumentType/SaleCode/เลขเอกสาร/ช่วงคุ้มครอง/**TotalPremium:decimal(19,2)** + breakdown 5 ตัว/PaymentStatus/PaidDate — **ไม่มี `MerchantId`** (ถูกถอดใน `ProductsCentralCatalogue`, แคตตาล็อกกลางไม่ผูก merchant); `Create(ProductInput)`, `MarkPaid` เท่านั้น (ไม่มี `Rename`/`Deactivate` — ถูกลบใน products-sp-53-alignment) |
| `ProductInput.cs` / `ProductGroup.cs` / `DocumentType.cs` / `PaymentStatus.cs` / `InsuranceType.cs` | Domain | input record + enum ทั้งชุด (`InsuranceType` = computed `Motor`/`NonMotor`, ไม่มีคอลัมน์) |
| `CreateProductCommand.cs` | App | `ICommand<Guid>` + handler — **ไม่ใช่** `IMerchantScoped` (แคตตาล็อกไม่มี merchant); ไม่ reachable ผ่าน HTTP, เป็น write seam ที่จองไว้ให้ importer ในอนาคต — ผู้เรียกที่มีจริงตอนนี้คือ test เท่านั้น |
| `ListProducts.cs` | App | `ListProductsQuery` (page/limit + `required ProductFilterDto ProductFilters`) → `PagedResult<ProductListItem>` (32 field §5.2 + `Id`) — **ไม่มี SFS** แล้ว (`ProductSfs` ถูกลบ, เลิก inherit `PagedQuery`) |
| `GetProductById.cs` | App | lookup ต่อ id (ใช้ตอนตั้งราคา cart line ฝั่ง server) — คืน `ProductListItem` ตัวเดียวกัน (`ProductView`/`GetProductsQuery` ถูกลบ) |
| `DocumentPaidOnOrderPaidConsumer.cs` | App | consume `OrderPaid` -> `Product.MarkPaid` (idempotent ต่อ replay) |
| `IProductRepository.cs` | App | port (`Add`/`ListAsync`/`GetAsync`) |
| `ProductConfiguration.cs` / `ProductsModuleRegistration.cs` | Infra | EF config + `AddProductsModule()` (marker เปล่า — ดู §2.2 หมายเหตุ) |
| `Persistence.MerchantRuntime/Products/ProductConfiguration.cs` / `ProductRepository.cs` | Infra (นอกโมดูล) | EF config + repo ตัวจริงที่ผูก runtime context (§3) — โมดูลถือแค่ port (`IProductRepository`) |

### 4.2 Carts — ตะกร้า

| ไฟล์ | ชั้น | บทบาท |
|------|------|-------|
| `Cart.cs` | Domain | aggregate: MerchantId/Status/Items; `AddItem` (รวม line ซ้ำ), `RemoveItem`, `SetItemQuantity`, `Clear`, `MarkCheckedOut`; invariant: แก้ได้เฉพาะ `Open`, item ต้องสกุลเดียว |
| `Items/Item.cs` | Domain | line: ProductId/Quantity/UnitPrice; **denormalize `MerchantId` ของตัวเอง** (IDOR closure — กรองตรงตัวเอง ไม่พึ่ง join ผ่าน parent) |
| `CartStatus.cs` | Domain | `Open=0`, `CheckedOut=1` |
| `CreateCartCommand/Handler.cs`, `AddItemToCartCommand/Handler.cs`, `CartEdits.cs`, `GetCart.cs`, `AddItemResult.cs` | App | เปิด cart, เพิ่ม line, แก้/ลบ/ล้าง, อ่าน `CartView` (มี `Subtotal:Money?`) |
| `ICartRepository.cs` | App | port (`GetAsync` รวม Items) |
| `CartConfiguration.cs`, `Items/ItemConfiguration.cs`, `CartModuleRegistration.cs` | Infra | EF config + `AddCartModule()` |

### 4.3 Checkouts — ล็อกราคา + snapshot ผู้เอาประกัน

| ไฟล์ | ชั้น | บทบาท |
|------|------|-------|
| `Session.cs` | Domain | aggregate: MerchantId/CartId/**Amount:Money**/Status/CreatedAt/**NotificationRecipient?**; `Start`, `Confirm`, `Abandon` (throw ถ้าไม่ใช่ `Started`) |
| `Items/Item.cs` | Domain | **1 ผู้เอาประกันต่อ line**: ProductId/Quantity/UnitPrice + snapshot field เอกสาร (`DocumentNo`/`ProductGroup`/`DocumentType`/`PolicyNumber`/`StartDate`/`EndDate` — เดิมเป็น `SumInsured`/`CoverageDurationDays`/`Insurer` ก่อน checkout-chain-document-fields) + PII ผู้เอาประกัน (ชื่อ/สกุล/เลขบัตร/วันเกิด) ณ เวลาซื้อ |
| `Items/CheckoutItemInput.cs` | Domain | input DTO ของ line (ต้องอยู่ใน `*.Domain` ไม่ใช่ `*.Application`) |
| `SessionStatus.cs` | Domain | `Started=0`, `Confirmed=1`, `Abandoned=2` |
| `StartCheckout.cs` / `ConfirmCheckout.cs` | App | Start ตั้งราคาจาก **cart subtotal** (ไม่เชื่อ client); Confirm emit `CheckoutConfirmed` → Orders เปิด order |
| `ICheckoutRepository.cs` | App | port |
| `SessionConfiguration.cs`, `Items/ItemConfiguration.cs`, `CheckoutModuleRegistration.cs` | Infra | EF config + `AddCheckoutModule()` |

### 4.4 Orders — คำสั่งซื้อ + policy reference record

โมดูลที่โตที่สุดฝั่ง funnel (consume `CheckoutConfirmed` + `PaymentPaid`).

| ไฟล์ | ชั้น | บทบาท |
|------|------|-------|
| `Order.cs` | Domain | aggregate: MerchantId/PaymentSessionId?/CheckoutSessionId?/Amount/Status/CreatedAt/PaidAt?/**SummaryToken + SummaryTokenExpiresAt**/NotificationRecipient?; `Create` (พร้อม items), `AttachPaymentSession`, **`MarkPaid(Money, DateTime)`** re-verify amount+currency + idempotent ผ่าน status, `ReissueSummary`, `Cancel` |
| `OrderStatus.cs` / `OrderPaid.cs` | Domain | `AwaitingPayment=0`/`Paid=1`/`Cancelled=2`; domain event ภายในโมดูล |
| `Items/Item.cs` · `OrderItemInput.cs` | Domain | order line + snapshot ผู้เอาประกัน (INSERT-only — ไม่แก้หลังสร้าง) |
| `Items/ItemPolicy.cs` | Domain | **aggregate mutable 1:1 กับ Item** (แยกจาก Item ที่ INSERT-only): `InsuranceCategory?`/`ReferenceNumberType?`/`ReferenceNumber?`/`EndorsementNumber?`/`RenewalReminderNumber?`/`InsuredObjectReference?`/net+gross premium (decimal+currency)/`PremiumRemittanceStatus`/`DeductedAt?`; invariant อยู่ใน `Apply()` |
| `Items/ItemPolicyAudit.cs` · `RevealAudit.cs` | Domain | audit append-only ของการแก้ policy / การเปิดอ่าน PII |
| `Items/{ActorKind,AuditOperation,InsuranceCategory,PremiumRemittanceStatus,ReferenceNumberType}.cs` | Domain | enum (`ActorKind` = `Merchant`/admin ฝั่งไหนเป็นคนแก้) |
| `CheckoutConfirmedConsumer.cs` · `OrderPaidConsumer.cs` · `CustomerOrderNotificationConsumer.cs` | App | consume integration event: เปิด order / mark paid (re-verify ข้างใน, ไม่เจอ = เงียบกัน retry loop) / ส่ง notification |
| `UpsertItemPolicyCommand.cs` · `UpsertItemPolicyAdminCommand.cs` | App | แก้ policy ฝั่ง merchant / ฝั่ง admin (admin command พก scope มาเป็น primitive) |
| `ListPolicyReportQuery.cs` · `ListPolicyReportAdminQuery.cs` · `PolicyReportItem.cs` | App | รายงาน policy 2 plane (merchant / admin cross-merchant) |
| `GetOrders.cs` · `GetOrderDetail.cs` · `GetReconciliationSummary.cs` · `ResendOrderSummary.cs` | App | list/detail SFS, สรุป reconciliation, ออก summary token ใหม่ |
| `IOrderRepository.cs` · `IItemPolicyRepository.cs` · `IAdminItemPolicyWriter.cs` · `IOrderSummaryReader.cs` · `IRevealAuditWriter.cs` | App | ports |
| `OrderConfiguration.cs` · `Items/*Configuration.cs` · `OrdersModuleRegistration.cs` | Infra | EF config + `AddOrdersModule()` |

### 4.5 Payments — money path + PSP integration

webhook = source of truth. Application แยกโฟลเดอร์ต่อ use-case + `Ports/`.

**Domain**

| ไฟล์ | บทบาท |
|------|-------|
| `Session.cs` | aggregate state machine: MerchantId/OrderId/Amount/Method/`Psp:Code`/Status/PspExternalChargeId?/RedirectUrl?/timestamps/**`RowVersion`** (optimistic concurrency); `Create` (bind order+amount+method+PSP ตั้งแต่แรก ไม่มี attach-race), `BeginRedirect` (Created→Redirected ใต้ RowVersion **ก่อน**แตะ PSP), `SetPspCharge` (Redirected เท่านั้น กัน double-charge), `MarkPaid` (idempotent), `MarkFailed`, `MarkExpired` |
| `SessionStatus.cs` | `Created=0`/`Redirected=1`/`Paid=2`/`Failed=3`/`Expired=4` — Paid เฉพาะตอน webhook ยืนยัน (ไม่ใช่ browser return) |
| `Psp/Code.cs` | enum `TwoCTwoP=0`, `Omise=1` + `Codes.ToCode/FromCode` (`"2c2p"`/`"omise"` — stable wire code) |
| `Psp/Connection.cs` | entity ต่อ (merchant, PSP): EnabledMethods, **`SecretRefName`** (ชื่อ lookup ใน vault — ไม่ใช่ secret), Metadata (display-only ห้าม secret/PII), IsEnabled |

**Application — 4 use-case + Ports/**

| use-case | handler ทำอะไร |
|----------|----------------|
| `CreateSession/` | `Session.Create` bind ครบ → add → commit. **ไม่แตะ PSP** |
| `GetSession/` | load → project view |
| `StartRedirect/` | **แตะ PSP ครั้งแรก**: Redirected+มี URL แล้ว → คืนเลย (idempotent); ไม่งั้น `BeginRedirect` → save ใต้ RowVersion (loser ชน `ConcurrencyConflict` → ดึง URL ผู้ชนะคืน) → reveal secret → `adapter.CreateRedirectChargeAsync` → `SetPspCharge` → คืน URL |
| `HandlePspWebhook/` | (1) load connection by `PspConnectionId` (ไม่ใช่จาก URL) + reveal secret; (2) `VerifyWebhook` ไม่ผ่าน → `Rejected`; (3) ใน 1 tx: parse → claim idempotency multi-key **connection-scoped** (`{psp}:{connId}:event:{eventId}` + `...:charge:{chargeId}:{status}`) ซ้ำ → `Duplicate`; (4) **fetch-to-confirm** `FetchChargeAsync` server-to-server (webhook status แค่ advisory); (5) Paid → `MarkPaid` → `Outbox.Enqueue(PaymentPaid)` อะตอม |
| `Ports/` | `IPspAdapter` (`CreateRedirectChargeAsync`/`VerifyWebhook`/`FetchChargeAsync`/`ParseWebhook`), `IPspAdapterFactory`, `ISessionRepository`, `Psp/IConnectionRepository`, `IPspSecretEnvelopeFactory`, `PspContracts` |

**Infrastructure**

| ไฟล์ | บทบาท |
|------|-------|
| `Psp/PspAdapterBase.cs` | primitive ร่วม (stateless singleton): pooled `IHttpClientFactory`, `EncodeJwtHs256`/`TryReadVerifiedJwtHs256` (alg-pinned + `CryptographicOperations.FixedTimeEquals`), `SendOnceAsync` (charge-create **ไม่ retry** — timeout ห้าม double-charge), `SendWithRetryAsync` (fetch GET idempotent, backoff+jitter) |
| `Psp/TwoCTwoPAdapter.cs` | 2C2P: body = `{"payload": HS256-JWT}`; `POST /payment/4.3/paymentToken` (invoiceNo = session id → external charge id คงที่), ต้องได้ respCode `0000`; inquiry ผ่าน `/paymentInquiry` |
| `Psp/OmiseAdapter.cs` | Omise: HTTP Basic; `POST /charges` (`Idempotency-Key` = session id, ไม่ส่ง card data); `Verify` = well-formedness gate (HMAC defer — fetch-to-confirm คือ authority); `GuardKeyEnvironment` กัน `skey_test_`/`skey_live_` ผิด environment. **PromptPay ยัง defer** |
| `Psp/PspAdapterFactory.cs` · `PspOptions.cs` · `PspSecretEnvelope.cs` · `PspSecretEnvelopeFactory.cs` | factory (dict by `adapter.Psp`), config non-secret, shape ของ secret หลัง reveal |
| `Persistence/SessionConfiguration.cs` · `Persistence/Psp/ConnectionConfiguration.cs` · `PaymentsModuleRegistration.cs` | EF config (RowVersion token, unique filtered index บน `(Psp, PspExternalChargeId)`) + `AddPaymentsModule()` |

### 4.6 Merchants — merchant + merchant-user identity

| กลุ่ม | ไฟล์ | บทบาท |
|------|------|-------|
| merchant | `Merchant.cs`, `MerchantCode.cs`, `MerchantStatus.cs`, `ProvisioningAudit.cs` | aggregate ผู้เช่า: Code (normalize lowercase; allowlist `vprivilege`/`vcommerce`/`vsouvenir`), DisplayName, LegalEntityId, Country, Currency, EnabledChannels, Metadata |
| merchant-user | `Users/User.cs`, `Session.cs`, `ExternalLogin.cs`, `AuthAudit.cs`, `RegistrationAudit.cs`, `RegistrationNotice.cs`, `Roles/RoleAssignment.cs`, `PersonType.cs`, `TicketPurpose.cs`, `UserStatus.cs`, `SessionDecision.cs` | identity ฝั่ง merchant console (Google/Entra OIDC → session cookie) + registration flow |
| App | `Users/{SubmitRegistration,ApproveReject,ResolveLogin,ResolveById,SetUserRoles,PhotoValidation,RegistrationConsumer}.cs`, `*Ports.cs`, `UserScope.cs` | สมัคร → approve/reject → resolve login → ผูก role |
| App | `GetMerchant/*`, `ProvisionMerchant/*`, `IMerchantRepository.cs` | อ่าน merchant + provision (คู่กับ `Persistence.Provisioning`) |
| Infra | `Persistence/*Configuration*.cs`, `LocalPhotoStore.cs`, `MerchantsModuleRegistration.cs` | EF config + เก็บรูปสมัคร + `AddMerchantsModule()` |

### 4.7 Admins — admin identity (control plane)

| ไฟล์ | บทบาท |
|------|-------|
| `Domain/Users/{User,Session,Audit,AuthAudit,MerchantAccess,Tier,UserStatus,SessionDecision}.cs` | admin aggregate + session + audit; `Tier` = `Super`/`Scoped`; `MerchantAccess` = merchant ที่ scoped admin เข้าถึงได้ |
| `Domain/Roles/RoleAssignment.cs` | role assignment ฝั่ง admin (catalog อยู่ที่ Iam) |
| `Application/Users/*` | 20 use-case: `SelfProvisionSuperAdmin` (bootstrap ผ่าน allowlist), `CreateScopedAdmin`, `BindInvitedAdmin`, `AssignMerchant`/`UnassignMerchant`, `ChangeAdminTier`, `Suspend`/`Reactivate`, `SetAdminRoles`, `UpdateAdminProfile`, `RevokeAdminSession`, `AccessibleMerchants`, `ResolveAdmin`/`ById`, `UserQueries` (SFS) |
| `Application/IAdminScope.cs` · `IAdminMerchantDirectory.cs` · `Roles/RolePorts.cs` | scope ของ admin ที่ล็อกอินอยู่ + directory + ports |
| `Infrastructure/*` | EF config + `UserSfs` + `AddAdminModule()` |

### 4.8 Iam — RBAC catalog (ใช้ร่วม 2 plane)

| ไฟล์ | บทบาท |
|------|-------|
| `Domain/Permissions/{Keys,Permission,PermissionGroup}.cs` | **central catalog** `iam.*` — `Keys` เป็น const ของ permission key ทุกตัว (`payment.create`, `payment.redirect`, ...) |
| `Domain/Roles/{Role,RolePermission,RoleStatus,RoleVisibility}.cs` | role + mapping; `RoleVisibility` แยกว่า role นี้ของ plane ไหน |
| `Application/Roles/{CreateRole,UpdateRole,DeleteRole,RoleQueries,RolePorts,RoleSideContext,IRoleAssignmentCounter,IRoleAuditSink}.cs` | CRUD role + query (SFS) — **unified**: Admin console กับ Merchant console ใช้ command/type ชุดเดียวกัน แยกด้วย `RoleSideContext` |
| `Application/Permissions/PermissionCatalog.cs` | อ่าน catalog (scope `Platform`/`Merchant`) |
| `Infrastructure/*` | EF config + `RoleSfs` + `AddIamModule()` |

### 4.9-4.12 Divisions / Levels / Offices / Positions — master data

4 โมดูลรูปทรงเหมือนกันเป๊ะ (แตกออกมาจาก `MasterData` เดิม, PR #117): `<X>.Domain/<X>.cs` (entity) · `<X>.Application/<X>Store.cs` (store port + query) · `<X>.Infrastructure/Persistence/<X>Configurations.cs` + `<X>sModuleRegistration.cs`. ตารางอยู่ schema **`cfg`** (control-plane, ไม่มี merchant dimension); lookup ข้ามโมดูลผ่าน `IProfileLookup` แบบ enum-keyed.

---

## 5. Hosts — composition root

**host เดียว: `Hosts/Api`** (`Api.csproj`) — HTTP surface + webhook ingest + background outbox dispatch ในโปรเซสเดียวกัน. โปรเจกต์ `Worker` ถูกลบทั้งตัว (`multi-tier-deployment`, 2026-07-22); deploy artifact เหลือ 2 image: `api` + `migrate`.

| ไฟล์ | บทบาท |
|------|-------|
| `Program.cs` | ประกอบทั้งระบบ (~2,370 บรรทัด): Mediator source-gen + `MerchantGuardBehavior` → `AddBuildingBlocksInfrastructure` + `AddSecurityTelemetry` → **connection string เดียว `ConnectionStrings:App`** (`pol_app`, stamp `ApplicationName="Api"`) → `ModuleAssemblies(HostModuleAssemblies.All)` → module registration + `AddIamRoleManagement` → 3 runtime persistence + provisioning + admin-policy writer → Admin/MerchantUser BFF (OIDC + session scheme + data protection + prune service) → CORS/OpenAPI/Scalar/rate limiter → **route ทั้งหมดใต้ `app.MapGroup("/api/v1")`** |
| `DesignTimeDbContextFactories.cs` | `HostModuleAssemblies.All` — **list assembly ของทั้ง 12 โมดูล** ที่ context ใช้ discover `IEntityTypeConfiguration` ตอน model-build (แชร์ระหว่าง runtime composition root กับ design-time factory เพื่อให้ `dotnet ef migrations` build model เดียวกับที่แอปรัน) + `PolDbContextFactory` (connection จาก env `POL_DESIGN_SQL`) |
| `HttpActorContext.cs` | impl `IActorContext` สำหรับ HTTP request — merchant มาจาก principal ที่ authenticate แล้ว **ไม่ใช่จาก URL** |
| `Persistence/WriteAuthorizers.cs` | **write floor impl**: `MerchantRequestWriteAuthorizer` (เขียนได้เฉพาะ merchant ของ actor), `ControlPlaneAdminWriteAuthorizer` (admin ผ่าน `IAdminScope` + unbound allowlist สำหรับ login flow), `ProvisioningSuperWriteAuthorizer`, `AdminItemPolicyWriteAuthorizer` |
| `BackgroundDispatch/BackgroundDispatchScope.cs` | **discriminator ตัวเดียว** — `IsHttpRequest(sp)` (มี `HttpContext` ไหม); dispatcher สร้าง scope เองจึงไม่มีเสมอ |
| `BackgroundDispatch/WorkerActorContext.cs` · `WorkerWriteAuthorizer.cs` | ย้ายมาจาก Worker host เดิม (คงชื่อ class) — ถูก resolve แทน `HttpActorContext`/request authorizer เมื่อ scope ไม่มี `HttpContext`. **นี่คือจุดที่นั่งอยู่บน security boundary ตรง ๆ** จึงมี composition-root test เฉพาะ |
| `Admins/*` (11 ไฟล์) | Admin BFF: `OidcAuthentication` (provider-scoped `Admin{Provider}` scheme), `SessionAuthenticationHandler`, `SessionCookies` (`__Host-adm_session`), `CsrfFilter`, `LoginService`, `AuthRateLimiting`, `AdminDataProtection`, `SessionPruneService`, `AuthOptions`, `HostWiring` |
| `Merchants/*` (12 ไฟล์) | MerchantUser BFF ชุดคู่ขนาน: `UserOidcAuthentication` (`MerchantUser{Provider}`), `UserSessionAuthenticationHandler`, `UserSessionCookies` (`__Host-mch_session`), `UserCsrfFilter`, `UserRegistration`, `UserPermissionAuthorization`, `UserAuthRateLimiting`, ... |
| `Iam/PermissionAuthorization.cs` · `RoleHostWiring.cs` | `RequirePermission(Keys.*)` endpoint filter (fail-closed) + boot parity guard |
| `OidcProviderOptions.cs` · `ReturnUrlPolicy.cs` | config OIDC ต่อ provider (Google/Entra) + allowlist ของ return URL |
| `SfsQueryParser.cs` · `SfsOpenApi.cs` | parse `page`/`limit`/`filters`/`sort`/`search` จาก query string ดิบ + ประกาศ parameter เข้า OpenAPI |
| `Webhooks/RateLimiting.cs` | sliding-window partition by **source IP** (ไม่ใช่ connection id กัน budget exhaustion), QueueLimit=0 → 429 |
| `DesignTimeDbContextFactories.cs` | factory ตอน `dotnet ef migrations` — connection จาก env `POL_DESIGN_SQL` |
| `appsettings.json` | prod defaults: `ConnectionStrings:App` (password ว่าง ฉีด runtime), `AdminAuth`/`MerchantAuth:Providers:*`, `Cors:AllowedOrigins`, `Vault:*`, `ForwardedHeaders:*`; prod ไม่ publish OpenAPI |
| `appsettings.Development.json.example` | template ของ dev config (ตัวจริง gitignored — มี `ConnectionStrings:Migrator` สำหรับ auto-migrate ตอน boot) |
| `Properties/launchSettings.json` | profile http (5100) / https (5101) |

### Middleware order (`Program.cs`)

```
ForwardedHeaders → [HttpLogging (Dev)] → CorrelationId → ExceptionHandler → StatusCodePages
  → PolCors → RateLimiter → Authentication → Authorization
  → HealthChecks (/health/live, /health/ready)  [นอก /api/v1]
  → [OpenAPI + Scalar (Dev เท่านั้น)]           [นอก /api/v1]
  → MapGroup("/api/v1") → endpoints
```

### Route surface — จัดกลุ่มตาม API tag จริงใน Scalar (18 tag ตรงกับ `.WithTags(...)` ใน `Program.cs`, ทุกเส้นอยู่ใต้ `/api/v1`)

| Tag (Scalar) | Endpoint | Module |
|---|---|---|
| Webhooks | `POST /webhooks/{pspConnectionId}` (anonymous, rate-limited) | §4.5 Payments (ไม่มีโมดูลของตัวเอง — ขี่อยู่บน Payments) |
| ผลิตภัณฑ์ | `GET /products` (อ่านอย่างเดียว — ไม่มี POST) | §4.1 Products |
| ตะกร้าสินค้า | `POST /carts` · `GET /carts/{cartId}` · `POST/PUT/DELETE /carts/{cartId}/items[/{productId}]` · `POST /carts/{cartId}/clear` | §4.2 Carts |
| เช็คเอาต์ | `POST /checkouts` · `POST /checkouts/{checkoutSessionId}/confirm` | §4.3 Checkouts |
| การชำระเงิน | `POST /payments/sessions` · `POST /payments/sessions/{paymentSessionId}/redirect` | §4.5 Payments |
| คำสั่งซื้อ | `GET /orders` · `GET /orders/{orderId}` · `GET /orders/{token}/summary` (anonymous capability link) · `POST /orders/{orderId}/summary/resend` · `PUT /orders/{orderId}/items/{itemId}/policy` · `GET /reports/reconciliation` · `GET /reports/policies` | §4.4 Orders |
| การเข้าสู่ระบบ | `GET /admins/auth/{provider}/login` · `POST /admins/auth/logout[-all]` · `GET /admins/me` | §5 Admin BFF (`Hosts/Api/Admins/*`, ไม่ใช่ `Modules/*`) |
| ร้านค้า (ผู้ดูแลระบบ) | `POST /merchants` · `GET /merchants/{code}` | §4.6 Merchants (`ProvisionMerchant`/`GetMerchant`) |
| การเข้าสู่ระบบ (ผู้ใช้ร้านค้า) | `GET /merchants/auth/{provider}/login` · `POST /merchants/users/register` · `POST /merchants/auth/logout[-all]` · `GET /merchants/users/me` | §4.6 Merchants (Users) + §5 MerchantUser BFF |
| บทบาท (ผู้ใช้ร้านค้า) | `GET /merchants/users/permissions` · `GET/POST/PUT/DELETE /merchants/users/roles[/{code}]` · `PUT /merchants/users/{merchantUserId}/roles` | §4.8 Iam (merchant-scope; permissions + role catalog CRUD) · §4.6 Merchants (`SetRolesCommand`, assign roles to a merchant-user) |
| ผู้ใช้ร้านค้า (ผู้ดูแลระบบ) | `POST /admins/merchants/users/{subject}/approve` · `POST /admins/merchants/users/{subject}/reject` | §4.6 Merchants (Users/ApproveReject สั่งฝั่ง admin) |
| คำสั่งซื้อ (ผู้ดูแลระบบ) | `PUT /admins/orders/{orderId}/items/{itemId}/policy` · `GET /admins/reports/policies` | §4.4 Orders (admin cross-merchant use case) |
| ผู้ดูแลระบบ | `POST/GET /admins` · `GET /admins/{id}` · `GET /admins/{id}/effective-permissions` · `POST/DELETE /admins/{id}/merchants[/{merchantId}]` · `POST /admins/{id}/{suspend,reactivate,tier}` · `PUT /admins/{id}/profile` · `PUT /admins/{id}/roles` · `GET /admins/{id}/sessions` · `DELETE /admins/{id}/sessions/{sessionId}` | §4.7 Admins |
| บทบาท (ผู้ดูแลระบบ) | `GET /admins/permissions` · `GET/POST/PUT/DELETE /admins/roles[/{code}]` | §4.8 Iam (admin-scope) |
| ตำแหน่ง | `GET /positions[/{id}]` · `POST /positions` · `PUT/DELETE /positions/{id}` (soft-deactivate, gate `user.manage`) | §4.9-4.12 |
| สำนักงาน | เหมือน Positions, path `/offices` | §4.9-4.12 |
| ระดับ | เหมือน Positions, path `/levels` | §4.9-4.12 |
| แผนก | เหมือน Positions, path `/divisions` | §4.9-4.12 |

> `Models` ใน Scalar sidebar **ไม่ใช่ tag/module** — เป็น schema/DTO index ที่ Scalar auto-generate จาก `document.Components.Schemas` เฉย ๆ

> audience บังคับ **ต่อ endpoint** ผ่าน `.RequireAuthorization("merchant-user"|"admin")` + `.RequirePermission(Keys.*)` — ไม่ใช่จาก path segment (path บอกแค่ *area*).

---

## 6. แกนข้ามชั้นที่ต้องเข้าใจ

### 6.1 Multi-merchant isolation (floor = **app layer**, ไม่ใช่ SQL RLS)

> **RLS ถูกถอดทิ้งทั้งระบบแล้ว** (migration `20260719081817_RlsTeardownAndOnePrincipal`, spec `rls-to-query-filter`). ไม่มี `SECURITY POLICY` / predicate function / `SESSION_CONTEXT` / `EXECUTE AS` bypass proc / `pol_admin`+`pol_worker`+`pol_rls_bypass` หลงเหลืออยู่เลย. เอกสาร/ความจำใดที่บอกว่า floor อยู่ที่ DB = **ล้าสมัย**.

```
request → IActorContext ผูก MerchantId (จาก principal/binding ไม่ใช่ URL)
        → [read floor]  EF global query filter: MerchantId == CurrentMerchant  (deny-default)
        → [write floor] GuardedRuntimeDbContext.GuardPendingChanges (sealed SaveChanges)
                        append-only + tenant-key immutable + Guid.Empty reject + IWriteAuthorizer.CanWrite
        → SQL Server 2025 — principal เดียว pol_app, ไม่มี floor ที่ DB
```

- **Deny-default ทั้งสองชั้น**: ไม่มี actor ผูก = เห็น **ศูนย์แถว** (ไม่ใช่เห็นหมด) และเขียนไม่ได้เลย
- สองชั้นนี้ **คือ floor เอง** ไม่ใช่ชั้นสะดวกเสริมบน floor อื่น — ระบบจึงพึ่งโค้ดแอปเขียนถูกทุกจุด, ชดเชยด้วย `ISecurityTelemetry` → Seq ที่จดทุก denial
- `IgnoreQueryFilters()`/`ExecuteUpdate`/`ExecuteDelete`/raw SQL = escape hatch, ทำได้เฉพาะไฟล์ใน allowlist ที่ arch test บังคับ (§3)
- **RBAC คนละแกน**: RBAC ตอบ "ใครกดปุ่มอะไรได้"; floor ตอบ "เห็น/แก้ของ merchant ไหน" — แทนกันไม่ได้

### 6.2 Vault (envelope encryption + tamper-evident audit)

```
Store:  plaintext --AES-256-GCM(DEK)--> ciphertext ;  DEK --wrap(KEK ต่อ merchant)--> EncryptedDek ; เก็บ + hint(last4)
Reveal: unwrap DEK --> decrypt --> เขียน reveal-audit (fail-closed) --> คืน plaintext (server-only, ไม่ log)
Rotate master key: re-wrap DEK ไป active key (plaintext ไม่โผล่)
Audit: hash chain ต่อ merchant (Seq + PrevHash), append ใต้ sp_getapplock ; verify เดิน chain ดัก gap/edit
```

PSP secret ไม่เคยอยู่ใน `Psp/Connection` — เก็บแค่ `SecretRefName`, reveal ตอนจะเรียก PSP เท่านั้น.

### 6.3 Payment happy-path (end-to-end)

```
1. POST /api/v1/checkouts/{id}/confirm → CheckoutConfirmed → CheckoutConfirmedConsumer → Order = AwaitingPayment
2. CreateSessionCommand          → Payments.Session = Created (bind order+amount+method+PSP, ไม่แตะ PSP)
3. StartRedirectCommand          → BeginRedirect (RowVersion claim) → [PSP call แรก] CreateRedirectChargeAsync → redirect URL
4. ลูกค้า redirect ไปจ่ายที่หน้า PSP
5. PSP webhook → HandlePspWebhook → verify sig → idempotency multi-key → [fetch-to-confirm] FetchChargeAsync
                                   → Paid → session.MarkPaid → Outbox.Enqueue(PaymentPaid)  [atomic]
6. OutboxDispatcher (IHostedService ใน Api process — Persistence.MerchantRuntime/Outbox/OutboxDispatcher.cs)
     → lease batch (escape-hatch, เห็นทุก merchant) → IActorScope.Begin(msg.MerchantId) ต่อ message
     → publish PaymentPaid → OrderPaidConsumer → order.MarkPaid (re-verify amount+currency) → Order = Paid
```

safeguard สำคัญ: webhook (ไม่ใช่ browser return) คือ source of truth · fetch-to-confirm ตรวจซ้ำ server-to-server · idempotency multi-key connection-scoped กัน replay · RowVersion กัน double-charge · `Order.MarkPaid` re-verify amount+currency · routing webhook ด้วย `PspConnectionId` ไม่ใช่ค่าใน URL ก่อน verify · dispatcher bind merchant **ต่อ message** ผ่าน `IActorScope` (ไม่ใช่พึ่ง principal แยกอีกแล้ว).

---

## หมายเหตุ

- ตัวเลขไฟล์ (ณ commit นี้, นับเฉพาะไฟล์ที่ git track): **46 `.csproj`**, **424 `.cs`** (ไม่นับ `Migrations/` = migration + designer + snapshot; รวมแล้ว 453)
- ทำไม 22 → 46 `.csproj`: โมดูลธุรกิจเพิ่มจาก 5 → **12** (+21 project — Merchants/Admins/Iam + master data 4 ตัวที่แตกจาก `MasterData`) · แยกชั้น **`Persistence.*` ออกมา 4 project** ตอนถอด RLS · ลบ `Worker` host ไป 1
- flow ธุรกิจจริงคือ **ขายประกัน** ไม่ใช่ payment orchestration ล้วน — order line ผูก 1 ผู้เอาประกัน + snapshot เงื่อนไขกรมธรรม์ ณ เวลาซื้อ, และมี `ItemPolicy` เป็น record อ้างอิงกรมธรรม์ที่แก้ได้ทีหลัง
- จุดที่ยัง defer: Omise PromptPay (`NotSupportedException`), Omise webhook HMAC (พึ่ง fetch-to-confirm แทน)
- โฟลเดอร์ `src/Hosts/Worker/` และ `src/Modules/MasterData/` ถ้ายังโผล่บนเครื่อง = ซาก `obj/` ที่ไม่ได้ track (ไม่มีไฟล์ source เหลือ) — ลบทิ้งได้
