# Requirements: รื้อ POL Platform รุ่นแรกสำหรับทีมเล็ก

> Status: approved 2026-09-09

## ภาพรวม

ปรับ pol-core จากเจ้าของข้อมูลและ project ที่ซ้ำกันเป็น Backend เดียวที่ทีมเล็กดูแลได้ โดยคง Account, Access, Merchant, Order, Checkout, Transaction และ Notification ตามชุด pol-platform-latest ไม่เพิ่ม Payment aggregate และไม่สูญเสียรายการหรือหลักฐานเดิม

ผู้ใช้มอบหมายให้จัด spec ทั้งชุดก่อน implementation รอบนี้ไม่แก้โค้ด ไม่ deploy และไม่อนุมัติการลบข้อมูล เอกสารนี้ล็อกค่าเริ่มต้นที่จำเป็นสำหรับรุ่นแรกเป็นข้อเสนอที่ตรวจทานได้ ไม่ใช่ approval metadata

## ขอบเขตรุ่นแรกและค่าที่เลือก

| เรื่อง | ข้อตกลงรุ่นแรก |
|---|---|
| โครงสร้าง | 4 source projects, 3 test projects, API host เดียว, SQL Server database เดียว, BackgroundService ใน host เดิม |
| ผู้ใช้ | Employee, Agent, System และลูกค้าแบบ capability โดยไม่สร้าง Account ลูกค้า |
| Access | หนึ่ง active MerchantAccess ต่อ Account/Merchant; DataScope อยู่ Access; PlatformAccess แยกสำหรับ Employee |
| Agent | หนึ่ง Account ผูก Sale เดียวใน Merchant เดียว; case การสมัครตรึง identity และ Merchant |
| SYSTEM | หนึ่ง Client ต่อ SYSTEM Account แต่ Merchant มีหลาย Client ได้; Client ผูก Merchant/environment เดียว |
| การชำระ | เต็มยอด Order สกุลเดียว ไม่มี partial/split payment, wallet, ledger, refund/void/capture/settlement APIs |
| API | 111 รายการตาม api-scope.json; defer API-033, API-034 และ API-108 ถึง API-110 |
| การแจ้งเตือน | Email และ SMS สำหรับผลสมัคร, business webhook หนึ่งปลายทางต่อ Merchant; templates อยู่ใน release ที่ versioned |
| การย้าย | รักษาข้อมูล ใช้ maintenance cutover แบบควบคุม ไม่ทำ rolling dual writers และไม่ reset DB |
| งานที่ไม่ทำ | OTP engine, template/rule designer, future PSP, Microservices, message broker/Redis ใหม่ และการออกกรมธรรม์ |

## REQ-1: โครงสร้างและความรับผิดชอบ

**ความต้องการของผู้ใช้:** ในฐานะทีมดูแล ฉันต้องการหาเจ้าของงานและจุดแก้ไขได้โดยไม่ต้องไล่ project ซ้ำหลายชุด

**เกณฑ์การยอมรับ:**

- 1.1 ระบบต้องจัด source ที่ส่งมอบไว้ใน Pol.Domain, Pol.Application, Pol.Infrastructure และ Pol.Api
- 1.2 ระบบต้องจัด tests ไว้ใน Pol.UnitTests, Pol.ArchitectureTests และ Pol.IntegrationTests
- 1.3 ระบบต้องมีเจ้าของการเปลี่ยนข้อมูลหลักเพียงโมดูลเดียวสำหรับแต่ละ entity ตาม design
- 1.4 ระบบต้องใช้ API host เดียวสำหรับ HTTP และงานเบื้องหลังของรุ่นแรก
- 1.5 ระบบต้องแยก runtime persistence เป็น Control Plane และ Commerce โดยไม่ใช้ migration context เป็น runtime store
- 1.6 ระบบต้องใช้ schema mapping เจ้าของเดียวแทนการคัดลอก column/index ระหว่าง contexts
- 1.7 หาก dependency ของ Domain หรือ Application อ้าง Infrastructure หรือ HTTP host โดยตรง ระบบต้องทำให้ architecture test ล้มเหลว
- 1.8 เมื่อย้าย packaging โดยยังไม่สลับ business contract ระบบต้องรักษาพฤติกรรมที่ baseline tests เดิมครอบคลุม

## REQ-2: Account และการยืนยันตัวตน

**ความต้องการของผู้ใช้:** ในฐานะพนักงาน ตัวแทน และระบบเชื่อมต่อ ฉันต้องเข้าสู่ระบบตามบทบาทโดยมีบัญชีอ้างอิงกลาง

**เกณฑ์การยอมรับ:**

- 2.1 ระบบต้องอ้างบัญชีธุรกิจด้วย AccountId โดยไม่จับคู่บุคคลจาก email
- 2.2 เมื่อ human login ผ่านการตรวจ ระบบต้อง resolve identity ด้วย Provider, TenantId และ ExternalUserId
- 2.3 หาก issuer, tenant, audience, signature, lifetime หรือ OIDC state/nonce ไม่ผ่าน ระบบต้องปฏิเสธ login
- 2.4 เมื่อ Employee ที่เข้าเกณฑ์และยังไม่มีบัญชี login ระบบต้องสร้าง Account/Employee/LoginAccount เพียงชุดเดียวแม้มีคำขอแข่งกัน
- 2.5 เมื่อสร้าง Employee แบบ JIT ระบบต้องไม่มอบ PlatformAccess หรือ MerchantAccess อัตโนมัติ
- 2.6 ระบบต้องออก Platform access token สำหรับ human ด้วย authorization code ที่บังคับ PKCE
- 2.7 ระบบต้องยืนยัน SYSTEM ด้วย client_credentials และ private_key_jwt โดยใช้ public keys ที่ลงทะเบียน
- 2.8 หาก client assertion ถูก replay หรือใช้ key ที่เพิกถอน/หมดอายุ ระบบต้องปฏิเสธการออก token
- 2.9 ระบบต้องไม่ออก refresh token ให้ SYSTEM
- 2.10 เมื่อ Account หรือ Client ถูกระงับ ระบบต้องปฏิเสธคำขอใหม่หลังบันทึกการระงับแล้ว
- 2.11 ระบบต้องให้ OAuth component เป็นเจ้าของ token/code/authorization state เพียงชุดเดียว
- 2.12 ขณะที่ผู้สมัครยังไม่มี Account ที่อนุมัติ ระบบต้องให้เพียง registration session ที่จำกัด identity และ Merchant
- 2.13 เมื่อ human เปลี่ยน Merchant context ระบบต้องตรวจการเข้าถึงก่อนออก token context ใหม่
- 2.14 เมื่อ logout หรือ revoke human session ระบบต้องตัดสิทธิ์ต่ออายุของ session ที่เลือก

## REQ-3: Access และการแยกข้อมูล

**ความต้องการของผู้ใช้:** ในฐานะผู้ดูแล ฉันต้องให้สิทธิ์เท่าที่จำเป็นโดยไม่ข้ามบริษัทหรือทำให้สิทธิ์อ่านขยายเป็นสิทธิ์เขียนเอง

**เกณฑ์การยอมรับ:**

- 3.1 ระบบต้องเก็บ DataScope บน MerchantAccess ที่ผูก Account และ Merchant ชัดเจน
- 3.2 ระบบต้องจำกัดหนึ่ง active Access ต่อ Account/Merchant ในรุ่นแรก
- 3.3 ระบบต้องประเมิน AccessRoles และ BranchAccess ภายใน Access เดียวกัน
- 3.4 หากไม่มีบริบทที่อนุญาต ระบบต้องปฏิเสธการเข้าถึงแทนการตีความเป็น all-merchants
- 3.5 ระบบต้องให้ PlatformAccess ใช้ได้เฉพาะ Employee ที่ได้รับ role ส่วนกลาง
- 3.6 เมื่อ Agent ใช้ SELF ระบบต้องกรอง Order ตาม OwnerSaleId ของ Agent ภายใน Merchant
- 3.7 เมื่อใช้ BRANCH หรือ ASSIGNED_BRANCHES ระบบต้องกรองด้วย OwnerBranchIdAtCreation และสาขาที่อนุญาตตาม design
- 3.8 หาก Employee หรือ SYSTEM ขอ SELF โดยไม่มีนโยบายที่รองรับ ระบบต้องปฏิเสธ assignment
- 3.9 เมื่ออ่าน child, snapshot, history หรือ export ระบบต้องใช้ขอบเขตเจ้าของ Order เช่นเดียวกับการอ่าน Order
- 3.10 เมื่อให้สิทธิ์ใหม่ ระบบต้องไม่อนุญาตให้ผู้มอบให้สิทธิ์เกินขอบเขตที่ตนมอบได้
- 3.11 เมื่อ permission, role หรือ access เปลี่ยน ระบบต้องทำให้ token context รุ่นเก่าใช้สิทธิ์เดิมต่อไม่ได้
- 3.12 หาก custom role หรือ branch อยู่คนละ Merchant กับ Access ระบบต้องปฏิเสธการผูก

## REQ-4: การสมัครและอนุมัติตัวแทน

**ความต้องการของผู้ใช้:** ในฐานะผู้สมัครและผู้พิจารณา ฉันต้องติดตามแต่ละรอบได้และไม่เกิดบัญชีหรือสิทธิ์ครึ่งชุด

**เกณฑ์การยอมรับ:**

- 4.1 เมื่อบันทึกร่าง ระบบต้องใช้ Registration case ของ verified identity เดิมโดยไม่สร้าง Account
- 4.2 เมื่อ submit ร่างที่ผ่านการตรวจ ระบบต้องสร้าง Attempt snapshot ใหม่พร้อมหมายเลขรอบและ SubmittedAt
- 4.3 หาก submit เจตนาเดิมซ้ำ ระบบต้องคืนผลรอบเดิมโดยไม่สร้าง Attempt ซ้อน
- 4.4 ขณะที่มีรอบ pending ระบบต้องไม่รับ submit เจตนาใหม่ซ้อนรอบนั้น
- 4.5 เมื่อ approve ระบบต้องตรวจว่าเป็นรอบปัจจุบันและ Sale/Branch/identity ยังตรงกับเงื่อนไขที่ใช้พิจารณา
- 4.6 หาก Sale ถูกผูกกับ Agent อื่น ระบบต้องปฏิเสธ approval แม้เคยผ่านการตรวจตอน submit
- 4.7 เมื่อ approve สำเร็จ ระบบต้องบันทึก Account, LoginAccount, Agent, initial Access, decision และงานแจ้งผลแบบ atomic
- 4.8 เมื่อ reject ระบบต้องเก็บเหตุผลที่เผยแก่ผู้สมัครได้โดยไม่สร้าง Account
- 4.9 หาก approve/reject แข่งขันกัน ระบบต้องยอมรับผลตัดสินเพียงคำสั่งเดียว
- 4.10 เมื่อผู้ถูก reject ยื่นใหม่ ระบบต้องคง case และประวัติ Attempt เก่าที่ตัดสินแล้ว
- 4.11 เมื่อผู้สมัครอ่านสถานะหรือประวัติ ระบบต้องไม่เปิด InternalReviewNote หรือข้อมูลผู้สมัครรายอื่น
- 4.12 เมื่อพิจารณาผลสมัคร ระบบต้องใช้ contact snapshot ของรอบนั้นสำหรับ Email และ SMS

## REQ-5: Merchant และ Provider configuration

**ความต้องการของผู้ใช้:** ในฐานะผู้ดูแลการชำระ ฉันต้องตั้งค่าบริษัทและ PSP โดยตรวจย้อนหลังได้และไม่เปลี่ยนรายการที่เริ่มไปแล้ว

**เกณฑ์การยอมรับ:**

- 5.1 ระบบต้องกำหนด Merchant, Sale และ Branch จาก master ที่มีเจ้าของชัดเจนโดยใช้รหัสไม่ซ้ำภายใน Merchant
- 5.2 หาก Sale/Branch/ProviderAccount ที่อ้างไม่อยู่ใน Merchant ที่กำหนด ระบบต้องปฏิเสธการเขียน
- 5.3 เมื่อเสนอเปลี่ยน routing, environment หรือ active credential ระบบต้องสร้างคำขอที่อ้าง BaseVersion
- 5.4 หาก maker เป็นผู้พิจารณาคำขอของตนเอง ระบบต้องปฏิเสธการอนุมัติ
- 5.5 หาก BaseVersion เปลี่ยนก่อน activation ระบบต้องปฏิเสธการใช้ configuration เก่าทับค่าใหม่
- 5.6 ระบบต้องเก็บ secret material ใน protected store โดย API อ่านคืนได้เพียงข้อมูลปกปิด
- 5.7 เมื่อเริ่ม Transaction ระบบต้องตรึง ProviderAccount, environment และ credential/configuration provenance ที่ใช้
- 5.8 เมื่อ emergency disable ระบบต้องหยุดการเริ่มจ่ายใหม่โดยคงทางตรวจรายการเดิม
- 5.9 ระบบต้องเปิดช่องทางเฉพาะ intersection ของ business policy, Merchant, ผู้สร้าง, adapter capability และ amount/currency ที่ใช้ได้
- 5.10 หากยังไม่มีหลักฐาน provider contract ของช่องทาง ระบบต้องไม่แสดงช่องทางนั้นว่าเปิดใช้งานจริง

## REQ-6: Order และยอดเงิน

**ความต้องการของผู้ใช้:** ในฐานะผู้สร้างคำสั่งซื้อ ฉันต้องบันทึกธุรกิจหลายรูปแบบด้วย Order contract เดียวและยอดที่ตรวจสอบได้

**เกณฑ์การยอมรับ:**

- 6.1 ระบบต้องสร้าง Order พร้อมอย่างน้อยหนึ่ง OrderItem ภายใต้ Merchant และ Currency เดียว
- 6.2 ระบบต้องบันทึก CreatedByAccountId จากผู้เรียกที่ตรวจแล้ว
- 6.3 เมื่อ Agent สร้าง Order ระบบต้อง derive OwnerSaleId จาก Agent แทนการเชื่อค่า owner ใน request
- 6.4 เมื่อ Employee/SYSTEM สร้างแทน ระบบต้องตรวจสิทธิ์ต่อ OwnerSale/Branch ที่ระบุ
- 6.5 เมื่อ Business Handler อนุญาตรายการระดับ Merchant ระบบต้องรองรับ OwnerSale/Branch ว่างโดยไม่สร้าง Sale ปลอม
- 6.6 ระบบต้องคำนวณ TotalAmount จาก SUM(LineAmount) ลบ OrderDiscountAmount บวก OrderChargeAmount ตาม Money contract
- 6.7 หากราคาไม่มี trusted source หรือยอด/สกุลเงินไม่ผ่านกติกา ระบบต้องปฏิเสธ Order
- 6.8 เมื่อ issueNow เป็น false ระบบต้องสร้าง DRAFT โดยไม่ออก PaymentLink
- 6.9 เมื่อ create พร้อม issueNow หรือ issue ร่าง ระบบต้องใช้คำสั่ง issue กลางที่ตรึงข้อมูลและออกลิงก์แรกอย่าง atomic
- 6.10 หาก Order ไม่ใช่ DRAFT ระบบต้องไม่แก้ items/ยอด/ownership ผ่าน PATCH
- 6.11 เมื่อ cancel ตาม state matrix ใน design ระบบต้องปิดธุรกิจและสิทธิ์เริ่มจ่ายโดยไม่แปลว่าเป็น refund
- 6.12 ระบบต้องแยก OrderStatus ออกจาก PaymentStatus โดยไม่มี Payment entity/table

## REQ-7: Checkout และ capability

**ความต้องการของผู้ใช้:** ในฐานะลูกค้า ฉันต้องชำระรายการที่เห็นได้อย่างถูกต้องโดยไม่สมัครบัญชี

**เกณฑ์การยอมรับ:**

- 7.1 ระบบต้องผูก PaymentLink กับ Order โดยตรงและเก็บ token เป็น hash
- 7.2 เมื่อออกลิงก์ใหม่ ระบบต้องเพิกถอนลิงก์ active เดิมโดยไม่สร้าง Order หรือ Transaction ใหม่
- 7.3 เมื่อ replay คำสั่งออกลิงก์เดิม ระบบต้องคืนผลเดิมได้เฉพาะจากผลที่ป้องกันความลับและยังไม่หมดอายุ
- 7.4 เมื่อแลก token ระบบต้องให้ capability อายุจำกัดของ Order เดียวโดยไม่ออก Business JWT
- 7.5 เมื่ออ่าน summary ระบบต้องคืนเฉพาะข้อมูลลูกค้าที่อนุญาต ไม่คืน raw OrderSnapshot
- 7.6 เมื่อ confirm ระบบต้องตรวจ capability, OrderId, Version และสถานะลิงก์อีกครั้ง
- 7.7 หาก cookie/context ของอีกแท็บไม่ตรง Order ที่ผู้ใช้ยืนยัน ระบบต้องปฏิเสธแทนการจ่าย Order อีกใบ
- 7.8 เมื่อมี Transaction ที่ยังอาจเก็บเงิน ระบบต้องกลับไปหรือตรวจรอบเดิมแทนเริ่มรอบใหม่
- 7.9 หากติดต่อ PSP แล้วผลกำกวม ระบบต้องคืน pending response โดยไม่เปิดการจ่ายซ้ำอัตโนมัติ
- 7.10 เมื่อลูกค้ากลับจาก PSP หลังลิงก์ถูกปิด ระบบต้องให้เพียง status-only context ของ Transaction เดิม

## REQ-8: Transaction และผลเงินจริง

**ความต้องการของผู้ใช้:** ในฐานะทีมการชำระ ฉันต้องทราบผลแต่ละครั้งโดยไม่ทำเงินซ้ำหรือทิ้งหลักฐานที่มาช้า

**เกณฑ์การยอมรับ:**

- 8.1 เมื่อเริ่มจ่าย ระบบต้องบันทึก Transaction และ immutable OrderSnapshot ก่อนเรียก PSP
- 8.2 ระบบต้องจำกัดหนึ่ง potentially-chargeable Transaction ต่อ Order ด้วย concurrency guard ที่คำสั่งทุกทางใช้ร่วมกัน
- 8.3 เมื่อ retry ทางเครือข่าย ระบบต้องใช้ provider request reference เดิมของ Transaction
- 8.4 ระบบต้องตรวจ Merchant/ProviderAccount/environment/reference/amount/currency ก่อนยืนยัน SUCCEEDED
- 8.5 หากการตรวจยังไม่ยืนยันผล ระบบต้องคง PENDING_CONFIRMATION โดยไม่เปลี่ยน provider
- 8.6 เมื่อยืนยันสำเร็จปกติ ระบบต้อง commit Transaction, Order.PaymentStatus, SuccessfulTransactionId และ outbox สอดคล้องกัน
- 8.7 เมื่อรับผลซ้ำหรือ out-of-order ระบบต้องไม่ทำผลธุรกิจสำเร็จซ้ำ
- 8.8 เมื่อผลสำเร็จมาหลัง Order ถูกยกเลิก ระบบต้องเก็บผลเงินพร้อม NeedsReview โดยไม่เปิด Order กลับ
- 8.9 เมื่อเกิดเงินสำเร็จซ้ำจริง ระบบต้องเก็บ Transaction ที่สำเร็จทั้งสองตามจริง
- 8.10 หากยอดหรือสกุลเงินที่ PSP แจ้งไม่ตรง ระบบต้องบันทึกหลักฐานและส่งตรวจโดยไม่ตั้ง Order เป็น PAID
- 8.11 ระบบต้องติดตามรายการกำกวมจาก backend แม้ลูกค้าปิด browser
- 8.12 เมื่อคีย์เดิมใช้ตรวจไม่ได้ ระบบต้องใช้เฉพาะทางกู้คืนที่ provider contract อนุญาตหรือส่งตรวจ โดยไม่เปลี่ยน Merchant/PSP account ของรายการ

## REQ-9: Notification และการดูแลรายการค้าง

**ความต้องการของผู้ใช้:** ในฐานะผู้สมัครและทีมธุรกิจ ฉันต้องได้รับผลที่ถูกต้อง และทีมดูแลต้องส่งซ้ำได้โดยไม่แก้ประวัติ

**เกณฑ์การยอมรับ:**

- 9.1 เมื่อบันทึกผลสมัคร ระบบต้องสร้างงาน Email และ SMS แยกกันจาก event เดียว
- 9.2 หากช่องทางใดส่งล้มเหลว ระบบต้องไม่ย้อนผลสมัครหรือผลชำระ
- 9.3 เมื่อ retry delivery ระบบต้องคง recipient และ template snapshot เดิม
- 9.4 ระบบต้องแยก ACCEPTED ออกจาก DELIVERED ตามหลักฐานของผู้ให้บริการ
- 9.5 เมื่อ provider อาจรับงานแล้วแต่คำตอบหาย ระบบต้องบันทึก UNKNOWN และใช้วิธีตรวจ/retry ตาม contract
- 9.6 เมื่อแจ้งธุรกิจ ระบบต้องส่ง signed event ไปยัง endpoint ที่อนุญาตของ Merchant
- 9.7 หาก URL ปลายทางไม่ผ่าน allowlist/SSRF policy ระบบต้องไม่ส่ง request
- 9.8 เมื่อ replay outbox/inbox ระบบต้องไม่สร้าง logical delivery ซ้ำสำหรับ event/channel/recipient เดิม
- 9.9 ระบบต้องให้เจ้าหน้าที่ค้นงานค้างจาก OrderNo, TransactionNo หรือ CorrelationId และบันทึก review note ได้โดยไม่ force-paid
- 9.10 เมื่อเปลี่ยน template ใน release ระบบต้องไม่เปลี่ยน template version ของ delivery ที่สร้างแล้ว

## REQ-10: HTTP contracts และการปฏิบัติงาน

**ความต้องการของผู้ใช้:** ในฐานะทีม client และผู้ดูแล ฉันต้องมีสัญญา API ชุดเดียวที่ไม่ต้องเดาพฤติกรรม

**เกณฑ์การยอมรับ:**

- 10.1 ระบบต้องเปิด method/path ใน scope v1 ตาม api-scope.json และไม่เปิด deferred endpoints เป็นฟีเจอร์ใหม่ของรุ่นแรก
- 10.2 หากใช้ Idempotency-Key เดิมกับ payload ต่างใน caller/Merchant/operation เดียว ระบบต้องตอบ conflict
- 10.3 หากแก้ resource ด้วยเวอร์ชันเก่า ระบบต้องตอบ precondition failed โดยไม่เขียนทับ
- 10.4 ระบบต้องให้ GET ธุรกิจสำหรับ status/read ไม่มี PSP network side effect
- 10.5 ระบบต้องตรวจ callback ตาม provider policy โดยไม่ใช้ human JWT เป็นหลักฐานความแท้
- 10.6 ระบบต้องใช้ Problem Details/stable code สำหรับ business errors และ OAuth error contract สำหรับ protocol endpoints
- 10.7 ระบบต้องมี audit ของ privileged mutations และ correlation ที่ไม่บันทึก token, secret หรือ PII ใน application log
- 10.8 ระบบต้องมี readiness/liveness ที่แยกสถานะ process ออกจากความพร้อม dependency โดยไม่เผย credential
- 10.9 เมื่อเปลี่ยน API contract ระบบต้องไม่เปิดสอง semantics ที่ method/path เดียวกันให้ client เดาเอง

## REQ-11: Migration, rollback และการถอดของเก่า

**ความต้องการของผู้ใช้:** ในฐานะเจ้าของระบบ ฉันต้องย้ายได้โดยรักษาข้อมูลและสามารถกลับทางได้อย่างตรวจสอบได้

**เกณฑ์การยอมรับ:**

- 11.1 เมื่อเตรียมลบ source ระบบต้องมี inventory ครอบคลุม callers, DI/reflection/generated wiring, config/jobs และข้อมูลค้างที่เกี่ยวข้อง
- 11.2 ระบบต้องเก็บ mapping ของ legacy identities/IDs โดยไม่เดา identity หรือ Merchant จาก email/ชื่อที่แสดง
- 11.3 เมื่อ backfill ระบบต้องรักษา OrderId, PSP references, ยอดต่อ currency และประวัติที่มีหลักฐาน
- 11.4 หากข้อมูลสำหรับ mapping ไม่ครบหรือขัดกัน ระบบต้องรายงานข้อขัดแย้งและหยุด cutover ที่เกี่ยวข้องแทนสร้างข้อมูลปลอม
- 11.5 เมื่อย้าย Pending/Rejected user ระบบต้องย้ายเป็น Registration แทนสร้าง Active Account
- 11.6 เมื่อย้าย Session ระบบต้องสร้างตัวแทนประวัติเป็น Transaction โดยไม่เริ่มชำระหรือส่ง business event ใหม่
- 11.7 เมื่อประกอบ snapshot ตอนย้าย ระบบต้องระบุ migration provenance
- 11.8 ขณะที่ดำเนิน cutover ระบบต้องมีผู้เริ่ม charge ได้เพียงชุดเดียวและคงทางรับ/กู้คืนผล PSP ค้าง
- 11.9 เมื่อ rollback ระบบต้องรักษาผลเงินจริงและ events ที่เกิดหลัง cutover
- 11.10 เมื่อเตรียมถอด legacy adapter/store ระบบต้องผ่านการตรวจว่าไม่มี consumer หรือ callback ที่ยังต้องใช้
- 11.11 ระบบต้องไม่เปลี่ยน production หรือ reset ฐานข้อมูลจากการรัน test/spec tools โดยปริยาย

## REQ-12: เกณฑ์ส่งมอบและความพร้อมภายนอก

**ความต้องการของผู้ใช้:** ในฐานะทีมประสบการณ์น้อย ฉันต้องมีวิธีตรวจและเดินระบบที่ทำตามได้โดยไม่ตีความว่า mock ผ่านคือพร้อม production

**เกณฑ์การยอมรับ:**

- 12.1 ระบบต้องมีคำสั่ง build, unit, architecture และ integration tests ที่ทำซ้ำได้ตาม tasks.md
- 12.2 ระบบต้องมี integration cases สำหรับ cross-Merchant access, race, replay, timeout และ late/duplicate success
- 12.3 หากยังขาดหลักฐาน Entra/provider/vendor จริง ระบบต้องระบุข้อจำกัดและไม่เปิด capability นั้นใน live configuration
- 12.4 ระบบต้องมีคู่มือ start, health, log lookup, ตรวจรายการค้าง, controlled retry และ rollback ชุดเดียว
- 12.5 เมื่อจบแต่ละ task ระบบต้องบันทึกผลที่รันจริงและความคลาดเคลื่อนก่อนทำ checkpoint
- 12.6 หากยังมี regression ที่ไม่ได้จำแนก ระบบต้องไม่ประกาศ cutover-ready

## Edge Cases & Open Questions

ไม่มีการให้ผู้ implement เลือก product semantics เอง: state matrix, token defaults, contact verification รุ่นแรก, scope และ API dispositions อยู่ใน design/API contract ส่วน credential, issuer URLs, master-data mappings และการอนุมัติ production เป็น input ของสภาพแวดล้อม ต้องตรวจใน preflight/cutover checklist ไม่ใช่การอนุญาตจาก spec นี้

ผู้ใช้อนุมัติ spec รุ่นแรกวันที่ 9 กันยายน 2026 และระบุว่ายังไม่ให้เริ่ม implementation จึงบันทึก approval ของเอกสารเท่านั้น ทุก task ยังไม่เริ่ม
