# สารบัญ API endpoints

เอกสารนี้รวบรวม HTTP endpoints ที่ source `src/Api` map ไว้จริง ณ วันที่ตรวจ source ล่าสุด พร้อม path เต็ม, หน้าที่, caller/policy และแหล่งอ้างอิงต่อแถว โดยคง route constraint เช่น `:guid` ไว้ตาม declaration.

## ขอบเขตและจำนวน

นับเป็น operation แยกตาม Method + fullPath; helper ที่สร้างหลายเส้นทางถูกขยายเป็นเส้นทางที่เรียกจริง และไม่รวม route ใน tests หรือ helper ที่ไม่ได้ถูกเรียกจาก composition root.

| รายการ | จำนวน |
| --- | ---: |
| Explicit mapped operations (`MapGet`/`MapPost`/`MapPut`/`MapPatch`/`MapDelete`) | **274** |
| GET | 134 |
| POST | 88 |
| PUT | 31 |
| PATCH | 7 |
| DELETE | 14 |
| Development infrastructure templates (`MapOpenApi`/`MapScalarApiReference`) | **2** |
| OIDC middleware callback defaults (แยกจาก explicit map) | **2** |

`MapOpenApi` และ `MapScalarApiReference` ถูกแสดงในส่วน infrastructure แยกต่างหาก เพราะ framework สร้าง route template ให้; callback ของ OIDC ที่ middleware จัดการเองก็แยกไว้ท้ายเอกสารและไม่ถูกรวมใน 274 operations.

ประเภท `Canonical` และ `Compatibility` ใช้เมื่อ source หรือเอกสารอ้างสถานะนั้นโดยตรง; `Current` หมายถึง surface ที่ใช้งานอยู่แต่ source ไม่ได้ประกาศว่าเป็น alias หรือ canonical owner.

Policy และ permission ในตารางใช้ชื่อ wire จริง: `admin`, `merchant-user`, `identity-platform`, `identity-bff`, `dual-console` และ `admin-or-identity-order` เป็น policy จาก [ConsoleSessionAuthentication.cs](../../src/Api/Api/Iam/ConsoleSessionAuthentication.cs) และ identity wiring และ BFF CSRF behavior จาก [BffCsrfFilter.cs](../../src/Api/Api/IdentityAccess/BffCsrfFilter.cs); ค่า permission เช่น `payment.create` และ `merchant.view` มาจาก [Keys.cs](../../src/Domain/Modules/Iam.Domain/Permissions/Keys.cs). `identity: order.read|order.write|checkout.write` ระบุ system scope ของ identity-order guard.

## Identity และ OAuth — 49 endpoints

| Method | fullPath | ประเภท | หน้าที่ | caller / auth policy | source |
| --- | --- | --- | --- | --- | --- |
| GET | `/.well-known/jwks.json` | Identity | อ่าน public signing keys | สาธารณะ · OIDC/OAuth entry point (metadata `AllowAnonymous`) | [IdentityAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs) |
| GET | `/.well-known/oauth-authorization-server` | Identity | อ่าน OAuth authorization server metadata | สาธารณะ · OIDC/OAuth entry point (metadata `AllowAnonymous`) | [IdentityAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs) |
| GET | `/api/v1/accounts` | Identity | ค้นหา business accounts | policy `admin` · permission `user.manage` | [CanonicalAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/CanonicalAccessEndpoints.cs) |
| GET | `/api/v1/accounts/{accountId:guid}` | Identity | อ่าน business account | policy `admin` · permission `user.manage` | [CanonicalAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/CanonicalAccessEndpoints.cs) |
| PATCH | `/api/v1/accounts/{accountId:guid}` | Identity | แก้ไขสถานะ business account | policy `admin` · permission `user.manage` · CSRF filter | [CanonicalAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/CanonicalAccessEndpoints.cs) |
| GET | `/api/v1/accounts/{accountId:guid}/merchant-access` | Identity | รายการ Merchant access ของ account | policy `admin` · permission `user.manage` | [CanonicalAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/CanonicalAccessEndpoints.cs) |
| DELETE | `/api/v1/accounts/{accountId:guid}/merchant-access/{merchantId:guid}` | Identity | เพิกถอน Merchant access | policy `admin` · permission `user.manage` · CSRF filter | [CanonicalAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/CanonicalAccessEndpoints.cs) |
| PUT | `/api/v1/accounts/{accountId:guid}/merchant-access/{merchantId:guid}` | Identity | แทนที่ Merchant access | policy `admin` · permission `user.manage` · CSRF filter | [CanonicalAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/CanonicalAccessEndpoints.cs) |
| GET | `/api/v1/accounts/{accountId:guid}/platform-access` | Identity | อ่าน Platform access | policy `admin` · permission `user.manage` | [CanonicalAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/CanonicalAccessEndpoints.cs) |
| PUT | `/api/v1/accounts/{accountId:guid}/platform-access` | Identity | แทนที่ Platform access | policy `admin` · permission `user.manage` · CSRF filter | [CanonicalAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/CanonicalAccessEndpoints.cs) |
| POST | `/api/v1/accounts/{accountId:guid}/session-revocations` | Identity | เพิกถอน sessions ของ account | policy `admin` · permission `user.manage` · CSRF filter | [CanonicalAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/CanonicalAccessEndpoints.cs) |
| GET | `/api/v1/agent-registration` | Identity | อ่าน registration case ปัจจุบันของผู้สมัครจาก registration session | ผู้สมัคร · registration session (metadata `AllowAnonymous`, handler ตรวจ session) | [AgentRegistrationEndpoints.cs](../../src/Api/Api/Accounts/AgentRegistrationEndpoints.cs) |
| PUT | `/api/v1/agent-registration` | Identity | บันทึก draft ข้อมูลการสมัครของผู้สมัคร | ผู้สมัคร · registration session (metadata `AllowAnonymous`, handler ตรวจ session) | [AgentRegistrationEndpoints.cs](../../src/Api/Api/Accounts/AgentRegistrationEndpoints.cs) |
| GET | `/api/v1/agent-registration/history` | Identity | อ่านประวัติ attempts ของการสมัครของตนเอง | ผู้สมัคร · registration session (metadata `AllowAnonymous`, handler ตรวจ session) | [AgentRegistrationEndpoints.cs](../../src/Api/Api/Accounts/AgentRegistrationEndpoints.cs) |
| POST | `/api/v1/agent-registration/submissions` | Identity | ส่ง draft การสมัครเพื่อเริ่มการพิจารณา | ผู้สมัคร · registration session (metadata `AllowAnonymous`, handler ตรวจ session) | [AgentRegistrationEndpoints.cs](../../src/Api/Api/Accounts/AgentRegistrationEndpoints.cs) |
| GET | `/api/v1/agent-registrations` | Identity | รายการ registration case ใน Admin merchant scope | policy `admin` · permission `merchants.users.view` · CSRF filter | [AgentRegistrationEndpoints.cs](../../src/Api/Api/Accounts/AgentRegistrationEndpoints.cs) |
| GET | `/api/v1/agent-registrations/{registrationId:guid}` | Identity | อ่าน registration case สำหรับการพิจารณา | policy `admin` · permission `merchants.users.view` · CSRF filter | [AgentRegistrationEndpoints.cs](../../src/Api/Api/Accounts/AgentRegistrationEndpoints.cs) |
| GET | `/api/v1/agent-registrations/{registrationId:guid}/attempts` | Identity | รายการ attempts ของ registration case | policy `admin` · permission `merchants.users.view` · CSRF filter | [AgentRegistrationEndpoints.cs](../../src/Api/Api/Accounts/AgentRegistrationEndpoints.cs) |
| POST | `/api/v1/agent-registrations/{registrationId:guid}/attempts/{attemptId:guid}/approve` | Identity | อนุมัติ attempt หลังตรวจข้อมูลและหลักฐาน | policy `admin` · permission `merchants.users.approve` · CSRF filter | [AgentRegistrationEndpoints.cs](../../src/Api/Api/Accounts/AgentRegistrationEndpoints.cs) |
| POST | `/api/v1/agent-registrations/{registrationId:guid}/attempts/{attemptId:guid}/reject` | Identity | ปฏิเสธ attempt พร้อมเหตุผลและบันทึกการตรวจ | policy `admin` · permission `merchants.users.reject` · CSRF filter | [AgentRegistrationEndpoints.cs](../../src/Api/Api/Accounts/AgentRegistrationEndpoints.cs) |
| GET | `/api/v1/auth/agents/callback` | Identity | รับ agent OIDC callback | สาธารณะ · OIDC/OAuth entry point (metadata `AllowAnonymous`) | [IdentityAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs) |
| GET | `/api/v1/auth/agents/login` | Identity | เริ่ม OIDC login สำหรับตัวแทนหรือผู้ใช้ร้านค้า | สาธารณะ · OIDC/OAuth entry point (metadata `AllowAnonymous`) | [IdentityAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs) |
| GET | `/api/v1/auth/employees/callback` | Identity | รับ workforce OIDC callback | สาธารณะ · OIDC/OAuth entry point (metadata `AllowAnonymous`) | [IdentityAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs) |
| GET | `/api/v1/auth/employees/login` | Identity | เริ่ม OIDC login สำหรับพนักงานภายใน | สาธารณะ · OIDC/OAuth entry point (metadata `AllowAnonymous`) | [IdentityAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs) |
| POST | `/api/v1/auth/logout` | Identity | ออกจาก identity BFF session | policy `identity-bff` · CSRF filter | [IdentityAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs) |
| POST | `/api/v1/auth/merchant-context` | Identity | เลือก merchant context ของ identity session | policy `identity-bff` · CSRF filter | [IdentityAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs) |
| GET | `/api/v1/auth/session` | Identity | อ่าน identity BFF session ปัจจุบัน | policy `identity-platform` | [IdentityAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs) |
| POST | `/api/v1/auth/session/refresh` | Identity | ต่ออายุ identity BFF session | policy `identity-bff` · CSRF filter | [IdentityAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs) |
| GET | `/api/v1/me` | Identity | อ่าน account context ของ caller ปัจจุบัน | policy `identity-platform` | [IdentityAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs) |
| GET | `/api/v1/me/access` | Identity | อ่าน authorization context และสิทธิ์ของ caller | policy `identity-platform` | [IdentityAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs) |
| GET | `/api/v1/me/merchants` | Identity | รายการ merchant access ของ account ปัจจุบัน | policy `identity-platform` | [IdentityAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs) |
| GET | `/api/v1/me/sessions` | Identity | รายการ BFF sessions ของบัญชีตนเอง | policy `identity-platform` | [IdentityAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs) |
| DELETE | `/api/v1/me/sessions/{sessionId:guid}` | Identity | เพิกถอน BFF session ที่เลือก | policy `identity-bff` · CSRF filter | [IdentityAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs) |
| GET | `/api/v1/permissions` | Identity | อ่าน permission catalog | policy `admin` · permission `user.roles` | [CanonicalAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/CanonicalAccessEndpoints.cs) |
| GET | `/api/v1/roles` | Identity | ค้นหา Platform roles | policy `admin` · permission `user.roles` | [CanonicalAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/CanonicalAccessEndpoints.cs) |
| POST | `/api/v1/roles` | Identity | สร้าง Platform role | policy `admin` · permission `user.roles` · CSRF filter | [CanonicalAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/CanonicalAccessEndpoints.cs) |
| GET | `/api/v1/roles/{roleId:guid}` | Identity | อ่าน Platform role | policy `admin` · permission `user.roles` | [CanonicalAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/CanonicalAccessEndpoints.cs) |
| PUT | `/api/v1/roles/{roleId:guid}` | Identity | แก้ Platform role | policy `admin` · permission `user.roles` · CSRF filter | [CanonicalAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/CanonicalAccessEndpoints.cs) |
| GET | `/api/v1/system-clients` | Identity | รายการ SYSTEM clients | policy `admin` · permission `user.manage` | [IdentityAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs) |
| POST | `/api/v1/system-clients` | Identity | สร้าง SYSTEM client | policy `admin` · permission `user.manage` · CSRF filter | [IdentityAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs) |
| GET | `/api/v1/system-clients/{clientId:guid}` | Identity | อ่าน SYSTEM client | policy `admin` · permission `user.manage` | [IdentityAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs) |
| PATCH | `/api/v1/system-clients/{clientId:guid}` | Identity | แก้ไข SYSTEM client | policy `admin` · permission `user.manage` · CSRF filter | [IdentityAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs) |
| PUT | `/api/v1/system-clients/{clientId:guid}/access` | Identity | แทนที่สิทธิ์ SYSTEM client | policy `admin` · permission `settings.manage` · CSRF filter | [IdentityAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs) |
| GET | `/api/v1/system-clients/{clientId:guid}/keys` | Identity | รายการ public keys ของ SYSTEM client | policy `admin` · permission `user.manage` | [IdentityAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs) |
| POST | `/api/v1/system-clients/{clientId:guid}/keys` | Identity | ลงทะเบียน SYSTEM public key | policy `admin` · permission `user.manage` · CSRF filter | [IdentityAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs) |
| DELETE | `/api/v1/system-clients/{clientId:guid}/keys/{keyId:guid}` | Identity | เพิกถอน SYSTEM public key | policy `admin` · permission `user.manage` · CSRF filter | [IdentityAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs) |
| GET | `/oauth/authorize` | Identity | เริ่ม OAuth authorization code flow | OpenIddict public endpoint · ตรวจ redirect URI, state และ PKCE (metadata `AllowAnonymous`) | [IdentityAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs) |
| POST | `/oauth/revoke` | Identity | เพิกถอน OAuth token | OpenIddict public endpoint · ตรวจ client ownership และ token reference (metadata `AllowAnonymous`) | [IdentityAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs) |
| POST | `/oauth/token` | Identity | ออก OAuth token | OpenIddict public endpoint · ตรวจ registered client และ grant type (metadata `AllowAnonymous`) | [IdentityAccessEndpoints.cs](../../src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs) |

## Commerce, checkout และ orders — 27 endpoints

| Method | fullPath | ประเภท | หน้าที่ | caller / auth policy | source |
| --- | --- | --- | --- | --- | --- |
| POST | `/api/v1/carts` | Current | เปิดตะกร้าสินค้า | policy `dual-console` · permission `payment.create` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |
| GET | `/api/v1/carts/{cartId:guid}` | Current | ดูตะกร้าสินค้า | policy `dual-console` · permission `payment.view` | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/carts/{cartId:guid}/clear` | Current | ล้างตะกร้าสินค้า | policy `dual-console` · permission `payment.create` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/carts/{cartId:guid}/items` | Current | เพิ่มรายการสินค้าในตะกร้า | policy `dual-console` · permission `payment.create` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |
| DELETE | `/api/v1/carts/{cartId:guid}/items/{itemId:guid}` | Current | ลบรายการในตะกร้า | policy `dual-console` · permission `payment.create` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |
| PUT | `/api/v1/carts/{cartId:guid}/items/{itemId:guid}` | Current | ปรับจำนวนรายการในตะกร้า | policy `dual-console` · permission `payment.create` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/checkout/access` | Current | แลกลิงก์เป็น checkout capability | ลูกค้า · opaque checkout capability/token (metadata `AllowAnonymous`) · rate limit | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/checkout/confirm` | Current | ยืนยันและเริ่มการชำระเงิน | ลูกค้า · opaque checkout capability/token (metadata `AllowAnonymous`) · rate limit | [Program.cs](../../src/Api/Api/Program.cs) |
| GET | `/api/v1/checkout/status` | Current | อ่านสถานะการชำระเงิน | ลูกค้า · opaque checkout capability/token (metadata `AllowAnonymous`) · rate limit | [Program.cs](../../src/Api/Api/Program.cs) |
| GET | `/api/v1/checkout/summary` | Current | อ่านสรุป Order ของลูกค้า | ลูกค้า · opaque checkout capability/token (metadata `AllowAnonymous`) · rate limit | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/checkout/verify` | Current | ขอให้ backend ตรวจสถานะ PSP | ลูกค้า · opaque checkout capability/token (metadata `AllowAnonymous`) · rate limit | [Program.cs](../../src/Api/Api/Program.cs) |
| GET | `/api/v1/orders` | Current | รายการคำสั่งซื้อ | policy `dual-console` · permission `payment.view (identity: order.read)` | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/orders` | Canonical | สร้างคำสั่งซื้อแบบ canonical | policy `identity-platform` · permission `payment.create` (human) OR system scope `order.write` · mutation guard: BFF CSRF for cookie, Bearer bypasses CSRF | [Program.cs](../../src/Api/Api/Program.cs) |
| GET | `/api/v1/orders/export` | Current | ส่งออกรายการคำสั่งซื้อ | policy `admin` · permission `txn.export` | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/orders/from-cart` | Compatibility | สร้างคำสั่งซื้อจากตะกร้า | policy `dual-console` · permission `payment.create` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |
| GET | `/api/v1/orders/{orderId:guid}` | Current | อ่านคำสั่งซื้อแบบเต็มพร้อม audit trail | policy `dual-console` · permission `payment.view (identity: order.read)` | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/orders/{orderId:guid}/cancel` | Current | ยกเลิกคำสั่งซื้อ | policy `dual-console` · permission `payment.create (identity: order.write)` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/orders/{orderId:guid}/issue` | Current | ออกคำสั่งซื้อและ PaymentLink แรก | policy `dual-console` · permission `payment.create (identity: order.write)` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |
| GET | `/api/v1/orders/{orderId:guid}/payment-links` | Current | อ่านสถานะลิงก์ของ Order | policy `dual-console` · permission `payment.view (identity: order.read)` | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/orders/{orderId:guid}/payment-links` | Current | ออกหรือหมุน PaymentLink | policy `dual-console` · permission `payment.create (identity: checkout.write)` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/orders/{orderId:guid}/summary/resend` | Current | ส่งลิงก์สรุปคำสั่งซื้อซ้ำ | policy `dual-console` · permission `payment.create` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/orders/{token}/pay` | Compatibility | ลูกค้าชำระเงินผ่านลิงก์ | ลูกค้า · opaque checkout capability/token (metadata `AllowAnonymous`) · rate limit | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/orders/{token}/payment-status` | Compatibility | สถานะการชำระเงินของลูกค้า | ลูกค้า · opaque checkout capability/token (metadata `AllowAnonymous`) · rate limit | [Program.cs](../../src/Api/Api/Program.cs) |
| GET | `/api/v1/orders/{token}/summary` | Compatibility | สรุปคำสั่งซื้อผ่านลิงก์ | ลูกค้า · opaque checkout capability/token (metadata `AllowAnonymous`) | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/payment-links/{linkId:guid}/revoke` | Current | เพิกถอน PaymentLink | policy `dual-console` · permission `payment.create (identity: checkout.write)` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |
| GET | `/api/v1/products` | Current | รายการผลิตภัณฑ์ | policy `merchant-user` · permission `payment.view` | [Program.cs](../../src/Api/Api/Program.cs) |
| GET | `/api/v1/products/documents` | Current | รายการเอกสารประกันสำหรับผู้ดูแลระบบ | policy `admin` · permission `txn.view` | [Program.cs](../../src/Api/Api/Program.cs) |

## Payment compatibility — 6 endpoints

| Method | fullPath | ประเภท | หน้าที่ | caller / auth policy | source |
| --- | --- | --- | --- | --- | --- |
| GET | `/api/v1/payment-returns/{providerCode}` | Integration | รับ browser return จาก PSP | PSP/browser return · metadata `AllowAnonymous` · rate limit | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/payment-returns/{providerCode}` | Integration | รับ form post return จาก PSP | PSP/browser return · metadata `AllowAnonymous` · rate limit | [Program.cs](../../src/Api/Api/Program.cs) |
| GET | `/api/v1/payments/sessions` | Compatibility | รายการ payment session ของร้านค้า | policy `merchant-user` · permission `payment.view` | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/payments/sessions` | Compatibility | สร้าง payment session จาก Order และให้ server เลือก PSP ตาม routing | Merchant Console หรือ Admin Console · policy `dual-console` · `payment.create` · audience CSRF | [Program.cs](../../src/Api/Api/Program.cs) |
| GET | `/api/v1/payments/sessions/{paymentSessionId:guid}` | Compatibility | อ่าน payment session ตาม merchant หรือ Admin scope | Merchant Console หรือ Admin Console · policy `dual-console` · `payment.view` | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/payments/sessions/{paymentSessionId:guid}/redirect` | Compatibility | claim payment session แล้วสร้าง URL redirect ไป PSP | Merchant Console หรือ Admin Console · policy `dual-console` · `payment.redirect` · audience CSRF | [Program.cs](../../src/Api/Api/Program.cs) |

## Payment webhooks — 2 endpoints

| Method | fullPath | ประเภท | หน้าที่ | caller / auth policy | source |
| --- | --- | --- | --- | --- | --- |
| POST | `/api/v1/webhooks/payment-providers/{providerAccountId:guid}` | Integration | Webhook ผลการชำระของ Transaction | PSP callback · provider signature · metadata `AllowAnonymous` · rate limit | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/webhooks/{pspConnectionId:guid}` | Integration | Webhook callback จาก PSP | PSP callback · ลายเซ็น `X-Signature` · rate limit | [Program.cs](../../src/Api/Api/Program.cs) |

## Canonical commerce และ transactions — 9 endpoints

| Method | fullPath | ประเภท | หน้าที่ | caller / auth policy | source |
| --- | --- | --- | --- | --- | --- |
| GET | `/api/v1/checkout/payment-methods` | Canonical | รายการ Payment methods ของ Checkout | policy `merchant-user` · permission `payment.view` | [CanonicalCommerceEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalCommerceEndpoints.cs) |
| PATCH | `/api/v1/orders/{orderId:guid}` | Canonical | แก้ไข Draft Order | policy `admin-or-identity-order` · permission `payment.create (identity: order.write)` · CSRF filter | [CanonicalCommerceEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalCommerceEndpoints.cs) |
| GET | `/api/v1/orders/{orderId:guid}/history` | Canonical | ประวัติ Order | policy `admin-or-identity-order` · permission `payment.view (identity: order.read)` | [CanonicalCommerceEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalCommerceEndpoints.cs) |
| GET | `/api/v1/orders/{orderId:guid}/items` | Canonical | รายการ Order items | policy `admin-or-identity-order` · permission `payment.view (identity: order.read)` | [CanonicalCommerceEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalCommerceEndpoints.cs) |
| GET | `/api/v1/transactions` | Canonical | รายการ Transaction | policy `admin` · permission `payment.view` | [CanonicalCommerceEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalCommerceEndpoints.cs) |
| GET | `/api/v1/transactions/{transactionId:guid}` | Canonical | อ่าน Transaction | policy `admin` · permission `payment.view` | [CanonicalCommerceEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalCommerceEndpoints.cs) |
| GET | `/api/v1/transactions/{transactionId:guid}/events` | Canonical | รายการ Transaction events | policy `admin` · permission `payment.view` | [CanonicalCommerceEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalCommerceEndpoints.cs) |
| POST | `/api/v1/transactions/{transactionId:guid}/review-notes` | Canonical | เพิ่ม Transaction review note | policy `admin` · permission `payment.view` · CSRF filter | [CanonicalCommerceEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalCommerceEndpoints.cs) |
| POST | `/api/v1/transactions/{transactionId:guid}/verify` | Canonical | ตรวจสอบ Transaction | policy `admin` · permission `payment.view` · CSRF filter | [CanonicalCommerceEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalCommerceEndpoints.cs) |

## Canonical control plane — 24 endpoints

| Method | fullPath | ประเภท | หน้าที่ | caller / auth policy | source |
| --- | --- | --- | --- | --- | --- |
| GET | `/api/v1/merchants/{merchantId:guid}` | Canonical | อ่าน Merchant | policy `admin` · permission `merchant.view` | [CanonicalMerchantConfigurationEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalMerchantConfigurationEndpoints.cs) |
| PATCH | `/api/v1/merchants/{merchantId:guid}` | Canonical | แก้ไข Merchant | policy `admin` · permission `merchant.manage` · CSRF filter | [CanonicalMerchantConfigurationEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalMerchantConfigurationEndpoints.cs) |
| GET | `/api/v1/merchants/{merchantId:guid}/branches` | Canonical | รายการสาขา Merchant | policy `admin` · permission `merchant.view` | [CanonicalMerchantConfigurationEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalMerchantConfigurationEndpoints.cs) |
| POST | `/api/v1/merchants/{merchantId:guid}/branches` | Canonical | สร้างสาขา Merchant | policy `admin` · permission `merchant.manage` · CSRF filter | [CanonicalMerchantConfigurationEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalMerchantConfigurationEndpoints.cs) |
| PATCH | `/api/v1/merchants/{merchantId:guid}/branches/{branchId:guid}` | Canonical | แก้ไขสาขา Merchant | policy `admin` · permission `merchant.manage` · CSRF filter | [CanonicalMerchantConfigurationEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalMerchantConfigurationEndpoints.cs) |
| GET | `/api/v1/merchants/{merchantId:guid}/payment-setting-requests` | Canonical | รายการคำขอ payment settings | policy `admin` · permission `settings.manage` | [CanonicalProviderConfigurationEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalProviderConfigurationEndpoints.cs) |
| POST | `/api/v1/merchants/{merchantId:guid}/payment-setting-requests` | Canonical | สร้างคำขอเปลี่ยน payment setting | policy `admin` · permission `settings.manage` · CSRF filter | [CanonicalProviderConfigurationEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalProviderConfigurationEndpoints.cs) |
| GET | `/api/v1/merchants/{merchantId:guid}/payment-setting-requests/{requestId:guid}` | Canonical | อ่านคำขอ payment setting | policy `admin` · permission `settings.manage` | [CanonicalProviderConfigurationEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalProviderConfigurationEndpoints.cs) |
| POST | `/api/v1/merchants/{merchantId:guid}/payment-setting-requests/{requestId:guid}/approve` | Canonical | อนุมัติ payment setting request | policy `admin` · permission `settings.manage` · CSRF filter | [CanonicalProviderConfigurationEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalProviderConfigurationEndpoints.cs) |
| POST | `/api/v1/merchants/{merchantId:guid}/payment-setting-requests/{requestId:guid}/reject` | Canonical | ปฏิเสธ payment setting request | policy `admin` · permission `settings.manage` · CSRF filter | [CanonicalProviderConfigurationEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalProviderConfigurationEndpoints.cs) |
| GET | `/api/v1/merchants/{merchantId:guid}/payment-settings` | Canonical | อ่าน effective payment settings | policy `admin` · permission `settings.manage` | [CanonicalProviderConfigurationEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalProviderConfigurationEndpoints.cs) |
| GET | `/api/v1/merchants/{merchantId:guid}/provider-accounts` | Canonical | รายการ Provider Account ของ Merchant | policy `admin` · permission `settings.manage` | [CanonicalProviderConfigurationEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalProviderConfigurationEndpoints.cs) |
| POST | `/api/v1/merchants/{merchantId:guid}/provider-accounts` | Canonical | สร้าง Provider Account configuration | policy `admin` · permission `settings.manage` · CSRF filter | [CanonicalProviderConfigurationEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalProviderConfigurationEndpoints.cs) |
| GET | `/api/v1/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}` | Canonical | อ่าน Provider Account | policy `admin` · permission `settings.manage` | [CanonicalProviderConfigurationEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalProviderConfigurationEndpoints.cs) |
| PATCH | `/api/v1/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}` | Canonical | แก้ไข Provider Account | policy `admin` · permission `settings.manage` · CSRF filter | [CanonicalProviderConfigurationEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalProviderConfigurationEndpoints.cs) |
| POST | `/api/v1/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}/connection-tests` | Canonical | ทดสอบการเชื่อมต่อ Provider | policy `admin` · permission `settings.manage` · CSRF filter | [CanonicalProviderConfigurationEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalProviderConfigurationEndpoints.cs) |
| GET | `/api/v1/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}/credential-versions` | Canonical | รายการ credential versions | policy `admin` · permission `settings.manage` | [CanonicalProviderConfigurationEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalProviderConfigurationEndpoints.cs) |
| POST | `/api/v1/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}/credential-versions` | Canonical | สร้าง credential version แบบ write-only | policy `admin` · permission `settings.manage` · CSRF filter | [CanonicalProviderConfigurationEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalProviderConfigurationEndpoints.cs) |
| POST | `/api/v1/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}/disable` | Canonical | หยุด Provider Account ฉุกเฉิน | policy `admin` · permission `settings.manage` · CSRF filter | [CanonicalProviderConfigurationEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalProviderConfigurationEndpoints.cs) |
| GET | `/api/v1/merchants/{merchantId:guid}/sales` | Canonical | รายการ Sale ของ Merchant | policy `admin` · permission `merchant.view` | [CanonicalMerchantConfigurationEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalMerchantConfigurationEndpoints.cs) |
| POST | `/api/v1/merchants/{merchantId:guid}/sales` | Canonical | สร้าง Sale ของ Merchant | policy `admin` · permission `merchant.manage` · CSRF filter | [CanonicalMerchantConfigurationEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalMerchantConfigurationEndpoints.cs) |
| PATCH | `/api/v1/merchants/{merchantId:guid}/sales/{saleId:guid}` | Canonical | แก้ไข Sale ของ Merchant | policy `admin` · permission `merchant.manage` · CSRF filter | [CanonicalMerchantConfigurationEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalMerchantConfigurationEndpoints.cs) |
| GET | `/api/v1/payment-providers` | Canonical | รายการ Payment Provider | policy `admin` · permission `settings.manage` | [CanonicalProviderConfigurationEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalProviderConfigurationEndpoints.cs) |
| GET | `/api/v1/payment-providers/{providerId:guid}/methods` | Canonical | รายการ method ของ Payment Provider | policy `admin` · permission `settings.manage` | [CanonicalProviderConfigurationEndpoints.cs](../../src/Api/Api/ControlPlane/CanonicalProviderConfigurationEndpoints.cs) |

## Admin และ merchant identity — 49 endpoints

| Method | fullPath | ประเภท | หน้าที่ | caller / auth policy | source |
| --- | --- | --- | --- | --- | --- |
| GET | `/api/v1/admins` | Current | รายการบัญชีผู้ดูแลระบบ | policy `admin` · permission `user.view` | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/admins` | Current | สร้าง Scoped Microsoft admin แบบ pre-bound | policy `admin` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/admins/auth/logout` | Current | ออกจากระบบเครื่องนี้ | สาธารณะ · OIDC/OAuth entry point (metadata `AllowAnonymous`) · rate limit | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/admins/auth/logout-all` | Current | ออกจากระบบทุกเครื่อง | policy `admin` | [Program.cs](../../src/Api/Api/Program.cs) |
| GET | `/api/v1/admins/auth/{provider}/login` | Current | เริ่มเข้าสู่ระบบผู้ดูแลระบบ | สาธารณะ · OIDC/OAuth entry point (metadata `AllowAnonymous`) · rate limit | [Program.cs](../../src/Api/Api/Program.cs) |
| GET | `/api/v1/admins/me` | Current | อ่านข้อมูลผู้ดูแลระบบปัจจุบัน | policy `admin` | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/admins/merchants/users/{merchantUserId:guid}/approve` | Current | อนุมัติผู้ใช้ร้านค้าเข้าร้านค้าหนึ่ง | policy `admin` · permission `merchants.users.approve` | [Program.cs](../../src/Api/Api/Program.cs) |
| GET | `/api/v1/admins/merchants/users/{merchantUserId:guid}/registrations` | Current | ดูประวัติการลงทะเบียนของผู้ใช้ร้านค้ารายคน | policy `admin` · permission `merchants.users.view` | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/admins/merchants/users/{merchantUserId:guid}/reject` | Current | ปฏิเสธผู้ใช้ร้านค้าที่รอดำเนินการ | policy `admin` · permission `merchants.users.reject` | [Program.cs](../../src/Api/Api/Program.cs) |
| GET | `/api/v1/admins/permissions` | Current | แคตตาล็อกสิทธิ์ | policy `admin` | [Program.cs](../../src/Api/Api/Program.cs) |
| GET | `/api/v1/admins/roles` | Current | รายการบทบาท | policy `admin` | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/admins/roles` | Current | สร้างบทบาท | policy `admin` · permission `user.roles` | [Program.cs](../../src/Api/Api/Program.cs) |
| DELETE | `/api/v1/admins/roles/{code}` | Current | ลบบทบาท | policy `admin` · permission `user.roles` | [Program.cs](../../src/Api/Api/Program.cs) |
| GET | `/api/v1/admins/roles/{code}` | Current | อ่านบทบาทตามรหัส | policy `admin` | [Program.cs](../../src/Api/Api/Program.cs) |
| PUT | `/api/v1/admins/roles/{code}` | Current | แก้ไขบทบาท | policy `admin` · permission `user.roles` | [Program.cs](../../src/Api/Api/Program.cs) |
| GET | `/api/v1/admins/{id:guid}` | Current | อ่านบัญชีผู้ดูแลระบบ | policy `admin` · permission `user.view` | [Program.cs](../../src/Api/Api/Program.cs) |
| GET | `/api/v1/admins/{id:guid}/effective-permissions` | Current | อ่านสิทธิ์ที่มีผลจริงของผู้ดูแลระบบ | policy `admin` · permission `user.view` | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/admins/{id:guid}/merchants` | Current | มอบสิทธิ์ร้านค้าให้ผู้ดูแลระบบ | policy `admin` | [Program.cs](../../src/Api/Api/Program.cs) |
| DELETE | `/api/v1/admins/{id:guid}/merchants/{merchantId:guid}` | Current | ถอนสิทธิ์ร้านค้าจากผู้ดูแลระบบ | policy `admin` | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/admins/{id:guid}/reactivate` | Current | เปิดใช้งานผู้ดูแลระบบที่ถูกระงับ | policy `admin` | [Program.cs](../../src/Api/Api/Program.cs) |
| PUT | `/api/v1/admins/{id:guid}/roles` | Current | กำหนดบทบาทของผู้ดูแลระบบ | policy `admin` · permission `user.roles` | [Program.cs](../../src/Api/Api/Program.cs) |
| GET | `/api/v1/admins/{id:guid}/sessions` | Current | รายการ session ของผู้ดูแลระบบ | policy `admin` | [Program.cs](../../src/Api/Api/Program.cs) |
| DELETE | `/api/v1/admins/{id:guid}/sessions/{sessionId:guid}` | Current | เพิกถอน session ของผู้ดูแลระบบ | policy `admin` | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/admins/{id:guid}/suspend` | Current | ระงับใช้งานผู้ดูแลระบบ | policy `admin` | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/admins/{id:guid}/tier` | Current | เลื่อนหรือลด tier ของผู้ดูแลระบบ | policy `admin` | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/merchants` | Current | Provision ร้านค้าใหม่ | policy `admin` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/merchants/auth/logout` | Current | ออกจากระบบเครื่องนี้ | สาธารณะ · OIDC/OAuth entry point (metadata `AllowAnonymous`) · CSRF filter · rate limit | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/merchants/auth/logout-all` | Current | ออกจากระบบทุกเครื่อง | policy `merchant-user` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |
| GET | `/api/v1/merchants/auth/{provider}/login` | Current | เริ่มเข้าสู่ระบบผู้ใช้ร้านค้า | สาธารณะ · OIDC/OAuth entry point (metadata `AllowAnonymous`) · rate limit | [Program.cs](../../src/Api/Api/Program.cs) |
| GET | `/api/v1/merchants/users` | Current | รายการผู้ใช้ร้านค้า | policy `dual-console` · audience permission: admin `merchants.users.view` / merchant `users.view` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/merchants/users/invitations` | Current | เชิญผู้ใช้เข้าร้านค้า | policy `merchant-user` · permission `users.manage` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |
| DELETE | `/api/v1/merchants/users/invitations/{invitationId:guid}` | Current | เพิกถอนคำเชิญผู้ใช้ร้านค้า | policy `merchant-user` · permission `users.manage` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |
| GET | `/api/v1/merchants/users/me` | Current | อ่านข้อมูลผู้ใช้ร้านค้าปัจจุบัน | policy `merchant-user` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |
| GET | `/api/v1/merchants/users/permissions` | Current | แคตตาล็อกสิทธิ์ของ MerchantUser | policy `merchant-user` · permission `roles.view` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/merchants/users/register` | Current | ส่งคำขอลงทะเบียนผู้ใช้ร้านค้า | ผู้สมัคร · signed registration ticket (metadata `AllowAnonymous`) · rate limit | [Program.cs](../../src/Api/Api/Program.cs) |
| GET | `/api/v1/merchants/users/roles` | Current | รายการบทบาทผู้ใช้ร้านค้า | policy `merchant-user` · permission `roles.view` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/merchants/users/roles` | Current | สร้างบทบาทผู้ใช้ร้านค้า | policy `merchant-user` · permission `roles.manage` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |
| DELETE | `/api/v1/merchants/users/roles/{code}` | Current | ลบบทบาทผู้ใช้ร้านค้า | policy `merchant-user` · permission `roles.manage` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |
| GET | `/api/v1/merchants/users/roles/{code}` | Current | อ่านบทบาทผู้ใช้ร้านค้าตามรหัส | policy `merchant-user` · permission `roles.view` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |
| PUT | `/api/v1/merchants/users/roles/{code}` | Current | แก้ไขบทบาทผู้ใช้ร้านค้า | policy `merchant-user` · permission `roles.manage` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |
| GET | `/api/v1/merchants/users/{merchantUserId:guid}` | Current | อ่านผู้ใช้ร้านค้า | policy `dual-console` · audience permission: admin `merchants.users.view` / merchant `users.view` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |
| PUT | `/api/v1/merchants/users/{merchantUserId:guid}` | Current | แก้ไขข้อมูลผู้ใช้ร้านค้า | policy `merchant-user` · permission `users.manage` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/merchants/users/{merchantUserId:guid}/approve` | Current | อนุมัติผู้ใช้ร้านค้า | policy `merchant-user` · permission `users.manage` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |
| GET | `/api/v1/merchants/users/{merchantUserId:guid}/edit` | Current | อ่านข้อมูลผู้ใช้ร้านค้าสำหรับแก้ไข | policy `merchant-user` · permission `users.manage` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/merchants/users/{merchantUserId:guid}/reactivate` | Current | เปิดใช้งานผู้ใช้ร้านค้าอีกครั้ง | policy `merchant-user` · permission `users.manage` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/merchants/users/{merchantUserId:guid}/reject` | Current | ปฏิเสธผู้ใช้ร้านค้า | policy `merchant-user` · permission `users.manage` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |
| PUT | `/api/v1/merchants/users/{merchantUserId:guid}/roles` | Current | กำหนดบทบาทของผู้ใช้ร้านค้า | policy `merchant-user` · permission `users.roles` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |
| POST | `/api/v1/merchants/users/{merchantUserId:guid}/suspend` | Current | ระงับผู้ใช้ร้านค้า | policy `merchant-user` · permission `users.manage` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |
| GET | `/api/v1/merchants/{code}` | Current | อ่านข้อมูลร้านค้าตามรหัส | policy `admin` · permission `merchant.view` · CSRF filter | [Program.cs](../../src/Api/Api/Program.cs) |

## Admin control และ merchant console — 60 endpoints

| Method | fullPath | ประเภท | หน้าที่ | caller / auth policy | source |
| --- | --- | --- | --- | --- | --- |
| GET | `/api/v1/merchants` | Compatibility | รายการร้านค้า | policy `admin` · permission `merchant.view` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| PUT | `/api/v1/merchants/{merchantId:guid}` | Compatibility | แก้ไขร้านค้า | policy `admin` · permission `merchant.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| GET | `/api/v1/merchants/{merchantId:guid}/permissions` | Current | แคตตาล็อกสิทธิ์ฝั่งร้านค้า | policy `admin` · permission `merchants.roles.view` · CSRF filter | [AdminMerchantIdentityEndpoints.cs](../../src/Api/Api/ControlPlane/AdminMerchantIdentityEndpoints.cs) |
| POST | `/api/v1/merchants/{merchantId:guid}/reactivate` | Compatibility | เปิดใช้งานร้านค้าอีกครั้งใน Admin scope | policy `admin` · permission `merchant.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| GET | `/api/v1/merchants/{merchantId:guid}/roles` | Current | รายการบทบาทของร้านค้า | policy `admin` · permission `merchants.roles.view` · CSRF filter | [AdminMerchantIdentityEndpoints.cs](../../src/Api/Api/ControlPlane/AdminMerchantIdentityEndpoints.cs) |
| POST | `/api/v1/merchants/{merchantId:guid}/roles` | Current | สร้างบทบาทของร้านค้า | policy `admin` · permission `merchants.roles.manage` · CSRF filter | [AdminMerchantIdentityEndpoints.cs](../../src/Api/Api/ControlPlane/AdminMerchantIdentityEndpoints.cs) |
| DELETE | `/api/v1/merchants/{merchantId:guid}/roles/{code}` | Current | ลบบทบาทของร้านค้า | policy `admin` · permission `merchants.roles.manage` · CSRF filter | [AdminMerchantIdentityEndpoints.cs](../../src/Api/Api/ControlPlane/AdminMerchantIdentityEndpoints.cs) |
| GET | `/api/v1/merchants/{merchantId:guid}/roles/{code}` | Current | อ่านบทบาทของร้านค้า | policy `admin` · permission `merchants.roles.view` · CSRF filter | [AdminMerchantIdentityEndpoints.cs](../../src/Api/Api/ControlPlane/AdminMerchantIdentityEndpoints.cs) |
| PUT | `/api/v1/merchants/{merchantId:guid}/roles/{code}` | Current | แก้ไขบทบาทของร้านค้า | policy `admin` · permission `merchants.roles.manage` · CSRF filter | [AdminMerchantIdentityEndpoints.cs](../../src/Api/Api/ControlPlane/AdminMerchantIdentityEndpoints.cs) |
| POST | `/api/v1/merchants/{merchantId:guid}/suspend` | Compatibility | ระงับร้านค้าใน Admin scope | policy `admin` · permission `merchant.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| POST | `/api/v1/merchants/{merchantId:guid}/user-invitations` | Current | เชิญผู้ใช้เข้าร้านค้าโดย Admin | policy `admin` · permission `merchants.users.manage` · CSRF filter | [AdminMerchantIdentityEndpoints.cs](../../src/Api/Api/ControlPlane/AdminMerchantIdentityEndpoints.cs) |
| PUT | `/api/v1/merchants/{merchantId:guid}/users/{merchantUserId:guid}` | Current | แก้ไขผู้ใช้ร้านค้าโดย Admin | policy `admin` · permission `merchants.users.manage` · CSRF filter | [AdminMerchantIdentityEndpoints.cs](../../src/Api/Api/ControlPlane/AdminMerchantIdentityEndpoints.cs) |
| GET | `/api/v1/merchants/{merchantId:guid}/users/{merchantUserId:guid}/edit` | Current | อ่านข้อมูลผู้ใช้ร้านค้าสำหรับแก้ไขโดย Admin | policy `admin` · permission `merchants.users.manage` · CSRF filter | [AdminMerchantIdentityEndpoints.cs](../../src/Api/Api/ControlPlane/AdminMerchantIdentityEndpoints.cs) |
| PUT | `/api/v1/merchants/{merchantId:guid}/users/{merchantUserId:guid}/roles` | Current | กำหนดบทบาทให้ผู้ใช้ร้านค้าโดย Admin | policy `admin` · permission `merchants.roles.manage` · CSRF filter | [AdminMerchantIdentityEndpoints.cs](../../src/Api/Api/ControlPlane/AdminMerchantIdentityEndpoints.cs) |
| GET | `/api/v1/originators` | Compatibility | รายการ Originator | policy `admin` · permission `merchant.view` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| POST | `/api/v1/originators` | Compatibility | สร้าง Originator | policy `admin` · permission `merchant.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| DELETE | `/api/v1/originators/{originatorId:guid}` | Compatibility | ลบ Originator | policy `admin` · permission `merchant.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| GET | `/api/v1/originators/{originatorId:guid}` | Compatibility | อ่าน Originator | policy `admin` · permission `merchant.view` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| PUT | `/api/v1/originators/{originatorId:guid}` | Compatibility | แก้ไข Originator | policy `admin` · permission `merchant.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| POST | `/api/v1/originators/{originatorId:guid}/disable` | Compatibility | ปิดใช้งาน Originator | policy `admin` · permission `merchant.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| POST | `/api/v1/originators/{originatorId:guid}/enable` | Compatibility | เปิดใช้งาน Originator | policy `admin` · permission `merchant.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| GET | `/api/v1/payments/merchant-settings/{merchantId:guid}` | Compatibility | อ่านสภาพแวดล้อมการชำระเงินของร้านค้า | policy `admin` · permission `settings.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| POST | `/api/v1/payments/merchant-settings/{merchantId:guid}/environment-change-requests` | Compatibility | ขอเปลี่ยนสภาพแวดล้อมการชำระเงินของร้านค้า | policy `admin` · permission `settings.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| GET | `/api/v1/payments/merchant-settings/{merchantId:guid}/simple-routing` | Compatibility | อ่าน simple routing ของร้านค้า | policy `admin` · permission `settings.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| PUT | `/api/v1/payments/merchant-settings/{merchantId:guid}/simple-routing` | Compatibility | กำหนด simple routing ของร้านค้า | policy `admin` · permission `settings.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| GET | `/api/v1/payments/merchants/{merchantId:guid}/methods` | Compatibility | รายการ effective method ของร้านค้า | policy `admin` · permission `merchant.view` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| GET | `/api/v1/payments/merchants/{merchantId:guid}/methods/{method}` | Compatibility | อ่าน payment method policy ของร้านค้า | policy `admin` · permission `merchant.view` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| PUT | `/api/v1/payments/merchants/{merchantId:guid}/methods/{method}` | Compatibility | กำหนด payment method policy ของร้านค้า | policy `admin` · permission `merchant.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| GET | `/api/v1/payments/merchants/{merchantId:guid}/users/{userId:guid}/methods` | Compatibility | รายการ method ของ Merchant User | policy `admin` · permission `merchants.users.view` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| GET | `/api/v1/payments/merchants/{merchantId:guid}/users/{userId:guid}/methods/{method}` | Compatibility | อ่าน method policy ของ Merchant User | policy `admin` · permission `merchants.users.view` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| PUT | `/api/v1/payments/merchants/{merchantId:guid}/users/{userId:guid}/methods/{method}` | Compatibility | กำหนด method policy ของ Merchant User | policy `admin` · permission `merchants.users.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| GET | `/api/v1/payments/merchants/{merchantId:guid}/users/{userId:guid}/methods/{method}/options` | Compatibility | รายการ effective option ของ Merchant User | policy `admin` · permission `merchants.users.view` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| GET | `/api/v1/payments/merchants/{merchantId:guid}/users/{userId:guid}/methods/{method}/resolution` | Compatibility | ตรวจ effective method ของ Merchant User | policy `admin` · permission `merchants.users.view` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| GET | `/api/v1/payments/methods` | Compatibility | Payment methods ที่ผู้ใช้ปัจจุบันใช้ได้ | policy `merchant-user` · permission `payment.view` | [PaymentCapabilityEndpoints.cs](../../src/Api/Api/Payments/PaymentCapabilityEndpoints.cs) |
| GET | `/api/v1/payments/methods/{method}` | Compatibility | อ่านสถานะ payment method | policy `admin` · permission `merchant.view` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| PUT | `/api/v1/payments/methods/{method}` | Compatibility | กำหนดสถานะ payment method | policy `admin` · permission `merchant.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| GET | `/api/v1/payments/methods/{method}/options` | Compatibility | Payment options ที่ผู้ใช้ปัจจุบันใช้ได้ | policy `merchant-user` · permission `payment.view` | [PaymentCapabilityEndpoints.cs](../../src/Api/Api/Payments/PaymentCapabilityEndpoints.cs) |
| GET | `/api/v1/payments/providers/{providerCode}` | Compatibility | อ่านสถานะ payment provider | policy `admin` · permission `merchant.view` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| PUT | `/api/v1/payments/providers/{providerCode}` | Compatibility | กำหนดสถานะ payment provider | policy `admin` · permission `merchant.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| GET | `/api/v1/payments/providers/{providerCode}/methods/{method}` | Compatibility | อ่าน method ของ payment provider | policy `admin` · permission `merchant.view` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| PUT | `/api/v1/payments/providers/{providerCode}/methods/{method}` | Compatibility | กำหนด method ของ payment provider | policy `admin` · permission `merchant.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| GET | `/api/v1/payments/providers/{providerCode}/methods/{method}/options/{option}` | Compatibility | อ่าน option ของ provider method | policy `admin` · permission `merchant.view` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| PUT | `/api/v1/payments/providers/{providerCode}/methods/{method}/options/{option}` | Compatibility | กำหนด option ของ provider method | policy `admin` · permission `merchant.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| GET | `/api/v1/payments/psp-connections` | Compatibility | รายการ PSP connection | policy `admin` · permission `settings.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| POST | `/api/v1/payments/psp-connections` | Compatibility | สร้าง PSP connection | policy `admin` · permission `settings.manage`, `merchant.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| GET | `/api/v1/payments/psp-connections/{connectionId:guid}` | Compatibility | อ่าน PSP connection | policy `admin` · permission `settings.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| PUT | `/api/v1/payments/psp-connections/{connectionId:guid}` | Compatibility | แก้ไข PSP connection | policy `admin` · permission `settings.manage`, `merchant.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| POST | `/api/v1/payments/psp-connections/{connectionId:guid}/credential-change-requests` | Compatibility | ขอเปลี่ยน PSP credential | policy `admin` · permission `settings.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| POST | `/api/v1/payments/psp-connections/{connectionId:guid}/credential-change-requests/{approvalId:guid}/test` | Compatibility | ทดสอบ candidate credential ที่รออนุมัติ | policy `admin` · permission `settings.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| GET | `/api/v1/payments/psp-connections/{connectionId:guid}/methods/{method}` | Compatibility | อ่าน method ของ PSP connection | policy `admin` · permission `merchant.view` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| PUT | `/api/v1/payments/psp-connections/{connectionId:guid}/methods/{method}` | Compatibility | กำหนด method ของ PSP connection | policy `admin` · permission `merchant.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| GET | `/api/v1/payments/psp-connections/{connectionId:guid}/methods/{method}/options/{option}` | Compatibility | อ่าน option ของ PSP connection | policy `admin` · permission `merchant.view` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| PUT | `/api/v1/payments/psp-connections/{connectionId:guid}/methods/{method}/options/{option}` | Compatibility | กำหนด option ของ PSP connection | policy `admin` · permission `merchant.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| POST | `/api/v1/payments/psp-connections/{connectionId:guid}/test` | Compatibility | ทดสอบ PSP connection | policy `admin` · permission `settings.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| GET | `/api/v1/payments/routing-rulesets` | Compatibility | รายการ PSP routing ruleset | policy `admin` · permission `settings.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| POST | `/api/v1/payments/routing-rulesets` | Compatibility | สร้าง PSP routing draft | policy `admin` · permission `settings.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| DELETE | `/api/v1/payments/routing-rulesets/{rulesetId:guid}` | Compatibility | ลบ PSP routing draft | policy `admin` · permission `settings.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| GET | `/api/v1/payments/routing-rulesets/{rulesetId:guid}` | Compatibility | อ่าน PSP routing ruleset | policy `admin` · permission `settings.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| PUT | `/api/v1/payments/routing-rulesets/{rulesetId:guid}` | Compatibility | แทนที่ PSP routing draft | policy `admin` · permission `settings.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |
| POST | `/api/v1/payments/routing-rulesets/{rulesetId:guid}/activation-requests` | Compatibility | ขอเปิดใช้ PSP routing ruleset | policy `admin` · permission `settings.manage` · CSRF filter | [AdminControlEndpoints.cs](../../src/Api/Api/ControlPlane/AdminControlEndpoints.cs) |

## Governance และ audit — 6 endpoints

| Method | fullPath | ประเภท | หน้าที่ | caller / auth policy | source |
| --- | --- | --- | --- | --- | --- |
| GET | `/api/v1/approvals` | Current | รายการคำขอ maker-checker | policy `admin` · permission `settings.manage` | [GovernanceEndpoints.cs](../../src/Api/Api/Governance/GovernanceEndpoints.cs) |
| GET | `/api/v1/approvals/{approvalId:guid}` | Current | รายละเอียดคำขอ maker-checker | policy `admin` · permission `settings.manage` | [GovernanceEndpoints.cs](../../src/Api/Api/Governance/GovernanceEndpoints.cs) |
| POST | `/api/v1/approvals/{approvalId:guid}/approve` | Current | อนุมัติคำขอ maker-checker | policy `admin` · permission `settings.manage` · CSRF filter | [GovernanceEndpoints.cs](../../src/Api/Api/Governance/GovernanceEndpoints.cs) |
| POST | `/api/v1/approvals/{approvalId:guid}/reject` | Current | ปฏิเสธคำขอ maker-checker | policy `admin` · permission `settings.manage` · CSRF filter | [GovernanceEndpoints.cs](../../src/Api/Api/Governance/GovernanceEndpoints.cs) |
| GET | `/api/v1/audits` | Current | รายการ audit แบบ append-only | policy `admin` · permission `audit.view` | [GovernanceEndpoints.cs](../../src/Api/Api/Governance/GovernanceEndpoints.cs) |
| GET | `/api/v1/audits/{auditId:guid}` | Current | รายละเอียด audit แบบ append-only | policy `admin` · permission `audit.view` | [GovernanceEndpoints.cs](../../src/Api/Api/Governance/GovernanceEndpoints.cs) |

## API clients — 7 endpoints

| Method | fullPath | ประเภท | หน้าที่ | caller / auth policy | source |
| --- | --- | --- | --- | --- | --- |
| GET | `/api/v1/api-clients` | Current | รายการ API client | policy `admin` · permission `apikey.manage` · CSRF filter | [ApiClientEndpoints.cs](../../src/Api/Api/Iam/ApiClientEndpoints.cs) |
| POST | `/api/v1/api-clients` | Current | สร้าง API client | policy `admin` · permission `apikey.manage` · CSRF filter | [ApiClientEndpoints.cs](../../src/Api/Api/Iam/ApiClientEndpoints.cs) |
| POST | `/api/v1/api-clients/secrets/{ticketId}/reveal` | Current | เปิดดู client secret หนึ่งครั้ง | policy `admin` · permission `apikey.manage` · CSRF filter | [ApiClientEndpoints.cs](../../src/Api/Api/Iam/ApiClientEndpoints.cs) |
| GET | `/api/v1/api-clients/{clientId:guid}` | Current | อ่าน API client | policy `admin` · permission `apikey.manage` · CSRF filter | [ApiClientEndpoints.cs](../../src/Api/Api/Iam/ApiClientEndpoints.cs) |
| PUT | `/api/v1/api-clients/{clientId:guid}` | Current | แก้ไข API client | policy `admin` · permission `apikey.manage` · CSRF filter | [ApiClientEndpoints.cs](../../src/Api/Api/Iam/ApiClientEndpoints.cs) |
| POST | `/api/v1/api-clients/{clientId:guid}/revoke` | Current | เพิกถอน API client | policy `admin` · permission `apikey.manage` · CSRF filter | [ApiClientEndpoints.cs](../../src/Api/Api/Iam/ApiClientEndpoints.cs) |
| POST | `/api/v1/api-clients/{clientId:guid}/secret-rotation-requests` | Current | ขอหมุน client secret | policy `admin` · permission `apikey.manage` · CSRF filter | [ApiClientEndpoints.cs](../../src/Api/Api/Iam/ApiClientEndpoints.cs) |

## Notification delivery — 15 endpoints

| Method | fullPath | ประเภท | หน้าที่ | caller / auth policy | source |
| --- | --- | --- | --- | --- | --- |
| GET | `/api/v1/notifications/deliveries` | Current | รายการ notification delivery | policy `admin` · permission `settings.manage` · CSRF filter | [DeliveryEndpoints.cs](../../src/Api/Api/Notifications/DeliveryEndpoints.cs) |
| GET | `/api/v1/notifications/deliveries/{deliveryId:guid}` | Current | อ่าน notification delivery | policy `admin` · permission `settings.manage` · CSRF filter | [DeliveryEndpoints.cs](../../src/Api/Api/Notifications/DeliveryEndpoints.cs) |
| GET | `/api/v1/notifications/rules` | Current | รายการกฎการแจ้งเตือน | policy `admin` · permission `settings.manage` · CSRF filter | [DeliveryEndpoints.cs](../../src/Api/Api/Notifications/DeliveryEndpoints.cs) |
| POST | `/api/v1/notifications/rules` | Current | สร้างกฎการแจ้งเตือน | policy `admin` · permission `settings.manage` · CSRF filter | [DeliveryEndpoints.cs](../../src/Api/Api/Notifications/DeliveryEndpoints.cs) |
| DELETE | `/api/v1/notifications/rules/{ruleId:guid}` | Current | ลบกฎการแจ้งเตือน | policy `admin` · permission `settings.manage` · CSRF filter | [DeliveryEndpoints.cs](../../src/Api/Api/Notifications/DeliveryEndpoints.cs) |
| GET | `/api/v1/notifications/rules/{ruleId:guid}` | Current | อ่านกฎการแจ้งเตือน | policy `admin` · permission `settings.manage` · CSRF filter | [DeliveryEndpoints.cs](../../src/Api/Api/Notifications/DeliveryEndpoints.cs) |
| PUT | `/api/v1/notifications/rules/{ruleId:guid}` | Current | แก้ไขกฎการแจ้งเตือน | policy `admin` · permission `settings.manage` · CSRF filter | [DeliveryEndpoints.cs](../../src/Api/Api/Notifications/DeliveryEndpoints.cs) |
| GET | `/api/v1/webhooks/deliveries` | Current | รายการ outbound webhook delivery | policy `admin` · permission `settings.manage` · CSRF filter | [DeliveryEndpoints.cs](../../src/Api/Api/Notifications/DeliveryEndpoints.cs) |
| GET | `/api/v1/webhooks/deliveries/{deliveryId:guid}` | Current | อ่าน outbound webhook delivery | policy `admin` · permission `settings.manage` · CSRF filter | [DeliveryEndpoints.cs](../../src/Api/Api/Notifications/DeliveryEndpoints.cs) |
| POST | `/api/v1/webhooks/deliveries/{deliveryId:guid}/replay` | Current | ส่ง outbound webhook ซ้ำ | policy `admin` · permission `settings.manage` · CSRF filter | [DeliveryEndpoints.cs](../../src/Api/Api/Notifications/DeliveryEndpoints.cs) |
| GET | `/api/v1/webhooks/endpoints` | Current | รายการ outbound webhook endpoint | policy `admin` · permission `settings.manage` · CSRF filter | [DeliveryEndpoints.cs](../../src/Api/Api/Notifications/DeliveryEndpoints.cs) |
| POST | `/api/v1/webhooks/endpoints` | Current | สร้าง outbound webhook endpoint | policy `admin` · permission `settings.manage` · CSRF filter | [DeliveryEndpoints.cs](../../src/Api/Api/Notifications/DeliveryEndpoints.cs) |
| DELETE | `/api/v1/webhooks/endpoints/{endpointId:guid}` | Current | ลบ outbound webhook endpoint | policy `admin` · permission `settings.manage` · CSRF filter | [DeliveryEndpoints.cs](../../src/Api/Api/Notifications/DeliveryEndpoints.cs) |
| GET | `/api/v1/webhooks/endpoints/{endpointId:guid}` | Current | อ่าน outbound webhook endpoint | policy `admin` · permission `settings.manage` · CSRF filter | [DeliveryEndpoints.cs](../../src/Api/Api/Notifications/DeliveryEndpoints.cs) |
| PUT | `/api/v1/webhooks/endpoints/{endpointId:guid}` | Current | แก้ไข outbound webhook endpoint | policy `admin` · permission `settings.manage` · CSRF filter | [DeliveryEndpoints.cs](../../src/Api/Api/Notifications/DeliveryEndpoints.cs) |

## Canonical notifications — 9 endpoints

| Method | fullPath | ประเภท | หน้าที่ | caller / auth policy | source |
| --- | --- | --- | --- | --- | --- |
| GET | `/api/v1/audit-logs` | Canonical | ค้น audit logs | policy `admin` · permission `audit.view` | [CanonicalNotificationEndpoints.cs](../../src/Api/Api/Notifications/CanonicalNotificationEndpoints.cs) |
| GET | `/api/v1/merchants/{merchantId:guid}/event-endpoint` | Canonical | อ่าน Merchant event endpoint | policy `admin` · permission `settings.manage` | [CanonicalNotificationEndpoints.cs](../../src/Api/Api/Notifications/CanonicalNotificationEndpoints.cs) |
| PUT | `/api/v1/merchants/{merchantId:guid}/event-endpoint` | Canonical | กำหนดหรือปิด Merchant event endpoint | policy `admin` · permission `settings.manage` · CSRF filter | [CanonicalNotificationEndpoints.cs](../../src/Api/Api/Notifications/CanonicalNotificationEndpoints.cs) |
| GET | `/api/v1/notification-deliveries/{deliveryId:guid}` | Canonical | อ่านสถานะ Notification delivery | policy `admin` · permission `settings.manage` | [CanonicalNotificationEndpoints.cs](../../src/Api/Api/Notifications/CanonicalNotificationEndpoints.cs) |
| GET | `/api/v1/notification-deliveries/{deliveryId:guid}/attempts` | Canonical | รายการความพยายามส่ง Notification | policy `admin` · permission `settings.manage` | [CanonicalNotificationEndpoints.cs](../../src/Api/Api/Notifications/CanonicalNotificationEndpoints.cs) |
| POST | `/api/v1/notification-deliveries/{deliveryId:guid}/retries` | Canonical | ลองส่ง Notification ใหม่ | policy `admin` · permission `settings.manage` · CSRF filter | [CanonicalNotificationEndpoints.cs](../../src/Api/Api/Notifications/CanonicalNotificationEndpoints.cs) |
| GET | `/api/v1/notifications` | Canonical | ค้นหาผลการแจ้งเตือน | policy `admin` · permission `settings.manage` | [CanonicalNotificationEndpoints.cs](../../src/Api/Api/Notifications/CanonicalNotificationEndpoints.cs) |
| GET | `/api/v1/notifications/{notificationId:guid}` | Canonical | อ่านผลการแจ้งเตือน | policy `admin` · permission `settings.manage` | [CanonicalNotificationEndpoints.cs](../../src/Api/Api/Notifications/CanonicalNotificationEndpoints.cs) |
| POST | `/api/v1/webhooks/notifications/{providerCode}` | Canonical | Receipt จากผู้ให้บริการ Notification | Notification provider callback · `X-Signature` · metadata `AllowAnonymous` | [CanonicalNotificationEndpoints.cs](../../src/Api/Api/Notifications/CanonicalNotificationEndpoints.cs) |

## Inbound PSP webhooks — 2 endpoints

| Method | fullPath | ประเภท | หน้าที่ | caller / auth policy | source |
| --- | --- | --- | --- | --- | --- |
| GET | `/api/v1/webhooks/inbound-events` | Integration | รายการ PSP callback ที่ผ่านการลดข้อมูลอ่อนไหวแล้ว | policy `admin` · permission `audit.view` · CSRF filter | [InboundWebhookEndpoints.cs](../../src/Api/Api/Webhooks/InboundWebhookEndpoints.cs) |
| GET | `/api/v1/webhooks/inbound-events/{eventId:guid}` | Integration | รายละเอียด PSP callback โดยไม่คืน raw payload หรือลายเซ็น | policy `admin` · permission `audit.view` · CSRF filter | [InboundWebhookEndpoints.cs](../../src/Api/Api/Webhooks/InboundWebhookEndpoints.cs) |

## Reporting — 7 endpoints

| Method | fullPath | ประเภท | หน้าที่ | caller / auth policy | source |
| --- | --- | --- | --- | --- | --- |
| GET | `/api/v1/payments/transactions` | Current | รายการธุรกรรมจาก Order และ PaymentSession | policy `admin` · permission `txn.view` | [AdminReportingEndpoints.cs](../../src/Api/Api/Reporting/AdminReportingEndpoints.cs) |
| GET | `/api/v1/payments/transactions/export` | Current | ส่งออกธุรกรรมตาม query เดียวกับหน้าจอ | policy `admin` · permission `txn.export` | [AdminReportingEndpoints.cs](../../src/Api/Api/Reporting/AdminReportingEndpoints.cs) |
| GET | `/api/v1/payments/transactions/{paymentSessionId:guid}` | Current | รายละเอียดธุรกรรมและ lifecycle ที่ backend บันทึกจริง | policy `admin` · permission `txn.view` | [AdminReportingEndpoints.cs](../../src/Api/Api/Reporting/AdminReportingEndpoints.cs) |
| GET | `/api/v1/reports/dashboard` | Current | ข้อมูลสรุป dashboard จากธุรกรรมจริง | policy `admin` · permission `txn.view` | [AdminReportingEndpoints.cs](../../src/Api/Api/Reporting/AdminReportingEndpoints.cs) |
| GET | `/api/v1/reports/operations` | Current | รายงานปฏิบัติการจากธุรกรรมจริง | policy `admin` · permission `txn.view` | [AdminReportingEndpoints.cs](../../src/Api/Api/Reporting/AdminReportingEndpoints.cs) |
| GET | `/api/v1/reports/operations/export` | Current | ส่งออกรายงานปฏิบัติการ | policy `admin` · permission `txn.export` | [AdminReportingEndpoints.cs](../../src/Api/Api/Reporting/AdminReportingEndpoints.cs) |
| GET | `/api/v1/reports/reconciliation` | Current | รายงาน reconciliation | policy `dual-console` · permission `payment.view` | [Program.cs](../../src/Api/Api/Program.cs) |

## Infrastructure และ health — 2 endpoints

| Method | fullPath | ประเภท | หน้าที่ | caller / auth policy | source |
| --- | --- | --- | --- | --- | --- |
| GET | `/health/live` | Infrastructure | ตรวจว่า process ยังทำงาน | anonymous · metadata `AllowAnonymous` | [HealthChecks.cs](../../src/Api/BuildingBlocks.Web/HealthChecks.cs) |
| GET | `/health/ready` | Infrastructure | ตรวจ readiness ของ dependency | anonymous · metadata `AllowAnonymous` | [HealthChecks.cs](../../src/Api/BuildingBlocks.Web/HealthChecks.cs) |

## OpenAPI และ Scalar (development only) — 2 route templates

สองรายการนี้ถูก map เฉพาะเมื่อ `app.Environment.IsDevelopment()` เป็นจริง; ใช้ named audience documents จาก `OpenApiDocuments` และไม่อยู่ในยอด explicit 274 operations.

| Method | fullPath | ประเภท | หน้าที่ | caller / auth policy | source |
| --- | --- | --- | --- | --- | --- |
| GET | `/openapi/{documentName}.json` | Infrastructure | เสิร์ฟ OpenAPI document ที่เลือกใน development รวม combined `v1` เพื่อ compatibility | anonymous ใน development | [Program.cs](../../src/Api/Api/Program.cs) |
| GET | `/scalar/{documentName?}` | Infrastructure | เปิด Scalar reference UI และ named documents `merchant`, `admin`, `integration` ใน development | anonymous ใน development | [Program.cs](../../src/Api/Api/Program.cs) |

## OIDC callback ที่ middleware จัดการ — 2 default paths, ไม่ใช่ explicit map

เมื่อ provider `microsoft` ถูก configure, OIDC middleware ใช้ callback path ตาม options และสร้าง session ใน callback handler; ตารางนี้จึงเป็น framework callback inventory แยกจาก explicit endpoint count. Admin production ถูกบังคับให้ใช้ `/api/v1/admins/auth/microsoft/callback` โดย validation ใน `Program.cs`; ส่วน Merchant callback อ่านจาก provider configuration และอาจเปลี่ยนตาม deployment.

| Method ที่ทดสอบใน source | fullPath | หน้าที่ | caller / auth policy | source |
| --- | --- | --- | --- | --- |
| GET | `/api/v1/admins/auth/microsoft/callback` | รับ workforce OIDC callback เพื่อสร้าง Admin session cookie; actual handler คือ middleware | Microsoft workforce OIDC provider · middleware state/nonce/issuer validation | [appsettings.json](../../src/Api/appsettings.json), [OidcAuthentication.cs](../../src/Api/Api/Admins/OidcAuthentication.cs) |
| GET | `/api/v1/merchants/auth/microsoft/callback` | รับ merchant OIDC callback เพื่อสร้าง MerchantUser session หรือ registration ticket; actual handler คือ middleware | Microsoft merchant OIDC provider · middleware state/nonce/issuer validation | [appsettings.json](../../src/Api/appsettings.json), [UserOidcAuthentication.cs](../../src/Api/Api/Merchants/UserOidcAuthentication.cs) |

Fallback GET routes `/api/v1/auth/employees/callback` และ `/api/v1/auth/agents/callback` ที่ประกาศใน `IdentityAccessEndpoints.cs` อยู่ในตาราง explicit ด้านบนแล้ว; มันเป็น fallback endpoint สำหรับการไหลที่ไม่ได้ถูก middleware callback path ทับ ไม่ใช่การนับซ้ำกับสอง default paths นี้.

## Evidence และข้อจำกัดของ inventory

- Composition root เรียก `app.MapIdentityAccessEndpoints()`, `api.MapAgentRegistrationEndpoints()` และ module mappers ทั้งหมดจาก [Program.cs](../../src/Api/Api/Program.cs) ใต้ `var api = app.MapGroup("/api/v1")`; จึงใช้เป็นหลักฐานว่า routes ใน extension files ถูก register จริง.
- Dynamic declarations ที่ขยายคือ Governance decision, merchant status, Originator state, canonical payment-setting decision และ MerchantUser lifecycle; แต่ละรายการมี invocation ที่ให้ segment/operation name ใน source เดียวกัน.
- ไม่รวม `MapGroup` เป็น HTTP operation, ไม่รวม tests, และไม่รวม callback ที่ framework middleware สร้างเอง; route framework ที่ผู้ใช้เรียกได้แต่ไม่ได้ประกาศด้วย `MapGet` ฯลฯ ถูกแยกไว้ในสองส่วนท้าย.
- Merchant provider callback path อาจเปลี่ยนตาม configuration ใน runtime; Admin Microsoft production path ถูกบังคับคงที่ตาม `Program.cs`. Catalog นี้ระบุเฉพาะ path ที่ source/config และ tests ยืนยัน ไม่เดาเส้นทางจาก provider ภายนอก.
