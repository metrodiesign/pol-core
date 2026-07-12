# FE Migration Notes — rf1-schema-reset

> **อัปเดต (2026-07-12):** มี breaking change รอบสองต่อจากนี้แล้ว — route/permission-key/OpenAPI-id รอบ
> `hierarchical-naming` (เช่น `/api/v1/merchant-users/*` ที่เอกสารนี้อ้างถึงด้านล่าง ถูกเปลี่ยนต่อเป็น
> `/api/v1/merchants/users/*`). ดู [`hierarchical-naming/FE-MIGRATION.md`](../hierarchical-naming/FE-MIGRATION.md)
> สำหรับรอบล่าสุด — เอกสารนี้ (rf1) ยังถูกต้องสำหรับ auth model / Money wire format / JSON field rename
> ที่คุมอยู่ด้านล่าง, ไม่ต้องอ่านซ้ำสองรอบ.

> สำหรับทีม FE ทั้ง 2 repo (`pol-tenant`/merchant-user console, `pol-admin`) ที่เรียก backend `pol-core` นี้อยู่.
> rf1 เป็น breaking change ฝั่ง client แบบ big-bang — ไม่มี alias/route เก่าเหลือ, ไม่มี dual-write ช่วงเปลี่ยนผ่าน.
> ต้อง deploy backend + FE ทั้งสองพร้อมกัน (coordinate cutover ก่อน merge ตาม design.md Non-Functional
> Considerations). Satisfies REQ-10.4.

## 1. Route rename (เก่า → ใหม่)

ไม่มี alias/redirect ของ path เก่าเหลือเลย — เรียก path เก่าจะได้ 404 ทันทีหลัง deploy.

| เก่า | ใหม่ |
|---|---|
| `/api/v1/producers/*` (ทุก endpoint ใต้ merchant-user BFF) | `/api/v1/merchant-users/*` |
| `/api/v1/admins/tenants` (list/create merchant) | `/api/v1/admins/merchants` |
| `/api/v1/admins/tenants/{code}` | `/api/v1/admins/merchants/{code}` |
| `/api/v1/admins/tenant-users/{subject}/approve` | `/api/v1/admins/merchant-users/{subject}/approve` |
| `/api/v1/admins/tenant-users/{subject}/reject` | `/api/v1/admins/merchant-users/{subject}/reject` |

ที่ **ไม่เปลี่ยน** (ยังเป็น path เดิมเป๊ะ ๆ): `/products`, `/carts`, `/checkouts`, `/orders`,
`/orders/{orderId}/summary/resend`, `/payments/sessions`, `/reports/reconciliation`,
`/webhooks/{pspConnectionId}`, `/admins/auth/*`, `/admins/merchants/{id}/*` (suspend/reactivate/roles/sessions),
`/admins` (platform-user CRUD), order summary token endpoint (`GET /orders/{token}/summary`). เฉพาะ segment
ที่มีคำว่า `producer`/`tenant`/`tenant-user` เท่านั้นที่เปลี่ยน — ดู C# rename map เต็มใน
[design.md](design.md#c-rename-map-หลัก--ที่เหลือ-mechanical-ตาม-pattern-เดียวกัน).

## 2. Auth model — Bearer id-token ถูกถอดทิ้งทั้งหมด

**นี่คือ breaking change ที่สำคัญที่สุดของ rf1 ฝั่ง client.** ก่อนหน้านี้ merchant-user console (เดิมเรียก
"tenant SPA") ยิง Google id-token เป็น `Authorization: Bearer <id_token>` ตรงเข้า funnel endpoint (audience
`tenant`) — **path นี้ถูกลบทั้งก้อน** (`AddGoogleIdTokenAuthentication` + policy `tenant` ไม่มีอยู่แล้ว).

แทนที่ด้วย server-side OIDC BFF (แบบเดียวกับที่ admin console ใช้อยู่แล้ว):

- Login: `GET /api/v1/merchant-users/auth/login` → redirect Google → callback → ตั้ง session cookie
- ทุก request ถัดไปใช้ **cookie** (browser ส่งอัตโนมัติ, `credentials: 'include'` / `withCredentials: true`
  บน XHR) แทน `Authorization` header — **ต้องลบโค้ดที่แนบ Bearer header ออกจากทุก API call ของ funnel**
  (products/carts/checkouts/orders/payments/reports) ไม่ใช่แค่ endpoint `/merchant-users/*`
- Cookie: `__Host-mch_session` (เดิม `__Host-prd_session`) — `Secure`+`HttpOnly`+`SameSite=Lax` (หรือ `None`
  ถ้า deploy คนละ site), FE **อ่านค่าคุกกี้นี้ไม่ได้และไม่ควรพยายามอ่าน**
- CSRF: mutation ทุกตัว (`POST`/`PUT`/`DELETE`) ต้องแนบ header `X-CSRF-Token` ที่มีค่าตรงกับคุกกี้
  `mch_csrf` (เดิม `prd_csrf`) — double-submit pattern เดิม, แค่เปลี่ยนชื่อคุกกี้
- CORS: FE ต้องเรียกด้วย credentialed request (`AllowCredentials`) — origin ต้องอยู่ใน `Cors:AllowedOrigins`
  ฝั่ง backend (ตั้งค่าที่ operator backend, ไม่ใช่ FE)
- 401 handling: session หมดอายุ/invalid → redirect ไปหน้า login เดิม (mechanism ไม่เปลี่ยน จาก 401 Bearer เดิม
  เป็น 401 session เฉย ๆ — เช็ค response status เดิมได้)

Admin console **ไม่กระทบ** จุดนี้ (เดิมใช้ OIDC BFF cookie อยู่แล้ว, mechanism เดิมทุกอย่าง).

## 3. JSON field rename

- ทุก DTO ที่เคยมี field `tenantId` → **`merchantId`** (camelCase เดิม, แค่เปลี่ยนชื่อ key)
- Event/webhook payload (ถ้า FE consume ตรง — ปกติไม่ควร): `TenantUserRegistrationSubmitted` →
  `MerchantUserRegistrationSubmitted`, field `TenantId` → `MerchantId`
- Response shape อื่นไม่เปลี่ยน (จำนวน field, nesting เดิมทั้งหมด — เฉพาะชื่อ key ที่มี `tenant` เปลี่ยนเป็น
  `merchant`)

## 4. Money wire format — เปลี่ยนจาก flat minor-units เป็น object string

**เดิม** (ทุก endpoint ที่มีราคา/ยอดเงิน — createProduct, addItem, createPaymentSession, orderSummary ฯลฯ):

```json
{ "priceMinorUnits": 150000, "priceCurrency": "THB" }
```

**ใหม่** — รวมเป็น object เดียว ชื่อ field ตาม property เดิม (เช่น `price`/`amount`/`subtotal` แล้วแต่ endpoint)
แต่ตัวค่าเปลี่ยนเป็น:

```json
{ "price": { "amount": "1500.0000", "currency": "THB" } }
```

กติกา:
- `amount` เป็น **string เสมอ** (ไม่ใช่ number) — fixed 4 ตำแหน่งทศนิยมเสมอ (`"1500.0000"` ไม่ใช่ `"1500"` หรือ
  `"1500.00"`) ทั้งตอนรับและตอนส่ง
- ส่ง `amount` เป็น JSON number (ไม่ใส่ quote) → **400 RFC 9457** ทันที (กัน IEEE754 double precision loss —
  FE ต้อง format เป็น string เองก่อนส่ง ห้ามพึ่ง `JSON.stringify` บน number ตรง ๆ)
- `currency` เป็น ISO 4217 3 ตัวใหญ่เดิม ไม่เปลี่ยน
- ทุก endpoint ที่เคยส่ง/รับ `*MinorUnits` + `*Currency` แยก field สองอัน → รวมเป็น object เดียวหมดแล้ว — grep
  FE codebase หา `MinorUnits`/`minorUnits` เพื่อหาจุดที่ต้องแก้ทั้งหมด

## Reference

- Route/auth/Money mechanism เต็ม: [design.md](design.md) (Auth policies, Money section, C# rename map)
- Requirements: [requirements.md](requirements.md) REQ-5 (auth), REQ-6 (Money), REQ-8 (route/wire rename)
- Operator-side config renames (ไม่กระทบ FE โดยตรง แต่กระทบว่า backend คนไหน boot ได้): ดู
  [`docs/runbooks/local-dev-run.md`](../../docs/runbooks/local-dev-run.md) §2.3 Cutover note
