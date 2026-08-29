# Requirements: insurance-pivot

> Status: approved 2026-07-20

## Overview

pol-core วันนี้เป็น payment orchestration platform ทั่วไป (catalog → cart → checkout → order →
payment/PSP redirect, จบที่ `PaymentPaid`). งานนี้ reshape domain retail ให้เป็นระบบ **ขายประกันภัยออนไลน์
+ รับชำระเงิน** โดย **reuse pipeline เดิมทั้งชุด** ไม่สร้างโมดูลใหม่ — เติม semantic ประกันเข้า `Product` ที่มี
อยู่แล้ว บวกกับปิด gap 2 จุดที่ user ยืนยันแล้วว่าจำเป็นต่อ flow นี้จริง: Order ต้องรู้ว่าซื้อกรมธรรม์แผนไหน
(อาจหลายแผน/order) และ Checkout ต้องเก็บว่าใครคือผู้เอาประกัน

**Terminology mapping (business term → entity ที่ reuse/เพิ่ม):**

| Business term | Entity/โมดูล | หมายเหตุ |
|---|---|---|
| กรมธรรม์/แผนประกัน | `Products.Domain.Product` | เติม field ใหม่ 3 ตัว (REQ-1) — ไม่สร้าง `Policy`/`InsurancePlan` แยก |
| คำสั่งซื้อ (N กรมธรรม์/order) | `Orders.Domain.Order` + `OrderLine` ใหม่ | `OrderLine` เป็น entity ใหม่ (REQ-6) — lifecycle ของ `Order` เองไม่แก้ (REQ-3) |
| ผู้เอาประกัน | field ใหม่บน `OrderLine` — **1 คน/line** (PII) | REQ-7 — ไม่ใช่ entity แยก, ผูกกับ `OrderLine` ไม่ใช่ระดับ checkout/order เดียว |
| ชำระเบี้ย/Transaction | `Payments.Domain.Session` (+ PSP adapter 2C2P/Omise) | ไม่แก้เลย (REQ-3) — คำว่า "Transaction" หมายถึง entity นี้ ไม่ใช่ entity ใหม่ |

Flow ที่ reuse อยู่แล้ววันนี้ (`Product` สร้างจาก producer → `Cart` (ไม่แก้) → `Checkout.Confirm` เปิด `Order`
(`AwaitingPayment`) → `Payment/Session` redirect ไป PSP → webhook confirm → `PaymentPaid` →
`Order.MarkPaid` → `Order.Paid`) ครอบเป้าหมาย "Product → Order → Transaction สำเร็จ" อยู่แล้วในเชิงกลไก —
ส่วนที่ต้องเติมจริงคือ field เชิงประกันบน `Product` (REQ-1/2), การรักษาว่า order ผูกกับกรมธรรม์แผนไหนบ้าง
(REQ-6), ข้อมูลผู้เอาประกัน (REQ-7), บวก seed/docs (REQ-4/5) และ regression guard ว่า Order lifecycle/Payment
ไม่ถูกแตะ (REQ-3)

**Locked decisions (user ตัดสินแล้ว — ห้าม re-litigate ในเฟสถัดไป):**

- `Product` = กรมธรรม์/แผนประกัน — reuse entity เดิม เติม semantic ประกัน ไม่สร้าง entity `Policy` แยก
- Insurance line: **generic ชุดเดียวทุก line** (PA/Travel/Motor/Property ใช้ field เดียวกัน) — ไม่มี
  per-line schema, ไม่มี `ProductLine` enum
- `Insurer` เก็บเป็น **column บน `products`** ตรง (เช่น `InsurerName`) — ไม่สร้าง master-data/entity แยก,
  ไม่มี FK ใหม่
- `CoverageDurationDays`: `int` จำนวนวัน (Product เป็น catalog template ไม่มี start/end date จริง)
- Route/area: คง `/api/v1/products`/`/orders`/`/payments` เดิมทั้งหมด — ไม่ rename
- Order-to-Product traceability: **N กรมธรรม์/Order** — เพิ่ม entity `OrderLine` ใหม่ (ไม่ใช่ 1 field เดียว);
  `OrderLine` snapshot ค่าเชิงประกัน (`SumInsured`/`CoverageDurationDays`/`Insurer`) **ณ ตอนซื้อ** — ไม่อ่านสด
  จาก `Product`
- ผู้เอาประกัน: **เก็บข้อมูลตอน checkout ต่อ `OrderLine`** (1 คน/line, ไม่ใช่ 1 คน/checkout) — PII, field set
  สุดท้ายยืนยันตอน design, baseline = ชื่อ/เลขบัตรประชาชน/วันเกิด; **ไม่เข้ารหัส field-level ตอนนี้** (mark
  เป็น hardening TODO ก่อน prod — ดู REQ-7.6); **mask `IdNumber` บน list/summary response, คืนเต็มเฉพาะ
  detail read + เขียน audit ทุกครั้งที่ reveal เต็ม** (mirror pattern มาสก์ secret ที่มีอยู่แล้ว — locked,
  override การตัดสินก่อนหน้าที่บอกว่า "คืนเต็มทุกที่")
- Payment/Transaction = ชำระเบี้ย — reuse payment orchestration + PSP (2C2P/Omise) เดิมทั้งชุด ห้าม rebuild
- Flow จบที่ `Order.Paid`/`Payment.Paid` สำเร็จ — **ไม่มีขั้น issue policy** (policy number, policy
  document/PDF, issuance workflow) ในระบบนี้เลย แม้จะเก็บ OrderLine + ข้อมูลผู้เอาประกันแล้วก็ตาม

## Scope

**In (ถูกแตะโดยสเปกนี้):**
- `Products.Domain.Product` — เพิ่ม 3 field + validation (REQ-1)
- `POST /api/v1/products`, `GET /api/v1/products` request/response DTOs (REQ-2)
- `Orders.Domain` — entity ใหม่ `OrderLine` (ProductId/Quantity/UnitPrice + insurance-term snapshot +
  insured-person ต่อ line) (REQ-6/7)
- `Checkouts.Domain.Session` + `StartCheckoutCommand` — snapshot item lines ต่อ line (ไม่ใช่ยอดรวมเดียว
  เหมือนวันนี้) พร้อมข้อมูลผู้เอาประกันต่อ line (REQ-6/7)
- `Contracts.CheckoutConfirmed` (v1 payload) — เพิ่ม line snapshot + insured-person data ต่อ line (REQ-6/7)
- seed data (REQ-4), docs canon (REQ-5)

**Out (ห้ามแตะ/ห้ามสร้าง — ไม่เปลี่ยนจาก draft แรก):**
- `Orders.Domain.OrderStatus` lifecycle และ verify-logic ของ `Order.MarkPaid` (REQ-3.1)
- `Payments.Domain.Session`/`SessionStatus`, `IPspAdapter`, PSP adapter ใดๆ (2C2P/Omise) (REQ-3.2)
- `Carts.Domain` เอง — `Item` มี `ProductId`/`Quantity`/`UnitPrice` อยู่แล้ว พอสำหรับ REQ-6 โดยไม่ต้องแก้
  entity/behavior ของ Cart เลย (มีแค่ `Checkouts`/`Contracts`/`Orders` ที่ต้องแก้ให้ข้อมูลนี้ไหลผ่าน)
- iam/admin/auth (Google SSO, RBAC, session)
- policy issuance ใดๆ, claims/renewal/endorsement/underwriting/commission/reinsurance
- entity ใหม่นอกเหนือ `OrderLine` (ไม่มี `Policy`, `InsurancePlan`, หรือ `Insurer` เป็น entity)

## REQ-1: Product carries insurance-plan attributes

**User Story:** As a producer, I want to catalog an insurance plan with its sum insured, coverage
duration and insurer, so a customer can see what they are buying before paying the premium.

**Acceptance Criteria (EARS):**
- 1.1 THE SYSTEM SHALL เพิ่ม field ใหม่ 3 ตัวเข้า `Products.Domain.Product` (entity เดิม ไม่สร้างใหม่): `SumInsured` (`Money` — ทุนประกัน), `CoverageDurationDays` (`int` — ระยะคุ้มครองเป็นจำนวนวัน), `Insurer` (`string` — ผู้รับประกันภัย). Field เดียวกันนี้ใช้กับทุก insurance line (PA/Travel/Motor/Property) แบบ generic — ไม่มี per-line schema หรือ `ProductLine` enum.
- 1.2 THE SYSTEM SHALL ตีความ `Product.Price` เดิม (field ที่มีอยู่แล้ว) เป็นเบี้ยประกัน (premium) ของแผนนั้น — ไม่เพิ่ม field ใหม่แยกสำหรับเบี้ย.
- 1.3 WHEN สร้าง `Product` ใหม่ (`Product.Create`), THE SYSTEM SHALL validate `SumInsured.Amount > 0`, `CoverageDurationDays > 0`, และ `Insurer` ไม่เป็นค่าว่าง/whitespace-only — ระดับความเข้มเดียวกับ validation ที่ `Name`/`Price` มีอยู่แล้ววันนี้.
- 1.4 THE SYSTEM SHALL เก็บ `SumInsured` เป็น `Money` มาตรฐาน (`DECIMAL(19,4)` + currency ISO 4217) เหมือน `Price` ทุกชั้น (domain/persistence/wire) — ห้าม float/double, ห้าม minor units.
- 1.5 IF `SumInsured.Currency` ของ `Product` ไม่เท่ากับ `Price.Currency` ของ `Product` เดียวกัน THEN THE SYSTEM SHALL reject การสร้าง/แก้ไขนั้นด้วย validation error (`ArgumentException` ระดับเดียวกับ validation อื่นของ entity นี้).
- 1.6 THE SYSTEM SHALL NOT สร้าง entity `Policy`/`InsurancePlan`/`Insurer` แยกออกจาก `Product` — `Insurer` เป็น column string บน `products` ตรง (เช่น `InsurerName`) ไม่ใช่ master-data/reference table ใหม่ (locked decision).

## REQ-2: Catalog API surfaces insurance fields

**User Story:** As a producer, I want the existing product create/list API to accept and return the
insurance fields, so the console can show plan details without a separate lookup.

**Acceptance Criteria (EARS):**
- 2.1 THE SYSTEM SHALL extend `CreateProductRequest`/`CreateProductCommand` ที่ผูกกับ `POST /api/v1/products` ให้รับ `sumInsured`, `coverageDurationDays`, `insurer` เพิ่มจาก `name`/`price` เดิม (endpoint เดิม ไม่สร้าง route ใหม่).
- 2.2 THE SYSTEM SHALL extend `ProductListItem` (response ของ `GET /api/v1/products`) ให้คืน `sumInsured`, `coverageDurationDays`, `insurer` เพิ่มจาก field เดิม.
- 2.3 THE SYSTEM SHALL ส่ง `sumInsured` บน wire ตาม Money JSON convention เดิมของแพลตฟอร์ม (object `{ "amount": "<string ทศนิยม 4 ตำแหน่ง>", "currency": "<ISO4217>" }`) เหมือน `price` — ห้าม float/double บน wire.
- 2.4 IF request สร้าง `Product` ขาด field บังคับใหม่ (`sumInsured`/`coverageDurationDays`/`insurer`) THEN THE SYSTEM SHALL ตอบตาม error contract เดิมของ endpoint นี้ (ProblemDetails, ไม่เปลี่ยนรูปแบบ error).

## REQ-3: Order lifecycle & Payment flow reused unmodified

**User Story:** As the platform, I want the existing Order-state-machine and Cart→Checkout→Order→Payment
pipeline to keep working unmodified for insurance products, so premium collection reuses the proven,
audited payment orchestration instead of a rebuild.

**Acceptance Criteria (EARS):**
- 3.1 THE SYSTEM SHALL NOT แก้ `Orders.Domain.OrderStatus`, lifecycle ของ `Order` (`AwaitingPayment → Paid/Cancelled`), หรือ verify-amount+currency invariant ของ `Order.MarkPaid` เพื่อ รองรับ insurance pivot นี้ — REQ-6 เพิ่ม child entity `OrderLine` เข้า `Order` ได้ แต่ห้ามแก้ state machine/`MarkPaid` semantics เดิม.
- 3.2 THE SYSTEM SHALL NOT แก้ `Payments.Domain.Session`/`SessionStatus`, PSP adapter (`IPspAdapter`, 2C2P/Omise), หรือ webhook-confirm pipeline — ใช้ pipeline เดิมทั้งชุดสำหรับชำระเบี้ยประกัน.
- 3.3 WHEN ลูกค้าชำระเบี้ยสำเร็จผ่าน PSP (webhook confirm), THE SYSTEM SHALL flip `Order` เป็น `Paid` ผ่าน `PaymentPaid` event เดิม (`Order.MarkPaid`, verify amount+currency ตามเดิม) โดยไม่มีขั้นตอนใดๆ เพิ่มเติมหลังจากนั้น.
- 3.4 THE SYSTEM SHALL NOT เพิ่ม state, entity, field หรือ event ใดๆ ที่แสดงถึงการออกกรมธรรม์ (เลขกรมธรรม์, policy document/PDF, issuance workflow) ต่อจาก `Order.Paid` — flow จบที่สถานะนี้เสมอ แม้ Order จะมี `OrderLine`/ข้อมูลผู้เอาประกันแล้วก็ตาม.

## REQ-4: Seed data for insurance products

**User Story:** As a developer/QA, I want sample insurance plans seeded in dev/demo environments, so
the pivoted catalog can be exercised end-to-end without manual data entry.

**Acceptance Criteria (EARS):**
- 4.1 THE SYSTEM SHALL provide seed data ของ `Product` (แผนประกันตัวอย่าง) ที่มีค่าครบทุก field ใหม่ (`SumInsured`, `CoverageDurationDays`, `Insurer`) สำหรับ dev/demo environment.
- 4.2 WHERE seed data ถูกใส่ผ่าน migration, THE SYSTEM SHALL ใช้ GUID ที่ well-formed (RFC-4122, version/variant ถูกต้อง) ตาม convention เดิมของ seed อื่นในระบบ (เช่น master-data seed).

## REQ-5: Documentation sync

**User Story:** As any agent/dev reading canon, I want the module docs to describe Product's
insurance semantics, so the canon stays truthful after this pivot.

**Acceptance Criteria (EARS):**
- 5.1 THE SYSTEM SHALL update `docs/reference/platform-modules.md` §5 (Product) ให้สะท้อน field ใหม่ (`SumInsured`/`CoverageDurationDays`/`Insurer`) และแก้ข้อความที่อธิบาย Product เป็นสินค้าขายทั่วไป generic ให้ตรงกับสถานะหลังปรับ.
- 5.2 THE SYSTEM SHALL update `.ai/shared/PROJECT_CONTEXT.md` (Purpose/Key Features) ให้สะท้อนว่า แพลตฟอร์มนี้คือระบบขายประกันภัย + รับชำระเงิน โดยคง non-goals เดิมไว้ครบทุกข้อ (ห้าม settlement/billing/onboarding/แตะบัตร/ฟังก์ชัน PSP เอง/non-redirect/reconciliation ที่เคลื่อนเงิน **และห้าม issue policy**).

## REQ-6: Order preserves purchased insurance plans as OrderLine(s)

**User Story:** As the platform, I want a paid Order to record exactly which insurance plan(s), at
what quantity and premium, it was paid for, so a completed order is never just an opaque total —
it is traceable to the specific plans purchased.

**Acceptance Criteria (EARS):**
- 6.1 THE SYSTEM SHALL introduce a new child entity `OrderLine` (`Orders.Domain`), owned by `Order`, one row per distinct product purchased: `ProductId`, `Quantity` (`int`), `UnitPrice` (`Money` — เบี้ย ต่อหน่วย ณ ตอนซื้อ), บวก **snapshot เชิงประกัน ณ ตอนซื้อ**: `SumInsured` (`Money`), `CoverageDurationDays` (`int`), `Insurer` (`string`) — copy ค่าจาก `Product` ตอนสร้าง `OrderLine` ไม่ใช่ อ่านสดผ่าน `ProductId` (locked decision — `Product` ยังไม่ versioned ตาม REQ-1, snapshot กันการแก้ไข `Product` ย้อนหลังกระทบ order ที่จ่ายไปแล้ว). `OrderLine` ยังพกข้อมูลผู้เอาประกันต่อบรรทัด (REQ-7.1).
- 6.2 THE SYSTEM SHALL allow an `Order` to hold one or more `OrderLine` (N กรมธรรม์/order — locked decision Q2).
- 6.3 WHEN สร้าง `Order`, THE SYSTEM SHALL validate ว่า sum ของ `OrderLine.UnitPrice × Quantity` ทุกแถว เท่ากับ `Order.Amount` เป๊ะ (ไม่มี tolerance/rounding hole).
- 6.4 THE SYSTEM SHALL ให้ข้อมูลที่มีอยู่แล้วบน `Carts.Domain.Items.Item` (`ProductId`/`Quantity`/ `UnitPrice`) ไหลผ่าน `StartCheckoutCommand` → `Checkouts.Domain.Session` → `Contracts.CheckoutConfirmed` → `CheckoutConfirmedConsumer` จนถึง `OrderLine` — ไม่ต้องแก้ `Carts.Domain` เอง (ข้อมูลมีอยู่แล้ว แค่ไม่ ถูกยุบเหลือ `Amount` เดียวเหมือนวันนี้). ข้อมูลผู้เอาประกันต่อ line (REQ-7.1) ไหลผ่าน pipeline เดียวกันนี้ เพิ่มเข้ามา ณ ชั้น `Checkouts`/`Contracts` (ไม่มีอยู่บน `Cart` เพราะ `Cart` เกิดก่อน checkout).
- 6.5 THE SYSTEM SHALL extend `Checkouts.Domain.Session` ให้ snapshot item lines ต่อ line (ไม่ใช่แค่ `Amount` รวม) ตั้งแต่ `Start` — สอดคล้องกับ target design เดิมที่ระบุไว้แล้วว่า Checkout ต้อง freeze commercial snapshot (`docs/reference/platform-modules.md` §7 gap เดิม).
- 6.6 THE SYSTEM SHALL extend `Contracts.CheckoutConfirmed` (v1 payload — เพิ่ม field ใหม่เท่านั้น ตาม event-versioning rule เดิม, ไม่ breaking) ให้พก line snapshot (ProductId/Quantity/UnitPrice/insurance terms/ผู้เอาประกันต่อ line) เพิ่มจาก `Amount`/`Recipient` เดิม.
- 6.7 IF checkout ที่กำลัง confirm มี 0 order line (cart ว่าง) THEN THE SYSTEM SHALL reject การ confirm นั้น — ห้ามให้เกิด `Order` ที่ไม่มี `OrderLine` เลย.

## REQ-7: Insured-person data captured per OrderLine at Checkout (PII)

**User Story:** As a producer, I want to record who each insurance plan in an order covers, so a
completed order is tied to real insured people even though this system never issues the policy
itself.

**Acceptance Criteria (EARS):**
- 7.1 THE SYSTEM SHALL capture insured-person data **per `OrderLine`** (1 คน/line, ไม่ใช่ 1 คน/checkout — locked decision): `FirstName`, `LastName`, `IdNumber` (เลขบัตรประชาชน), `DateOfBirth` — baseline field set; field ชุดสุดท้ายยืนยันตอน design (ล็อกเฉพาะว่า "ต้องเก็บต่อ line" ไม่ได้ล็อก field ทุกตัว).
- 7.2 WHEN ยืนยัน checkout, THE SYSTEM SHALL validate ต่อ line: `FirstName`/`LastName`/`IdNumber` ไม่เป็น ค่าว่าง/whitespace-only และ `DateOfBirth` ไม่เป็นวันที่ในอนาคต.
- 7.3 THE SYSTEM SHALL NOT log ค่า `FirstName`/`LastName`/`IdNumber`/`DateOfBirth` ในที่ใดๆ (request log, response log, error log, trace/span attribute) — ตาม `SECURITY_RULES.md` "ห้าม log sensitive data (PII)" (standing rule ของแพลตฟอร์ม ไม่ใช่ตัวเลือกของสเปกนี้).
- 7.4 WHEN insured-person data ปรากฏบน list/summary response (เช่น order list, order-line overview), THE SYSTEM SHALL mask `IdNumber` (รูปแบบเดียวกับ mask ของ secret ที่มีอยู่แล้วในระบบ — โชว์ 4 ตัวสุดท้าย เช่น `••••3a9f`; ค่าสั้นกว่า 4 ตัวมาสก์เต็ม); WHEN request คือ detail read (order/order-line รายตัว), THE SYSTEM SHALL คืนค่าเต็มของ `IdNumber` (locked decision — override รอบก่อนที่บอกว่า "คืนเต็มทุกที่"). Mask format นี้เป็นแค่ convention เดียวกัน ไม่ใช่การ reuse shared helper ข้ามโมดูล (`PspSecretEnvelopeFactory` ของ Payments เป็น private ต่อไฟล์ ไม่มี masking utility กลางให้เรียกวันนี้ — implement ของตัวเองที่ชั้น Orders/Checkouts).
- 7.5 WHEN admin/producer เรียก detail read ที่คืนค่าเต็มของ insured-person data (ตาม 7.4), THE SYSTEM SHALL เขียน audit entry (actor, target, timestamp) — มิเรอร์ pattern reveal-audit ที่มีอยู่แล้วของ vault secret (`VaultRevealAudits`/`IVaultRevealAuditWriter`).
- 7.6 THE SYSTEM SHALL NOT implement encryption-at-rest mechanism ใหม่สำหรับ field เหล่านี้ตอนนี้ (ไม่มี app-layer encryption หรือ SQL Always Encrypted เพิ่ม) — floor เดิม (app-layer merchant isolation + RBAC) พอสำหรับสเปกนี้ (locked decision — ดู hardening TODO ด้านล่าง).

> **TODO (deferred — ไม่ block requirements approval):**
> - retention owner + ระยะเก็บของ insured-person data (`FirstName`/`LastName`/`IdNumber`/`DateOfBirth` บน
>   `OrderLine`) ยังไม่ตัดสิน — ตัดสินตอน design.
> - **Hardening ก่อน prod:** field-level encryption-at-rest ของ `IdNumber`/`DateOfBirth` (REQ-7.6 ปิดไว้
>   สำหรับสเปกนี้โดยตั้งใจ) — พิจารณาอีกครั้งก่อน production launch จริง.

## Non-Goals (restated — inherited from PROJECT_CONTEXT.md + team-lead scope, ห้าม implement)

- ห้ามสร้างขั้น policy issuance ใดๆ (policy-number generation, policy document/PDF, issuance workflow)
  — แม้จะมี `OrderLine` + ข้อมูลผู้เอาประกันแล้วก็ตาม การเก็บข้อมูล 2 อย่างนี้ไม่ใช่การ issue policy
- ห้ามเพิ่ม claims / renewal / endorsement / underwriting / commission / reinsurance
- ห้ามแตะ PSP orchestration engine เดิม (2C2P/Omise) นอกจาก wiring ที่จำเป็นสำหรับ REQ-2
- ห้ามแตะ iam/admin/auth (Google SSO, RBAC, session)
- ห้ามสร้าง entity ใหม่ (`Policy`, `InsurancePlan`, `Insurer` เป็น entity) — นอกเหนือจาก field ที่ล็อกไว้ใน
  REQ-1 และ `OrderLine` ที่ล็อกไว้ใน REQ-6
- ห้ามสร้าง encryption-at-rest mechanism ใหม่ (app-layer encryption / SQL Always Encrypted) สำหรับ
  insured-person PII ในสเปกนี้ (REQ-7.6) — floor เดิมพอแล้วสำหรับตอนนี้ (มี hardening TODO ก่อน prod)
- ห้าม reuse/extend masking helper ของ Payments (`PspSecretEnvelopeFactory.MaskAll`) ข้ามโมดูล — เป็น
  private ต่อไฟล์ ไม่ใช่ shared utility; masking ของ REQ-7.4 implement แยกที่ชั้น Orders/Checkouts

## Open Questions (เหลือ 1 ข้อ — TODO ที่ user สั่งให้ defer ไป design, ไม่ block approval)

1. **PII retention** — เจ้าของ (owner) และระยะเก็บ/นโยบายลบของ insured-person data
   (`FirstName`/`LastName`/`IdNumber`/`DateOfBirth` บน `OrderLine`, REQ-7.1) — ตัดสินตอน design phase.
