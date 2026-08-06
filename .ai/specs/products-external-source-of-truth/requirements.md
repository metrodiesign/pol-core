# Requirements: Products อ่านสดจากฐานข้อมูลภายนอก (เลิกเก็บใน VCentralPay)

> Status: approved 2026-08-05

## Overview

วันนี้ `GET /products` ค้นเอกสารประกันสดจาก stored procedure ต้นทาง (`hippodb`/`mammothdb` วันนี้,
`motordb`/`centerdb` ในอนาคต) แล้ว **mirror ทุกหน้าที่ค้นได้ลงตาราง `shop.Products`** ของฐานข้อมูล
VCentralPay เพื่อมินต์ `Guid` ให้แต่ละเอกสาร — `Guid` ตัวนั้นคือแกนที่ตะกร้า/checkout/order ทั้งสายใช้อ้าง
และตารางเดียวกันยังทำหน้าที่เป็นราคาที่เชื่อถือได้ (trusted price) และตัวกันขายเอกสารใบเดิมซ้ำ

ฟีเจอร์นี้ตัดที่เก็บนั้นออกทั้งหมด: แคตตาล็อกกลายเป็น read-only ผ่านต้นทางล้วน ๆ ไม่มีสำเนาใน VCentralPay
ผลคือสามหน้าที่ที่ผูกกับตารางเดิมต้องหาที่อยู่ใหม่ตาม decision ที่ผู้ใช้ล็อกไว้แล้ว:

| หน้าที่เดิมของ `shop.Products` | ที่อยู่ใหม่ |
|---|---|
| ตัวระบุเอกสาร (`Guid ProductId`) | `DocumentNo` (string) เป็นตัวระบุตรง ๆ ทั้ง wire และ DB |
| ราคา + snapshot ที่เชื่อถือได้ | อ่านสดจากต้นทางรายใบ ด้วย `documentNo` + `productGroup` + `saleCode` ที่ server กำหนด |
| กันขายเอกสารใบเดิมซ้ำ | อนุมานจาก Orders — order สถานะ `Paid` ที่มีเอกสารเดียวกัน |

ตาราง `shop.Products` ถูก DROP ใน migration เดียวกับการเปลี่ยนคอลัมน์ตัวระบุ

---

## REQ-1: แคตตาล็อกอ่านสดอย่างเดียว

**User Story:** ในฐานะ merchant user ฉันอยากค้นเอกสารประกันจากระบบต้นทางโดยตรง
เพื่อให้สิ่งที่เห็นคือสิ่งที่ต้นทางมีจริง ไม่ใช่สำเนาที่อาจล้าสมัย

**Acceptance Criteria (EARS):**

- 1.1 THE SYSTEM SHALL ตอบ `GET /products` จากผลลัพธ์ของ stored procedure ต้นทางเท่านั้น
- 1.2 WHEN `GET /products` ทำงาน THE SYSTEM SHALL ไม่เกิด `SaveChanges` และไม่มีคำสั่ง
  INSERT/UPDATE/DELETE ใด ๆ ในเส้นทางการตอบคำขอนั้น
- 1.3 THE SYSTEM SHALL คงรูปแบบ response envelope `ProductPage` (§5.1: `totalRows`, `totalPages`,
  `pageNo`, `pageSize`, `hasNextPage`, `hasPreviousPage`, `countMode`, `searchWindowMonths`) ตามเดิม
- 1.4 THE SYSTEM SHALL คงลำดับแถวตามที่ procedure คืนมา โดยไม่จัดเรียงใหม่
- 1.5 THE SYSTEM SHALL คงค่า `totalRows`/`totalPages` ตามที่ procedure รายงาน โดยไม่นับใหม่เอง
- 1.6 IF แถวจากต้นทางขาดฟิลด์บังคับ (`DocumentNo` ว่าง, `SaleCode` ว่าง, `TotalPremium` ไม่มากกว่า 0,
  หรือค่า enum นอกสัญญา) THEN THE SYSTEM SHALL ข้ามแถวนั้นออกจากผลลัพธ์และบันทึก log ระดับ warning
  พร้อมเหตุผล
- 1.7 IF แถวสองแถวในหน้าเดียวกันมี `DocumentNo` ซ้ำกัน (เทียบตาม REQ-2.3) THEN THE SYSTEM SHALL
  คงไว้แถวแรกและข้ามแถวถัดมาพร้อม log ระดับ warning
- 1.8 THE SYSTEM SHALL ไม่มีฟิลด์ `id` ชนิด `Guid` ในแต่ละรายการของผลลัพธ์

---

## REQ-2: ตัวระบุเอกสารคือ DocumentNo

**User Story:** ในฐานะผู้พัฒนา ฉันอยากให้ทุกชั้นอ้างเอกสารด้วยเลขเอกสารจริง
เพื่อไม่ต้องมีตารางกลางไว้แค่มินต์ `Guid`

**Acceptance Criteria (EARS):**

- 2.1 THE SYSTEM SHALL ใช้ `documentNo` (string ยาวไม่เกิน 150) เป็นตัวระบุเอกสารในทุก request/response
  ที่เคยใช้ `productId`
- 2.2 THE SYSTEM SHALL เก็บ `DocumentNo` แทนคอลัมน์ `ProductId` ในตารางรายการของตะกร้า, checkout session
  และ order
- 2.3 THE SYSTEM SHALL เทียบ `DocumentNo` สองค่าว่าเป็นเอกสารเดียวกัน เมื่อตรงกันทั้งสตริงหลังตัดช่องว่าง
  หัวท้าย โดยไม่สนตัวพิมพ์ใหญ่เล็ก
- 2.4 THE SYSTEM SHALL ไม่มี endpoint หรือ payload ใดที่ยังรับหรือคืน `productId` ชนิด `Guid`
- 2.5 IF request อ้าง `documentNo` ที่ว่าง เป็นช่องว่างล้วน หรือยาวเกิน 150 ตัวอักษร THEN THE SYSTEM SHALL
  ตอบ 400
- 2.6 THE SYSTEM SHALL บันทึก `DocumentNo` ตามที่ต้นทางสะกดมา หลังตัดช่องว่างหัวท้าย โดยไม่แปลงตัวพิมพ์
- 2.7 THE SYSTEM SHALL ให้คอลัมน์ `DocumentNo` ทุกตารางใน VCentralPay ใช้ collation ที่เทียบแบบไม่สน
  ตัวพิมพ์และรองรับอักษรไทย เหมือนกันทุกตาราง เพื่อให้การเทียบฝั่ง SQL ให้ผลตรงกับ REQ-2.3

---

## REQ-3: อ่านเอกสารรายใบสดจากต้นทาง

**User Story:** ในฐานะระบบ ฉันต้องอ่านเอกสารใบเดียวจากต้นทางได้
เพื่อใช้เป็นแหล่งราคาและเงื่อนไขที่เชื่อถือได้ตอนใส่ตะกร้าและตอนเริ่ม checkout

**Acceptance Criteria (EARS):**

- 3.1 THE SYSTEM SHALL มีทางอ่านเอกสารใบเดียวจากต้นทาง โดยรับ `documentNo` และ `productGroup` จากผู้เรียก
  ส่วน `saleCode` มาจากฝั่ง server ตาม REQ-4.8
- 3.2 THE SYSTEM SHALL เลือก procedure ฝั่ง Motor หรือ NonMotor จาก `productGroup` ที่ได้รับ
  (CMI/VMI = Motor, FIRE/MISC = NonMotor)
- 3.3 THE SYSTEM SHALL ค้นด้วย `@PaymentStatus = 'ALL'` เพื่อให้เห็นสถานะจริงของเอกสาร ไม่ใช่เฉพาะที่ยัง
  ไม่จ่าย
- 3.4 THE SYSTEM SHALL คืนเฉพาะแถวที่ `DocumentNo` ตรงกับที่ขอตาม REQ-2.3
- 3.5 THE SYSTEM SHALL ใช้ค่าจากแถวที่ต้นทางคืนเป็นค่าจริงของเอกสารทุกฟิลด์ รวมถึง `productGroup`
  โดยค่าที่ผู้เรียกส่งมาใช้เพียงเพื่อเลือก procedure เท่านั้น
- 3.6 IF ไม่พบแถวที่ตรง THEN THE SYSTEM SHALL ปฏิเสธคำขอนั้นตามบริบท (REQ-4.5 / REQ-7.4)
- 3.7 IF ต้นทางคืนแถวที่ตรงมากกว่าหนึ่งแถว THEN THE SYSTEM SHALL ปฏิเสธคำขอและบันทึก log ระดับ error
  โดยไม่เลือกแถวใดแถวหนึ่งเอง
- 3.8 THE SYSTEM SHALL ไม่เปิดการอ่านเอกสารรายใบเป็น HTTP endpoint — ใช้ได้จากภายในระบบเท่านั้น

---

## REQ-4: ราคาและ snapshot มาจากต้นทางเสมอ

**User Story:** ในฐานะเจ้าของแพลตฟอร์ม ฉันต้องมั่นใจว่าราคาที่ขายมาจากระบบต้นทาง
ไม่ใช่ตัวเลขหรือแถวที่ client เลือกให้

**Acceptance Criteria (EARS):**

- 4.1 WHEN มีการเพิ่มรายการเข้าตะกร้า THE SYSTEM SHALL ตั้งราคาต่อหน่วยจาก `TotalPremium` ที่ต้นทางคืน
  ในคำขอนั้น
- 4.2 THE SYSTEM SHALL ไม่รับฟิลด์ราคาใด ๆ จาก request body ของการเพิ่มรายการเข้าตะกร้า
- 4.3 THE SYSTEM SHALL มินต์สกุลเงิน THB ที่ขอบตะกร้าเพียงจุดเดียว เหมือนพฤติกรรมเดิม
- 4.4 WHEN เริ่ม checkout THE SYSTEM SHALL อ่านเอกสารสดจากต้นทางอีกครั้งต่อหนึ่งบรรทัดในตะกร้า
  แล้วใช้ค่าที่ได้เป็น snapshot ของ `DocumentNo`, `ProductGroup`, `DocumentType`, `PolicyNumber`,
  `StartDate`, `EndDate`
- 4.5 IF อ่านเอกสารตอนเพิ่มรายการเข้าตะกร้าไม่พบ THEN THE SYSTEM SHALL ตอบ 400
- 4.6 THE SYSTEM SHALL คงใช้ราคาที่บันทึกไว้ในตะกร้าเป็นราคาขายตอน checkout (พฤติกรรมเดิม —
  ไม่เปลี่ยนราคาตามที่อ่านสดได้ใหม่)
- 4.7 THE SYSTEM SHALL เก็บ `DocumentNo`, `SaleCode` และ `ProductGroup` ของแต่ละรายการในตะกร้า
  ด้วยค่าที่ต้นทางคืนกลับมา ไม่ใช่ค่าที่ client ส่งมา
- 4.8 THE SYSTEM SHALL กำหนด `saleCode` ที่ใช้ค้นต้นทางจากฟิลด์ `SaleCode` ของ merchant user ที่ยืนยัน
  ตัวตนแล้ว โดย client เลือก `saleCode` เองไม่ได้
- 4.9 IF merchant user ที่เรียกไม่มี `SaleCode` ผูกอยู่ THEN THE SYSTEM SHALL ปฏิเสธคำขอที่ต้องใช้
  แคตตาล็อกด้วย 403
- 4.10 IF ค่า `SaleCode` ที่ส่งมากับฟอร์มสมัครยาวเกิน 20 ตัวอักษรหลังตัดช่องว่างหัวท้าย หรือมีอักขระ
  นอกช่วง ASCII ที่พิมพ์ได้ THEN THE SYSTEM SHALL ตอบ 400 และไม่บันทึกค่านั้น
- 4.11 THE SYSTEM SHALL ไม่ส่งค่า `saleCode` ที่ถูกตัดทอนหรือถูกแปลงอักขระไปยังต้นทาง — ค่าที่ผูกกับ
  พารามิเตอร์ต้องเท่ากับค่าที่เก็บไว้ทุกตัวอักษร มิฉะนั้นต้องปฏิเสธคำขอ ไม่ใช่ค้นด้วยค่าที่เพี้ยน

---

## REQ-5: กันขายเอกสารใบเดิมซ้ำ โดยอนุมานจาก Orders

**User Story:** ในฐานะเจ้าของแพลตฟอร์ม ฉันต้องไม่ให้เอกสารใบที่ขายไปแล้วถูกขายอีก
แม้ระบบต้นทางจะไม่มีวันรายงานว่าเอกสารถูกขายผ่านแพลตฟอร์มนี้ (ต้นทางเป็น read-only)

**Acceptance Criteria (EARS):**

- 5.1 THE SYSTEM SHALL ถือว่าเอกสารถูกขายแล้ว เมื่อมี order สถานะ `Paid` อย่างน้อยหนึ่งใบที่มีรายการซึ่ง
  `DocumentNo` (ตาม REQ-2.3) และ `ProductGroup` ตรงกันทั้งคู่
- 5.2 THE SYSTEM SHALL ตรวจเงื่อนไข 5.1 ข้ามทุก merchant โดยไม่ถูกจำกัดด้วย merchant floor
- 5.3 THE SYSTEM SHALL ถือว่าเอกสารที่ต้นทางรายงานสถานะ `PAID` เป็นเอกสารที่ขายไม่ได้เช่นกัน
- 5.4 WHEN มีการเพิ่มรายการเข้าตะกร้าด้วยเอกสารที่ขายไม่ได้ THE SYSTEM SHALL ตอบ 400
- 5.5 WHEN เริ่ม checkout แล้วมีบรรทัดที่เอกสารขายไม่ได้ THE SYSTEM SHALL ตอบ 409 และไม่สร้าง
  checkout session
- 5.6 WHEN สร้าง payment session THE SYSTEM SHALL ตรวจเงื่อนไข 5.1 อีกครั้งกับทุกรายการของ order นั้น
  และตอบ 409 ก่อนสร้างรายการเรียกเก็บเงินกับ PSP หากพบว่ามีเอกสารที่ถูกขายไปแล้วโดย order อื่น
- 5.7 THE SYSTEM SHALL ไม่เปิดเผยรหัสหรือชื่อ merchant อื่นในข้อความตอบกลับของ 5.4-5.6
- 5.8 WHEN `GET /products` ถูกเรียกด้วย `paymentStatus` เป็น `UNPAID` (รวมกรณีไม่ระบุ) THE SYSTEM SHALL
  ตัดเอกสารที่ขายไม่ได้ตาม 5.1 ออกจากรายการที่ตอบกลับ
- 5.9 THE SYSTEM SHALL ระบุในผลลัพธ์ของ `GET /products` ว่าเอกสารแต่ละใบถูกขายผ่านแพลตฟอร์มนี้แล้วหรือไม่
  โดยไม่แก้ค่า `paymentStatus` ที่ต้นทางรายงาน
- 5.10 WHILE order มี payment session ที่ยังชำระได้ (ยังไม่ถึงสถานะสิ้นสุดแบบไม่มีการชำระ และอายุยังไม่ครบ
  ตามที่ระบบกำหนด) หรือมี payment session ที่ผู้ให้บริการชำระเงินยืนยันแล้ว THE SYSTEM SHALL ถือว่าเอกสาร
  ในนั้นขายไม่ได้
- 5.11 WHILE order อยู่ในสถานะ `AwaitingPayment` โดยไม่มี payment session ตาม 5.10 THE SYSTEM SHALL
  ถือว่าเอกสารในนั้นขายได้
- 5.12 WHILE order อยู่ในสถานะ `Cancelled` และไม่มี payment session ตาม 5.10 THE SYSTEM SHALL ถือว่า
  เอกสารในนั้นขายได้
- 5.13 THE SYSTEM SHALL ให้การล็อกตาม 5.10 หมดฤทธิ์เองเมื่อ payment session พ้นอายุ โดยไม่ต้องรอให้มี
  คำขอใดมาแก้สถานะของแถวนั้นก่อน
- 5.14 THE SYSTEM SHALL ให้การตรวจสถานะขายแล้วคืนเลขเอกสาร, รหัส order และเหตุผลของการล็อก
  (ขายแล้ว หรือกำลังอยู่ระหว่างชำระเงิน) ไม่ใช่ค่าจริง/เท็จ
- 5.15 THE SYSTEM SHALL ตรวจเงื่อนไข 5.1 และ 5.10 ให้ทุก `DocumentNo` ในคำขอหนึ่ง ด้วยการอ่านฐานข้อมูล
  ครั้งเดียว ไม่ใช่ครั้งละหนึ่งเอกสาร
- 5.16 IF เอกสารใบเดียวกันปรากฏใน order สถานะ `Paid` มากกว่าหนึ่งใบ THEN THE SYSTEM SHALL บันทึก log
  ระดับ critical พร้อมเลขเอกสารและ order ทั้งสองใบ โดยไม่นับ order ที่กำลังประมวลผลอยู่เป็นใบที่สอง

---

## REQ-6: ปลดระวังที่เก็บเดิม

**User Story:** ในฐานะผู้ดูแลระบบ ฉันอยากให้ตารางและโค้ดที่ไม่ใช้แล้วหายไปจริง
เพื่อไม่ให้เหลือทางเขียนข้อมูลที่ไม่มีใครอ่าน

**Acceptance Criteria (EARS):**

- 6.1 THE SYSTEM SHALL DROP ตาราง `shop.Products` ใน migration เดียวกับที่เปลี่ยนคอลัมน์ตัวระบุ
- 6.2 WHEN migration ทำงาน THE SYSTEM SHALL เติมค่า `DocumentNo`, `SaleCode` และ `ProductGroup` ของ
  รายการในตะกร้าที่มีอยู่เดิม จากตาราง `shop.Products` ก่อนที่ตารางนั้นจะถูก DROP
- 6.3 IF รายการในตะกร้าเดิมหาแถวใน `shop.Products` ไม่เจอ THEN THE SYSTEM SHALL ลบรายการนั้นทิ้ง
  แทนที่จะปล่อยค่าว่างไว้
- 6.4 THE SYSTEM SHALL ไม่มีโค้ดที่อ้างถึงตาราง `shop.Products` หลังงานนี้เสร็จ
- 6.5 THE SYSTEM SHALL ลบ aggregate `Product`, `ProductInput`, พอร์ตและอะแดปเตอร์ของ repository,
  EF configuration, `CreateProductCommand`, `GetProductByIdQuery` และ consumer ที่ mark เอกสารเป็น PAID
- 6.6 THE SYSTEM SHALL ลบสิทธิ์ GRANT ของตาราง `shop.Products` ที่ให้ principal ของแอป
- 6.7 THE SYSTEM SHALL ลบรายการ entity `Product` ออกจาก write authorizer ทุกตัวที่ยังอ้างถึง
- 6.8 IF migration ถูก rollback THEN THE SYSTEM SHALL สร้างโครงสร้างตารางและคอลัมน์เดิมกลับคืนได้
  โดยไม่คืนข้อมูลเดิม
- 6.9 THE SYSTEM SHALL ปรับ seed/demo script และ script ตรวจฐานข้อมูลใหม่ ให้ไม่อ้างตารางที่ถูกลบ
- 6.10 THE SYSTEM SHALL ตั้งค่า `SaleCode` ของ merchant user ในสคริปต์ seed เป็นรหัสที่มีอยู่จริงใน
  ข้อมูลของระบบต้นทาง เพื่อให้การค้นแคตตาล็อกบนเครื่อง demo คืนผลลัพธ์ที่ไม่ว่าง

---

## REQ-7: พฤติกรรมเมื่อต้นทางล้มเหลว

**User Story:** ในฐานะ merchant user ฉันอยากแยกออกว่าปัญหาเกิดที่ระบบต้นทางหรือที่แพลตฟอร์มนี้

**Acceptance Criteria (EARS):**

- 7.1 IF การเรียกต้นทางล้มเหลวด้วยเหตุ transport (ต่อไม่ติด, timeout, ผิดพลาดที่ไม่ใช่ §6) THEN
  THE SYSTEM SHALL ตอบ 503 ทั้งกรณีค้นรายการและกรณีอ่านเอกสารรายใบ
- 7.2 IF ต้นทาง raise ข้อผิดพลาดตามสัญญา §6 (50001-50009) THEN THE SYSTEM SHALL ตอบ 400 พร้อมสาระเดิม
- 7.3 THE SYSTEM SHALL ไม่เปิดเผยรายละเอียด SQL, ชื่อ procedure หรือ connection string ใน response
  โดยบันทึกรายละเอียดไว้ฝั่ง server เท่านั้น
- 7.4 IF อ่านเอกสารตอนเริ่ม checkout ไม่พบ (รวมกรณีเอกสารหลุดออกนอกหน้าต่างค้นหาของต้นทาง) THEN
  THE SYSTEM SHALL ตอบ 409 และไม่สร้าง checkout session
- 7.5 IF การเรียกต้นทางตอนเริ่ม checkout ล้มเหลวด้วยเหตุ transport THEN THE SYSTEM SHALL ตอบ 503
  และไม่สร้าง checkout session

---

## REQ-8: สัญญาและรายงานที่พึ่งตัวระบุเดิม

**User Story:** ในฐานะผู้พัฒนา ฉันอยากให้ทุกสายที่พก `ProductId` เปลี่ยนพร้อมกันในรอบเดียว
เพื่อไม่ให้เหลือ payload ครึ่ง ๆ กลาง ๆ

**Acceptance Criteria (EARS):**

- 8.1 THE SYSTEM SHALL ตัดฟิลด์ `ProductId` ออกจาก `CheckoutConfirmedItem` โดยใช้ `DocumentNo` ที่สัญญา
  นั้นพกอยู่แล้วเป็นตัวระบุ
- 8.2 WHEN order ถูกเปลี่ยนสถานะเป็น `Paid` THE SYSTEM SHALL ตรวจเงื่อนไข double-sell ตาม REQ-5.16
- 8.3 THE SYSTEM SHALL ลบ integration event ที่ไม่เหลือผู้บริโภคหลังงานนี้ ออกจากทั้งสัญญาและทะเบียน
  ชนิดของ outbox
- 8.4 THE SYSTEM SHALL ให้ request เริ่ม checkout อ้างผู้เอาประกันด้วย `documentNo` และยังบังคับว่า
  รายชื่อผู้เอาประกันต้องครอบทุกบรรทัดในตะกร้าพอดีหนึ่งครั้ง
- 8.5 IF request เริ่ม checkout มี `documentNo` ซ้ำกันในรายชื่อผู้เอาประกัน THEN THE SYSTEM SHALL ตอบ 400
- 8.6 THE SYSTEM SHALL คงฟิลด์และการทำงานของรายงานกรมธรรม์, หน้ารายละเอียด order และ order summary
  ตามเดิม โดยอ้างเอกสารด้วย `DocumentNo`
- 8.7 THE SYSTEM SHALL คงการทำงานของ `ItemPolicy` (1:1 กับ order item) โดยไม่เปลี่ยนความสัมพันธ์

---

## REQ-9: รายการในตะกร้าเมื่อเปลี่ยนตัวระบุ

**User Story:** ในฐานะ merchant user ฉันยังต้องลบและแก้จำนวนรายการในตะกร้าได้
แม้เลขเอกสารจะมี `/` และอักษรไทยจนใส่ใน URL ตรง ๆ ไม่ได้

**Acceptance Criteria (EARS):**

- 9.1 THE SYSTEM SHALL อ้างรายการในตะกร้าด้วยรหัสรายการ (`itemId`, `Guid`) ใน route ของการลบและการแก้
  จำนวน ไม่ใช่ด้วย `documentNo`
- 9.2 THE SYSTEM SHALL คืนค่า `itemId` ของทุกบรรทัดในผลลัพธ์การดูตะกร้า
- 9.3 IF `itemId` ที่อ้างไม่มีอยู่ในตะกร้านั้น THEN THE SYSTEM SHALL ตอบ 404
- 9.4 IF มีการเพิ่มเอกสารใบที่อยู่ในตะกร้านั้นอยู่แล้ว THEN THE SYSTEM SHALL ตอบ 400 แทนการรวมจำนวน
- 9.5 THE SYSTEM SHALL คงการบังคับจำนวนเท่ากับ 1 ต่อบรรทัดตอนเริ่ม checkout ไว้เป็นการป้องกันชั้นที่สอง

---

## REQ-10: เปลี่ยนชื่อ ProducerCode เป็น SaleCode

**User Story:** ในฐานะผู้พัฒนา ฉันอยากให้ฟิลด์ที่ผูก merchant user กับรหัสผู้ขายในระบบต้นทาง
ใช้ชื่อเดียวกับที่สัญญาต้นทางใช้ เพื่อไม่ให้มีสองชื่อสำหรับของสิ่งเดียวกัน

**Acceptance Criteria (EARS):**

- 10.1 THE SYSTEM SHALL เปลี่ยนชื่อฟิลด์ `ProducerCode` เป็น `SaleCode` บน merchant user และบน snapshot
  ของการสมัคร (registration attempt)
- 10.2 THE SYSTEM SHALL เปลี่ยนชื่อคอลัมน์ในฐานข้อมูลด้วยคำสั่งเปลี่ยนชื่อ โดยข้อมูลเดิมทุกแถวต้องคงอยู่
- 10.3 THE SYSTEM SHALL เปลี่ยนชื่อฟิลด์บน wire จาก `producerCode` เป็น `saleCode` ทั้งในฟอร์มสมัคร
  (`POST /api/v1/merchants/users/register`) และในผลลัพธ์ประวัติการสมัครฝั่ง admin
  (`GET /api/v1/admins/merchants/users/{subject}/registrations`) โดยไม่รับชื่อเดิมอีกต่อไป
- 10.4 THE SYSTEM SHALL ไม่คงชื่อ `ProducerCode`/`producerCode` ไว้ที่ใดในโค้ด สคริปต์ seed หรือ payload
- 10.5 THE SYSTEM SHALL คงกฎการปกปิดข้อมูลของฟิลด์นี้ไว้เหมือนเดิม (ไม่ถูก mask) ในประวัติการสมัคร
- 10.6 THE SYSTEM SHALL ให้คอลัมน์ `SaleCode` ทั้งบน merchant user และบน snapshot ของการสมัคร
  เป็นชนิดไม่ใช่ unicode ยาว 20 ให้ตรงกับพารามิเตอร์และคอลัมน์ของสัญญาต้นทาง
- 10.7 THE SYSTEM SHALL มี test ที่ยึดชื่อฟิลด์บน wire ไว้ทั้งสองทาง — ส่งฟอร์มด้วยคีย์ `saleCode`
  แล้วค่าต้องถูกบันทึก, ส่งด้วยคีย์ `producerCode` แล้วค่าต้องไม่ถูกบันทึก, และผลลัพธ์ประวัติการสมัคร
  ต้องมีคีย์ `saleCode` ไม่มีคีย์ `producerCode`
- 10.8 THE SYSTEM SHALL ปรับเอกสารอ้างอิงที่บรรยายสัญญาของ endpoint ทั้งสอง ให้สะกดชื่อฟิลด์ใหม่
  ตรงกับที่ระบบรับจริง

---

## Edge Cases & Open Questions

### เปิดค้าง

ไม่มี — ข้อค้างทั้งสามข้อจากรอบที่ 1 ถูกปิดในรอบที่ 2 (ดูตาราง F19-F22)

### กรณีขอบที่ requirements ครอบแล้ว แต่ต้องมี test

1. เอกสารหลุดออกนอกหน้าต่างค้นหา 6 เดือนของต้นทางระหว่างอยู่ในตะกร้า (REQ-7.4)
2. ต้นทางคืนเอกสารตรงเป๊ะสองแถว (REQ-3.7) — เป็นไปได้เมื่อสอง sale code ถือเอกสารเลขเดียวกัน
3. `DocumentNo` ที่ต่างกันแค่ตัวพิมพ์ หรือมีช่องว่างหัวท้าย ต้องถือเป็นเอกสารเดียวกันทั้งฝั่ง C# และฝั่ง SQL
   (REQ-2.3 / REQ-2.7) — ต้องมี test ที่ใช้เลขเอกสารที่มีอักษรไทยจริง
4. ต้นทางรายงาน `PAID` แต่ไม่มี `PaidDate` — เดิม log warning แล้วใช้ค่าที่เก็บไว้ ตอนนี้ไม่มีค่าที่เก็บไว้แล้ว
   เหลือแค่ log
5. ช่วงคาบเกี่ยวหลังผู้ให้บริการยืนยันการชำระ แต่ order ยังไม่ถูกปรับเป็น `Paid` (outbox ยังไม่ทำงาน) —
   เอกสารต้องยังถูกล็อกอยู่ตาม REQ-5.10 ไม่ใช่หลุดทั้งสองด้าน
6. payment session ที่พ้นอายุแล้วแต่ยังไม่มีใครไปแก้สถานะในฐานข้อมูล — เอกสารต้องกลับมาขายได้ทันที
   ตาม REQ-5.13
7. การเปลี่ยนชื่อคอลัมน์ตาม REQ-10.2 ต้องมีหลักฐานว่าข้อมูลเดิมยังอยู่ครบหลัง migrate

### ผลข้างเคียงที่ต้องยอมรับ

8. ข้อมูลใน `shop.Products` บนเครื่อง dev/demo หายถาวรเมื่อ migration รัน (ไม่มีข้อมูล prod)
9. การเพิ่มรายการเข้าตะกร้าและการเริ่ม checkout ยิงต้นทางเพิ่มหนึ่งครั้งต่อหนึ่งบรรทัด — เดิมอ่านจากตาราง
   ในเครื่อง งานทั้งสองจึงช้าลงและผูกกับความพร้อมของต้นทาง
10. test ที่พึ่ง upsert ลง `shop.Products` โดยตรงจะถูกลบทั้งไฟล์ ไม่ใช่แค่แก้
11. REQ-10 เปลี่ยนชื่อฟิลด์บน wire ของฟอร์มสมัคร (`producerCode` -> `saleCode`) = breaking change ต่อ
    frontend ที่ยังส่งชื่อเดิม
12. เอกสารถูกล็อกได้นานสุดเท่าอายุของ payment session ที่เปิดค้าง แม้ลูกค้าจะเลิกจ่ายไปแล้ว (REQ-5.10)
13. สเปกที่ ship ไปแล้วซึ่งอ้างชื่อ `ProducerCode` (`registration-attempt-history`, `producer-google-sso`,
    `demo-seed-data`) จะสะกดชื่อเดิมค้างไว้ — เป็นบันทึกของสิ่งที่ ship ตอนนั้น ไม่แก้ย้อนหลัง

---

## บันทึกผลการวิเคราะห์ (/spec-analyze)

> Anchor: `2370904` (HEAD ตอนวิเคราะห์ — requirements.md ยังไม่ถูก commit) ·
> รอบที่ 1, 2026-08-05 · ตรวจข้อเท็จจริงโดย agent `researcher`, เลือกทางโดย agent `architect`

| รหัส | ประเด็น | คำตัดสิน | ผลต่อไฟล์ |
|---|---|---|---|
| F1 | `DocumentNo` unique ข้าม Motor/NonMotor เป็นเพียง convention (prefix `69`/`26` + unique index คนละ instance + test) ไม่ใช่ constraint | คงตัวระบุเป็น `DocumentNo` เดี่ยว แต่ให้การตรวจขายแล้วเทียบ `(DocumentNo, ProductGroup)` และคง REQ-3.7 ให้ปฏิเสธ | REQ-5.1 |
| F2 | `DocumentNo` มี `/` และอักษรไทย ใช้เป็น path segment ไม่ได้ (route เดิมมี `:guid` constraint) | route ด้วย `itemId` ที่ `Carts.Domain.Items.Item.Id` มีอยู่แล้ว | REQ-9 (ใหม่) |
| F3 | ไม่มี requirement เรื่องย้ายข้อมูลเดิม — `CartItems` เป็นตารางเดียวที่ยังไม่มี `DocumentNo` (checkout/order มีแล้ว) | backfill จาก `shop.Products` ก่อน DROP, แถวที่ join ไม่เจอให้ลบ | REQ-6.2, REQ-6.3 |
| F4 | ข้อความค้างใน outbox | ไม่ต้องทำอะไรเป็นพิเศษ — `CheckoutConfirmedItem` พก `DocumentNo` อยู่แล้ว และ serializer ข้าม member ที่ไม่รู้จัก เหลือแค่ event ที่ไม่มี consumer ต้องถูกลบ | REQ-8.1, REQ-8.3 |
| F5 | double-sell ไม่มี gate ก่อนเรียกเก็บเงิน | ตรวจซ้ำตอนสร้าง payment session → 409 · **ปัดตัวเลือก unique constraint ทิ้ง**: `OrderItems` ไม่มีสถานะ order และ constraint จะยิงตอน mark Paid คือหลังลูกค้าจ่ายที่ PSP ไปแล้ว | REQ-5.6, REQ-5.7 |
| F6 | `ProductListItem.Id` ไม่มีใครนอกโมดูลอ่าน | ลบทิ้ง | REQ-1.8 |
| F7 | เจ้าของ `saleCode`/`productGroup` ในตะกร้า | เก็บค่าที่ต้นทางยืนยันกลับมา | REQ-4.7 |
| F8 | ถ้อยคำ REQ-3.4 ("ตรงทั้งสตริง") ขัดกับ REQ-2.3 (ไม่สนตัวพิมพ์) และไม่มีข้อไหนพูดเรื่อง trim | รวมกฎ normalize ไว้ที่ REQ-2.3 จุดเดียว แล้วให้ REQ-3.4 อ้างถึง — ตรงกับที่โค้ดทำอยู่แล้ว | REQ-2.3, REQ-2.6, REQ-3.4 |
| F9 | เอกสารที่ขายผ่านแพลตฟอร์มนี้จะโชว์ `UNPAID` ตลอดกาลเมื่อถามด้วย `ALL`/`PAID` | เพิ่มธงบอกในผลลัพธ์ · **ปัดการเขียนทับ `paymentStatus` ทิ้ง**: ขัด REQ-1.1/3.5 ที่ให้ต้นทางเป็นค่าจริง | REQ-5.9 |
| F10 | REQ-1.2 กว้างจนทดสอบไม่ได้ และไม่มี test เดิมคุ้มครองอยู่ (`WriteFloorTests`/`SpInsulationTests` คนละเรื่อง) | รัดเป็น "เส้นทางตอบ `GET /products` ต้องไม่เกิด SaveChanges" — ไม่ใช่ "ห้ามเขียน schema shop" เพราะระบบเขียน `shop.Carts` ตลอดเวลา | REQ-1.2 |
| F12 | REQ-8.2 ระบุ HOW ในเอกสาร WHAT | ตัดวลี "ภายในเส้นทางเดียวกับการเปลี่ยนสถานะ" ออก คง trigger ไว้ | REQ-8.2 |
| F13 | `shop.OrderItems` ไม่มี index บน `DocumentNo` เลย (มีแค่ `(OrderId, MerchantId)`) ขณะที่ของเดิมมี `IX_Products_DocumentNo` | **ไม่เขียนเป็น requirement** — index เป็น HOW ให้ design เป็นคนสั่ง แต่ต้องสั่งจริง ไม่ปล่อยลอย | ไม่มี (ส่งต่อ design) |
| F14 | ใส่เอกสารซ้ำในตะกร้าเดิม merge จำนวนแล้วไปตายตอน checkout | ปฏิเสธที่ add-item ด้วย 400 คง guard `Quantity != 1` ไว้เป็นชั้นที่สอง | REQ-9.4, REQ-9.5 |
| F15 | จะเปิดการอ่านเอกสารรายใบเป็น endpoint ไหม | internal เท่านั้น ไม่มี route (ของเดิมก็จงใจไม่ map) | REQ-3.8 |
| F16 | collation ของ `DocumentNo` ฝั่ง VCentralPay ไม่มีใครสั่ง — sold-check ถูกแปลเป็น SQL ดังนั้น DB collation เป็นคนตัดสิน ไม่ใช่ `OrdinalIgnoreCase` ใน C# | เพิ่มข้อบังคับให้ collation ตรงกันทุกตาราง | REQ-2.7, open #6 |
| F17 | `saleCode` กลายเป็นตัวเลือกว่าแถวไหนถูกคิดเงิน | user ตัดสิน: server กำหนด `saleCode` จาก merchant user, client เลือกเองไม่ได้ — **ฟิลด์ยังไม่มีในโค้ด** จึงค้างเป็น open #3 | REQ-4.8, REQ-4.9, REQ-3.1, open #3 |
| F18 | `Product.SoldOrderId` หายพร้อมตาราง = หลักฐาน "order ไหนขายไป" หายด้วย | ให้การตรวจคืนคู่เลขเอกสาร+order ไม่ใช่ค่าจริง/เท็จ และไม่นับ order ตัวเองตอน log | REQ-5.12, REQ-5.13 |

**สิ่งที่ยกไปเป็น open question แทนการเขียนเป็น REQ:** F13 (index — เป็น design decision),
F17 ส่วนที่ยังไม่มีฟิลด์รองรับ (open #3) — ทั้งสองถูกปิดในรอบที่ 2

### รอบที่ 2, 2026-08-05 — ปิดข้อค้างทั้งสาม

> เจาะลึกโดย agent `architect` (ข้อ 1 + F13), user ตัดสินเอง (ข้อ 2 + ข้อ 3)

| รหัส | ประเด็น | คำตัดสิน | ผลต่อไฟล์ |
|---|---|---|---|
| F19 | **open #1** — เอกสารถูกล็อกโดย order ที่ยังไม่ชำระหรือไม่ ช่องโหว่จริงไม่ใช่ race ระดับ millisecond แต่เป็นช่วงที่ order สองใบมี payment session เปิดค้างพร้อมกัน ยาวได้ถึงอายุของ session (`Session.OpenTtl`); ไล่แล้วพบว่า `Order.Cancel` และ `Session.MarkExpired`/`MarkFailed` เป็น request-driven ทั้งหมด ไม่มี BackgroundService ตัวใดแตะ order หรือ payment session เลย | **ล็อกเมื่อมี payment session ที่ยังชำระได้ ไม่ใช่ล็อกจากสถานะ `AwaitingPayment`** — เพราะอายุ session เป็นค่าที่คำนวณจากเวลาสร้าง (`Session.IsExpiredAt` เป็น pure function) ฝั่งอ่านจึงใช้เงื่อนไขเวลาได้เลย ปลดล็อกเองโดยไม่ต้องมี sweeper · **ปัดการล็อกจาก `AwaitingPayment` ทิ้ง**: ไม่มีทางปลดล็อกอัตโนมัติ = เอกสารค้างขายไม่ได้ถาวร และ merchant อื่นแตะ order ที่ล็อกไม่ได้ (cancel ผูก merchant floor) · ต้องรวมสถานะ "ผู้ให้บริการยืนยันแล้ว" ในเงื่อนไขล็อกด้วย มิฉะนั้นมีรูช่วงที่ session ชำระแล้วแต่ order ยังไม่ `Paid` เพราะ outbox ยังไม่ทำงาน | REQ-5.10 ถึง REQ-5.14, edge #5, #6, #12 |
| F20 | **F13 ต่อ** — รายละเอียดของ index สำหรับตรวจเอกสารที่ขายแล้ว | index เป็น HOW คงไม่เขียนเป็น REQ · แต่สิ่งที่วัดได้และกันปัญหาจริงคือ "อ่านครั้งเดียวต่อคำขอ" ไม่ใช่ N+1 บน 25 แถว จึงเขียนเป็น REQ-5.15 แทน · design ต้องสั่งชื่อ index, key, INCLUDE, เหตุผลที่ไม่ unique (order ที่ยกเลิกแล้วถือเอกสารเดียวกันได้โดยชอบธรรม) และห้ามเขียน query แบบที่ทำให้ seek พัง (`COLLATE` ในประโยค, `ToUpper`, `StringComparison`, trim ฝั่ง SQL) · collation ตรงกันทั้ง DB อยู่แล้ว (`Thai_100_CI_AS` พร้อม gate ใน `docker/bootstrap/01-principals.sql`) REQ-2.7 จึงเป็นข้อกันไม่ให้ใครมาทับ ไม่ต้องแก้โค้ด | REQ-5.15, ส่งต่อ design |
| F21 | **open #2** — ราคาต้นทางเปลี่ยนระหว่างของอยู่ในตะกร้า | user ตัดสิน: ตอนกดเพิ่มลงตะกร้า server ดึงข้อมูลใหม่จากต้นทางแล้วเก็บลงฐานข้อมูล ราคาที่เก็บตอนนั้นคือราคาขาย ไม่เทียบซ้ำตอน checkout — ตรงกับ REQ-4.1/4.6/4.7 ที่เขียนไว้แล้ว ไม่ต้องเพิ่ม 409 | ไม่มี (ยืนยันข้อเดิม) |
| F22 | **open #3** — `Merchants.Domain.Users.User` ไม่มีฟิลด์ `SaleCode` มีแต่ `ProducerCode` | user ตัดสิน: **เปลี่ยนชื่อ `ProducerCode` เป็น `SaleCode`** ไม่เพิ่มฟิลด์ใหม่ · แตะ 8 จุดในโค้ด (`Merchants.Domain/Users/User.cs`, `RegistrationAttempt.cs`, `SubmitRegistration.cs`, `GetRegistrationHistory.cs`, `Merchants.Infrastructure/.../UserConfigurations.cs`, `Persistence.MerchantUsers/Users/UserConfiguration.cs`, `Hosts/Api/Merchants/UserRegistration.cs`, `docker/bootstrap/seed-demo.sql`) + rename คอลัมน์สองตาราง + tests · ความยาวเดิม 64 ตัวอักษร แต่สัญญาต้นทางรับ 20 จึงต้องรัดลง | REQ-10 (ใหม่), REQ-4.8 ถึง REQ-4.10, edge #7, #11, #13 |

### รอบที่ 3, 2026-08-05 — ความยาว/ชนิดของ SaleCode และการเปลี่ยนชื่อบน wire

> เจาะลึกโดย agent `architect`

| รหัส | ประเด็น | คำตัดสิน | ผลต่อไฟล์ |
|---|---|---|---|
| F23 | ความยาวไม่ตรงกัน: `ProducerCode` เป็น `nvarchar(64)` แต่ต้นทางเป็น `varchar(20)` ทั้งพารามิเตอร์และคอลัมน์ · ความเสี่ยงจริงไม่ใช่ implicit conversion (ค่าไม่เคยอยู่ในประโยค SQL เดียวกัน) แต่คือ **silent truncation**: `SqlParameter.Size = 20` ตัดค่าที่ยาวกว่าให้เหลือ 20 ตัวแรกแล้วส่งไปโดยไม่มี error = ค้นด้วยรหัสของคนอื่นเงียบ ๆ · วันนี้ฟิลด์นี้ **ไม่มี validation ความยาวสักชั้น** ด่านเดียวคือ `HasMaxLength(64)` ที่ EF ซึ่งให้ 500 ไม่ใช่ 400 | รัดเหลือ 20 **ที่ขอบตอนสมัคร** ไม่ใช่ตอนใช้งาน (truncation ล้มเหลวเงียบ ตรวจตอนใช้งาน = ปล่อยค่าที่ใช้ไม่ได้เข้า DB แล้วเด้งไกลจากจุดที่แก้ได้) · เลือก non-unicode ให้ mirror ต้นทาง + บังคับรับเฉพาะ ASCII เพราะอักษรไทยหายเงียบทั้ง `varchar` และ `nvarchar` · **ไม่ต้องมีแผนจัดการข้อมูลเดิม**: ค่ายาวสุดใน seed คือ 10 ตัวอักษร fixture คือ 4 และไม่มีข้อมูล prod | REQ-4.10, REQ-4.11, REQ-10.6 |
| F24 | REQ-10.3 เปลี่ยนชื่อบน wire = breaking change · ชื่อโผล่บน wire สองจุด จุดละบรรทัดเดียว (ฟอร์มสมัคร + ประวัติการสมัครฝั่ง admin ที่คืน record ตรงโดยไม่มีชั้นแปลง) ผู้กินอยู่นอก repo ทั้งหมด · **gate จับให้ไม่ได้**: `check_rename_identifiers.py` blank string literal ทิ้งก่อนจับคู่ จึงมองไม่เห็น `Value(form, "producerCode")` และไม่มี OpenAPI snapshot test · ลืมแก้จุดเดียว = ฟอร์มยังตอบ 201 ค่ากลายเป็น null เงียบ แล้วไปโผล่เป็น 403 ตอนค้นแคตตาล็อก | เปลี่ยนบน wire ด้วย แบบ big-bang ตาม precedent `admin-actor-rename` · **ปัดช่วงรับสองชื่อทิ้ง**: repo นี้ไม่มีกระบวนการ deprecate wire field เลย (ไม่มี API version ที่สอง ไม่มี sunset header) fallback จะค้างถาวรและทำให้ REQ-10.4 เป็นเท็จตั้งแต่วันแรก · แต่ต้องมี test ยึดชื่อ เพราะนี่คือ failure mode เดียวในงานนี้ที่ล้มเหลวโดยไม่มีใครเห็น | REQ-10.3, REQ-10.7, REQ-10.8 |
| F25 | ค่า seed ของฟิลด์นี้ (`PRD-VP-001`, …) ไม่มีอยู่จริงในต้นทาง (ต้นทางมี `77001`-`77006`) พอ REQ-4.8 มีผล merchant user ที่ seed ไว้ทุกคนจะค้นได้ศูนย์แถวบน demo และแยกไม่ออกว่าพังหรือไม่มีของ — อาการเดียวกับที่เคยเจอใน `products-sp-53-alignment` | เขียนเป็นข้อบังคับใน REQ-6 ว่า seed ต้องใช้รหัสที่มีอยู่จริงในต้นทาง | REQ-6.10 |

**หมายเหตุส่งต่อ design:** เพิ่ม `ProducerCode` เข้า regex ของ `check_rename_identifiers.py` ได้และช่วยกัน
identifier C# กลับมาในอนาคต แต่ห้ามอ้างว่ามันคุ้มครอง REQ-10.4 อยู่แล้ว เพราะมันมองไม่เห็น string literal ·
จุดวางกฎความยาว/ASCII คือ `User.SetDetails` ซึ่งเป็นทางเดียวที่ค่าเข้าฟิลด์นี้ได้

**สถานะ artifact อื่น:** ยังไม่มี `design.md` และ `tasks.md` — ไม่ต้อง sync
