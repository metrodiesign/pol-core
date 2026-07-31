# Requirements — foundation-scaffold

> WHAT the foundation must do. EARS notation (ดู `.ai/shared/EARS.md`). REQ-ID เสถียร —
> design.md / tasks.md / tests อ้างถึง ห้าม renumber.
>
> ขอบเขต: นี่คือ spec ของ **foundation scaffold** — โครงสร้าง solution, shared spine, รูปทรง
> โมดูล, hosts, tests, RLS/idempotency/outbox/vault pattern, และ migration baseline. ไม่ครอบคลุม
> business logic เต็มของแต่ละโมดูล (จะแยก spec ต่อ feature) — แต่กำหนด "สัญญา" ที่ทุกโมดูลต้อง
> สร้างทับ. ที่มา: PROJECT_CONTEXT.md, ARCHITECTURE.md, CODING_STANDARDS.md, SECURITY_RULES.md,
> stack/dotnet.md และ 16 foundational decisions ใน PLAN.md.

## REQ-1: Solution shape (Modular Monolith · Clean Architecture · CQRS)

**User Story:** As a platform engineer, I want a single deployable backend แยกเป็นโมดูลตามชั้น
Clean Architecture, so that ทุกโมดูลแยกความรับผิดชอบชัดและ dependency ชี้เข้า domain ทางเดียว.

**Acceptance Criteria (EARS):**
- 1.1 THE SYSTEM SHALL จัดวาง source เป็น `src/SharedKernel`, `src/Contracts`,
  `src/BuildingBlocks/{BuildingBlocks.Application,BuildingBlocks.Infrastructure}`,
  `src/Modules/<M>/{<M>.Domain,<M>.Application,<M>.Infrastructure}` สำหรับ M ใน
  Products, Cart, Checkout, Orders, Payments, และ `src/Hosts/{TenantConsole,Worker}`
  (TenantConsole = Backend API เดียว; AdminConsole host ถูกถอด — ดู REQ-10).
- 1.2 THE SYSTEM SHALL บังคับทิศ dependency Domain ← Application ← Infrastructure ← Host;
  Domain ห้ามอ้าง EF Core / ASP.NET.
- 1.3 THE SYSTEM SHALL อนุญาตให้ `<M>.Application` reference เฉพาะ `<M>.Domain` + `Contracts` +
  `BuildingBlocks.Application` เท่านั้น.
- 1.4 THE SYSTEM SHALL อนุญาตให้ `<M>.Infrastructure` reference เฉพาะ `<M>.Application` +
  `BuildingBlocks.Infrastructure`.
- 1.5 IF โมดูลหนึ่ง reference `*.Domain` หรือ `*.Infrastructure` ของอีกโมดูล THEN THE SYSTEM
  SHALL ทำให้ architecture test fail.
- 1.6 THE SYSTEM SHALL ให้ทั้ง solution build เขียวด้วย `dotnet build -warnaserror` โดยมี 0 warning
  (TreatWarningsAsErrors + nullable-clean).

## REQ-2: Cross-module communication ผ่าน Contracts + Mediator เท่านั้น

**User Story:** As a module author, I want โมดูลคุยกันผ่าน message สัญญาเดียว, so that ไม่มีการ
coupling ตรงข้ามโมดูลและเปลี่ยน internal ของโมดูลได้โดยไม่กระทบโมดูลอื่น.

**Acceptance Criteria (EARS):**
- 2.1 THE SYSTEM SHALL ให้โมดูลสื่อสารข้ามกันผ่าน `Contracts` (Mediator `INotification`) เท่านั้น.
- 2.2 THE SYSTEM SHALL นิยาม `PaymentPaid(Guid PaymentSessionId, Guid OrderId, Guid TenantId,
  Money Amount, string PspCode, string ExternalChargeId, string EventId, DateTime OccurredAtUtc)`
  เป็น `INotification` ใน `Contracts` พร้อม `SchemaVersion = "v1"`.
- 2.3 WHEN handler ปลายสุด (Hosts) build THE SYSTEM SHALL ลงทะเบียน handler อัตโนมัติผ่าน
  `Mediator.SourceGenerator` (`PrivateAssets=all` ที่ Hosts).
- 2.4 IF request ใด ๆ ไม่มี handler THEN THE SYSTEM SHALL ปล่อย build diagnostic (ห้ามปิด warning นี้).
- 2.5 THE SYSTEM SHALL ให้ `IMediator` เป็น Singleton ได้ แต่ handler/pipeline ที่พึ่ง `DbContext`
  ต้องเป็น Scoped (หรือ inject `IDbContextFactory`); เปิด `ValidateScopes=true`.

## REQ-3: Money เป็น value object เดียวใน SharedKernel

**User Story:** As a payments engineer, I want จำนวนเงินเป็น minor-unit + ISO4217 ตัวเดียวร่วมทุก
seam, so that ไม่มี decimal/float drift ระหว่าง Payments (สตางค์) กับ Orders (บาท).

**Acceptance Criteria (EARS):**
- 3.1 THE SYSTEM SHALL ให้ `Money { long MinorUnits, string Currency }` อยู่ใน SharedKernel เป็น
  source เดียวของจำนวนเงินที่ cross-module seam.
- 3.2 WHEN สร้าง `Money.Of(minorUnits, currency)` THE SYSTEM SHALL validate ว่า currency เป็น
  ISO4217 ที่รองรับ (THB, USD, JPY) และ minorUnits ไม่ติดลบ.
- 3.3 IF currency ไม่รองรับ หรือ minorUnits ติดลบ THEN THE SYSTEM SHALL throw และไม่สร้าง instance.
- 3.4 IF `Add` ถูกเรียกข้ามสกุลเงิน THEN THE SYSTEM SHALL ปฏิเสธ (ต้อง same-currency).
- 3.5 THE SYSTEM SHALL serialize/deserialize `Money` ผ่าน `MoneyJsonConverter` ด้วย JSON camelCase.
- 3.6 THE SYSTEM SHALL ห้าม map `Money` เป็น owned/complex type ใน EF; entity เก็บสองคอลัมน์ scalar
  (`AmountMinorUnits` long + `AmountCurrency` string HasMaxLength(3)) แล้ว expose
  `Amount => Money.Of(AmountMinorUnits, AmountCurrency)` และ `builder.Ignore(x => x.Amount)`.

## REQ-4: BuildingBlocks — shared application + infrastructure spine

**User Story:** As a module author, I want cross-cutting service (clock, UoW, idempotency, outbox,
vault, tenant context) สำเร็จรูป, so that ทุกโมดูลใช้ floor เดียวกันโดยไม่ต้องประดิษฐ์ซ้ำ.

**Acceptance Criteria (EARS):**
- 4.1 THE SYSTEM SHALL ให้ `BuildingBlocks.Application` export `ITenantContext`, `IClock`,
  `IUnitOfWork`, `IIdempotencyStore`, `IOutbox`, `IVaultSecretStore`, `ITenantScoped`.
- 4.2 THE SYSTEM SHALL ให้ `BuildingBlocks.Infrastructure` export `ProducerDbContext` (schema
  `producer`) และ `AdminDbContext` (schema `admin`).
- 4.3 WHEN เรียก `AddBuildingBlocksInfrastructure()` THE SYSTEM SHALL ลงทะเบียน clock,
  RLS interceptor, `IUnitOfWork`, `IIdempotencyStore`, `IVaultSecretStore`, `IOutbox`, OutboxDispatcher.
- 4.4 THE SYSTEM SHALL ให้ `ProducerDbContext` เป็นเจ้าของตาราง Outbox / Idempotency / Vault และ
  discover `IEntityTypeConfiguration` ของแต่ละโมดูลจาก `ModuleAssemblies.Producer` ตอน model-build.
- 4.5 WHEN handler เรียก `IUnitOfWork.ExecuteInTransactionAsync(...)` THE SYSTEM SHALL ครอบงาน
  ทั้งหมดใน transaction เดียว และ commit outbox row พร้อม SaveChanges ของ handler.

## REQ-5: Multi-tenant isolation — RLS floor

**User Story:** As a security owner, I want tenant isolation บังคับที่ data layer ไม่พึ่ง app code,
so that backend ร่วมกันแต่ข้อมูลข้าม tenant รั่วไม่ได้แม้ app เผลอ.

**Acceptance Criteria (EARS):**
- 5.1 THE SYSTEM SHALL ใช้ SQL Server native RLS + `SESSION_CONTEXT('TenantId')` เป็น isolation floor;
  EF global query filter เป็นชั้นสะดวกเสริม ไม่ใช่ floor.
- 5.2 WHEN connection ถูกเปิด THE SYSTEM SHALL set `SESSION_CONTEXT('TenantId')` ผ่าน
  `DbConnectionInterceptor` ตอน connection-open (ไม่ใช่ต่อ query — pooled connection per-connection scope).
- 5.3 WHILE command/query implement `ITenantScoped` แต่ไม่มี tenant ใน context THE SYSTEM SHALL
  ปฏิเสธ request.
- 5.4 IF โค้ดเขียน raw SQL ข้าม tenant scope หรือเรียก `IgnoreQueryFilters` ข้าม tenant THEN THE
  SYSTEM SHALL ถือเป็นการละเมิด (ban; ครอบด้วย test).
- 5.5 WHERE ต้องการ admin cross-tenant access THE SYSTEM SHALL ทำผ่าน DB principal แยกเท่านั้น
  พร้อม reason/correlation id ลง audit — ห้าม bypass RLS จาก session ของ Merchant Console.
- 5.6 WHEN pooled connection ถูก reuse ข้าม request THE SYSTEM SHALL ไม่ retain tenant เดิม
  (test พิสูจน์ leak ปิด).

## REQ-6: Idempotency + Outbox (จ่ายไม่ซ้ำ, publish reliable)

**User Story:** As a payments engineer, I want การ deliver ซ้ำไม่ทำให้สถานะเคลื่อนสองครั้ง และ
event ออกแน่นอน, so that webhook replay หรือ retry ไม่ทำให้ Order ถูกจ่ายซ้ำ.

**Acceptance Criteria (EARS):**
- 6.1 WHEN `IIdempotencyStore.TryBeginAsync(keys, context)` ถูกเรียกครั้งแรก THE SYSTEM SHALL คืน
  `true`; WHEN เป็น replay ของ key เดิม THE SYSTEM SHALL คืน `false`.
- 6.2 THE SYSTEM SHALL claim idempotency แบบ multi-key อย่างน้อย `(psp,eventId)` และ
  `(psp,externalChargeId,normalizedStatus)` แบบ atomic.
- 6.3 WHEN handler enqueue ผ่าน `IOutbox.Enqueue(notification)` THE SYSTEM SHALL track row แล้ว
  commit พร้อม SaveChanges ของ handler (ไม่ publish นอก transaction).
- 6.4 THE SYSTEM SHALL ให้ OutboxDispatcher poll แบบมี lock/lease + poison/DLQ และ consumer เป็น
  idempotent.
- 6.5 IF claim idempotency คืน `false` (replay) THEN THE SYSTEM SHALL ไม่ transition สถานะและไม่
  enqueue event ซ้ำ.

## REQ-7: Credential vault (secret write-only, read masked)

**User Story:** As a security owner, I want secret ทุกตัวผ่าน vault เท่านั้น, so that ไม่มี
credential หลุดในโค้ด/ log และอ่านกลับเห็นแต่ค่า mask.

**Acceptance Criteria (EARS):**
- 7.1 THE SYSTEM SHALL ให้เข้าถึง secret ผ่าน `IVaultSecretStore` เท่านั้น
  (`StoreAsync`/`RevealAsync`/`MaskedAsync`/`ExistsAsync`).
- 7.2 IF โค้ด hardcode key/secret หรือ log secret/PII THEN THE SYSTEM SHALL ถือเป็นการละเมิด
  (gate `check-secrets` + review).
- 7.3 WHEN อ่าน secret กลับเพื่อแสดงผล THE SYSTEM SHALL คืนค่า mask (ไม่คืน plaintext ออกนอก
  boundary ที่ verify).
- 7.4 THE SYSTEM SHALL ออกแบบ vault ให้รองรับ envelope encryption (per-tenant KEK ใน KMS/HSM, DEK
  ต่อ secret, key id+version + rotation) — provider KMS จริงเป็น backlog (REQ-12).

## REQ-8: Redirect-only PSP boundary (คง PCI SAQ A)

**User Story:** As a compliance owner, I want PSP adapter คืนแค่ hosted redirect URL, so that
โดเมนเราไม่แตะข้อมูลบัตรและคงสถานะ SAQ A.

**Acceptance Criteria (EARS):**
- 8.1 THE SYSTEM SHALL ให้ `IPspAdapter` คืน hosted redirect URL เท่านั้น; ห้ามมี card-number field,
  Omise.js, hosted-fields, iframe, หรือ display-QR.
- 8.2 THE SYSTEM SHALL ใช้ enum `PspCode { TwoCTwoP, Omise }` กับ code string เสถียร `"2c2p"`/`"omise"`
  และ payment method code string `"card"`/`"promptpay"`/`"installment"`.
- 8.3 WHERE ช่องทางคือ Omise PromptPay THE SYSTEM SHALL ใช้ Payment Links+ (hosted `transaction_url`)
  เท่านั้น — ห้าม source+charge (offline QR = non-redirect = ต้องห้าม).
- 8.4 IF requirement/ticket นำไปสู่ non-redirect flow, card field, settlement, billing, public
  onboarding, หรือ issuance THEN THE SYSTEM SHALL หยุดและถามก่อน (Non-Goals ใน PROJECT_CONTEXT).

## REQ-9: Webhook = source of truth (verify → claim → confirm → transition → emit, ใน 1 tx)

**User Story:** As a payments engineer, I want สถานะการจ่ายมาจาก webhook ที่ตรวจแล้วเท่านั้น, so that
browser return ปลอมไม่ทำให้ Order กลายเป็น Paid.

**Acceptance Criteria (EARS):**
- 9.1 THE SYSTEM SHALL ถือ webhook เป็น source of truth; browser return URL เป็น UX เท่านั้น.
- 9.2 WHEN webhook เข้ามา THE SYSTEM SHALL route ด้วย PSP connection id หรือ signed path —
  ห้าม trust tenant/PSP จาก raw URL path ก่อน verify signature.
- 9.3 WHEN ประมวลผล webhook THE SYSTEM SHALL ทำตามลำดับ verify signature (secret จาก
  `IVaultSecretStore`) → claim idempotency (multi-key) → fetch-to-confirm กับ PSP → transition →
  enqueue `PaymentPaid` ผ่าน `IOutbox` — ทั้งหมดภายใน `IUnitOfWork.ExecuteInTransactionAsync` เดียว.
- 9.4 IF signature ไม่ผ่าน THEN THE SYSTEM SHALL ปฏิเสธและไม่ transition.
- 9.5 WHEN Orders รับ `PaymentPaid` THE SYSTEM SHALL verify amount + currency (ไม่ใช่แค่ PaymentId)
  ก่อนเปลี่ยนสถานะเป็น Paid.

## REQ-10: Host — Backend API เดียว, แยก authz ที่ระดับ endpoint/role

> เดิม REQ-10 แยกเป็น 2 console host (Tenant public / Admin internal). ปรับ architecture: pol-core เป็น
> **Backend API ตัวเดียว** เสิร์ฟ 2 browser SPA แยก (pol-tenant, pol-admin) ที่ทำนอก repo. การแยก
> admin/tenant authz ย้ายจาก host-isolation มาที่ endpoint/role (เจตนา 10.2 คงเดิม). AdminConsole host
> ถูกถอด (เคยเป็น stub เดียว). cross-tenant super-admin (ถ้าต้องการในอนาคต) = internal tool แยก ไม่ใช่
> public API นี้.

**User Story:** As a security owner, I want backend เป็น API เดียวที่ RLS-enforced, so that ทุก request ของ
SPA ถูกจำกัด tenant ที่ระดับ DB และไม่มี public host ไหนถือ connection ที่ bypass RLS ได้.

**Acceptance Criteria (EARS):**
- 10.1 THE SYSTEM SHALL ให้ pol-core เป็น Backend API host เดียว (principal `pol_app`, ไม่ใช่สมาชิก
  `pol_rls_bypass`) เสิร์ฟทั้ง pol-tenant และ pol-admin SPA และรับ Google ID token ที่ audience เป็น OAuth
  client ของ SPA ใดก็ได้ใน `Google:ClientIds`; Worker เป็น background host แยก (`pol_worker`).
- 10.2 IF endpoint admin (cross-tenant / approve / config) ถูกเรียกโดย principal/role ที่ไม่มีสิทธิ์ admin
  THEN THE SYSTEM SHALL ปฏิเสธ (authorization ระดับ endpoint/role — บังคับเมื่อ admin endpoint จริงถูกสร้าง).
- 10.3 THE SYSTEM SHALL วาง `Mediator.SourceGenerator` ที่ host (project ปลายสุด) และ wire
  `ModuleAssemblies` เข้า DI.

## REQ-11: Test foundation + naming/standards gate

**User Story:** As a maintainer, I want test project ครบทุกชั้นและ standard ถูกบังคับ, so that
foundation เขียวพิสูจน์ได้และ convention ไม่ drift.

**Acceptance Criteria (EARS):**
- 11.1 THE SYSTEM SHALL มี test project: `SharedKernel.Tests`, `BuildingBlocks.Tests`,
  `Payments.Tests`, `Orders.Tests`, `Architecture.Tests`, `Hosts.Tests`.
- 11.2 THE SYSTEM SHALL ให้ `Architecture.Tests` บังคับทิศ dependency (REQ-1.2..1.5).
- 11.3 THE SYSTEM SHALL ใช้ naming: acronym >= 3 ตัว = PascalCase (`Psp` ไม่ใช่ `PSP`); async method
  ลงท้าย `Async`; interface prefix `I`; private field `_camelCase`; PK = `{Entity}Id` (Guid);
  UTC datetime column ลงท้าย `Utc`; JSON camelCase.
- 11.4 THE SYSTEM SHALL run `dotnet test` เขียวทั้ง solution; ห้าม commit `[Fact(Skip=...)]` / `.only`.
- 11.5 WHERE path เป็น critical (webhook / idempotency / money) THE SYSTEM SHALL ครอบด้วย
  property-based test (ดู `/spec-pbt`).
- 11.6 WHEN ship stub โดยตั้งใจ THE SYSTEM SHALL mark ด้วยคอมเมนต์ `// ponytail:` ระบุสิ่งที่ stub
  และ upgrade path (เช่น PSP adapter ยังไม่ยิง HTTP จริง).

## REQ-12: Backlog (กั้นออกจาก foundation อย่างชัดเจน)

**User Story:** As a planner, I want งานที่ตั้งใจเลื่อนถูกบันทึกเป็น backlog ชัด, so that ไม่ถูกเข้าใจ
ผิดว่า foundation ยังไม่เสร็จและไม่หลุดเข้ามาทำก่อนเวลา.

**Acceptance Criteria (EARS):**
- 12.1 THE SYSTEM SHALL ถือ "real PSP HTTP integration" (ยิง 2C2P / Omise จริง) เป็น backlog —
  foundation มีแค่ adapter stub ที่ mark `// ponytail:`.
- 12.2 THE SYSTEM SHALL ถือ "vault KMS/HSM provider" (envelope encryption จริง) เป็น backlog —
  foundation มี `IVaultSecretStore` + provider พื้นฐานพอให้ flow ทำงาน.
- 12.3 WHEN เริ่มงาน backlog แต่ละชิ้น THE SYSTEM SHALL เปิด spec ของตัวเอง (ไม่ทำใน foundation spec นี้).
