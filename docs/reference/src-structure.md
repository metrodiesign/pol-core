# โครงสร้าง Source ปัจจุบัน

เอกสารนี้อธิบาย layout ที่ tracked ของ `pol-core` หลัง `platform-restructure-v1` โดยยึด 4 source projects และ 3 test projects ใน solution ปัจจุบัน.

## Projects และเส้นทางหลัก

```text
src/
  Api/
    Api/                         composition root, HTTP routes, BFF และ background dispatch
    BuildingBlocks.Web/          middleware, CORS, health และ ProblemDetails
  Application/
    BuildingBlocks.Application/  ports, actor/tenant guards, paging/SFS และ cross-cutting contracts
    Contracts/                   published cross-module notifications/events
    Modules/                     module commands, queries, handlers และ ports
  Domain/                         one Domain project
    SharedKernel/                Entity, AggregateRoot, Money และ value types
    Modules/                     domain aggregates, invariants และ enums
  Infrastructure/
    BuildingBlocks.Infrastructure/ migration owner, persistence primitives, outbox และ vault base
    Modules/                     module registrations, adapters และ EF mappings
    Persistence/                 runtime contexts, repositories, dispatchers และ provisioning
    Tools/                       explicit operator tools เช่น workforce identity migrator

tests/
  UnitTests/                     domain/application/unit coverage แยกตาม module
  ArchitectureTests/             dependency, mapping, isolation และ write-floor checks
  IntegrationTests/              host, SQL Server, migration และ end-to-end contract checks
```

Project wrappers หรือ compatibility/build residue รุ่นเก่าที่อาจเหลือใน working tree ไม่ใช่ tracked source projects ปัจจุบัน. Source of truth คือ project paths ด้านบนและ `pol-core.slnx`.

## Dependency direction

```text
Api -> Infrastructure -> Application -> Domain
Domain/SharedKernel เป็น folder ภายใน project Domain ไม่ใช่ project เพิ่ม
Application/Contracts เป็น event seam ที่ host และ adapter ใช้ร่วมกัน
```

`Domain` ไม่ reference EF Core หรือ `Infrastructure`. `Application` ประกาศ command/query, handler contract และ port; `Infrastructure` เป็น implementation; `Api` เป็น composition root และเป็นเจ้าของ route รวมถึง coordinator ที่ต้องเชื่อมหลาย module. เนื่องจากทุก domain module อยู่ใน project เดียวกัน architecture tests จึงบังคับ module namespace/dependency boundary แทนการอ้าง project reference แยก; cross-module business fact ใช้ `Application/Contracts` หรือ approved application port.

## Current module roots

Module names ที่ source ใช้จริงมี `Access`, `Accounts`, `Admins`, `Carts`, `Checkouts`, `Governance`, `Iam`, `Merchants`, `Migration`, `Notifications`, `Orders`, `Payments`, `Platform`, `Products` และ `Reporting`. บาง module เป็น support-only และจึงมีเพียงบาง layer เช่น `Migration`/`Platform` อยู่ใน Application หรือ `Access` มี Domain/Infrastructure mapping.

Commerce path คือ `Products -> Carts -> Orders -> Checkouts/PaymentLink -> Payments -> Notifications`. `Checkouts` ใน current code เป็น capability/payment-link และ transaction boundary; ไม่ใช่ persisted legacy Checkout session.

## Runtime persistence

Runtime register เพียง 2 contexts:

| Context | เจ้าของข้อมูล | ข้อสังเกต |
|---|---|---|
| `ControlPlaneDbContext` | `admin`, `iam`, `acct`, `access`, `oauth`, `cfg`, merchant identity/profile/vault และ `txn` provider/routing/capability/approval configuration | identity/access rows อยู่ context เดียวกัน; merchant-scoped reads ใช้ actor/filter ตาม entity |
| `CommerceDbContext` | `shop`, `checkout` และ `txn` เฉพาะ payment attempts, Transactions/events, inbound webhooks, outbox และ notification runtime | query filter `MerchantId == CurrentMerchant` และ user/order ownership filter เป็น isolation floor |

`PolDbContext` อยู่ใน `src/Infrastructure/BuildingBlocks.Infrastructure/Persistence/PolDbContext.cs` และเป็น migration owner/design-time model เท่านั้น. API startup ไม่ register context นี้; local migration ใช้ explicit operator command.

ทุก runtime context ใช้ `GuardedRuntimeDbContext`, tenant-key metadata และ sealed write authorizer. Named escape hatches สำหรับ admin/cross-merchant read อยู่ใน allowlist พร้อม architecture tests; SQL RLS, `SESSION_CONTEXT` และ bypass principal ไม่ใช่ runtime mechanism ปัจจุบัน.

## API และ background runtime

`src/Api/Api/Program.cs` รวม route root `/api/v1`, named OpenAPI documents, BFF auth, CORS, module registrations และ background dispatch ใน process เดียว. ไม่มี Worker host ที่เป็น runtime dependency. Background services จัดการ outbox, notification/webhook delivery, session/photo pruning และ maintenance ตาม registrations ใน `src/Infrastructure/Persistence/` และ `src/Api/Api/BackgroundDispatch/`.

Infrastructure routes อยู่นอก API area ได้แก่ `/health/*`, `/openapi/*` และ `/scalar`. API area ใช้ path scheme `/api/v1/{area}`; audience/auth policy อยู่ metadata ของ endpoint ไม่อยู่ใน path.

## Tests

- `tests/UnitTests` ตรวจ domain/application behavior และ contracts
- `tests/ArchitectureTests` ตรวจ project references, mapping ownership, query/write floor, bypass allowlist และ legacy retirement
- `tests/IntegrationTests` ตรวจ real SQL Server, migrations, host routes, OpenAPI, transactions, outbox และ provider capture boundaries

ชุดทดสอบจาก handoff `platform-restructure-v1` ผ่าน local gates แล้ว แต่ live Entra, PSP, Email/SMS และ production cutover authorization ยังไม่มีหลักฐานใน environment นี้ จึงไม่ประกาศ production-ready.

## Source of truth

- `src/Api/Api/Program.cs`
- `src/Api/Api/ControlPlane/CanonicalCommerceEndpoints.cs`
- `src/Application/Modules/`
- `src/Domain/Modules/`
- `src/Infrastructure/Modules/`
- `src/Infrastructure/Persistence/`
- `src/Infrastructure/BuildingBlocks.Infrastructure/Persistence/Migrations/PolDbContextModelSnapshot.cs`
