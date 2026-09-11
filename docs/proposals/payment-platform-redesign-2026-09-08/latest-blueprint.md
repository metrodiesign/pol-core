# POL Platform — Blueprint ตามชุดออกแบบล่าสุด

ฉบับสำหรับลงมือรุ่นแรกอยู่ที่ [platform-restructure-v1](../../../.ai/specs/platform-restructure-v1/handoff.md) พร้อม requirements, design และ tasks ให้ใช้ defaults ใน spec นั้นเมื่อแตกต่างจากข้อเสนอหน้านี้

เอกสารนี้สรุป `01-business-overview.md` และ `04-data-model-and-migration.md` จากชุด `pol-platform-latest` เป็น target architecture สำหรับเขียน spec ก่อนแก้ระบบ โดยรักษาโครง 7 modules และไม่เพิ่ม Payment aggregate หรือโครงเดิมคู่ขนาน

**สถานะ:** ข้อเสนอปรับล่าสุด วันที่ 9 กันยายน 2026 เน้นความเรียบง่ายสำหรับทีมเล็กตามคำขอล่าสุด ยังไม่แก้ code, schema หรือข้อมูล

**แผนภาพ:** [ERD แบบย่อ 6 กลุ่ม](../../../outputs/diagrams/2026-09-08_payment-platform-latest-blueprint_v3.md) เป็นแผนที่แนวคิดทั้ง scope ไม่ใช่คำสั่งให้สร้างทุกตารางหรือทุก API ในรุ่นแรก

**ผลเทียบและตรวจ API:** [สิ่งที่เปลี่ยนจากแบบก่อน พร้อมจุดที่ต้องล็อกใน spec](latest-source-review.md)

## 1. ลำดับความน่าเชื่อถือของแบบ

1. `01-business-overview.md` และ `04-data-model-and-migration.md` ในชุดล่าสุดเป็น source หลักของ target
2. `02-activity-sequence.md` และ `03-api-endpoints.md` ขยาย flow/API แต่ห้ามขัดเจ้าของข้อมูลในข้อ 1
3. ใช้ blueprint นี้กับ latest-source-review และ ERD v3 เป็นชุดทำงานเดียว เอกสารที่ถูกแทนแล้วลบออกจากชุดทำงาน
4. Source code ใน repo ใช้บอกโครงปัจจุบันและข้อมูลที่ต้องย้าย ส่วน capability/production data ต้องพิสูจน์ก่อนเปิดใช้

Flow หลักคือ Account ที่มีสิทธิ์สร้าง Order พร้อม OrderItems → Checkout ออก PaymentLink → ลูกค้ายืนยัน → บันทึก Transaction ก่อนเรียก PSP → ตรวจผล → อัปเดต `Order.PaymentStatus` และแจ้งธุรกิจ

## 2. เจ้าของข้อมูล 7 modules

| Module | เป็นเจ้าของ | ห้ามเก็บซ้ำ |
|---|---|---|
| Account | Accounts, Employees, Agents, LoginAccounts, SystemClients/Keys, HumanSessions/RefreshTokens, AgentRegistrations และ Attempts | Role, Merchant config, Order และ Transaction |
| Access | Roles/Permissions, MerchantAccess พร้อม DataScope, AccessRoles, BranchAccess, PlatformAccess, MethodAccess และ SystemScopes | Account/Profile, Sale master และ Order ownership |
| Merchant | Merchants, BusinessType, Branches, Sales, Providers, ProviderMethods, ProviderAccounts, CredentialVersions และ PaymentSettingRequests | Account, Transaction status และเงินจริง |
| Order | Orders, OrderItems, TotalAmount, OrderStatus, PaymentStatus, owner/creator และ business metadata | PaymentLink, PSP callback และ provider credential |
| Checkout | PaymentLinks, public summary, checkout capability และ confirm coordination | Payment, CheckoutSession และ Transaction ชุดที่สอง |
| Transaction | Transactions, TransactionEvents, immutable OrderSnapshot, routing/credential pin และ PSP verification | Order lifecycle, fulfillment และ settlement |
| Notification | Notifications, NotificationDeliveries, DeliveryAttempts, TemplateVersions และ business webhook delivery | ผลสมัคร ผลชำระ และ contact ปัจจุบัน |

Platform Core มี CurrentAccount/CurrentMerchant, audit, idempotency, inbox/outbox, concurrency, error contract และ observability เป็นกลไกกลาง ไม่เป็น module เจ้าของ business state เพิ่ม

## 3. Account, OAuth และ Registration

`Accounts` เป็นรหัสกลาง มี `AccountType = EMPLOYEE | AGENT | SYSTEM` ซึ่ง server กำหนดจาก realm/flow ที่เชื่อถือได้ Profile เก็บเฉพาะ field ของชนิดนั้นและไม่มีสิทธิ์แฝง

`LoginAccounts` ใช้กับมนุษย์และ unique ด้วย `(Provider, TenantId, ExternalUserId)` โดย `ExternalUserId` คือ Entra `oid` Email/Phone เป็น contact ที่เปลี่ยนได้และไม่ใช้ auto-merge

`Accounts.DisplayName` เป็นชื่อใช้งานปัจจุบันของ POL ส่วน `LoginAccounts.DisplayName/Email` เป็นค่าที่สังเกตจาก provider ไม่ใช่ current profile อีกชุด Employees/Agents จึงไม่สร้าง CurrentName ที่แก้แข่งกันได้

EMPLOYEE ใช้ Entra Workforce และ JIT เฉพาะ tenant/เงื่อนไขพนักงานที่อนุญาต Account ใหม่ไม่เป็น Admin อัตโนมัติ AGENT ใช้ Entra External ID แต่ยังไม่มี Account จน Attempt ล่าสุดได้รับอนุมัติ

Registration เป็นข้อมูลของ Account module `AgentRegistrations` ผูก stable identity และ Merchant ตั้งแต่ต้น ส่วน `AgentRegistrationAttempts` เก็บ snapshot ต่อ Submit แบบ append-only การเปิดหน้า/แก้ draft ไม่สร้าง Attempt

Approve ต้องตรวจ current pending attempt, Sale, Home Branch, Merchant และ identity ซ้ำ แล้ว commit Account + LoginAccount + Agent + initial Access + decision + Outbox พร้อมกัน Reject ต้องมี public reason และไม่สร้าง Account

SYSTEM ใช้ OAuth `client_credentials` ส่วน `private_key_jwt` ยังเป็นข้อเสนอที่ต้องยืนยัน หากเลือกวิธีนี้ Client ถือ private key และ Platform เก็บ public key, `kid`, algorithm, validity กับ replay state ของ assertion

Client assertion ใช้พิสูจน์ SYSTEM ที่ `POST /oauth/token` ส่วน Platform JWT เป็น Access Token อายุสั้นสำหรับเรียก Business API ทั้งสองเป็น JWT คนละหน้าที่ รุ่นแรกไม่มี SYSTEM refresh token

Human ใช้ authorization code + PKCE เว็บไซต์ใช้ BFF และเก็บ token ฝั่ง server ได้ HumanSessions/RefreshToken families เก็บ opaque token hash, rotation/reuse และ revoke state แต่ Business API ตรวจ Platform JWT กลาง ไม่รับ Entra ID token เป็น business token โดยตรง

`Account.Status` ปิดสิทธิ์ทั้งบัญชี ส่วน `SystemClient.Status` ปิดเฉพาะ Client นั้น ทั้งสอง kill switch ต้องผ่านก่อนออกหรือรับ token

## 4. Access, Merchant, Sale และ Branch

`MerchantAccess` เป็นเจ้าของ DataScope ตามแบบล่าสุด `AccessRoles` และ `BranchAccess` ผูก Access เดียวกัน ห้าม union ข้าม Merchant หรือใช้ merchantId ว่างแทน Super access

`PlatformAccess` แยกสำหรับ Employee และไม่มีความหมายเป็น Merchant wildcard ส่วน SYSTEM ใช้ SystemScopes/MethodAccess ร่วมกับ MerchantAccess ของ Client

`Agents.SaleId` เป็นตำแหน่งเจ้าของ Sale ตาม HTML หนึ่ง Agent มีหนึ่ง Sale, Sale มี Home Branch หนึ่งแห่ง ณ เวลาใดเวลาหนึ่ง และ Sale/Branch ต้องอยู่ Merchant เดียวกัน

| DataScope | ความหมายเป้าหมาย |
|---|---|
| SELF | AGENT เห็น Order ที่ `OwnerSaleId` เป็น Sale ของตน EMPLOYEE ยังไม่เปิด SELF จนมีนิยามเจ้าของงาน |
| BRANCH | AGENT ใช้ Home Branch ของ Sale ส่วน EMPLOYEE ใช้ได้เมื่อกำหนดบริบทสาขาหลักชัด |
| ASSIGNED_BRANCHES | AGENT ใช้ Home Branch พร้อม BranchAccess ที่ยังมีผล EMPLOYEE ใช้เฉพาะสาขาที่มอบหมาย |
| MERCHANT | เห็นทุก Order ใน Merchant จากสิทธิ์ระดับนี้ |

Order แยก `CreatedByAccountId`, `OwnerSaleId` และ `OwnerBranchIdAtCreation` ผู้เรียกกับเจ้าของงานจึงเป็นคนละค่าได้ OwnerSale/Branch ว่างได้เมื่อ Business Handler อนุญาตโดยไม่สร้าง Sale ปลอม การย้าย Sale ไปสาขาใหม่ไม่เขียนประวัติ Order เก่า

Merchant เป็นเจ้าของ BusinessType และ provider configuration การเปลี่ยนค่าใหม่ไม่เปลี่ยน BusinessType, route, environment หรือ credential ที่ Transaction เดิมตรึงไว้

## 5. Order, Checkout และ Transaction

หนึ่ง Order อยู่หนึ่ง Merchant ใช้หนึ่ง Currency และมี OrderItem อย่างน้อยหนึ่งรายการ `SubtotalAmount = SUM(LineAmount)` และ `TotalAmount = SubtotalAmount - OrderDiscountAmount + OrderChargeAmount` ใช้ Money `decimal(19,4)` และไม่บวกภาษีที่รวมใน LineAmount ซ้ำ

Order เก็บ `OrderStatus = DRAFT | OPEN | CANCELLED` และ `PaymentStatus = UNPAID | PROCESSING | PAID` ไม่มี Payments table หรือ Payment aggregate สถานะเงินสำเร็จชี้ `SuccessfulTransactionId`

เมื่อ issue ให้ freeze items, amount, ownership, BusinessType และ metadata ที่มีผลต่อการจ่าย `Order.Metadata` กับ `OrderItem.Metadata` เป็น JSON ที่มี schema version และ validator ตาม BusinessType

Checkout สร้าง PaymentLink ที่ผูก Order โดยตรง เก็บ TokenHash, status และ expiry หนึ่ง Order มีลิงก์ประวัติหลายรายการแต่ active เพื่อเริ่มจ่ายได้หนึ่งรายการในรุ่นแรก Raw token คืนครั้งเดียวหรือ replay ผล idempotent จากข้อมูลเข้ารหัสที่มี TTL งานส่งลิงก์ใช้ payload ป้องกันความลับ การออกใหม่ revoke ลิงก์เดิมโดยไม่สร้าง Transaction

Checkout cookie เป็น capability อายุสั้น ไม่ใช่ Account หรือ CheckoutSession aggregate ทุก tab/confirm ต้องผูก OrderId กับ OrderVersion ที่แสดงและ recheck link revoked/expired แม้ cookie ออกไปแล้ว ใช้ HttpOnly, Secure และ CSRF สำหรับ mutation ส่วน return-state เปิดได้เฉพาะ status view

Transaction คือหนึ่งการเริ่มชำระกับ ProviderAccount หนึ่งชุด ไม่ใช่ทุก HTTP retry `POST /api/v1/checkout/confirm` ต้อง commit Transaction + immutable OrderSnapshot ก่อนเรียก PSP

Transaction ตรึง ProviderAccount, environment, CredentialVersion, method, amount/currency และ idempotency reference มี AttemptNo ที่ unique ต่อ Order ExternalChargeId อาจยังว่างเมื่อ PSP ไม่ตอบและสถานะเป็น `PENDING_CONFIRMATION`

หนึ่ง Order มี Transaction ที่ยังอาจเรียกเก็บเงินจริงพร้อมกันไม่เกินหนึ่งรายการ Network retry ใช้ Transaction/idempotency เดิม การลองรอบใหม่สร้าง Transaction ใหม่เมื่อรอบเก่าปิดแน่นอน

เมื่อยืนยันสำเร็จ Transaction เป็น `SUCCEEDED`, Order เป็น `PaymentStatus = PAID`, กำหนด SuccessfulTransactionId และเขียน Outbox ใน transaction เดียว Late/duplicate success เก็บตามจริงพร้อม NeedsReview แต่แจ้งธุรกิจสำเร็จตามปกติเพียงครั้งเดียว

Browser return ใช้กลับสู่หน้าตรวจสอบเท่านั้น Webhook หรือ inquiry ที่ตรวจ provider reference, amount และ currency แล้วจึงเปลี่ยนผลเงินจริง หน้า Checkout อาจแสดง FAILED/CANCELLED/EXPIRED ของ Transaction ล่าสุด แต่ Order.PaymentStatus ยังใช้เพียง UNPAID/PROCESSING/PAID

PSP API credential และ Webhook signature key อาจมี lifecycle ต่างกัน Adapter ต้องใช้กติกา rotation/expiry ตาม provider จริง การเก็บ key version เก่าไม่รับประกันว่า PSP ยังยอมรับตลอดไป

## 6. Notification และขอบเขตความปลอดภัย

หนึ่งผลสมัครสร้างหนึ่ง Notification ที่อ้าง AgentRegistrationAttempt และมี EMAIL + SMS Deliveries แยก retry ส่วน Order ที่ PAID ส่ง signed `BUSINESS_WEBHOOK` ผ่านกลไกเดียวกัน

Notification ใช้ recipient snapshot ของรอบที่ตัดสิน TemplateVersion และ SourceEventId การส่งล้มเหลวไม่ย้อน decision หรือ `Order.PaymentStatus`

- คง redirect-only และห้าม PAN/CVV, hosted fields, iframe หรือ QR บนโดเมน POL
- คง PSP signature verification, fetch/inquiry, inbox idempotency และ callback ของรายการเก่า
- คง Merchant isolation, authorization version/revocation, query/write guard และ audit แบบ append-only
- คง vault/secret store, credential version, maker-checker, masked reads และ emergency disable
- คง outbox, idempotent consumer, retry/DLQ และ security telemetry
- ไม่มี wallet, ledger, settlement, payout, reconciliation engine, refund/void/capture API หรือ policy issuance

## 7. ปัจจุบัน → Target → Retire

| ปัจจุบันที่พบ | Target ล่าสุด | Retire หลังย้ายครบ |
|---|---|---|
| `admin.Users`, `merch.Users`, `iam.ApiClients` เป็น identity หลายชุด | Accounts + Profiles + LoginAccounts + SystemClients/Keys | User/ApiClient เดิมเป็น source of truth ส่วน credential scheme เดิมถอดเมื่อวิธี SYSTEM ใหม่ได้รับอนุมัติ |
| Pending/Rejected อยู่ใน `merch.Users` และ Attempt ผูก User | AgentRegistrations + Attempts ก่อน Account | Pending Agent User และการแก้ form บน User |
| Role assignment แยกสอง schema, Admin MerchantAccess และ `ScopesCsv` | AccessRoles, MerchantAccess, BranchAccess, SystemScopes | assignment/resolver ที่ผูก schema และ CSV scope เดิม |
| `Originator`, SaleCode บน User/Order และ audience-specific initiator | Agents.SaleId, Sales/Branches, CreatedByAccountId และ OwnerSaleId | polymorphic Originator กับ owner fields ที่ซ้ำความหมาย |
| SummaryToken/expiry/recipient อยู่ใน Order | Checkout.PaymentLinks | token lifecycle และ raw link responsibility ใน Order |
| `Payments.Domain.Session` เป็นหนึ่งการลอง | Transaction.Transactions + TransactionEvents | Payments Session/module naming ไม่มี Payment aggregate มารองรับคู่ขนาน |
| `PaymentPaid` เป็น contract ปัจจุบัน | Event contract เดียวจาก Order ที่เปลี่ยนเป็น PAID แล้ว Notification ส่ง business webhook | event adapter/name เดิมหลัง consumer ย้ายครบ ห้ามส่งสอง event ให้เกิดผลซ้ำ |
| Order ชี้ current `PaymentSessionId` | PaymentStatus + SuccessfulTransactionId และ Order 1:N Transactions | pointer ไป attempt ล่าสุดและ Order status ที่สรุปจาก attempt เดียว |
| Provider settings/capability กระจาย Merchant/Payments | Merchant-owned ProviderAccounts/CredentialVersions/requests | config writer และ mapping ซ้ำหลัง cutover |
| Notification delivery กับ direct order/registration sender แยกทาง | Notifications + Deliveries + DeliveryAttempts | direct sender และ delivery history คู่ขนาน |
| Products/Carts เป็น platform modules ใน flow ปัจจุบัน | ย้ายกฎตรวจรายการและ upstream adapter ที่จำเป็นเข้า Order business contract | API/aggregate เก่าเมื่อ consumer ย้ายครบ โดยไม่ทิ้งการตรวจสินค้าที่ flow ประกันยังต้องใช้ |

Reporting เป็น query/read model บน Order และ Transaction ไม่เพิ่ม module เจ้าของเงินหรือตาราง financial truth

## 8. โครงสร้างที่ทีมเล็กดูแลได้

ทิศทางที่ผู้ใช้กำหนดคือทำให้ง่ายที่สุดและลบสิ่งที่ไม่ใช้ เป้าหมายรุ่นแรกจึงมี Backend host เดียว ฐานข้อมูลเดียว และ process เดียวสำหรับ API กับงานเบื้องหลัง ไม่มี Microservices, Redis หรือ message broker เพิ่มโดยไม่มีความจำเป็นที่วัดได้

### จำนวนส่วนที่ต้องดูแล

| ส่วน | ปัจจุบันที่สำรวจ | เป้าหมายที่เสนอ |
|---|---|---|
| Source projects | 39 | 4: Pol.Domain, Pol.Application, Pol.Infrastructure, Pol.Api |
| Test projects | 14 | 3: Unit, Architecture, Integration |
| Runtime DbContexts | 3 | 2: Control Plane และ Commerce หลังตรวจ transaction/isolation ครบ |
| Migration composition | 1 | 1 โดยใช้ mapping เจ้าของเดียวกับ runtime ไม่คัดลอก column/index |

7 โมดูลเป็นโฟลเดอร์/namespace ในแต่ละ layer ไม่สร้าง Domain/Application/Infrastructure project ซ้ำเจ็ดชุด Domain เก็บกฎและ model, Application เก็บ use case/CQRS, Infrastructure ต่อ DB/บริการภายนอก และ Api เป็นทางเข้า/จุดประกอบระบบ

การยุบ assembly ลด compiler boundary จึงต้องมี architecture tests ตรวจ dependency และ module ownership Helper ใช้ internal โดยปริยาย เปิด public เฉพาะ contract/type ที่ข้าม layer จริง จำนวน project ข้างต้นเป็นแผน ยังไม่ได้ย้ายโค้ดให้ได้จำนวนนี้

### ทางหลักเดียวต่อหนึ่งงาน

| เรื่อง | แนวทางเรียบง่าย |
|---|---|
| ข้อมูลและสิทธิ์ | หนึ่ง owner ต่อข้อมูล หนึ่งการตรวจสิทธิ์กลาง ไม่สร้าง Role/Account อีกชุดตามชื่อ console |
| การเข้าใช้ | ใช้ OAuth/OIDC component ที่ดูแลและรองรับมาตรฐาน ไม่เขียน authorization server/crypto เอง และไม่เก็บ token/session ซ้ำกับ component ที่เลือก |
| Access | เริ่มหนึ่ง active MerchantAccess ต่อ Account+Merchant และ DataScope เดียวในบริบทนั้น เป็น default ของแบบเรียบง่าย |
| SYSTEM | เริ่มหนึ่ง client binding ต่อ SYSTEM Account โดย Client ผูก Merchant/environment ชัด ไม่ทำหลายวิธียืนยัน Client เผื่อไว้ |
| PSP | ใช้ adapter ร่วมของ 2C2P/Omise เลือกจาก priority และ eligibility ชัดเจน ไม่มี rule language/plugin engine และไม่ failover จาก timeout |
| งานเบื้องหลัง | ใช้ BackgroundService ที่มีอยู่กับ outbox/ตารางงาน เลือกเวลาลองใหม่จาก NextAttemptAt ไม่สร้าง scheduler framework เพิ่ม |
| หนึ่ง use case | มี handler หลักเดียว ลด service/manager/repository wrapper ที่ส่งต่อข้อมูลอย่างเดียว แต่คง port ของ DB/PSP และ transaction boundary ที่จำเป็น |
| ดูแลปัญหา | ค้นจาก OrderNo/TransactionNo/CorrelationId มีรายการรอตรวจและวิธี retry ที่ควบคุมได้ ไม่เพิ่ม dashboard หลายชุดที่ตอบเรื่องเดียวกัน |

### สิ่งที่ยังไม่สร้างและสิ่งที่จะถอด

หากแบบเรียบง่ายยังแสดงสิทธิ์ที่ต้องการไม่ได้ ให้หยุดมอบสิทธิ์นั้นจนออกแบบครบ ไม่ขยาย DataScope เพื่อชดเชยข้อจำกัด

- ยังไม่สร้าง OTP/ContactVerifications จนเลือกข้อเสนอและวิธียืนยัน contact ชัด การตรวจรูปแบบและกระบวนการยืนยันที่ต้องใช้ยังคงอยู่
- ยังไม่สร้าง multi-access contexts, multi-endpoint routing, PSP รายอนาคต หรือ rule/template designer ที่ไม่มี use case จริง
- 116 API เป็น inventory ของแบบเต็ม ก่อน implement แต่ละรายการต้องผูกกับหน้าจอ Client หรือ use case ที่อยู่ในรุ่นที่จะส่งมอบ ไม่สร้าง CRUD ตามจำนวนตาราง
- ถอด Admin/Merchant account stores, Session naming, mapping ซ้ำ และ compatibility adapters หลังผู้เรียกและข้อมูลย้ายครบ
- Products/Carts ถอดได้หลังย้ายการตรวจรายการที่ flow ประกันยังต้องใช้เข้า Order contract ไม่ลบ validation ไปพร้อมชื่อโมดูล

ยังคง SYSTEM, 2C2P/Omise, ช่องทางชำระที่กำหนด และ Email+SMS ตามข้อกำหนด ส่วน tenant isolation, signature/amount verification, idempotency, outbox/inbox, audit, snapshots, retry และการรับผลเงินมาช้าเป็นฐานที่ต้องรักษา

### เกณฑ์ก่อนลบจากโค้ดจริง

ต้องตรวจทั้งผู้เรียกใน repo/Client, DI/reflection/generated registration, config/jobs และข้อมูลหรือ callback ที่ยังใช้อยู่ การไม่พบชื่อจาก search ครั้งเดียวไม่ใช่หลักฐานว่าไม่ใช้ จากนั้นย้ายผู้เรียก/ข้อมูลให้ครบ ลบเป็นชุดเล็ก และให้ build, architecture/integration tests กับ contract ที่กระทบผ่านก่อนส่งมอบ

ไม่ลบตาราง ประวัติ หรือ PSP references เพราะโค้ดดูไม่ถูกเรียก Backfill ไม่สร้างการลองชำระใหม่ Snapshot ที่ประกอบตอนย้ายต้องบอกที่มา และ rollback ต้องรักษาผลเงินหลัง cutover

## 9. Open decisions ที่ชุดล่าสุดยังไม่ล็อก

| เรื่อง | ข้อเสนอเริ่มต้น | เหตุผลที่ต้องยืนยัน |
|---|---|---|
| Registration identity/cardinality | ตั้งต้นหนึ่ง stable identity กับ Merchant ของ Sale | ต้องตัดสิน unique ต่อ identity หรือ identity + Merchant จากข้อมูลจริงและ recovery flow |
| MerchantAccess cardinality | แบบเรียบง่ายเริ่มหนึ่ง active Access ต่อ Account+Merchant | ถ้าต้องแยกอ่าน/เขียนคนละ data scope ภายใน Merchant เดียว ต้องเป็น requirement เพิ่มที่พิสูจน์จาก use case |
| SYSTEM client authentication | เลือกวิธีมาตรฐานหนึ่งวิธีตาม Client contract เริ่มหนึ่ง Client ต่อ SYSTEM Account | ต้องยืนยัน client capability/issuer/algorithm ก่อนเปิดใช้ ไม่สร้างหลาย scheme เผื่อไว้ |
| Employee DataScope | MERCHANT/ASSIGNED_BRANCHES ตาม assignment ส่วน SELF/BRANCH ยังไม่เปิดโดยปริยาย | ต้องนิยาม owner และ primary branch ของ Employee ก่อนใช้ |
| Contact verification | เลื่อน OTP ส่วนวิธียืนยัน contact ที่ใช้จริงต้องชัดก่อนเปิด flow | เพิ่ม OTP เมื่อมี requirement และ vendor ที่เลือก ไม่ตัดการตรวจ contact ที่จำเป็น |
| PaymentLink landing | ใช้ fragment exchange เป็นค่าเริ่มต้น | path-token ยังทำได้หาก redact log/referrer ครบ |
| Business endpoint จริง | คงหนึ่ง HTTPS endpoint ต่อ Merchant ตามข้อเสนอรุ่นแรก | ยังต้องกำหนดปลายทาง ผู้รับผิดชอบ allowlist และ signing contract ของระบบจริง |
| Amount/tax | LineAmount เป็น payable และกำหนด tax inclusion ที่ source | หากต้นทางส่งนิยามต่างกัน TotalAmount จะคลาดเคลื่อน |
| Operations | กำหนด TTL, assertion/key lifetime, polling, retry, retention และ SLA | ต้องอิง provider, webhook/API-key lifecycle, vendor และเวลาทำการจริง |

## 10. Source anchors

- Target: `/Users/king_developer/Downloads/pol-platform-latest/01-business-overview.md`, `/Users/king_developer/Downloads/pol-platform-latest/04-data-model-and-migration.md`
- Current identity/access: `src/Pol.Domain/Modules/Admins.Domain/Users/User.cs`, `src/Pol.Domain/Modules/Merchants.Domain/Users/User.cs`, `src/Pol.Domain/Modules/Iam.Domain/ApiClients/ApiClient.cs`
- Current merchant/order/checkout: `src/Pol.Domain/Modules/Merchants.Domain/Originator.cs`, `src/Pol.Domain/Modules/Orders.Domain/Order.cs`
- Current transaction/event: `src/Pol.Domain/Modules/Payments.Domain/Session.cs`, `src/Pol.Application/Contracts/PaymentPaid.cs`
- Current notification: `src/Modules/Notifications/`, `src/Pol.Infrastructure/Persistence/Persistence.ControlPlane/Notifications/DeliveryStore.cs`
