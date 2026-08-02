# Requirements: Purchase Flow Completion (ปิด flow ซื้อประกันภัย End-to-End)

> Status: approved 2026-08-02, amended 2026-08-02 (REQ-6.3 — Discount เป็น Money ตามมาตรฐาน repo, จาก design critique F-13)

## Overview

ระบบชำระเงินกลาง (API) รองรับการขายเอกสารประกันภัยผ่านตัวแทน: Merchant SPA สร้างคำสั่งซื้อ (เลือกสินค้า → ตะกร้า → เช็คเอาท์ → คำสั่งซื้อ) และ Customer SPA ให้ลูกค้าชำระเงินจากลิงก์ที่ส่งทาง SMS/อีเมล ผ่าน 2C2P แบบ redirect-only ปัจจุบัน chain ระดับ event ต่อครบแล้ว แต่ flow ยังไม่สมบูรณ์: เพิ่มสินค้าเข้าตะกร้าไม่ได้ (409 ทุกครั้ง), เปิด checkout ซ้ำจากตะกร้าเดิมได้ไม่จำกัด, payment session ค้างบล็อกคำสั่งซื้อถาวร, ยกเลิกคำสั่งซื้อไม่ได้, เอกสารที่ขายแล้วยังโผล่ในผลค้นหา, ลูกค้าไม่มีทางชำระเงินจริง และ backend ยังไม่มี field ที่หน้าจอ Merchant SPA ใช้ (ช่องทางชำระ, ส่วนลด, ข้อมูลลูกค้า, เลขคำสั่งซื้อ) spec นี้ปิดช่องว่างทั้งหมดให้ flow เดินได้จริงตั้งแต่ค้นหาสินค้าจนลูกค้าชำระเงินสำเร็จ

ขอบเขตที่ตัดออก (ตัดสินใจแล้ว): ระบบต้นทางเอกสาร (hippodb/mammothdb) เป็นระบบภายนอกแบบ read-only — ไม่มีการเขียนสถานะกลับ; reservation ระหว่าง checkout→payment ไม่ทำรอบนี้; การส่ง SMS/อีเมลจริง (ปัจจุบันเป็น logging stub) อยู่นอกขอบเขต

## REQ-1: เพิ่มสินค้าเข้าตะกร้า (แก้ add-item 409)

**User Story:** As a merchant user, I want เพิ่มเอกสารประกันเข้าตะกร้าได้สำเร็จ, so that เริ่มสร้างคำสั่งซื้อได้

**Acceptance Criteria (EARS):**
- 1.1 WHEN merchant user เพิ่มสินค้าที่ยังไม่มีในตะกร้า THE SYSTEM SHALL บันทึกรายการใหม่เป็น INSERT สำเร็จ (ไม่เกิด concurrency conflict จากการที่ EF ตีความ entity ใหม่เป็น UPDATE)
- 1.2 WHEN merchant user เพิ่มสินค้าเดิมซ้ำ THE SYSTEM SHALL เพิ่มจำนวนบนรายการเดิม (พฤติกรรม merge เดิมคงไว้)
- 1.3 IF สินค้าที่เพิ่มมี PaymentStatus ไม่เท่ากับ UNPAID THEN THE SYSTEM SHALL ปฏิเสธด้วย 400 (guard เดิมคงไว้)
- 1.4 THE SYSTEM SHALL กำหนดค่า Id ของ cart item จากฝั่ง client (`ValueGeneratedNever`) ตรงกันทั้งสอง configuration mirror (module Infrastructure และ Persistence.MerchantRuntime)

## REQ-2: ปิดวงจร cart–checkout (กันเปิด checkout ซ้ำ)

**User Story:** As a merchant user, I want ตะกร้าถูกล็อกเมื่อเริ่ม checkout และปลดล็อกเมื่อยกเลิก, so that ไม่เกิดคำสั่งซื้อซ้ำจากตะกร้าเดียวกัน

**Acceptance Criteria (EARS):**
- 2.1 WHEN checkout session ถูกเปิดจากตะกร้าสำเร็จ THE SYSTEM SHALL เปลี่ยนสถานะตะกร้าเป็น CheckedOut
- 2.2 IF มีการขอเปิด checkout จากตะกร้าที่สถานะไม่ใช่ Open THEN THE SYSTEM SHALL ปฏิเสธด้วย 409
- 2.3 IF มีการขอเปิด checkout ขณะที่ตะกร้าเดียวกันมี checkout session เปิดอยู่ (Started หรือ Confirmed) THEN THE SYSTEM SHALL ปฏิเสธด้วย 409
- 2.4 THE SYSTEM SHALL บังคับ 1 open checkout session ต่อ 1 ตะกร้าด้วย filtered unique index บน `CheckoutSessions.CartId` (backstop ระดับ DB กัน race)
- 2.5 WHEN merchant user ยกเลิก checkout session ที่สถานะ Started THE SYSTEM SHALL เปลี่ยน session เป็น Abandoned และเปลี่ยนตะกร้ากลับเป็น Open
- 2.6 IF มีการขอยกเลิก checkout session ที่สถานะ Confirmed THEN THE SYSTEM SHALL ปฏิเสธด้วย 409
- 2.7 IF มีการแก้ไขรายการในตะกร้า (เพิ่ม/ลบ/แก้จำนวน/ล้าง) ที่สถานะไม่ใช่ Open THEN THE SYSTEM SHALL ปฏิเสธด้วย 409
- 2.8 THE SYSTEM SHALL gate endpoint ยกเลิก checkout ด้วย policy merchant-user + CSRF
- 2.9 WHEN มีการยกเลิก checkout session ที่สถานะ Abandoned อยู่แล้ว THE SYSTEM SHALL ตอบสำเร็จโดยไม่เปลี่ยนแปลงอะไร (idempotent)

## REQ-3: อายุ payment session (ปลดบล็อกคำสั่งซื้อ)

**User Story:** As a merchant user, I want payment session ที่ถูกทิ้งค้างหมดอายุได้เอง, so that คำสั่งซื้อไม่ถูกบล็อกถาวรและเปิด session ใหม่ได้

**Acceptance Criteria (EARS):**
- 3.1 THE SYSTEM SHALL ถือว่า payment session ที่สถานะ Created หรือ Redirected เกิน 24 ชั่วโมงนับจากเวลาสร้างเป็น session ที่หมดอายุ (คำนวณจาก CreatedAt ไม่เพิ่มคอลัมน์)
- 3.2 WHEN มีการขอเปิด payment session ใหม่ขณะที่ session เดิมของคำสั่งซื้อนั้นหมดอายุแล้ว THE SYSTEM SHALL เปลี่ยน session เดิมเป็น Expired และเปิด session ใหม่ได้ ภายใน unit of work เดียวกัน
- 3.3 WHEN มีการขอเปิด payment session ใหม่ขณะที่ session เดิมยังไม่หมดอายุ THE SYSTEM SHALL ปฏิเสธด้วย 409 (พฤติกรรม one-open-session เดิมคงไว้)
- 3.4 IF PSP ยืนยันการชำระเงินของ session ที่ถูกเปลี่ยนเป็น Expired ไปแล้ว THEN THE SYSTEM SHALL ปฏิเสธการ transition (MarkPaid throw) และความล้มเหลวต้องมองเห็นได้ในผลลัพธ์ของ webhook/status check ไม่เงียบหาย
- 3.5 IF เกิดกรณีตาม 3.4 THEN THE SYSTEM SHALL log ระดับ Critical และ payment-status ตอบ `failed` — การคืนเงินเป็นกระบวนการ manual นอกระบบ (TTL 24 ชั่วโมงยาวกว่าอายุ hosted page ของ 2C2P มาก เคสนี้แทบเกิดไม่ได้)

## REQ-4: ยกเลิกคำสั่งซื้อ

**User Story:** As a merchant user, I want ยกเลิกคำสั่งซื้อที่ยังไม่ชำระเงินได้, so that คำสั่งซื้อที่ลูกค้าไม่จ่ายไม่ค้างในระบบตลอดไป

**Acceptance Criteria (EARS):**
- 4.1 WHEN merchant user ยกเลิกคำสั่งซื้อสถานะ AwaitingPayment ที่ไม่มี payment session เปิดอยู่ THE SYSTEM SHALL เปลี่ยนคำสั่งซื้อเป็น Cancelled
- 4.2 IF คำสั่งซื้อมี payment session เปิดอยู่ที่หมดอายุแล้ว THEN THE SYSTEM SHALL เปลี่ยน session เป็น Expired แล้วจึงยกเลิกคำสั่งซื้อ
- 4.3 IF คำสั่งซื้อมี payment session เปิดอยู่ที่ยังไม่หมดอายุ THEN THE SYSTEM SHALL ปฏิเสธด้วย 409 (ลูกค้าอาจกำลังชำระเงิน)
- 4.4 IF คำสั่งซื้อสถานะ Paid THEN THE SYSTEM SHALL ปฏิเสธการยกเลิก
- 4.5 WHEN มีการยกเลิกคำสั่งซื้อที่สถานะ Cancelled อยู่แล้ว THE SYSTEM SHALL ตอบสำเร็จโดยไม่เปลี่ยนแปลงอะไร (idempotent)
- 4.6 THE SYSTEM SHALL gate endpoint ยกเลิกด้วย policy merchant-user + CSRF โดยไม่เพิ่ม permission key ใหม่

## REQ-5: กรองเอกสารที่ขายแล้วออกจากผลค้นหา

**User Story:** As a merchant user, I want ผลค้นหาที่กรองสถานะ UNPAID ไม่แสดงเอกสารที่ขายผ่านระบบไปแล้ว, so that ไม่หยิบเอกสารที่ขายแล้วมาขายซ้ำ

**Acceptance Criteria (EARS):**
- 5.1 WHERE คำขอค้นหาส่ง filter สถานะการชำระเป็น UNPAID ไปยังระบบต้นทาง THE SYSTEM SHALL กรองแถวที่ mirror ฝั่งเรา (`shop.Products`) มี PaymentStatus เป็น PAID ออกจากผลลัพธ์ก่อนตอบ (ตัดสินจากค่า filter ที่ส่งให้ SP เท่านั้น — ไม่ส่ง filter = ALL แสดงได้ทุกสถานะ)
- 5.2 THE SYSTEM SHALL ยอมรับว่า TotalRows ที่ระบบต้นทางนับมาอาจมากกว่าจำนวนแถวที่แสดงจริงชั่วขณะ (เอกสารที่เพิ่งขาย) — ไม่ re-query เพื่อแก้ตัวเลข
- 5.3 THE SYSTEM SHALL คงพฤติกรรมเดิม: mirror ที่เป็น PAID ไม่ถูก downgrade โดยข้อมูลต้นทาง และ add-item/checkout ปฏิเสธเอกสารที่ไม่ใช่ UNPAID
- 5.4 IF consumer ทำเครื่องหมาย PAID ให้ product ที่เป็น PAID อยู่แล้ว THEN THE SYSTEM SHALL log ระดับ Critical (สัญญาณขายซ้ำ) แทน no-op เงียบ

## REQ-6: Checkout enrichment (ช่องทางชำระ, ส่วนลด, ข้อมูลลูกค้า)

**User Story:** As a merchant user, I want ระบุช่องทางชำระ ส่วนลดต่อรายการ และข้อมูลลูกค้าตอนยืนยันคำสั่งซื้อ, so that ลิงก์ชำระเงินของลูกค้าพร้อมใช้โดยลูกค้าไม่ต้องเลือกอะไรเอง

**Acceptance Criteria (EARS):**
- 6.1 WHEN merchant user เปิด checkout THE SYSTEM SHALL รับและเก็บช่องทางชำระเงิน 1 ช่องทาง (ค่า wire: `CARD` / `PROMPTPAY_QR` / `INSTALLMENT`) บน checkout session — สำหรับ `INSTALLMENT` เก็บแค่ช่องทาง ธนาคาร/จำนวนงวดลูกค้าเลือกบนหน้า 2C2P hosted
- 6.2 IF ช่องทางชำระเงินที่ส่งมาไม่อยู่ในชุดค่าที่รองรับ THEN THE SYSTEM SHALL ปฏิเสธด้วย 400
- 6.3 WHEN merchant user เปิด checkout THE SYSTEM SHALL รับส่วนลดต่อรายการเป็น `Money` (DECIMAL(19,4) + currency ตามมาตรฐาน repo, ค่าเริ่มต้น 0, สกุลเดียวกับราคาบรรทัดนั้น) และคำนวณยอดชำระต่อรายการ = เบี้ยรวม − ส่วนลด
- 6.4 IF ส่วนลดติดลบ หรือมากกว่าเบี้ยรวมของรายการนั้น THEN THE SYSTEM SHALL ปฏิเสธด้วย 400
- 6.5 THE SYSTEM SHALL ตรวจว่ายอดรวมที่ client ส่งมาเท่ากับผลรวมยอดชำระ (หลังหักส่วนลด) ที่คำนวณฝั่ง server มิฉะนั้นปฏิเสธด้วย 400
- 6.6 WHEN merchant user เปิด checkout THE SYSTEM SHALL รับข้อมูลลูกค้าเป็น 3 field: ชื่อ-นามสกุล (บังคับ), เบอร์โทรศัพท์ (บังคับ), อีเมล (ไม่บังคับ) — ตาม contract หน้าจอ Merchant SPA
- 6.7 IF ชื่อหรือเบอร์โทรศัพท์ขาด หรืออีเมล/เบอร์โทรศัพท์ที่ส่งมารูปแบบไม่ถูกต้อง (validate พื้นฐาน) THEN THE SYSTEM SHALL ปฏิเสธด้วย 400
- 6.8 WHEN checkout ถูก confirm THE SYSTEM SHALL ส่งช่องทางชำระ ส่วนลดต่อรายการ และข้อมูลลูกค้า 3 field ไปกับ event `CheckoutConfirmed` (snapshot เป็น string บน wire ตามธรรมเนียม contract)

## REQ-7: Order enrichment (เลขคำสั่งซื้อ + field ที่ carry จาก checkout)

**User Story:** As a merchant user, I want คำสั่งซื้อมีเลขอ่านง่ายและถือข้อมูลครบจาก checkout, so that อ้างอิงกับลูกค้าและระบบภายนอกได้

**Acceptance Criteria (EARS):**
- 7.1 WHEN คำสั่งซื้อถูกสร้างจาก `CheckoutConfirmed` THE SYSTEM SHALL มินต์เลขคำสั่งซื้อรูปแบบ `ORD` + ปี พ.ศ. 2 หลัก + เลข running 8 หลัก (เช่น `ORD6900000006`) จาก SQL sequence และบังคับ unique
- 7.2 THE SYSTEM SHALL เก็บช่องทางชำระ ส่วนลดต่อรายการ และข้อมูลลูกค้า 3 field บนคำสั่งซื้อ (order เป็น source of truth ตอนชำระเงิน)
- 7.3 THE SYSTEM SHALL แสดง OrderNo ใน `GET /orders`, `GET /orders/{orderId}` และ order summary
- 7.4 WHERE `GET /orders` ถูกเรียกพร้อม filter เลขคำสั่งซื้อ THE SYSTEM SHALL คืนเฉพาะคำสั่งซื้อที่ OrderNo ตรง
- 7.5 IF event `CheckoutConfirmed` หรือ `CustomerOrderNotification` เวอร์ชันเก่า (ไม่มี field ใหม่) ค้างอยู่ใน outbox ตอน deploy THEN THE SYSTEM SHALL ยัง deserialize และประมวลผลได้ (field ใหม่เป็น nullable/default)

## REQ-8: Customer payment path (ลูกค้าชำระเงินผ่านลิงก์)

**User Story:** As a customer, I want ชำระเงินจากลิงก์คำสั่งซื้อผ่าน 2C2P และเห็นผลสำเร็จ/ไม่สำเร็จ, so that จ่ายเบี้ยประกันได้เองโดยไม่ต้องมีบัญชีในระบบ

**Acceptance Criteria (EARS):**
- 8.1 WHEN ลูกค้าเรียก `POST /orders/{token}/pay` ด้วย summary token ที่ถูกต้อง THE SYSTEM SHALL สร้าง payment session จากยอดของคำสั่งซื้อฝั่ง server, เริ่ม redirect กับ 2C2P ด้วยช่องทางชำระที่ merchant เลือกไว้ และคืน `redirectUrl` — โดยไม่ต้องมี session cookie หรือ CSRF token (token คือ capability)
- 8.2 IF token ไม่ถูกต้องหรือหมดอายุ THEN THE SYSTEM SHALL ตอบ 404 โดยไม่เปิดเผยว่า token รูปแบบถูกแต่ไม่มีอยู่
- 8.3 THE SYSTEM SHALL บังคับ rate limiting บน endpoint `pay` และ `payment-status`
- 8.4 THE SYSTEM SHALL ผูก actor scope จาก merchant ของคำสั่งซื้อก่อนประมวลผล (host-layer composition ตามแบบ webhook) และไม่เปิดเผย merchantId ใน response ของลูกค้า
- 8.5 WHEN ลูกค้ากลับจาก 2C2P แล้ว Customer SPA เรียก `POST /orders/{token}/payment-status` THE SYSTEM SHALL ตรวจ session ล่าสุดของคำสั่งซื้อ: ถ้ายังเปิดอยู่ ให้ verify กับ 2C2P (fetch-to-confirm เส้นเดียวกับ webhook) แล้วคืนสถานะ `paid` / `failed` / `pending` / `cancelled`
- 8.6 WHEN การ verify กับ 2C2P ยืนยันว่าชำระแล้วและยอดตรง THE SYSTEM SHALL claim idempotency, เปลี่ยน session เป็น Paid และ enqueue `PaymentPaid` ด้วย semantics เดียวกับ webhook (webhook ที่มาช้าหรือ status check ที่มาซ้ำต้องไม่ทำให้เกิดผลซ้ำ)
- 8.7 IF ยอดหรือสกุลเงินที่ 2C2P ยืนยันไม่ตรงกับ session THEN THE SYSTEM SHALL ไม่เปลี่ยนสถานะเป็น Paid, log ระดับ Critical และคืนสถานะ `pending` แก่ลูกค้า (ไม่ยืนยันผิด ๆ — ops ตรวจสอบต่อ)
- 8.8 WHEN merchant user เรียก `GET /payments/sessions/{id}` THE SYSTEM SHALL คืนสถานะ session ภายใต้ policy merchant-user และตอบ 404 (ไม่ใช่ 409) เมื่อไม่พบ
- 8.9 THE SYSTEM SHALL ถอด `paymentSessionId` ออกจาก order summary response (ค่าเป็น null เสมอ ไม่มีผู้เขียน — สถานะการชำระดูผ่าน `payment-status`)
- 8.10 WHEN ลูกค้าเรียก `pay` ซ้ำขณะที่ session เปิดอยู่ ช่องทางเดิม และยังไม่หมดอายุ THE SYSTEM SHALL คืน `redirectUrl` เดิม (resume) แทนการปฏิเสธ — กันลูกค้าติดตายจาก double click / 2 แท็บ
- 8.11 IF `pay` ถูกเรียกบนคำสั่งซื้อสถานะ Paid THEN THE SYSTEM SHALL ปฏิเสธด้วย 409; IF สถานะ Cancelled THEN THE SYSTEM SHALL ตอบ 404
- 8.12 WHEN `payment-status` ถูกเรียกบนคำสั่งซื้อที่ไม่มี payment session เลย THE SYSTEM SHALL คืน `pending` (สถานะ Paid → `paid`, Cancelled → `cancelled` โดยไม่ต้องมี session)
- 8.13 WHEN `payment-status` พบ session ที่เกินอายุ (REQ-3.1) แต่ยังไม่ถูก mark Expired THE SYSTEM SHALL verify กับ 2C2P ก่อน: ชำระแล้วและยอดตรง → ดำเนินตาม 8.6; ยังไม่ชำระ → เปลี่ยน session เป็น Expired และคืน `failed` (ห้าม expire ก่อน verify)

## Edge Cases & Open Questions

- Mapping ค่า `PaymentChannel` → 2C2P payment token request (channel code จริงของ 2C2P) — รายละเอียดอยู่ใน design.md
- การมินต์ OrderNo กันชนกัน (sequence ต่อปี vs sequence เดียว + ปีใน format) และพฤติกรรมข้ามปี พ.ศ. — ตัดสินใน design.md
- ข้อมูล `Recipient` เดิม (string เดียว) บนแถวที่มีอยู่: migration เขียนมือย้ายเข้า email/phone ตาม heuristic แล้ว drop คอลัมน์เดิม
- Summary token หมุนได้เมื่อ resend — ลิงก์เก่าตาย: Customer SPA ต้องเก็บ token ใน sessionStorage ก่อน redirect ไป 2C2P (ฝั่ง FE, นอกขอบเขต backend)
- การส่ง SMS/อีเมลจริงยังเป็น logging stub — นอกขอบเขต spec นี้ (บันทึกเป็น known gap)
- ระบบภายนอกอื่นที่ขายเอกสารชุดเดียวกันไม่รู้สถานะของเรา — กันไม่ได้จากฝั่งเรา (ยอมรับ, ไม่มี reservation รอบนี้)

### Findings log (/spec-analyze)

> Anchor: HEAD `9320521` (requirements.md ยังไม่ commit ณ เวลา audit — full audit รอบแรก, ตัดสิน 2026-08-02)

| # | ประเภท | REQ | ประเด็น | ตัดสิน |
|---|---|---|---|---|
| F1 | Gap | 1×2 | mutation ตะกร้าหลัง freeze ผ่านได้ | เพิ่ม 2.7: mutation บน cart ไม่ Open → 409 |
| F2 | Gap | 2 | abandon ไม่มี gate + idempotency | เพิ่ม 2.8 (gate merchant-user+CSRF) + 2.9 (ซ้ำ → no-op) |
| F3 | Money | 3.4×8.5 | ลูกค้าจ่ายสำเร็จหลัง session Expired | เพิ่ม 3.5: LogCritical + `failed` + refund manual (TTL 24h ทำให้แทบเกิดไม่ได้) |
| F4 | Gap | 3×8.5 | payment-status เจอ session เกิน TTL ยังไม่ mark | เพิ่ม 8.13: verify กับ 2C2P ก่อน แล้วค่อย expire |
| F5 | Ambiguity | 6.7 | หน้าจอบังคับเบอร์โทร ไม่ใช่ at-least-one | แก้ 6.6/6.7: name+phone บังคับ, email optional + validate format |
| F6 | Unstated | 6.1 | INSTALLMENT มีธนาคารบนหน้าจอ | แก้ 6.1: เก็บแค่ channel — bank/งวดเลือกบน 2C2P hosted |
| F7 | Gap | 8.1 | pay ซ้ำ double click / 2 แท็บ | เพิ่ม 8.10: resume คืน redirectUrl เดิม |
| F8 | Gap | 8.1/8.5 | pay/status บน order ไม่ใช่ AwaitingPayment | เพิ่ม 8.11 (pay: Paid→409, Cancelled→404) + 8.12 (status ตาม order) |
| F9 | Gap | 8.5 | ยังไม่มี session เลย | รวมใน 8.12: คืน `pending` |
| F10 | Ambiguity | 8.7 | ยอดไม่ตรงตอน verify ตอบอะไร | แก้ 8.7: `pending` + LogCritical |
| F-13 (design critique) | Standards | 6.3 | Discount decimal(19,2) ขัดมาตรฐาน Money ทุกชั้น | amend 6.3: `Money` DECIMAL(19,4) + currency + `SameCurrencyAs` (2026-08-02) |
