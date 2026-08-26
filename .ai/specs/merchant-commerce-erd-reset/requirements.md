# Requirements: Merchant-Commerce ERD Reset

> Status: approved 2026-08-07
> Notes:, amended 2026-08-07

## Overview

ปรับ `pol-core` แบบ big-bang ให้โครงสร้างข้อมูลและ commerce flow ตรงกับ
`merchant-commerce-payment-erd-revised-kyc-simplified.md` โดยใช้ ERD เป็น authoritative target
สำหรับรายการที่ระบุให้เปลี่ยนหรือลบ และคงโครงสร้างปัจจุบันที่ ERD ไม่ได้สั่งลบโดยชัดแจ้ง งานนี้สร้าง
SQL Server baseline ใหม่, ตัด persisted checkout และ policy-specific model, เปลี่ยน Cart ให้สร้าง Order
โดยตรง, เพิ่ม KYC photo แบบ key-only, และทำให้ Order lifecycle รับผล Paid/Failed/Expired จาก payment
ผ่าน outbox โดยยังคง merchant isolation, payment security, auditability และ API security floor เดิม

## REQ-1: ERD canon และขอบเขตการเปลี่ยนแปลง

**User Story:** As a platform engineer, I want target schema มี canon เดียวและ deviation ที่ตรวจสอบได้, so that การ reset ไม่ลบหรือเปลี่ยนข้อมูลนอกขอบเขตโดยเงียบ

**Acceptance Criteria (EARS):**
- 1.1 THE SYSTEM SHALL เก็บสำเนา ERD ที่อนุมัติไว้ใต้ `docs/reference/`
- 1.2 THE SYSTEM SHALL ถือ field, type, entity, relationship และรายการลบที่ระบุใน ERD เป็น target บังคับ
- 1.3 THE SYSTEM SHALL บันทึก deviation จาก ERD ที่อนุมัติไว้ใน appendix ของเอกสาร canon
- 1.4 IF ERD ไม่ได้สั่งลบ field หรือตารางปัจจุบันโดยชัดแจ้ง THEN THE SYSTEM SHALL คง field หรือตารางนั้นไว้
- 1.5 THE SYSTEM SHALL คง `merch.RegistrationAttempts`
- 1.6 THE SYSTEM SHALL คง concurrency `Version` ของ `shop.Carts`
- 1.7 THE SYSTEM SHALL คง `OrderNo` ของ `shop.Orders`
- 1.8 THE SYSTEM SHALL คง customer snapshot fields ของ `shop.Orders`
- 1.9 THE SYSTEM SHALL คง payment snapshot fields ของ `shop.Orders`
- 1.10 THE SYSTEM SHALL คง discount fields ของ Cart และ Order model ปัจจุบัน
- 1.11 THE SYSTEM SHALL คง security fields และ audit tables ปัจจุบันที่ไม่ถูกสั่งลบ
- 1.12 THE SYSTEM SHALL ไม่เพิ่ม generic SKU gateway ใน scope นี้
- 1.13 THE SYSTEM SHALL ไม่เพิ่ม souvenir fulfillment ใน scope นี้
- 1.14 THE SYSTEM SHALL ใช้ insurance stored procedure integration ปัจจุบันเป็น product source เดียวใน runtime

## REQ-2: Schema naming และ status model ตาม ERD

**User Story:** As a maintainer, I want database และ domain vocabulary ตรงกับ ERD, so that code, API และ persisted model ใช้ชื่อและสถานะชุดเดียวกัน

**Acceptance Criteria (EARS):**
- 2.1 THE SYSTEM SHALL เพิ่ม nullable `UpdatedAt` ใน `admin.Users`
- 2.2 THE SYSTEM SHALL ใช้ชื่อ `AdminUserId` สำหรับ foreign-key fields ที่ ERD ระบุว่าอ้าง `admin.Users.Id`
- 2.3 THE SYSTEM SHALL เปลี่ยน admin session field `CreatedIp` เป็น `IpAddress`
- 2.4 THE SYSTEM SHALL เปลี่ยน merchant session field `CreatedIp` เป็น `IpAddress`
- 2.5 THE SYSTEM SHALL เปลี่ยน `iam.PermissionGroups.LabelTh` เป็น `Name`
- 2.6 THE SYSTEM SHALL เปลี่ยน `iam.Permissions.LabelTh` เป็น `Name`
- 2.7 THE SYSTEM SHALL เพิ่ม `Status` แบบ integer ใน `iam.PermissionGroups`
- 2.8 THE SYSTEM SHALL เพิ่ม `Status` แบบ integer ใน `iam.Permissions`
- 2.9 THE SYSTEM SHALL ใช้ค่า `Active=0` และ `Inactive=1` สำหรับ status ใหม่ของ IAM
- 2.10 THE SYSTEM SHALL เปลี่ยน `IsActive bit` เป็น `Status int` ใน `cfg.Divisions`
- 2.11 THE SYSTEM SHALL เปลี่ยน `IsActive bit` เป็น `Status int` ใน `cfg.Levels`
- 2.12 THE SYSTEM SHALL เปลี่ยน `IsActive bit` เป็น `Status int` ใน `cfg.Offices`
- 2.13 THE SYSTEM SHALL เปลี่ยน `IsActive bit` เป็น `Status int` ใน `cfg.Positions`
- 2.14 THE SYSTEM SHALL ใช้ค่า `Active=0` และ `Inactive=1` สำหรับ status ของ CFG
- 2.15 THE SYSTEM SHALL เปลี่ยน `merch.Merchants.DisplayName` เป็น `Name`
- 2.16 THE SYSTEM SHALL ลบ `merch.Merchants.LegalEntityId`
- 2.17 THE SYSTEM SHALL เพิ่ม nullable `Note` ใน `merch.Merchants`
- 2.18 THE SYSTEM SHALL เปลี่ยน `merch.Users.PersonType` เป็น `IdentityType`
- 2.19 THE SYSTEM SHALL เปลี่ยน `merch.Users.IdNumber` เป็น `IdentityNumber`
- 2.20 THE SYSTEM SHALL ใช้ชื่อ `UserId` สำหรับ merchant-user foreign-key fields ที่ ERD ระบุ
- 2.21 THE SYSTEM SHALL เปลี่ยน `merch.VaultSecrets.Name` เป็น `SecretName`
- 2.22 THE SYSTEM SHALL เปลี่ยน `merch.VaultSecrets.KeyId` เป็น `SecretKey`
- 2.23 THE SYSTEM SHALL เปลี่ยน `dbo.DataProtectionKeys.FriendlyName` เป็น `SecretKey`
- 2.24 WHILE resolving permissions THE SYSTEM SHALL ใช้เฉพาะ Role ที่มี `Status=Active`
- 2.25 WHILE resolving permissions THE SYSTEM SHALL ใช้เฉพาะ Permission ที่มี `Status=Active`
- 2.26 WHILE resolving permissions THE SYSTEM SHALL ใช้เฉพาะ PermissionGroup ที่มี `Status=Active`

## REQ-3: KYC photo แบบ key-only

**User Story:** As a merchant user, I want แนบรูป KYC ตอนสมัครหรือส่งข้อมูลใหม่ได้, so that ระบบเก็บหลักฐานโดยไม่เปิด surface การ review เพิ่มในรอบนี้

**Acceptance Criteria (EARS):**
- 3.1 THE SYSTEM SHALL เพิ่ม nullable `KycPhotoObjectKey` ใน `merch.Users`
- 3.2 THE SYSTEM SHALL ไม่สร้าง KYC status field
- 3.3 THE SYSTEM SHALL ไม่สร้าง KYC review field
- 3.4 WHEN client ส่ง multipart registration THE SYSTEM SHALL ยอมรับ file part ชื่อ `kycPhoto` เป็น optional file ที่สอง
- 3.5 WHEN `kycPhoto` เป็น JPEG, PNG หรือ WebP ที่ content type และ magic bytes ตรงกัน THE SYSTEM SHALL ยอมรับไฟล์
- 3.6 IF `kycPhoto` มีขนาดมากกว่า 2 MiB THEN THE SYSTEM SHALL reject request ด้วย validation error
- 3.7 IF `kycPhoto` มี content type ที่ไม่รองรับ THEN THE SYSTEM SHALL reject request ด้วย validation error
- 3.8 IF `kycPhoto` มี magic bytes ไม่ตรงกับชนิดไฟล์ที่ประกาศ THEN THE SYSTEM SHALL reject request ด้วย validation error
- 3.9 WHEN `kycPhoto` ผ่าน validation THE SYSTEM SHALL สร้าง opaque object key ฝั่ง server
- 3.10 WHEN registration resubmission ไม่ส่ง `kycPhoto` THE SYSTEM SHALL คง `KycPhotoObjectKey` เดิม
- 3.11 THE SYSTEM SHALL ไม่คืน `KycPhotoObjectKey` ใน public registration response
- 3.12 THE SYSTEM SHALL ไม่เพิ่ม public KYC read endpoint
- 3.13 THE SYSTEM SHALL ไม่เพิ่ม KYC review endpoint
- 3.14 THE SYSTEM SHALL ไม่เพิ่ม KYC status endpoint
- 3.15 WHEN registration resubmission ส่ง `kycPhoto` ใหม่ THE SYSTEM SHALL persist object key ใหม่แทน key เดิม
- 3.16 WHEN database commit ของ object key ใหม่สำเร็จ THE SYSTEM SHALL ลบ KYC object เดิม
- 3.17 IF database commit ของ object key ใหม่ล้มเหลว THEN THE SYSTEM SHALL ลบ KYC object ใหม่

## REQ-4: Native JSON บน SQL Server 2025

**User Story:** As a platform engineer, I want native JSON ใช้เฉพาะ column ที่อนุมัติ, so that schema ใช้ SQL Server 2025 capability โดยไม่เปลี่ยน JSON storage อื่นเกิน scope

**Acceptance Criteria (EARS):**
- 4.1 THE SYSTEM SHALL map `admin.ProvisioningOperations.Result` เป็น native SQL type `json`
- 4.2 THE SYSTEM SHALL map `merch.UserOutbox.Payload` เป็น native SQL type `json`
- 4.3 THE SYSTEM SHALL map `merch.Merchants.Metadata` เป็น native SQL type `json`
- 4.4 THE SYSTEM SHALL map `shop.CartItems.Metadata` เป็น native SQL type `json`
- 4.5 THE SYSTEM SHALL map `shop.OrderItems.Metadata` เป็น native SQL type `json`
- 4.6 THE SYSTEM SHALL คง JSON-like columns อื่นเป็น `nvarchar(max)` เมื่อ ERD ไม่ได้สั่งเปลี่ยน
- 4.7 THE SYSTEM SHALL configure SQL Server compatibility level 170 ใน runtime DbContext
- 4.8 THE SYSTEM SHALL configure SQL Server compatibility level 170 ใน design-time DbContext
- 4.9 THE SYSTEM SHALL configure SQL Server compatibility level 170 ใน provisioning DbContext
- 4.10 IF invalid JSON ถูกเขียนลงหนึ่งในห้า native JSON columns THEN THE SYSTEM SHALL reject write
- 4.11 WHEN bootstrap สร้าง runtime database THE SYSTEM SHALL ตั้ง database compatibility level เป็น 170
- 4.12 WHEN bootstrap ตรวจ runtime database THE SYSTEM SHALL assert ว่า database compatibility level เป็น 170
- 4.13 IF database engine ไม่รองรับ SQL Server 2025 native JSON THEN THE SYSTEM SHALL fail migration หรือ startup

## REQ-5: Product catalog และ generic CartItem contract

**User Story:** As a merchant user, I want เลือกเอกสารประกันด้วย product/variant contract กลาง, so that Cart พร้อมย้ายไป commerce model โดย source และราคาอยู่ภายใต้ server control

**Acceptance Criteria (EARS):**
- 5.1 THE SYSTEM SHALL คง `GET /api/v1/products` ให้ดึงข้อมูลจาก insurance stored procedure integration ปัจจุบัน
- 5.2 THE SYSTEM SHALL เปลี่ยน CartItem field `DocumentNo` เป็น `ProductCode`
- 5.3 THE SYSTEM SHALL เปลี่ยน CartItem field `ProductGroup` เป็น `VariantCode`
- 5.4 THE SYSTEM SHALL เพิ่ม nullable `VariantName` ใน CartItem
- 5.5 THE SYSTEM SHALL เพิ่ม nullable native JSON `Metadata` ใน CartItem
- 5.6 WHEN client เพิ่มรายการลง Cart THE SYSTEM SHALL รับ `productCode`
- 5.7 WHEN client เพิ่มรายการลง Cart THE SYSTEM SHALL รับ `variantCode`
- 5.8 WHEN client เพิ่มรายการลง Cart THE SYSTEM SHALL รับ positive integer `quantity`
- 5.9 THE SYSTEM SHALL ไม่บังคับ quantity ของ insurance item ให้เท่ากับ 1
- 5.10 WHEN client เพิ่มรายการลง Cart THE SYSTEM SHALL resolve product จาก insurance source ด้วย server credentials
- 5.11 WHEN product ถูก resolve THE SYSTEM SHALL snapshot `VariantName` จาก server source
- 5.12 WHEN product ถูก resolve THE SYSTEM SHALL snapshot unit price จาก server source
- 5.13 WHEN product ถูก resolve THE SYSTEM SHALL สร้าง item metadata ฝั่ง server
- 5.14 THE SYSTEM SHALL ไม่รับ arbitrary item metadata จาก client
- 5.15 THE SYSTEM SHALL ไม่รับ unit price จาก client เป็น authoritative value
- 5.16 IF product หรือ variant ไม่มีใน source THEN THE SYSTEM SHALL reject cart mutation
- 5.17 IF product ไม่พร้อมขาย THEN THE SYSTEM SHALL reject cart mutation
- 5.18 WHEN CartItem มี quantity มากกว่า 1 THE SYSTEM SHALL คูณ server-owned unit price ด้วย quantity
- 5.19 WHEN CartItem มี quantity มากกว่า 1 THE SYSTEM SHALL เก็บ server metadata หนึ่งชุดต่อ line
- 5.20 WHEN CartItem มี quantity มากกว่า 1 THE SYSTEM SHALL ตรวจ sold guard หนึ่งครั้งต่อ product/variant line

## REQ-6: สร้าง Order จาก Cart โดยตรง

**User Story:** As a merchant user, I want สร้าง Order จาก Cart โดยไม่ผ่าน CheckoutSession, so that payment flow สั้นลงและ snapshot เกิดแบบ atomic

**Acceptance Criteria (EARS):**
- 6.1 THE SYSTEM SHALL expose `POST /api/v1/orders`
- 6.2 WHEN client เรียก `POST /api/v1/orders` THE SYSTEM SHALL รับ `cartId`
- 6.3 WHEN client เรียก `POST /api/v1/orders` THE SYSTEM SHALL รับ customer `name`
- 6.4 WHEN client เรียก `POST /api/v1/orders` THE SYSTEM SHALL รับ customer `phone`
- 6.5 WHEN client เรียก `POST /api/v1/orders` THE SYSTEM SHALL รับ customer `email` แบบ optional
- 6.6 THE SYSTEM SHALL รับ claimed `amount` แบบ optional โดยใช้ Money JSON contract ที่ amount เป็น fixed-decimal string
- 6.7 WHEN สร้าง Order THE SYSTEM SHALL validate ว่า Cart อยู่ใน Merchant ของ authenticated actor
- 6.8 WHEN สร้าง Order THE SYSTEM SHALL validate ว่า Cart ยัง Open
- 6.9 WHEN coordinator อ่าน Cart THE SYSTEM SHALL capture `Version` เป็น optimistic concurrency token ฝั่ง server
- 6.10 WHEN สร้าง Order THE SYSTEM SHALL revalidate product availability จาก insurance source
- 6.11 WHEN สร้าง Order THE SYSTEM SHALL revalidate sold guard จาก insurance source
- 6.12 WHEN สร้าง Order THE SYSTEM SHALL คำนวณ total จาก server-owned price และ quantity
- 6.13 IF client ส่ง claimed amount ไม่ตรงกับ server total THEN THE SYSTEM SHALL ตอบ 400
- 6.14 IF authenticated merchant user ไม่มี `SaleCode` THEN THE SYSTEM SHALL ตอบ 403
- 6.15 IF Cart ไม่มีอยู่ใน scope ของ actor THEN THE SYSTEM SHALL ตอบ 404
- 6.16 IF Cart ถูกแก้ไขหลัง version ที่ตรวจไว้ THEN THE SYSTEM SHALL ตอบ 409
- 6.17 IF Cart ปิดแล้ว THEN THE SYSTEM SHALL ตอบ 409
- 6.18 IF product ไม่พร้อมขายตอนสร้าง Order THEN THE SYSTEM SHALL ตอบ 409
- 6.19 IF insurance source ใช้งานไม่ได้ THEN THE SYSTEM SHALL ตอบ 503
- 6.20 WHEN validation ผ่าน THE SYSTEM SHALL สร้าง Order สถานะ Pending
- 6.21 WHEN validation ผ่าน THE SYSTEM SHALL snapshot CartItems เป็น OrderItems
- 6.22 WHEN validation ผ่าน THE SYSTEM SHALL snapshot customer fields ลง Order
- 6.23 WHEN validation ผ่าน THE SYSTEM SHALL enqueue customer notification ผ่าน outbox
- 6.24 WHEN validation ผ่าน THE SYSTEM SHALL เปลี่ยน Cart เป็น CheckedOut
- 6.25 THE SYSTEM SHALL commit Order, OrderItems, notification outbox และ Cart state ใน transaction เดียวของ MerchantRuntime database
- 6.26 IF operation ใดใน transaction ล้มเหลว THEN THE SYSTEM SHALL rollback transaction ทั้งหมด
- 6.27 WHEN Order ถูกสร้างสำเร็จ THE SYSTEM SHALL ตอบ 201
- 6.28 WHEN Order ถูกสร้างสำเร็จ THE SYSTEM SHALL คืน `orderId`
- 6.29 WHEN Order ถูกสร้างสำเร็จ THE SYSTEM SHALL คืน `orderNo`
- 6.30 WHEN Order ถูกสร้างสำเร็จ THE SYSTEM SHALL คืน `status` เป็น string `Pending`
- 6.31 WHEN Order ถูกสร้างสำเร็จ THE SYSTEM SHALL คืน `amount` เป็น Money JSON object
- 6.32 IF client retry Cart ที่สร้าง Order สำเร็จแล้ว THEN THE SYSTEM SHALL ตอบ 409
- 6.33 THE SYSTEM SHALL ไม่สร้าง Cart-to-Order result ledger สำหรับ replay response
- 6.34 IF order creation request ไม่ผ่าน input validation THEN THE SYSTEM SHALL ตอบ 400
- 6.35 WHEN concurrent order requests ใช้ Cart เดียวกัน THE SYSTEM SHALL commit สำเร็จได้หนึ่ง request เท่านั้น
- 6.36 WHEN OrderItem ถูกสร้างใน flow นี้ THE SYSTEM SHALL snapshot discount เป็นศูนย์ใน line currency

## REQ-7: Generic OrderItem, metadata privacy และ read surfaces

**User Story:** As an API consumer, I want OrderItem ใช้ product/variant snapshot กลางและแยกข้อมูลที่เปิดเผยตาม audience, so that domain ไม่ผูกกับประกันและไม่รั่วข้อมูลอ่อนไหว

**Acceptance Criteria (EARS):**
- 7.1 THE SYSTEM SHALL เปลี่ยน OrderItem field `DocumentNo` เป็น `ProductCode`
- 7.2 THE SYSTEM SHALL เปลี่ยน OrderItem field `ProductGroup` เป็น `VariantCode`
- 7.3 THE SYSTEM SHALL เพิ่ม nullable `VariantName` ใน OrderItem
- 7.4 THE SYSTEM SHALL เพิ่ม nullable native JSON `Metadata` ใน OrderItem
- 7.5 THE SYSTEM SHALL ลบ OrderItem field `DocumentType`
- 7.6 THE SYSTEM SHALL ลบ OrderItem field `PolicyNumber`
- 7.7 THE SYSTEM SHALL ลบ OrderItem field `StartDate`
- 7.8 THE SYSTEM SHALL ลบ OrderItem field `EndDate`
- 7.9 THE SYSTEM SHALL ลบ OrderItem fields ที่ขึ้นต้นด้วย `Insured`
- 7.10 THE SYSTEM SHALL สร้าง OrderItem metadata จาก server facts เท่านั้น
- 7.11 THE SYSTEM SHALL ไม่เก็บ insured PII ใน CartItem metadata
- 7.12 THE SYSTEM SHALL ไม่เก็บ insured name, identity number, date of birth, phone, email หรือ address ใน OrderItem metadata
- 7.13 WHERE insurance facts มีอยู่ THE SYSTEM SHALL อนุญาต metadata keys `sourceType`, `documentType`, `policyNumber`, `startDate` และ `endDate`
- 7.14 WHEN customer summary ถูกอ่าน THE SYSTEM SHALL ไม่คืน OrderItem metadata
- 7.15 WHEN authorized merchant detail ถูกอ่าน THE SYSTEM SHALL คืน server-owned OrderItem metadata
- 7.16 WHEN merchant detail เปิดเผย OrderItem metadata THE SYSTEM SHALL เขียน `shop.OrderItemRevealAudits`
- 7.17 THE SYSTEM SHALL เปลี่ยน order read contract ให้ใช้ `productCode`
- 7.18 THE SYSTEM SHALL เปลี่ยน order read contract ให้ใช้ `variantCode`
- 7.19 THE SYSTEM SHALL ไม่คืน insurance-specific top-level item fields ที่ถูกลบ
- 7.20 THE SYSTEM SHALL classify `policyNumber` เป็น sensitive business identifier ที่เก็บได้แต่ต้องผ่าน reveal audit

## REQ-8: ตัด Checkout และ policy-specific surfaces

**User Story:** As a maintainer, I want ลบ persisted checkout และ policy-specific implementation ทั้งชุด, so that runtime ไม่มีสอง flow หรือ dead authorization surface ค้างอยู่

**Acceptance Criteria (EARS):**
- 8.1 THE SYSTEM SHALL ลบตาราง `shop.CheckoutSessions`
- 8.2 THE SYSTEM SHALL ลบตาราง `shop.CheckoutSessionItems`
- 8.3 THE SYSTEM SHALL ลบตาราง `shop.OrderItemPolicies`
- 8.4 THE SYSTEM SHALL ลบตาราง `shop.OrderItemPolicyAudits`
- 8.5 THE SYSTEM SHALL ลบ Checkouts module จาก source
- 8.6 THE SYSTEM SHALL ลบ Checkouts module registration จาก dependency injection
- 8.7 THE SYSTEM SHALL ลบ `/api/v1/checkouts/**`
- 8.8 THE SYSTEM SHALL ลบ policy write routes
- 8.9 THE SYSTEM SHALL ลบ policy report routes
- 8.10 THE SYSTEM SHALL ลบ policy readers
- 8.11 THE SYSTEM SHALL ลบ policy-specific escape-hatch allowlist entries
- 8.12 THE SYSTEM SHALL ลบ permission group `merchants.policies`
- 8.13 THE SYSTEM SHALL ลบ permission group `policies`
- 8.14 THE SYSTEM SHALL ลบ permission keys `merchants.policies.read`, `merchants.policies.write`, `policies.read` และ `policies.write`
- 8.15 WHEN seed เสร็จ THE SYSTEM SHALL มี permission keys 19 รายการ
- 8.16 WHEN seed เสร็จ THE SYSTEM SHALL มี permission groups 7 รายการ
- 8.17 IF client เรียก checkout route เก่า THEN THE SYSTEM SHALL ตอบ 404
- 8.18 IF client เรียก policy route เก่า THEN THE SYSTEM SHALL ตอบ 404
- 8.19 THE SYSTEM SHALL ไม่มี production code reference ไปยัง Checkouts module
- 8.20 THE SYSTEM SHALL ไม่มี production code reference ไปยัง OrderItem policy entities
- 8.21 THE SYSTEM SHALL ลบ role grants ที่อ้าง policy permission keys ทั้งสี่รายการ

## REQ-9: Order lifecycle จาก Payment events

**User Story:** As a merchant user, I want Order สะท้อนผล payment ครบทั้งสำเร็จ ล้มเหลว และหมดอายุ, so that สถานะ commerce ไม่ค้าง Pending

**Acceptance Criteria (EARS):**
- 9.1 THE SYSTEM SHALL ใช้ Order status `Pending=0`
- 9.2 THE SYSTEM SHALL ใช้ Order status `Paid=1`
- 9.3 THE SYSTEM SHALL ใช้ Order status `Failed=2`
- 9.4 THE SYSTEM SHALL ใช้ Order status `Expired=3`
- 9.5 THE SYSTEM SHALL reserve Order status `Refunded=4`
- 9.6 THE SYSTEM SHALL ใช้ Order status `Cancelled=5` เป็น approved deviation จาก ERD
- 9.7 WHEN payment ยืนยันสำเร็จ THE SYSTEM SHALL emit `PaymentPaid` ผ่าน outbox
- 9.8 WHEN payment ล้มเหลว THE SYSTEM SHALL emit `PaymentFailed` ผ่าน outbox
- 9.9 WHEN payment หมดอายุ THE SYSTEM SHALL emit `PaymentExpired` ผ่าน outbox
- 9.10 WHEN Order สถานะ Pending รับ `PaymentPaid` ที่ amount และ currency ตรง THE SYSTEM SHALL เปลี่ยน Order เป็น Paid
- 9.11 IF `PaymentPaid` มี amount ไม่ตรง Order THEN THE SYSTEM SHALL ไม่เปลี่ยน Order เป็น Paid
- 9.12 IF `PaymentPaid` มี currency ไม่ตรง Order THEN THE SYSTEM SHALL ไม่เปลี่ยน Order เป็น Paid
- 9.13 WHEN Order สถานะ Pending รับ `PaymentFailed` THE SYSTEM SHALL เปลี่ยน Order เป็น Failed
- 9.14 WHEN Order สถานะ Pending รับ `PaymentExpired` THE SYSTEM SHALL เปลี่ยน Order เป็น Expired
- 9.15 WHEN event เดิมถูก deliver ซ้ำ THE SYSTEM SHALL ประมวลผลแบบ idempotent
- 9.16 IF Order อยู่สถานะ Paid, Cancelled หรือ Refunded THEN THE SYSTEM SHALL ไม่เปลี่ยนสถานะจาก payment event ภายหลัง
- 9.17 WHEN authorized merchant user cancel Order สถานะ Pending ที่ไม่มี active PaymentSession THE SYSTEM SHALL เปลี่ยน Order เป็น Cancelled
- 9.18 IF authorized merchant user cancel Order ที่ไม่ใช่ Pending THEN THE SYSTEM SHALL ตอบ 409
- 9.19 THE SYSTEM SHALL ไม่เพิ่ม refund API ใน scope นี้
- 9.20 THE SYSTEM SHALL ไม่สร้าง transition ไป Refunded ใน scope นี้
- 9.21 WHEN Order ถูกสร้าง THE SYSTEM SHALL ตั้ง `PaymentSessionId` เป็น null
- 9.22 WHEN Order ถูกสร้าง THE SYSTEM SHALL ตั้ง `PaymentChannel` เป็น null
- 9.23 WHEN PaymentSession ถูกสร้าง THE SYSTEM SHALL attach `PaymentSessionId` ลง Order
- 9.24 WHEN PaymentSession ถูกสร้าง THE SYSTEM SHALL snapshot payment method ลง `PaymentChannel`
- 9.25 WHEN PaymentSession ใหม่ถูกสร้างสำหรับ Order สถานะ Failed หรือ Expired THE SYSTEM SHALL เปลี่ยน Order กลับเป็น Pending
- 9.26 IF PaymentSession ใหม่ถูกขอสำหรับ Order สถานะ Paid, Cancelled หรือ Refunded THEN THE SYSTEM SHALL ตอบ 409
- 9.27 WHEN Order สถานะ Failed หรือ Expired รับ `PaymentPaid` ที่ตรวจสอบแล้ว THE SYSTEM SHALL เปลี่ยน Order เป็น Paid
- 9.28 IF Order สถานะ Pending มี PaymentSession สถานะ Created หรือ Redirected THEN THE SYSTEM SHALL ตอบ 409 ต่อ manual cancel

## REQ-10: Fresh baseline และ database reset

**User Story:** As a platform engineer, I want migration baseline ใหม่ที่สร้าง target database จากศูนย์ได้, so that big-bang cutover ไม่มี legacy migration chain หรือ schema residue

**Acceptance Criteria (EARS):**
- 10.1 THE SYSTEM SHALL ลบ EF migration history files เดิมออกจาก source tree
- 10.2 THE SYSTEM SHALL ลบ model snapshot เดิมออกจาก source tree
- 10.3 THE SYSTEM SHALL สร้าง migration baseline ใหม่ชื่อ `InitialSchema`
- 10.4 WHEN apply `InitialSchema` กับ database ว่าง THE SYSTEM SHALL สร้าง target EF schema ครบ
- 10.5 WHEN apply baseline กับ database ว่าง THE SYSTEM SHALL สร้าง raw SQL objects ที่ runtime ต้องใช้ครบ
- 10.6 WHEN apply baseline กับ database ว่าง THE SYSTEM SHALL สร้าง indexes ตาม target model ครบ
- 10.7 WHEN apply baseline กับ database ว่าง THE SYSTEM SHALL สร้าง foreign keys ตาม target model ครบ
- 10.8 WHEN apply baseline กับ database ว่าง THE SYSTEM SHALL สร้าง check constraints ตาม target model ครบ
- 10.9 WHEN apply baseline กับ database ว่าง THE SYSTEM SHALL สร้าง database grants ตาม least-privilege matrix ครบ
- 10.10 WHEN apply baseline กับ database ว่าง THE SYSTEM SHALL seed bootstrap data ครบ
- 10.11 WHEN apply baseline กับ database ว่าง THE SYSTEM SHALL seed demo data ที่ยังอยู่ใน supported scope
- 10.12 THE SYSTEM SHALL ไม่รองรับ in-place data migration จาก schema ก่อนหน้า
- 10.13 THE SYSTEM SHALL ไม่รองรับ data backfill จาก Checkout หรือ policy tables ที่ถูกลบ
- 10.14 IF baseline rollback ถูกเรียกใน non-production environment THEN THE SYSTEM SHALL ถอด objects ที่ baseline สร้างใน dependency-safe order
- 10.15 THE SYSTEM SHALL ไม่รัน destructive reset บน production โดยอัตโนมัติ

## REQ-11: Security และ transaction boundaries

**User Story:** As a security owner, I want ERD reset คง authorization, tenant isolation และ sensitive-data controls, so that schema change ไม่เปิดช่องข้าม Merchant หรือรั่ว credential/PII

**Acceptance Criteria (EARS):**
- 11.1 WHILE authenticated actor เข้าถึง Cart THE SYSTEM SHALL enforce merchant query filter แบบ deny-default
- 11.2 WHILE authenticated actor เขียน Cart THE SYSTEM SHALL enforce merchant write guard
- 11.3 WHILE authenticated actor เข้าถึง Order THE SYSTEM SHALL enforce merchant query filter แบบ deny-default
- 11.4 WHILE authenticated actor เขียน Order THE SYSTEM SHALL enforce merchant write guard
- 11.5 WHEN `POST /api/v1/orders` ถูกเรียก THE SYSTEM SHALL require merchant-user authorization
- 11.6 WHEN `POST /api/v1/orders` ถูกเรียกจาก browser session THE SYSTEM SHALL require CSRF validation
- 11.7 THE SYSTEM SHALL ไม่ log KYC object keys
- 11.8 THE SYSTEM SHALL ไม่ log customer PII จาก order creation request
- 11.9 THE SYSTEM SHALL ไม่เก็บ secret material ใน native JSON metadata
- 11.10 THE SYSTEM SHALL คง payment webhook verification controls ปัจจุบัน
- 11.11 THE SYSTEM SHALL คง payment idempotency controls ปัจจุบัน
- 11.12 THE SYSTEM SHALL คง vault encryption controls ปัจจุบัน

## REQ-12: API cutover และ consumer migration guide

**User Story:** As an API consumer, I want contract ใหม่และคู่มือ cutover ที่ชัด, so that frontend เปลี่ยนจาก Checkout/policy contract ไป Cart→Order ได้ใน release เดียว

**Acceptance Criteria (EARS):**
- 12.1 THE SYSTEM SHALL publish OpenAPI contract สำหรับ `POST /api/v1/orders`
- 12.2 THE SYSTEM SHALL publish OpenAPI contract สำหรับ CartItem `productCode` และ `variantCode`
- 12.3 THE SYSTEM SHALL ถอด checkout operations ออกจาก OpenAPI contract
- 12.4 THE SYSTEM SHALL ถอด policy operations ออกจาก OpenAPI contract
- 12.5 THE SYSTEM SHALL มี backend migration guide สำหรับ frontend consumers
- 12.6 THE SYSTEM SHALL ระบุ old-to-new route mapping ใน migration guide
- 12.7 THE SYSTEM SHALL ระบุ old-to-new request field mapping ใน migration guide
- 12.8 THE SYSTEM SHALL ระบุ old-to-new response field mapping ใน migration guide
- 12.9 THE SYSTEM SHALL ระบุ Order status mapping ใน migration guide
- 12.10 THE SYSTEM SHALL ระบุ error status 400, 403, 404, 409 และ 503 ของ order creation ใน migration guide
- 12.11 THE SYSTEM SHALL ไม่แก้ frontend repository อื่นใน scope นี้
- 12.12 THE SYSTEM SHALL ไม่คง route alias สำหรับ checkout หรือ policy API เก่า

## REQ-13: Verification และ operational rollout

**User Story:** As a release owner, I want หลักฐานอัตโนมัติและ rollback plan ครบก่อน cutover, so that ERD reset deploy ได้โดยรู้ผลกระทบและย้อนกลับได้

**Acceptance Criteria (EARS):**
- 13.1 THE SYSTEM SHALL มี unit tests สำหรับ renamed fields และ status values
- 13.2 THE SYSTEM SHALL มี unit tests พิสูจน์ว่า insurance quantity มากกว่า 1 ใช้ได้
- 13.3 THE SYSTEM SHALL มี unit tests พิสูจน์ว่า metadata มาจาก server เท่านั้น
- 13.4 THE SYSTEM SHALL มี unit tests สำหรับ optional KYC photo และ resubmission preservation
- 13.5 THE SYSTEM SHALL มี unit tests สำหรับ active-only IAM resolution
- 13.6 THE SYSTEM SHALL มี unit tests สำหรับ Order terminal transitions
- 13.7 THE SYSTEM SHALL มี integration tests สำหรับ successful Cart-to-Order transaction
- 13.8 THE SYSTEM SHALL มี integration tests สำหรับ Cart-to-Order rollback
- 13.9 THE SYSTEM SHALL มี integration tests สำหรับ order retry ที่ตอบ 409
- 13.10 THE SYSTEM SHALL มี API tests สำหรับ authorization และ CSRF ของ `POST /api/v1/orders`
- 13.11 THE SYSTEM SHALL มี API tests พิสูจน์ว่า routes ที่ลบตอบ 404
- 13.12 THE SYSTEM SHALL มี architecture tests พิสูจน์ว่าไม่มี Checkouts และ policy references ค้าง
- 13.13 THE SYSTEM SHALL มี architecture tests พิสูจน์ permission seed จำนวน 19 keys และ 7 groups
- 13.14 THE SYSTEM SHALL มี database tests พิสูจน์ schema, native JSON, indexes, constraints, grants และ seeds บน fresh database
- 13.15 THE SYSTEM SHALL มี end-to-end test ครอบ Cart→Order→Payment redirect→webhook→Paid
- 13.16 THE SYSTEM SHALL มี end-to-end test ครอบ PaymentFailed→Order Failed
- 13.17 THE SYSTEM SHALL มี end-to-end test ครอบ PaymentExpired→Order Expired
- 13.18 THE SYSTEM SHALL block merge เมื่อ build ด้วย warnings-as-errors ไม่ผ่าน
- 13.19 THE SYSTEM SHALL block merge เมื่อ unit หรือ integration test suite ไม่ผ่าน
- 13.20 THE SYSTEM SHALL block merge เมื่อ spec trace gate ไม่ผ่าน
- 13.21 THE SYSTEM SHALL block merge เมื่อ secret scan ไม่ผ่าน
- 13.22 THE SYSTEM SHALL block production deployment เมื่อ staging deployment ไม่ผ่าน
- 13.23 THE SYSTEM SHALL require operator-confirmed database backup ก่อน production reset
- 13.24 THE SYSTEM SHALL require rollback plan ที่คืน old application และ restore database backup เดิมก่อน production deployment
- 13.25 THE SYSTEM SHALL มี integration test พิสูจน์ว่า concurrent order requests บน Cart เดียว commit ได้หนึ่งครั้ง
- 13.26 THE SYSTEM SHALL มี unit tests สำหรับ Failed/Expired payment retry และ late PaymentPaid
- 13.27 THE SYSTEM SHALL มี API test พิสูจน์ว่า active PaymentSession ทำให้ manual cancel ตอบ 409
- 13.28 THE SYSTEM SHALL มี database test พิสูจน์ database compatibility level 170
- 13.29 THE SYSTEM SHALL มี tests สำหรับ KYC replacement cleanup ทั้ง commit success และ database failure
- 13.30 THE SYSTEM SHALL มี unit test พิสูจน์ว่า direct order creation snapshot discount เป็นศูนย์

## Edge Cases & Open Questions

### Decisions ที่ยืนยันแล้ว

- **ERD authority:** exact replacement เฉพาะรายการที่ ERD สั่งเปลี่ยนหรือลบ; โครงสร้างอื่นคงไว้ตาม REQ-1.4
- **Migration:** reset database และ squash migration history; ไม่มี in-place migration หรือ backfill
- **API:** big-bang cutover; ไม่มี compatibility alias
- **Catalog:** runtime ใช้ insurance stored procedure source เดียว
- **Order creation:** synchronous coordinator ภายใน MerchantRuntime transaction เดียว
- **Retry:** การสร้าง Order ซ้ำจาก Cart เดิมตอบ 409; ไม่มี replay ledger
- **Metadata:** server facts เท่านั้น; client ส่ง arbitrary JSON ไม่ได้; insured PII ห้ามเก็บ
- **KYC:** optional single `kycPhoto`; key-only; ไม่มี status/review/read API
- **Lifecycle:** รองรับ Paid/Failed/Expired; Refunded เป็น reserved state; คง manual Cancelled เป็น deviation
- **Quantity:** insurance item รองรับ quantity มากกว่า 1
- **Policy retirement:** ลบ table, code, route, report, permission group, permission key และ grant ทั้งหมด
- **Frontend:** ส่ง backend migration guide เท่านั้น; frontend repositories อยู่นอก scope

### Edge cases ที่ต้องพิสูจน์ใน design/tests

- Product ที่ final authoritative lookup/sold probe ตอน create-order ตรวจพบว่าถูกขายหรือปิดขาย ต้อง fail ด้วย
  409 โดยไม่สร้าง partial Order; guarantee สิ้นสุดที่ probe เพราะ upstream ไม่มี revision/hold token
- Client retry หลัง server commit สำเร็จแต่ response สูญหายจะได้ 409 ตาม trade-off ที่อนุมัติ
- Payment event ซ้ำหรือล่าช้าหลัง Order เข้าสถานะ terminal ต้องไม่เปลี่ยน terminal result
- Registration resubmission ที่ไม่มี `kycPhoto` ต้องไม่ล้าง key เดิม
- Native JSON mapping ต้องทำงานบน SQL Server compatibility level 170 ทั้ง runtime, design-time และ provisioning
- Database reset บน production ต้องเกิดผ่าน operator-confirmed release procedure เท่านั้น

### Open questions

- ไม่มีคำถามค้างหลัง `/spec-analyze`; findings F1-F10 มี decision ครบ

### /spec-analyze findings log — anchor: b143dc3 (2026-08-07; ไฟล์ยัง untracked ตอน audit จึงใช้ HEAD ณ เวลานั้น)

| # | Category | Finding | Decision |
|---|---|---|---|
| F1 | Ambiguity / gap | REQ-6.2 ไม่รับ Cart version แต่ REQ-6.9 ต้องตรวจ concurrency และ REQ-6.33 ห้ามมี result ledger | ใช้ server-captured `Cart.Version` เป็น optimistic token; transaction เดียวทำให้ concurrent request สำเร็จหนึ่งครั้ง (แก้ 6.9; เพิ่ม 6.35) |
| F2 | Conflicting constraints | REQ-1.10 คง discount แต่ direct-order contract ไม่มี discount source และ REQ-5.14 ห้าม client metadata | คง columns แต่ direct-order flow snapshot discount เป็นศูนย์ (เพิ่ม 6.36) |
| F3 | Ambiguity | REQ-1.9 คง payment snapshot fields แต่ POST Order ไม่มี payment input | เริ่ม Order ด้วย null; attach PaymentSessionId และ method เมื่อสร้าง PaymentSession (เพิ่ม 9.21-9.24) |
| F4 | Logical gap | Failed/Expired terminal ตาม REQ-9.16 เดิมขัด payment retry และ late verified Paid | เปิด payment attempt ใหม่แล้วกลับ Pending; verified Paid เปลี่ยน Failed/Expired เป็น Paid; Paid/Cancelled/Refunded เท่านั้นที่ terminal (แก้ 9.16; เพิ่ม 9.25-9.27) |
| F5 | Gap | Manual cancel แข่งกับ PaymentSession ที่กำลัง redirect อาจเกิด charged-but-cancelled | ตอบ 409 เมื่อมี PaymentSession Created/Redirected (แก้ 9.17; เพิ่ม 9.28) |
| F6 | Ambiguity | REQ-6.6/6.30/6.31 ไม่ pin optional amount และ wire status | claimed amount optional Money object; response status เป็น `Pending`; response amount เป็น Money object (แก้ 6.6/6.30/6.31) |
| F7 | Unstated assumption | DbContext compatibility 170 ไม่รับประกัน database compatibility หรือ engine capability | bootstrap ตั้งและ assert level 170; fail migration/startup เมื่อ engine ไม่รองรับ SQL Server 2025 native JSON (เพิ่ม 4.11-4.13) |
| F8 | Security ambiguity | REQ-7.11-7.16 อนุญาต policyNumber แต่คำว่า insured PII ไม่มี boundary ชัด | อนุญาต policyNumber เป็น sensitive business identifier; ห้าม insured name/ID/DOB/contact/address; ทุก reveal มี audit (แก้ 7.12; เพิ่ม 7.20) |
| F9 | Gap | KYC resubmission เปลี่ยน key แต่ไม่กำหนด object cleanup เมื่อ commit สำเร็จหรือล้มเหลว | commit key ใหม่ก่อนลบ object เดิม; DB fail ต้องลบ object ใหม่ (เพิ่ม 3.15-3.17) |
| F10 | Unstated assumption | Insurance ProductCode อ้าง document เดียวแต่ quantity มากกว่า 1 ไม่มี billing/metadata semantics | quantity คูณ server price; metadata หนึ่งชุดและ sold guard หนึ่งครั้งต่อ line (เพิ่ม 5.18-5.20) |
