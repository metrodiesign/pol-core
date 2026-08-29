# Requirements: OpenAPI Documents by Audience

> Status: unknown

แยกสัญญา OpenAPI ตามผู้ใช้หลัก เพื่อให้ Scalar และ client generator เห็นเฉพาะ surface ที่เกี่ยวข้อง
โดยไม่เปลี่ยน route หรือ authorization ของ API จริง

## REQ-1: Named documents

**User Story:** ในฐานะผู้พัฒนา client ฉันต้องการเลือกสัญญาตาม audience เพื่อไม่ต้องค้น endpoint จากสัญญารวมก้อนเดียว

**Acceptance Criteria (EARS):**

- 1.1 WHILE host ทำงานใน Development THE SYSTEM SHALL publish `merchant`, `admin`, และ `integration` OpenAPI documents
- 1.2 WHILE host ทำงานใน Development THE SYSTEM SHALL คง combined document ชื่อ `v1` ที่ URL เดิม `/openapi/v1.json`
- 1.3 WHEN Scalar เปิด THE SYSTEM SHALL แสดงตัวเลือก `Merchant API`, `Admin API`, และ `Integration API`
- 1.4 WHEN Scalar เปิดครั้งแรก THE SYSTEM SHALL เลือก `Merchant API` เป็นค่าเริ่มต้น
- 1.5 WHILE host ไม่ได้ทำงานใน Development THE SYSTEM SHALL ไม่ publish OpenAPI documents และ Scalar ตามพฤติกรรมเดิม

## REQ-2: Audience partition

**User Story:** ในฐานะผู้พัฒนา client ฉันต้องการให้แต่ละ document มี operation ตรงกับ credential และ flow ของตน

**Acceptance Criteria (EARS):**

- 2.1 WHEN operation รองรับ `MerchantUserSession` THE SYSTEM SHALL include operation นั้นใน `merchant`
- 2.2 WHEN operation รองรับ `AdminSession` THE SYSTEM SHALL include operation นั้นใน `admin`
- 2.3 WHEN operation รองรับทั้งสอง session THE SYSTEM SHALL include operation นั้นในทั้ง `merchant` และ `admin`
- 2.4 WHEN operation เป็น anonymous Merchant authentication หรือ registration THE SYSTEM SHALL include operation นั้นใน `merchant`
- 2.5 WHEN operation เป็น anonymous Admin authentication THE SYSTEM SHALL include operation นั้นใน `admin`
- 2.6 WHEN operation เป็น public customer payment flow THE SYSTEM SHALL include operation นั้นใน `merchant` และ `integration`
- 2.7 WHEN operation เป็น PSP webhook callback THE SYSTEM SHALL include operation นั้นใน `integration`
- 2.8 THE SYSTEM SHALL include ทุก operation จาก `v1` ใน named document อย่างน้อยหนึ่งฉบับ
- 2.9 THE SYSTEM SHALL ไม่ include authenticated Admin หรือ Merchant management operation ใน `integration`

## REQ-3: Document-local contract

**User Story:** ในฐานะผู้ใช้ Scalar ฉันต้องการ navigation และ auth ของแต่ละ document ตรงกับ operation ที่มองเห็น

**Acceptance Criteria (EARS):**

- 3.1 THE SYSTEM SHALL publish `AdminSession` security scheme เฉพาะ `admin` และ `v1`
- 3.2 THE SYSTEM SHALL publish `MerchantUserSession` security scheme เฉพาะ `merchant` และ `v1`
- 3.3 WHEN shared operation อยู่ใน named document THE SYSTEM SHALL advertise เฉพาะ security scheme ของ document นั้น
- 3.4 THE SYSTEM SHALL group ทุก active operation tag ใน `x-tagGroups` หนึ่งครั้งต่อ document
- 3.5 THE SYSTEM SHALL ไม่ใส่ stale tag ที่ไม่มี operation ใน document ลง `x-tagGroups`
- 3.6 WHEN shared operation มี request หรือ response schema ต่างกันตาม audience THE SYSTEM SHALL publish เฉพาะ schema ของ named document นั้น และคง `oneOf` ทั้งสอง schema ใน `v1`

## REQ-4: Scalar content accuracy

**User Story:** ในฐานะผู้ใช้ Scalar ฉันต้องการข้อความอธิบายที่ตรงกับ endpoint ปัจจุบัน เพื่อไม่เรียก API ตามข้อมูลเก่า

**Acceptance Criteria (EARS):**

- 4.1 THE SYSTEM SHALL publish summary และ description ที่ไม่ว่างสำหรับทุก operation ใน combined document
- 4.2 WHEN Scalar แสดง session security scheme THE SYSTEM SHALL อธิบาย provider-scoped login route และชื่อ cookie ปัจจุบัน
- 4.3 THE SYSTEM SHALL ไม่ publish internal task ID, retired Bearer flow หรือ retired authentication route ในข้อความ OpenAPI
- 4.4 WHEN OpenAPI content ถูกแก้ THE SYSTEM SHALL คง route, operation ID, authorization, request schema และ response schema เดิม

## Out of scope

- ไม่เปลี่ยน API route, request/response DTO, authorization policy หรือ runtime behavior
- ไม่เพิ่ม dependency และไม่สร้าง custom Scalar UI
- ไม่ลบ combined `v1` จนกว่า consumer เดิมย้ายครบ
- ไม่เปลี่ยน runtime API behavior เพื่อให้ตรงกับข้อความเก่า; ข้อความต้องตามโค้ดปัจจุบัน
