# โครงสร้าง `src/` — pol-core (payment platform)

> เอกสารอ้างอิงโครงสร้างจริงของโค้ดใน `src/` (file-by-file role map).
> ground truth คือไฟล์จริง; เอกสารนี้สรุปบทบาท ไม่ใช่ spec. canonical architecture: [`.ai/shared/ARCHITECTURE.md`](../../.ai/shared/ARCHITECTURE.md) · product canon: [`.ai/shared/PROJECT_CONTEXT.md`](../../.ai/shared/PROJECT_CONTEXT.md) · โมดูลเชิงลึก: [`payment-orchestration-modules.md`](payment-orchestration-modules.md)

## ภาพรวม

รูปทรง: **Modular Monolith** ตามแนว **Clean Architecture + CQRS** — 1 codebase, แยกเป็น 22 .csproj, deploy เป็น 2 host (Api + Worker). command/query แยกผ่าน Mediator; โมดูลคุยข้ามกันด้วย `INotification` ผ่าน transactional outbox ไม่อ้างถึงกันตรง.

- TargetFramework: `net10.0` ทุก project · `LangVersion 14.0` · `Nullable enable` (จาก `Directory.Build.props` กลาง)
- package version จัดกลางที่ `Directory.Packages.props` (Central Package Management)
- Mediator: source-generated (`Mediator.SourceGenerator`) — handler ถูก discover ตอน compile

### Dependency rule (ทิศชี้เข้า domain)

```
Hosts (Api / Worker)            composition root — ผูกทุกอย่างเข้าด้วยกัน
   │  ลงไปได้ทุกชั้น
   ▼
Infrastructure (per-module + BuildingBlocks)   EF, repo, PSP adapter, vault
   │  ขึ้นกับ
   ▼
Application (per-module + BuildingBlocks)       command/query/handler + ports (interface)
   │  ขึ้นกับ
   ▼
Domain (per-module) + SharedKernel + Contracts  entity/value object/event บริสุทธิ์ ไม่มี dependency นอก
```

กฎ: Domain ไม่ขึ้นกับใคร (นอกจาก SharedKernel). Application รู้จัก Domain + ประกาศ **port** (interface) ที่ Infrastructure ไป implement. Host เป็นที่เดียวที่ประกอบ concrete เข้า interface.

### โครงสร้าง top-level

```
src/
  SharedKernel/                 domain primitive ใช้ร่วมทุกโมดูล (Money, Entity, ISO4217)
  Contracts/                    integration event ข้ามโมดูล (PaymentPaid)
  BuildingBlocks/
    BuildingBlocks.Application/    abstraction กลาง (tenant, outbox, idempotency, vault port, exception)
    BuildingBlocks.Infrastructure/ data-plane จริง (EF, RLS, outbox dispatcher, vault, migrations)
    BuildingBlocks.Web/           cross-cutting HTTP (auth, cors, health, problem-details, correlation id)
  Modules/                      5 โมดูลธุรกิจ × 3 ชั้น (Domain/Application/Infrastructure)
    Products/ Cart/ Checkout/ Orders/ Payments/
  Hosts/
    Api/                          HTTP host — SPA endpoint + webhook ingest
    Worker/                       background host — outbox dispatcher
```

ลำดับโดเมนตาม flow ธุรกิจ: **Products → Cart → Checkout → Orders → Payments**.

---

## 1. Foundation — `SharedKernel/` + `Contracts/`

ชั้นล่างสุด ไม่มี dependency ภายนอก. ทุกโมดูล reference ได้.

### SharedKernel (`SharedKernel.csproj` — ไม่มี ProjectReference)

| ไฟล์ | บทบาท | key types |
|------|-------|-----------|
| `Entity.cs` | base class ของ DDD: เทียบ identity ด้วย type+Id | `Entity<TId>`, `AggregateRoot<TId>` (ถือ `IDomainEvent` collection), `IDomainEvent` marker |
| `Money.cs` | value object เงิน non-negative — แกนของ "ไม่มี decimal/float ที่ cross-module seam" | `readonly record struct Money { long MinorUnits; string Currency }`; factory `Of()` validate currency+non-negative; `Add()` กัน currency ผิด + overflow; `default(Money)` ใช้ไม่ได้ (throw) |
| `Iso4217.cs` | registry สกุลเงินขั้นต่ำ (THB/USD/JPY) | `IsSupported(code)`, `MinorUnitDigits(code)` (THB/USD=2, JPY=0) — throw เมื่อไม่รู้จัก |
| `MoneyJsonConverter.cs` | (de)serialize `Money` เป็น `{minorUnits, currency}` แล้ว re-validate ผ่าน `Money.Of()` | `JsonConverter<Money>` — ใช้ใน outbox payload + JSON API |

### Contracts (`Contracts.csproj` → SharedKernel, Mediator.Abstractions)

| ไฟล์ | บทบาท | key types |
|------|-------|-----------|
| `PaymentPaid.cs` | integration event v1 ที่ Payments emit เมื่อ PSP ยืนยันจ่ายแล้ว; Orders consume แบบ idempotent + re-verify amount/currency | `sealed record PaymentPaid(PaymentSessionId, OrderId, TenantId, Money Amount, PspCode, ExternalChargeId, EventId, OccurredAtUtc) : INotification`; `SchemaVersion="v1"` |

> `PaymentPaid.Amount` เป็น `Money` (ไม่ใช่ `long` ดิบ) — ปิด seam ที่ ARCHITECTURE.md เตือนไว้ระหว่าง Payments↔Orders.

---

## 2. BuildingBlocks — โครงสร้างพื้นฐานข้ามโมดูล

### 2.1 BuildingBlocks.Application (`→ SharedKernel, Contracts, Mediator.Abstractions`)

abstraction ที่ระดับ application ใช้ร่วม — transactional seam, tenant isolation, exception, vault/idempotency **port** (interface เปล่า ไม่มี impl).

| ไฟล์ | บทบาท | key |
|------|-------|-----|
| `IClock.cs` | source เวลา UTC แบบ test ได้ | `DateTime UtcNow` |
| `IUnitOfWork.cs` | commit transaction โดย handler ไม่ต้องเห็น DbContext | `SaveChangesAsync()`, `ExecuteInTransactionAsync<T>()` — ห่อ idempotency-claim + state change + outbox enqueue เป็นก้อนเดียว |
| `IOutbox.cs` | เขียน integration event ลง outbox ใน tx เดียวกับ state | `Enqueue(INotification)` — dispatcher ส่งทีหลังแบบ at-least-once |
| `IIdempotencyStore.cs` | ledger กันซ้ำ multi-key (payment/webhook) | `TryBeginAsync(keys, context)` — claim หลาย key อะตอม, false = replay |
| `ITenantContext.cs` | ambient tenant ต่อ request (resolve จาก principal ไม่ใช่ URL) | `TenantId` (throw ถ้าไม่มี), `HasTenant` — feed `SESSION_CONTEXT('TenantId')` |
| `ITenantScope.cs` | bind tenant แบบ explicit สำหรับ entry ที่ไม่มี auth (webhook, dispatcher) | `Begin(Guid)` → disposable; throw ถ้า bind ซ้ำ (กัน confused-deputy) |
| `ITenantScoped.cs` | marker ของ command/query ที่เป็นของ tenant เดียว | ใช้โดย `TenantGuardBehavior` |
| `TenantGuardBehavior.cs` | **MediatR `IPipelineBehavior`** — กัน message `ITenantScoped` ที่ไม่มี tenant bound | throw `TenantBindingException` ก่อนเข้า handler (ชั้นสะดวกบน RLS floor) |
| `IVaultSecretStore.cs` | custody secret PSP แบบ write-only-from-outside (envelope encryption) | `StoreAsync/RevealAsync/MaskedAsync/ExistsAsync` — `Reveal` เรียกฝั่ง server เท่านั้น ไม่ log |
| `IVaultMaintenance.cs` | re-wrap DEK ตอน rotate master key (นอก request path) | `RewrapTenantToActiveKeyAsync(tenantId)` — idempotent, ไม่ถอด plaintext |
| `IVaultRevealAuditWriter.cs` | เขียน audit ทุกครั้งที่ reveal secret (tamper-evident) | `AppendAsync(tenantId, secretName)` — durable แยกจาก UoW ของ caller |
| `IVaultRevealAuditVerifier.cs` | ตรวจ hash chain ของ reveal-audit | `VerifyAsync(tenantId)` → `VaultAuditVerifyResult{Ok, FirstBrokenSeq, Reason}` |
| `IWebhookTenantResolver.cs` | map `pspConnectionId` → tenant สำหรับ webhook ที่ไม่มี auth | `ResolveTenantAsync(pspConnectionId)` — แก้ปัญหา RLS chicken-and-egg |
| `ConcurrencyConflictException.cs` | optimistic-concurrency ชน (จาก UoW) → handler catch ตัวนี้ (ไม่ผูก EF) | → map 409 |
| `NotFoundException.cs` | aggregate ไม่มี หรือมองไม่เห็นใต้ RLS | → map 404 |
| `TenantBindingException.cs` | `ITenantScoped` ถึง pipeline โดยไม่มี tenant bound | → map opaque 500 |

### 2.2 BuildingBlocks.Infrastructure (`→ BuildingBlocks.Application`, EF Core SqlServer)

data-plane จริง: persistence, multi-tenant RLS, outbox, idempotency, vault envelope-encryption.

**root**

| ไฟล์ | บทบาท |
|------|-------|
| `BuildingBlocksInfrastructureRegistration.cs` | DI กลาง — ผูก UoW/outbox/idempotency/vault/clock/interceptor เข้า service collection |
| `SystemClock.cs` | impl `IClock` = `DateTime.UtcNow` |

**Persistence/** — EF + multi-tenant isolation

| ไฟล์ | บทบาท | key |
|------|-------|-----|
| `ProducerDbContext.cs` | DbContext (Scoped) schema `producer`; เป็นเจ้าของ Outbox/Idempotency/Vault tables + discover entity config ของทุกโมดูลตอน `OnModelCreating` | |
| `SessionContextConnectionInterceptor.cs` | **หัวใจ RLS** — ตอน connection open ทุกครั้ง set `SESSION_CONTEXT('TenantId')` (read-only) ถ้ามี tenant bound | ทำที่ระดับ connection (ไม่ใช่ per-query) เพราะ SESSION_CONTEXT ผูก connection |
| `AmbientTenant.cs` | holder (Scoped) ของ explicit tenant binding (`ITenantScope`) | 1 binding ต่อ UoW, ไม่ nest; ใช้โดย dispatcher + webhook resolver |
| `EfUnitOfWork.cs` | impl `IUnitOfWork` | `SaveChangesAsync` แปลง `DbUpdateConcurrencyException` → `ConcurrencyConflictException`; `ExecuteInTransactionAsync` ใช้ execution strategy (retry transient) |
| `WebhookTenantResolver.cs` | impl `IWebhookTenantResolver` — เรียก `VCentralPay.usp_resolve_webhook_tenant(pspConnectionId)` ใน DI scope ใหม่ | เลี่ยงเปิด connection แบบไม่มี tenant ก่อน bind |
| `ModuleAssemblies.cs` | singleton ถือ list assembly ของโมดูล (set ตอน composition) → ใช้ apply `IEntityTypeConfiguration` ของทุกโมดูล | |

**Outbox/** — transactional outbox (at-least-once cross-module event)

| ไฟล์ | บทบาท | key |
|------|-------|-----|
| `EfOutbox.cs` | impl `IOutbox` — enqueue เป็น row (track ยังไม่ save) ให้ commit พร้อม handler | Id = UUIDv7 (เรียงตามเวลา, index ดี); stamp `TenantId` |
| `OutboxDispatcher.cs` | `BackgroundService` poll outbox (batch 50, ทุก 2s) แล้ว publish ผ่าน Mediator | lease ด้วย `READPAST`+`UPDLOCK` + owner + หมดอายุ 1 นาที; เลิกหลัง 8 attempt; รันใต้ principal `pol_worker`; re-bind tenant ต่อ event ด้วย `ITenantScope.Begin()` ก่อนเรียก handler |
| `OutboxMessage.cs` | entity | Id(UUIDv7), TenantId, Type, Payload(JSON), OccurredAtUtc, ProcessedAtUtc, Attempts, Error, LeaseOwner, LeaseExpiresAtUtc; `MarkProcessed`/`MarkFailed` |
| `OutboxMessageConfiguration.cs` | EF config — table `OutboxMessages`, index `(ProcessedAtUtc, LeaseExpiresAtUtc)` (hot path dispatcher) | |
| `OutboxSerializer.cs` | `JsonSerializerOptions` camelCase + `MoneyJsonConverter` | |

**Idempotency/**

| ไฟล์ | บทบาท | key |
|------|-------|-----|
| `EfIdempotencyStore.cs` | impl `IIdempotencyStore` — claim โดย insert 1 row ต่อ key; ดัก duplicate PK (SqlException 2627/2601) → สัญญาณ replay | RLS-scoped ต่อ tenant |
| `IdempotencyRecord.cs` | entity: `Key`(PK), TenantId, Context, CreatedAtUtc — immutable | |
| `IdempotencyRecordConfiguration.cs` | EF config — table `IdempotencyRecords`, Key ≤400, Context ≤256 | |

**Vault/** — envelope encryption (per-tenant KEK, DEK ต่อ secret) + tamper-evident reveal audit

| ไฟล์ | บทบาท | key |
|------|-------|-----|
| `LocalEnvelopeVaultStore.cs` | impl `IVaultSecretStore` self-hosted | `Store`: derive KEK ต่อ tenant → random DEK → encrypt plaintext (AES-256-GCM) → wrap DEK ด้วย KEK → เก็บ ciphertext + last-4 hint. `Reveal`: decrypt + เขียน audit (fail-closed) คืน plaintext. `Masked`: `**** + hint`. DEK/KEK zero หลังใช้ |
| `VaultEnvelope.cs` | primitive crypto | `DeriveKek` (HKDF-SHA256, salt=tenantId, info คงที่ `pol-core/vault/kek/v1`), `Encrypt/Decrypt` (AES-256-GCM, pack `nonce\|ct\|tag`) |
| `VaultKeyring.cs` | keyring immutable (keyId → byte[32]) | `Active` = key สำหรับ write ใหม่; `ResolveOrNull(keyId)` fail-closed (unknown → null ไม่ fallback) |
| `VaultKeyringFactory.cs` | build+validate keyring ตอน startup (fail-fast) | source: file ก่อน (mounted secret) ไม่งั้น inline base64; กัน legacy `MasterKeyBase64` + `Keys` ตั้งพร้อมกัน |
| `VaultMaintenance.cs` | impl `IVaultMaintenance` — re-wrap DEK ไป active key | ไม่ re-encrypt secret ciphertext (plaintext ไม่โผล่), idempotent, RLS-scoped |
| `VaultOptions.cs` | bind section `Vault`: `ActiveKeyId`, `Keys` (id→entry), legacy `MasterKeyBase64` | `VaultKeyEntry`: `KeyFile` (prod, mounted) มาก่อน `KeyBase64` (dev/env) |
| `VaultSecretBlob.cs` | entity secret | PK `(TenantId, Name)`, KeyId, EncryptedDek, EncryptedSecret, Hint, timestamps; `Rotate` (ค่าใหม่ใต้ active key), `Rewrap` (เปลี่ยน DEK เท่านั้น) |
| `VaultSecretBlobConfiguration.cs` | EF config — table `VaultSecrets`, PK `(TenantId, Name)`, KeyId ≤64, Hint ≤16 | |
| `VaultRevealAudit.cs` | record append-only, hash-chained ของการ reveal (ไม่เก็บ secret/plaintext) | `Hash = SHA256(prevHash \|\| tenantId \|\| len(secretName) \|\| ticks \|\| Seq)`, genesis = 32 zero bytes |
| `VaultRevealAuditConfiguration.cs` | EF config — table `VaultRevealAudits`, unique `(TenantId, Seq)` (ดัก fork/delete), index `(TenantId, Id)` (เดิน chain) | |
| `VaultRevealAuditWriter.cs` | impl writer — append บน DI scope ใหม่ tenant-bound (รอด caller rollback) | SQL: `usp_vault_audit_head` ใต้ applock ต่อ tenant (serialize, ไม่ fork); SQLite (unit test): อ่าน head ตรง |
| `VaultRevealAuditVerifier.cs` | impl verifier — เดิน chain ตาม Seq, ดัก gap/PrevHash ขาด/row ถูกแก้ | read-only |

**Persistence/Migrations/** (EF generated)

| migration | ผล |
|-----------|-----|
| `20260621133013_InitialProducerSchema` | สร้าง schema `producer` + 10 ตาราง (Products, Carts, CartItems, CheckoutSessions, Orders, PaymentSessions, PspConnections, OutboxMessages, IdempotencyRecords, VaultSecrets); index `TenantId` ทุกตาราง tenant |
| `20260621133209_AddRlsSecurityPolicy` | **RLS floor**: `fn_tenant_predicate` (match `SESSION_CONTEXT('TenantId')` หรือเป็นสมาชิก `pol_rls_bypass`), `fn_cartitem_predicate` (scope ผ่าน parent Cart), `usp_resolve_webhook_tenant` (bypass proc), `SECURITY POLICY TenantIsolationPolicy` FILTER+BLOCK 8 ตาราง (OutboxMessages = BLOCK-after-insert ไม่มี FILTER); grant สิทธิ์ `pol_app`/`pol_webhook_resolver`/`pol_worker`/`pol_admin` |
| `20260622022145_AddVaultRevealAudit` | ตาราง `VaultRevealAudits` + index; ALTER policy เพิ่ม BLOCK-after-insert; `usp_vault_audit_head` (EXECUTE AS `pol_vault_auditor`, applock ต่อ tenant); grant `pol_app` INSERT-only |
| `*.Designer.cs`, `ProducerDbContextModelSnapshot.cs` | snapshot ที่ EF generate — ไม่แก้มือ |

### 2.3 BuildingBlocks.Web (`→ BuildingBlocks.Application, BuildingBlocks.Infrastructure`; FrameworkReference AspNetCore)

cross-cutting HTTP — observability, auth, cors, health, error.

| ไฟล์ | บทบาท | key |
|------|-------|-----|
| `CorrelationIdMiddleware.cs` | stamp `X-Correlation-ID` ต่อ request (reuse ถ้า well-formed ≤128 char ไม่งั้น mint Guid) + ดัน correlation/tenant id เข้า logging scope (id เท่านั้น ไม่ PII) | `AddJsonConsoleLogging()`, `UseCorrelationId()` |
| `GoogleAuthenticationExtensions.cs` | validate Google ID token (RS256 ผ่าน OIDC discovery) | อ่าน `Google:Audiences` (role→client id), บังคับ `email_verified=true`, guard `Google:HostedDomain` (`hd`), map audience→claim `role`; 1 policy ต่อ role (`tenant`/`admin`); fail-fast นอก Dev ถ้า audience ว่าง/placeholder |
| `CorsExtensions.cs` | policy `pol-spa` จาก `Cors:AllowedOrigins` | `AllowAnyHeader/Method` แต่ **ไม่** `AllowAnyOrigin`, ไม่มี credentials; origins ว่าง = ปิด cross-origin (safe default) |
| `HealthChecks.cs` | readiness check + map endpoint | `ProducerDbReadinessCheck` (`CanConnectAsync`), `VaultReadinessCheck` (active key = 32 byte); map `/health/live` (เปล่า), `/health/ready` (tag `ready`), body `{"status":...}` ไม่หลุดรายละเอียด |
| `ProblemDetailsExceptionHandler.cs` | map exception → RFC7807 | `NotFound`→404, `ConcurrencyConflict`→409, `TenantBinding`→500 opaque, `ArgumentException`→400, `InvalidOperation`→409, อื่น→500; Detail เป็น string คงที่ต่อ bucket (ไม่ใช้ `exception.Message` กันหลุด), log เต็มฝั่ง server |

---

## 3. Modules — 5 โมดูลธุรกิจ

ทุกโมดูลรูปทรงเดียวกัน 3 ชั้น: **Domain** (entity/value/event บริสุทธิ์ → SharedKernel เท่านั้น) · **Application** (command/query/handler + repository **port** → Domain, Contracts, BuildingBlocks.Application, Mediator) · **Infrastructure** (EF config + repository impl + `Add<Module>Module()` DI → Application, BuildingBlocks.Infrastructure, EF SqlServer).

แพทเทิร์นร่วม: entity เก็บเงินเป็น scalar (`AmountMinorUnits:long` + `AmountCurrency:string`) แล้ว project กลับเป็น `Money` (EF ignore property computed); command/query เป็น `ITenantScoped`; repository **ไม่มี** method save (UoW commit); handler validate `Money.Of()` จาก payload ดิบ; isolation พึ่ง RLS floor (ไม่ filter tenant ใน SQL).

### 3.1 Products — แคตตาล็อกสินค้า

| ไฟล์ | ชั้น | บทบาท |
|------|------|-------|
| `Product.cs` | Domain | aggregate: Id/TenantId/Name/PriceMinorUnits/PriceCurrency/IsActive/CreatedAtUtc; `Price:Money` (computed); factory `Create(tenantId, name, Money, nowUtc)`; `Rename`, `Deactivate` |
| `CreateProductCommand.cs` | App | `ICommand<Guid>, ITenantScoped` (TenantId, Name, PriceMinorUnits, Currency) + handler: validate Money → `Product.Create` → add → commit |
| `GetProductsQuery.cs` | App | `IQuery<IReadOnlyList<ProductView>>, ITenantScoped` + handler: `ListByTenantAsync` → project `ProductView` |
| `ProductView.cs` | App | read-model record (ProductId, TenantId, Name, Price:Money, IsActive, CreatedAtUtc) |
| `IProductRepository.cs` | App | port: `Add`, `ListByTenantAsync(tenantId)` (ใหม่ก่อน) |
| `ProductConfiguration.cs` | Infra | EF: table `Products`, Name ≤200, Currency ≤3, ignore `Price`, index `(TenantId, IsActive)` |
| `ProductRepository.cs` | Infra | impl เหนือ `ProducerDbContext` |
| `ProductsModuleRegistration.cs` | Infra | `AddProductsModule()` → register repo + ใส่ assembly เข้า `ModuleAssemblies` |

### 3.2 Cart — ตะกร้า

| ไฟล์ | ชั้น | บทบาท |
|------|------|-------|
| `Cart.cs` | Domain | aggregate: TenantId, Status(`CartStatus`), Items; `AddItem` (รวม line ซ้ำ), `RemoveItem`, `Clear`, `MarkCheckedOut`; `Subtotal:Money?` (null เมื่อว่าง); invariant: แก้ได้เฉพาะ Open, item ต้องสกุลเดียว |
| `CartItem.cs` | Domain | owned entity: ProductId/Quantity/UnitPriceMinorUnits/UnitPriceCurrency; `UnitPrice`,`LineTotal` (computed, EF ignore); `IncreaseQuantity` |
| `CartStatus.cs` | Domain | enum: `Open=0`, `CheckedOut=1` |
| `CreateCartCommand.cs` / `CreateCartHandler.cs` | App | `ICommand<Guid>, ITenantScoped`; สร้าง Cart (`Guid.CreateVersion7()`) → add → commit |
| `AddItemToCartCommand.cs` / `AddItemToCartHandler.cs` | App | `ICommand<AddItemResult>, ITenantScoped`; load → ตรวจ tenant → `Money.Of` → `cart.AddItem` → commit |
| `AddItemResult.cs` | App | DTO (CartId, ItemCount, SubtotalMinorUnits, Currency) |
| `ICartRepository.cs` | App | port: `Add`, `GetAsync(cartId)` (รวม Items) |
| `CartConfiguration.cs` / `CartItemConfiguration.cs` | Infra | EF: table `Carts`/`CartItems`, Status string ≤16, own Items cascade delete, ignore computed |
| `CartRepository.cs` | Infra | impl (`GetAsync` ใช้ `.Include(Items)`) |
| `CartModuleRegistration.cs` | Infra | `AddCartModule()` |

### 3.3 Checkout — ล็อกช่องทางจ่าย

| ไฟล์ | ชั้น | บทบาท |
|------|------|-------|
| `CheckoutSession.cs` | Domain | aggregate: TenantId/CartId/AmountMinorUnits/AmountCurrency/Status(`CheckoutStatus`); `Amount:Money` (computed); factory `Start(tenantId, cartId, Money, nowUtc)`; `Confirm` (Started→Confirmed), `Abandon` (Started→Abandoned) — throw ถ้าไม่ใช่ Started |
| `CheckoutStatus.cs` | Domain | enum: `Started=0`, `Confirmed=1`, `Abandoned=2` |
| `StartCheckout.cs` | App | `StartCheckoutCommand/Result/Handler` ในไฟล์เดียว — `Money.Of` → `CheckoutSession.Start` → add → commit |
| `ConfirmCheckout.cs` | App | `ConfirmCheckoutCommand/Result/Handler` — load → `Confirm` → commit. **TODO**: cross-module ไป Orders/Payments ยัง defer (มี `ponytail:` comment) |
| `ICheckoutRepository.cs` | App | port: `Add`, `GetByIdAsync` |
| `CheckoutSessionConfiguration.cs` | Infra | EF: table `CheckoutSessions`, Currency ≤3, ignore `Amount` |
| `CheckoutRepository.cs` | Infra | impl |
| `CheckoutModuleRegistration.cs` | Infra | `AddCheckoutModule()` |

### 3.4 Orders — คำสั่งซื้อ (consume `PaymentPaid`)

| ไฟล์ | ชั้น | บทบาท |
|------|------|-------|
| `Order.cs` | Domain | aggregate: TenantId, PaymentSessionId(nullable), AmountMinorUnits/Currency, Status(`OrderStatus`), CreatedAtUtc, PaidAtUtc; `Create`, `AttachPaymentSession` (guard AwaitingPayment), **`MarkPaid(Money paid, DateTime)`** — re-verify amount+currency, idempotent ผ่าน status, raise `OrderPaid` เฉพาะครั้งแรก |
| `OrderStatus.cs` | Domain | enum: `AwaitingPayment=0`, `Paid=1`, `Cancelled=2` |
| `OrderPaid.cs` | Domain | domain event ภายในโมดูล (`IDomainEvent`) — `OrderPaid(OrderId, PaidAtUtc)`; ข้ามโมดูลใช้ Contracts เท่านั้น |
| `CreateOrderCommand.cs` | App | `ICommand<CreateOrderResult>, ITenantScoped` (OrderId, TenantId, Amount, Method, PspCode) + handler: `Order.Create` → add → commit |
| `OrderPaidConsumer.cs` | App | **consume `PaymentPaid` (INotification)** — load order by PaymentSessionId (ไม่เจอ = เงียบ, กัน retry loop), `order.MarkPaid(amount, occurredAt)` (re-verify ข้างใน), idempotent ผ่าน status |
| `IOrderRepository.cs` | App | port: `GetByPaymentSessionIdAsync`, `Add` |
| `OrderConfiguration.cs` | Infra | EF: table `Orders`, key `ValueGeneratedNever`, filtered index `PaymentSessionId IS NOT NULL` (webhook lookup) + index `TenantId` |
| `OrderRepository.cs` | Infra | impl (`GetByPaymentSessionIdAsync` = `SingleOrDefaultAsync`) |
| `OrdersModuleRegistration.cs` | Infra | `AddOrdersModule()` |

### 3.5 Payments — แกน money path + PSP integration

โมดูลใหญ่สุด. webhook = source of truth. โครง Application แยกโฟลเดอร์ต่อ use-case + โฟลเดอร์ `Ports/`.

**Domain**

| ไฟล์ | บทบาท |
|------|-------|
| `PaymentSession.cs` | aggregate state machine: TenantId/OrderId/Amount(scalar)/Method/Psp(`PspCode`)/Status/PspExternalChargeId/RedirectUrl/timestamps/**`RowVersion`** (SQL rowversion = optimistic concurrency); `Create` (bind order+amount+method+PSP ตั้งแต่แรก ไม่มี attach-race), `BeginRedirect` (Created→Redirected ใต้ RowVersion ก่อนแตะ PSP), `SetPspCharge` (Redirected เท่านั้น กัน double-charge), `MarkPaid(externalChargeId)` (idempotent), `MarkFailed`, `MarkExpired` |
| `PaymentStatus.cs` | enum: `Created=0`, `Redirected=1`, `Paid=2`, `Failed=3`, `Expired=4` — Paid เฉพาะตอน webhook ยืนยัน (ไม่ใช่ browser return) |
| `PspCode.cs` | enum `TwoCTwoP=0`, `Omise=1` + helper `PspCodes.ToCode/FromCode` (`"2c2p"`/`"omise"`, stable wire code) |
| `PspConnection.cs` | entity (ต่อ tenant+PSP): EnabledMethods (csv), **`SecretRefName`** (ชื่อ lookup ใน vault — ไม่ใช่ secret), Metadata (display-only ห้าม secret/PII), IsEnabled; `Create`, `Supports(method)`; unique ต่อ (tenant, PSP) |

**Application — 4 use-case**

| use-case | request | handler ทำอะไร |
|----------|---------|----------------|
| `CreatePaymentSession/` | `CreatePaymentSessionCommand : ICommand<...Result>, ITenantScoped` | `PaymentSession.Create` bind ครบ → add → commit. **ไม่แตะ PSP** |
| `GetPaymentSession/` | `GetPaymentSessionQuery : IQuery<PaymentSessionView>, ITenantScoped` | load → project view (throw ถ้าไม่เจอ) |
| `StartRedirect/` | `StartRedirectCommand : ICommand<...Result>, ITenantScoped` | **แตะ PSP ครั้งแรก**: ถ้า Redirected+มี URL แล้ว → คืนเลย (idempotent); ไม่งั้น `BeginRedirect` → save ใต้ RowVersion (loser ชน `ConcurrencyConflict` → ดึง URL ผู้ชนะคืน, กัน double-charge); ผู้ชนะ: fetch connection → `Vault.RevealAsync` secret → `Factory.For(psp)` → `adapter.CreateRedirectChargeAsync` → `SetPspCharge` → save → คืน URL |
| `HandlePspWebhook/` | `HandlePspWebhookCommand : ICommand<WebhookHandled>` (**ไม่** `ITenantScoped` — มาจาก PSP ไม่มี auth) | (1) load connection by `PspConnectionId` (ไม่ใช่จาก URL) + reveal secret + get adapter; (2) `VerifyWebhook` ไม่ผ่าน → `Rejected`; (3) ใน 1 transaction: `ParseWebhook` → claim idempotency multi-key **connection-scoped** (`{psp}:{pspConnectionId}:event:{eventId}` + `{psp}:{pspConnectionId}:charge:{externalChargeId}:{status}`) ซ้ำ → `Duplicate` — prefix `{psp}:{pspConnectionId}:` กัน event/charge id ที่ unique แค่ระดับ merchant ชนข้าม tenant/connection; (4) **fetch-to-confirm** `FetchChargeAsync` (server-to-server GET, retry) — webhook status แค่ advisory; ไม่ Paid → `Ignored`; (5) Paid: `GetByExternalChargeAsync` → `session.MarkPaid` → `Outbox.Enqueue(PaymentPaid)` → save อะตอม → `Processed` |

**Application — Ports/**

| ไฟล์ | บทบาท |
|------|-------|
| `IPspAdapter.cs` | contract ต่อ PSP: `Psp` (code), `CreateRedirectChargeAsync` (PSP call แรก), `VerifyWebhook`, `FetchChargeAsync` (confirm, retry ได้), `ParseWebhook` |
| `IPspAdapterFactory.cs` | `For(PspCode) → IPspAdapter` (throw ถ้าไม่มี) |
| `IPaymentSessionRepository.cs` | `Add`, `GetByIdAsync`, `GetByExternalChargeAsync(psp, externalChargeId)` (webhook lookup) |
| `IPspConnectionRepository.cs` | `GetAsync(tenantId, psp)`, `GetByIdAsync(pspConnectionId)` (webhook routing) |
| `PspContracts.cs` | DTO: `PspCharge(externalChargeId, redirectUrl)`, `PspChargeStatus{Pending,Paid,Failed}`, `WebhookEvent(eventId, externalChargeId, status)` |

**Infrastructure**

| ไฟล์ | บทบาท |
|------|-------|
| `Persistence/PaymentSessionConfiguration.cs` | EF: table `PaymentSessions`, ignore `Amount`, **RowVersion token**, unique filtered index `(Psp, PspExternalChargeId) IS NOT NULL` (lookup + กัน double-attach), index `OrderId` |
| `Persistence/PaymentSessionRepository.cs` | impl (`GetByExternalChargeAsync` query by `(Psp, PspExternalChargeId)`) |
| `Persistence/PspConnectionConfiguration.cs` | EF: table `PspConnections`, unique `(TenantId, Psp)`, SecretRefName ≤128, Metadata ≤4000 (display-only) |
| `Persistence/PspConnectionRepository.cs` | impl `GetAsync`/`GetByIdAsync` |
| `Psp/PspAdapterBase.cs` | primitive ร่วม (stateless singleton): `CreateClient` (pooled `IHttpClientFactory`), `FormatMajorUnitAmount`, `EncodeJwtHs256`/`TryReadVerifiedJwtHs256` (alg-pinned HS256 + constant-time compare ผ่าน `CryptographicOperations.FixedTimeEquals`), `SendOnceAsync` (charge-create ไม่ retry — timeout ห้าม double-charge), `SendWithRetryAsync` (fetch GET idempotent, backoff+jitter, ≤2 retry, จัด 5xx/408/429) |
| `Psp/TwoCTwoPAdapter.cs` | adapter 2C2P (card redirect): body เป็น `{"payload": HS256-JWT}` เซ็นด้วย merchant secret. `CreateRedirectCharge` POST `/payment/4.3/paymentToken` (invoiceNo = `session.Id("N")` = external charge id คงที่), ต้องได้ respCode `0000` + webPaymentUrl. `Verify` ตรวจ HS256 + merchantID. `Parse` อ่าน invoiceNo/tranRef/respCode→status. `Fetch` POST `/payment/4.3/paymentInquiry`. BaseUrl ตาม `UseSandbox` |
| `Psp/OmiseAdapter.cs` | adapter Omise (card redirect; **PromptPay defer** → `NotSupportedException`). auth = HTTP Basic (secretKey). `CreateRedirectCharge` POST `/charges` (form, `Idempotency-Key = session.Id("N")`, ไม่ส่ง card data) → chrg_id + authorize_uri. `Verify` = well-formedness gate (HMAC defer; fetch-to-confirm คือ authority). `Parse` map status. `Fetch` GET `/charges/{id}`. `GuardKeyEnvironment` กัน prefix `skey_test_`/`skey_live_` ไม่ตรง `UseSandbox` |
| `Psp/PspAdapterFactory.cs` | impl factory — inject `IEnumerable<IPspAdapter>` → dict by `adapter.Psp`; singleton |
| `Psp/PspOptions.cs` | config non-secret section `Psp`: `UseSandbox` (default true), `TwoCTwoPOptions`, `OmiseOptions` (base url, return url) |
| `Psp/PspSecretEnvelope.cs` | shape JSON ของ secret หลัง reveal: `TwoCTwoPSecret{merchantId, secretKey}`, `OmiseSecret{secretKey}` — parse post-reveal ไม่เก็บใน `PspConnection` |
| `PaymentsModuleRegistration.cs` | `AddPaymentsModule()` — register repo (Scoped), named pooled HttpClient ต่อ PSP (timeout 30s), adapter (Singleton) + factory |

---

## 4. Hosts — composition root

2 host, reference โมดูลชุดเดียวกัน (Products/Cart/Checkout/Orders/Payments + BuildingBlocks) แต่ประกอบคนละแบบ.

### Hosts/Api — HTTP host (SPA + webhook)

| ไฟล์ | บทบาท |
|------|-------|
| `Program.cs` | ประกอบ HTTP: register Mediator (source-gen, scope-validate fail-fast ใน Dev) + `TenantGuardBehavior` + `ProducerDbContext` (+`SessionContextConnectionInterceptor`) + 5 โมดูล + Vault/Psp options + Google auth. **middleware order**: CorrelationId → ExceptionHandler → StatusCodePages → CORS → RateLimiter → AuthN → AuthZ → HealthChecks → OpenAPI(Dev). **endpoint**: `POST /webhooks/{pspConnectionId:guid}` (ไม่ auth, rate-limited, resolve tenant จาก PSP connection แล้ว bind AmbientTenant), `POST /products`, `POST /payment-sessions`, `POST /payment-sessions/{id:guid}/redirect` (ทั้งหมด authorize `tenant`). มี `PspCodeJsonConverter` แปลง enum↔wire code |
| `HttpTenantContext.cs` | impl `ITenantContext` (Scoped) precedence: AmbientTenant binding > claim `tenant_id` > `Tenant:DevTenantId` (Dev เท่านั้น) |
| `WebhookRateLimiting.cs` | sliding-window 60 permit/10s (5 segment) partition by **source IP** (ไม่ใช่ pspConnectionId กัน budget exhaustion), QueueLimit=0 → 429 + RetryAfter; ต้องตั้ง ForwardedHeaders หลัง reverse proxy |
| `DesignTimeDbContextFactories.cs` | factory ตอน `dotnet ef migrations` — connection จาก env `POL_DESIGN_SQL` |
| `appsettings.json` | prod defaults: `ConnectionStrings:Producer` (`pol_app`, inject password runtime), `Google:Audiences`/`HostedDomain` (ตั้งต่อ env), `Cors:AllowedOrigins`, `Vault:*` (inject runtime); prod ไม่ publish OpenAPI |
| `appsettings.Development.json` | Dev: connection localhost, `Tenant:DevTenantId`, Cors `http://localhost:5120`, dev test key (placeholder ต่อ real integration) |
| `Properties/launchSettings.json` | profile http (5100) / https (5101); `ASPNETCORE_ENVIRONMENT=Development` |

### Hosts/Worker — background host (outbox)

| ไฟล์ | บทบาท |
|------|-------|
| `Program.cs` | ประกอบ background: register Mediator + BuildingBlocksInfrastructure + **`OutboxDispatcher`** + `ProducerDbContext` เป็น user `pol_worker` (อ่าน outbox ข้าม tenant, write ต่อ message แบบ RLS-scoped, ไม่มี bypass) + 5 โมดูล. endpoint = HealthChecks อย่างเดียว (ไม่มี routing/auth/CORS) |
| `WorkerTenantContext.cs` | impl `ITenantContext` (Scoped) — ไม่มี HTTP/principal; tenant = AmbientTenant ที่ dispatcher bind ต่อ message; `HasTenant=false` ตอน lease pass (SESSION_CONTEXT ไม่ตั้ง → scan outbox ข้าม tenant) |
| `WorkerModuleAssemblies.cs` | list assembly (เท่ากับ Api) ให้ build producer model ตรง migration |
| `appsettings.json` | `ConnectionStrings:Worker` = `pol_worker`; `Vault:*` inject runtime; ไม่มี config HTTP/auth |

---

## 5. แกนข้ามชั้นที่ต้องเข้าใจ

### 5.1 Multi-tenant isolation (floor = SQL Server RLS)

```
request → ITenantContext resolve TenantId (จาก claim/binding ไม่ใช่ URL)
        → SessionContextConnectionInterceptor set SESSION_CONTEXT('TenantId') ตอน connection open
        → SECURITY POLICY (fn_tenant_predicate) FILTER+BLOCK ทุก query ที่ DB
```

floor อยู่ที่ DB ไม่พึ่ง app code. `TenantGuardBehavior` (pipeline) เป็นชั้นสะดวกเสริม. principal แยกหน้าที่: `pol_app` (tenant CRUD), `pol_worker` (outbox), `pol_webhook_resolver`/`pol_vault_auditor` (bypass proc เฉพาะจุด), `pol_admin` (bypass read).

### 5.2 Vault (envelope encryption + tamper-evident audit)

```
Store:  plaintext --AES-256-GCM(DEK)--> ciphertext ;  DEK --wrap(KEK ต่อ tenant)--> EncryptedDek ;  เก็บ + hint(last4)
Reveal: unwrap DEK --> decrypt --> เขียน reveal-audit (fail-closed) --> คืน plaintext (server-only, ไม่ log)
Rotate master key: re-wrap DEK ไป active key (plaintext ไม่โผล่)
Audit: hash chain ต่อ tenant (Seq + PrevHash) ; verify เดิน chain ดัก gap/edit
```

PSP secret ไม่เคยอยู่ใน `PspConnection` — เก็บแค่ `SecretRefName`, reveal ตอนจะเรียก PSP เท่านั้น.

### 5.3 Payment happy-path (end-to-end)

```
1. CreateOrderCommand            → Order = AwaitingPayment
2. CreatePaymentSessionCommand   → PaymentSession = Created (bind order+amount+method+PSP, ไม่แตะ PSP)
3. StartRedirectCommand          → BeginRedirect (RowVersion claim) → [PSP call แรก] CreateRedirectChargeAsync → คืน redirect URL
4. ลูกค้า redirect ไปจ่ายที่หน้า PSP
5. PSP webhook → HandlePspWebhook → verify sig → idempotency multi-key → [fetch-to-confirm] FetchChargeAsync
                                   → Paid → session.MarkPaid → Outbox.Enqueue(PaymentPaid)  [atomic]
6. OutboxDispatcher (Worker)     → publish PaymentPaid → OrderPaidConsumer → order.MarkPaid (re-verify amount+currency) → Order = Paid
```

safeguard สำคัญ: webhook (ไม่ใช่ browser return) คือ source of truth · fetch-to-confirm ตรวจซ้ำ server-to-server · idempotency multi-key กัน replay · RowVersion กัน double-charge · `Order.MarkPaid` re-verify amount+currency (ไม่เชื่อแค่ id) · routing webhook ด้วย `PspConnectionId` ไม่ใช่ค่าใน URL ก่อน verify.

---

## หมายเหตุ

- ตัวเลขไฟล์: 22 `.csproj`, ~134 `.cs` (ไม่นับ `bin/`, `obj/`, EF designer/snapshot)
- การจ่ายจริงจบที่ `Order = Paid` — ไม่มี issuance/หลังจ่าย
- จุดที่ยัง defer: `ConfirmCheckout` ยังไม่เชื่อม Orders/Payments (มี `ponytail:` comment), Omise PromptPay (`NotSupportedException`), Omise webhook HMAC (พึ่ง fetch-to-confirm)
