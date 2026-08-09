# คู่มือ 6 Layers ของ pol-core

> As-built 2026-08-07. อ้างอิงโค้ดปัจจุบันและ solution ที่ tracked; `docs/reference/src-structure.md` ใช้ดู path
> รายละเอียดรายไฟล์.

## สรุป

| Layer | หน้าที่ |
|---|---|
| 1. `SharedKernel` | `Entity`, `AggregateRoot`, `Money`, currency และ JSON converter กลาง |
| 2. `Contracts` | published event contracts ข้ามโมดูล เช่น payment และ notification |
| 3. `BuildingBlocks` | actor context, authorization/merchant guard, ports, persistence primitives, web middleware |
| 4. `Persistence` | EF contexts, mappings, repositories, transaction/outbox adapters และ isolation floor |
| 5. `Modules` | domain/application/infrastructure ของ business module แต่ละตัว |
| 6. `Hosts` | composition root และ HTTP/background runtime; ปัจจุบันมี `Api` เป็น host ที่ใช้งาน |

Dependency direction: outer layer reference inner layer. `Domain` ห้าม reference `Infrastructure` หรือ EF Core.
Module ไม่ reference `.Domain` ของ module อื่น; cross-module communication ใช้ `Contracts`, `BuildingBlocks`
ports หรือ host composition.

## Current module set

Current tracked modules มี 11 ตัว:

1. `Admins`
2. `Carts`
3. `Divisions`
4. `Iam`
5. `Levels`
6. `Merchants`
7. `Offices`
8. `Orders`
9. `Payments`
10. `Positions`
11. `Products`

ไม่มี current `Checkouts`, `MasterData`, `Producer` หรือ local product catalogue contract ใน source ที่ใช้งาน.

## 1. SharedKernel

Path: `src/SharedKernel`.

- `Entity<TId>` และ `AggregateRoot<TId>` เป็น base identity/aggregate.
- `Money` เป็นเงินกลาง; amount ใช้ `decimal` และ currency ใช้ ISO code.
- `Iso4217` ตรวจสกุลเงินและ scale.
- `MoneyJsonConverter` คุม wire representation ของ Money.

SharedKernel ไม่มี reference ไป layer อื่น.

## 2. Contracts

Path: `src/Contracts`.

Contracts เป็น event/data seam ไม่ใช่บ้านของ HTTP DTO. Current event families ครอบคลุม:

- `PaymentPaid`, `PaymentFailed`, `PaymentExpired`
- customer order notification
- merchant-user registration/KYC lifecycle

Outbox enqueue เกิดใน transaction owner; background dispatcher เป็นผู้ส่ง event ภายหลัง.

ไม่มี current `CheckoutConfirmed` contract.

## 3. BuildingBlocks

Paths:

- `src/BuildingBlocks/BuildingBlocks.Application`
- `src/BuildingBlocks/BuildingBlocks.Infrastructure`
- `src/BuildingBlocks/BuildingBlocks.Web`

หน้าที่หลัก:

- `IActorContext`, merchant/user binding และ authorization primitives
- `IUnitOfWork`, transaction execution strategy และ outbox abstractions
- `IDocumentSaleProbe`, `IPhotoStore` และ cross-module ports ที่ไม่ควรอยู่ใน module ใด module หนึ่ง
- EF tenant descriptors, write guard และ shared middleware/problem handling

BuildingBlocks ไม่เก็บ business aggregate ของ merchant หรือ order.

## 4. Persistence

Current persistence projects:

| Project | ขอบเขต |
|---|---|
| `Persistence.ControlPlane` | admin/IAM/master data และ control-plane stores |
| `Persistence.MerchantUsers` | merchant-user identity, sessions, registration/KYC user rows |
| `Persistence.MerchantRuntime` | merchants, carts, orders, payments, outbox, photo/runtime stores |
| `Persistence.Provisioning` | merchant provisioning และ encrypted vault operations |

`PolDbContext` เป็น migration owner. Runtime contexts ใช้ model ที่เหมาะกับ boundary ของตน; ไม่ให้ runtime
context เป็น migration owner.

Isolation ปัจจุบันไม่ใช่ SQL RLS:

1. query filter จำกัด `MerchantId == CurrentMerchant`
2. actor context บังคับ merchant/sale binding
3. sealed write guard ตรวจ tenant key และ operation authority ก่อน commit
4. intentional cross-merchant probes ใช้ explicit escape hatch และเหตุผลใน code/test

Migration chain ปัจจุบัน:

1. `20260807042818_InitialSchema`
2. `20260807042828_SecurityObjects`
3. `20260807042833_SeedData`
4. `20260808161508_OneBasedPersistedEnumStorage`

## 5. Modules

Module ปกติแบ่งเป็น:

```text
<Module>.Domain          aggregate, value object, enum, invariant
<Module>.Application     command/query/handler, DTO, port
<Module>.Infrastructure  module registration, EF mapping, module adapter
```

### Business flow ปัจจุบัน

```mermaid
flowchart LR
    P["Products live upstream"] --> C["Carts"]
    C --> O["Orders"]
    O --> T["Payments"]
    T --> E["Contracts + Outbox"]
    E --> D["Dispatcher in Api host"]
```

Products ไม่ persist catalogue. Cart add-item และ order creation lookup upstream สด; Order creation revalidate
ทุก line ก่อน transaction. Order transaction เขียน Order, OrderItems, notification outbox และ `CheckedOut` Cart
พร้อมกัน. Payment events update order state แบบ versioned/idempotent.

### Master data

`Divisions`, `Levels`, `Offices`, `Positions` เป็น control-plane reference modules. Domain field ใช้ `Status`
enum (`Active=1`, `Inactive=2`), ไม่ใช่ `IsActive`. Store implementation อยู่ `Persistence.ControlPlane`;
route เป็น top-level `/api/v1/{divisions|levels|offices|positions}`.

### Merchants and KYC

Merchant user ใช้ OIDC BFF/session cookie. KYC photo ผ่าน private staged object:

- max 2 MiB, media type + magic validation
- deterministic operation key และ `(Key, CreatedNew)` idempotency result
- failed attempt discard เฉพาะ object ที่ call นั้นสร้างใหม่
- staging TTL 24 ชั่วโมง
- `PhotoStagingPruneService`: initial 5 นาที, interval 1 ชั่วโมง
- production single-host named volume `merchant-user-photos:/app/merchant-user-photos`

## 6. Hosts

Path: `src/Hosts/Api`.

`Program.cs` เป็น composition root ของ current API:

- root route `/api/v1`
- public products, merchant-user, admin และ merchant-provisioning areas อยู่ใน host เดียว
- background outbox dispatch และ photo staging prune ทำงานใน process ของ `Api`
- ไม่มี current tracked `Worker` host ที่เป็น runtime dependency

Route audience ใช้ authorization policy เช่น `merchant-user` หรือ `admin`; ไม่ใช้ audience-first prefix อย่าง
`/api/admin/v1` หรือ `/api/producer/v1`.

## Testing map

Test projects ปัจจุบันแยกตาม module และ boundary:

- `Architecture.Tests` — dependency direction และ forbidden reference
- `BuildingBlocks.Tests`, `SharedKernel.Tests` — primitives/guards
- `Admins.Tests`, `Carts.Tests`, `Divisions.Tests`, `Iam.Tests`, `Levels.Tests`, `Merchants.Tests`,
  `Offices.Tests`, `Orders.Tests`, `Payments.Tests`, `Positions.Tests`, `Products.Tests`
- `Hosts.Tests` — route composition, policy/CSRF gates, host behavior
- `Integration.Tests` — persistence/migration/integration paths

## Source of truth

- `.ai/shared/ARCHITECTURE.md`
- `.ai/shared/CODING_STANDARDS.md`
- `src/Hosts/Api/Program.cs`
- `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/`
- `docs/reference/src-structure.md`
- `docs/reference/entity-fields.md`
