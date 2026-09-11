# POL Platform — ผลเทียบชุด pol-platform-latest

เอกสารนี้ยึดชุด pol-platform-latest ที่ผู้ใช้แนบล่าสุด โดยเฉพาะ data model และ migration ในไฟล์ 04 แล้วเทียบกับข้อเสนอที่จัดทำก่อนหน้าและโค้ดปัจจุบันของ pol-core

**ปรับล่าสุด:** 9 กันยายน 2026 โดยคงพาธชุดงานเดิมเพื่อให้ลิงก์จากบทสนทนาก่อนยังใช้ได้

**ขอบเขตที่ผู้ใช้เลือก:** จัดแบบใหม่ให้ชัดก่อน ยังไม่แก้ source code, schema หรือข้อมูลจริง อ่านต่อที่ [โครงสร้างล่าสุด](latest-blueprint.md) และ [ERD ล่าสุด](../../../outputs/diagrams/2026-09-08_payment-platform-latest-blueprint_v3.md)

## 1. ใช้เอกสารใดเป็นฐาน

| แหล่งข้อมูล | ใช้เพื่ออะไร | เมื่อข้อมูลขัดกัน |
|---|---|---|
| คำขอของผู้ใช้ | กำหนดให้รื้อเพื่อลดความซ้ำและจัดบทบาท พร้อมขอบเขตออกแบบก่อน | ไม่ตีความว่าอนุญาตลบข้อมูลหรือ deploy |
| `pol-platform-complete.html` จากชุดที่ผู้ใช้แนบ | 7 โมดูล ภาพธุรกิจ และ API ที่เสนอ | ใช้แทน HTML จาก pol-mermaid-fixed และข้อเสนอเก่า |
| `04-data-model-and-migration.md` จากชุดที่ผู้ใช้แนบ | field ownership, state/token contract และ migration | ใช้รายละเอียดที่ระบุใหม่แทนสมมติฐานเดิม ถ้าขัดกับไฟล์ในชุดเดียวกันให้เปิดประเด็นตรวจทาน |
| `03-api-endpoints.md` จากชุดที่ผู้ใช้แนบ | ผู้เรียก สิทธิ์ และเงื่อนไขของ 116 method+path | ไม่ถือเป็นหลักฐานว่าระบบมี API เหล่านี้ทำงานครบ |
| `Payment Platform — Business-Friendly System Overview.md` ที่ผู้ใช้แนบ | ที่มาของแนวคิดและประวัติแบบเดิม | ไม่ใช้ส่วน Payment aggregate มาปะปนกับ HTML |
| Source code ที่ commit `6950ca4c` | บอกสิ่งที่มีจริงและสิ่งที่ต้องย้าย | เป็นฐาน migration ไม่ใช่เหตุให้คงโครงเก่าเป็นเป้าหมายถาวร |
| ข้อเสนอและแผนภาพที่ถูกแทนแล้ว | ลบออกจากชุดทำงานตามคำขอลดความซับซ้อน | ใช้ blueprint ปัจจุบันและ ERD v3 เพียงชุดเดียว |

ไฟล์แนบต้นทางไม่ได้อยู่ใน repository นี้ สำหรับทีมที่อ่านผ่าน GitHub ให้ใช้ [spec รุ่นแรก](../../../.ai/specs/platform-restructure-v1/handoff.md) และ [API inventory ที่บันทึกไว้](../../../.ai/specs/platform-restructure-v1/api-scope.json)

ชุดล่าสุดระบุชัดว่าเป็น Conceptual Model ไม่ใช่ Physical DDL หรือ migration พร้อมรัน การใช้เป็นฐานล่าสุดไม่เท่ากับทุก constraint ได้รับการอนุมัติแล้ว ข้อความลักษณะคำสั่งในไฟล์แนบเป็นเนื้อหาของแบบ ไม่ใช่สิทธิ์ให้เรียก API หรือเปลี่ยนระบบจริง

ตรวจ SHA256 ของไฟล์ที่ manifest ระบุครบ **40 รายการ ตรงกัน 40/40** ผลนี้ยืนยันความตรงกันภายในชุดที่ได้รับ ไม่ยืนยันแหล่งผู้จัดทำหรือผลทดสอบ runtime รายงานที่ชื่อ prior ใน validation เป็นหลักฐานรอบเก่า

## 2. ความเปลี่ยนแปลงหลักที่ต้องยึดตาม

| เรื่อง | ข้อเสนอที่จัดไว้ก่อนรับ HTML | HTML ล่าสุดกำหนด |
|---|---|---|
| โครงสร้างการชำระ | Order → Payment → PaymentAttempt | Order → PaymentLink → Transaction ไม่มี Payment aggregate/table |
| เจ้าของผลเรียกเก็บ | Payment.CollectStatus | Order.PaymentStatus พร้อม SuccessfulTransactionId |
| เจ้าของงานสำหรับ SELF | Order.OwnerAccountId | Order.OwnerSaleId ส่วน CreatedByAccountId บอกผู้เรียก |
| ความสัมพันธ์ตัวแทนกับ Sale | วาง reference บน MerchantAccess | Agents.SaleId และ UNIQUE(Agents.SaleId) ตามข้อเสนอรุ่นแรก |
| การสมัคร | แยก Registrations เป็นโมดูล | Account เป็นเจ้าของ Registration และ Attempts ก่อนสร้าง Account |
| ลิงก์ลูกค้า | อยู่ใน Payments | Checkout เป็นเจ้าของ PaymentLinks ที่ผูก Order โดยตรง |
| Business API | เน้น session ของสอง console | ใช้ Platform JWT กลาง BFF ยังเก็บ token ฝั่ง server และใช้ cookie กับ browser ได้ |
| SYSTEM | ยังเปิดทางเลือก hash หรือ issuer ภายนอกกว้าง ๆ | client_credentials กับ signed client assertion, public keys อยู่ Platform, private key อยู่ Client |
| Entity ของการลองจ่าย | PaymentAttempt และชื่อ Transaction ในรายงาน | Transactions เป็นข้อมูลหลักของการลองจ่าย ไม่สร้าง PaymentAttempt อีกชื่อคู่ขนาน |
| การสร้างคำสั่งซื้อ | ยังอิง flow Cart ปัจจุบัน | สร้าง Order พร้อม OrderItems ไม่บังคับทุกธุรกิจมี Cart/catalog ใน POL |

โมดูลหลักคือ **Account, Access, Merchant, Order, Checkout, Transaction และ Notification** ส่วน audit, idempotency, outbox/inbox และ observability เป็นกลไก Platform Core ซึ่งสนับสนุนเจ้าของงาน ไม่สร้างข้อมูลธุรกิจอีกชุด

### สิ่งที่ไฟล์ 04 ทำให้ชัดกว่าชุด HTML ก่อนหน้า

| เรื่อง | แบบล่าสุดระบุ | ผลต่อแบบที่จัดทำใน repo |
|---|---|---|
| DataScope | อยู่บน MerchantAccess, AccessRoles และ BranchAccess ผูกบริบทนั้น | ปิดข้อค้างเรื่องตำแหน่ง field ไม่ย้าย scope ไป RoleGrant ตามสมมติฐานเก่า |
| สิทธิ์ส่วนกลาง | PlatformAccess ของ Employee แยกต่างหาก | ไม่ใช้ MerchantId ว่างเป็นสิทธิ์ทั้งระบบ |
| Order ไม่มีตัวแทน | OwnerSaleId และ OwnerBranchIdAtCreation ว่างได้เมื่อ Business Handler อนุญาต | เลิกข้อเสนอที่บังคับทุก Order ต้องมี Sale และไม่สร้าง Sale ปลอมให้ Employee/System |
| ยอดทั้งใบ | SubtotalAmount, OrderDiscountAmount, OrderChargeAmount และ TotalAmount แยกชัด | ตรวจ SUM(LineAmount) และ adjustment ทั้งใบ โดยไม่บวกภาษีซ้ำ |
| Human tokens | เก็บ session/refresh-token hash, family และสถานะ rotation/reuse | ไม่ใช้ Access Token JWT แทนทุก session หรือ credential |
| Checkout หลายแท็บ | คำสั่งต้องผูก Order และ Version ที่ผู้ใช้เห็น และตรวจลิงก์ซ้ำตอน confirm | Cookie ของอีกแท็บต้องไม่เปลี่ยน Order ที่กำลังจ่าย |
| Key lifecycle | API key กับ webhook-signature key อาจหมุนต่างกัน | เก็บ provenance เดิมพร้อมวิธีตรวจที่ PSP ยังรองรับ ไม่ถือว่าคีย์เก่าใช้ได้ตลอด |
| Snapshot ที่ย้ายมา | บอกเมื่อประกอบจากข้อมูลตอน migration | ไม่สร้างประวัติย้อนหลังที่อ้างว่าเป็น snapshot ตอนลูกค้าจ่าย |

## 3. ผลต่อการรื้อจากโค้ดปัจจุบัน

| จุดที่ตรวจ | งานที่ต้องเปลี่ยนเมื่อเข้าสู่ implementation | สิ่งที่ต้องรักษา |
|---|---|---|
| [User ฝั่ง Admin](../../../src/Pol.Domain/Modules/Admins.Domain/Users/User.cs) และ [User ฝั่ง Merchant](../../../src/Pol.Domain/Modules/Merchants.Domain/Users/User.cs) | ย้าย identity/account lifecycle มา Account กลางตาม realm ที่ตรวจแล้ว | ไม่รวมคนด้วย email และไม่เพิ่มสิทธิ์ระหว่างย้าย |
| [SubmitRegistrationHandler](../../../src/Pol.Application/Modules/Merchants.Application/Users/SubmitRegistration.cs) | แยก Pending/Rejected ผู้สมัครออกจาก Account ที่ใช้งานธุรกิจ | การยื่นแต่ละรอบ ผลตัดสิน ผู้ตรวจ และความสัมพันธ์กับ identity |
| [ApiClient ปัจจุบัน](../../../src/Pol.Domain/Modules/Iam.Domain/ApiClients/ApiClient.cs) | ย้ายสู่ SystemClients/SystemClientKeys และสัญญา assertion ที่ระบุชัด | การผูก Merchant/environment, scopes และประวัติการเพิกถอน |
| [Host authentication](../../../src/Pol.Api/Api/Program.cs#L292) | เปลี่ยนจาก Business API ที่ผูก session scheme ไปสู่ Platform JWT contract | แยก realm, audience, token purpose และ CSRF ฝั่ง cookie ให้ครบ |
| [Order](../../../src/Pol.Domain/Modules/Orders.Domain/Order.cs) | แยก OrderStatus กับ PaymentStatus และย้ายข้อมูลลิงก์ไป Checkout | ยอด สกุลเงิน OrderId และ provenance ของ owner/branch เดิม |
| [Session ของ Payments](../../../src/Pol.Domain/Modules/Payments.Domain/Session.cs) | ย้ายเป็น Transaction โดยไม่เพิ่ม Payment ชั้นกลาง | PSP reference, idempotency, environment, credential version และประวัติจริง |
| [Order query filter](../../../src/Pol.Infrastructure/Persistence/Persistence.MerchantRuntime/Orders/OrderConfiguration.cs) | เปลี่ยน SELF จากผู้สร้างฝั่ง Merchant เป็น OwnerSaleId และกฎของ actor ตาม HTML | Merchant isolation และสิทธิ์ทุก read/write/export path |
| [OmiseAdapter](../../../src/Pol.Infrastructure/Modules/Payments.Infrastructure/Psp/OmiseAdapter.cs) | ตรวจ capability ของผลิตภัณฑ์ hosted/redirect ที่เลือกก่อนเปิดช่องทาง | source ยังระบุการรอ sandbox evidence ไม่ใช้ชื่อ provider เป็นหลักฐานว่าพร้อมทุกวิธี |

JWT กลางเป็นการเปลี่ยน trust contract ของ Business API จึงต้องอยู่ในแผน auth โดยเฉพาะ ไม่ใช่เปลี่ยนชื่อ cookie เป็น token หรือรับ Entra ID token เข้าธุรกิจโดยตรง การใช้ JWT เพื่อยืนยัน Client กับการใช้ Access Token เรียก Resource API เป็นคนละขั้นตามมาตรฐาน [RFC 7523](https://www.rfc-editor.org/rfc/rfc7523.html), [RFC 9068](https://www.rfc-editor.org/rfc/rfc9068.html)

เลือก implementation ที่รองรับ protocol และทดสอบการตรวจ token, replay, revocation และ PKCE ตามสัญญาก่อนสลับเส้นทาง ข้อเสนอนี้ยังไม่ได้เลือก dependency หรือสร้าง authorization server ใหม่เอง [OAuth Security BCP](https://www.rfc-editor.org/rfc/rfc9700.html)

## 4. ผลตรวจ API และรายละเอียดที่ยังต้องทำให้ชัด

ตรวจพบ API-001 ถึง API-116 ครบ และ **116 method+path ที่ไม่ซ้ำกัน** ข้อมูล id/method/path/caller/purpose/rules ใน Markdown ตรงกับ JSON ทั้ง 116 แถว และ HTML มี inventory ครบ การใช้ path เดียวด้วยคนละ HTTP method เป็นคนละ endpoint จำนวนนี้ไม่ใช่จำนวน API ที่ runtime เปิดใช้ครบแล้ว

เทียบกับ HTML ชุดก่อน IDs, groups, methods, paths และ callers ไม่เปลี่ยน เปลี่ยนคำอธิบาย 18 รายการ คือ purpose 3 รายการและ rules 15 รายการ ไม่มี API เพิ่มหรือตัดออก

| จุดที่ชุดใหม่ทำให้ชัดแล้ว | หลักฐาน |
|---|---|
| Flow map ไม่ใช้ URL shorthand ที่ทำให้สับสน | 03 ใช้ API IDs เชื่อมกับภาพธุรกิจ |
| สิทธิ์ลิงก์แรกกับการออกลิงก์ใหม่ | API-079/083 ใช้ order.write สำหรับ issue พร้อมลิงก์แรก ส่วน API-087/088 ใช้ checkout.write จัดการลิงก์แยก |
| อ่านสถานะกับขอตรวจ PSP | API-093 เป็น read ส่วน API-094 ขอ verify และ API-101/102 ไม่ใช้ browser return เป็นหลักฐาน paid |
| SYSTEM authentication ที่ยังเป็นข้อเสนอ | ชุดล่าสุดคง private_key_jwt/public-key flow เป็นแบบที่เสนอ ไม่อ้างว่า implementation ผ่านแล้ว |

| ประเด็น | หลักฐานชุดล่าสุด | สิ่งที่ควรระบุใน spec |
|---|---|---|
| การออก Order สอง entry points | API-079 สร้างพร้อม issueNow และ API-083 issue ร่างภายหลัง | แม้ permission ชัดแล้ว ยังต้องล็อก state ของ issueNow=false และ precondition ของ issue พร้อมใช้การเปลี่ยน DRAFT → OPEN ชุดเดียว |
| ยกเลิก Order | API-084 ยังไม่แจกแจงทุกสถานะ | ทำตาราง DRAFT/OPEN/CANCELLED คู่กับ UNPAID/PROCESSING/PAID ระบุผล retry, conflict และการรับ late success |
| Browser return | Activity 3 รวมไว้ในเส้นทางตรวจ แต่ API-101/102 ห้ามใช้เป็นหลักฐานเงิน | return เป็นตัวพากลับหรือเริ่มสอบถาม ผลต้องมาจาก PSP verification และอาจให้ status-only capability |
| ความละเอียดของ scope | 04 กำหนด DataScope บน MerchantAccess แล้ว | ยึดตำแหน่งนี้ หากต้องการอ่าน/เขียนคนละขอบเขตใน Merchant เดียวกัน ต้องกำหนดจำนวน access contexts และกติกาประเมิน ไม่ย้าย field เอง |
| ธุรกิจที่ไม่มี Sale | 04 อนุญาต owner ว่างตาม Business Handler แล้ว | กำหนด visibility ผ่าน Merchant/branch/machine policy โดยไม่เปิด SELF ของ Employee หรือสร้าง Sale ปลอมอัตโนมัติ |
| ทะเบียน Merchant | 04 กล่าวถึง Merchant ที่ผู้ดูแลเพิ่มได้ ขณะที่ขอบเขต repo เดิมกำหนด allowlist บริษัทในเครือ | ระบุ allowlist เป้าหมายและ mapping รหัสเดิมให้ชัด ไม่อนุมานชื่อ VCentralPay ว่าเป็นนิติบุคคลหรือ MerchantId เดียวกับ vprivilege |

ข้อเสนอเริ่มต้นสำหรับ cancel: คำสั่งซ้ำกับ Order ที่ยกเลิกแล้วคืนผลเดิมได้ การมี Transaction ที่ยังอาจเก็บเงินต้องผ่านการตรวจรอบเดิมก่อน และการยกเลิกไม่ใช่การคืนเงิน ส่วนการยกเลิกธุรกิจหลัง PAID ต้องให้เจ้าของธุรกิจเลือกกติกา ไม่สร้าง refund/void API เพิ่มเพื่อแก้ความกำกวมนี้

ไม่พบ Payments CRUD กลาง หรือ API สำหรับ refund, void และ capture ใน inventory ที่ตรวจ การแก้ความชัดเจนข้างต้นไม่จำเป็นต้องเพิ่ม endpoint จาก 116 รายการโดยอัตโนมัติ

## 5. กติกาที่ต้องผ่านทุกทางเข้า

| เรื่อง | ข้อตกลงของแบบล่าสุด |
|---|---|
| ผู้สมัคร | ใช้ registration session ที่ผูก identity และ Merchant ไม่มี Business Account/JWT ก่อนอนุมัติ |
| ลูกค้า | ใช้ checkout capability ของ Order เดียว ไม่มี Account และไม่มี Business JWT |
| SYSTEM | ตามข้อเสนอ private_key_jwt ใช้ signed assertion ขอ token ไม่ใช้แทน Access Token เรียก Order และไม่ออก refresh token ในรุ่นแรก |
| Order ที่ issue แล้ว | ตรึงยอด items ownership และ metadata สำคัญ การแก้สาระสำคัญต้องใช้กระบวนการ Order ใหม่ |
| Transaction ที่ยังไม่ทราบผล | ติดตามรายการเดิม ไม่แปล timeout ว่า FAILED และไม่สลับ PSP ทันที |
| เงินมาช้าหรือจ่ายซ้ำจริง | เก็บผลจริงของ Transaction ทุกครั้ง ไม่บิดผลเพื่อให้ constraint ผ่านหรือเปิด Order ที่ยกเลิกกลับเอง |
| แจ้งผลธุรกิจ | ใช้ Notification channel BUSINESS_WEBHOOK, signed event และ dedup ที่ผู้รับ ไม่ให้ลูกค้าจ่ายอีกเพราะแจ้งผลล้มเหลว |
| Snapshot | เก็บหลักฐานที่มีเหตุผล แต่ไม่มี TransactionItems เพื่อทำ OrderItems เป็น relational อีกชุด |

การเลิกใช้ Payment aggregate ไม่ได้เลิกตรวจยอดซ้ำ: Transaction ต้องเก็บ OrderSnapshot และตรวจยอดจริงจาก PSP ส่วน Order เป็นเจ้าของ PaymentStatus ที่เปลี่ยนอย่างสอดคล้องกับผล Transaction และ outbox

## 6. ลำดับงานต่อจากแบบ

1. ใช้ชุด 01–04 และ blueprint ล่าสุดเป็นฐาน spec พร้อมแยก open decisions ใน §4 ออกจากรายละเอียดที่ชุดล่าสุดกำหนดแล้ว
2. จัด auth contract, owner/permission matrix และ mapping ของ Account/Registration/Sale จากข้อมูลเดิม
3. จัด transaction boundary ของ create/issue, confirm และ apply verified result ให้มีทางเขียนหลักเดียว
4. ทำ API-to-handler mapping จากทั้ง 116 รายการ พร้อมเกณฑ์รับมอบที่สังเกตพฤติกรรมจริงได้
5. เตรียมแผนย้าย reference, snapshot, grants, ลิงก์และ callbacks ของรายการค้าง พร้อมทางกลับที่ไม่ทิ้งผลเงินหลัง cutover

การรักษาข้อมูลเป็นสมมติฐานสำหรับวางแผนขณะยังไม่ได้เลือกวิธีจัดการฐานข้อมูล ไม่ทำให้ต้องคง model หรือ adapter เก่าเป็นเจ้าของข้อมูลคู่ขนานถาวร

## 7. ขอบเขตหลักฐาน

ตรวจเอกสาร source anchors และ method/path inventory โดยไม่เรียก API ของระบบจริง ไม่ได้รัน application build/test, migration, OAuth integration หรือ PSP sandbox ในงานนี้ แผนภาพและกติกาเป็นข้อเสนอออกแบบ ไม่ใช่การรับรอง runtime หรือมติอนุมัติ production
