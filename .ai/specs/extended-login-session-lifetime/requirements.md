# Requirements: Extended Login Session Lifetime

> Status: unknown

ขยายอายุ server-side login session ของ Admin และ Merchant User จากค่าเดิม โดยคงกลไก
rotation, revoke และ browser-session cookie เดิมทั้งหมด

## REQ-1: อายุ session ใหม่

**User Story:** ในฐานะผู้ใช้ระบบ ฉันต้องการให้ login อยู่ได้นานขึ้น เพื่อไม่ต้อง login ซ้ำระหว่างการใช้งานต่อเนื่อง

**Acceptance Criteria (EARS):**

- 1.1 WHEN Admin login สำเร็จ THE SYSTEM SHALL ตั้ง idle expiry เริ่มต้นเป็น 24 ชั่วโมงหลังเวลาออก session
- 1.2 WHEN Admin login สำเร็จ THE SYSTEM SHALL ตั้ง absolute expiry เป็น 168 ชั่วโมงหลังเวลาออก session
- 1.3 WHEN Merchant User login สำเร็จ THE SYSTEM SHALL ตั้ง idle expiry เริ่มต้นเป็น 24 ชั่วโมงหลังเวลาออก session
- 1.4 WHEN Merchant User login สำเร็จ THE SYSTEM SHALL ตั้ง absolute expiry เป็น 168 ชั่วโมงหลังเวลาออก session
- 1.5 WHEN authenticated request ถูกให้บริการ THE SYSTEM SHALL เลื่อน idle expiry ไปอีก 24 ชั่วโมงโดยไม่เกิน absolute expiry
- 1.6 IF idle expiry หรือ absolute expiry มาถึงก่อน THEN THE SYSTEM SHALL ปฏิเสธ session นั้น

## REQ-2: พฤติกรรมความปลอดภัยที่คงเดิม

**User Story:** ในฐานะผู้ดูแลระบบ ฉันต้องการขยายอายุ login โดยไม่ลดกลไกป้องกัน session เดิม

**Acceptance Criteria (EARS):**

- 2.1 THE SYSTEM SHALL คง session token rotation ทุก 15 นาที
- 2.2 THE SYSTEM SHALL คง grace window สำหรับ concurrent request ที่ 60 วินาที
- 2.3 THE SYSTEM SHALL คง session cookie เป็น browser-session cookie โดยไม่กำหนด `Expires` หรือ `Max-Age`
- 2.4 WHEN ค่า lifetime ใหม่ถูก deploy THE SYSTEM SHALL ไม่แก้ `AbsoluteExpiresAt` ของ session ที่มีอยู่ก่อน deploy

## ขอบเขตและสมมติฐาน

- ค่าใหม่ใช้กับ Admin และ Merchant User เท่ากัน
- session ใหม่หลัง deploy ได้ hard cap 7 วัน
- session เดิมยังมี hard cap ตาม `AbsoluteExpiresAt` ที่บันทึกไว้ ต้อง login ใหม่จึงได้ 7 วันเต็ม
- การจำ login หลังปิด browser อยู่นอก scope

