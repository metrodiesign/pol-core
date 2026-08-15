# Admin Control Plane Reference

เอกสารนี้สรุป Admin API ที่มีอยู่จริงใน `pol-core` ณ 2026-08-13. ใช้คู่กับ [admins.md](admins.md) ซึ่งอธิบาย OIDC, session, cookie และ RBAC พื้นฐาน.

## Boundary

Admin control plane ใช้ route root `/api/v1` และ `AdminSession` เดียวกับ Admin Console. ทุก operation ตรวจ `admin` authorization policy, permission key และ merchant scope ที่ backend; client ห้ามเลือก scope แทนการตรวจของ server.

- `Super` อ่าน aggregate ได้โดยไม่เลือก merchant แต่ mutation ที่มี merchant scope ต้องส่ง `merchantId` ชัดเจน
- `Scoped` อ่านและแก้ได้เฉพาะ merchant ที่ถูก assign
- resource ที่รองรับ concurrency คืน `ETag` และ mutation ต้องส่ง `If-Match`
- mutation ที่รองรับ replay ต้องส่ง `Idempotency-Key`
- secrets เขียนเข้า backend แล้วเก็บใน vault; read response คืนแค่ mask หรือ hint
- error ใช้ RFC 9457 Problem Details และไม่คืน raw secret, token, raw webhook payload หรือ signature

## Tenant, Originator และ PSP

| Capability | Routes | Permission | พฤติกรรมสำคัญ |
|---|---|---|---|
| Provision merchant | `POST /merchants` | `Super` tier | สร้าง merchant พร้อม PSP connection; รับ CSRF, ใช้ captive code allowlist และเก็บ secret ใน vault โดยไม่คืน plaintext |
| Merchant lookup | `GET /merchants/{code}` | `merchant.view` | Scoped admin เห็นเฉพาะ merchant ที่ assign; นอก scope หรือไม่พบคืน `404`, response คืน `ETag` |
| Merchant | `GET /merchants` · `PUT /merchants/{merchantId}` · `POST /merchants/{merchantId}/suspend` · `POST /merchants/{merchantId}/reactivate` | `merchant.view` / `merchant.manage` | list แบ่งหน้า; mutation ใช้ `If-Match` และ `Idempotency-Key` |
| Originator | `GET|POST /originators` · `GET|PUT|DELETE /originators/{originatorId}` · `POST /originators/{originatorId}/enable` · `POST /originators/{originatorId}/disable` | `merchant.view` / `merchant.manage` | รองรับ `branch`, `agent`, `broker`, `staff`, `app`; `code` เปลี่ยนไม่ได้; รายการที่ถูกอ้างอิงลบไม่ได้ |
| PSP connection | `GET|POST /payments/psp-connections` · `GET|PUT /payments/psp-connections/{connectionId}` · `POST /payments/psp-connections/{connectionId}/test` · `POST /payments/psp-connections/{connectionId}/credential-change-requests` | `settings.manage` | config อ่านได้, credential ไม่อ่านกลับ; test บันทึก health; credential change ต้อง maker-checker |
| Routing ruleset | `GET|POST /payments/routing-rulesets` · `GET|PUT|DELETE /payments/routing-rulesets/{rulesetId}` · `POST /payments/routing-rulesets/{rulesetId}/activation-requests` | `settings.manage` | draft แก้ได้; validate overlap/priority ก่อน activation; active ruleset ต้อง approval |

`GET` บางรายการของ control plane มี `RequireCsrf` ตาม endpoint metadata ปัจจุบัน. ให้ใช้ OpenAPI เป็น contract สุดท้ายของ header, query และ response.

## Merchant users และ Merchant roles

Admin branch อยู่บน route เดียวกับ resource owner แต่ไม่ใช้ `MerchantUserSession`:

| Capability | Routes | Permission |
|---|---|---|
| User profile/invitation | `GET /merchants/{merchantId}/users/{merchantUserId}/edit` · `POST /merchants/{merchantId}/user-invitations` · `PUT /merchants/{merchantId}/users/{merchantUserId}` | `merchants.users.manage` |
| Merchant roles | `GET /merchants/{merchantId}/roles` · `GET /merchants/{merchantId}/roles/{code}` · `GET /merchants/{merchantId}/permissions` | `merchants.roles.view` |
| Role mutations | `POST /merchants/{merchantId}/roles` · `PUT|DELETE /merchants/{merchantId}/roles/{code}` · `PUT /merchants/{merchantId}/users/{merchantUserId}/roles` | `merchants.roles.manage` / `merchants.users.manage` |

Invitation ใช้ `MerchantUserInvitation` aggregate เดียวกับ merchant console, hash token, ผูก merchant กับ email และ enqueue delivery ใน owner unit of work. Response ไม่คืน raw invitation token.

## Reporting และ transaction projection

| Route | Permission | พฤติกรรม |
|---|---|---|
| `GET /reports/dashboard` | `txn.view` | สรุปยอดตาม currency, transaction count, success/failure/pending และ breakdown ตาม PSP, method, originator |
| `GET /payments/transactions` | `txn.view` | projection จาก `Order` + `PaymentSession` + lifecycle event, SFS และ mask ข้อมูลลูกค้า |
| `GET /payments/transactions/{paymentSessionId}` | `txn.view` | detail, order lines, lifecycle, capability flags และ `ETag` |
| `GET /payments/transactions/export` | `txn.export` | CSV จาก query เดียวกัน, ช่วงสูงสุด 31 วัน, ไม่เกิน 100,000 แถวและ 100 MiB |
| `GET /reports/operations` | `txn.view` | summary ชุดเดียวกับ dashboard; default 7 วัน, สูงสุด 31 วัน |
| `GET /reports/operations/export` | `txn.export` | CSV ของ totals และ breakdown; ป้องกัน spreadsheet formula injection |

Transaction ไม่ใช่ ledger หรือ aggregate ใหม่. Capability ที่ยังไม่มี owner จริง เช่น `refund`, `capture`, `void` และ `receipt` ถูกคืนเป็น unavailable; backend ไม่จำลอง success.

## Governance และ audit

| Route | Permission | พฤติกรรม |
|---|---|---|
| `GET /approvals` · `GET /approvals/{approvalId}` | `settings.manage` | list/detail คำขอ maker-checker พร้อม target version และ execution outcome |
| `POST /approvals/{approvalId}/approve` | `settings.manage` | checker อนุมัติและเรียก owner operation |
| `POST /approvals/{approvalId}/reject` | `settings.manage` | checker ปฏิเสธพร้อมเหตุผล |
| `GET /audits` · `GET /audits/{auditId}` | `audit.view` | ตรวจ hash chain ก่อนคืน append-only audit ที่ redact แล้ว |

กฎบังคับ: maker ห้ามตัดสินคำขอของตัวเอง, approval ที่ไม่ใช่ `Pending` หรือ target version เปลี่ยนแล้วต้องถูกปฏิเสธ, audit record แก้หรือลบไม่ได้. Hash-chain integrity ผิดปกติคืน `503`.

## API clients และ delivery

### API clients

Routes ใต้ `/api/v1/api-clients` ใช้ `apikey.manage`:

- `GET /api-clients` และ `GET /api-clients/{clientId}` อ่าน config, scope, status และ secret hint โดยไม่คืน secret
- `POST /api-clients` สร้าง client และออก one-time secret ticket; ต้องส่ง `Idempotency-Key`
- `PUT /api-clients/{clientId}` แก้ name, scopes และ IP policy; ต้องส่ง `If-Match` กับ `Idempotency-Key`
- `POST /api-clients/{clientId}/revoke` revoke client และต้องใช้ `If-Match` กับ `Idempotency-Key`
- `POST /api-clients/{clientId}/secret-rotation-requests` สร้าง maker-checker request
- `POST /api-clients/secrets/{ticketId}/reveal` consume ticket แล้วคืน secret ครั้งเดียว พร้อม `Cache-Control: no-store`

Ticket ที่ pending, ถูกใช้แล้ว, ถูกปฏิเสธ, หมดอายุ หรือไม่รู้จักคืนสถานะ error ตาม OpenAPI; secret ที่เก็บแล้วไม่มี read-back.

### Outbound webhook และ notification

Routes ใต้ `/api/v1/webhooks/endpoints`, `/api/v1/webhooks/deliveries`, `/api/v1/notifications/rules` และ `/api/v1/notifications/deliveries` ใช้ `settings.manage`.

- webhook endpoint ตรวจ destination แบบ SSRF-safe ก่อนบันทึก
- signing secret คืนเฉพาะ response แรกของการสร้าง พร้อม `no-store`; read ภายหลังคืน hint
- delivery replay สร้าง delivery ใหม่โดยไม่แก้ประวัติเดิม และต้องใช้ `Idempotency-Key`
- notification rule รองรับ event, channel, destination, threshold และ enabled
- delivery history เก็บ destination แบบ mask และ failure code ที่ลดข้อมูลอ่อนไหว

Inbound PSP callback เป็นคนละ surface: `GET /webhooks/inbound-events` และ `GET /webhooks/inbound-events/{eventId}` ใช้ `audit.view`, คืน fingerprint และ linkage เท่านั้น ไม่คืน raw payload หรือลายเซ็น.

## OpenAPI documents

Development เปิดเอกสาร 4 ชุด:

| Document | ขอบเขต |
|---|---|
| `v1` | ทุก operation เพื่อ backward compatibility |
| `admin` | `AdminSession` และ Admin auth/control plane |
| `merchant` | `MerchantUserSession`, merchant auth/register และ customer payment capability |
| `integration` | customer payment capability และ inbound PSP webhook |

`admin` ใช้เฉพาะ `AdminSession`; ไม่โฆษณา `MerchantUserSession` หรือ Bearer flow. Scalar แสดง named documents และซ่อน combined `v1`.

## Source of truth

- Routes: `src/Hosts/Api/ControlPlane/`, `src/Hosts/Api/Governance/`, `src/Hosts/Api/Iam/ApiClientEndpoints.cs`, `src/Hosts/Api/Notifications/`, `src/Hosts/Api/Reporting/`, `src/Hosts/Api/Webhooks/`
- OpenAPI: `src/Hosts/Api/OpenApiDocuments.cs`, `src/Hosts/Api/AudienceOpenApi.cs`
- Owners: `src/Modules/Governance/`, `src/Modules/Notifications/`, `src/Modules/Reporting/`, `src/Modules/Iam/ApiClients/`, `src/Modules/Payments/AdminControlPlane/`
- Persisted schema: [entity-fields.md](entity-fields.md)
