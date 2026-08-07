# Entity and Field Reference

> As-built หลัง merchant-commerce ERD reset 2026-08-07. เอกสารนี้สรุป contract ปัจจุบัน; รายละเอียดทุก column,
> index, FK และ check constraint ใช้ migration/model snapshot เป็น source of truth.

## Source of truth

ลำดับอ้างอิง:

1. `docs/reference/merchant-commerce-payment-erd-revised-kyc-simplified.md` — ERD canon
2. `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/20260807042818_InitialSchema.cs`
3. `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/20260807042828_SecurityObjects.cs`
4. `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/20260807042833_SeedData.cs`
5. `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/PolDbContextModelSnapshot.cs`
6. EF configurations ในแต่ละ module — runtime ownership/query/write rules

Migration chain ต้องมีสามตัวตามลำดับนี้เท่านั้น. Database เป้าหมายคือ SQL Server 2025 build
`17.0.4045.5` ขึ้นไป, compatibility level 170, collation `Thai_100_CI_AS`.

## Schema ownership

| Schema | ข้อมูลหลัก | Runtime owner |
|---|---|---|
| `admin` | platform users, sessions, role assignments, auth/audit | Control plane |
| `iam` | permission groups, permissions, roles, role-permission grants | Control plane |
| `cfg` | positions, offices, levels, divisions | Control plane |
| `merch` | merchants, merchant users/sessions/roles, registration, vault | Merchant identity/runtime ตาม entity |
| `shop` | carts, cart items, orders, order items | Merchant runtime |
| `txn` | payment sessions, outbox, reveal/audit records | Merchant runtime |

`PolDbContext` เป็น migration owner เท่านั้น. Runtime ใช้ `ControlPlaneDbContext`, `MerchantUserDbContext` และ
`MerchantRuntimeDbContext`. Principal เดียวคือ `pol_app`; authorization, tenant query filter และ guarded write
อยู่ app layer. ไม่มี SQL RLS, bypass principal หรือ `SESSION_CONTEXT`.

## Admin, IAM and reference data

- Admin/User identity ใช้ scalar columns สำหรับ subject, status, authorization version และ audit timestamps.
- IAM catalog มี 19 Active permissions, 7 Active groups, 4 seed roles และ 25 grants.
- Role resolution รวมเฉพาะ role, group และ permission ที่ status `Active`.
- `cfg` seed มี 12 positions, 8 offices, 10 levels และ 10 divisions; status `Active`.
- Role assignments อ้าง `iam.Roles`; shared seed role มี `MerchantId = NULL`, custom merchant role ต้องมี merchant owner.

## Merchant and KYC

- Merchant profile เก็บ business/search fields เป็น scalar; allowlisted extension อยู่ `merch.Merchants.Metadata`.
- Merchant user ใช้ `IdentityType`; ไม่มี `PersonType`.
- KYC photo persistence เก็บ object key เท่านั้น. Binary อยู่ private object store.
- Upload รองรับ image ที่ผ่าน content type + magic validation ขนาดไม่เกิน 2 MiB.
- Omission ตอน resubmit คง key เดิม. Lifecycle outbox commit/delete แบบ idempotent; orphan staging TTL 24 ชั่วโมง.
- API, registration history และ log ห้ามคืน object key, path, credential หรือ PII ที่ไม่จำเป็น.

## Commerce

### Cart

`shop.Carts` เป็น aggregate owner. State มี `Open` และ `CheckedOut`; `Version` เป็น optimistic concurrency.
`SaleCode` มาจาก authenticated merchant user.

`shop.CartItems` เป็น generic line:

| Field | ความหมาย |
|---|---|
| `Id` | client mutation handle; route ใช้ field นี้ |
| `CartId`, `MerchantId` | parent + tenant boundary |
| `ProductCode` | authoritative upstream document/product code |
| `VariantCode` | `Motor` หรือ `NonMotor` สำหรับ integration ปัจจุบัน |
| `VariantName` | server-owned display snapshot |
| `Quantity` | จำนวนมากกว่า 0 |
| `UnitPriceAmount`, `UnitPriceCurrency` | server-owned source price |
| `Metadata` | typed, PII-free native JSON snapshot |

Browser ส่งเฉพาะ `productCode`, `variantCode`, `quantity`. ห้ามส่ง price, name, metadata, `SaleCode` หรือ merchant ID.

### Direct Cart-to-Order

`POST /api/v1/orders` revalidate product availability แล้ว transaction เดียวทำ:

1. lock/reload Cart และตรวจ `Version`/state/lines
2. สร้าง `shop.Orders` สถานะ `Pending`
3. สร้าง immutable `shop.OrderItems`
4. enqueue customer notification ใน `txn.OutboxMessages`
5. เปลี่ยน Cart เป็น `CheckedOut`

`shop.OrderNoSeq` เป็น raw SQL sequence. `OrderNo` มาจาก sequence; `OrderId` เป็น aggregate key.
Order customer/contact เป็น scalar PII และไม่ปรากฏใน generic customer summary. Merchant detail reveal fail-closed และมี audit.

`shop.OrderItems` snapshot `ProductCode`, `VariantCode`, `VariantName`, quantity, unit price, zero discount และ typed
metadata. ไม่มี policy entity/report/audit surface.

Order status:

| Value | Wire name | ความหมาย |
|---:|---|---|
| 0 | `Pending` | รอ payment |
| 1 | `Paid` | PSP-confirmed |
| 2 | `Failed` | attempt ล่าสุดล้มเหลว |
| 3 | `Expired` | attempt ล่าสุดหมดอายุ |
| 4 | `Refunded` | คืนเงิน |
| 5 | `Cancelled` | ยกเลิกแบบ terminal |

### Payment

`txn.PaymentSessions` ผูก `OrderId + MerchantId`, เก็บ canonical payment method, PSP connection, amount/currency,
status, correlation และ concurrency token. มี filtered unique index กัน open session มากกว่าหนึ่งต่อ Order.

Lifecycle ใช้ versioned `PaymentPaid`, `PaymentFailed`, `PaymentExpired`. Order writers serialize ด้วย Order row lock.
Event correlation เก่าถูก ignore; paid event ที่ขัดกับ terminal stateสร้าง reconciliation evidence. Webhook ยังคง verify,
idempotent และ fetch-to-confirm.

## Native JSON allowlist

มี native `json` exactly 5 columns:

| Column | Contract |
|---|---|
| `merch.Merchants.Metadata` | typed merchant metadata allowlist |
| `shop.CartItems.Metadata` | typed commerce item metadata |
| `shop.OrderItems.Metadata` | immutable typed commerce item metadata |
| `txn.OutboxMessages.Payload` | closed event registry payload |
| `merch.MerchantUserOutboxMessages.Payload` | closed registration/KYC lifecycle payload |

Unknown/secret-shaped field ถูก reject. ห้ามเก็บ credential, password, token, photo object key หรือ customer PII ใน
native JSON. SQL Server ตรวจ invalid JSON write ทุก column.

## Raw objects and grants

- `shop.OrderNoSeq` — Order number sequence
- `merch.RegistrationNotices` — raw notice table excluded from EF migrations model
- `pol_app` — runtime principal เดียว; grant matrix อยู่ `SecurityObjects`

`SecurityObjects.Down` revoke ก่อน drop และ drop dependency-safe. Production ห้ามใช้ migration Down เป็น rollback;
restore verified backup ตาม release runbook.

## Retired surfaces

ไม่มี project/table/contract/route/grant สำหรับ:

- Checkout session/item/event และ `/api/v1/checkouts*`
- Order item policy, policy audit/report และ route ที่มี `/policy`
- legacy product catalogue persistence
- SQL RLS/security policy/bypass principal
- standalone demo seed script/data funnel

Route เก่าตอบ `404`; ไม่มี alias หรือ overlap window. Frontend mappingอยู่
`.ai/specs/merchant-commerce-erd-reset/FE-MIGRATION.md` และ published contract อยู่
`.ai/specs/merchant-commerce-erd-reset/openapi-cart-order.yaml`.

## Baseline seed safety

Seed มี synthetic merchant หนึ่งรายและ disabled PSP connection เพื่อพิสูจน์ FK/shape เท่านั้น. ไม่มี credential,
login subject, PII, Cart, Order หรือ payment row. Fresh assertion ตรวจ schema, migration history, JSON columns,
raw objects, grants, IAM/cfg counts และ retired table absence ก่อน integration suite.
