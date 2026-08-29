# Requirements: จำแนกความล้มเหลวของ dependency บนเส้นทางตรวจสถานะขายเอกสาร

> Status: unknown

เมื่อฐานข้อมูล VCentralPay (ฐานข้อมูลของแพลตฟอร์มเราเอง) ไม่พร้อม การตรวจว่าเอกสารประกันถูกขายไปแล้วหรือยัง
จะล้มเหลวและกลายเป็น 500 ที่ไม่บอกอะไร เอกสารนี้กำหนดว่าผู้เรียกแต่ละด่านควรเห็นอะไรแทน

## Overview

`DocumentSaleProbe` อ่าน `shop.OrderItems`, `shop.Orders` และ `txn.PaymentSessions` ด้วย EF
(`src/Persistence/Persistence.MerchantRuntime/Orders/DocumentSaleProbe.cs:59-73`) เมื่อการอ่านนั้นล้มเหลว
`Microsoft.Data.SqlClient.SqlException` ไม่ถูกห่อเป็น exception ของชั้นแอปเลย จึงตกถังท้าย `_ => 500` ของ
`src/BuildingBlocks/BuildingBlocks.Web/ProblemDetailsExceptionHandler.cs:72-73`

เทียบกับการอ่านต้นทางภายนอก (`hippodb`/`mammothdb`) ซึ่งจับ `SqlException` ของตัวเองแล้วห่อเป็น
`UpstreamUnavailableException` ให้เป็น 503 ตาม REQ-7.1 ของ `products-external-source-of-truth`
(`src/Modules/Products/Products.Infrastructure/Sp/SpDocumentGateway.cs:80-95`) ผลคือ dependency สองตัวที่ล้ม
ด้วยเหตุเดียวกันให้คำตอบคนละอย่าง

### ทำไมจึงเป็นช่องว่าง ไม่ใช่การละเมิด spec เดิม

REQ-7.1/7.5 ของ `products-external-source-of-truth` มีประธานเป็น "การเรียกต้นทาง" คือ `SearchAsync`/`LookupAsync`
ของ `ISpDocumentGateway` เท่านั้น ส่วน REQ-5 ซึ่งเป็นเจ้าของ probe กำหนดเพียง semantics ขายได้/ขายไม่ได้
ไม่มี criterion ใดผูก status code ตอนฐานข้อมูลแพลตฟอร์มล่ม — ยืนยันโดย reviewer รอบ 5
(`.pipeline/products-external-source-of-truth/review-t5.md:258-275`)

### หลักฐานว่าเกิดจริง

CI check `dotnet integration (live SQL 2025)` ของ PR #184 แดงที่ `GET /api/v1/products` ด้วย 500 และ
reproduce ได้ 3/3 บน Linux ว่า `SqlException` ระดับ connection (`TCP Provider, error 35`) ออกมาจาก
`DocumentSaleProbe.ProbeAsync` จุด `ToListAsync` โดยไม่มีชั้นไหน map
(`.pipeline/products-external-source-of-truth/changes-t5.md:315-345`)

### ด่านที่ได้รับผลกระทบ

| ด่าน | ตำแหน่งเรียก probe | คำตอบเมื่อเอกสารถูกถือครอง | คำตอบวันนี้เมื่อ DB ล่ม |
|---|---|---|---|
| เพิ่มรายการเข้าตะกร้า | `src/Hosts/Api/Program.cs:716-719` | 400 | 500 |
| เริ่ม checkout | `src/Hosts/Api/Program.cs:850-853` | 409 | 500 |
| สร้าง payment session (ก่อนมินต์ charge) | `src/Modules/Payments/Payments.Application/CreateSession/CreateSessionHandler.cs:151-154` | 409 | 500 |
| `GET /products` | `src/Modules/Products/Products.Application/ListProducts.cs:303-305` | ตัดออก/ติดธง | 500 |

---

## REQ-1: จำแนกความล้มเหลวของ dependency ให้ต่างจากคำตอบของการตรวจ

**User Story:** ในฐานะ merchant user ฉันอยากแยกออกว่า "เอกสารใบนี้ขายไม่ได้" กับ "ระบบตรวจให้ไม่ได้ตอนนี้"
เพื่อจะได้รู้ว่าควรลองใหม่หรือควรเลิกซื้อเอกสารใบนี้

**Acceptance Criteria (EARS):**

- 1.1 IF การอ่านฐานข้อมูล VCentralPay เพื่อตรวจสถานะขายเอกสารล้มเหลวด้วยเหตุ transport หรือ infrastructure
  (ต่อไม่ติด, TLS/pre-login handshake ล้ม, timeout, login หรือสิทธิ์ไม่ผ่าน) THEN THE SYSTEM SHALL ตอบด้วย
  status code ที่แยกจาก 500 ตามที่ตัดสินใน D1
- 1.2 THE SYSTEM SHALL ให้ทุกด่านที่เรียกการตรวจสถานะขายบนเส้นทางเงิน (เพิ่มรายการเข้าตะกร้า, เริ่ม checkout,
  สร้าง payment session) ตอบด้วย status code เดียวกันในกรณี 1.1 โดยไม่แยกตามด่าน
- 1.3 THE SYSTEM SHALL ไม่ใช้ status code ของกรณี 1.1 ซ้ำกับ status code ที่ spec เดิมกำหนดให้แปลว่า
  "เอกสารถูกถือครองแล้ว" (400 ตาม REQ-5.4, 409 ตาม REQ-5.5 และ REQ-5.6 ของ `products-external-source-of-truth`)
- 1.4 IF ผู้เรียกยกเลิกคำขอ (cancellation) ระหว่างการอ่าน THEN THE SYSTEM SHALL ไม่รายงานกรณีนั้นเป็น
  dependency ล้มเหลว
- 1.5 THE SYSTEM SHALL ไม่แปลงความล้มเหลวของการเขียน (`SaveChanges` หรือ commit) เป็นคำตอบตาม 1.1 —
  การเขียนที่ผลลัพธ์ไม่แน่นอนต้องคงพฤติกรรมเดิมทุกประการ
- 1.6 THE SYSTEM SHALL ไม่เปลี่ยน status code ของกรณีที่ spec เดิมกำหนดไว้แล้ว (REQ-7.1, REQ-7.2, REQ-7.4,
  REQ-7.5 และ REQ-5.4 ถึง REQ-5.6 ของ `products-external-source-of-truth`)

**เหตุผลที่คำตอบต่างจากกรณีต้นทางภายนอกได้ ทั้งที่อาการเหมือนกัน:** `UpstreamUnavailableException` นิยามตัวเอง
ว่าเป็น "a system outside our own data plane" (`src/BuildingBlocks/BuildingBlocks.Application/UpstreamUnavailableException.cs:4-9`)
การนำมาใช้กับฐานข้อมูลของเราเองทำให้ log และ alert แยกไม่ออกว่าใครล่ม — ดู D1

---

## REQ-2: ตรวจไม่ได้ ไม่เท่ากับ ขายได้

**User Story:** ในฐานะเจ้าของแพลตฟอร์ม ฉันต้องไม่ให้ความล้มเหลวของฐานข้อมูลกลายเป็นช่องขายเอกสารซ้ำ

**Acceptance Criteria (EARS):**

- 2.1 IF การตรวจสถานะขายเอกสารล้มเหลว THEN THE SYSTEM SHALL ไม่ถือว่าเอกสารในคำขอนั้นขายได้
- 2.2 IF การตรวจล้มเหลวตอนเพิ่มรายการเข้าตะกร้า THEN THE SYSTEM SHALL ไม่บันทึกรายการลงตะกร้า
- 2.3 IF การตรวจล้มเหลวตอนเริ่ม checkout THEN THE SYSTEM SHALL ไม่สร้าง checkout session
- 2.4 IF การตรวจล้มเหลวตอนสร้าง payment session THEN THE SYSTEM SHALL ไม่สร้างรายการเรียกเก็บเงินกับ PSP
  และไม่บันทึก session row
- 2.5 THE SYSTEM SHALL ไม่มีค่า config, flag หรือโหมดใดที่ข้ามการตรวจนี้แล้วปล่อยให้คำขอเดินต่อได้
- 2.6 IF การตรวจล้มเหลวระหว่างตอบ `GET /products` THEN THE SYSTEM SHALL ไม่คืนหน้ารายการที่ยังไม่ผ่านการตัด
  เอกสารที่ขายแล้วตาม REQ-5.8 ของ `products-external-source-of-truth` (ทางเลือกที่ยังไม่ล็อก — ดู D3)
- 2.7 WHEN การตรวจล้มเหลว THE SYSTEM SHALL ไม่ทิ้ง state ค้างที่ทำให้คำขอเดิมสำเร็จบางส่วน — ผลลัพธ์ของคำขอนั้น
  ต้องเท่ากับคำขอที่ไม่เคยเกิดขึ้น

---

## REQ-3: สิ่งที่ client เห็นต้องไม่รั่วข้อมูลภายใน

**User Story:** ในฐานะเจ้าของแพลตฟอร์ม ฉันต้องไม่ให้ข้อความ error บอกโครงสร้างภายในหรือข้อมูลของ merchant รายอื่น

**Acceptance Criteria (EARS):**

- 3.1 THE SYSTEM SHALL ไม่ใส่ข้อความของ exception, SQL text, ชื่อ server, ชื่อฐานข้อมูล, connection string
  หรือชื่อ principal ลงใน response — ต่อยอดกฎเดิม REQ-7.3 ของ `products-external-source-of-truth` ให้ครอบ
  ฐานข้อมูลแพลตฟอร์มด้วย
- 3.2 THE SYSTEM SHALL ไม่เปิดเผยรหัส order, รหัสหรือชื่อ merchant รายอื่นในข้อความตอบกลับของกรณีนี้
  (กฎเดียวกับ REQ-5.7 ของ `products-external-source-of-truth`)
- 3.3 THE SYSTEM SHALL ใช้ข้อความ detail ที่เป็นค่าคงที่ต่อหนึ่งชนิดความล้มเหลว โดยไม่ derive จาก exception
  ใด ๆ — ตรงกับวินัยที่ `ProblemDetailsExceptionHandler` ประกาศไว้แล้วที่
  `src/BuildingBlocks/BuildingBlocks.Web/ProblemDetailsExceptionHandler.cs:52-53`
- 3.4 THE SYSTEM SHALL ไม่ระบุในผลตอบกลับว่าเอกสารใบใดในคำขอเป็นต้นเหตุ

---

## REQ-4: สัญญาณฝั่ง server เมื่อ dependency ล้ม

**User Story:** ในฐานะผู้ดูแลระบบ ฉันอยากรู้จาก log ว่าเป็นฐานข้อมูลของเราล่ม ไม่ใช่ต้นทางล่ม และล่มที่ด่านไหน

**Acceptance Criteria (EARS):**

- 4.1 WHEN การอ่านตาม 1.1 ล้มเหลว THE SYSTEM SHALL บันทึก log ระดับ error ฝั่ง server พร้อม exception ตัวจริง
  และหมายเลข/สถานะ/คลาสของข้อผิดพลาด SQL — รูปแบบเดียวกับที่ต้นทางทำอยู่แล้วที่
  `src/Modules/Products/Products.Infrastructure/Sp/SpDocumentGateway.cs:91-93`
- 4.2 THE SYSTEM SHALL ให้ log ของกรณีนี้แยกออกจาก log ของกรณี "ต้นทางภายนอกล่ม" ได้ด้วยการกรอง โดยไม่ต้อง
  อ่าน stack trace
- 4.3 THE SYSTEM SHALL ไม่ส่งเหตุการณ์นี้เข้า `ISecurityTelemetry` — `DenialCategory` ทั้ง 11 ค่าที่ pin ไว้
  (`src/BuildingBlocks/BuildingBlocks.Application/ISecurityTelemetry.cs`) ไม่มีค่าใดหมายถึง dependency ไม่พร้อม
  และ probe ถูกตัดสินไว้แล้วว่าไม่ emit telemetry (`tests/Architecture.Tests/BypassPrimitiveTests.cs:39`,
  design finding S5)
- 4.4 THE SYSTEM SHALL ไม่บันทึก connection string, credential หรือ PII ลง log ของกรณีนี้

---

## REQ-5: ไม่รื้อพฤติกรรมที่ทำงานอยู่

**User Story:** ในฐานะผู้พัฒนา ฉันอยากให้การแก้เรื่อง status code ไม่ลากเรื่อง transaction หรือ retry เข้ามาด้วย

**Acceptance Criteria (EARS):**

- 5.1 THE SYSTEM SHALL คงผลลัพธ์ของกรณีปกติ (การตรวจสำเร็จ) ไว้เท่าเดิมทุกด่าน ทั้ง status code และ payload
- 5.2 THE SYSTEM SHALL คงจำนวนการอ่านฐานข้อมูลต่อหนึ่งคำขอไว้เท่าเดิม — หนึ่งการอ่านต่อคำขอตาม REQ-5.15 ของ
  `products-external-source-of-truth`
- 5.3 THE SYSTEM SHALL ไม่เปลี่ยนขอบเขตของ transaction ที่มีอยู่ ทั้ง `ExecuteInTransactionAsync`
  (`src/Persistence/Persistence.MerchantRuntime/MerchantRuntimeUnitOfWork.cs:62-74`) และ transaction ที่
  `VaultAuditAppender` เป็นเจ้าของเอง (`src/Persistence/Persistence.MerchantRuntime/Vault/VaultAuditAppender.cs:45`)
- 5.4 THE SYSTEM SHALL ไม่เปิด `EnableRetryOnFailure` เป็นส่วนหนึ่งของงานนี้ (ทางเลือกที่ยังไม่ล็อก — ดู D4)

---

## ขอบเขต

### อยู่ในขอบเขต

- การอ่านฐานข้อมูล VCentralPay ที่เกิดในเส้นทางตอบคำขอ HTTP และล้มเหลวด้วยเหตุ transport/infrastructure
- ด่านทั้งสี่ในตารางหัวข้อ Overview
- การจำแนก, สิ่งที่ client เห็น และ log ฝั่ง server ของกรณีนั้น

### อยู่นอกขอบเขต

- ความล้มเหลวของการเขียน (`SaveChanges`, commit) — ผลลัพธ์ไม่แน่นอน จึงเป็นคนละ failure class และการตอบ
  ให้ retry ได้คือทางสู่การเรียกเก็บเงินซ้ำ ตามที่ `UpstreamUnavailableException` เตือนไว้เองที่
  `src/BuildingBlocks/BuildingBlocks.Application/UpstreamUnavailableException.cs:8-9`
- ความล้มเหลวของต้นทางภายนอก — ครอบแล้วโดย REQ-7 ของ `products-external-source-of-truth`
- การเปลี่ยนกฎว่าเอกสารใบไหนขายได้ (REQ-5 ของ spec เดิมไม่ถูกแตะ)
- การตั้งค่า connection pooling ของ production และการปรับ CI runner

---

## Edge Cases & Open Questions

### กรณีขอบที่ต้องมี test

1. การอ่านล้มเหลวที่ด่านเพิ่มรายการเข้าตะกร้า — ตะกร้าต้องไม่มีรายการเพิ่มขึ้นหลังคำขอ (REQ-2.2)
2. การอ่านล้มเหลวที่ด่านสร้าง payment session — ต้องไม่มี session row และไม่มีการเรียก PSP adapter (REQ-2.4)
3. การอ่านล้มเหลวขณะที่เอกสารในคำขอ "ขายได้จริง" — คำตอบต้องเป็นความล้มเหลว ไม่ใช่ 200 (REQ-2.1)
4. คำขอถูกยกเลิกกลางการอ่าน — ต้องไม่ถูกนับเป็น dependency ล่ม (REQ-1.4) กันอาการเดียวกับที่ต้นทางกันไว้ที่
   `SpDocumentGateway.cs:84`
5. response body ของกรณีล้มเหลว — ต้องไม่มีสตริงของ SQL, ชื่อ host หรือรหัส order (REQ-3.1, REQ-3.2)
6. ต้นทางล่มกับฐานข้อมูลแพลตฟอร์มล่ม — ต้องแยกกันได้ใน log (REQ-4.2)

### ผลข้างเคียงที่ต้องยอมรับ

7. ผู้ใช้จะเห็นความล้มเหลวถี่ขึ้นในสายตา (แยกออกมาจากถัง 500 เดิม) แม้จำนวนความล้มเหลวจริงเท่าเดิม
8. เมื่อฐานข้อมูลแพลตฟอร์มไม่พร้อม แคตตาล็อกจะเรียกดูไม่ได้เลย ไม่ใช่แค่ซื้อไม่ได้ (ตาม D3 ที่จะเลือก)

### ข้อเสนอนอกขอบเขต ไม่ผูกเข้างานนี้

- guard เรื่อง collation ใน `DocumentSaleProbe.cs:88-91` โยน `InvalidOperationException` ซึ่ง
  `ProblemDetailsExceptionHandler.cs:68-69` แม็พเป็น 409 = ผู้เรียกอ่านได้ว่า "เอกสารถูกถือครอง" ทั้งที่ความจริง
  คือ "ตรวจไม่ได้" เป็นคลาสเดียวกับ D1 แต่คนละ trigger — ควรเปิดเป็นข้อตัดสินแยกหลัง D1 ล็อก
- doc comment ที่ `MerchantRuntimeUnitOfWork.cs:7-8` เขียนว่า transaction "retry-safe under transient SQL Server
  faults" แต่ `EnableRetryOnFailure` ไม่ถูกตั้งที่ใดเลยในโค้ด (grep ทั้ง `src` และ `tests` ได้ 0 จุด) —
  คำอธิบายจึงยังไม่จริง ควรแก้ไม่ว่าจะเลือก D4 ทางไหน

---

## จุดตัดสินใจที่รอ approve

### D1: ผู้เรียกเห็น status code อะไร และห่อด้วย exception ตัวไหน

| ทางเลือก | ข้อดี | ข้อเสีย |
|---|---|---|
| A. ใช้ `UpstreamUnavailableException` ตัวเดิม -> 503 | ไม่มีชนิดใหม่ ไม่ต้องแตะ handler เลย มี arm อยู่แล้วที่ `ProblemDetailsExceptionHandler.cs:70-71` | ขัดนิยามของ exception เองที่เขียนว่า "outside our own data plane" ทำให้ log/alert แยกไม่ออกว่าต้นทางล่มหรือเราล่ม ขัด REQ-4.2 |
| B. ชนิดใหม่ (เช่น `DependencyUnavailableException`) -> 503 เหมือนกัน | client เห็น 503 เท่ากัน retry ได้เหมือนกัน แต่ฝั่ง server แยกสองสาเหตุออกจากกันชัด | เพิ่มชนิด 1 ตัว + arm 1 บรรทัดใน handler |
| C. คง 500 ไว้ แค่ทำให้ log พูดได้ | ซื่อสัตย์ว่าเป็นความผิดฝั่งเรา ไม่มีการแก้พฤติกรรมใด ๆ | 500 สื่อว่า "อย่าลองใหม่" ทั้งที่การอ่านนี้ retry ปลอดภัยเสมอ และเส้นเงินจะไม่มีสัญญาณให้ client ลองใหม่เลย |

**แนะนำ B** — client เห็น 503 ซึ่งตรงกับความจริง (การอ่านไม่เกิดผลข้างเคียง จึงลองใหม่ได้)
ส่วนการแยกสองสาเหตุออกจากกันคือสิ่งที่ REQ-4.2 ต้องการ และเป็นเหตุผลเดียวที่ทำให้ A ไม่พอ
ต้นทุนส่วนต่างระหว่าง A กับ B คือไฟล์ใหม่หนึ่งไฟล์กับหนึ่งบรรทัดใน handler

### D2: ขอบเขตของประธานในข้อกำหนด — แค่ probe หรือการอ่าน DB แพลตฟอร์มทั้งชั้น

| ทางเลือก | ข้อดี | ข้อเสีย |
|---|---|---|
| S1. เฉพาะ `DocumentSaleProbe` (4 ด่าน) | diff เล็กสุด ตรงกับจุดที่ auditor เจอจริง | ด่านเดียวกันยังมีการอ่าน DB แพลตฟอร์มอื่นที่ล้มแบบเดียวกันแล้วได้ 500 (อ่านตะกร้า, อ่าน order, อ่าน connection, อ่าน session) — เป็น failure class เดียวกันที่แก้ไม่จบรอบเดียว |
| S2. การอ่านทุกจุดของ `Persistence.MerchantRuntime` ในเส้นทางตอบคำขอ (ไม่รวมการเขียน) | ปิดทั้ง class รอบเดียว วันนี้ไม่มีการอ่านจุดใดแปล exception เลย — `MerchantRuntimeUnitOfWork.cs:20-51` แปลเฉพาะฝั่งเขียน 3 กรณี | ต้องนิยาม seam ให้ชัดว่าอะไรคือ "การอ่านในเส้นทางตอบคำขอ" และต้องพิสูจน์ว่าไม่กลืนการเขียนเข้าไปด้วย |
| S3. เพิ่ม arm ของ `SqlException`/`DbException` ที่ `ProblemDetailsExceptionHandler.Map` จุดเดียว | เปลี่ยนบรรทัดเดียว ครอบทุก context ทุก endpoint | **ไม่ปลอดภัย** — จะกลืนความล้มเหลวของการเขียนที่ผลลัพธ์ไม่แน่นอนไปเป็น 503 ที่แปลว่า "ลองใหม่ได้" ตรงกับสิ่งที่ `UpstreamUnavailableException.cs:8-9` ห้ามไว้ตรง ๆ เป็นทางสู่การเรียกเก็บเงินซ้ำ |

**แนะนำ S2** และตัด S3 ทิ้งด้วยเหตุผลด้านความปลอดภัยข้างต้น เหตุผลที่ไม่เอา S1: กฎ class sweep ของ repo นี้
บอกว่าเจอสมาชิกหนึ่งตัวต้องไล่ทั้งกลไกในรอบเดียว — probe เป็นแค่จุดที่ CI บังเอิญจับได้ ไม่ใช่จุดเดียวที่พัง
ถ้ายอมรับ S1 ควรยอมรับพร้อมกันว่าจะมีรอบสองแน่ ๆ

### D3: `GET /products` ทำอย่างไรเมื่อการตรวจล้มเหลว

| ทางเลือก | ข้อดี | ข้อเสีย |
|---|---|---|
| P1. ล้มทั้งคำขอเหมือนด่านอื่น | สอดคล้องกับ REQ-2.1 ทั้งฉบับ ไม่มีทางที่หน้ารายการจะโชว์เอกสารที่ขายไปแล้วว่าว่าง | ฐานข้อมูลแพลตฟอร์มล่ม = เรียกดูแคตตาล็อกไม่ได้เลย แม้ต้นทางยังตอบได้ |
| P2. คืนหน้ารายการโดยไม่ตัด/ไม่ติดธง | ยังเรียกดูของได้ระหว่างที่ DB เราล่ม | ขัด REQ-5.8 ของ spec เดิมโดยตรง และผู้ใช้จะกดเพิ่มลงตะกร้าแล้วโดนปฏิเสธอยู่ดี = ให้ความหวังผิด ๆ |

**แนะนำ P1** — P2 ไม่ได้ทำให้ผู้ใช้ทำงานสำเร็จเพิ่มขึ้นเลย เพราะด่านถัดไป (เพิ่มลงตะกร้า) อ่าน DB เดียวกัน
และจะล้มด้วยเหตุเดียวกัน แลกมาด้วยการโชว์ของที่ขายไปแล้วว่ายังว่าง

### D4: retry/transient — เขียนเป็นข้อกำหนดหรือกันออกนอกขอบเขต

**ข้อเท็จจริงที่ต้องรู้ก่อนตัดสิน:**

- `EnableRetryOnFailure` ไม่ถูกตั้งที่ใดเลย (grep `src` + `tests` = 0 จุด) — `CreateExecutionStrategy()` ที่
  `MerchantRuntimeUnitOfWork.cs:66` จึงคืน strategy ที่ไม่ retry วันนี้
- transaction ที่ผ่าน `ExecuteInTransactionAsync` ห่อด้วย strategy อยู่แล้ว จึงรองรับการเปิด retry ได้
- แต่ `VaultAuditAppender.cs:45` เปิด transaction เองนอก strategy โดยจงใจ (อธิบายไว้ที่ `:11-13` ว่าต้อง match
  ขอบเขตของ `sp_getapplock` เดิม) — เปิด retry แล้วจุดนี้จะโยน `InvalidOperationException` ซึ่ง
  `ProblemDetailsExceptionHandler.cs:68-69` แม็พเป็น **409** ไม่ใช่ 500 คือพังแบบอ่านผิดความหมายด้วย
- การ retry ทั้ง transaction คือการรันงานเขียนใหม่ทั้งก้อน ซึ่งยังไม่มีใครพิสูจน์ว่างานเขียนบนเส้นเงินทุกตัว
  รันซ้ำได้อย่างปลอดภัย

| ทางเลือก | ข้อดี | ข้อเสีย |
|---|---|---|
| R1. กันออกนอกขอบเขต (ตาม REQ-5.4 ที่ร่างไว้) | งานนี้จบเป็นเรื่องเดียวคือการจำแนก ไม่มีผลข้างเคียงต่อ transaction; 503 ที่ retry ได้ก็เป็นคำตอบที่ถูกต้องอยู่แล้วโดยไม่ต้องมี retry ฝั่ง server | อาการ transient ที่เคยทำ CI แดงจะยังเกิด เพียงแต่เห็นเป็น 503 แทน 500 |
| R2. รวมเข้ามาในงานนี้ | ลดจำนวนความล้มเหลวที่ผู้ใช้เห็นจริง | ต้องแก้ `VaultAuditAppender` ให้เข้า strategy ก่อน แตะ vault audit chain ซึ่งเป็นของอ่อนไหว และต้องพิสูจน์ความปลอดภัยของการรันซ้ำทุก transaction บนเส้นเงิน = งานคนละขนาดกัน |

**แนะนำ R1** — เก็บ retry เป็น spec ถัดไป งานนี้ตอบคำถาม "ผู้เรียกเห็นอะไร" ให้จบก่อน โดยไม่พึ่ง retry
เพื่อให้ข้อกำหนดเป็นจริง หากเลือก R2 ต้องถอด REQ-5.4 ออกและเพิ่ม REQ ใหม่เรื่องความปลอดภัยของการรันซ้ำ
