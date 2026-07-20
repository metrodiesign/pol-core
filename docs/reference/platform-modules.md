# บริบทและบทบาทของโมดูล — Payment Orchestration Platform

> **[เอกสารเก่า — pre-rf1 vocabulary, ณ 2026-07-12]** เขียนก่อน spec `rf1-schema-reset` (multi-schema + actor
> rename ทั้งระบบ: `Tenant`→`Merchant`, `AdminAccount`→`PlatformUser`, `ProducerAccount`→`MerchantUser`,
> `Money.MinorUnits`→`DECIMAL(19,4)`) — เนื้อหาด้านล่างอาจยังอ้างชื่อ/schema เก่า. ของจริงปัจจุบันดู
> [`ARCHITECTURE.md`](../../.ai/shared/ARCHITECTURE.md) · [`CODING_STANDARDS.md`](../../.ai/shared/CODING_STANDARDS.md) ·
> [`rf1-schema-reset/design.md`](../../.ai/specs/rf1-schema-reset/design.md) (schema/rename map เต็ม). rewrite
> เอกสารนี้ทั้งฉบับเป็นงานของ spec ปลายทางที่เกี่ยวข้อง — ไม่ใช่ rf1.

> เอกสารนี้คือ module map ระดับแพลตฟอร์ม: **บริบท (ทำไมต้องมี) + บทบาท (ทำอะไร/ไม่ทำอะไร)
> + ฟีเจอร์ละเอียด (โมเดลเป้าหมายรายข้อ เทียบ as-built) + โมเดลเป้าหมายเชิง API (normative target)**
> ของทุกโมดูล เขียนตาม **โมเดลเป้าหมาย** พร้อมระบุ **สถานะจริงในโค้ด** ต่อโมดูลและต่อฟีเจอร์
> (สถานะจริง ณ 2026-07-04 บน `develop`; target design เชิง API รับเข้า 2026-07-05 จาก external design session).
>
> ส่วน [เป้าหมายเชิง API ระดับแพลตฟอร์ม](#เป้าหมายเชิง-api-ระดับแพลตฟอร์ม-normative-target) และหัวข้อ
> "โมเดลเป้าหมายเชิง API" ในแต่ละโมดูลเป็น **normative target** — ไม่ใช่รายการ endpoint ที่ต้อง implement ทันที
> และไม่ใช่คำยืนยันว่าโค้ดปัจจุบันมีครบ; การแก้ gap ใดๆ ต้องเปิด spec ของตัวเอง (`/spec-new`)
>
> เอกสารลึกรายเรื่อง: [payment-orchestration-modules.md](payment-orchestration-modules.md)
> (Payments/PSP/flow + ภาค 8 Canonical Payment API target design),
> [entity-fields.md](entity-fields.md) (ทุก entity/field/enum), [src-structure.md](src-structure.md) (โครงโค้ด),
> [admin-module.md](admin-module.md) + [producer-module.md](producer-module.md) (auth),
> [search-filter-sort.md](search-filter-sort.md) (query convention)

---

## สารบัญ

- [วิธีอ่านเอกสารนี้](#วิธีอ่านเอกสารนี้)
- [ภาพรวมแพลตฟอร์ม](#ภาพรวมแพลตฟอร์ม)
- [เป้าหมายเชิง API ระดับแพลตฟอร์ม (normative target)](#เป้าหมายเชิง-api-ระดับแพลตฟอร์ม-normative-target)
- [1. Payment Orchestration Platform](#1-payment-orchestration-platform)
- [2. Tenant](#2-tenant)
- [3. Admin](#3-admin) — [3.1 โมดูล Admin](#31-โมดูล-admin--บัญชีผู้ใช้--google-oidc-bff) · [3.2 โมดูล Admin RBAC](#32-โมดูล-admin-rbac)
- [4. Producer](#4-producer) — [4.1 โมดูล Producer](#41-โมดูล-producer--บัญชีผู้ใช้--google-oidc-bff) · [4.2 โมดูล Producer RBAC](#42-โมดูล-producer-rbac)
- [5. Product](#5-product)
- [6. Cart](#6-cart)
- [7. Checkout](#7-checkout)
- [8. Order](#8-order)
- [9. Payment](#9-payment--external-redirect--hosted-payment-page)
- [10. Transaction / PaymentAttempt](#10-transaction--paymentattempt)
- [11. Payment Service Providers](#11-payment-service-providers)
- [12. Webhooks](#12-webhooks)
- [13. Notifications](#13-notifications)
- [14. Audit](#14-audit)
- [ตารางสรุป](#ตารางสรุป)
- [ช่องว่างเทียบเป้าหมาย (as-built gaps)](#ช่องว่างเทียบเป้าหมาย-as-built-gaps)
- [ทะเบียนตัดสินใจค้าง (ADR pending) และลำดับเปิด spec](#ทะเบียนตัดสินใจค้าง-adr-pending-และลำดับเปิด-spec)

---

## วิธีอ่านเอกสารนี้

ทุกโมดูลมี 5 ส่วนคงที่:

| ส่วน | ความหมาย |
|---|---|
| **บริบท** | โมดูลนี้คืออะไร อยู่ตรงไหนของ flow แก้ปัญหาอะไร |
| **บทบาท** | หน้าที่ที่ own + ขอบเขตที่จงใจ *ไม่ทำ* |
| **ฟีเจอร์ละเอียด** | รายการฟีเจอร์รายข้อของโมเดลเป้าหมาย + ข้อเสนอใหม่ พร้อมสถานะจริงต่อข้อ |
| **ความสัมพันธ์** | โยงกับโมดูลอื่นอย่างไร (id-reference, integration event) |
| **สถานะ** | เทียบโค้ดจริงบน `develop` (สรุประดับโมดูล) |

ค่าสถานะ (ใช้ทั้งระดับโมดูลและรายฟีเจอร์):

| สถานะ | ความหมาย |
|---|---|
| **มีแล้ว** | โค้ด + test อยู่บน `develop` ครบตามบทบาทหลัก |
| **บางส่วน** | มีแกนแล้ว แต่ยังขาด field/พฤติกรรมเทียบโมเดลเป้าหมาย (ระบุ gap ไว้ในหัวข้อ) |
| **ยังไม่มี** | เป้าหมาย (จาก target design ในเอกสารนี้ + [payment-orchestration-modules.md](payment-orchestration-modules.md) ภาค 8) ที่ยังไม่เริ่ม implement — ถ้ามี `(ข้อ N)` ดูรายละเอียดใน [ช่องว่าง](#ช่องว่างเทียบเป้าหมาย-as-built-gaps) |
| **เสนอ** | ข้อเสนอใหม่จากการวิเคราะห์ 2026-07-04 — ยังไม่อยู่ในเป้าหมาย/สเปกใด ต้องตัดสินใจเชิง product ก่อนเปิด spec |

การแก้ gap ใดๆ ต้องผ่าน spec workflow ของตัวเอง (`/spec-new`) — เอกสารนี้บันทึกเพื่อการรับรู้ ไม่ใช่ใบสั่งงาน
และฟีเจอร์ทุกข้อต้องเคารพ [non-goals](../../.ai/shared/PROJECT_CONTEXT.md) (ห้าม settlement/payout, billing,
public onboarding, แตะข้อมูลบัตร, ฟังก์ชัน PSP เอง, non-redirect, reconciliation ที่เคลื่อนเงิน)

---

## ภาพรวมแพลตฟอร์ม

**ผู้ใช้งาน 3 กลุ่ม, console 2 แอป:**

| Actor | เข้าระบบผ่าน | ทำอะไร |
|---|---|---|
| **Admin** — พนักงานภายในองค์กร | Admin Console (internal-only) | provision tenant, ตั้งค่า PSP, อนุมัติ producer, จัดการ role, monitor/audit |
| **Producer** — ตัวแทน/นายหน้าประกันภัย | Tenant Console (3 บริษัทใช้ร่วม) | เลือกสินค้า → ตะกร้า → checkout → สร้างคำสั่งซื้อ ในนามบริษัทในเครือของตน |
| **ลูกค้า** | ลิงก์หน้าสรุปคำสั่งซื้อ (ไม่มีบัญชี) | เปิดลิงก์ → กดยืนยัน → จ่ายผ่าน redirect ไปหน้า PSP เท่านั้น |

**2 ระนาบที่แยกขาดกัน:** control plane (config/กำกับดูแล — ตาราง admin/session/RBAC/provisioning ไม่อยู่ใต้ RLS, สิทธิ์ `pol_admin`) กับ data plane (รายการขาย/จ่าย + สถานะ — ทุกตารางผูก `TenantId` ใต้ SQL Server RLS floor ผ่าน `SESSION_CONTEXT('TenantId')`) — เงินจริงไม่วิ่งผ่านแพลตฟอร์มในทุกกรณี

**เส้นทางหลัก (happy path):**

```mermaid
flowchart LR
    PR[5. Product] --> CA[6. Cart] --> CO[7. Checkout]
    CO -- CheckoutConfirmed --> OR[8. Order]
    OR -- ลิงก์หน้าสรุป --> NO[13. Notifications]
    OR --> PA[9. Payment]
    PA -- redirect --> PSP[(หน้า PSP<br/>2C2P / Omise)]
    PSP -- webhook --> WH[12. Webhooks]
    WH -- PaymentPaid --> OR
```

Audit (14) บันทึกการกระทำสำคัญตลอดแนว; Tenant (2) / Admin (3) / Producer (4) / PSP (11) เป็นชั้น config + ตัวตนที่รองรับ flow นี้

**ลำดับความสำคัญของ config ช่องทางชำระเงิน** — ตัวเลข "อันดับ" ในเอกสารนี้คือลำดับความสำคัญ/ลำดับการตั้งค่า (config รากฐานต้องพร้อมก่อน) ไม่ใช่ logic ตัดกรองอัตโนมัติ:

| อันดับ | ระดับ | ตั้งอะไร | ที่เก็บ | สถานะ |
|---|---|---|---|---|
| 1 | **PSP** (§11) | การเชื่อมต่อ PSP + ช่องทางที่เปิดต่อ connection | `PspConnection.EnabledMethods` | มีแล้ว (เก็บ verbatim, ยังไม่ enforce) |
| 2 | **Tenant** (§2) | ช่องทางที่บริษัทในเครือเปิดใช้ | `Tenant.EnabledChannels` | มีแล้ว (เก็บ verbatim, ยังไม่ enforce) |
| 3 | **Producer** (§4) | ช่องทางที่ผู้ใช้งานใช้ได้ | - | ยังไม่มี (ปัจจุบัน RBAC คุมแค่ *สิทธิ์ทำรายการจ่าย* ไม่ใช่รายช่องทาง) |

ช่องทางทั้งระบบมี 3 ค่า (code string เสถียร): `"card"` / `"promptpay"` / `"installment"` — redirect-only ทุกช่องทาง (นี่คือ catalog ของค่า; adapter จริงยังรองรับไม่ครบทุกช่อง — ดู §11)

---

## เป้าหมายเชิง API ระดับแพลตฟอร์ม (normative target)

> รับเข้า 2026-07-05 จาก external design session (โหมด "Design Deep เท่านั้น") — เป็นข้อกำหนดเชิงสถาปัตยกรรม
> ของ **โมเดลเป้าหมาย** ไม่ใช่คำอธิบายโค้ดปัจจุบัน; ช่องว่างเทียบ as-built ดู
> [ช่องว่าง](#ช่องว่างเทียบเป้าหมาย-as-built-gaps) ข้อ 16-22 และจุดที่ยังต้องตัดสินดู
> [ทะเบียนตัดสินใจค้าง](#ทะเบียนตัดสินใจค้าง-adr-pending-และลำดับเปิด-spec)
> ส่วน deep design เต็มของ Payment/Attempt/Webhook/Routing อยู่ที่
> [payment-orchestration-modules.md ภาค 8](payment-orchestration-modules.md)

### หลักสถาปัตยกรรมของ API

1. **แยก business intent ออกจาก provider attempt** — canonical model แยกอย่างน้อยสองระดับ:
   `Payment` (เจตนาชำระหนึ่งรายการ ผูก Order/ยอด/สกุล/ช่องทางที่ล็อกจาก Checkout) และ
   `PaymentAttempt` (การติดต่อ PSP หนึ่งครั้ง: connection, provider reference, redirect URL, ผลของ attempt)
   — Payment หนึ่งรายการมีหลาย attempt ได้จาก retry/fallback แต่ `Succeeded` ได้ครั้งเดียว และมี
   active attempt พร้อมกันไม่เกินหนึ่ง; คำว่า **Transaction** ใน API ฝั่งผู้ใช้เป็น read model ของ
   PaymentAttempt + webhook history ไม่ใช่ money ledger
2. **Server-authoritative fields** — ฟิลด์ต่อไปนี้ห้ามรับเป็นค่าที่เชื่อถือจาก browser/producer client
   ตอนเริ่มชำระ: `tenantId`, `orderId` ที่ไม่ผ่าน capability/authorization, `amount`, `currency`,
   `paymentMethod`, `psp`/`pspConnectionId`, merchant/PSP credential, payment status —
   Payment สร้างจาก Order snapshot ฝั่ง server เท่านั้น; PSP ถูกเลือกโดย routing policy ภายใน
3. **Canonical API ไม่เปิดศัพท์ของ PSP** — ภายนอกใช้คำ canonical: `paymentMethod`
   (`card`/`promptpay`/`installment`), `paymentStatus` (7 ค่า — ดู [mapping](#canonical-status-mapping)),
   `nextAction.type` = `redirect`; `provider` แสดงได้เฉพาะ API ฝั่ง admin/operations —
   ฟิลด์เฉพาะ PSP เช่น `paymentUri`, `authorize_uri`, `charge.complete` อยู่หลัง adapter boundary เท่านั้น
4. **At-least-once เป็นค่าเริ่มต้น** — HTTP retry, webhook redelivery, outbox delivery, worker retry
   เกิดซ้ำได้ทั้งหมด; ทุก command ที่สร้าง/เปลี่ยน state ต้อง idempotent ตาม business key หรือ
   `Idempotency-Key`; consumer ต้องบันทึก processed key ใน transaction เดียวกับ state transition;
   ห้ามอ้างว่า external call เป็น exactly-once — ต้องใช้ provider idempotency key, deterministic
   reference และ inquiry/fetch-to-confirm
5. **Consistency boundary** — ใน aggregate เดียวใช้ transaction เดียว; ข้ามโมดูลใช้ integration event
   ผ่าน transactional outbox; ห้าม distributed transaction กับ PSP; การเรียก PSP ใช้ saga/state machine
   และต้องรองรับสถานะ "ไม่ทราบผลแน่ชัด"
6. **Multi-tenant isolation** — data plane ทุก record มี `TenantId`; tenant context มาจาก trusted
   authentication/session หรือ connection resolution เท่านั้น; SQL Server RLS เป็น safety floor
   ไม่ใช่ตัวแทน application authorization; admin cross-tenant query ผ่าน dedicated seam + audit เหตุผล

### API surfaces และ trust boundary

Base path `/api/v1/{area}` เป็น **convention ที่ตัดสินแล้ว + migrate as-built ครบ (2026-07-05, spec `api-route-scheme`)** — version-first global (`v1` เดียวทั้ง API), segment ที่สอง = **domain area** (plural noun) ไม่ใช่ audience; audience บังคับ per-endpoint ผ่าน `RequireAuthorization`. หมายเหตุ: ตัวอย่าง "API surface" ต่อโมดูลด้านล่างบางบรรทัดยังเขียนด้วย notation เก่าแบบ surface-first (audience นำหน้า version — เช่น `/api/admin/v1/...`, target design เดิมที่กว้างกว่า as-built) — อ่านเป็น area scheme: audience ย้ายไปบังคับต่อ endpoint, resource domain = area (เช่น `/api/admin/v1/tenants` → `/api/v1/admins/tenants`, `/api/producer/v1/products` → `/api/v1/products`)

| Surface | Base path | ผู้เรียก | Auth | ขอบเขต |
|---|---|---|---|---|
| Admin API | `/api/admin/v1` | Admin Console | OIDC BFF session + CSRF | control plane, cross-tenant ตาม tier/permission |
| Producer API | `/api/producer/v1` | Tenant Console | Producer BFF session | data ของ tenant และสิทธิ์ของ producer |
| Customer capability API | `/api/customer/v1` | Browser ลูกค้า | opaque capability token | อ่าน summary และเริ่ม redirect เฉพาะ Order เดียว |
| Integration API | `/api/integration/v1` | ระบบภายในบริษัท | OAuth2 client credentials หรือ signed API key | machine-to-machine ภายใต้ tenant เดียว |
| PSP Webhook API | `/api/webhooks/v1` | PSP | provider signature + connection key | write-only ingress จาก PSP |
| Operations API | `/api/operations/v1` | Worker/ops tooling | workload identity / privileged admin | replay, DLQ, health, diagnostics |

ห้ามใช้ authentication scheme เดียวกันข้าม surface โดยไม่ตั้งใจ — admin session ห้ามกลายเป็น
producer session และ customer capability token ห้ามเรียก endpoint ทั่วไป

> **[intake 2026-07-05 — ช่องว่างเทียบ as-built]** route ปัจจุบันทั้งระบบยังไม่มี `/api` prefix และไม่มี
> version (เช่น `/admin/tenants`, `/payment-sessions`, `/webhooks/{pspConnectionId}`) — ถือเป็น legacy
> จนกว่าจะ migrate (ดู [ช่องว่าง](#ช่องว่างเทียบเป้าหมาย-as-built-gaps) ข้อ 18); แผนย้าย/compat
> (dual-route ช่วงเปลี่ยนผ่าน, ผลกระทบ FE proxy `/admin/*` + `/producer/*`) ต้องผ่าน ADR

### Canonical API conventions

**Resource identifiers** — ใช้ opaque ID ที่ไม่สื่อ tenant หรือ provider; ID บน URL ต้องถูก authorize
ซ้ำเสมอ (ห้ามเชื่อว่าคาดเดาไม่ได้แล้วปลอดภัย); provider reference เป็นข้อมูลภายใน ไม่ใช้เป็น
primary identifier ฝั่ง public API

**Money** — มาตรฐานแพลตฟอร์ม (ตัดสิน 2026-07-05): `Money { Amount: DECIMAL(19,4), Currency: ISO4217 }`
ทุกชั้น — domain, persistence (SQL Server `DECIMAL(19,4)`), และ wire

```json
{
  "amount": {
    "amount": "18300.0000",
    "currency": "THB"
  }
}
```

- **ห้าม float/double เด็ดขาด** ในทุกชั้น (domain, DB, serialization)
- currency เป็น ISO 4217 uppercase; aggregate เดียวห้ามมีหลาย currency
- บน wire แนะนำส่ง amount เป็น JSON **string** (กัน IEEE754 double ฝั่ง client เช่น JavaScript) —
  carrier สุดท้าย (string vs number) ตัดสินใน ADR ([ทะเบียน](#ทะเบียนตัดสินใจค้าง-adr-pending-และลำดับเปิด-spec) ข้อ 16)
- as-built ปัจจุบันคือ `Money { MinorUnits: long }` เก็บ bigint — เป็น legacy จนกว่า migration
  (ดู [ช่องว่าง](#ช่องว่างเทียบเป้าหมาย-as-built-gaps) ข้อ 22)

**Time** — เก็บ UTC และส่ง RFC 3339 เช่น `2026-07-05T05:30:00Z`; ชื่อ field ลงท้าย `At`
ทั้งบน JSON และใน persistence (`CreatedAt` / `UpdatedAt` / `OccurredAt` — **ไม่ใส่** suffix `Utc`
ตาม convention ของทีม); timezone ใช้เฉพาะ presentation/policy calculation

**Idempotency** — command ประเภท create/confirm/resend/approve ต้องรองรับ `Idempotency-Key`:
scope = tenant + principal/client + operation + key; เก็บ request hash และ canonical response;
key เดิม + payload เดิม → replay response เดิม; key เดิม + payload ต่าง → `409 idempotency.key_reused`;
retention ต้องมากกว่าช่วงเวลาที่ client retry ได้

**Optimistic concurrency** — control-plane update และ aggregate ที่แก้ผ่าน UI ใช้ `ETag`/`If-Match`
หรือ version field; version ไม่ตรง → `412 concurrency.version_mismatch`; ห้าม silent last-write-wins
กับ routing, PSP connection, tenant policy หรือ role

**Error contract** — RFC 9457 Problem Details พร้อม **stable error code**:

```json
{
  "type": "https://errors.example.internal/payment/order-not-payable",
  "title": "Order is not payable",
  "status": 409,
  "code": "payment.order_not_payable",
  "traceId": "00-...",
  "details": { "orderStatus": "paid" }
}
```

- `code` เสถียรกว่า `title`; validation ใช้ `422`; resource ที่ไม่มีสิทธิ์เห็นตอบ `404` แทน `403`
  เมื่อต้องกัน enumeration; transient PSP failure ใช้ `503` + `Retry-After` เฉพาะกรณี retry ปลอดภัย
- as-built ใช้ RFC 7807 ProblemDetails ผ่าน `ProblemDetailsExceptionHandler` อยู่แล้ว —
  ส่วนที่ยังไม่มีคือ `code` catalog เสถียร (ข้อ 18)

**Query contract** — list endpoint ใช้ cursor pagination เป็นค่าเริ่มต้น; sort ต้องมี deterministic
tie-breaker ด้วย ID; filter ใช้ whitelist ห้ามส่ง expression ไป execute ตรง; export เป็น async job
เมื่อข้อมูลมาก

> **[intake 2026-07-05 — ยังไม่ตัดสิน]** cursor pagination ขัดกับ SFS convention ที่ approve แล้ว
> ([search-filter-sort.md](search-filter-sort.md) — offset `Page`/`Limit` + `PagedResult<T>`;
> team กำลัง implement) — **SFS ยังเป็นมาตรฐานบังคับของ list endpoint ปัจจุบัน** จนกว่ามี ADR;
> SFS doc เองเปิดช่อง keyset สำหรับ deep pages — เส้นทางตัดสินดู
> [ทะเบียน](#ทะเบียนตัดสินใจค้าง-adr-pending-และลำดับเปิด-spec) ข้อ 13

**Correlation** — ทุก request/event/attempt ต้องมี `traceId`, `correlationId`, `causationId`
(สำหรับ event), `idempotencyKey` เมื่อเกี่ยวข้อง, `actorType`/`actorId` ใน audit

### Cross-module event contracts

Event ข้ามโมดูลใช้ envelope เดียว (target — as-built ปัจจุบันเป็น POCO ใน `src/Contracts` ยังไม่มี envelope/version):

```json
{
  "eventId": "evt_...",
  "eventType": "payment.succeeded.v1",
  "occurredAt": "2026-07-05T05:30:00Z",
  "tenantId": "ten_...",
  "correlationId": "cor_...",
  "causationId": "cmd_...",
  "producer": "payments",
  "data": {}
}
```

| Event (target) | Producer | Consumer หลัก | Guarantee | เทียบ as-built |
|---|---|---|---|---|
| `checkout.confirmed.v1` | Checkout | Orders | at-least-once, dedupe ด้วย CheckoutId | `CheckoutConfirmed` มีแล้ว |
| `order.created.v1` | Orders | Notifications | at-least-once | `CustomerOrderNotification` มีแล้ว (คนละชื่อ/รูป) |
| `payment.succeeded.v1` | Payments | Orders, reporting | at-least-once, immutable success identity | `PaymentPaid` มีแล้ว |
| `order.paid.v1` | Orders | Notifications/reporting | at-least-once | ยังไม่มี |
| `producer.registration_submitted.v1` | Producer | Admin notice/Notifications | at-least-once | `TenantUserRegistrationSubmitted` มีแล้ว (ชื่อ wire freeze) |
| `notification.dead_lettered.v1` | Notifications | Admin/producer ops | at-least-once | ยังไม่มี |

Event schema version ห้ามเปลี่ยนความหมายย้อนหลัง; เพิ่ม field ได้เฉพาะ optional ที่ consumer เก่าปลอดภัย;
breaking schema ใช้ event ใหม่ ไม่แก้ v1 — การ rename event เดิมเป็นรูป `.v1` ต้องผ่าน ADR
(ชื่อ wire ปัจจุบันถูก freeze ตาม CODING_STANDARDS — [ทะเบียน](#ทะเบียนตัดสินใจค้าง-adr-pending-และลำดับเปิด-spec) ข้อ 15)

### State ownership matrix

| State | Owner | ผู้มีสิทธิ์เปลี่ยน | แหล่งข้อมูล |
|---|---|---|---|
| Product lifecycle | Product | authorized producer/admin | domain command |
| Cart state | Cart | producer/session policy | producer command/job |
| Checkout state | Checkout | producer/job | confirm/expiry |
| Order state | Order | Order domain | checkout/payment events |
| Payment state | Payment | Payment domain | attempt outcome |
| PaymentAttempt state | Transaction/Attempt | Payment orchestration | adapter/inquiry/webhook |
| PSP connection state | PSP Config | admin + approval | control-plane command |
| Webhook delivery state | Webhook Inbox | webhook worker | ingress/processor |
| Notification delivery state | Notifications | notification worker/provider callback | delivery result |
| Audit state | Audit | append-only writer | domain/control actions |

โมดูลอื่นห้าม update ตารางของ owner โดยตรง แม้อยู่ฐานข้อมูลเดียวกัน

### Canonical status mapping

Adapter ต้อง map provider status เข้าค่า canonical โดยไม่ให้ provider term รั่วไป domain:

| Canonical | ความหมาย |
|---|---|
| `pending` | ยังไม่เริ่มหรือพร้อมสร้าง attempt |
| `action_required` | มี redirect action ให้ลูกค้าดำเนินการ |
| `processing` | PSP รับรายการแล้วแต่ยังไม่ terminal |
| `succeeded` | PSP inquiry ยืนยันสำเร็จและยอดตรง |
| `failed` | terminal failure ที่ attempt เดิมใช้ต่อไม่ได้ |
| `expired` | หมดอายุโดย policy หรือ PSP |
| `cancelled` | ยกเลิกโดยระบบ/ผู้มีสิทธิ์ก่อนสำเร็จ |

Unknown provider status ห้าม map เป็น `failed` แบบเดา — map เป็น `processing` หรือ `unknown`
ภายใน attempt แล้ว alert เพื่อเพิ่ม adapter mapping

> **[intake 2026-07-05 — ช่องว่างเทียบ as-built]** enum ปัจจุบัน: canonical เดิม
> `PaymentStatus { Pending, Paid, Failed, Expired }` (4 ค่า), `PaymentSession` states จริง
> `Created/Redirected/Paid/Failed/Expired` (5 ค่า) — ไม่มี `action_required`/`processing`/`cancelled`
> และ `Paid` ต้อง rename เป็น `succeeded`; การ map/rename เป็นส่วนหนึ่งของ migration Phase 1 + ADR
> (ดู [ช่องว่าง](#ช่องว่างเทียบเป้าหมาย-as-built-gaps) ข้อ 19)

### Security design checklist (target)

- Admin/Producer ใช้คนละ OIDC client, cookie และ authorization scheme (as-built: มีแล้ว)
- Customer capability token เก็บ hash, single-purpose, rotate/revoke ได้
- API client secret และ PSP secret แสดง plaintext ครั้งเดียว
- return URL ใช้ server-owned allowlist ห้ามรับ arbitrary URL จาก client
- webhook endpoint key ไม่ใช่ secret แต่ต้อง opaque และ rotate ได้
- request body size limit เฉพาะ surface; rate limit แยก auth, customer payment create, webhook, admin export
- PII field มี classification และ retention owner
- log scrubbing ครอบคลุม URL query, headers, redirect URL และ provider payload
- production error ไม่คืน provider raw error/stack trace
- privileged ops (reprocess/requeue/inquire) ต้อง permission + reason + audit

### Observability และ SLO ที่ต้องออกแบบพร้อม API (target)

Metrics หลัก: payment creation success rate · PSP create latency แยก provider/method/connection ·
conversion `action_required` → `succeeded` · unknown outcome count · webhook ingress/processed latency ·
invalid signature/unknown reference rate · outbox lag, inbox lag, DLQ depth · routing fallback count +
circuit-open duration · notification delivery success/retry/DLQ · order awaiting payment age distribution

Trace ต้องเชื่อม: `Checkout -> Order -> Payment -> PaymentAttempt -> PSP call -> WebhookDelivery ->
PaymentSucceeded -> OrderPaid -> Notification` — ห้ามใส่ secret, capability token, email, phone,
redirect URL ลง span attribute

---

## 1. Payment Orchestration Platform

**บริบท** — ตัวกลางจัดการและกระจายธุรกรรมการชำระเงินของบริษัทในเครือ (vPrivilege / vCommerce / vSouvenir) แบบ **captive/internal**: ให้ทุกบริษัทรับชำระออนไลน์ผ่าน PSP ที่ถือใบอนุญาตอยู่แล้ว โดยแพลตฟอร์ม **"ใช้" PSP ไม่ใช่ "เป็น" PSP** — เงิน settle จาก PSP เข้าบัญชี merchant ของแต่ละบริษัทโดยตรง จึงอยู่นอก funds flow (ไม่เข้าข่ายใบอนุญาตประเภทที่ 3) และคง PCI **SAQ A** ด้วยโมเดล redirect-only

**บทบาท**
- orchestrate ทั้งสาย: catalog → cart → checkout → order → payment → webhook → แจ้งเตือน — จบที่รับชำระสำเร็จ (ไม่มีขั้นออกกรมธรรม์/จัดส่ง)
- normalize PSP หลายเจ้าให้เป็นสัญญาเดียว (adapter ต่อ PSP, §11)
- บังคับ cross-cutting ทั้งระบบ: multi-tenant isolation (RLS floor), webhook = source of truth, idempotency, transactional outbox, credential vault, audit append-only
- *ไม่ทำ* (non-goals ตาม [PROJECT_CONTEXT](../../.ai/shared/PROJECT_CONTEXT.md)): settlement/payout/ledger เงินจริง · billing/เก็บค่าบริการ · public onboarding · แตะข้อมูลบัตร · ฟังก์ชันของ PSP เอง · flow แบบ non-redirect · reconciliation ที่เคลื่อนเงิน

**ฟีเจอร์ละเอียด (cross-cutting ของทั้งแพลตฟอร์ม)**

| ฟีเจอร์ | รายละเอียด | สถานะ |
|---|---|---|
| Modular monolith + Mediator | 8 โมดูลใน `src/Modules/` คุยผ่าน `ICommand`/`IQuery`/`INotification` เท่านั้น — ห้ามอ้างถึงกันตรง (มี Architecture.Tests คุม) | มีแล้ว |
| Transactional outbox + Worker | integration event เขียนใน unit of work เดียวกับ state change → Worker dispatch พร้อม retry/backoff → DLQ | มีแล้ว |
| Idempotency store | claim แบบ multi-key (`IdempotencyRecord`) กันประมวลผลซ้ำทั้งขา webhook และ consumer ภายใน | มีแล้ว |
| Multi-tenant RLS floor | SQL Server RLS + `SESSION_CONTEXT('TenantId')` ทุกตาราง data plane; scoped-admin cross-tenant read บังคับผ่าน seam เดียว `IAdminQuery` | มีแล้ว |
| Money type ที่ seam | as-built: `Money { MinorUnits, Currency }` ใน SharedKernel (bigint) — มาตรฐานใหม่ (ตัดสิน 2026-07-05) คือ `Money { Amount: DECIMAL(19,4), Currency }` ทุกชั้น ห้าม float/double | บางส่วน (ข้อ 22) |
| Credential vault | envelope encryption ต่อ tenant, secret write-only + อ่านกลับ mask, ทุกการ reveal ลง audit แบบ hash-chain | มีแล้ว |
| Audit append-only | grant เฉพาะ `SELECT`+`INSERT` ระดับ DB + เขียนใน transaction เดียวกับ action | มีแล้ว |
| Error contract 2 surface | JSON = ProblemDetails · OAuth callback = 302 redirect + `?reason=` ทุก outcome | มีแล้ว |
| Rate limiting เฉพาะจุดเสี่ยง | 3 policy: admin auth · producer auth · webhook | มีแล้ว |
| Maker-checker | action อ่อนไหว (approve tenant, เปลี่ยน routing, แก้ allowlist) ต้องมีผู้อนุมัติคนที่สอง — target formalize เป็น `ChangeRequest` aggregate (maker/checker คนละ principal, TTL, request hash — ดู §3.2); ปัจจุบันทุก action เป็น single-actor + permission gate | ยังไม่มี (ข้อ 14) |
| Health check endpoint | `GET /health/live` (process-only) + `GET /health/ready` (ตรวจ producer-db + vault) — impl ใน `BuildingBlocks.Web/HealthChecks.cs` (`AddReadinessHealthChecks()` + `MapPolHealthChecks()`) wire ทั้ง Api และ Worker | มีแล้ว |
| Observability/ops | target: metrics taxonomy + alert (DLQ โต, webhook `Rejected` ผิดปกติ, outbox ค้าง) + Operations API (`GET /api/operations/v1/outbox`, `POST .../outbox/{messageId}/requeue`, `GET .../dlq` — ทุก requeue ต้อง audit) | ยังไม่มี (ข้อ 15) |
| Canonical API conventions ขาเข้า | inbound `Idempotency-Key` + idempotency record, `ETag`/`If-Match`, RFC 9457 `code` catalog, correlation/causation ids — ดู [เป้าหมายเชิง API](#เป้าหมายเชิง-api-ระดับแพลตฟอร์ม-normative-target) | ยังไม่มี (ข้อ 18) |
| API surface + version | base path `/api/v1/{area}` (version-first global, area = domain; ตัดสิน + migrate as-built ครบ 2026-07-05 ผ่าน `api-route-scheme`) | **มีแล้ว** (spec api-route-scheme) |

**โมเดลเป้าหมายเชิง API** (Platform Core)

- **Owns**: SharedKernel (`Money`, IDs, clocks, error model) · mediator contracts + pipeline behaviors +
  integration event envelope · transactional outbox/inbox + idempotency primitives · tenant execution
  context + RLS session binding · common API middleware (correlation, ProblemDetails, rate limit,
  auth boundary) · health/readiness contract + operational metrics taxonomy
- **ไม่ own**: business state ของ Product/Order/Payment, provider-specific payload, authorization policy ของแต่ละ domain
- **Invariants**: domain module ห้าม reference infrastructure ของโมดูลอื่นตรง · integration event ต้อง
  immutable + versioned · outbox record เขียนใน transaction เดียวกับ state change · worker ทำงาน
  at-least-once · log ห้ามมี secret/capability token/PAN/PII ที่ไม่จำเป็น
- **API/operations**: `GET /health/live` · `GET /health/ready` (สองตัวนี้**มีแล้ว** —
  `BuildingBlocks.Web/HealthChecks.cs`) · `GET /api/operations/v1/outbox` ·
  `POST /api/operations/v1/outbox/{messageId}/requeue` · `GET /api/operations/v1/dlq` —
  แยก authorization จาก business API, ทุก requeue ต้อง audit
- **Design decisions**: ใช้ modular monolith ต่อไปจนมีเหตุผลด้าน scale/ownership ชัดเจน — ห้ามแตก
  microservice เพียงเพราะมี module boundary; schema/event compatibility มาก่อนการแยก deploy

**ความสัมพันธ์** — โมดูลทั้งหมดอยู่ใน backend เดียว (modular monolith, Clean Architecture + CQRS) คุยกันผ่าน Mediator เท่านั้น ไม่อ้างถึงกันตรง; ข้ามโมดูลใช้ integration event ผ่าน transactional outbox + Worker: `CheckoutConfirmed`, `PaymentPaid`, `CustomerOrderNotification`, `TenantUserRegistrationSubmitted`

**สถานะ: มีแล้ว** — 8 โมดูลใน `src/Modules/` + `BuildingBlocks`/`Contracts`/`SharedKernel`, hosts `Api` + `Worker`; เงิน as-built = `Money { MinorUnits, Currency }` ใน SharedKernel (มาตรฐานใหม่ DECIMAL(19,4) — ข้อ 22)

---

## 2. Tenant

**บริบท** — ข้อมูลตั้งค่าของบริษัทในเครือเพื่อเชื่อมต่อกับแพลตฟอร์ม: ตัวตนบริษัท, ช่องทางชำระเงินที่เปิดใช้ระดับบริษัท, และเป็นแกนของ multi-tenant isolation ทั้งระบบ (**ลำดับความสำคัญ config อันดับ 2** — ตั้งได้เมื่อ PSP connection พร้อมแล้ว)

**บทบาท**
- เก็บ `Tenant`: `Code` (allowlist เฉพาะ 3 บริษัทในเครือ, lowercase), `DisplayName`, `LegalEntityId`, `Status`, `Country`, `Currency`, `EnabledChannels`, `Metadata`
- เป็นเจ้าของ `TenantId` ที่ RLS ใช้กรองทุกตาราง data plane — backend ร่วมกันแต่ข้อมูลไม่รั่วข้าม tenant
- จุดยึด provisioning: Admin สร้าง tenant + `PspConnection` + secret ลง vault **ใน transaction เดียว** (ADR-0001 — ยัง valid ตราบที่ vault เป็น DB-backed) พร้อม `ProvisioningAudit`
- *ไม่เก็บ* PSP credential เอง — อยู่ใน vault ผ่าน `PspConnection.SecretRefName` (§11)

**ฟีเจอร์ละเอียด**

| ฟีเจอร์ | รายละเอียด | สถานะ |
|---|---|---|
| Tenant entity ครบ field | `Code` (allowlist, lowercase) · `DisplayName` · `LegalEntityId` · `Status` · `Country` · `Currency` · `EnabledChannels` · `Metadata` | มีแล้ว |
| Provisioning แบบ atomic | `POST /api/v1/admins/tenants` (Super-only): tenant + `PspConnection` + secret ลง vault ใน transaction เดียว + `ProvisioningAudit` + idempotent ด้วย tenant key | มีแล้ว |
| อ่าน tenant รายตัว | `GET /api/v1/admins/tenants/{code}` | มีแล้ว |
| List tenant ฝั่ง admin | target: `GET /api/admin/v1/tenants` สำหรับหน้า console (เลือก/จัดการ) | ยังไม่มี |
| แก้ config หลัง provision | target: `PATCH /api/admin/v1/tenants/{tenantId}` + `If-Match` (optimistic concurrency) — update `DisplayName`/`EnabledChannels`/`Metadata` | ยังไม่มี |
| Tenant lifecycle | target: states `draft`/`active`/`suspended`/`deactivated` + `POST .../activate|suspend|reactivate`; suspend ต้อง revoke/disable M2M credentials + block create flows ทันที — `Status` มี field แล้วแต่ไม่มีเส้นทางใด | ยังไม่มี |
| Channel enablement enforce | `EnabledChannels` ถูกใช้ตัดสิทธิ์จริงตอนสร้าง payment; target = effective method rule `AllowedMethods = Tenant.EnabledMethods ∩ Producer.MethodEntitlements ∩ AvailableRoutingCapabilities` (§4.2) | ยังไม่มี (ข้อ 1) |
| Branding/routing/session policy | payload เป้าหมายใน [payment-orchestration-modules.md](payment-orchestration-modules.md) (branding, routing primary/fallback ต่อช่องทาง, session expiry/idempotency TTL) — เก็บได้ใน `Metadata` verbatim แต่ยังไม่มี schema/ผู้ใช้จริง; target routing เป็น versioned policy (ข้อ 13) | ยังไม่มี (routing = ข้อ 13) |
| Versioned tenant policy | target: policy snapshot มี version + `GetEffectiveTenantPolicy`; policy update ไม่มีผลย้อนหลังต่อ Order/Payment ที่สร้างแล้ว | ยังไม่มี |
| API client ต่อ tenant | target: `GET/POST /api/admin/v1/tenants/{tenantId}/api-clients`, `POST .../api-clients/{clientId}/rotate-secret`, `DELETE .../api-clients/{clientId}` — secret แสดง plaintext ครั้งเดียวตอนสร้าง/rotate; คู่กับ Integration API (`/api/integration/v1` — Order-backed payment เท่านั้น, ห้ามเปิด `POST /payment-intents` รับ amount อิสระ) (permission `apikey.manage` จองชื่อแล้ว) | ยังไม่มี (ข้อ 6) |

**โมเดลเป้าหมายเชิง API**

- **Owns**: legal/display identity · lifecycle (`draft`/`active`/`suspended`/`deactivated`) ·
  default currency/locale/timezone + branding reference · enabled payment methods ระดับ tenant ·
  session/idempotency policy ที่ config ได้ · API clients + credential metadata (M2M) ·
  versioned tenant policy snapshot — **ไม่ own**: PSP secret/merchant config, producer profile,
  Order/Payment data, routing decision ของ attempt ที่เกิดแล้ว
- **Invariants**: tenant code unique + immutable + อยู่ใน captive allowlist · tenant ที่ไม่ `active`
  ห้ามสร้าง Cart/Checkout/Order/Payment ใหม่ · currency ของ Order ต้องอยู่ใน currency policy ·
  policy update ไม่มีผลย้อนหลัง · API client secret แสดง plaintext ครั้งเดียว
- **API surface**: `GET/POST /api/admin/v1/tenants` · `GET /api/admin/v1/tenants/{tenantId}` ·
  `PATCH /api/admin/v1/tenants/{tenantId}` + `If-Match` · `POST .../activate|suspend|reactivate` ·
  `GET/POST .../api-clients` · `POST .../api-clients/{clientId}/rotate-secret` · `DELETE .../api-clients/{clientId}`
- **Commands/Queries**: `ProvisionTenant` (idempotent ด้วย tenant code) · `UpdateTenantProfile` ·
  `ChangeTenantStatus` · `CreateTenantApiClient` · `RotateTenantApiClientSecret` ·
  `GetTenant`/`ListTenants`/`GetEffectiveTenantPolicy`
- **Events**: `TenantProvisionedV1` · `TenantPolicyChangedV1` · `TenantSuspendedV1` · `TenantApiClientRotatedV1`
- **Security**: sensitive update ใช้ maker-checker (ข้อ 14)

**ความสัมพันธ์** — ทุก entity ฝั่ง data plane (Product/Cart/CheckoutSession/Order/PaymentSession/PspConnection) อ้าง `TenantId`; `AdminTenantAssignment` (§3) และ `ProducerTenantAssignment` (§4) ชี้เข้าเพื่อกำหนดขอบเขตการเข้าถึง

**สถานะ: บางส่วน**
- มีแล้ว: entity + provisioning (`POST /api/v1/admins/tenants` Super-only, `GET /api/v1/admins/tenants/{code}`)
- gap: `EnabledChannels` เก็บ verbatim ยังไม่ถูกใช้ enforce ที่ใด (จงใจ defer ใน tenant spec REQ-3.4); **"ไคลเอนต์ API" ยังไม่มี entity** — auth ฝั่ง Tenant Console ปัจจุบันคือ Google id-token Bearer ผูก audience ของ SPA (permission `apikey.manage` จองชื่อไว้ในแคตตาล็อกแต่ยังไม่ implement); ยังไม่มีเส้นทางแก้ไข/จัดการ tenant หลัง provision

---

## 3. Admin

**บริบท** — พนักงานภายในองค์กร ผู้ใช้งาน **Admin Console** (internal-only, คนละแอปกับ Tenant Console เพื่อลด blast radius): กำกับดูแลแพลตฟอร์ม — provision tenant, ตั้งค่า PSP, อนุมัติ/ปฏิเสธ producer, จัดการสิทธิ์, ตรวจ audit

### 3.1 โมดูล Admin — บัญชีผู้ใช้ + Google OIDC BFF

**บทบาท**
- `AdminAccount`: `Subject` (Google `sub`, ผูกตอน login ครั้งแรกของบัญชีที่ถูกเชิญ), `Email` (invite key), `Tier` (`Super`/`Scoped`), `Status` (`Active`/`Suspended`)
- login = **server-side OIDC BFF** (Authorization Code + PKCE, confidential client): opaque session cookie `__Host-adm_session` (DB เก็บเฉพาะ hash), rotation + reuse-detection + instant revoke, CSRF double-submit — ไม่รับ Google id-token เป็น Bearer ฝั่ง admin
- bootstrap Super คนแรกผ่าน allowlist self-provision; เชิญ Scoped ด้วย email
- `Scoped` เข้าถึงเฉพาะ tenant ตาม `AdminTenantAssignment`; cross-tenant read ทุกอย่างบังคับผ่าน seam เดียว `IAdminQuery` (`Super` = unrestricted)
- endpoints: `GET /api/v1/admins/auth/login`, `POST /api/v1/admins/auth/logout[-all]`, `GET /api/v1/admins/me`, จัดการบัญชี `POST /api/v1/admins`, `POST /api/v1/admins/{id}/suspend`, assign tenant `POST/DELETE /api/v1/admins/{id}/tenants[/{tenantId}]`

**ฟีเจอร์ละเอียด**

| ฟีเจอร์ | รายละเอียด | สถานะ |
|---|---|---|
| Google OIDC BFF login | Authorization Code + PKCE, confidential client — `GET /api/v1/admins/auth/login` | มีแล้ว |
| Opaque session cookie | `__Host-adm_session`; DB เก็บเฉพาะ SHA-256 hash ของ token | มีแล้ว |
| Session hygiene | rotation + reuse-detection + instant revoke; `POST /api/v1/admins/auth/logout` และ `logout-all` | มีแล้ว |
| CSRF | double-submit cookie | มีแล้ว |
| Auth rate limiting | policy เฉพาะเส้นทาง auth ฝั่ง admin | มีแล้ว |
| Bootstrap Super คนแรก | allowlist self-provision (config `AdminAllowlist:Subjects`) | มีแล้ว |
| เชิญ/พักบัญชี | `POST /api/v1/admins` (invite ด้วย email), `POST /api/v1/admins/{id}/suspend` | มีแล้ว |
| Reactivate บัญชีที่ถูกพัก | `POST /api/v1/admins/{id}/reactivate` (Super-only) — คืนสถานะ Active + revoke session ทั้งหมดของ target (fresh-login), idempotent; audit ทุกครั้ง (spec `admin-account-management`) | มีแล้ว |
| List/ดูบัญชี admin | `GET /api/v1/admins` (SFS, gate `user.view`) + `GET /api/v1/admins/{id}` (detail) + จัดการ session (`GET /api/v1/admins/{id}/sessions`, `DELETE .../sessions/{sessionId}` — Super-only, revoke ทั้ง rotation family) (spec `admin-account-management`) | มีแล้ว |
| Tenant assignment | `POST/DELETE /api/v1/admins/{id}/tenants[/{tenantId}]` — `Scoped` เห็นเฉพาะ tenant ที่ assign | มีแล้ว |
| Cross-tenant read seam | `IAdminQuery` seam เดียว (ฝัง `WHERE TenantId IN accessible`; `Super` unrestricted) | มีแล้ว |
| ตัวตนปัจจุบัน | `GET /api/v1/admins/me` | มีแล้ว |

**โมเดลเป้าหมายเชิง API**

- **Owns**: AdminAccount + status · OIDC BFF session (CSRF, rotation, reuse detection, revoke) ·
  tenant assignment สำหรับ scoped admin · bootstrap/recovery ที่ตรวจสอบได้ — **ไม่ own**:
  permission catalog/role composition (§3.2), tenant business data, producer approval record (§4)
- **Invariants**: admin endpoint รับเฉพาะ admin session scheme · session token เก็บเฉพาะ hash ·
  suspended admin ใช้ session เดิมไม่ได้ · scoped admin ห้ามอ่าน tenant ที่ไม่ได้ assign แม้มี
  permission · cross-tenant action ต้องมี reason
- **API surface**: `GET /api/admin/v1/auth/login|callback` · `POST /api/admin/v1/auth/logout[-all]` ·
  `GET /api/admin/v1/me` · `GET/POST /api/admin/v1/admins` · `POST .../admins/{adminId}/suspend|reactivate` ·
  `PUT .../admins/{adminId}/tenant-assignments` · `GET .../admins/{adminId}/sessions` + `DELETE .../sessions/{sessionId}`
- **Events**: `AdminInvitedV1` · `AdminSuspendedV1` · `AdminTenantAssignmentsChangedV1` · `AdminSessionsRevokedV1`
- **Error semantics**: OAuth callback ใช้ redirect result code แบบ allowlist (ห้ามสะท้อนข้อความภายใน);
  API JSON ใช้ ProblemDetails — ตรงกับ as-built 2 surface อยู่แล้ว

**ความสัมพันธ์** — ตารางทั้งหมดเป็น control plane (ไม่อยู่ใต้ RLS); การกระทำลง `AdminAccountAudit`/`AdminAuthAudit` (§14); เป็นผู้อนุมัติ producer (§4)

**สถานะ: มีแล้ว** — รายละเอียด flow เต็ม: [admin-module.md](admin-module.md)

### 3.2 โมดูล Admin RBAC

> **สถานะ rf2 (2026-07-13, spec `rf2-iam-rbac`):** permission/role catalog ฝั่ง admin (เดิม `admin.*` 16 keys / 6 groups, entities `AdminRole*`/`AdminPermission*`) ถูกยุบเข้า **catalog กลางเดียว module `Iam` schema `iam`** ร่วมกับฝั่ง merchant-user — vocabulary รวม 20 keys / 8 groups, seed 4 roles (platform: `platform_admin`/`platform_auditor`; merchant: `merchant_manager`/`merchant_staff`). Admin console เห็นเฉพาะ Platform-scope keys (13 keys / 5 groups). recovery anchor เปลี่ยน `super_admin` → `platform_admin` (ปิด/ลบไม่ได้). `RequirePermission` + boot parity guard เหลือกลไกเดียว side-aware (`Api.Iam`). ตัวเลข/ชื่อ entity ในส่วนด้านล่าง (16/6, `AdminRole*`, `super_admin`) เป็นสถานะก่อน rf2 — ดู `.ai/specs/rf2-iam-rbac/`.

**บทบาท**
- permission catalog เป็น reference data ใน DB (16 keys / 6 กลุ่ม: txn, merchant, finance, user, system, producer) — feature ใหม่ seed key ของตัวเองผ่าน migration
- `AdminRole` → `AdminRolePermission` → `AdminRoleAssignment`; สิทธิ์รวม = **union ของ role ที่ Active**
- แกน role/permission **orthogonal กับ `AdminTier`**: Tier คุม *ขอบเขต tenant*, role คุม *ความสามารถ* — ไม่มี Super bypass permission
- `RequirePermission(...)` fail-closed (403 เมื่อ scope ไม่ถูก bind, ไม่มีทาง 500) + boot parity guard (startup fail ถ้า gate ใช้ key ที่ไม่อยู่ในแคตตาล็อก); `super_admin` เป็น recovery anchor ลบ/ปิดไม่ได้
- endpoints: `GET /api/v1/admins/permissions`, `GET/POST/PUT/DELETE /api/v1/admins/roles[/{code}]`, `PUT /api/v1/admins/{id}/roles`

**ฟีเจอร์ละเอียด**

| ฟีเจอร์ | รายละเอียด | สถานะ |
|---|---|---|
| Permission catalog ใน DB | 16 keys / 6 กลุ่ม seed ผ่าน migration; feature ใหม่เพิ่ม key ของตัวเอง | มีแล้ว |
| Role CRUD + assignment | `GET/POST/PUT/DELETE /api/v1/admins/roles[/{code}]`, `PUT /api/v1/admins/{id}/roles`; code slug `^[a-z0-9_]+$` | มีแล้ว |
| สิทธิ์รวม = union ของ role Active | resolve สดต่อ request | มีแล้ว |
| Orthogonal Tier × role | Tier คุมขอบเขต tenant, role คุมความสามารถ — ไม่มี Super bypass | มีแล้ว |
| Fail-closed + boot parity guard | `RequirePermission(...)` = 403 เสมอเมื่อ bind ไม่ถูก; startup fail ถ้า gate ใช้ key นอกแคตตาล็อก | มีแล้ว |
| Recovery anchor | `super_admin` ลบ/ปิดไม่ได้; bootstrap auto-assign + migration back-fill | มีแล้ว |
| Audit การเปลี่ยน role | ทุกการเปลี่ยนบัญชี/role ลง `AdminAccountAudit` มี actor เสมอ | มีแล้ว |
| Effective-permission view | `GET /api/v1/admins/{id}/effective-permissions` (gate `user.view`) — union ของ role Active, sorted ascending; ใช้ได้กับ target ที่ suspended (spec `admin-account-management`) | มีแล้ว |
| Change request แบบ maker-checker | target: `POST/GET /api/admin/v1/change-requests`, `POST .../change-requests/{requestId}/approve\|reject` — maker กับ checker คนละ principal, checker ต้องมี permission ของ action เดียวกันหรือ permission approval เฉพาะ, approval มี TTL + ผูก request hash (payload เปลี่ยนต้องขอใหม่) | ยังไม่มี (ข้อ 14) |

**โมเดลเป้าหมายเชิง API**

- **Owns**: permission catalog · admin roles + role assignments · effective permission evaluation ·
  sensitive action approval request (maker-checker) — **ไม่ own**: authentication session,
  business command ของโมดูลปลายทาง
- **Invariants**: tier ไม่ bypass permission · permission ที่ endpoint ใช้ต้องมีใน catalog ตอน
  startup · recovery role ปิด/ลบไม่ได้ · maker/checker ต้องเป็นคนละ principal · approval มี TTL +
  ผูก request hash
- **API surface**: `GET /api/admin/v1/permissions` · `GET/POST/PUT/DELETE /api/admin/v1/roles[/{roleCode}]` ·
  `PUT /api/admin/v1/admins/{adminId}/roles` · `GET .../admins/{adminId}/effective-permissions` ·
  `POST/GET /api/admin/v1/change-requests` + `POST .../{requestId}/approve|reject`
- **Events**: `AdminRoleChangedV1` · `AdminPermissionsChangedV1` · `SensitiveChangeApprovedV1` · `SensitiveChangeRejectedV1`

**สถานะ: มีแล้ว** — ยกเว้น maker-checker (ข้อ 14). NOTE (spec `admin-account-management`): reads ของ admin
directory (`GET /api/v1/admins`, `/{id}`, `/{id}/effective-permissions`) gate ด้วย permission `user.view` เดี่ยว
(single-key filter ไม่ใช่ OR) — role ที่ให้ `user.roles` ควร grant `user.view` ด้วย เพื่อให้ operator เห็นรายชื่อ
ก่อน assign role ได้; lifecycle/session ops (reactivate, sessions list/revoke) gate ด้วย `AdminTier.Super` mirror suspend

---

## 4. Producer

**บริบท** — **ตัวแทนประกันภัย / นายหน้าประกันภัย** ผู้ใช้งาน **Tenant Console**: ผู้ทำรายการขายและรับชำระในนามบริษัทในเครือที่ตนสังกัด (**ลำดับความสำคัญ config อันดับ 3**)
หมายเหตุนิยาม: เอกสารรุ่นก่อนหน้าเรียก actor นี้ว่า "พนักงานบริษัทในเครือ" — เอกสารนี้ปรับเป็นตัวแทน/นายหน้าตามโมเดลธุรกิจจริง ซึ่งโค้ดรองรับอยู่แล้ว (`ProducerCode`, `LicenseNumber`, `PersonType` บนบัญชี)

### 4.1 โมดูล Producer — บัญชีผู้ใช้ + Google OIDC BFF

**บทบาท**
- `ProducerAccount`: `Subject` (unique), `Email`, `Status` (`PendingApproval` → `Active` / `Rejected`; `Suspended`), ข้อมูลบุคคล/ใบอนุญาต (`FirstName`, `LastName`, `PersonType`, `IdNumber`, `ProducerCode`, `LicenseNumber`, `Phone`) + รูปถ่าย
- สมัครแบบ **ticket-gated**: ticket เป็น stateless signed token (Data Protection — ไม่มีตาราง ticket) → `POST /api/v1/producers/register` (multipart + รูป) → admin อนุมัติ/ปฏิเสธ (`POST /api/v1/admins/tenant-users/{subject}/approve|reject`, gate ด้วย permission `producer.approve`/`producer.reject` ฝั่ง Admin) — tenant + role ถูกกำหนดฝั่ง server ตอนอนุมัติ ไม่มาจาก token
- login = OIDC BFF มิเรอร์ฝั่ง Admin แต่แยกขาดกัน (OAuth client + scheme `ProducerGoogle` คนละตัว): cookie `__Host-prd_session` + CSRF `prd_csrf`, rotation/reuse-detection/revoke; callback แตก 4 ทางตามสถานะบัญชี (Active → session, ยังไม่มีบัญชี → ticket ไปหน้า register ฯลฯ)
- นโยบาย auth `producer` เป็น dual-scheme: ProducerSession cookie **หรือ** tenant Bearer
- endpoints: `GET /api/v1/producers/auth/login`, `POST /api/v1/producers/register`, `GET /api/v1/producers/me`, `POST /api/v1/producers/auth/logout[-all]`

**ฟีเจอร์ละเอียด**

| ฟีเจอร์ | รายละเอียด | สถานะ |
|---|---|---|
| สมัครแบบ ticket-gated | ticket = stateless signed token (Data Protection), short-lived + single-use — ไม่มีตาราง ticket | มีแล้ว |
| ฟอร์มสมัคร + รูปถ่าย | `POST /api/v1/producers/register` (multipart): ข้อมูลบุคคล/ใบอนุญาต (`PersonType`, `IdNumber`, `ProducerCode`, `LicenseNumber`, `Phone`) + รูป | มีแล้ว |
| กันสมัครซ้ำ | guard `HasPendingAsync` (Subject OR Email) block การสมัครที่มีคำขอค้าง | มีแล้ว |
| อนุมัติ/ปฏิเสธ + เหตุผล | `POST /api/v1/admins/tenant-users/{subject}/approve\|reject` gate ด้วย `producer.approve`/`producer.reject`; tenant + role กำหนดฝั่ง server; เหตุผล persist ลง `RegistrationAudit` | มีแล้ว |
| Resubmit หลังถูกปฏิเสธ | correction ticket → แก้ข้อมูล → กลับเข้า `PendingApproval` | มีแล้ว |
| OIDC BFF login แยกขาดจาก admin | scheme `ProducerGoogle` + OAuth client คนละตัว; cookie `__Host-prd_session` + `prd_csrf`; rotation/reuse-detection/instant revoke; logout/logout-all; auth rate limiting | มีแล้ว |
| Callback แตกตามสถานะบัญชี | 4 ทาง: Active → session · ไม่มีบัญชี → ticket ไปหน้า register · Pending/Rejected/Suspended → 302 redirect + `?reason=` (ทุก outcome เป็น redirect เดียวกันหมด) | มีแล้ว |
| Dual-scheme policy | นโยบาย `producer` = ProducerSession cookie หรือ tenant Bearer | มีแล้ว |
| ตัวตนปัจจุบัน | `GET /api/v1/producers/me` | มีแล้ว |
| พัก/เพิกถอนบัญชี producer | target: `POST /api/admin/v1/producers/{producerId}/suspend|reactivate|deactivate` — state machine เพิ่ม `Deactivated` เป็น terminal (กลับมาใช้ใหม่ = explicit re-onboarding); `Suspended` มีใน enum + callback รองรับแล้ว แต่ไม่มีเส้นทางสั่ง | ยังไม่มี |
| List/ค้นหา producer ฝั่ง admin | target: `GET /api/admin/v1/producer-registrations[/{registrationId}]` (คิว `PendingApproval` + จัดการรายบัญชี) — ปัจจุบันมีแค่ notice ในระบบ + approve/reject ราย subject | ยังไม่มี |
| แก้ไข profile หลัง Active | target: `PATCH /api/producer/v1/me/profile` — field ที่กระทบ compliance ต้องผ่าน approval workflow เมื่อแก้หลัง Active | ยังไม่มี |

**โมเดลเป้าหมายเชิง API**

- **State machine (target)**: `New -> PendingApproval -> Active`, `PendingApproval -> Rejected -> PendingApproval`,
  `Active <-> Suspended`, `Active/Suspended -> Deactivated` (terminal — การกลับมาใช้ใหม่เป็น explicit
  re-onboarding decision)
- **Invariants**: tenant assignment มาจาก admin approval เท่านั้น · Active producer มี tenant assignment
  เดียว (target model ปัจจุบัน) · producer code/license uniqueness ต้องนิยาม scope ชัด (ต่อ tenant
  หรือทั้งองค์กร) · suspended/deactivated producer สร้าง write command ใหม่ไม่ได้
- **API surface**: `GET /api/producer/v1/auth/login|callback` · `POST /api/producer/v1/registrations`
  (+ `/{registrationId}/resubmit`) · `GET /api/producer/v1/me` + `PATCH /api/producer/v1/me/profile` ·
  `POST /api/producer/v1/auth/logout[-all]` · ฝั่ง admin: `GET /api/admin/v1/producer-registrations[/{id}]` ·
  `POST .../producer-registrations/{id}/approve|reject` · `POST .../producers/{producerId}/suspend|reactivate|deactivate`
- **Events**: `ProducerRegistrationSubmittedV1` · `ProducerApprovedV1` · `ProducerRejectedV1` ·
  `ProducerSuspendedV1` · `ProducerProfileChangedV1`

**ความสัมพันธ์** — `ProducerTenantAssignment` ผูกบัญชีกับ tenant (1 บัญชี/1 tenant); สมัครแล้ว emit `TenantUserRegistrationSubmitted` แจ้งฝั่ง Admin; auth/registration ลง `ProducerAuthAudit`/`RegistrationAudit` (§14)

**สถานะ: มีแล้ว** — รายละเอียด flow เต็ม: [producer-module.md](producer-module.md)

### 4.2 โมดูล Producer RBAC

> **สถานะ rf2 (2026-07-13, spec `rf2-iam-rbac`):** catalog ฝั่ง merchant-user (เดิม producer, `merch.*` 7 keys / 3 groups, จงใจ duplicate โครงจาก Admin) ถูกยุบเข้า **catalog กลางเดียว `iam`** ร่วมกับฝั่ง admin — merchant console เห็นเฉพาะ Merchant-scope keys (7 keys / 3 groups: `catalog`/`payment`/`roles`), seed 2 roles ฝั่ง merchant `merchant_manager` (ทุก merchant key) / `merchant_staff`; anchor `merchant_manager` ปิด/ลบไม่ได้. custom role ของ merchant ผูก `Roles.MerchantId` ไม่รั่วข้าม merchant แล้ว (ปิด wart เดิม). merchant-user gate ใช้ `RequirePermission` กลไกเดียวร่วมกับ admin (แทน `RequireProducerPermission`/`RequireMerchantUserPermission` แยกฝั่ง). ตัวเลข/ชื่อในส่วนด้านล่างเป็นสถานะก่อน rf2 — ดู `.ai/specs/rf2-iam-rbac/`.

**บทบาท**
- แคตตาล็อกแยกของฝั่ง producer (จงใจ duplicate โครงจาก Admin RBAC — คนละวงจรชีวิต ห้าม refactor รวม): 7 keys / 3 กลุ่ม — `product.create`, `product.update`, `payment.create`, `payment.redirect`, `producer.roles.view`, `producer.roles.manage`, `producer.user.roles`
- `RequireProducerPermission(...)` fail-closed + boot parity guard เช่นเดียวกับฝั่ง Admin; การ enforce ฝั่ง write เปิดผ่าน flag `Producer:EnforcePermissionsOnWrites`
- endpoints: `GET /api/v1/producers/permissions`, `GET/POST/PUT/DELETE /api/v1/producers/roles[/{code}]`, `PUT /api/v1/producers/tenant-users/{tenantUserId}/roles`

**ฟีเจอร์ละเอียด**

| ฟีเจอร์ | รายละเอียด | สถานะ |
|---|---|---|
| แคตตาล็อกแยกฝั่ง producer | 7 keys / 3 กลุ่ม — จงใจ duplicate โครงจาก Admin (คนละวงจรชีวิต ห้าม refactor รวม) | มีแล้ว |
| Role CRUD + assignment | `GET/POST/PUT/DELETE /api/v1/producers/roles[/{code}]`, `PUT /api/v1/producers/tenant-users/{tenantUserId}/roles`; role status lowercase บน wire | มีแล้ว |
| Fail-closed + boot parity guard | `RequireProducerPermission(...)` มิเรอร์ฝั่ง Admin | มีแล้ว |
| Enforce flag ฝั่ง write | `Producer:EnforcePermissionsOnWrites` เปิด/ปิดการ enforce บน write endpoint — **target ห้ามใช้ feature flag ปิด authorization ใน production**: ต้อง enforce เสมอ | มีแล้ว (target เข้มกว่า) |
| ช่องทางจ่ายต่อ producer | config อันดับ 3 — target: `PUT /api/producer/v1/producers/{producerId}/payment-method-entitlements` + สูตร `AllowedMethods = Tenant.EnabledMethods ∩ Producer.MethodEntitlements ∩ AvailableRoutingCapabilities` (ไม่มี entitlement = "ไม่จำกัดเพิ่ม" ไม่ใช่ "ห้ามหมด"; entitlement จำกัดเพิ่มจากชั้นบนเท่านั้น เปิดสิ่งที่ชั้นบนปิดไม่ได้); ปัจจุบัน RBAC คุมแค่สิทธิ์ *ทำรายการจ่าย* | ยังไม่มี |
| Effective-permission view ฝั่ง producer | target: `GET /api/producer/v1/producers/{producerId}/effective-permissions` | ยังไม่มี |

**โมเดลเป้าหมายเชิง API**

- **Owns**: producer permission catalog · tenant-scoped roles + assignment · method entitlement
  ต่อ producer (optional) — **Invariants**: role/assignment ทุก record มี TenantId · role ของ
  tenant vCommerce ใช้กับ producer tenant vSouvenir ไม่ได้ · evaluation fail-closed · enforce เสมอใน production
- **API surface**: `GET /api/producer/v1/permissions` · `GET/POST/PUT/DELETE /api/producer/v1/roles[/{roleCode}]` ·
  `PUT /api/producer/v1/producers/{producerId}/roles` · `PUT .../payment-method-entitlements` ·
  `GET .../effective-permissions`

**สถานะ: มีแล้ว** — ส่วน "ช่องทางชำระเงินที่เปิดใช้ต่อ producer" (อันดับ 3 ของ config ช่องทาง) **ยังไม่มี**: RBAC ปัจจุบันคุมสิทธิ์ *ทำรายการจ่าย* (`payment.create`/`payment.redirect`) ไม่ใช่รายช่องทาง

---

## 5. Product

**บริบท** — สินค้าที่ขายบนแพลตฟอร์ม = แผน/กรมธรรม์ประกันภัยที่ producer เลือกให้ลูกค้า; catalog แยกต่อ tenant

**บทบาท**
- `Product`: `Name`, ราคาเป็น `Money` (minor units + currency — สองคอลัมน์ scalar), `IsActive`
- ฟิลด์แผนประกันบน `Product` เอง (insurance-pivot, ไม่ต้องรอ `ProductVersion` เป้าหมายด้านล่าง): `SumInsured`
  (ทุนเอาประกัน, `Money`), `CoverageDurationDays` (ระยะเวลาคุ้มครองเป็นวัน), `Insurer` (ชื่อบริษัทประกันภัย) —
  validate ตอน `Create` (จำนวนเป็นบวก, `Insurer` ไม่ว่าง, currency ของ `SumInsured` ตรงกับ `Price`)
- เป็น source ของราคา**และเงื่อนไขประกัน**เสมอ — ทั้ง Cart (ราคา) และ Checkout (เงื่อนไขประกัน, ดู §7) ดึงจาก
  catalog ตอนทำรายการ, ไม่รับราคาหรือเงื่อนไขประกันจาก client
- endpoints: `POST /products` (tenant Bearer หรือ producer + `product.create`), `GET /products` (แบ่งหน้า/กรอง/เรียงตาม [search-filter-sort.md](search-filter-sort.md))

**ฟีเจอร์ละเอียด**

| ฟีเจอร์ | รายละเอียด | สถานะ |
|---|---|---|
| สร้างสินค้า | `POST /products` (tenant Bearer หรือ producer + `product.create`) — body รับ `SumInsured`/`CoverageDurationDays`/`Insurer` ด้วย | มีแล้ว |
| ราคาเป็น `Money` + source of truth | Cart ดึงราคาจาก catalog ตอน add — ไม่รับราคาจาก client; `Price` เป็น unmapped computed (project scalar สองคอลัมน์) | มีแล้ว |
| เงื่อนไขแผนประกันบน `Product` | `SumInsured`/`CoverageDurationDays`/`Insurer` — snapshot เข้า `OrderLine` ตอน checkout-start (server-side, ไม่รับจาก client — ดู §7/§8) | มีแล้ว (insurance-pivot) |
| List + ค้นหา/กรอง/เรียง | `GET /products` ตาม SFS convention (JSON-DSL) — implement แล้ว (`ProductSfs`) | มีแล้ว |
| Query รายตัวภายใน | `GetProductById` ผ่าน Mediator — ผู้ใช้คือ Cart/Checkout ตอน add item / เริ่ม checkout (ไม่มี public endpoint) | มีแล้ว |
| แก้ไข/ปิดสินค้า | target: `POST /api/producer/v1/products/{productId}/activate|deactivate` — `IsActive` มี field และ permission `product.update` จองแล้ว แต่ไม่มี endpoint | ยังไม่มี (ข้อ 11) |
| อ่านรายตัว public | target: `GET /api/producer/v1/products/{productId}` สำหรับหน้า detail ฝั่ง console | ยังไม่มี |
| Product versioning + quote | target formalize แล้ว: `Product` (identity/สถานะ) + `ProductVersion` (immutable version ของชื่อ/coverage/premium/currency/effective period — publish แล้วแก้ย้อนหลังไม่ได้ ต้องออก version ใหม่; version ที่ inactive/expired เพิ่มลง cart ใหม่ไม่ได้) + `ProductQuote` (optional เมื่อราคาต้องคำนวณจากข้อมูลผู้เอาประกัน — มี expiry + input hash) — target เดิมวางแผนครอบ field เฉพาะประกันภัย (แผนความคุ้มครอง, ทุนเอาประกัน ฯลฯ) ผ่าน `ProductVersion`; insurance-pivot ใส่ field ชุด baseline (`SumInsured`/`CoverageDurationDays`/`Insurer`) ตรงบน `Product` ไปก่อนแล้ว (ไม่มี versioning/immutability — แก้ `Product` เปลี่ยนเงื่อนไขได้ทันที, ไม่กระทบ order ที่จ่ายแล้วเพราะ snapshot เข้า `OrderLine` แล้ว) — `ProductVersion`/`ProductQuote` เองยังไม่มี | ยังไม่มี (ProductVersion/ProductQuote) |

**โมเดลเป้าหมายเชิง API**

- **Owns**: Product identity + lifecycle · sellable `ProductVersion` · premium/price rule หรือ quoted
  premium result · product metadata ที่จำเป็นต่อการขาย — **ไม่ own**: Cart quantity, Order snapshot,
  discount approval, payment status
- **Invariants**: client ห้ามส่งราคาเป็น source of truth · published ProductVersion แก้ย้อนหลังไม่ได้ ·
  inactive/expired version เพิ่มลง cart ใหม่ไม่ได้ · quote มี expiry + input hash · product currency
  สอดคล้อง tenant policy
- **API surface**: `GET /api/producer/v1/products[/{productId}]` · `POST /api/producer/v1/products` ·
  `POST .../products/{productId}/versions` · `POST .../products/{productId}/activate|deactivate` ·
  `POST /api/producer/v1/product-quotes`
- **Events**: `ProductCreatedV1` · `ProductVersionPublishedV1` · `ProductDeactivatedV1` · `ProductQuoteCreatedV1`

**ความสัมพันธ์** — `CartItem` อ้าง `ProductId`; ราคาถูก snapshot เข้า cart ตอนหยิบ

**สถานะ: มีแล้ว** — ไม่ใช่ generic catalog item อีกต่อไป (insurance-pivot): มีฟิลด์เฉพาะแผนประกัน
(`SumInsured`/`CoverageDurationDays`/`Insurer`) ตรงบน `Product` แล้ว; target ยกระดับเป็น
Product/ProductVersion/ProductQuote (มี field เพิ่ม แต่ยังไม่มี versioning/immutability — ยังไม่เริ่ม);
ยังไม่มีเส้นทางแก้ไข/ปิดสินค้า

---

## 6. Cart

**บริบท** — ตะกร้ารวมรายการสินค้าที่ producer เลือกให้ลูกค้า ก่อนเข้าสู่ checkout

**บทบาท**
- `Cart` + `CartItem`: add แล้ว merge รายการสินค้าเดิมที่ราคาเท่ากัน, แก้จำนวน/ลบ/ล้างได้ระหว่าง `Open`
- `Subtotal` คำนวณฝั่ง domain, บังคับสกุลเงินเดียวทั้งตะกร้า
- `MarkCheckedOut` freeze ตะกร้า (`Open` → `CheckedOut`)
- endpoints: `POST /carts`, `POST /carts/{id}/items`, `GET /carts/{id}`, `PUT/DELETE /carts/{id}/items/{productId}`, `POST /carts/{id}/clear`

**ฟีเจอร์ละเอียด**

| ฟีเจอร์ | รายละเอียด | สถานะ |
|---|---|---|
| สร้าง/อ่านตะกร้า | `POST /carts`, `GET /carts/{id}` | มีแล้ว |
| เพิ่มรายการ + merge | `POST /carts/{id}/items` — รายการสินค้าเดิมที่ราคาเท่ากันถูกรวมจำนวน | มีแล้ว |
| แก้จำนวน / ลบ / ล้าง | `PUT/DELETE /carts/{id}/items/{productId}`, `POST /carts/{id}/clear` — ทำได้เฉพาะสถานะ `Open` | มีแล้ว |
| Subtotal ฝั่ง domain | คำนวณใน domain + บังคับสกุลเงินเดียวทั้งตะกร้า | มีแล้ว |
| Freeze ตอน checkout | `MarkCheckedOut` (`Open` → `CheckedOut`) — แก้ไขต่อไม่ได้ | มีแล้ว |
| เก็บกวาดตะกร้าค้าง | target: state machine `Open -> CheckedOut / Expired / Abandoned` + cart expiry (`POST /api/producer/v1/carts/{cartId}/abandon` + job) | ยังไม่มี |
| นโยบายราคาเปลี่ยนระหว่างทาง | target ตัดสินแล้ว: server ต้อง revalidate product/quote ตอน checkout (invariant ของ Checkout) — ตอนนี้ยึด snapshot ตอน add เสมอ | ยังไม่มี |
| If-Match บน write endpoint | target: cart write ทุกเส้นใช้ `If-Match` (กันหลาย tab เขียนทับกัน) | ยังไม่มี (ข้อ 18) |

**โมเดลเป้าหมายเชิง API**

- **Owns**: Cart + CartItem ที่แก้ไขได้ · snapshot อ้างอิง ProductVersion/Quote ขณะเพิ่ม · subtotal
  ชั่วคราว · cart expiry + concurrency version — **ไม่ own**: final customer data, locked payment
  method, final Order amount, PSP interaction
- **State machine (target)**: `Open -> CheckedOut / Expired / Abandoned`
- **Invariants**: แก้ได้เฉพาะ `Open` · ทุก item currency เดียวกัน · duplication ใช้ deterministic
  merge rule · server revalidate product/quote ตอน checkout · checkout แล้วกลับมา Open ไม่ได้
- **API surface**: `POST /api/producer/v1/carts` · `GET .../carts/{cartId}` · `POST .../carts/{cartId}/items` ·
  `PUT/DELETE .../carts/{cartId}/items/{itemId}` · `POST .../carts/{cartId}/clear` ·
  `POST .../carts/{cartId}/abandon` — write ทุกเส้นใช้ `If-Match`
- **Events**: cart ไม่จำเป็นต้อง emit ทุกการแก้ไข — event ที่มี business meaning คือ
  `CartCheckedOutV1` หรือให้ Checkout เป็นผู้ emit

**ความสัมพันธ์** — ราคา unit ดึงจาก Products ตอน add (กัน client กำหนดราคาเอง); `CheckoutSession` อ้าง `CartId` และล็อกยอดจาก `Subtotal`

**สถานะ: มีแล้ว**

---

## 7. Checkout

**บริบท** — ขั้นกำหนดข้อมูลประกอบรายการ **ก่อนยืนยันคำสั่งซื้อ** โมเดลเป้าหมายครอบคลุม: ผู้ทำรายการ (Producer), ข้อมูลลูกค้า, รายการสินค้า, ช่องทางการชำระเงิน (ล็อก 1 ช่องต่อคำสั่งซื้อ), การแจ้งเตือนลูกค้า (อีเมล/SMS), การแจ้งเตือนผู้รับที่กำหนดเอง (อีเมล/SMS), และหมายเหตุ

**บทบาท**
- `CheckoutSession`: ล็อกยอดจาก `Cart.Subtotal` ฝั่ง server เสมอ (client ส่งยอดเองไม่ได้), สถานะ `Started` → `Confirmed` / `Abandoned`
- `Confirm()` emit `CheckoutConfirmed` ผ่าน outbox ใน unit of work เดียวกัน → Orders เปิดคำสั่งซื้อ (Checkout ไม่สร้าง Order เอง และไม่แตะ PSP)
- endpoints: `POST /checkout`, `POST /checkout/{id}/confirm`

**ฟีเจอร์ละเอียด**

| ฟีเจอร์ | รายละเอียด | สถานะ |
|---|---|---|
| Session + ล็อกยอด server-side | ยอดมาจาก `Cart.Subtotal` เสมอ — client ส่งยอดเองไม่ได้ (`POST /checkout`) | มีแล้ว |
| Confirm → event | `POST /checkout/{id}/confirm` → `CheckoutConfirmed` ผ่าน outbox ใน unit of work เดียว → Orders เปิดใบ | มีแล้ว |
| ผู้รับแจ้งเตือน 1 ค่า | `NotificationRecipient` ต่อ session | มีแล้ว |
| ผูกผู้ทำรายการ | ระบุ producer บนรายการ — target: producer มาจาก authenticated principal เสมอ (อยู่ใน `CheckoutConfirmedV1` payload) | ยังไม่มี (ข้อ 3) |
| ข้อมูลลูกค้า | ชื่อ/ช่องทางติดต่อผู้ซื้อ — target: customer contact snapshot ผ่าน schema/consent policy | ยังไม่มี (ข้อ 3) |
| ผู้รับแจ้งเตือนหลายรายการ แยกประเภท | ลูกค้า + ผู้รับที่กำหนดเอง, ระบุชนิดต่อรายการ (อีเมล/SMS) + consent flags | ยังไม่มี (ข้อ 3) |
| ล็อกช่องทางจ่ายตั้งแต่ checkout | target: `paymentMethod` เลือกและล็อกที่ Checkout — ต้องอยู่ใน effective allowed methods; ลูกค้าเปลี่ยนเองไม่ได้ (ปัจจุบันช่องทางถูกเลือกตอนสร้าง payment session §9 แทน) | ยังไม่มี (ข้อ 3) |
| หมายเหตุ | note ประกอบรายการ | ยังไม่มี (ข้อ 3) |
| ส่วนลด | target ระบุ "note/discount decision reference" บน CheckoutSession — scope ส่วนลด (ชนิด/ผู้อนุมัติ) ยังต้องตัดสิน | ยังไม่มี (เป้าหมายเดิม) |
| Abandon | target: state machine `Started -> Confirmed / Abandoned / Expired` + `POST .../checkouts/{checkoutId}/abandon` — `Abandon()` มีใน domain แต่ไม่มีผู้เรียก | ยังไม่มี (ข้อ 12) |
| Confirm idempotent + freeze snapshot | target: confirm ทำได้ครั้งเดียวและ idempotent; ต้อง freeze commercial snapshot ที่ Orders ใช้ได้โดยไม่ query Product/Cart อีก | ยังไม่มี |

**โมเดลเป้าหมายเชิง API**

- **Owns**: CheckoutSession · customer contact snapshot · selected + locked payment method ·
  notification recipients + consent flags · note/discount decision reference · validation result
  ก่อน confirm — **ไม่ own**: Order number/lifecycle, PSP selection, payment attempt,
  notification delivery result
- **State machine (target)**: `Started -> Confirmed / Abandoned / Expired`
- **Invariants**: amount จาก server-side cart revalidation · producer จาก authenticated principal ·
  customer data ผ่าน schema/consent policy · payment method อยู่ใน effective allowed methods ·
  confirm ครั้งเดียว + idempotent · confirm ต้อง freeze commercial snapshot
- **API surface**: `POST /api/producer/v1/checkouts` · `GET/PATCH .../checkouts/{checkoutId}` ·
  `POST .../checkouts/{checkoutId}/confirm|abandon` — create request รับเฉพาะ `cartId`,
  customer/recipient data, `paymentMethod`, note, discount reference (ไม่รับ amount/currency/tenantId)
- **Emits**: `CheckoutConfirmedV1` ต้องมีครบ: checkoutId, tenantId, producerId, customer snapshot,
  immutable order lines snapshot, total Money, locked payment method, notification recipients,
  source/correlation metadata

**ความสัมพันธ์** — อ้าง `CartId`; ปลายทางเดียวที่ทำให้เกิด Order (§8)

**สถานะ: บางส่วน** — แกน session + ยอด + `NotificationRecipient` (ผู้รับแจ้งเตือน 1 ค่า) มีแล้ว; ยังไม่มีเทียบเป้าหมาย: ผู้ทำรายการ (ผูก producer), ข้อมูลลูกค้า, ผู้รับแจ้งเตือนหลายรายการ/แยกประเภท (ลูกค้า vs กำหนดเอง), การล็อกช่องทางจ่ายตั้งแต่ checkout (ปัจจุบันช่องทางถูกเลือกตอนสร้าง payment session §9), หมายเหตุ

---

## 8. Order

**บริบท** — รายการคำสั่งซื้อ: สถานะกลางที่ทั้งระบบและลูกค้าอ้างอิง ตั้งแต่รอชำระจนชำระสำเร็จ

**บทบาท**
- `Order`: ยอดเดียวทั้งใบ (`Money`), สถานะ `AwaitingPayment` → `Paid` / `Cancelled`; `MarkPaid` **ตรวจ amount + currency ซ้ำ** (ไม่เชื่อแค่ id) และ idempotent
- ออก `SummaryToken` (TTL 72 ชั่วโมง) เป็น capability link ให้ลูกค้าเปิดหน้าสรุปแบบไม่มีบัญชี (`404` ไม่รู้จัก / `410` หมดอายุ) + resend ได้ (rotate token + ส่งแจ้งเตือนใหม่)
- reconciliation = **read-only report** สรุปยอดเหนือ Orders (ไม่เคลื่อนเงิน)
- endpoints: `GET /orders/{token}/summary` (anonymous), `POST /orders/{orderId}/summary/resend`, `GET /reports/reconciliation` — ไม่มี `POST /orders`; Order เกิดจาก consumer เท่านั้น

**ฟีเจอร์ละเอียด**

| ฟีเจอร์ | รายละเอียด | สถานะ |
|---|---|---|
| Order เกิดจาก consumer เท่านั้น | `CheckoutConfirmedConsumer` idempotent ด้วย unique `CheckoutSessionId` — ไม่มี `POST /orders` | มีแล้ว |
| `MarkPaid` ตรวจซ้ำ + idempotent | verify amount + currency (ไม่เชื่อแค่ id); เรียกซ้ำปลอดภัย | มีแล้ว |
| จับคู่ด้วย `PaymentPaid.OrderId` | consumer resolve order ด้วย `OrderId` บน contract; mismatch/cancelled ล้มดังเข้า DLQ (PR #44) | มีแล้ว |
| Summary link แบบ capability | `SummaryToken` TTL 72 ชม.; `GET /orders/{token}/summary` anonymous; `404` ไม่รู้จัก / `410` หมดอายุ | มีแล้ว |
| Resend ลิงก์ | `POST /orders/{orderId}/summary/resend` — rotate token + enqueue แจ้งเตือนใหม่ | มีแล้ว |
| Reconciliation report | `GET /reports/reconciliation` — read-only สรุปยอดเหนือ Orders | มีแล้ว |
| Order lines | target: immutable `OrderLine` snapshots + customer/producer/payment-method snapshot บนใบ (total + lines แก้ไม่ได้หลังสร้าง) — ปัจจุบันเก็บยอดเดียวทั้งใบ หน้าสรุปแสดงรายละเอียดสินค้าไม่ได้ | ยังไม่มี (ข้อ 21) |
| Cancel/หมดอายุใบสั่งซื้อ | target: state machine `AwaitingPayment -> Paid / Cancelled / Expired` + `POST /api/producer/v1/orders/{orderId}/cancel` + `OrderExpiredV1` — enum ปัจจุบันไม่มี `Expired` และ `Cancel()` ไม่มีผู้เรียก | ยังไม่มี (ข้อ 12) |
| Retry & dunning | ติดตามรายการจ่ายไม่ผ่าน/ใกล้หมดอายุ — แจ้งเตือนซ้ำตามรอบ (target เดิมใน canon) | ยังไม่มี (เป้าหมายเดิม) |
| List/ค้นหา order | target: `GET /api/producer/v1/orders` (tenant ตน) + `GET /api/admin/v1/orders[/{orderId}]` (ผ่าน `IAdminQuery`) | ยังไม่มี |
| Timeline ต่อใบ | target: `GET /api/producer/v1/orders/{orderId}/timeline` (โยง unified audit ที่ defer, §14) | ยังไม่มี |
| Summary token hardening | target: เก็บเฉพาะ hash ของ token, rotate แล้ว token เก่าต้องใช้ไม่ได้ — as-built มี rotate (`ReissueSummary` แทนที่ค่าเดิม) แต่เก็บ token ตรงในคอลัมน์ ไม่ใช่ hash | บางส่วน |

**โมเดลเป้าหมายเชิง API**

- **Owns**: Order aggregate + order number · immutable OrderLine snapshots ·
  customer/producer/payment-method snapshot · total Money · order lifecycle · customer summary
  capability token lifecycle · payment status projection จาก trusted Payment event — **ไม่ own**:
  provider reference, redirect URL, PSP routing, notification delivery attempt
- **State machine (target)**: `AwaitingPayment -> Paid / Cancelled / Expired` — `Paid` เป็น terminal
  ใน scope ปัจจุบัน (refund ในอนาคต = เปิด scope/ADR ใหม่ ห้ามเพิ่มสถานะเงียบๆ)
- **Invariants**: Order สร้างจาก `CheckoutConfirmedV1` เท่านั้น (unique `CheckoutId` กันซ้ำ) ·
  total/lines/payment method แก้ไม่ได้หลังสร้าง · `MarkPaid` ตรวจ OrderId, tenantId, amount,
  currency และ payment success identity · capability token เก็บ hash + มี TTL + rotate แล้วเก่าใช้ไม่ได้
- **API surface**: `GET /api/producer/v1/orders[/{orderId}]` · `POST .../orders/{orderId}/cancel` ·
  `POST .../orders/{orderId}/summary-link/resend` · `GET /api/customer/v1/order-summaries/{token}` ·
  `GET /api/admin/v1/orders[/{orderId}]` · `GET .../orders/{orderId}/timeline`
- **Events**: `OrderCreatedV1` · `OrderCancelledV1` · `OrderExpiredV1` · `OrderPaidV1` ·
  `OrderSummaryLinkRotatedV1` — รับ `PaymentSucceededV1` จาก Payments แบบ idempotent

**ความสัมพันธ์** — สร้างโดย `CheckoutConfirmedConsumer` (idempotent ด้วย unique `CheckoutSessionId`); enqueue `CustomerOrderNotification` (§13); รับ `PaymentPaid` จาก Payments เพื่อ flip เป็น `Paid`

**สถานะ: มีแล้ว** — เส้นทาง `PaymentPaid` → `Order.Paid` ปิดแล้ว (PR #44: consumer resolve ด้วย `PaymentPaid.OrderId` — ดู [ช่องว่าง](#ช่องว่างเทียบเป้าหมาย-as-built-gaps) ข้อ 2); ยังไม่มี order lines (รายการสินค้าต่อใบ)

---

## 9. Payment — External Redirect / Hosted Payment Page

**บริบท** — การชำระเงินแบบ **external redirect เท่านั้น**: ลูกค้าถูกพาไปหน้า hosted payment page ของ PSP — ไม่มี card field / iframe / QR บนโดเมนเรา จึงคง PCI SAQ A และไม่แตะข้อมูลบัตรในทุกกรณี

**บทบาท**
- `PaymentSession` ต่อความพยายามจ่าย 1 ครั้ง: `Method` (ช่องทางที่เลือก), `Psp`, ยอด, สถานะ `Created` → `Redirected` → `Paid` / `Failed` / `Expired`, `PspExternalChargeId`, `RedirectUrl`
- **แตะ PSP ครั้งแรกตอน redirect**: claim สิทธิ์ redirect ด้วย SQL `rowversion` ก่อนเรียก PSP (claims-then-charges — กัน double-charge จากการกดซ้ำ/แข่งกัน) แล้วจึงขอ `RedirectUrl` จาก adapter
- browser return จาก PSP = UX เท่านั้น — สถานะจริงรอ webhook (§12)
- endpoints: `POST /payment-sessions`, `POST /payment-sessions/{id}/redirect` (tenant Bearer หรือ producer + `payment.create`/`payment.redirect`)

> **[intake 2026-07-05 — target ใหม่]** target design แยก `PaymentSession` (fused ปัจจุบัน) เป็นสอง
> ระดับ: **`Payment`** = เจตนาชำระหนึ่งรายการต่อ Order (amount/currency/method snapshot จาก Order,
> lifecycle + expiry, active attempt coordination, final success identity) และ **`PaymentAttempt`** =
> การติดต่อ PSP หนึ่งครั้ง (ดู §10) — deep design เต็ม + migration 5 phases:
> [payment-orchestration-modules.md ภาค 8](payment-orchestration-modules.md); ช่องว่าง = ข้อ 16

**ฟีเจอร์ละเอียด**

| ฟีเจอร์ | รายละเอียด | สถานะ |
|---|---|---|
| PaymentSession ต่อ attempt | `Method`, `Psp`, ยอด (`Money`), วงจร `Created → Redirected → Paid/Failed/Expired`, `PspExternalChargeId`, `RedirectUrl` | มีแล้ว |
| Claims-then-charges | claim สิทธิ์ redirect ด้วย SQL `rowversion` ก่อนเรียก PSP — กัน double-charge จากกดซ้ำ/แข่งกัน | มีแล้ว |
| Redirect ไป hosted page | `POST /payment-sessions/{id}/redirect` ขอ `RedirectUrl` จาก adapter ตามช่องทาง/PSP ของ session | มีแล้ว |
| Return = UX เท่านั้น | browser return จาก PSP ไม่ตัดสินสถานะ — สถานะจริงรอ webhook | มีแล้ว |
| Validate order ตอนสร้าง session | target preconditions ([payment-orchestration-modules.md](payment-orchestration-modules.md) ภาค 8.5): Order `awaiting_payment` + ไม่หมดอายุ/ยกเลิก/จ่ายแล้ว + tenant `active` + ไม่มี Payment `Succeeded`/active attempt ค้าง — ปัจจุบัน handler ไม่แตะ Orders เลย (สร้าง session ให้ order ที่ไม่มีจริง/จ่ายแล้วได้) | ยังไม่มี (ข้อ 10) |
| ล็อกยอด/สกุล/method จาก Order ฝั่ง server | target: server-authoritative ทั้งหมด — client ไม่ส่ง amount/currency/method/psp (`POST .../payments` ไม่มี body ที่เชื่อได้ นอกจาก locale); ปัจจุบัน client ส่งเองทั้งหมด แนวกันปลายทางเดียวคือ `Order.MarkPaid` verify | ยังไม่มี (ข้อ 10) |
| Enforce channel enablement | ตัดสิทธิ์ `Method` จาก effective method rule (`Tenant ∩ Producer ∩ Routing` — `Supports()` มีอยู่แต่ไม่มีผู้เรียก) | ยังไม่มี (ข้อ 1) |
| Method routing primary/fallback | target: versioned routing policy (`ordered_failover`) + eligibility + decision snapshot + safe fallback rules — ปัจจุบัน client เลือก `psp` เอง | ยังไม่มี (ข้อ 13) |
| Session/Payment expiry | target: payment TTL + expiry job (inquiry active/unknown attempt ก่อน expire, PSP ยืนยัน paid → succeed) — `MarkExpired` มีใน domain แต่ไม่มีผู้เรียก | ยังไม่มี (ข้อ 12) |
| นโยบาย attempt ซ้อน | target formalize: active attempt ต่อ Payment ไม่เกิน 1 บังคับด้วย filtered unique index + reuse active attempt เดิมเมื่อยังใช้ได้ (ตอนนี้สร้างกี่ session ก็ได้) | ยังไม่มี (ข้อ 16) |
| Customer capability payment API | target: `POST /api/customer/v1/order-summaries/{token}/payments` (Idempotency-Key; ตอบ `nextAction.redirect`) + `GET /api/customer/v1/payments/{publicPaymentToken}` (polling) + return handler `GET /api/customer/v1/payment-returns/{attemptToken}` (ไม่เชื่อ query string, ห้าม open redirect) | ยังไม่มี (ข้อ 20) |
| Idempotency-Key ขาเข้า + record | target: claim → `processing`/`completed`/`failed_replayable`; key เดิม+payload เดิม = replay, payload ต่าง = `409 idempotency.key_reused`; concurrent duplicate ห้ามเรียก PSP สองครั้ง + unique `Payment(OrderId)` กันซ้ำระดับ business | ยังไม่มี (ข้อ 18) |
| 2-transaction boundary | target ยกระดับ claims-then-charges เป็น: TX A reserve attempt + commit → เรียก PSP → TX B persist ผลด้วย optimistic concurrency; timeout หลังเรียก PSP → attempt `Unknown` ห้าม fallback จน inquiry ตัดสิน | ยังไม่มี (ข้อ 16) |

**โมเดลเป้าหมายเชิง API**

- **Owns**: Payment aggregate หนึ่งรายการต่อ Order (unique `Payment(OrderId)` ใน v1) ·
  amount/currency/method snapshot จาก Order · payment lifecycle + expiry · active attempt
  coordination · final success identity · canonical next action — **ไม่ own**: commercial Order
  lines, PSP credential/config, raw webhook payload, notification delivery
- **State machine (target)**: `Pending -> ActionRequired -> Processing -> Succeeded` ·
  `Pending -> Failed` · `Pending/ActionRequired/Processing -> Expired` ·
  `Pending/ActionRequired -> Cancelled` · `ActionRequired -> Pending` (attempt จบแบบ retryable) —
  `Succeeded` terminal + precedence สูงสุด: webhook failed ที่มาหลัง success = conflicting event +
  alert ห้าม downgrade
- **Invariants**: สร้างจาก payable Order ฝั่ง server เท่านั้น · amount/currency/method เท่ากับ Order
  snapshot · client ไม่เลือก PSP · active attempt ไม่เกินหนึ่ง · success ครั้งแรกเป็น final (ซ้ำ = no-op) ·
  outcome uncertain ห้ามสร้าง fallback attempt จน inquiry/timeout policy ตัดสิน · Payment expiry
  ไม่เปลี่ยน Order เป็น Paid
- **API surface**: customer — `POST /api/customer/v1/order-summaries/{token}/payments` ·
  `GET /api/customer/v1/payments/{publicPaymentToken}` · `GET /api/customer/v1/payment-returns/{attemptToken}`;
  producer/admin — `GET /api/producer/v1/orders/{orderId}/payment` · `GET /api/admin/v1/payments[/{paymentId}]` ·
  `POST /api/admin/v1/payments/{paymentId}/expire` (exceptional ops: permission + audit,
  ผ่าน domain command ที่ตรวจ state ไม่ใช่ SQL update)
- **Events**: `PaymentCreatedV1` · `PaymentActionRequiredV1` · `PaymentProcessingV1` ·
  `PaymentSucceededV1` · `PaymentFailedV1` · `PaymentExpiredV1`

**ความสัมพันธ์** — อ้าง `OrderId`; ใช้ `PspConnection` + vault + `IPspAdapter` (§11); ถูก `MarkPaid` โดย webhook handler แล้ว emit `PaymentPaid` ให้ Orders

**สถานะ: มีแล้ว** — แกน session/claim/redirect ครบ; จุดที่ยังไม่ปิด: การ validate order + ล็อกยอดฝั่ง server ตอนสร้าง session (ข้อ 10), channel enforcement (ข้อ 1), auto-expire (ข้อ 12)

---

## 10. Transaction / PaymentAttempt

**บริบท** — "รายการชำระเงิน": target แยกเป็นสองสิ่ง — **`PaymentAttempt`** คือ write model
หลักฐานการพยายามติดต่อ PSP แต่ละครั้ง (ระบุ connection, provider reference, redirect, ผล) ส่วน
**`Transaction`** คือ query/read model ที่รวม attempt + provider status + webhook result
ให้ฝ่ายปฏิบัติการตรวจย้อนหลัง/กระทบยอด — ไม่ใช่ money ledger

**บทบาท (target)**
- **Owns**: attempt number ต่อ Payment · selected PSP connection + immutable routing decision
  snapshot · provider merchant reference + provider payment reference · create/inquiry request
  metadata (redacted) · redirect URL/token ที่มีอายุ · canonical attempt status + failure
  classification · timing/latency/retryability — **ไม่ own**: Order state, Payment final decision
  โดยลำพัง, money ledger/settlement balance
- **State machine (target)**: `Reserved -> CreatingAtProvider -> ActionRequired -> ProviderProcessing
  -> Succeeded/Failed/Expired` · `Reserved/CreatingAtProvider -> CreationFailed` ·
  `CreatingAtProvider -> Unknown` (timeout) · `Unknown -> ActionRequired/ProviderProcessing/Succeeded/Failed/Expired`
  (หลัง inquiry) — terminal precedence: `Succeeded` > `Failed`/`Expired` > `ActionRequired`/`ProviderProcessing` > `Unknown`;
  reconcile out-of-order ใช้ precedence + provider event time + fetch-to-confirm ไม่ใช้ลำดับ arrival
- **Invariants**: unique `(PaymentId, AttemptNumber)` · filtered unique active attempt ต่อ Payment ·
  merchant reference deterministic + unique ต่อ connection · routing snapshot แก้ไม่ได้หลัง reserve ·
  redirect URL ห้าม log · failure จำแนกตาม taxonomy · fallback เฉพาะ `technical_retryable`
  ที่ยืนยันแล้วและยังไม่ redirect ลูกค้า

**ฟีเจอร์ละเอียด**

| ฟีเจอร์ | รายละเอียด | สถานะ |
|---|---|---|
| PaymentAttempt write model | entity แยกจาก Payment + state machine ข้างบน (มี `Unknown` + inquiry recovery) — deep design: [payment-orchestration-modules.md ภาค 8](payment-orchestration-modules.md) | ยังไม่มี (ข้อ 16) |
| Routing decision snapshot ต่อ attempt | policy ID/version, eligible candidates, selected connection, rejection reasons, health snapshot, timestamp — อธิบายได้ว่าทำไม transaction ไป PSP นั้น | ยังไม่มี (ข้อ 13) |
| Failure taxonomy | `business_decline` / `validation_terminal` / `technical_retryable` / `technical_terminal` / `unknown` / `security_rejected` — กำหนด retry/fallback ต่อหมวด; raw provider code เก็บสำหรับ admin ops | ยังไม่มี (ข้อ 19) |
| Transaction read model/query API | target: `GET /api/producer/v1/transactions[/{transactionId}]` + `GET /api/admin/v1/transactions[/{transactionId}]` + `GET .../transactions/{transactionId}/webhooks` — query เท่านั้น ห้ามตารางเงินใหม่ (ledger = non-goal); producer ไม่เห็น raw provider payload | ยังไม่มี (ข้อ 5) |
| Manual inquiry ops | target: `POST /api/operations/v1/payment-attempts/{attemptId}/inquire` — permission เฉพาะ + `reason` + idempotent + audit; ห้าม force status โดยไม่ผ่าน PSP inquiry | ยังไม่มี |
| Export กระทบยอดฝั่งบริษัท | target: reconciliation read model + `POST /api/producer/v1/reconciliation-exports` (read-only, ไม่เคลื่อนเงิน) — ดู discrepancy types ในภาค 8.17 | ยังไม่มี |

**ความสัมพันธ์** — เป็นอนุพันธ์ของ Payments (§9) + Webhooks (§12); attempt ถูกสร้าง/เปลี่ยนสถานะโดย Payment orchestration เท่านั้น

**สถานะ: ยังไม่มี**

> **มุมมองเดิม (≤2026-07-04) — superseded 2026-07-05**: เอกสารรุ่นก่อนสรุปว่า "ไม่ต้องมี entity แยก —
> `PaymentSession` คือ record ต่อ attempt อยู่แล้ว, `IdempotencyRecord` เก็บร่องรอย event, reconciliation
> เป็น report เหนือ Orders" — ข้อสรุปนี้ถูกแทนที่ด้วย target design ที่แยก `Payment`/`PaymentAttempt`
> (รองรับ retry/fallback/`Unknown` recovery ที่รุ่น fused ทำไม่ได้) และให้ Transaction เป็น read API
> เหนือ attempt; **ข้อห้ามเดิมยังคงอยู่ทุกประการ: ห้ามสร้าง money ledger** (non-goal, §1) —
> Transaction เป็น denormalized query model เท่านั้น ห้ามมี debit/credit/balance/settlement fields

---

## 11. Payment Service Providers

**บริบท** — ข้อมูลตั้งค่าเพื่อเชื่อมต่อ PSP ภายนอก: **2C2P + Omise/Opn** และช่องทางที่เปิดใช้ต่อการเชื่อมต่อ (**ลำดับความสำคัญ config อันดับ 1** — เป็นรากฐานที่ต้องพร้อมก่อน config ระดับ Tenant/Producer)

**บทบาท**
- `PspCode` enum: `TwoCTwoP`, `Omise` (code string เสถียร `"2c2p"` / `"omise"`)
- `PspConnection` ต่อ (tenant, PSP): `EnabledMethods` (ช่องทางที่เปิดต่อ connection), `SecretRefName` (ชี้ secret ใน vault — **credential ไม่อยู่ใน DB**, envelope encryption + reveal ถูก audit), `IsEnabled`, `Metadata` (display-only)
- adapter ต่อ PSP normalize เป็นสัญญาเดียว `IPspAdapter`: `CreateRedirectChargeAsync` / `VerifyWebhook` / `ParseWebhook` / `FetchChargeAsync` — ค่าจาก PSP ภายนอกคงรูปเดิมเสมอ (เช่น Omise `authorize_uri`, event `charge.complete`)
- เป้าหมาย: ทั้ง 3 ช่องทาง (`card`/`promptpay`/`installment`) เปิดได้บนทั้ง 2 PSP แบบ redirect-only —
  **as-built รองรับจริงเฉพาะ `card`**: `OmiseAdapter` throw `NotSupportedException` สำหรับ
  `promptpay`/`installment` และ `TwoCTwoPAdapter` ส่ง `paymentChannel = ["CC"]` คงที่โดยไม่อิง
  `PaymentSession.Method` (ดู [ช่องว่าง](#ช่องว่างเทียบเป้าหมาย-as-built-gaps) ข้อ 8)

**ฟีเจอร์ละเอียด**

| ฟีเจอร์ | รายละเอียด | สถานะ |
|---|---|---|
| `PspConnection` ต่อ (tenant, PSP) | `EnabledMethods`, `SecretRefName`, `IsEnabled`, `Metadata` — สร้างพร้อม provisioning | มีแล้ว |
| Credential ใน vault | envelope encryption, write-only + อ่านกลับ mask, reveal ลง audit hash-chain | มีแล้ว |
| สัญญากลาง `IPspAdapter` | `CreateRedirectChargeAsync` / `VerifyWebhook` / `ParseWebhook` / `FetchChargeAsync` — ค่าจาก PSP คงรูปเดิม | มีแล้ว |
| 2C2P: card | hosted page (Redirect API) — ปัจจุบันส่ง `paymentChannel = ["CC"]` คงที่ | มีแล้ว |
| Omise: card | Links API → `paymentUri` (หน้า hosted ของ Opn) | มีแล้ว |
| PromptPay ทั้ง 2 PSP | 2C2P hosted page · Omise ต้องผ่าน **Payment Links+** (`transaction_url`, QR render ฝั่ง Opn) เท่านั้น — direct source+charge เป็น offline QR ขัด redirect-only/SAQ A | ยังไม่มี (ข้อ 8) |
| Installment ทั้ง 2 PSP | 2C2P hosted page (terms/banks ตาม config) · Omise source+charge → `authorize_uri` (source types ตาม `enabledSources`) | ยังไม่มี (ข้อ 8) |
| Enforce `EnabledMethods` | ใช้ตัดสิทธิ์ตอนสร้าง payment (`Supports()` ไม่มีผู้เรียก); target: enabled method ต้องรองรับจริงใน adapter capability matrix ด้วย (โยงข้อ 8) | ยังไม่มี (ข้อ 1) |
| แยก environment sandbox/production | target invariant: connection unique ตาม tenant + provider + environment + merchant account; production/sandbox credential ห้ามใช้ข้ามกัน — entity จริงยังไม่มี field `environment` | ยังไม่มี |
| จัดการ connection หลัง provision | target: `PATCH /api/admin/v1/psp-connections/{connectionId}` + `If-Match`, `POST .../enable\|disable`, `POST .../rotate-secret` (secret versioning: current + previous grace; disabled connection ห้ามถูก route ใหม่) | ยังไม่มี |
| ทดสอบการเชื่อมต่อ | target: `POST /api/admin/v1/psp-connections/{connectionId}/test` | ยังไม่มี |
| Routing policy versioned | target: policy ต่อ tenant+method — `strategy: ordered_failover`, routes มี priority + conditions (currencies, min/max amount), มี version + effective time; `PUT /api/admin/v1/tenants/{tenantId}/routing-policies/{paymentMethod}` + `POST .../simulate`; deterministic ordered failover ก่อน (เลี่ยง weighted/AI จน operational maturity พร้อม) | ยังไม่มี (ข้อ 13) |
| Adapter contract เป้าหมาย | target `IPspAdapter` ใหม่ (คนละ signature กับ as-built): `CreatePaymentAsync`/`GetPaymentAsync`/`VerifyWebhookAsync`/`ParseWebhookAsync` + `PspCapabilities` + canonical records (`CreateProviderPaymentCommand/Result`, `ProviderPaymentSnapshot` — คืน amount/reference ให้ orchestration verify ไม่ใช่ status อย่างเดียว) | ยังไม่มี (ข้อ 16) |
| Connection health / circuit state | target: health + circuit ต่อ connection เป็น input ของ eligibility (circuit open = ข้าม เว้น policy อนุญาต probe) | ยังไม่มี (ข้อ 13) |

**โมเดลเป้าหมายเชิง API**

- **Owns**: PspConnection lifecycle ต่อ tenant/environment · enabled methods + capability metadata ·
  secret references + rotation metadata · routing policy ต่อ tenant/method · connection health/circuit
  state + effective eligibility · adapter registry — **ไม่ own**: Payment/Order state, plaintext secret
  ใน application table, customer-facing choice of PSP
- **Routing inputs**: tenant, environment, method, currency, amount range, connection enabled state,
  adapter capability, producer entitlement, configured primary/fallback, health/circuit — output ต้อง
  deterministic พร้อม reason codes + policy version
- **API surface**: `GET/POST /api/admin/v1/tenants/{tenantId}/psp-connections` ·
  `GET/PATCH /api/admin/v1/psp-connections/{connectionId}` (+ `If-Match`) ·
  `POST .../enable|disable|rotate-secret|test` · `GET /api/admin/v1/tenants/{tenantId}/routing-policies` ·
  `PUT .../routing-policies/{paymentMethod}` · `POST .../routing-policies/{paymentMethod}/simulate`
- **Invariants**: attempt เก็บ routing policy version ที่ใช้ · secret reveal ต้อง least privilege + audit ·
  API อ่านกลับห้ามคืน secret field แม้เป็น null placeholder

**ความสัมพันธ์** — ถูกสร้างพร้อม provisioning tenant (§2); Payments ใช้ตอน redirect; Webhooks route ด้วย `pspConnectionId`

**สถานะ: มีแล้ว (2 PSP)** — `EnabledMethods` ยังเก็บ verbatim ไม่ถูก enforce ตอนสร้าง payment session (ดู [ช่องว่าง](#ช่องว่างเทียบเป้าหมาย-as-built-gaps) ข้อ 1)

---

## 12. Webhooks

**บริบท** — เหตุการณ์ที่ PSP ส่งกลับมายืนยันสถานะการชำระเงิน — **source of truth ของสถานะจ่าย** (ไม่เชื่อ browser redirect); PSP ส่งซ้ำได้เมื่อส่งไม่สำเร็จ ระบบต้องรับซ้ำอย่างปลอดภัย

**บทบาท**
- endpoint เดียว: `POST /webhooks/{pspConnectionId}` (anonymous + rate-limited) — route ด้วย connection id ที่ trust ได้ ไม่ parse tenant/PSP จาก URL ก่อน verify
- pipeline ใน **transaction เดียว**: verify signature (secret จาก vault; ไม่ผ่าน → 401 ไม่มี transition) → claim **multi-key idempotency** (`{psp}:{connId}:event:{id}` และ `{psp}:{connId}:charge:{id}:{status}`) → **fetch-to-confirm** สถานะจริงกับ PSP (ไม่เชื่อ body ของ webhook) → transition `PaymentSession` → enqueue `PaymentPaid` ผ่าน outbox → commit
- ระดับ verify signature ต่อ PSP (as-built): **2C2P ตรวจจริง** (verify JWT HS256 ที่ฝังใน body);
  **Omise ยังเป็น well-formedness check เท่านั้น** (HMAC deferred โดยเจตนา — comment ใน adapter ระบุ)
  โดย **fetch-to-confirm เป็น authority ของสถานะเสมอ** (event ปลอมที่ฟอร์มถูกไม่ทำให้ Paid ได้
  เพราะสถานะจริงถูก fetch จาก PSP) + webhook rate limiter จำกัดการ probe
  (ดู [ช่องว่าง](#ช่องว่างเทียบเป้าหมาย-as-built-gaps) ข้อ 9)
- ผลลัพธ์จำแนก `Processed` / `Duplicate` / `Ignored` / `Rejected`
- "ส่งซ้ำได้": ขาเข้า — PSP redeliver กี่ครั้งก็ปลอดภัยด้วย idempotency store; ขาออก — event ภายในกระจายผ่าน outbox + Worker พร้อม retry/backoff จนถึง DLQ

**ฟีเจอร์ละเอียด**

| ฟีเจอร์ | รายละเอียด | สถานะ |
|---|---|---|
| Endpoint เดียว route ด้วย connection id | `POST /webhooks/{pspConnectionId}` anonymous + rate-limited — ไม่ parse tenant/PSP จาก URL ก่อน verify; target เปลี่ยน addressing เป็น `POST /api/webhooks/v1/{endpointKey}` (opaque, random, rotate ได้ — ไม่ใช้ id ตรง) | มีแล้ว (addressing เปลี่ยนใน target — ข้อ 17) |
| Pipeline ใน transaction เดียว | verify → multi-key idempotency claim → fetch-to-confirm → transition `PaymentSession` → outbox → commit — target แยกเป็น two-stage: ingress (durable insert + ตอบเร็ว) / async processor (fetch-to-confirm + transition) | มีแล้ว (target ยกระดับ — ข้อ 17) |
| จำแนกผลลัพธ์ | `Processed` / `Duplicate` / `Ignored` / `Rejected` — target ขยาย: `accepted`/`duplicate`/`rejected`/`processed`/`ignored`/`failed_retryable`/`dead_lettered` | มีแล้ว |
| 2C2P signature verify | ตรวจ JWT HS256 ที่ฝังใน body จริง | มีแล้ว |
| Omise HMAC verify | ปัจจุบัน well-formedness check เท่านั้น (deferred โดยเจตนา) — ป้องกันจริงด้วย fetch-to-confirm + rate limiter; target ingress validation เข้มกว่า: content-type allowlist, body size limit, header limit, verify ด้วย secret version active + grace ระหว่าง rotation, clock skew/replay window | ยังไม่มี (ข้อ 9) |
| Redeliver ปลอดภัยทั้งสองขา | ขาเข้า: idempotency store · ขาออก: outbox + retry/backoff → DLQ | มีแล้ว |
| Durable webhook inbox | target: `WebhookDelivery` entity (provider event ID, dedupe keys, signature outcome, encrypted/redacted payload ref, processing state/attempts/last error, linked attempt) + reprocess (`POST /api/operations/v1/webhook-deliveries/{deliveryId}/reprocess`) — ปัจจุบันร่องรอยเดียวคือ `IdempotencyRecord` | ยังไม่มี (ข้อ 17) |
| Event log ตรวจย้อนหลัง | target: `GET /api/admin/v1/webhook-deliveries[/{deliveryId}]` เหนือ inbox | ยังไม่มี (ข้อ 17) |
| Out-of-order / conflict rules | ทุก event ต้อง fetch current provider state (ไม่ย้อน state ตาม payload เก่า): `processing` หลัง `succeeded` = no-op + record stale; `failed` หลัง `succeeded` = conflict alert ห้าม downgrade; duplicate success = no-op ไม่ emit ซ้ำ — fetch-to-confirm ปัจจุบันกันไว้ได้ส่วนหนึ่งแต่ไม่มี conflict recording/alert | บางส่วน |
| Unmatched webhook handling | target: หา attempt ไม่พบ → mark `Unmatched` + retry ช่วงสั้น (เผื่อ create commit ช้ากว่า webhook) → เกิน threshold = ops alert; ห้ามผูกด้วย amount/เวลาแบบ heuristic | ยังไม่มี (ข้อ 17) |
| Alert เมื่อ `Rejected` ผิดปกติ | target metrics: invalid signature rate, unmatched > 0 ต่อเนื่อง, processing lag (โยง observability ข้อ 15) | ยังไม่มี (ข้อ 15) |

**โมเดลเป้าหมายเชิง API**

- **Owns**: WebhookDelivery envelope · signature verification result · provider event ID/reference ·
  deduplication keys · processing status/attempts/last error · redacted payload retention —
  **ไม่ own**: final Payment/Order state, provider credential, business notification
- **Two-stage pipeline (target)**: ingress transaction (resolve connection จาก opaque endpoint key →
  จำกัด method/content-type/size/rate → verify signature → extract dedupe keys → durable insert →
  duplicate ตอบ success เดิม → `200`/`202` หลัง commit) แล้ว processor transaction (claim record →
  fetch-to-confirm → locate attempt ด้วย connection + provider reference → validate
  amount/currency/merchant reference → transition attempt/payment → outbox → mark processed)
- **Idempotency keys (target)**: หลาย key กัน provider ที่ event ID ไม่เสถียร —
  `provider-event:{connectionId}:{eventId}` · `provider-payment-state:{connectionId}:{providerPaymentId}:{state}:{providerUpdatedAt}` ·
  `payload-hash:{connectionId}:{sha256(rawBody)}` (as-built multi-key มีแนวเดียวกันแล้ว)
- **Ingress response (target)**: invalid signature → `401`/`400` ตาม provider expectation · valid
  duplicate → `200` · durable accepted → `200`/`202` · transient DB unavailable → `503` ให้ PSP
  redeliver · ห้ามตอบรายละเอียดภายใน
- **Invariants**: browser return ไม่สร้าง success event · invalid signature ไม่เปลี่ยน state ·
  duplicate ไม่ emit ซ้ำ · unknown reference เก็บเพื่อ investigation ไม่ bind แบบเดา ·
  worker crash หลัง commit ต้อง replay ได้

**ความสัมพันธ์** — `PspConnection` (routing + secret), Payments (transition), Orders (ผ่าน `PaymentPaid`); tenant ถูก resolve ผ่าน seam เฉพาะก่อนเข้า scope งาน

**สถานะ: มีแล้ว** (รุ่น one-transaction) — target ยกระดับเป็น durable inbox + async processor (ข้อ 17)

---

## 13. Notifications

**บริบท** — แจ้งเตือนเหตุการณ์สำคัญถึงผู้เกี่ยวข้อง (เคสหลัก: ส่งลิงก์หน้าสรุปคำสั่งซื้อให้ลูกค้า) และดูประวัติการส่งแจ้งเตือนย้อนหลัง

**บทบาท (เป้าหมาย)** — ส่งอีเมล/SMS ถึงลูกค้า + ผู้รับที่กำหนดเอง, เก็บประวัติ/สถานะการส่งต่อรายการ, resend ได้

**บทบาท (ที่มีจริง)**
- ส่งแบบ background เสมอ: Orders enqueue `CustomerOrderNotification` ใน unit of work เดียวกับการสร้าง order → Worker → port `INotificationSender` (ล้มเหลว → outbox retry/backoff → DLQ)
- ห้ามมี PII ใน log; resend ผูกกับ rotate `SummaryToken` (§8)

**ฟีเจอร์ละเอียด**

| ฟีเจอร์ | รายละเอียด | สถานะ |
|---|---|---|
| ส่ง background ผ่าน outbox | enqueue ใน unit of work เดียวกับสร้าง order → Worker → retry/backoff → DLQ | มีแล้ว |
| Port `INotificationSender` | สัญญากลางฝั่งส่ง — impl จริงมีตัวเดียว: `LoggingNotificationSender` (log อย่างเดียว ไม่ส่งจริง) | บางส่วน (ข้อ 4) |
| Email provider จริง | เชื่อม provider ส่งอีเมลลิงก์หน้าสรุป (defer โดย spec โดยเจตนา) | ยังไม่มี (ข้อ 4) |
| SMS provider จริง | ช่องทาง SMS ตามโมเดลเป้าหมาย | ยังไม่มี (ข้อ 4) |
| ผู้รับหลายรายการ/แยกประเภท | ลูกค้า + ผู้รับกำหนดเอง, ชนิดต่อรายการ (อีเมล/SMS) — ต้องมาพร้อม Checkout target (ข้อ 3) | ยังไม่มี (ข้อ 4) |
| ประวัติการส่ง + สถานะรายรายการ | target: delivery attempts ต่อ channel/provider + retry schedule + suppression/invalid recipient + `GET /api/producer/v1/orders/{orderId}/notifications` + `GET /api/admin/v1/notification-deliveries` + retry ops (`POST /api/operations/v1/notification-deliveries/{deliveryId}/retry` — retry เฉพาะ failure ที่ retryable) — ปัจจุบันร่องรอยเดียวคือ outbox/DLQ | ยังไม่มี (ข้อ 4) |
| แจ้งผู้ผลิตเมื่อส่งไม่สำเร็จจนเข้า DLQ | target: emit `NotificationDeadLetteredV1` → admin/producer ops | ยังไม่มี |
| Resend | ผูกกับ rotate `SummaryToken` (§8) → enqueue รอบใหม่ — target invariant: resend ต้องใช้ token ปัจจุบันเท่านั้น | มีแล้ว |
| No PII in log | กฎบังคับทั้ง pipeline แจ้งเตือน — target: mask email/phone + recipient data minimize/encrypt | มีแล้ว |
| แจ้ง admin ในระบบเมื่อมีผู้สมัครใหม่ | `TenantUserRegistrationSubmitted` → `ProducerRegistrationNotice` (in-app ฝั่ง Admin — คนละเรื่องกับอีเมล/SMS ลูกค้า) | มีแล้ว |
| Template ข้อความ | target: template/version/locale ต่อ tenant + `GET/POST/PUT /api/admin/v1/notification-templates[/{templateId}]` | ยังไม่มี |
| Provider callback idempotent | target: delivery report จาก email/SMS provider ต้อง idempotent | ยังไม่มี (ข้อ 4) |

**ความสัมพันธ์** — Orders เป็นผู้ enqueue; ฝั่งสมัคร producer มี `TenantUserRegistrationSubmitted` → `ProducerRegistrationNotice` (แจ้งเตือนในระบบถึง admin — คนละเรื่องกับอีเมล/SMS ลูกค้า)

**สถานะ: บางส่วน** — implementation จริงมีตัวเดียวคือ `LoggingNotificationSender` (log อย่างเดียว ยังไม่ส่งจริง — email/SMS provider ถูก defer โดย spec โดยเจตนา); ผู้รับ 1 ค่าต่อ order; **ยังไม่มี**: provider จริง, ผู้รับหลายรายการ/กำหนดเอง, ตารางประวัติการส่ง (ปัจจุบันร่องรอยเดียวคือ outbox/DLQ)

---

## 14. Audit

**บริบท** — บันทึกการกระทำสำคัญทั้งหมดในระบบ เพื่อการตรวจสอบย้อนหลัง — **อ่านอย่างเดียว แก้ไขหรือลบไม่ได้**

**บทบาท**
- append-only บังคับที่ระดับ DB: grant เฉพาะ `SELECT` + `INSERT` (ไม่มี `UPDATE`/`DELETE`) และเขียนใน **transaction เดียวกับ action ที่บันทึก**
- ครอบคลุมปัจจุบัน (แยกตารางต่อโดเมน):
  - `AdminAuthAudit` — วงจร auth ฝั่ง admin (login สำเร็จ/logout/rotation/ตรวจจับ reuse/ปฏิเสธ)
  - `AdminAccountAudit` — การเปลี่ยนแปลงบัญชี admin + role (มี actor เสมอ)
  - `ProducerAuthAudit` / `RegistrationAudit` — auth ฝั่ง producer + วงจรสมัคร (register/resubmit/approve/reject พร้อมเหตุผล)
  - `ProvisioningAudit` — การ provision tenant (admin subject + correlation id, ไม่มี secret)
  - `VaultRevealAudit` — ทุกการเปิดอ่าน secret จาก vault: **hash-chain tamper-evident** (ตรวจความต่อเนื่องได้)
- การกระทำ cross-tenant ของ admin ต้องแนบ reason + correlation id ลง audit เสมอ

**ฟีเจอร์ละเอียด**

| ฟีเจอร์ | รายละเอียด | สถานะ |
|---|---|---|
| Append-only ระดับ DB | grant เฉพาะ `SELECT`+`INSERT`; เขียนใน transaction เดียวกับ action | มีแล้ว |
| ตาราง audit ต่อโดเมน | `AdminAuthAudit` · `AdminAccountAudit` · `ProducerAuthAudit` · `RegistrationAudit` · `ProvisioningAudit` · `VaultRevealAudit` | มีแล้ว |
| Hash-chain vault reveal | tamper-evident — ตรวจความต่อเนื่องของ chain ได้ | มีแล้ว |
| Cross-tenant ต้องมีเหตุผล | reason + correlation id บังคับกับทุกการกระทำ cross-tenant ของ admin | มีแล้ว |
| Unified audit ของ domain data | ประวัติแก้ไข order/payment รายตัว (timeline ต่อ entity — โยง `GET .../orders/{orderId}/timeline` §8) — defer เป็น spec อนาคต | ยังไม่มี |
| มุมมองค้นหา/export สำหรับผู้ตรวจ | target: `GET /api/admin/v1/audit-events[/{auditId}]` + `POST /api/admin/v1/audit-exports` + `GET .../audit-exports/{exportId}` (export = read-only + audit การ export ด้วย) | ยังไม่มี |
| Retention/archival policy | target: retention matrix ต่อชนิดข้อมูล ([payment-orchestration-modules.md ภาค 8.18](payment-orchestration-modules.md)) — append-only ห้ามลบ, archive = ย้ายที่เก็บ, purge job ต้อง tenant-aware + auditable; ต้องผ่านฝ่ายกฎหมาย/compliance | ยังไม่มี |
| Tamper-evidence chain ครอบเหตุการณ์อ่อนไหว | target: hash-chain ครอบ secret reveal, routing change, role change, maker-checker, replay/requeue + `GET /api/operations/v1/audit-chain/verify` — ปัจจุบันมีเฉพาะ `VaultRevealAudit` | บางส่วน |
| Audit action ที่ target บังคับ | payment creation/reuse ด้วย idempotency · route decision + fallback · connection/policy change · secret rotation/reveal · manual inquiry/reprocess/requeue · exceptional cancel/expire — invalid signature spike ใช้ security log/metric aggregate ไม่ใช่ 1 row ต่อ request | ยังไม่มี |

**ความสัมพันธ์** — ทุกโมดูล control plane เขียนเข้า audit ของโดเมนตัวเอง; รายละเอียด field ครบทุกตาราง: [entity-fields.md](entity-fields.md)

**สถานะ: มีแล้ว (แบบกระจายรายโดเมน)** — unified audit trail สำหรับ domain data (เช่น ประวัติแก้ไข order/payment รายตัว) ยังไม่มี — ถูก defer ไว้เป็น spec อนาคต

---

## ตารางสรุป

| # | โมดูล | บทบาทหนึ่งบรรทัด | สถานะ | อ้างอิงลึก |
|---|---|---|---|---|
| 1 | Platform | orchestrate การรับชำระแบบ captive/redirect-only โดยไม่ถือเงิน | มีแล้ว (โครง) | [payment-orchestration-modules.md](payment-orchestration-modules.md) |
| 2 | Tenant | config บริษัทในเครือ + แกน `TenantId` ของ RLS (config อันดับ 2) | บางส่วน | [entity-fields.md](entity-fields.md) |
| 3.1 | Admin | บัญชีพนักงานภายใน + OIDC BFF session | มีแล้ว | [admin-module.md](admin-module.md) |
| 3.2 | Admin RBAC | role → permission (fail-closed) orthogonal กับ Tier | มีแล้ว | [entity-fields.md](entity-fields.md) |
| 4.1 | Producer | บัญชีตัวแทน/นายหน้า + สมัคร ticket-gated + OIDC BFF | มีแล้ว | [producer-module.md](producer-module.md) |
| 4.2 | Producer RBAC | role → permission ฝั่ง merchant-user (rf2: catalog กลาง `iam` ร่วมกับ admin) | มีแล้ว | [producer-module.md](producer-module.md) |
| 5 | Product | catalog สินค้า/กรมธรรม์ต่อ tenant, source ของราคา | มีแล้ว | [entity-fields.md](entity-fields.md) |
| 6 | Cart | ตะกร้า + subtotal สกุลเดียว | มีแล้ว | [entity-fields.md](entity-fields.md) |
| 7 | Checkout | กำหนดข้อมูล + ล็อกยอด ก่อนยืนยันคำสั่งซื้อ | บางส่วน | [entity-fields.md](entity-fields.md) |
| 8 | Order | คำสั่งซื้อ + summary link + reconciliation report | มีแล้ว (มี gap link) | [entity-fields.md](entity-fields.md) |
| 9 | Payment | PaymentSession + redirect ไป hosted page ของ PSP — target แยก Payment/PaymentAttempt | มีแล้ว (target ยกระดับ) | [payment-orchestration-modules.md](payment-orchestration-modules.md) ภาค 8 |
| 10 | Transaction / PaymentAttempt | target: PaymentAttempt write model + Transaction read API | ยังไม่มี | [payment-orchestration-modules.md](payment-orchestration-modules.md) ภาค 8 |
| 11 | PSP | connection 2C2P/Omise + secret ใน vault (config อันดับ 1) | มีแล้ว | [payment-orchestration-modules.md](payment-orchestration-modules.md) |
| 12 | Webhooks | source of truth ของสถานะจ่าย, idempotent + fetch-to-confirm | มีแล้ว | [payment-orchestration-modules.md](payment-orchestration-modules.md) |
| 13 | Notifications | แจ้งเตือน background ผ่าน outbox + Worker | บางส่วน (stub) | [entity-fields.md](entity-fields.md) |
| 14 | Audit | append-only ทุกการกระทำสำคัญ แก้/ลบไม่ได้ | มีแล้ว (กระจาย) | [entity-fields.md](entity-fields.md) |
| 15 | Divisions / Levels / Offices / Positions | reference data โปรไฟล์พนักงาน — เดิมรวมเป็นโมดูล MasterData เดียว (แยกจาก Admin 2026-07-13) แล้วแตกเป็น 4 โมดูลอิสระ ลบ MasterData ทิ้ง (masterdata-split, 2026-07-19); schema `cfg` เดิม, route/permission เดิม | มีแล้ว | [ARCHITECTURE.md](../../.ai/shared/ARCHITECTURE.md) |

---

## ช่องว่างเทียบเป้าหมาย (as-built gaps)

> รวมจุดที่โมเดลเป้าหมายกับโค้ดจริงยังไม่ตรงกัน — บันทึกเพื่อการรับรู้; การแก้แต่ละข้อต้องเปิด spec ของตัวเอง

1. **Channel enablement ยังไม่ enforce** — `Tenant.EnabledChannels` และ `PspConnection.EnabledMethods` ถูกเก็บ verbatim (จงใจ defer ตอน provisioning spec) แต่ตอนสร้าง payment session ไม่มีการ validate `Method` กับค่าใดเลย (`PspConnection.Supports()` ไม่มีผู้เรียก) — เปิดช่องสร้าง session ด้วยช่องทางที่ไม่ได้เปิดใช้; ระดับ producer (อันดับ 3) ยังไม่มีแนวคิดในโค้ด
2. **Order ↔ PaymentSession link housekeeping ค้าง** — resolve ผ่าน `PaymentPaid.OrderId` แล้ว (spec `bugfix-order-paid-link`; mismatch/cancelled ล้มดังเข้า DLQ); เหลือ housekeeping: ลบ `AttachPaymentSession` + column `PaymentSessionId` ที่เป็น legacy ไม่มี writer
3. **Checkout ขาด field ตามเป้าหมาย** — ผู้ทำรายการ (producer), ข้อมูลลูกค้า, ผู้รับแจ้งเตือนหลายรายการ (ลูกค้า + กำหนดเอง, อีเมล/SMS), การล็อกช่องทางจ่ายตั้งแต่ checkout, หมายเหตุ
4. **Notifications ยังเป็น stub** — ไม่มี email/SMS provider จริง (defer โดย spec), ไม่มีตารางประวัติการส่ง, ผู้รับ 1 ค่าต่อ order
5. **Transaction view ยังไม่มี** — ถ้าต้องการหน้า "รายการชำระเงิน" ให้ทำเป็น read model เหนือ `PaymentSession` (ห้ามสร้าง money ledger — non-goal)
6. **API client ระดับ tenant ยังไม่มี entity** — สิทธิ์ `apikey.manage` จองชื่อไว้ในแคตตาล็อกแล้วแต่ยังไม่ implement
7. **`src/Modules/Identity` เป็นโฟลเดอร์ค้าง** จากโมดูลที่ถูกลบ (ถูกแทนด้วย Producer) — ควรเก็บกวาดใน housekeeping ถัดไป
8. **PSP adapter รองรับจริงเฉพาะ `card`** — `OmiseAdapter.CreateRedirectChargeAsync` throw
   `NotSupportedException` สำหรับ `promptpay`/`installment`; `TwoCTwoPAdapter` ส่ง
   `paymentChannel = ["CC"]` คงที่ไม่อิง `PaymentSession.Method` (เลือก method อื่นจะได้ flow บัตรแทน) —
   เป้าหมาย 3 ช่องทาง × 2 PSP ยังไม่ถึง
9. **Omise webhook ยังไม่ verify HMAC** — `OmiseAdapter.VerifyWebhook` เป็น well-formedness check
   (deferred โดยเจตนา, comment ใน adapter ระบุ); การป้องกันจริงคือ fetch-to-confirm (ไม่เชื่อ body)
   + webhook rate limiter — ฝั่ง 2C2P verify JWT HS256 จริงแล้ว
10. **`POST /payment-sessions` ไม่ validate order และไม่ล็อกยอดฝั่ง server** —
    `CreatePaymentSessionHandler` ไม่แตะ Orders เลย: client ส่ง `orderId`/`amount`/`currency`/`method`/`psp`
    เองทั้งหมด → สร้าง session ให้ order ที่ไม่มีจริง/จ่ายแล้ว/ยอดผิดได้; แนวกันปลายทางเดียวคือ
    `Order.MarkPaid` verify amount+currency ตอนรับ `PaymentPaid` (จ่ายยอดผิดจะล้มดังเข้า DLQ แทนที่จะกันตั้งแต่ต้นทาง)
11. **`product.update` จองสิทธิ์ไว้แต่ไม่มี endpoint** — permission อยู่ใน Producer RBAC catalog แล้ว
    แต่ไม่มีเส้นทางแก้ไข/ปิดสินค้า (`IsActive` ไม่มี writer หลังสร้าง) — pattern เดียวกับ `apikey.manage` (ข้อ 6)
12. **สถานะปลายทางมีใน domain แต่ไม่มี trigger ใน production** — `PaymentSession.MarkExpired`,
    `CheckoutSession.Abandon`, `Order.Cancel` ไม่มีผู้เรียกนอก test (ตรวจ 2026-07-04):
    ไม่มี job auto-expire payment session (target payload มี `session.expiryMinutes`), ไม่มีนโยบาย abandon
    checkout ค้าง, ไม่มีเส้นทาง cancel/หมดอายุ order — รายการที่ไม่ถูกจ่ายจะค้าง `AwaitingPayment` ตลอดไป
13. **Method routing (primary/fallback ต่อช่องทาง) ยังไม่มี** — target มี Method router + `tenant.routing`
    payload (เช่น installment → Omise ก่อน ตกไป 2C2P) แต่ปัจจุบัน client เป็นผู้เลือก `psp` เองตอนสร้าง session;
    ยังไม่มีที่เก็บ/ผู้ใช้ routing config
14. **Maker-checker ยังไม่มี** — canon กำหนดสำหรับ action อ่อนไหว (approve tenant, เปลี่ยน routing,
    แก้ allowlist) แต่ทุก action ปัจจุบันเป็น single-actor + permission gate (เช่น approve producer ใช้คนเดียว)
15. **Observability/Operations API ยังไม่มี** — health check endpoint มีแล้ว (`/health/live` +
    `/health/ready` ตรวจ producer-db + vault ใน `BuildingBlocks.Web/HealthChecks.cs`
    (`AddReadinessHealthChecks()`/`MapPolHealthChecks()`) wire ทั้ง Api และ Worker); gap ที่เหลือจริงคือ
    observability: metrics taxonomy + alerts (DLQ โต, webhook `Rejected` ผิดปกติ, outbox ค้าง) +
    Operations API (outbox/DLQ inspect + requeue) — ดู
    [เป้าหมายเชิง API](#เป้าหมายเชิง-api-ระดับแพลตฟอร์ม-normative-target)
16. **Payment/PaymentAttempt split ยังไม่มี** — `PaymentSession` ปัจจุบันหลอมรวม payment intent กับ
    PSP attempt ไว้ในตัวเดียว (1 session = 1 attempt, ไม่มี retry/fallback model); target แยกเป็น
    `Payment` (เจตนาต่อ Order, unique `Payment(OrderId)`) + `PaymentAttempt` (ติดต่อ PSP ต่อครั้ง,
    มี state `Unknown` + inquiry recovery, filtered unique active attempt) + `Transaction` read model —
    supersede ข้อสรุปเดิมของ §10 ("ไม่ต้องมี entity แยก"); adapter contract เปลี่ยน signature ด้วย;
    migration จาก PaymentSession มี 5 phases (dual-read, ห้ามหยุดรับ webhook นาน) —
    deep design: [payment-orchestration-modules.md ภาค 8](payment-orchestration-modules.md)
17. **Webhook durable inbox ยังไม่มี** — pipeline ปัจจุบันเป็น one-transaction (ยังถูกต้องของรุ่น
    ปัจจุบัน: fetch-to-confirm เป็น authority); target = `WebhookDelivery` inbox (persist ก่อน ตอบเร็ว)
    + async processor + reprocess/admin views + unmatched handling + addressing ด้วย opaque
    `endpointKey` (`POST /api/webhooks/v1/{endpointKey}`) แทน `pspConnectionId` ตรงบน URL
18. **Canonical API conventions ขาเข้า + route version ยังไม่มี** — target: inbound `Idempotency-Key`
    + idempotency record (claim/replay/`409 key_reused`), `ETag`/`If-Match` (`412 version_mismatch`),
    RFC 9457 `code` catalog เสถียร, correlation/causation ids; base path `/api/v1/{area}`
    (version-first global, area = domain, audience per-endpoint) **migrate as-built ครบ 2026-07-05**
    ผ่าน spec `api-route-scheme` (big-bang — route flat เดิมถูกลบ ไม่ alias; idempotency/ETag/RFC9457 ยังเปิดอยู่);
    cursor pagination ยังขัด SFS (offset) — ยังไม่ตัดสิน (ADR ข้อ 13)
19. **Canonical status 7 ค่า + failure taxonomy ยังไม่มี** — target statuses:
    `pending`/`action_required`/`processing`/`succeeded`/`failed`/`expired`/`cancelled` — ปัจจุบัน
    canonical เดิม 4 ค่า (`Pending/Paid/Failed/Expired`) + `PaymentSession` states 5 ค่า
    (`Created/Redirected/...`); ไม่มี `action_required`/`processing`/`cancelled` และ `Paid` ต้อง
    rename เป็น `succeeded` (ADR ข้อ 15); failure taxonomy 6 หมวด
    (`business_decline`/`validation_terminal`/`technical_retryable`/`technical_terminal`/`unknown`/`security_rejected`)
    ยังไม่มีในโค้ด
20. **Customer capability payment API ยังไม่มี** — target ให้ลูกค้าเริ่มจ่ายเองผ่าน
    `POST /api/customer/v1/order-summaries/{token}/payments` (ไม่รับ amount/method/provider ใดๆ —
    รับได้เฉพาะ UX metadata เช่น locale) + public payment token สำหรับ polling + return handler
    `GET /api/customer/v1/payment-returns/{attemptToken}` (ไม่เชื่อ query string, ห้าม open redirect);
    ปัจจุบัน `POST /payment-sessions` เป็นฝั่ง producer/tenant client และส่งทุก field เอง (โยงข้อ 10)
21. **OrderLine + snapshot บน Order ยังไม่มี** — target: immutable `OrderLine` snapshots +
    customer/producer/payment-method snapshot บนใบ (แก้ไม่ได้หลังสร้าง) เพื่อให้หน้าสรุป/audit
    ถูกต้องย้อนหลัง; ปัจจุบัน Order เก็บยอดเดียวทั้งใบ — lines อยู่บน Cart เท่านั้น (โยงข้อ 3)
22. **Money ต้อง migrate เป็น DECIMAL(19,4)** — มาตรฐานใหม่ (ตัดสิน 2026-07-05):
    `Money { Amount: DECIMAL(19,4), Currency }` ทุกชั้น (domain + DB + wire) **ห้าม float/double**;
    as-built ปัจจุบันคือ `Money { MinorUnits: long }` เก็บ bigint + wire `{minorUnits, currency}` =
    legacy จนกว่า migration — ADR ต้องตัดสิน: wire carrier (string แนะนำ กัน IEEE754 double vs number),
    rounding rules, แผน migrate คอลัมน์ `Amount*` + backward compat (ADR ข้อ 16)

---

## ทะเบียนตัดสินใจค้าง (ADR pending) และลำดับเปิด spec

> รับเข้า 2026-07-05 — การตัดสินใจที่ target design ระบุว่าต้องเปิด ADR ก่อน implement
> (ADR จริงสร้างทีละใบเมื่อตัดสิน ตาม template ใน `.ai/shared/OUTPUT_FORMATS.md` → `docs/adr/000N-*.md`;
> ตารางนี้เป็นทะเบียนรอ ไม่ใช่ ADR)

### ADR ค้างตัดสิน

| # | เรื่อง | โมดูลกระทบ | spec ที่ควร trigger |
|---|---|---|---|
| 1 | หนึ่ง Order มี Payment เดียวตลอด หรืออนุญาต recreate หลัง `Expired` | Payment, Order | Payment/Attempt split |
| 2 | business decline อนุญาตสร้าง attempt ใหม่กับ PSP เดิม/ต่าง PSP อย่างไร | Payment, Routing | Routing policy |
| 3 | payment TTL, redirect TTL และ uncertainty deadline | Payment, Attempt | Payment/Attempt split |
| 4 | provider ที่ไม่มี inquiry by merchant reference จัดการ timeout อย่างไร | PSP adapter | Routing/recovery |
| 5 | webhook raw payload retention/encryption | Webhooks, Audit | Webhook inbox |
| 6 | active attempt filtered uniqueness implementation ใน SQL Server (enum ใน filtered index) | Attempt | Payment/Attempt split |
| 7 | manual operation ใดอนุญาตใน production | Operations | Ops tooling |
| 8 | tenant/producer method entitlement precedence | Tenant, Producer RBAC | Channel enforcement |
| 9 | customer status polling vs server push | Customer API | Customer capability API |
| 10 | direct M2M payment intent เปิดใน v2 หรือไม่ (ต้องมี canonical `PaymentSource` ก่อน) | Integration API | API client |
| 11 | refund/void อยู่ใน scope อนาคตหรือถูกห้ามต่อเนื่อง | Payment, Order | - (product decision) |
| 12 | legal/compliance retention ต่อชนิดข้อมูล | ทุกโมดูล | Retention policy |
| 13 | cursor pagination vs SFS offset (`Page`/`Limit`) — SFS approve แล้ว team กำลัง implement; SFS doc เปิดช่อง keyset สำหรับ deep pages; ตัดสินก่อน endpoint แรกที่ใช้ `nextCursor` | ทุก list endpoint | Transaction read API |
| 15 | canonical status rename (`Paid` → `succeeded`, เพิ่ม `action_required`/`processing`/`cancelled`) + event naming `.v1` — ชื่อ wire ปัจจุบัน (`tenant-users`, `TenantUserRegistrationSubmitted`, `PaymentPaid`) ถูก freeze ตาม CODING_STANDARDS ต้องมี compat strategy | Payment, Contracts | Payment/Attempt split |
| 16 | Money migration เป็น `DECIMAL(19,4)` — ADR เหลือ: wire carrier (string vs number), rounding rules, แผน migrate คอลัมน์ + backward compat | SharedKernel, ทุกโมดูลที่ถือเงิน | Money migration |

### ลำดับเปิด spec แนะนำ (design priorities × gaps × migration phases)

1. **Payment-create hardening** — ข้อ 10 + 1 (+22 บางส่วน): validate order, server-derive
   amount/currency/method, enforce effective methods, unique Order→Payment — migration Phase 1,
   ลดความเสี่ยงสูงสุดต่อเงินจริง
2. **Payment/PaymentAttempt split + canonical status** — ข้อ 16 + 19 — migration Phase 2
   (ต้องผ่าน ADR 15 ก่อน)
3. **Routing policy + provider idempotency + Unknown/inquiry recovery** — ข้อ 13 — Phase 3
4. **Webhook durable inbox + endpointKey + Omise HMAC** — ข้อ 17 + 9 — Phase 4
5. **OrderLine + checkout snapshot** — ข้อ 21 + 3
6. **Lifecycle/expiry jobs** (payment/checkout/cart/order terminal states) — ข้อ 12
7. ตามด้วย: tenant API client + Integration API (ข้อ 6) · notification delivery history (ข้อ 4) ·
   maker-checker (ข้อ 14) · observability + Operations API (ข้อ 15 — health endpoints มีแล้ว) ·
   Money migration (ข้อ 22 — จะทำพร้อมข้อ 2 ก็ได้ถ้า ADR 16 ตัดสินทัน)

ทุก phase ของ migration ต้องมี dual-read/compatibility strategy — ห้าม migration แบบหยุดรับ webhook นาน

### Definition of Done ของ spec ต่อโมดูล

สเปกโมดูลถือว่าพร้อม implement เมื่อครบ: owner/non-owner ชัด · aggregate/state machine/invariants ·
command/query/endpoint พร้อม authorization · request/response schema + server-authoritative fields ·
idempotency + concurrency semantics · stable error codes · event contracts + dedupe key ·
transactional boundary + partial-failure behavior · audit/PII/retention · metrics + alert + recovery path ·
compatibility/versioning · test matrix (happy, duplicate, concurrent, retry, timeout, stale version,
unauthorized, cross-tenant, provider mismatch) — DoD ฝั่ง Payment API เต็ม:
[payment-orchestration-modules.md ภาค 8.28](payment-orchestration-modules.md)
