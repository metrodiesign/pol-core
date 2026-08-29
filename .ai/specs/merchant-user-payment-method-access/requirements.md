# Requirements: Merchant User Payment Method Access

> Status: approved 2026-08-17
> Notes:, amended 2026-08-17

## Overview

ระบบชำระเงินกลางต้องเพิ่ม policy ช่องทางชำระเงินระดับ Merchant User โดยต่อยอด identity, IAM/RBAC, tenant guard และ payment flow เดิมของ `pol-core` ระบบต้องตัดสินสิทธิ์จาก capability ทุกชั้นแบบ fail-closed ใช้ normalized data เป็น source of truth หลัง cutover และรักษา Platform Admin แยกจาก Merchant User

ขอบเขตที่ตัดสินแล้ว:

| หัวข้อ | ข้อกำหนด |
|---|---|
| Workflow | Requirements-First พร้อม approval gate ทุก artifact |
| Wire codes | คง `"card"`, `"promptpay"`, `"installment"` verbatim |
| UI | Merchant Console และ Admin Console UI อยู่นอกขอบเขต |
| Omise PromptPay | ไม่เพิ่ม live processing จนกว่า adapter รองรับจริง |
| User options | ไม่มี policy ธนาคารหรือ option ระดับ User |
| Supplemental example | ใช้เป็นตัวอย่างความสัมพันธ์เท่านั้น ไม่ใช่คำสั่งเพิ่ม scope |
| Production | ไม่รัน migration หรือ deploy production ในงานนี้ |

## Source Requirement Coverage

| Section ต้นทาง | เนื้อหา | REQ |
|---:|---|---|
| 1 | Capability hierarchy | REQ-2, REQ-3, REQ-4, REQ-5 |
| 2 | MerchantUsers | REQ-1 |
| 3 | User belongs to one Merchant | REQ-1, REQ-4 |
| 4 | MerchantPaymentMethods | REQ-4 |
| 5 | Merchant method provider invariant | REQ-3 |
| 6 | MerchantUserPaymentMethods | REQ-4 |
| 7 | Cross-Merchant database protection | REQ-4, REQ-11 |
| 8 | Effective permission intersection | REQ-5 |
| 9 | User A/User B example | REQ-11 |
| 10 | Options remain Merchant/Provider scoped | REQ-9 |
| 11 | KBANK/SCB/KTC/BAY example | REQ-9, REQ-11 |
| 12 | Admin separation | REQ-1, REQ-6, REQ-7, REQ-8 |
| 13 | Updated logical model | REQ-2 |
| 14 | Fifteen logical concepts | REQ-2 |
| 15 | Five required queries | REQ-6 |
| 16 | Validation rules | REQ-1, REQ-3, REQ-4, REQ-5, REQ-9, REQ-11 |
| 17 | Updated acceptance example | REQ-11 |

## REQ-1: Merchant User Identity Boundary

**User Story:** As a platform administrator, I want Merchant User ใช้ identity model เดิมและมี Merchant ownership ชัดเจน, so that ไม่เกิด identity ซ้ำหรือสิทธิ์ข้าม Merchant

**Acceptance Criteria (EARS):**

- 1.1 THE SYSTEM SHALL ใช้ `merch.Users` เป็น identity และ profile source ของ Merchant User โดยไม่สร้าง authentication model ซ้ำ
- 1.2 WHILE User มีสถานะ `PendingApproval` หรือ `Rejected` THE SYSTEM SHALL ถือว่าแถวนั้นเป็น registration applicant ไม่ใช่ Merchant User actor
- 1.3 IF registration applicant ถูก resolve เพื่อใช้งาน Merchant API หรือ payment capability THEN THE SYSTEM SHALL ปฏิเสธ
- 1.4 WHEN User เปลี่ยนสถานะเป็น `Active` THE SYSTEM SHALL กำหนด `MerchantId` ที่ไม่ใช่ empty Guid ภายใน transaction เดียวกัน
- 1.5 IF มีการ persist User สถานะ `Active` หรือ `Suspended` โดยไม่มี `MerchantId` THEN THE SYSTEM SHALL ให้ฐานข้อมูลปฏิเสธ
- 1.6 THE SYSTEM SHALL ผูก Merchant User สถานะ `Active` หรือ `Suspended` กับ Merchant เดียวผ่าน scalar `MerchantId`
- 1.7 THE SYSTEM SHALL บังคับ uniqueness ของ `(IdentityProvider, ExternalIdentitySubject)` ข้าม Merchant ทั้งระบบ
- 1.8 IF Merchant User ที่มี `MerchantId` ถูกขอให้ผูกกับ Merchant อื่น THEN THE SYSTEM SHALL ปฏิเสธ
- 1.9 THE SYSTEM SHALL NOT เก็บ password, password hash, credential หรือ secret บน Merchant User
- 1.10 THE SYSTEM SHALL รักษา Platform Admin identity แยกจาก Merchant User โดยไม่เพิ่ม `IsAdmin` shortcut
- 1.11 THE SYSTEM SHALL ใช้ SQL Server `UNIQUEIDENTIFIER` และ .NET `Guid` สำหรับ entity PK/FK ในขอบเขตนี้
- 1.12 THE SYSTEM SHALL NOT แทน Merchant User ownership ด้วย many-to-many Merchant membership

## REQ-2: Normalized Capability Model

**User Story:** As a platform administrator, I want capability model แยกตามความรับผิดชอบ, so that แต่ละชั้นจำกัดสิทธิ์ได้โดยไม่ปะปนกัน

ระบบต้องแทน logical concepts ต่อไปนี้ครบ โดย reuse entity เดิมได้เมื่อ semantics และ constraint เทียบเท่า:

| # | Logical concept | หน้าที่ |
|---:|---|---|
| 1 | Merchants | เจ้าของ tenant และ Merchant status |
| 2 | MerchantUsers | identity/profile และ Merchant ownership |
| 3 | PaymentMethods | canonical payment method |
| 4 | PaymentMethodOptionGroups | canonical option grouping |
| 5 | PaymentMethodOptions | canonical option เช่น bank code |
| 6 | PaymentProviders | canonical provider |
| 7 | PaymentProviderMethods | method ที่ provider รองรับ |
| 8 | PaymentProviderMethodOptions | option ที่ provider method รองรับ |
| 9 | MerchantProviderAccounts | account ของ Merchant กับ provider |
| 10 | MerchantProviderConfigurations | non-secret account configuration |
| 11 | MerchantProviderCredentials | vault-backed credential reference/version |
| 12 | MerchantProviderAccountMethods | method ที่ account เปิดใช้ |
| 13 | MerchantProviderAccountMethodOptions | option ที่ account method เปิดใช้ |
| 14 | MerchantPaymentMethods | canonical Merchant method policy |
| 15 | MerchantUserPaymentMethods | canonical User method policy |

**Acceptance Criteria (EARS):**

- 2.1 THE SYSTEM SHALL แทน logical concepts ทั้ง 15 รายการด้วย relation หรือ existing source ที่มี semantics และ constraints เทียบเท่า
- 2.2 THE SYSTEM SHALL เก็บ canonical Payment Method codes เป็น `"card"`, `"promptpay"` และ `"installment"`
- 2.3 THE SYSTEM SHALL บังคับ Payment Method code ไม่ซ้ำกัน
- 2.4 THE SYSTEM SHALL ใช้ Guid เป็น PK/FK ของ logical entities ที่มี identity
- 2.5 THE SYSTEM SHALL NOT collapse Platform, Provider, Merchant Provider Account, Merchant และ User capability ลง table เดียว
- 2.6 WHEN authorization cutover สำเร็จ THE SYSTEM SHALL ใช้ normalized rows เป็น canonical authorization source
- 2.7 WHILE normalized authorization mode ทำงาน THE SYSTEM SHALL NOT ใช้ `Merchant.EnabledChannels`, `PspConnection.EnabledMethods`, arbitrary CSV หรือ arbitrary JSON เพื่อตัดสินสิทธิ์
- 2.8 THE SYSTEM SHALL เก็บ credential material ใน vault model เดิม
- 2.9 THE SYSTEM SHALL เชื่อม Payment Method Option กับ Option Group และ Payment Method ที่เป็น parent อย่างชัดเจน
- 2.10 THE SYSTEM SHALL บังคับหนึ่ง Option Group code ต่อ Payment Method
- 2.11 THE SYSTEM SHALL บังคับหนึ่ง Payment Method Option code ต่อ Option Group
- 2.12 THE SYSTEM SHALL บังคับ Payment Provider code ไม่ซ้ำกัน
- 2.13 THE SYSTEM SHALL เก็บเพียง credential reference/version บน provider account model
- 2.14 WHEN API รับ known Payment Method code THE SYSTEM SHALL trim และ normalize case เป็น canonical lowercase value
- 2.15 IF API รับ blank, unknown หรือ conceptual alias ที่ไม่ใช่ canonical code THEN THE SYSTEM SHALL ปฏิเสธ

## REQ-3: Provider and Merchant Provider Account Capability

**User Story:** As a platform administrator, I want provider และ account capability สะท้อนสิ่งที่ adapter ทำได้จริง, so that Merchant เปิด method ที่ประมวลผลไม่ได้ไม่ได้

**Acceptance Criteria (EARS):**

- 3.1 THE SYSTEM SHALL บังคับหนึ่ง Payment Provider Method ต่อ `(PaymentProviderId, PaymentMethodId)`
- 3.2 THE SYSTEM SHALL บังคับหนึ่ง Merchant Provider Account Method ต่อ `(MerchantProviderAccountId, PaymentMethodId)`
- 3.3 THE SYSTEM SHALL ผูก Merchant Provider Account Method กับ Provider Method ของ provider เดียวกับ account
- 3.4 THE SYSTEM SHALL ผูก Provider Method Option กับ Provider Method และ canonical Payment Method Option ที่ตรงกัน
- 3.5 THE SYSTEM SHALL ผูก Account Method Option กับ Account Method และ Provider Method Option ที่ตรงกัน
- 3.6 WHEN Platform Admin เปิด Merchant Payment Method THE SYSTEM SHALL ตรวจว่ามี active Merchant Provider Account Method รองรับอย่างน้อยหนึ่งรายการ
- 3.7 WHEN Platform Admin เปิด Merchant Payment Method THE SYSTEM SHALL ตรวจ invariant ข้าม capability tables ภายใน application transaction เดียวกับการเขียน policy
- 3.8 IF ไม่มี active Merchant Provider Account Method รองรับ THEN THE SYSTEM SHALL ปฏิเสธ mutation โดยไม่บันทึก partial state
- 3.9 THE SYSTEM SHALL NOT ใช้ SQL trigger เพื่อบังคับ cross-table capability invariant
- 3.10 THE SYSTEM SHALL ถือ `IPspAdapter.SupportedMethods` เป็น hard ceiling ของ Provider Method capability
- 3.11 IF Provider Method catalog เปิด method ที่ registered adapter ไม่รองรับ THEN THE SYSTEM SHALL ปฏิเสธ configuration
- 3.12 THE SYSTEM SHALL มี automated validation ที่ตรวจ Provider Method catalog กับ registered adapter capability
- 3.13 WHILE Merchant Provider Account หรือ Account Method ถูก disable THE SYSTEM SHALL ถือว่า capability จาก account นั้นไม่ effective
- 3.14 THE SYSTEM SHALL บังคับหนึ่ง Provider Method Option ต่อ `(PaymentProviderMethodId, PaymentMethodOptionId)`
- 3.15 THE SYSTEM SHALL บังคับหนึ่ง Account Method Option ต่อ `(MerchantProviderAccountMethodId, PaymentMethodOptionId)`
- 3.16 THE SYSTEM SHALL บังคับหนึ่ง Merchant Provider Account ต่อ `(MerchantId, PaymentProviderId)`
- 3.17 THE SYSTEM SHALL ถือ Merchant Provider Account เป็น qualifying account เมื่อ account enabled
- 3.18 THE SYSTEM SHALL ถือ Merchant Provider Account Method เป็น qualifying method เมื่อ method enabled
- 3.19 THE SYSTEM SHALL ถือ Provider Method เป็น qualifying method เมื่อ Provider Method active และ adapter รองรับ
- 3.20 THE SYSTEM SHALL NOT ใช้ connection health เป็น authorization capability condition
- 3.21 THE SYSTEM SHALL NOT ใช้ credential readiness เป็น authorization capability condition
- 3.22 IF persisted Provider Method เกิน registered adapter capability THEN THE SYSTEM SHALL ให้ resolver ปฏิเสธ method แบบ fail-closed
- 3.23 WHEN pre-deploy integration validation พบ Provider Method drift THE SYSTEM SHALL ปิดกั้น release
- 3.24 THE SYSTEM SHALL NOT auto-enable หรือ auto-disable persisted Provider Method rows ระหว่าง startup

## REQ-4: Merchant and Merchant User Payment Policies

**User Story:** As a platform administrator, I want กำหนด method policy แยกระดับ Merchant และ User, so that User ใช้ได้ไม่เกินขอบเขตของ Merchant

**Acceptance Criteria (EARS):**

- 4.1 THE SYSTEM SHALL บังคับหนึ่ง Merchant Payment Method ต่อ `(MerchantId, PaymentMethodId)`
- 4.2 THE SYSTEM SHALL บังคับหนึ่ง Merchant User Payment Method ต่อ `(MerchantUserId, PaymentMethodId)`
- 4.3 THE SYSTEM SHALL เก็บ `MerchantId` บน Merchant User Payment Method เพื่อใช้ relational tenant protection
- 4.4 THE SYSTEM SHALL บังคับ composite relationship `(MerchantUserId, MerchantId)` ไปยัง Merchant User
- 4.5 THE SYSTEM SHALL บังคับ composite relationship `(MerchantId, PaymentMethodId)` ไปยัง Merchant Payment Method
- 4.6 IF มีการ insert หรือ update User policy ด้วย Merchant ที่ไม่ตรงกับ User THEN THE SYSTEM SHALL ให้ฐานข้อมูลปฏิเสธ
- 4.7 IF Merchant Payment Method ไม่มีอยู่หรือ disabled THEN THE SYSTEM SHALL ถือว่า User policy ของ method นั้นไม่ effective
- 4.8 IF Merchant User Payment Method ไม่มีอยู่หรือ disabled THEN THE SYSTEM SHALL ปฏิเสธ User ใช้ method นั้น
- 4.9 THE SYSTEM SHALL อนุญาต lower layer จำกัด capability จาก parent layer
- 4.10 THE SYSTEM SHALL NOT อนุญาต lower layer ขยาย capability เหนือ parent layer
- 4.11 WHEN User policy เปลี่ยน THE SYSTEM SHALL NOT เปลี่ยน Provider, Account, Merchant หรือ Option configuration
- 4.12 THE SYSTEM SHALL บันทึก actor และเวลาของการสร้างหรือแก้ policy
- 4.13 THE SYSTEM SHALL ใช้ optimistic concurrency token สำหรับ policy mutation
- 4.14 WHEN Platform Admin enable User policy THE SYSTEM SHALL recheck enabled Merchant Payment Method ภายใน transaction เดียวกับ mutation
- 4.15 IF Merchant Payment Method ไม่มีอยู่หรือ disabled ตอน User policy mutation commit THEN THE SYSTEM SHALL ปฏิเสธโดยไม่บันทึก partial state
- 4.16 WHEN Merchant Payment Method ถูก disable หลังมี enabled User policy THE SYSTEM SHALL เก็บ User policy row เดิมไว้เป็น ineffective configuration
- 4.17 THE SYSTEM SHALL ใช้ tenant query filter และ sealed write guard เดิมกับ merchant-scoped capability entities ใหม่ทุก entity
- 4.18 WHEN Platform Admin เข้าถึง merchant-scoped capability ข้าม tenant THE SYSTEM SHALL ใช้ explicit sanctioned cross-tenant port

## REQ-5: Effective Payment Method Resolver

**User Story:** As a Merchant User, I want ระบบตัดสิน method จาก policy ปัจจุบันทุกชั้น, so that ใช้ได้เฉพาะ capability ที่ได้รับจริง

User-level authorization behaviors ใน REQ-5, REQ-7 และ REQ-8.6–REQ-8.11 มีผลเมื่อ normalized authorization mode ตาม REQ-10.16 ทำงานแล้วเท่านั้น

**Acceptance Criteria (EARS):**

- 5.1 THE SYSTEM SHALL ใช้ effective capability resolver เดียวเป็น canonical decision point สำหรับ User Payment Method authorization
- 5.2 WHEN User, Merchant, Merchant policy, User policy, Account Method, Provider Method และ adapter capability ใช้งานได้ครบ THE SYSTEM SHALL อนุญาต method
- 5.3 IF User status ไม่ใช่ `Active` THEN THE SYSTEM SHALL ปฏิเสธ method
- 5.4 IF Merchant status ไม่ใช่ `Active` THEN THE SYSTEM SHALL ปฏิเสธ method
- 5.5 IF Merchant Payment Method ไม่มีอยู่หรือ disabled THEN THE SYSTEM SHALL ปฏิเสธ method
- 5.6 IF Merchant User Payment Method ไม่มีอยู่หรือ disabled THEN THE SYSTEM SHALL ปฏิเสธ method
- 5.7 IF ไม่มี active Merchant Provider Account Method รองรับ THEN THE SYSTEM SHALL ปฏิเสธ method
- 5.8 IF Provider Method ไม่มีอยู่หรือ inactive THEN THE SYSTEM SHALL ปฏิเสธ method
- 5.9 IF registered adapter ไม่รองรับ method THEN THE SYSTEM SHALL ปฏิเสธ method
- 5.10 WHEN resolve method โดยยังไม่ระบุ provider THE SYSTEM SHALL ถือว่า method effective เมื่อมี qualifying account อย่างน้อยหนึ่งรายการ
- 5.11 WHEN payment action ระบุ provider THE SYSTEM SHALL ตรวจ qualifying account ของ provider ที่เลือกโดยตรง
- 5.12 IF provider ที่เลือกไม่รองรับ method แต่ provider อื่นรองรับ THEN THE SYSTEM SHALL ปฏิเสธ provider ที่เลือก
- 5.13 WHEN resolver ถูกเรียกใน request ใหม่ THE SYSTEM SHALL อ่าน current capability state โดยไม่เชื่อ authorization snapshot จาก request ก่อนหน้า

## REQ-6: Administrative and User Query Contracts

**User Story:** As a platform administrator or Merchant User, I want query capability ตาม audience ของตน, so that จัดการและเลือก method ได้โดยไม่เปิดข้อมูลข้าม tenant

**Acceptance Criteria (EARS):**

- 6.1 WHEN Platform Admin query Users ของ Merchant THE SYSTEM SHALL คืนเฉพาะ Users สถานะ `Active` หรือ `Suspended` ที่ผูกกับ Merchant นั้น
- 6.2 WHEN Platform Admin query methods available to Merchant THE SYSTEM SHALL คืน Merchant methods ที่ effective ตาม Merchant และ provider/account capability
- 6.3 WHEN Platform Admin query methods assigned to User THE SYSTEM SHALL คืน explicit User policy พร้อม enabled state
- 6.4 WHEN Platform Admin resolve User capability สำหรับ Merchant, User และ Method THE SYSTEM SHALL คืนผล allowed หรือ denied จาก canonical resolver
- 6.5 WHEN Platform Admin resolve options สำหรับ Merchant, User, Provider และ Method THE SYSTEM SHALL คืนเฉพาะ effective options ของ provider/account ที่เลือก
- 6.6 THE SYSTEM SHALL จำกัด policy management ทุกระดับให้ Platform Admin audience
- 6.7 IF Merchant User มี `users.manage` หรือ generic Merchant permission อื่น THEN THE SYSTEM SHALL NOT อนุญาต policy mutation
- 6.8 WHEN Merchant User query methods ของตน THE SYSTEM SHALL resolve UserId และ MerchantId จาก authenticated server context
- 6.9 WHEN Merchant User query options ของตน THE SYSTEM SHALL resolve UserId และ MerchantId จาก authenticated server context
- 6.10 THE SYSTEM SHALL NOT รับ target UserId หรือ target MerchantId จาก Merchant User เพื่อ query effective capability ของบุคคลอื่น
- 6.11 IF query อ้าง User ที่ไม่อยู่ใน Merchant ที่กำหนด THEN THE SYSTEM SHALL ไม่เปิดเผยข้อมูล User หรือ policy ข้าม Merchant
- 6.12 THE SYSTEM SHALL คืน Payment Method codes บน API ด้วย wire values เดิมแบบ lowercase
- 6.13 WHEN Platform Admin แก้ policy THE SYSTEM SHALL บังคับ Platform Admin authentication และ IAM/RBAC เดิม
- 6.14 WHEN Platform Admin แก้ policy THE SYSTEM SHALL บังคับ CSRF convention เดิม
- 6.15 WHEN Platform Admin แก้ policy THE SYSTEM SHALL บังคับ idempotency convention เดิม
- 6.16 WHEN Platform Admin แก้ policy THE SYSTEM SHALL บังคับ optimistic concurrency convention เดิม
- 6.17 THE SYSTEM SHALL มี administrative contracts สำหรับ enable หรือ disable Provider, Account, Merchant และ User capability โดยไม่เปิดเผย credential material
- 6.18 IF API รับ malformed, blank หรือ unknown Payment Method input THEN THE SYSTEM SHALL ตอบ 400
- 6.19 IF query อ้าง resource ที่ไม่มีอยู่หรืออยู่นอก requested/resolved Merchant scope THEN THE SYSTEM SHALL ตอบ 404
- 6.20 IF server-resolved Merchant User ไม่มี effective User method permission THEN THE SYSTEM SHALL ตอบ 403
- 6.21 IF Merchant, Provider หรือ Account capability state ไม่พร้อมใช้งาน THEN THE SYSTEM SHALL ตอบ 409
- 6.22 THE SYSTEM SHALL gate catalog, Provider, Account และ Merchant policy reads ด้วย `merchant.view`
- 6.23 THE SYSTEM SHALL gate catalog, Provider, Account และ Merchant policy mutations ด้วย `merchant.manage`
- 6.24 THE SYSTEM SHALL gate User policy reads ด้วย `merchants.users.view`
- 6.25 THE SYSTEM SHALL gate User policy mutations ด้วย `merchants.users.manage`
- 6.26 THE SYSTEM SHALL gate Merchant User self effective-method และ effective-option reads ด้วย `payment.view`
- 6.27 THE SYSTEM SHALL NOT เพิ่ม IAM permission key ใหม่สำหรับ feature นี้
- 6.28 WHEN Platform Admin query registration applicants THE SYSTEM SHALL ใช้ registration query contracts เดิมแทน Merchant Users query

## REQ-7: Merchant-Originated Payment Enforcement

**User Story:** As a Merchant, I want ทุก payment entry point ใช้ policy เดียวกัน, so that ไม่มี endpoint ใด bypass User-level permission

**Acceptance Criteria (EARS):**

- 7.1 WHEN Merchant User เลือก Payment Method เพื่อสร้าง order THE SYSTEM SHALL ตรวจ canonical effective capability ก่อน persist order
- 7.2 WHEN Merchant User สร้าง Payment Session THE SYSTEM SHALL ตรวจ canonical effective capability ใหม่
- 7.3 WHEN Merchant User เริ่ม first redirect claim ก่อน external charge อาจถูกสร้าง THE SYSTEM SHALL ตรวจ canonical effective capability ใหม่
- 7.4 WHEN Merchant User ระบุ provider สำหรับ Payment Session THE SYSTEM SHALL ตรวจ provider-specific capability ตาม REQ-5.11
- 7.5 IF Merchant User มี generic `payment.create` แต่ไม่มี effective User method permission THEN THE SYSTEM SHALL ปฏิเสธ payment action
- 7.6 IF Merchant User มี generic `payment.redirect` แต่ไม่มี effective User method permission THEN THE SYSTEM SHALL ปฏิเสธ redirect
- 7.7 THE SYSTEM SHALL NOT ใช้ list/query endpoint result เป็น authorization proof สำหรับ payment mutation
- 7.8 THE SYSTEM SHALL NOT รับ Merchant User identity จาก payment request body เพื่อใช้ตัดสินสิทธิ์
- 7.9 IF capability ถูกถอนก่อน first external charge claim THEN THE SYSTEM SHALL ปฏิเสธ request ถัดไปก่อนเรียก PSP adapter
- 7.10 WHEN Platform Admin เริ่ม payment flow THE SYSTEM SHALL ใช้ Admin authorization plane เดิมโดยไม่สร้าง Merchant User ปลอม
- 7.11 WHEN Platform Admin เริ่ม payment flow THE SYSTEM SHALL ยังบังคับ Merchant, Provider และ Account capability
- 7.12 THE SYSTEM SHALL รักษา existing payment flows ที่ยังผ่าน capability ทุกชั้นให้ทำงานต่อได้
- 7.13 THE SYSTEM SHALL ใช้ Payment Method บน Order เป็น authoritative immutable method ของ payment lifecycle
- 7.14 IF Payment Session request ส่ง Method ที่ไม่ตรงกับ Order THEN THE SYSTEM SHALL ปฏิเสธก่อนสร้าง session
- 7.15 WHEN Payment Session ถูกสร้าง THE SYSTEM SHALL persist Method จาก Order
- 7.16 IF Payment Session มี redirect claim หรือ external charge อาจมีอยู่ THEN THE SYSTEM SHALL ดำเนิน idempotent settlement ต่อโดยไม่ re-authorize User method
- 7.17 WHILE settling existing redirect claim THE SYSTEM SHALL NOT สร้าง replacement Payment Session หรือ external charge ใหม่
- 7.18 WHEN webhook หรือ payment-status ยืนยัน external charge THE SYSTEM SHALL reconcile payment ต่อโดยไม่ใช้ current User, Merchant, Provider หรือ Account authorization state
- 7.19 THE SYSTEM SHALL NOT อ้างว่า User permission revocation ยกเลิก external charge ที่ PSP สร้างแล้ว

## REQ-8: Anonymous Customer Payment Authorization

**User Story:** As a customer, I want ชำระจาก order link โดยใช้ context ที่ server เชื่อถือได้, so that client ไม่สามารถเลือก Merchant, User หรือ method เพื่อข้าม policy

**Acceptance Criteria (EARS):**

- 8.1 WHEN Merchant User สร้าง order THE SYSTEM SHALL persist initiating Merchant User ID จาก authenticated server context
- 8.2 WHEN order ถูกสร้าง THE SYSTEM SHALL persist trusted initiating audience เพื่อแยก Merchant User จาก Platform Admin
- 8.3 THE SYSTEM SHALL ถือ initiating identity และ audience บน order เป็น immutable authorization context
- 8.4 WHEN anonymous customer เรียก pay THE SYSTEM SHALL derive Merchant, Method และ initiating identity จาก order ฝั่ง server
- 8.5 THE SYSTEM SHALL NOT รับ MerchantId, MerchantUserId, initiating audience หรือ Payment Method override จาก anonymous pay request
- 8.6 WHEN anonymous pay สร้าง Payment Session หรือ resume session ก่อน first external charge claim THE SYSTEM SHALL re-resolve current effective capability ของ initiating Merchant User
- 8.7 WHEN anonymous pay เริ่ม first redirect claim THE SYSTEM SHALL re-resolve current effective capability ของ initiating Merchant User
- 8.8 IF initiating Merchant User ไม่มีอยู่ ไม่ Active หรือไม่มี method permission ก่อน first external charge claim THEN THE SYSTEM SHALL ปฏิเสธก่อนเรียก PSP adapter
- 8.9 THE SYSTEM SHALL NOT ถือ Order หรือ Payment Session snapshot เป็นสิทธิ์สำหรับสร้าง external charge ใหม่แทน current resolver result
- 8.10 WHEN initiating audience เป็น Platform Admin THE SYSTEM SHALL ไม่ใช้ Merchant User policy
- 8.11 WHEN initiating audience เป็น Platform Admin THE SYSTEM SHALL ยังตรวจ Merchant, Provider และ Account capability ปัจจุบัน
- 8.12 THE SYSTEM SHALL รักษา anonymous customer flow โดยไม่บังคับ customer session cookie
- 8.13 THE SYSTEM SHALL รักษา summary token validation เดิม
- 8.14 THE SYSTEM SHALL รักษา summary token expiry เดิม
- 8.15 THE SYSTEM SHALL รักษา anonymous payment rate limiting เดิม
- 8.16 WHILE initiating audience เป็น Merchant User THE SYSTEM SHALL กำหนด initiating Merchant User ID ที่ไม่เป็น null
- 8.17 WHILE initiating audience เป็น Merchant User THE SYSTEM SHALL บังคับ composite relationship `(InitiatingMerchantUserId, MerchantId)` ไปยัง Merchant User เดียวกัน
- 8.18 WHILE initiating audience เป็น Platform Admin THE SYSTEM SHALL กำหนด initiating Merchant User ID เป็น null
- 8.19 IF initiating audience กับ initiating identity ไม่เป็นไปตามข้อกำหนด THEN THE SYSTEM SHALL ให้ฐานข้อมูลปฏิเสธ

## REQ-9: Effective Payment Method Options

**User Story:** As a Merchant User, I want เห็นเฉพาะ options ที่ provider account เปิดจริง, so that User permission ไม่ขยาย bank หรือ option capability

**Acceptance Criteria (EARS):**

- 9.1 THE SYSTEM SHALL จำกัด User-level policy ที่ Payment Method เท่านั้น
- 9.2 THE SYSTEM SHALL NOT สร้าง `MerchantUserPaymentMethodOptions`
- 9.3 IF User ไม่มี effective permission สำหรับ Method THEN THE SYSTEM SHALL คืน effective options เป็นชุดว่าง
- 9.4 WHEN resolve options THE SYSTEM SHALL ใช้ Provider ที่ระบุใน query
- 9.5 IF Provider Method Option ไม่มีอยู่หรือ inactive THEN THE SYSTEM SHALL ไม่คืน option นั้น
- 9.6 IF Merchant Provider Account Method Option ไม่มีอยู่หรือ disabled THEN THE SYSTEM SHALL ไม่คืน option นั้น
- 9.7 WHEN VCommerce User B มี `"installment"` และ 2C2P account เปิด KBANK กับ SCB THE SYSTEM SHALL คืน KBANK และ SCB
- 9.8 WHEN 2C2P account ปิด KTC กับ BAY THE SYSTEM SHALL NOT คืน KTC หรือ BAY
- 9.9 WHEN User method policy เปลี่ยน THE SYSTEM SHALL NOT แก้ Provider หรือ Account option rows
- 9.10 THE SYSTEM SHALL NOT เพิ่ม bank-selection UI, PSP bank payload หรือ live Omise PromptPay processing ในขอบเขตนี้
- 9.11 WHEN resolve Provider-specific options THE SYSTEM SHALL ตรวจว่า selected Merchant Provider Account enabled
- 9.12 WHEN resolve Provider-specific options THE SYSTEM SHALL ตรวจว่า selected Account Method enabled
- 9.13 WHEN resolve Provider-specific options THE SYSTEM SHALL ตรวจว่า selected Provider Method active และ adapter รองรับ
- 9.14 WHEN resolve Provider-specific options THE SYSTEM SHALL คืนเฉพาะ active Provider Method Options
- 9.15 WHEN resolve Provider-specific options THE SYSTEM SHALL คืนเฉพาะ enabled Account Method Options
- 9.16 THE SYSTEM SHALL NOT fallback หรือ union options จาก Provider อื่น

## REQ-10: Migration, Backfill, Cutover and Rollback

**User Story:** As an operator, I want ย้ายข้อมูลเดิมแบบตรวจสอบได้และ fail-closed, so that ไม่มีข้อมูลสูญหายหรือ authorization เปลี่ยนโดยไม่ตั้งใจ

**Acceptance Criteria (EARS):**

- 10.1 WHEN expand migration รัน THE SYSTEM SHALL เพิ่ม normalized schema แบบ additive โดยยังไม่ลบ legacy columns
- 10.2 WHEN backfill Payment Methods THE SYSTEM SHALL ใช้เฉพาะ wire codes `"card"`, `"promptpay"` และ `"installment"`
- 10.3 WHEN backfill Provider Methods THE SYSTEM SHALL จำกัด rows ตาม registered adapter `SupportedMethods`
- 10.4 WHEN backfill Account Methods THE SYSTEM SHALL parse legacy `EnabledMethods` แบบ deterministic และ intersect กับ adapter capability
- 10.5 WHEN backfill Merchant Methods THE SYSTEM SHALL intersect legacy `EnabledChannels` กับ active qualifying Account Methods
- 10.6 WHEN final pre-cutover backfill Active Merchant Users THE SYSTEM SHALL สร้าง enabled User policies ตาม effective Merchant methods ณ authorization cutoff
- 10.7 WHEN User ถูก activate หลัง authorization cutover THE SYSTEM SHALL ไม่ auto-grant User method policy
- 10.8 THE SYSTEM SHALL NOT parse arbitrary Provider Account metadata เพื่อสร้าง option rows
- 10.9 THE SYSTEM SHALL สร้าง option rows เฉพาะจาก explicit Admin configuration หรือ deterministic seed fixture
- 10.10 WHEN legacy order มี trusted Admin-origin marker THE SYSTEM SHALL backfill initiating audience เป็น Platform Admin โดยไม่มี Merchant User ID
- 10.11 WHEN backfill initiating User ของ legacy merchant-originated order THE SYSTEM SHALL map เฉพาะกรณี `(MerchantId, SaleCode)` ตรงกับ User สถานะ `Active` หรือ `Suspended` เพียงคนเดียว
- 10.12 IF legacy order จับคู่ User ไม่ได้หรือจับคู่ได้หลาย User THEN THE SYSTEM SHALL บันทึก conflict เพื่อ remediation
- 10.13 IF unresolved legacy order conflict ยังเหลือ THEN THE SYSTEM SHALL ปิดกั้น authorization cutover
- 10.14 IF legacy capability value ไม่รู้จักหรือขัดกับ adapter THEN THE SYSTEM SHALL บันทึก conflict โดยไม่เดาหรือ silently enable
- 10.15 WHILE mixed-version rollout ยังทำงาน THE SYSTEM SHALL ปิด normalized User authorization และใช้ legacy authorization read จน old application instances ออกจากระบบ
- 10.16 WHEN final reconciliation ผ่าน THE SYSTEM SHALL activate normalized authorization เป็น source เดียวใน cutover เดียวกัน
- 10.17 WHILE compatibility window ยังเปิด THE SYSTEM SHALL update legacy CSV projections จาก normalized writes โดยไม่อ่าน projections เพื่อตัดสินสิทธิ์
- 10.18 IF rollback เกิดก่อน authorization cutover THEN THE SYSTEM SHALL รักษา legacy reads และ normalized backfill data โดยไม่ลบข้อมูล
- 10.19 IF rollback เกิดหลัง authorization cutover THEN THE SYSTEM SHALL NOT กลับไปใช้ user-unaware legacy authorization ที่ขยายสิทธิ์
- 10.20 THE SYSTEM SHALL มี migration verification สำหรับ row counts
- 10.21 THE SYSTEM SHALL มี migration verification สำหรับ uniqueness constraints
- 10.22 THE SYSTEM SHALL มี migration verification สำหรับ tenant relationships
- 10.23 THE SYSTEM SHALL มี migration verification สำหรับ adapter drift
- 10.24 THE SYSTEM SHALL มี migration verification สำหรับ unresolved conflicts
- 10.25 THE SYSTEM SHALL NOT รัน migration บน production เป็นส่วนหนึ่งของ implementation งานนี้
- 10.26 WHEN rollback application หลัง authorization cutover THE SYSTEM SHALL ใช้เฉพาะ binary ที่รองรับ normalized authorization schema
- 10.27 IF normalized-aware rollback binary ใช้งานไม่ได้ THEN THE SYSTEM SHALL fail closed และใช้ roll-forward recovery
- 10.28 WHEN final reconciliation รัน THE SYSTEM SHALL delta-backfill Active Users ที่เกิดหลัง initial backfill และก่อน authorization cutoff
- 10.29 WHEN final reconciliation รัน THE SYSTEM SHALL delta-backfill merchant-originated Orders ที่เกิดหลัง initial backfill และก่อน authorization cutoff
- 10.30 WHEN authorization cutoff ถูก capture THE SYSTEM SHALL รวม User activations และ merchant-originated Orders ทุกแถวที่ commit ก่อน cutoff ใน final reconciliation
- 10.31 THE SYSTEM SHALL อนุญาต deterministic seed สำหรับ canonical Option Groups และ Payment Method Options
- 10.32 THE SYSTEM SHALL สร้าง production Merchant Provider Account option assignments ผ่าน explicit Admin configuration เท่านั้น
- 10.33 THE SYSTEM SHALL จำกัด VCommerce, KBANK, SCB, KTC และ BAY sample assignments ไว้ที่ test หรือ demo fixtures

## REQ-11: Security, Compatibility and Acceptance Evidence

**User Story:** As a reviewer, I want หลักฐานทดสอบครอบ schema, resolver และ payment flow, so that อนุมัติ feature ได้จากผลที่รันจริง

**Acceptance Criteria (EARS):**

- 11.1 THE SYSTEM SHALL มี database test ที่พิสูจน์ว่า Active Merchant User ต้องมี Merchant
- 11.2 THE SYSTEM SHALL มี database test ที่พิสูจน์ว่า external identity เดียวลงทะเบียนข้าม Merchant ซ้ำไม่ได้
- 11.3 THE SYSTEM SHALL มี database test ที่พิสูจน์ว่า User อยู่หลาย Merchant ไม่ได้
- 11.4 THE SYSTEM SHALL มี database test ที่พิสูจน์ว่า cross-Merchant User policy ถูกปฏิเสธ
- 11.5 THE SYSTEM SHALL มี database test ที่พิสูจน์ว่า duplicate Merchant policy ถูกปฏิเสธ
- 11.6 THE SYSTEM SHALL มี database test ที่พิสูจน์ว่า duplicate User policy ถูกปฏิเสธ
- 11.7 THE SYSTEM SHALL มี application test ที่พิสูจน์ว่า Merchant เปิด method โดยไม่มี qualifying provider account ไม่ได้
- 11.8 THE SYSTEM SHALL มี resolver tests สำหรับ allowed path และ denial ของทุก capability layer ใน REQ-5
- 11.9 THE SYSTEM SHALL มี tests สำหรับ required queries ทั้งห้าใน REQ-6
- 11.10 THE SYSTEM SHALL มี payment flow test ที่พิสูจน์ว่า generic `payment.create` อย่างเดียว bypass User policy ไม่ได้
- 11.11 THE SYSTEM SHALL มี payment flow test ที่พิสูจน์ว่า User suspension มีผลกับ request ถัดไปที่ต้อง resolve current capability
- 11.12 THE SYSTEM SHALL มี payment flow test ที่พิสูจน์ว่า Merchant suspension มีผลกับ request ถัดไปที่ต้อง resolve current capability
- 11.13 THE SYSTEM SHALL มี anonymous pay test ที่พิสูจน์ว่า revoke หลังสร้าง Order แต่ก่อน first external charge ปิดกั้น pay หรือ redirect ถัดไป
- 11.14 THE SYSTEM SHALL มี adapter/catalog drift test ที่ fail เมื่อ DB capability เกิน registered adapter capability
- 11.15 THE SYSTEM SHALL มี acceptance test ที่พิสูจน์ว่า User A ใช้ `"card"` และ `"promptpay"` แต่ใช้ `"installment"` ไม่ได้
- 11.16 THE SYSTEM SHALL มี acceptance test ที่พิสูจน์ว่า User B ใช้ได้เฉพาะ `"installment"`
- 11.17 THE SYSTEM SHALL มี acceptance test ที่พิสูจน์ว่า User B เห็น KBANK กับ SCB แต่ไม่เห็น KTC หรือ BAY
- 11.18 THE SYSTEM SHALL มี migration tests สำหรับ successful backfill, unknown values, ambiguous creator และ cutover blocker
- 11.19 THE SYSTEM SHALL รักษา existing supported payment flow tests ให้ผ่าน
- 11.20 IF environment ไม่มี SQL Server THEN THE SYSTEM SHALL รายงาน integration test เป็น not run โดยไม่อ้างว่า green
- 11.21 THE SYSTEM SHALL NOT มี committed test `.only` หรือ `.skip`
- 11.22 THE SYSTEM SHALL NOT เพิ่ม dependency ใหม่หรือ expose secret เพื่อทำ feature นี้
- 11.23 THE SYSTEM SHALL มี payment flow test ที่พิสูจน์ว่า Session Method ต่างจาก Order Method ถูกปฏิเสธ
- 11.24 THE SYSTEM SHALL มี migration test ที่พิสูจน์ว่า legacy Order map ไปยัง unique Suspended creator ได้
- 11.25 THE SYSTEM SHALL มี payment flow test ที่พิสูจน์ว่า existing external charge ถูก reconcile หลัง permission revoke โดยไม่สร้าง charge ใหม่
- 11.26 THE SYSTEM SHALL มี database test สำหรับ tenant write guard และ Order initiating-identity constraints
- 11.27 THE SYSTEM SHALL มี effective-options tests สำหรับ selected Provider chain และ no-fallback behavior
- 11.28 THE SYSTEM SHALL มี rollout tests สำหรับ final delta backfill, compatibility projection และ normalized-aware rollback

คำสั่ง verification ที่ implementation phase ต้องรันจริง:

```bash
dotnet restore pol-core.slnx
dotnet build pol-core.slnx --no-restore -warnaserror
dotnet test pol-core.slnx --no-build --filter "Category!=Integration"
dotnet test pol-core.slnx --filter "Category=Integration"
scripts/check-rename-identifiers.sh
.ai/bin/check-secrets.sh --all
scripts/spec-trace.sh merchant-user-payment-method-access
```

## Edge Cases & Open Questions

การตัดสินใจที่ล็อกแล้ว:

| ประเด็น | Decision |
|---|---|
| Registration boundary | Pending/Rejected คือ applicant; Active/Suspended ต้องมี Merchant เดียว |
| Existing User backfill | Active ก่อน authorization cutoff ได้ current effective methods; หลัง cutover deny จน Admin assign |
| Policy manager | Platform Admin เท่านั้น |
| Merchant User reads | อ่านเฉพาะ effective methods/options ของตนจาก server-resolved identity |
| Anonymous revocation | re-resolve ก่อน first external charge; charge ที่อาจมีอยู่ต้อง settle/reconcile ต่อ |
| Legacy order creator | map unique Active/Suspended `(MerchantId, SaleCode)`; conflict ต้อง remediation ก่อน cutover |
| Provider authority | adapter capability เป็น hard ceiling |
| Admin-originated order | ไม่ใช้ User policy แต่ยังใช้ Merchant/Provider/Account capability |
| Legacy option data | ไม่มี canonical source; ห้ามเดาจาก arbitrary metadata |
| Order Method | Order เป็น immutable source; Session Method ต้องตรง |
| Provider account cardinality | หนึ่ง account ต่อ `(MerchantId, PaymentProviderId)` |
| Rollback | หลัง cutover ใช้เฉพาะ normalized-aware binary หรือ fail-closed/roll-forward |

### Findings Log: spec-analyze

> Anchor: repository HEAD `0ca847c`; `requirements.md` ยังไม่มี file commit ตอน full audit วันที่ 2026-08-17

ผู้ใช้เลือกตัวเลือกแนะนำทุกข้อวันที่ 2026-08-17:

| Finding | Category | REQ | Decision และเหตุผล |
|---|---|---|---|
| F1 | Logical inconsistency | REQ-5, REQ-7, REQ-8, REQ-10.15–10.16 | Legacy authorization ทำงานจน verified cutover; ห้าม mixed instances ใช้คนละ authorization source |
| F2 | Logical inconsistency | REQ-8.8, REQ-10.11 | Legacy creator map ได้ทั้ง Active/Suspended; current resolver เป็นผู้ deny สถานะล่าสุด |
| F3 | Logical inconsistency | REQ-4.7–4.8 | Reject การ enable User policy ใต้ disabled Merchant policy; parent ปิดภายหลังทำให้ child inert โดยไม่ rewrite |
| F4 | Ambiguity | REQ-7.1–7.4, REQ-8.4–8.9 | Order Method เป็น immutable source; Session ต้องใช้ค่าเดียวกัน |
| F5 | Ambiguity | REQ-5.11, REQ-6.5, REQ-9.4 | คงหนึ่ง Merchant account ต่อ Provider; supplemental UAT/PROD split ไม่ขยาย scope |
| F6 | Ambiguity | REQ-3.6, REQ-5.7, REQ-7.11 | Qualifying account ดู enabled capability chain; health/credential readiness เป็น runtime availability |
| F7 | Ambiguity | REQ-1.2, REQ-6.1 | Merchant Users query คืน Active/Suspended; applicant ใช้ registration contracts เดิม |
| F8 | Ambiguity | REQ-2.2, REQ-6.12, REQ-11.19 | คง trim/case-insensitive input และ lowercase canonical output; alias/unknown ถูกปฏิเสธ |
| F9 | Conflicting constraint | REQ-7.3, REQ-7.9, REQ-8.6–8.9 | Re-authorize ก่อน first charge; หลัง charge อาจมีอยู่ให้ settle/reconcile แบบ idempotent |
| F10 | Conflicting constraint | REQ-10.17–10.19 | CSV เป็น derived projection; post-cutover rollback ห้ามใช้ user-unaware legacy binary |
| F11 | Gap | REQ-10.6–10.7, REQ-10.15 | Cutoff อยู่ที่ authorization cutover; final delta ครอบ Users และ Orders ที่ commit ก่อน cutoff |
| F12 | Gap | REQ-4.4–4.6, REQ-6.11, REQ-8.1–8.5 | ใช้ tenant filter/write guard พร้อม DB composite FK/check; Admin ผ่าน sanctioned port |
| F13 | Gap | REQ-4.6, REQ-5, REQ-6.11, REQ-7.9 | Error mapping: malformed 400, scoped-not-found 404, User denial 403, capability state 409 |
| F14 | Gap | REQ-5.11, REQ-6.5, REQ-9.3–9.6 | Options ต้องผ่าน selected Provider chain ทั้งชุด; ไม่มี fallback/union |
| F15 | Gap | REQ-3.10–3.12 | Admin write และ release validation reject drift; resolver fail-closed; startup ไม่ auto-mutate DB |
| F16 | Unstated assumption | REQ-6.6–6.17 | Reuse `merchant.*`, `merchants.users.*`, `payment.view`; ไม่เพิ่ม IAM key |
| F17 | Unstated assumption | REQ-9.7–9.8, REQ-10.9 | Production account options มาจาก Admin; VCommerce/bank sample อยู่ test/demo เท่านั้น |

Open questions หลัง audit: ไม่มี

Sync impact: ยังไม่มี `design.md` หรือ `tasks.md`
