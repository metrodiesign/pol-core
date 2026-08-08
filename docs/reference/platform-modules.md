# pol-core Platform Modules

> As-built 2026-08-07. หน้านี้สรุป current tracked implementation; target design และ migration history อยู่ใน
> `.ai/specs/` แยกต่างหาก.

## ภาพรวม

pol-core เป็น modular monolith ที่รันผ่าน `Api` host และใช้ `/api/v1` เป็น route root. Current business flow:

```mermaid
flowchart LR
    P["Products: live upstream"] --> C["Carts"]
    C --> O["Orders"]
    O --> T["Payments"]
    T --> X["Outbox events"]
```

ไม่มี persisted Checkout, local product catalogue หรือ audience-first route.

## Current module set

| Module | หน้าที่ปัจจุบัน | Boundary หลัก |
|---|---|---|
| `Admins` | platform admin identity, sessions, profile และ admin operations | control plane |
| `Iam` | permissions, groups, roles และ role grants | control plane |
| `Divisions` | reference division CRUD | control plane `cfg` |
| `Levels` | reference level CRUD | control plane `cfg` |
| `Offices` | reference office CRUD | control plane `cfg` |
| `Positions` | reference position CRUD | control plane `cfg` |
| `Merchants` | merchant profile, merchant-user OIDC BFF, registration/KYC, provisioning | merchant identity/runtime |
| `Products` | live upstream insurance-document search/lookup | external source |
| `Carts` | open cart, server-resolved lines, optimistic concurrency | merchant runtime `shop` |
| `Orders` | direct Cart-to-Order, order lifecycle, summary/reveal | merchant runtime `shop` |
| `Payments` | payment session, PSP redirect/webhook/status orchestration | merchant runtime `txn` |

รวม 11 โมดูล. ไม่มี current `Checkouts`, `MasterData`, `Producer` หรือ `Tenant` module ใน tracked implementation.

## Module details

### Admins + IAM

`Admins` จัดการ platform admin account, session, profile และ accessible merchant binding.
`Iam` เป็น catalog ของ permission/group/role และ role-permission grants.

Current admin routes อยู่ใต้ `/api/v1/admins...`; master-data routes เป็น top-level `/api/v1/positions`,
`/offices`, `/levels`, `/divisions`. Authorization ใช้ policy `admin`, permission key และ CSRF สำหรับ mutations.

Role resolution ใช้เฉพาะ role/group/permission ที่ `Active`. Shared role ใช้ `MerchantId = NULL`; custom merchant
role ต้องมี owner.

### Reference master data

สี่โมดูลมี shape เดียวกัน:

- aggregate มี `Code`, `Name`, `Status`
- `Status` เป็น enum `Active=0`, `Inactive=1`
- `Code` immutable, unique, regex `^[a-z0-9_]+$`
- PUT เปลี่ยนชื่อและ status; DELETE เป็น soft-deactivate
- store อยู่ `Persistence.ControlPlane`; migration owner คือ `PolDbContext`

### Merchants

Merchant user ใช้ OIDC BFF providers Google/Microsoft Entra, opaque `__Host-mch_session` cookie และ CSRF
double-submit. Commerce actor ได้ `MerchantId`, `SaleCode` และ Active-only IAM permission จาก server.

KYC photo:

- multipart `kycPhoto`, maximum 2 MiB
- allowlisted media type + magic bytes
- deterministic staging key ตาม operation id
- store คืน `(Key, CreatedNew)` เพื่อ idempotent retry และ race-safe cleanup
- DB เก็บ object key เท่านั้น; lifecycle ผ่าน outbox
- orphan staging TTL 24 ชั่วโมง; `PhotoStagingPruneService` เริ่มหลัง 5 นาที แล้ว sweep ทุก 1 ชั่วโมง
- single-host production ใช้ `merchant-user-photos:/app/merchant-user-photos`
- multi-host ต้องใช้ shared object store adapter

Provisioning เป็น idempotent saga ระหว่าง merchant DB, encrypted vault และ outbox. PSP credential write-only,
encrypted และไม่คืน/log.

### Products

Products เรียก upstream stored procedure ผ่าน `ISpDocumentGateway`:

- `GET /api/v1/products` เป็น endpoint เดียว
- รับ `page`, `limit`, typed `productFilters`
- `SaleCode` มาจาก actor server-side
- response ไม่มี local `Guid`; upstream document identifier คือ `DocumentNo`
- ไม่มี `shop.Products`, upsert หรือ local product repository

Cart add และ order creation ใช้ `LookupDocumentQuery` แบบ internal เพื่ออ่าน price/metadata สด.
`IDocumentSaleProbe` ตรวจเอกสารที่ platform ขายแล้วหรือกำลังจ่าย.

### Carts

Cart อยู่ `shop.Carts`/`shop.CartItems` และมี `Open`/`CheckedOut` state. `Version` เป็น application-managed
optimistic concurrency token ที่ bump ทุก mutation.

Request add-item รับเฉพาะ:

```json
{
  "productCode": "...",
  "variantCode": "CMI",
  "quantity": 1
}
```

server resolve product, price, sale code, variant name และ typed metadata. Duplicate `ProductCode` ใน Cart
ถูก reject. รายละเอียดอยู่ [`carts.md`](carts.md).

### Orders

`POST /api/v1/orders` สร้าง order โดยตรงจาก Cart. `OrderCreationCoordinator` revalidate upstream document,
payment state, sale probe และ Cart version ก่อน transaction.

Transaction เดียวเขียน `shop.Orders`, immutable `shop.OrderItems`, notification ใน `txn.OutboxMessages` และ
เปลี่ยน Cart เป็น `CheckedOut`. Order states: `Pending`, `Paid`, `Failed`, `Expired`, `Refunded`, `Cancelled`.

Customer summary ใช้ opaque token มี TTL; merchant detail metadata reveal มี audit และ fail-closed.

### Payments

`txn.PaymentSessions` ผูกกับ `OrderId + MerchantId` และ PSP connection. Current routes:

- `POST /api/v1/payments/sessions`
- `POST /api/v1/payments/sessions/{paymentSessionId}/redirect`
- `GET /api/v1/payments/sessions/{paymentSessionId}`
- `POST /api/v1/webhooks/{pspConnectionId:guid}`

Webhook verify, idempotency และ fetch-to-confirm. Payment events ใช้ `PaymentPaid`, `PaymentFailed`,
`PaymentExpired`; stale correlation ถูก ignore และ state transition ของ Order serialize ด้วย row lock.

## Persistence topology

| Project | Current responsibility |
|---|---|
| `Persistence.ControlPlane` | admin, IAM, cfg reference data |
| `Persistence.MerchantUsers` | merchant-user identity/session/registration rows |
| `Persistence.MerchantRuntime` | merchant profile, carts, orders, payments, outbox, photo store |
| `Persistence.Provisioning` | provisioning/vault workflow |

Schemas:

- `admin`: platform users/session/role/access/audit/provisioning
- `iam`: permission catalog and grants
- `cfg`: four master-data tables
- `merch`: merchants, users, sessions, registration, vault, user outbox
- `shop`: carts, cart items, orders, order items, reveal audits
- `txn`: payment sessions, PSP connections, idempotency, outbox

`PolDbContext` เป็น migration owner. Current migrations:

1. `20260807042818_InitialSchema`
2. `20260807042828_SecurityObjects`
3. `20260807042833_SeedData`

ไม่มี SQL RLS. Isolation floor อยู่ app layer: query filters, actor binding, tenant-key validation และ guarded
write. Intentional cross-merchant probe ใช้ explicit `IgnoreQueryFilters()` ที่มี test/allowlist รองรับ.

## Layer/dependency rules

```text
Hosts -> Persistence/Infrastructure -> Application -> Domain -> SharedKernel
                    \-> Contracts/BuildingBlocks ports
```

Domain ไม่ reference EF Core หรือ Infrastructure. Application ประกาศ port; Persistence/Infrastructure เป็น
implementation. Host composition เป็นจุดเดียวที่ผูก concrete adapter กับ module contract.

## Current route map

| Area | Routes |
|---|---|
| Products | `/api/v1/products` |
| Carts | `/api/v1/carts...` |
| Orders | `/api/v1/orders...` |
| Payments | `/api/v1/payments/sessions...`, `/api/v1/webhooks/{pspConnectionId}` |
| Admins | `/api/v1/admins...` |
| Merchant users | `/api/v1/merchants/auth...`, `/api/v1/merchants/users...` |
| Merchant provisioning | `/api/v1/merchants...` |
| Master data | `/api/v1/positions`, `/offices`, `/levels`, `/divisions` |
| Reconciliation | `GET /api/v1/reports/reconciliation` |

Authorization policy แยก audience; path ไม่ใช้ `/api/admin/v1` หรือ `/api/producer/v1`.

## Retired surfaces

ไม่มี current project/table/contract/route/grant สำหรับ:

- Checkout session/item/event และ `/api/v1/checkouts*`
- local product catalogue `shop.Products`
- policy entity/audit/report routes
- SQL RLS/security policy/bypass principal
- standalone `Worker` runtime host

ถ้าเอกสารเก่าหรือ client ยังใช้ surface เหล่านี้ ให้ยึด code และ
`.ai/specs/merchant-commerce-erd-reset/FE-MIGRATION.md` เป็น migration map.

## Source of truth

- `src/Hosts/Api/Program.cs`
- `src/Modules/*`
- `src/Persistence/*`
- `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/`
- [`entity-fields.md`](entity-fields.md)
- [`layers-guide.md`](layers-guide.md)
- [`db-connection-and-rls.md`](db-connection-and-rls.md)
