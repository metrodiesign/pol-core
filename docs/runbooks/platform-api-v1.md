# คู่มือปฏิบัติการ Platform API v1

เอกสารนี้เป็นคู่มือดูแล API contract รุ่น v1 แหล่ง inventory เดียวคือ [api-scope.json](../../.ai/specs/platform-restructure-v1/api-scope.json): 116 แถว แบ่งเป็น v1 จำนวน 109, deferred จำนวน 3 และ retired จำนวน 4 รายการ
registration OTP (Issue #274) เปิด API-033/034 แล้ว เหลือ deferred เฉพาะ API-108–110; ข้อกำหนดฟีเจอร์อยู่ใน Issue #274 และ ADR 0002

## บริบทผู้เรียก

| บริบท | ใช้กับ | หลักฐานที่ต้องส่ง |
|---|---|---|
| `E` Employee/Admin | Account, access, merchant, provider, order, transaction, notification และ audit ตาม permission | platform JWT ใน `Authorization: Bearer` (จาก `/oauth/authorize` + `/oauth/token`), `Authorization` policy และ permission ของ operation; ไม่มี admin session cookie แล้ว (retire 2026-09-14) |
| `A` Agent/Merchant user | Merchant-owned order และ checkout ตาม Merchant scope | Merchant user session cookie; scope มาจาก session ฝั่ง server |
| `S` SYSTEM client | machine-to-machine ที่เปิดใน inventory | OAuth client authentication และ scope ที่ server ตรวจ |
| `C` Customer checkout | payment-link access, summary, confirm, status และ verify | payment-link proof/cookie และ checkout CSRF ตาม operation |
| `P` Provider callback | PSP webhook และ browser return | provider signature/state/return binding; ไม่ใช้ human JWT |
| `N` Notification provider | Notification receipt | provider verifier ที่ผูก `providerCode` กับ receipt/delivery; ค่า default ไม่มี vendor จะ fail closed |
| Monitoring | `/health/live`, `/health/ready` | policy เครือข่ายของ health probe ไม่ใช้ user session |

ระบบตรวจ Merchant/account ownership จากข้อมูล server-side ทุกครั้ง. ห้ามใช้ `merchantId`, owner, role หรือ permission ที่ส่งใน body เป็นหลักฐานแทน session และห้ามอ่าน child resource โดยข้ามการตรวจ parent.

## สร้าง Order แบบ canonical

`POST /api/v1/orders` เป็น operation canonical ของ API-079 และรับ `CreateOrderRequest` ตาม OpenAPI โดยต้องส่ง `businessType`, `currency` และ `items`; แต่ละรายการต้องมี `productReference`, `productCode`, `productName`, `quantity`, `unitPrice`, `discountAmount`, `taxAmount` และ `lineAmount` เป็น decimal string. `orderDiscountAmount` และ `orderChargeAmount` มีค่าเริ่มต้น `0.0000`; `issueNow` มีค่าเริ่มต้น `true`.

ระบบตรวจ `merchant_id` จาก identity token และใช้ query `merchantId` ได้เฉพาะเป็นค่าตรวจความตรงกัน. การไม่มี claim หรือ claim/query ไม่ตรงกันได้ `403` พร้อม stable `code`; owner ของ Agent derive จาก trusted `AgentSaleId`. Employee และ SYSTEM ระบุ `ownerSaleId`/`ownerBranchId` ได้เมื่ออยู่ใน `MerchantAccess`/`BranchAccess` ที่ active เท่านั้น. ระบบไม่รับ client quote เป็นแหล่งราคา: quote, currency หรือ adjustment ที่ไม่ตรง trusted pricing ได้ `409` พร้อม `code=pricing_mismatch` และต้องไม่มี Order หรือ link ใหม่.

เมื่อ `issueNow=false` ระบบสร้าง `DRAFT` โดยไม่มี `PaymentLink`; caller เดิม replay ด้วย `Idempotency-Key` จะได้ผล Draft snapshot เดิมแม้มีการ PATCH, issue หรือ rotate ภายหลัง. เมื่อ field ถูกละเว้นหรือ `issueNow=true` ระบบ freeze Order และสร้าง PaymentLink แรกใน transaction เดียว. Issued replay คืน raw token ได้เฉพาะจาก protected replay mechanism ที่มี purpose/expiry; ห้ามค้นคืนจาก hash หรือเขียน raw token ลง `AdminOperationRecords`/log.

การเขียน canonical ใช้ `identity-platform`: Employee/Agent ใช้ Account permission `payment.create`, SYSTEM ต้องมี scope `order.write`. ทุก identity caller ส่ง platform JWT ใน `Authorization: Bearer` จึงไม่มี cookie/CSRF; token ที่ authorization version หรือสถานะบัญชีเปลี่ยนไปแล้วได้ `401` และต้อง refresh (`POST /oauth/token grant_type=refresh_token`) หรือ login ใหม่. Operations ที่กำหนด `Idempotency-Key` จะ reject intent ใหม่ด้วย `409`; PATCH ใช้ `If-Match` และ Draft-only โดยไม่รับ idempotency header ส่วน issue/rotate ต้องตรง ETag เดิม มิฉะนั้นได้ `412` โดยไม่เขียนซ้ำ.

เส้นทางเดิมจาก Cart ยังคงเป็น compatibility route ที่ระบุชัด `POST /api/v1/orders/from-cart` และยังใช้ `CreateOrderFromCartRequest` กับ dual-console auth. Client ใหม่ต้องย้ายไป canonical route; ห้ามใช้ DTO เดียวกันเพื่อเดาความหมายระหว่างสอง workflow.

`metadata` ของ Order และ item ต้องเป็น `VersionedMetadata` รูป `{ "schemaVersion": 1, "data": { ... } }` โดยจำกัดขนาดและโครงสร้างตาม schema contract. PATCH ที่ไม่ส่ง metadata จะคง snapshot เดิม; metadata ที่จับคู่กับ item ซ้ำหรือคลุมเครือจะถูกปฏิเสธ. Trusted CommerceItemMetadata เป็นข้อมูลคนละชุดและไม่ถูกเขียนทับ.

`notificationIntent.send=true` เก็บ email/phone แยกกันบน Order. Draft จะเก็บ intent แต่ยังไม่ enqueue; issue และ `issueNow=true` จะ enqueue `PaymentLinkNotificationRequestedV1`, ส่วน rotate ใช้ `sendNotification=true`. Outbox payload เก็บเฉพาะ protected token; consumer สร้าง Email/SMS delivery แบบ dedupe และ delivery worker เปิด token หลัง claim ใน memory เท่านั้น. SMS ที่ยังไม่มี sender เป็น `BLOCKED_NOT_CONFIGURED`; ไม่มี recipient สำหรับ create ได้ `400 notification_recipient_required` และ rotate ได้ `409 notification_recipient_required`.

## Headers และการเขียนข้อมูล

การเปลี่ยนแปลงที่รองรับ retry ต้องส่ง `Idempotency-Key` เป็น key ที่คงเดิมสำหรับ intent เดิม. Key เดิมกับ request hash ต่างกันต้องได้ `409` และ `code=idempotency_key_reused` หรือ stable conflict code ของ operation; ระบบห้ามสร้างแถวซ้ำหรือทำ side effect ซ้ำ.

การแก้ resource ที่มี version ต้องส่ง `If-Match: "v{version}"`. Version เก่าต้องได้ `412` และไม่มี write ใหม่. Response ที่เปลี่ยน resource จะคืน `ETag` ใหม่ตาม operation metadata. Mutation ที่ใช้ cookie/session audience ต้องส่ง CSRF header ให้ตรงกับ CSRF cookie; protocol callback, OAuth form endpoint และ provider receipt ใช้ verifier ของตนเองตาม contract.

เขียน DTO ต้องยึด schema ใน OpenAPI และ matrix. ฟิลด์ server-derived เช่น account, Merchant, owner, status, version, amount, provider credential และ audit actor ห้ามรับเป็น authority จาก caller. Secret เป็น write-only หรือ protected reference; ไม่คืน plaintext ใน response, audit หรือ application log.

## การอ่านข้อมูลและ SFS

รายการที่รองรับ search/filter/sort ใช้ SFS allowlist เท่านั้น: `page`, `limit`, `filters`, `sort`, `search` และ query เฉพาะที่ operation ประกาศ. ค่า field/operator ที่ไม่อยู่ allowlist ต้องถูกปฏิเสธหรือละทิ้งตาม contract ของ endpoint; ห้ามส่ง filter ไปใช้กับ column อื่นโดยปริยาย. หน้ารายการต้องคืน `items`, `page`, `limit`, `total` และใช้ stable tie-breaker.

การอ่าน Order `items`, `history`, transaction, event และ export ต้องเริ่มจาก Order parent แล้วตรวจ Merchant/owner visibility. Merchant อื่นต้องเห็นผลเหมือน resource ไม่มีอยู่ (`404`) และ response ต้องไม่มี raw provider payload, webhook, token, secret หรือข้อมูล PII ที่ไม่อยู่ใน DTO. GET/read/status ไม่มี PSP network side effect; API-094 และ API-098 เป็น explicit verify command แยกจาก read.

## Error และการตรวจสอบ

Business API ใช้ RFC 9457 Problem Details และ `code` ที่คงที่. ตัวอย่าง code ที่ใช้ปฏิบัติการคือ `validation_failed`, `forbidden`, `precondition_failed`, `idempotency_key_reused`, `unsafe_destination`, `audit_integrity_unhealthy` และ `capability_not_configured`. OAuth endpoints ใช้ OAuth error response ตาม protocol (`invalid_request`, `invalid_client`, `invalid_grant`) ไม่แปลงเป็น business Problem Details.

เมื่อพบ error ให้เก็บ `traceId`/correlation และ operation key โดยไม่เก็บ token, password, signing key, raw payment-link, assertion, raw provider payload หรือ recipient เต็ม. ตรวจ audit log ผ่าน `/api/v1/audit-logs` โดยใช้ `merchantId`, actor, action, resource และช่วงเวลาเป็น filter; audit เป็น append-only และตรวจ hash chain ก่อนคืนผล.

## Callback และ receipt

PSP webhook ต้องตรวจ provider/account/environment/reference และ signature ก่อนเปลี่ยน Transaction. Browser return ตรวจ protected state และใช้ status-only context; redirect ไม่ใช่หลักฐานว่าเงินสำเร็จ. Notification receipt ตรวจ provider signature/auth ผ่าน verifier, resolve delivery เพื่อ bind Merchant แล้วจึงเปลี่ยนสถานะ; receipt ที่ไม่รู้ผลคง `UNKNOWN`, key เดิม replay ได้ และ signature ผิดต้อง `401` โดยไม่ mutation.

## Health

`GET /health/live` ตรวจเฉพาะ process และคืน `200` กับ `{"status":"healthy"}`. `GET /health/ready` ตรวจ dependency ที่จำเป็น เช่น database และ vault; ถ้าไม่พร้อมคืน `503` กับ `{"status":"not_ready"}`. Body ต้องไม่เผย connection string, host, password, key, exception หรือ topology. ถ้า live healthy แต่ ready degraded ให้หยุดรับ traffic ที่ load balancer และตรวจ dependency ตาม `traceId`/server log.

## ความสามารถภายนอกและ deviation

- Entra tenant/client, PSP sandbox contract, SMS vendor และ production business endpoint ยังไม่มี credential/allowlist ที่ใช้ทดสอบ live ได้; local SQL Server, capture verifier/sender และ capability disabled เป็นหลักฐานทดแทน
- SMS ที่ไม่มี vendor ต้องเป็น `BLOCKED_NOT_CONFIGURED` และไม่เรียก provider
- PSP connection test เป็น probe authentication ไม่ใช่ channel certification และไม่สร้าง charge
- Notification template routes `API-108–110` ยัง deferred; API-033/034 เปิดใน v1 แล้ว โดย SMS ใน Development/Testing ใช้ log เท่านั้น ส่วน environment อื่นตอบ 503 จนกว่าจะมี vendor
- legacy extras ใน EndpointDataSource/OpenAPI ไม่ใช่ approval ให้ client ใช้; การ retire ต้องรอ Task10 พร้อม evidence ของ dependency และ replacement

เมื่อเปลี่ยน route ให้แก้ inventory, operation matrix, OpenAPI metadata และ runtime evidence แล้วรัน comparator ยืนยัน expected109/overlap109/missing0/deferred0

## การลงทะเบียนตัวแทนและ OTP — Issue #274

anonymous PUT สร้างเคสใต้ merchant ที่ตั้งใน `IdentityAccess:AgentMerchantId` และออก cookie อายุ 30 นาทีโดยค่าเริ่มต้น
cookie หมดอายุแล้ว email ซ้ำตอบ 409 ให้เข้าสู่ระบบ Microsoft ด้วย email เดิมเพื่อกลับมา ไม่ใช่เริ่มเคสใหม่

| ขั้นตอน | พฤติกรรม |
|---|---|
| PUT draft | มือถือไทย local 10 หลัก prefix 06/08/09 เท่านั้น รูปแบบอื่นตอบ 400 phone_invalid |
| POST contact-verifications | ใช้เบอร์ใน draft ไม่รับเบอร์จาก body; คืน 202 พร้อม masked recipient และเวลา resend |
| POST confirm | รหัส 6 หลัก อายุ 5 นาที ผิดได้ 5 ครั้ง; success คืน 200 และ ETag ใหม่ |
| POST submissions | ต้องมีรูปและยืนยันเบอร์ปัจจุบันก่อนจึงได้ 201 |
| Reviewer approve | สร้าง Account; ถ้าไม่มี ExternalIdentity ยังไม่สร้าง LoginAccount |
| Microsoft callback | หา LoginAccount แล้ว registration ด้วย identity ก่อน fallback email; Approved ผูก LoginAccount, ที่เหลือ pin session ไป /register |

### การส่ง SMS และข้อจำกัด

- Development/Testing ใช้ keyed sender `contact-verification` ที่ log รหัสระดับ Information เฉพาะ local; ห้ามส่ง log รหัสไปหลักฐานหรือระบบรวม log production
- environment อื่นใช้ not-configured sender ตอบ 503 capability_not_configured ก่อนสร้าง challenge; unkeyed sender ของ notification เดิมไม่เปลี่ยน
- cooldown 60 วินาทีและ 5 sends/ชั่วโมงใช้เบอร์ persisted ร่วมทุกเคส; lockout อาจต้องรอประมาณ 1 ชั่วโมง ไม่มี support bypass
- rate limiter ใช้ SHA256 ของ registration cookie เมื่อมี ไม่เก็บ raw cookie ใน key/log; request แรกไม่มี cookie ใช้ IP
- operator ต้องตั้ง trusted proxy/ForwardedHeaders ให้ถูกต้อง; limiter ของ UserAuth เดิมมีข้อจำกัด proxy IP เดียวเช่นกัน แต่ไม่แก้ใน Issue นี้
- ยังไม่มี prune job ของ registration sessions และ contact verifications
- ยังไม่มี vendor adapter ที่แปลง E.164: เมื่อเพิ่มให้แปลงเฉพาะขอบเขตส่ง SMS ห้ามแก้ persisted phone

### Transaction inventory

`AgentRegistrationStore` มี 9 transactions ทั้งหมดใช้ ControlPlaneDbContext เดียวและ admin UoW เดิม
5 เส้นทางเดิมคือ draft/photos/submit/approve/reject เพิ่ม anonymous create + session, OTP issue, OTP confirm และ identity bind
OTP issue ล็อกทั้งเคสและ hash ของเบอร์; confirm บันทึกจำนวนครั้งก่อนแปลงผลเป็น HTTP error
SMS ส่งหลัง commit challenge จึงไม่เปิด transaction ค้างระหว่างเรียก provider และการส่งล้มเหลวยังนับโควตา

### Migration และ rollback

migration `20260919102405_AgentAnonymousRegistrationOtp` หยุดด้วย THROW เมื่อ email ซ้ำต่อ merchant
หรือ backfill Approved แล้วหา Account ไม่ได้ ห้ามเลือก winner, dedupe หรือลบข้อมูลอัตโนมัติ
operator รัน pre-check ต่อไปนี้ผ่านช่องทางที่ได้รับอนุญาตก่อน deploy; output มี email จึงห้ามแนบ log สาธารณะ

```sql
SELECT MerchantId, LOWER(TRIM(Email)) AS EmailNormalized,
       STRING_AGG(CONVERT(nvarchar(max), Id), N',') AS RegistrationIds,
       STRING_AGG(CONVERT(nvarchar(max), Status), N',') AS Statuses
FROM acct.AgentRegistrations
GROUP BY MerchantId, LOWER(TRIM(Email))
HAVING COUNT(*) > 1;
```

ใช้ EF database update สำหรับ local ที่มีข้อมูล และใช้ schema.sql เฉพาะ fresh DB ตาม local-dev runbook
script แยก backfill ออกจาก AddColumn ด้วย GO และต้องเปิด QUOTED_IDENTIFIER (sqlcmd -I)
ก่อน deploy ต้องสำรองข้อมูล; rollback หลังมี anonymous rows ถูกปฏิเสธโดย Down guard ให้ restore backup ก่อน migration หรือ roll forward
การถอย application โดยไม่ถอย DB ใช้ไม่ได้กับ nullable identity contract; rollout pol-merchant ต้องตาม backend contract นี้


## Migration readiness ของ Task 9

การซ้อมย้ายต้องเป็น operator action บนฐานข้อมูล isolated ที่ระบุชื่อชัดเจนเท่านั้น และห้ามผูกเข้ากับ host startup หรือ production connection.

```bash
set -a; source .env.integration; set +a
export POL_DB=master
dotnet test tests/IntegrationTests/IntegrationTests.csproj --filter "Capability=MigrationReadiness"
```

Runner ต้องทำให้ `MigrationRehearsalStatus` เป็น `Blocked` เมื่อ identity/reference/currency/invariant มี conflict และต้องเก็บ `MigrationConflictReport` ที่ไม่มี email, display name, token หรือ secret. `LegacyIdentityMap` ใช้ `(LegacyKind, LegacyId) -> AccountId` จาก identity evidence และ `OrderId`/PSP reference/amount/currency/history ต้องคงเดิม.

ขณะ pause ให้ writer เดียวเป็นเจ้าของ maintenance window; callback ที่เข้ามาหลัง watermark ต้องลง `MigrationRecoveryInbox` และ replay ได้หลัง target พร้อม. Rollback ใช้ forward recovery รักษา target results/events และห้าม restore backup ทับข้อมูลหลัง cutover.

การเขียน target ต้องถือ transaction-scoped SQL `sp_getapplock` resource เดียวต่อ target database. `BackfillTargetsAsync` จะตรวจ lease บน server ก่อนเขียนและ rollback เมื่อ connection/session หาย; ผู้เริ่มงานคนถัดไปต้อง acquire lease สำเร็จก่อนจึงทำต่อได้. หลักฐาน local ครอบ `acct.Accounts`, `acct.LoginAccounts`, `acct.AgentRegistrations`, `acct.AgentRegistrationAttempts`, `shop.Orders`, `txn.Transactions` และ `txn.TransactionEvents` จาก restored synthetic snapshot.

Environment นี้ยังไม่มี sanitized backup, master identity mapping, Entra/PSP/SMS/Email credential หรือ production authorization. Synthetic backup ที่สร้างและ restore ใน `PolMigrationReadinessTask9Test` พิสูจน์เฉพาะ implementation/local tests complete; ยังไม่ใช่ external acceptance หรือ production/cutover readiness.
