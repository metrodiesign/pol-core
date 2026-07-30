# คู่มือ 6 Layer หลักของ pol-core — คืออะไร บทบาทอะไร ทำงานยังไง

> เอกสารนี้อธิบาย **ทำไม** แต่ละ top-level folder ใต้ `src/` ถึงมีอยู่ และ **ทำงานยังไง** ในเชิง narrative — เขียนสำหรับคนที่กำลังทำความเข้าใจระบบครั้งแรก ไม่ใช่ file-by-file reference.
>
> ถ้าต้องการรู้ว่าไฟล์ไหนอยู่ตรงไหน อ่าน [`src-structure.md`](src-structure.md) (ground truth = ไฟล์จริง, สรุปเป็นตารางต่อไฟล์).
> ถ้าต้องการรายละเอียดเชิงธุรกิจของแต่ละโมดูล อ่าน [`platform-modules.md`](platform-modules.md).
> ถ้าต้องการกลไก isolation floor เต็ม (query filter/write guard/escape-hatch) อ่าน [`db-connection-and-rls.md`](db-connection-and-rls.md).
> canonical target architecture: [`.ai/shared/ARCHITECTURE.md`](../../.ai/shared/ARCHITECTURE.md).

## สรุปด่วน: 6 layer ในบรรทัดเดียว

อ่านตารางนี้ก่อนถ้าต้องการสแกนเร็ว — รายละเอียดเต็มอยู่ใน §1-§6 ด้านล่าง

| Layer | หน้าที่ 1 บรรทัด | ถ้าไม่มีชั้นนี้ จะพังแบบไหน |
|---|---|---|
| 1. SharedKernel | นิยาม `Money`/`Entity` กลางที่ทุกโมดูลต้องใช้เหมือนกัน | แต่ละโมดูลคิดเลขเงินเอง ปัดทศนิยมไม่ตรงกัน เงินหมุนข้ามโมดูลผิดจำนวน |
| 2. Contracts | event กลางที่โมดูลใช้บอกกันว่า "เกิดอะไรขึ้น" โดยไม่ต้องรู้จักกันตรง ๆ | โมดูลต้อง reference กันตรง ๆ เพื่อคุยกัน ผูกกันแน่นจนแก้โมดูลหนึ่งกระทบอีกโมดูลทันที |
| 3. BuildingBlocks | เช็ค actor/merchant ก่อนทุก request + guard การเขียนก่อนทุก commit | ไม่มีจุดกลางเช็คสิทธิ์ ต้องเขียน auth check ซ้ำทุก handler เสี่ยงลืมจุดใดจุดหนึ่ง |
| 4. Persistence | isolation floor จริง (query filter + write guard) หลัง RLS ถูกถอด | merchant หนึ่งอาจอ่าน/เขียนข้อมูลของอีก merchant ได้ตรง ๆ ผ่าน SQL |
| 5. Modules | business logic ของแต่ละโดเมน แยก isolate กันด้วย published language | logic ธุรกิจกระจัดกระจายปนกับ infra เปลี่ยนกฎธุรกิจจุดเดียวกระทบทั้งระบบ |
| 6. Hosts | composition root จุดเดียวที่ผูกทุกอย่างเข้าด้วยกันแล้วรันจริง | ไม่มีจุดผูก policy จริง (เช่น `IWriteAuthorizer`) ระบบ boot ไม่ได้เลย |

## ภาพรวม: ใครขึ้นกับใคร

pol-core เป็น **Modular Monolith** ตามแนว **Clean Architecture + CQRS** — 1 codebase, 46 `.csproj`, deploy เป็น host เดียว (`Api`). กฎ dependency ข้อเดียวที่คุมทุกอย่าง: **ลูกศรชี้เข้าหา Domain เสมอ**

```
Hosts (Api)                              composition root — ผูกทุกอย่างเข้าด้วยกัน, ลงไปได้ทุกชั้น
   ▼
Persistence.* + Infrastructure (ต่อโมดูล + BuildingBlocks)   EF context/repo/adapter จริง
   ▼
Application (ต่อโมดูล + BuildingBlocks)  command/query/handler + ประกาศ port (interface)
   ▼
Domain (ต่อโมดูล) + SharedKernel + Contracts   entity/value object/event บริสุทธิ์ ไม่มี dependency นอก
```

Domain ไม่รู้จักใครนอกจาก SharedKernel. Application รู้จัก Domain แล้วประกาศ port ให้ Infrastructure/Persistence มา implement. Host คือที่เดียวที่เห็นทั้ง interface และ concrete implementation พร้อมกัน แล้วผูกมันเข้าด้วยกันตอน boot.

6 layer ที่ตามมาเรียงจาก **ในสุด (primitive ที่ไม่พึ่งใคร) ไปนอกสุด (composition root ที่พึ่งทุกคน)** — อ่านตามลำดับนี้จะเห็นว่าแต่ละชั้นเอาชั้นก่อนหน้าไปต่อยอดยังไง

มองจากอีกมุม — "ในสุด" อยู่ตรงกลาง "นอกสุด" อยู่วงนอกสุด (ทิศทางเดียวกับลูกศรด้านบน แค่มองเป็นวงเข้าหาแกนกลาง):

```
[ 6. Hosts (Api) — composition root, เห็นทุกชั้นพร้อมกัน, ผูกทุกอย่างเข้าด้วยกันแล้วรันจริง
   [ 4. Persistence — isolation floor จริง (query filter อ่าน + write guard เขียน)
      [ 5. Modules — business logic ต่อโดเมน (Products/Carts/Checkouts/Orders/Payments/Merchants/...)
         [ 3. BuildingBlocks — actor/merchant context, mediator pipeline, port กลาง
            [ 2. Contracts — event กลางข้ามโมดูล (published language)
               [ 1. SharedKernel — Money / Entity — พึ่งใครไม่ได้เลยสักตัว ]
            ]
         ]
      ]
   ]
]
```

Persistence อยู่วงนอกของ Modules ไม่ใช่วงใน — ตรวจกับ `.csproj` จริงแล้ว: `Persistence.MerchantRuntime.csproj`
reference เข้า `Products.Domain`/`Products.Application` ฯลฯ ตรง ๆ (§4) ในขณะที่ไม่มี Module ไหน reference กลับ
เข้า `Persistence.*` เลยสักตัว — ทิศทางเดียวกับ arrow diagram ด้านบนที่วาง Persistence ไว้เหนือ Application/Domain
อยู่แล้ว. เพราะงั้นเลขหัวข้อ §4/§5 ในวงนี้จึงไม่เรียงจากในสุดไปนอกสุดแบบเป๊ะ ๆ ตามลำดับที่อ่านเอกสาร — เลขหัวข้อ
ตามลำดับการอ่าน (reading order) ส่วนตำแหน่งวงตามทิศทาง dependency จริง (dependency order) สองอย่างนี้ต่างกัน.

อ่านจากในสุด (1) ไปนอกสุด (6): วงนอกอ้างถึงวงในได้เสมอ วงในอ้างถึงวงนอกไม่ได้เลย — กฎเดียวกับ
"ลูกศรชี้เข้าหา Domain เสมอ" ด้านบน แค่เปลี่ยนมุมมองจากเส้นตรงเป็นวงซ้อน. SharedKernel วงในสุด ≠ สำคัญน้อยสุด
— คือ primitive ที่ทุกวงข้างนอกต้องพึ่ง (เป็นจุดเดียวที่ dependency ออกเป็นศูนย์จริง ๆ ดู §1)

---

## อธิบายแบบเข้าใจง่าย (ตึกให้เช่า)

เอกสาร [`db-connection-and-rls.md`](db-connection-and-rls.md#อธิบายแบบเข้าใจง่าย-ตึกให้เช่า) เปรียบ isolation
floor เป็น **ตึกให้เช่าที่มีหลายบริษัทมาเช่าห้อง** (Database = ตัวตึก, Merchant = บริษัทผู้เช่า, พนักงานตรวจบัตร
= EF query filter + write guard) — หน้านี้ต่อยอด analogy เดียวกันให้ครอบคลุมทั้ง 6 layer แทนที่จะพูดถึงแค่ชั้น
เก็บของ เพื่อให้ผู้อ่านมี mental model เดียวกันข้ามเอกสารทั้งชุด ไม่ต้องเรียนภาพใหม่ทุกครั้งที่เปลี่ยนไฟล์.

ถ้า Persistence (§4) คือ "ห้องเก็บเอกสาร/คลังของอาคาร ที่มีพนักงานตรวจบัตรทุกครั้งก่อนเข้า-ออก" ตามที่อธิบายไว้
เต็มแล้วในเอกสารนั้น อีก 5 layer ที่เหลือก็มีบทบาทของตัวเองในตึกเดียวกัน:

| Layer | เทียบตึกให้เช่า | อธิบายสั้น |
|---|---|---|
| 1. SharedKernel | มาตรฐานกลางที่นิติบุคคลอาคารกำหนดตายตัว | สัญญาเช่าทุกห้องต้องคิดค่าเช่าเป็นหน่วยเดียวกัน ทศนิยม 4 ตำแหน่งเป๊ะ ห้ามห้องไหนคิดเลขเอง |
| 2. Contracts | บอร์ดประกาศส่วนกลางของอาคาร | ห้องหนึ่งมีเรื่องเกิดขึ้น ติดประกาศไว้ ห้องอื่นที่สนใจมาอ่านเอง ไม่ต้องเดินไปบอกทีละห้อง |
| 3. BuildingBlocks | ทีมงานส่วนกลางของอาคาร | รปภ.ตรวจบัตรทุกคนก่อนเข้า (actor/merchant check), ช่างเทคนิคดูแลระบบท่อ-ไฟฟ้ากลางที่ทุกห้องใช้ร่วมกัน |
| 4. Persistence | ห้องเก็บเอกสาร/คลังของอาคาร | มีพนักงานตรวจบัตรทุกครั้งก่อนเข้า-ออก — รายละเอียดเต็มอยู่ที่ [`db-connection-and-rls.md`](db-connection-and-rls.md#อธิบายแบบเข้าใจง่าย-ตึกให้เช่า) แล้ว ไม่ขอย้ำซ้ำที่นี่ |
| 5. Modules | ห้องเช่าของผู้เช่าแต่ละราย | แต่ละห้องทำธุรกิจของตัวเอง เดินเข้าห้องอื่นตรง ๆ ไม่ได้ ต้องผ่านบอร์ดประกาศกลาง (แถวที่ 2) เท่านั้น |
| 6. Hosts | สำนักงานนิติบุคคลอาคารชุด | จุดเดียวที่รู้จักทุกห้อง ทุกทีมงาน ผูกทุกอย่างเข้าด้วยกัน กำหนดว่าใครเข้าห้องไหนได้บ้าง แล้วเปิดตึกให้ทำการจริง |

ตารางนี้ให้แค่ภาพตั้งต้น — รายละเอียดจริงของแต่ละ layer (โค้ดจริง, ไฟล์จริง, จุดอ่อนจริง) อยู่ใน §1-§6 ถัดจากนี้.

---

## 1. SharedKernel — พื้นล่างสุด ไม่มีใครให้พึ่ง

**คืออะไร**: project เดียว (`SharedKernel.csproj`) ที่ไม่มี `ProjectReference` ออกไปหาใครเลย — เป็นจุดต่ำสุดของทั้ง dependency graph. มีแค่ 4 ไฟล์ ไม่มี subfolder: `Entity.cs`, `Money.cs`, `Iso4217.cs`, `MoneyJsonConverter.cs`.

**บทบาท**: เป็นจุดเดียวที่นิยาม domain primitive ที่ต้อง "เหมือนกันทุกที่" ข้ามทั้ง 12 โมดูล — จะได้ไม่มีโมดูลไหนคิด `Money` เองแล้วเงินหมุนข้าม seam ผิดรูปแบบ. โดยเฉพาะกฎที่ user ตัดสินไว้ตั้งแต่ 2026-07-05 (as-built ตรงตามนี้แล้วตั้งแต่ rf1): **ห้าม float/double ที่ไหนก็ตามที่แทนเงิน**.

**ถ้าไม่มีชั้นนี้**: แต่ละโมดูลจะประกาศ `Money`/เงินของตัวเองแยกกัน (บางที่อาจใช้ `decimal` ดิบ บางที่อาจพลาด
ใช้ `double`) แล้วพอเงินข้าม seam ระหว่างโมดูล (เช่น `PaymentPaid.Amount` ไปเข้า `Orders`) จะไม่มีอะไรการันตี
ว่าทั้งสองฝั่งปัดทศนิยม/validate currency แบบเดียวกัน — เสี่ยงเงินหายหรือเกินจากการปัดเศษไม่ตรงกัน ซึ่งเป็น
ความเสี่ยงที่ user ตัดสินห้ามไว้ตรง ๆ ตั้งแต่ 2026-07-05 (ห้าม float/double แทนเงินที่ไหนก็ตาม).

**หน้าที่แต่ละไฟล์**:
- `Entity<TId>` — base class DDD ที่เทียบ "ตัวตน" ด้วย runtime type + Id (ไม่ใช่เทียบ field ทีละตัว) — constructor ว่างเป็น `protected` ไว้ให้ EF Core materialize เท่านั้น
- `AggregateRoot<TId>` — สืบทอดจาก `Entity<TId>` เพิ่ม `Raise(IDomainEvent)` (เก็บลง list ภายใน) และ `ClearDomainEvents()` (drain ออก). **ของจริงวันนี้**: ไม่มี aggregate ไหนเรียก `Raise()` เลยสักที่ (grep ทั้ง repo เจอแค่ definition), และทุก EF config เรียก `builder.Ignore(x => x.DomainEvents)` ตรง ๆ พร้อมคอมเมนต์ "events are enqueued by the handler in-tx (REQ-20), not via the aggregate" — เท่ากับ hook นี้เป็น dead machinery ที่ยังไม่มีใครต่อสาย ไม่ใช่กลไก auto-translate เป็น outbox row จริง; ของจริงคือ handler เรียก `IOutbox.Enqueue(...)` เองตรง ๆ ทุกจุด (ดู §2, B2, B3)
- `Money` — `readonly record struct { decimal Amount; string Currency }`, สร้างได้ทางเดียวคือ `Money.Of(amount, currency)` ซึ่ง validate currency ต้องรู้จักใน ISO-4217, amount ห้ามติดลบ, scale ห้ามเกิน 4 ตำแหน่งทศนิยม. `default(Money)` เป็นค่า sentinel ที่ `Currency` เป็น `null` — **แต่การ์ดนี้ครอบแค่ arithmetic** (`Add()` เช็ค null แล้ว throw `InvalidOperationException` ก่อนบวก) เท่านั้น: อ่าน `.Amount`, เรียก `ToString()`, หรือ serialize ผ่าน `MoneyJsonConverter.Write()` **ไม่ throw** เลย (converter จะเขียน `{"amount":"0.0000","currency":null}` เงียบ ๆ) — ถ้า default `Money` หลุดเข้า response จริงจะไม่มี exception เตือน ต้องระวังตอนสร้าง object ให้ผ่าน `Money.Of()` เสมอ ไม่ใช่พึ่งการ throw ของ sentinel
- `Iso4217` — registry สกุลเงินขั้นต่ำ (THB/USD/JPY วันนี้), บอกจำนวนทศนิยมต่อสกุล
- `MoneyJsonConverter` — บังคับ JSON field `amount` ต้องเป็น **string** เท่านั้น (ปฏิเสธถ้าเจอเป็น number กัน IEEE754 double precision loss ตอน parse), เขียนกลับ fix 4 ตำแหน่งเสมอ, และตอนอ่านกลับก็เรียก `Money.Of()` ซ้ำ — เท่ากับ re-validate ทุกครั้งที่ deserialize ไม่ใช่ trust JSON เฉย ๆ

**ทำงานยังไง**: ทุกโมดูลใน `src/Modules/*.Domain` และ `*.Application` reference `SharedKernel` ได้ (21 `.csproj` อ้างถึงจริง) — แต่ `SharedKernel` เองไม่ reference กลับไปหาใคร. กติกานี้ไม่ใช่แค่คำแนะนำในเอกสาร — `Architecture.Tests` (NetArchTest) เช็คจริงว่า `*.Domain` ห้ามพึ่ง `Microsoft.EntityFrameworkCore` โดยเฉพาะ และห้ามพึ่ง namespace `*.Infrastructure` ใด ๆ เลย (ของตัวเองหรือโมดูลอื่น) — **ไม่ใช่ guard แบบ "ห้ามพึ่ง framework ใด ๆ เลย" กว้าง ๆ** ถ้า Domain วันหนึ่งไป reference ASP.NET Core หรือ Mediator ตรง ๆ (ไม่ผ่าน EF Core/`*.Infrastructure` namespace) จะไม่ถูก guard ปัจจุบันจับ. ที่ยังไม่พังทุกวันนี้เพราะ Domain มีแค่ `SharedKernel` ให้พึ่งเท่านั้น. `Contracts.csproj` (§2) ก็ reference `SharedKernel` เช่นกัน เพื่อให้ event ข้ามโมดูลพก `Money` แบบ value object ได้ตรง ๆ (เช่น `PaymentPaid.Amount: Money`) แทนที่จะต้องแปลงเป็น `decimal`/`long` ดิบตรง seam.

**ทำงานร่วมกับ layer อื่นตรงไหน**: ทุก aggregate ในทั้ง 12 โมดูล (`Product`, `Merchant`, `Order`, `Payments.Session` ฯลฯ) สืบทอด `AggregateRoot<TId>` จากที่นี่ และเก็บฟิลด์เงินเป็น `Money` เสมอ — เห็นตัวจริงที่ตัวอย่าง **B1 (สร้างสินค้า)** ด้านล่าง ที่ `Product.Price`/`Product.SumInsured` เป็น `Money` ตั้งแต่ domain ยัน wire response.

---

## 2. Contracts — ภาษากลางที่โมดูลใช้คุยกัน

**คืออะไร**: project เดียว (`Contracts.csproj`) reference แค่ `SharedKernel` + package `Mediator.Abstractions`. เก็บ record 4 ตัว: `PaymentPaid`, `CheckoutConfirmed`, `CustomerOrderNotification`, `MerchantUserRegistrationSubmitted` — **ไม่ใช่** API request/response DTO (พวกนั้นไม่มีบ้านแยก ประกาศ inline อยู่ที่ `Hosts/Api` ตรงจุด map endpoint). Contracts คือ payload ของ **event ข้ามโมดูลใน process เดียวกัน** เท่านั้น.

**บทบาท**: เป็น "ภาษากลาง" (published language) ที่โมดูลหนึ่งใช้บอกอีกโมดูลว่าเกิดอะไรขึ้น โดยไม่ต้อง reference โมดูลปลายทางตรง ๆ เลย — เช่น `Checkouts.Application` ไม่รู้จัก `Orders.Application` เลยแม้แต่นิดเดียว แค่ publish `CheckoutConfirmed` ไปเข้า outbox แล้วปล่อยให้ `Orders` ไปสมัคร consumer ของตัวเองมารับ.

**ถ้าไม่มีชั้นนี้**: `Checkouts.Application` ต้อง reference `Orders.Application` ตรง ๆ เพื่อบอกว่า checkout
confirm แล้ว — โมดูลทั้ง 12 จะผูกกันแน่นเป็นใยแมงมุม แก้ business rule ในโมดูลหนึ่งกระทบโมดูลอื่นทันทีโดยไม่
ตั้งใจ และกฎที่ `Architecture.Tests` บังคับ (ห้าม peer-module reference กันตรง ๆ ยกเว้น `Merchants.Application
→ Payments.Application` ที่ตั้งใจเปิดไว้) จะไม่มีทางบังคับได้เลยถ้าไม่มีภาษากลางแบบนี้ให้เลือกใช้แทน.

**convention ที่ทุก record ตามเหมือนกันหมด**: เป็น `sealed record ... : INotification`, มี `public const string SchemaVersion = "v1"` (versioning เป็น constant field ไม่ใช่แยก type/namespace ต่อ version — วันนี้มีแค่ v1 ทุกตัว ยังไม่เคยต้องขึ้น v2 จริง), และ `Money` ข้าม seam เป็น value object เสมอ ไม่ใช่ raw scalar.

**ทำงานยังไง**: ผ่าน **transactional outbox** — handler ฝั่งผู้ส่งบันทึก state change ของ aggregate ตัวเองพร้อมกับ `IOutbox.Enqueue(event)` **ในทรานแซคชันเดียวกัน** (atomic — ถ้า commit ไม่สำเร็จ event ก็ไม่ถูกส่ง ไม่มีทาง state เปลี่ยนแต่ event หาย). แล้ว background dispatcher (`OutboxDispatcher` ใน `Persistence.MerchantRuntime`, `MerchantUserOutboxDispatcher` ใน `Persistence.MerchantUsers` — รันเป็น `IHostedService` อยู่ใน process `Api`) poll ตาราง outbox แล้ว publish event จริงผ่าน Mediator. รูปแบบนี้คือ **at-least-once** — consumer ฝั่งรับต้อง idempotent เอง ไม่ใช่หน้าที่ของ Contracts.

ตัวอย่างเส้นทางจริง 2 เส้น:
- `Checkouts.Application/ConfirmCheckout.cs` เขียน `CheckoutConfirmed` → `Orders.Application/CheckoutConfirmedConsumer.cs` รับแล้วเปิด order ใหม่
- `Payments.Application/HandlePspWebhook/...` เขียน `PaymentPaid` (หลัง PSP ยืนยันจ่ายจริง) → `Orders.Application/OrderPaidConsumer.cs` รับแล้ว **re-verify amount/currency ซ้ำ** ก่อนเรียก `Order.MarkPaid(...)` — ไม่เชื่อ event เฉย ๆ โดยไม่เช็ค

**ทำงานร่วมกับ layer อื่นตรงไหน**: ทั้งสองเส้นทางข้างบนเดินเต็ม end-to-end (parse event → outbox → dispatcher → consumer) อยู่ที่ตัวอย่าง **B2 (checkout confirm เปิด order เอง)** และ **B3 (PSP webhook ยืนยันจ่าย)** ด้านล่าง

---

## 3. BuildingBlocks — โครงสร้างพื้นฐานที่ทุกโมดูลยืมใช้ แต่ไม่ใช่ business logic ของใคร

**คืออะไร**: ไม่ใช่ project เดียวแบบ SharedKernel/Contracts — เป็น **3 project เรียงเป็นวง (ring)** ที่แต่ละวงมีหน้าที่ต่างกันชัดเจน:

- **`BuildingBlocks.Application`** (→ SharedKernel, Contracts, Mediator.Abstractions) — มีแต่ interface/port ล้วน ๆ ไม่มี implementation, framework-agnostic
- **`BuildingBlocks.Infrastructure`** (→ BuildingBlocks.Application, EF Core SqlServer) — implementation จริงของ port ข้างบน (EF/crypto/HTTP)
- **`BuildingBlocks.Web`** (→ ทั้งสองตัวบน, FrameworkReference ASP.NET Core) — cross-cutting เฉพาะระดับ HTTP

comment ในตัว `.csproj` ของ `.Web` อธิบายเหตุผลไว้ตรง ๆ ว่า: cross-cutting HTTP concern ต้องมีที่เดียวแล้วให้ทุก host ยืมใช้ ไม่ใช่เขียนซ้ำ.

**บทบาท**: เป็น "platform core" ที่ทุกโมดูลใช้ร่วมกัน แต่ตัวมันเองไม่รู้จัก business state ของ Product/Order/Payment เลย — สิ่งที่มัน own คือ: actor/merchant execution context, mediator pipeline behavior, transactional outbox/idempotency primitive, write-guard, common API middleware, health check contract. สิ่งที่มัน **ไม่** own คือ authorization policy เฉพาะของแต่ละ domain หรือ provider-specific payload (เช่น PSP).

**ถ้าไม่มีชั้นนี้**: ทุก handler ในทุกโมดูลต้องเขียนเช็ค actor/merchant เองซ้ำทุกจุด (ไม่มี `MerchantGuardBehavior`
กลางคอยกันตั้งแต่ก่อนเข้า handler) — ลืมเช็คจุดเดียวในโมดูลใดโมดูลหนึ่งก็เท่ากับรูรั่วให้ merchant หนึ่งเห็นข้อมูล
ของอีก merchant ได้ ตัวอย่างจริงว่าทำไม "จุดบังคับกลาง" สำคัญคือ B6 — write-guard ที่ต้องพึ่ง 3 จุดตรงกันเป๊ะ
(ไม่ใช่แค่จุดเดียว) ยังเคยพังเงียบ ๆ มาแล้วครั้งหนึ่งจริง ๆ.

**หน้าที่ตัวอย่างสำคัญที่สุด 2 กลไก**:

1. **Actor/merchant isolation** (`BuildingBlocks.Application`): `IActorContext`/`IActorScope` คือแกนกลาง — merchant ปัจจุบันของ request มาจาก **authenticated principal เท่านั้น ไม่ใช่จาก URL**. `IMerchantScoped` เป็น marker interface ว่าง ๆ ที่ command/query ต้อง implement ถ้าแตะข้อมูลของ merchant เดียว แล้ว `MerchantGuardBehavior<,>` (Mediator `IPipelineBehavior`) จะเช็คก่อนทุกครั้งว่ามี actor ผูกอยู่ไหม — ถ้าไม่มี throw `MerchantBindingException` ทันทีก่อนแม้แต่จะเข้า handler พร้อมยิง security telemetry event ว่ามี unbound actor พยายามเข้าถึง
2. **Write guard** (`BuildingBlocks.Infrastructure`): `GuardedRuntimeDbContext` คือ abstract base class ที่ seal ทั้ง 4 overload ของ `SaveChanges`/`SaveChangesAsync` ผ่านจุดเดียว (`GuardPendingChanges()` — derived class เขียนทับไม่ได้) ทุก DbContext ที่ runtime จริงต้อง inherit ตัวนี้ แล้วจะได้การเช็คฟรี 3 ชั้นต่อทุก entity ที่ tracked: (ก) ห้ามแก้/ลบ entity ที่ mark เป็น append-only เช่น audit trail, (ข) ห้าม tenant key เป็น `Guid.Empty` และห้ามแก้ tenant key หลัง insert แล้ว, (ค) เรียก `IWriteAuthorizer.CanWrite(entityType, operation, targetMerchant)` ซึ่ง default-deny — implementation จริงของ port นี้อยู่ที่ Host เท่านั้น (§6)

**ไฟล์อื่นที่น่ารู้**: `PolDbContext` (migration-owner ตัวเดียวของทั้งระบบ ไม่ registered runtime), `SchemaNames` (constant ชื่อ schema ที่ทุก `IEntityTypeConfiguration` ต้องใช้), `Vault/` (envelope encryption AES-256-GCM สำหรับ secret ของ PSP), `Outbox/`+`Idempotency/` (entity รองรับกลไก §2), `Observability/` (ส่ง denial event ไป Seq), และฝั่ง `.Web`: `ProblemDetailsExceptionHandler` (จุดเดียวทั้งระบบที่แปลง exception type → HTTP status ตาม RFC7807 — `Detail` เป็น string คงที่เสมอ ไม่ใช่ `exception.Message` กัน leak ข้อมูล merchant/SQL ออกไปกับ response).

**โปรเจกต์ในวงนี้ทำอะไรบ้าง (project ต่อ project)**: ทำไมต้องแยกเป็น 3 project แทนที่จะรวมเป็นก้อนเดียว —
เพราะ `.Application` คือสิ่งที่ Domain (§1) ได้รับอนุญาตให้ reference (เป็นแค่ port ล้วน ไม่มี implementation) ถ้า
รวม EF/HTTP เข้าไปในโปรเจกต์เดียวกัน Domain จะมีทางลัด reference เข้า framework โดยไม่ตั้งใจทันทีที่วันหนึ่งมีคน
เผลอ `using Microsoft.EntityFrameworkCore` ใน `.Application` แล้ว build ผ่าน — การแยก project เป็นการบังคับด้วย
compiler ไม่ใช่แค่ convention:
- `BuildingBlocks.Application` — framework-agnostic แท้ (reference แค่ `SharedKernel`/`Contracts`/
  `Mediator.Abstractions`) — ถ้าเห็นไฟล์ในนี้ import อะไรนอกเหนือจากนี้คือสัญญาณว่าโครงสร้างเพี้ยนแล้ว
- `BuildingBlocks.Infrastructure` — จุดเดียวที่มี `PolDbContext` (migration-owner) แต่ **ไม่ registered ที่ runtime
  เลย** — คนใหม่มักงงว่าทำไม `dotnet ef migrations add` ใช้ context คนละตัวกับที่ API รันจริง (`ControlPlaneDbContext`/
  `MerchantRuntimeDbContext`/`MerchantUserDbContext` ใน §4) — เหตุผลคือต้องมี context กลางตัวเดียวที่เห็น
  `IEntityTypeConfiguration` ของทุกโมดูลพร้อมกันตอน generate migration แต่ runtime แยก context ตาม transactional
  cluster เพื่อคุม isolation floor (§4)
- `BuildingBlocks.Web` — เฉพาะ cross-cutting ที่ผูกกับ ASP.NET Core ตรงๆ (`ProblemDetailsExceptionHandler` ฯลฯ)
  แยกออกมาเพื่อไม่ให้ `.Infrastructure` (ที่ EF/crypto ใช้ได้แม้ไม่มี HTTP context เช่นตอนรัน migration หรือ
  background dispatch) ต้องพ่วง `FrameworkReference` ของ ASP.NET Core ไปด้วยทั้งที่ไม่จำเป็น

**ทำงานยังไง (เดินเส้นทางเดียวให้เห็นภาพ)**: request เข้ามา → `HttpActorContext` (ประกาศที่ Host) resolve merchant จาก principal ที่ authenticate ผ่านแล้ว → Mediator ส่ง command เข้า `MerchantGuardBehavior` ก่อนถึง handler จริง (เช็ค actor ผูกหรือยัง) → handler เรียก repository ซึ่งเขียนผ่าน DbContext ที่ inherit `GuardedRuntimeDbContext` (เช็ค write authorize) → ถ้าจุดไหน deny exception จะโยนขึ้นไปโดน `ProblemDetailsExceptionHandler` แปลงเป็น response กลับไปหา client.

**consumer**: ทุกโมดูล `.Application` และ `.Infrastructure` reference BuildingBlocks (30 `.csproj`) — ยกเว้น `.Domain` ไม่แตะเลยแม้แต่ตัวเดียว (สอดคล้องกับกฎ §1 ว่า Domain พึ่งได้แค่ SharedKernel).

**ทำงานร่วมกับ layer อื่นตรงไหน**: `MerchantGuardBehavior` คั่นกลางระหว่าง Hosts กับ Modules ทุก request, `GuardedRuntimeDbContext` คั่นกลางระหว่าง Modules กับ Persistence ทุกครั้งที่เขียน — เห็นทั้งคู่ทำงานพร้อมกันในตัวอย่าง **B1**; `IOutbox`/`IUnitOfWork.ExecuteInTransactionAsync` เห็นชัดสุดใน **B3**; ส่วน `IWriteAuthorizer` ที่ implement จริงอยู่ที่ Host เห็นเบื้องหลังเต็ม ๆ ใน **B6**

---

## 4. Persistence — runtime data-plane จริง หลัง RLS ถูกถอด

**คืออะไร**: 4 assembly ที่แยกตาม **"transactional cluster" ไม่ใช่แยกตาม business module** — เกิดขึ้นจาก spec `rls-to-query-filter` (2026-07-19) ที่ถอด SQL Server Row-Level Security ออกจากระบบทั้งหมด แล้วย้าย isolation floor มาไว้ที่ app layer แทน (ก่อนหน้านั้นชั้นนี้ไม่มีอยู่เลย):

- **`Persistence.ControlPlane`** → `ControlPlaneDbContext` — คุม schema `admin`/`iam`/`cfg`/`dbo.DataProtectionKeys`, ไม่มี merchant dimension เลยจึงไม่มี query filter
- **`Persistence.MerchantUsers`** → `MerchantUserDbContext` — คุม schema `merch` เฉพาะส่วน identity/session
- **`Persistence.MerchantRuntime`** → `MerchantRuntimeDbContext` — คุม schema `shop`/`txn`/`merch` (ส่วนข้อมูล) — **นี่คือ isolation floor จริงของระบบ**: ทุก entity มี query filter `MerchantId == CurrentMerchant`
- **`Persistence.Provisioning`** → `ProvisioningCoordinator` — จุดเดียวในทั้งระบบที่ 2 context ข้างบนแชร์ connection/transaction เดียวกัน (ใช้ตอน super-admin provision merchant ใหม่: เขียน ledger ฝั่ง control plane + เขียน merchant/PSP connection/vault secret ฝั่ง merchant runtime แบบ atomic)

**ทำไมต้องแยกเป็น assembly ที่ 4 แทนที่จะฝัง `ProvisioningCoordinator` ไว้ใน `ControlPlane` หรือ `MerchantRuntime`
เลย**: เพราะฝังที่ไหนก็ตาม assembly นั้นจะต้อง `ProjectReference` กลับไปหาอีก assembly หนึ่ง (ต้องเห็นทั้ง
`ControlPlaneDbContext` และ `MerchantRuntimeDbContext` พร้อมกันเพื่อแชร์ transaction) ซึ่งทำลายกติกา "1 assembly
= 1 transactional cluster" ที่ทั้ง section นี้ยืนอยู่บน — escape-hatch allowlist ที่ CI บังคับก็ผูกกับเส้นแบ่งนี้
เช่นกัน แยกเป็น assembly ที่ 4 (ไม่ใช่ของฝ่ายไหน) จึงเป็นทางเดียวที่ reference เข้าทั้งคู่ได้โดยไม่ทำให้ 2
assembly หลักต้องรู้จักกัน

**เพิ่ม EF entity ใหม่ในโมดูลที่มีอยู่แล้ว ต้องแตะไฟล์ไหนบ้างถึงจะครบ** (พลาดจุดใดจุดหนึ่งคือ migration ผ่านแต่
runtime ใช้งานไม่ได้ หรือ query ได้แต่เขียนไม่ได้):
1. `IEntityTypeConfiguration` ของ entity ใหม่ — วาง namespace ให้ `PolDbContext` (`BuildingBlocks.Infrastructure`,
   migration-owner) discover เจอตอน `dotnet ef migrations add`
2. runtime `DbContext` ที่ตรง schema จริง (1 ใน `ControlPlaneDbContext`/`MerchantRuntimeDbContext`/
   `MerchantUserDbContext` ตามที่ตารางด้านบนแบ่งไว้) ต้อง `Set<TEntity>()` ให้ตรงตัว ไม่งั้น repository จริงมองไม่
   เห็น entity เลยแม้ migration จะสร้างตารางไปแล้ว
3. migration SQL ต้อง `GRANT` สิทธิ์ตารางใหม่ให้ `pol_app` เสมอ (ไม่มี default grant) — จุดนี้ SQLite unit test
   จับไม่ได้เพราะไม่มี concept ของ SQL principal จะรู้ตัวก็ตอน deploy จริงหรือ integration test เท่านั้น

**บทบาท**: แทนที่ SQL RLS เดิมด้วย 2 ชั้นที่ทำงานที่ app layer ทั้งคู่ — read floor = EF global query filter (deny-by-default: ถ้า actor ไม่ผูก merchant, `CurrentMerchant` จะเป็น `Guid.Empty` และ query filter จะคืน 0 แถวเสมอ ไม่ใช่ error), write floor = `GuardedRuntimeDbContext` (§3) ผสมกับ `IWriteAuthorizer` ที่ implement จริงอยู่ที่ Host.

**ถ้าไม่มีชั้นนี้**: ไม่มี query filter/write guard ที่ app layer แปลว่าไม่มีอะไรกันเลยระหว่าง merchant กับ
merchant ในระดับ SQL — นี่ไม่ใช่สถานการณ์สมมติ: ก่อน spec `rls-to-query-filter` (2026-07-19) ระบบเคยพึ่ง SQL
Server RLS ที่ตัว database เป็นคนกันแทน พอถอด RLS ออก ถ้าไม่มี Persistence layer นี้มารับช่วงต่อ merchant หนึ่ง
จะ query ข้ามเห็นข้อมูลของอีก merchant ได้ตรง ๆ ผ่าน connection เดียว (`pol_app`) ที่เข้าได้ทุก schema ทางกายภาพ
อยู่แล้ว.

**ทำงานยังไง**: ทั้ง 3 runtime context เป็น `internal sealed` — Host เห็น **type ตรง ๆ ไม่ได้เลย** ต้องผ่าน DI extension method ของแต่ละ assembly เท่านั้น (`AddControlPlanePersistence`, `AddMerchantUserPersistence`, `AddMerchantRuntimePersistence`, `AddProvisioning`) ซึ่งเป็น "seam สาธารณะ" จุดเดียวที่แต่ละ assembly เปิดออกมา. `MerchantRuntimeDbContext.CurrentMerchant` คำนวณ **ต่อ query ต่อ instance** จาก `IActorContext` (ไม่ bake ค่าเข้า cached EF model) ทำให้ query filter ตรวจสอบใหม่ทุกครั้งไม่ใช่ตรวจครั้งเดียวตอน boot.

รายละเอียดเต็ม (flow อ่าน/เขียนทีละขั้น A-E, escape-hatch allowlist ที่ CI บังคับ, การ recover จาก concurrency conflict) ยาวเกินจะสรุปในหน้านี้ — ดู [`db-connection-and-rls.md`](db-connection-and-rls.md) ซึ่งมี analogy แบบภาษาธรรมดาด้วย.

**ทำงานร่วมกับ layer อื่นตรงไหน**: query filter คั่นทุก query ที่ Modules ยิงผ่าน repository (**B1**), `OutboxDispatcher` เป็นสะพานที่ทำให้ Contracts (§2) ทำงานได้จริงข้ามโมดูล (**B2**/**B3**), `ProvisioningCoordinator` คือจุดเดียวที่ 2 context คุยกันในทรานแซคชันเดียว (**B5**)

---

## 5. Modules — 12 โมดูลธุรกิจ ที่แต่ละอันหน้าตาเหมือนกันหมด

**คืออะไร**: 12 โมดูลอยู่ใต้ `src/Modules/`, แต่ละโมดูลแบ่ง **3 project เหมือนกันเป๊ะทุกตัว**:

- `<Module>.Domain` — entity/value object/domain event บริสุทธิ์ พึ่งได้แค่ `SharedKernel`
- `<Module>.Application` — command/query/handler + ประกาศ **port** (interface) พึ่ง Domain ของตัวเอง + `Contracts` + `BuildingBlocks.Application` + `Mediator.Abstractions`
- `<Module>.Infrastructure` — EF `IEntityTypeConfiguration` + `Add<Module>Module()` (DI marker) — **หลัง Persistence แยกออกมาเป็นชั้นของตัวเอง (§4) repository implementation ส่วนใหญ่ย้ายออกจากตรงนี้ไปแล้ว** ทำให้ `Add<Module>Module()` ของเกือบทุกโมดูลวันนี้เหลือแค่ `=> services` เปล่า ๆ (มีไว้ให้ `HostModuleAssemblies.All` อ้าง assembly handle เพื่อ discover entity config เท่านั้น) — มีแค่ `AddMerchantsModule()`/`AddPaymentsModule()` ที่ยังมี body จริง (photo store default, PSP `HttpClient`)

**12 โมดูล**:

| โมดูล | หน้าที่ |
|---|---|
| Products | แคตตาล็อกกรมธรรม์ต่อ merchant |
| Carts | ตะกร้าเก็บ line ก่อน checkout |
| Checkouts | ล็อกราคาจาก cart subtotal + snapshot เงื่อนไขกรมธรรม์/ข้อมูลผู้เอาประกัน ณ เวลาซื้อ |
| Orders | order + line snapshot + policy-reference record ที่แก้ทีหลังได้ + reconciliation |
| Payments | payment session + redirect ไป PSP + รับ webhook (source of truth การจ่าย) |
| Merchants | merchant (tenant) entity + merchant-user identity ทั้งวงจร |
| Admins | admin staff identity + session + ขอบเขต merchant ที่เข้าถึงได้ |
| Iam | central RBAC catalog (permission/role) ใช้ร่วมทั้ง admin plane และ merchant plane |
| Divisions / Levels / Offices / Positions | reference data (schema `cfg`, ไม่มี merchant dimension) |

**บทบาท**: เป็น business logic container ที่แยก isolate กันด้วย **"published language" pattern** — ถ้าโมดูล B ต้องอ่าน vocabulary ที่เสถียรของโมดูล A (เช่น `Merchants.Infrastructure` ต้องรู้จัก `Role`/`Permission` ของ `Iam`) จะ reference ได้แค่ **`A.Domain` เท่านั้น** ห้ามแตะ `A.Application`/`A.Infrastructure` — บังคับจริงด้วย `Architecture.Tests` (NetArchTest) ไม่ใช่แค่ convention ปากเปล่า.

**ข้อยกเว้นเดียวที่ตั้งใจไว้**: `Merchants.Application.csproj` reference `Payments.Application.csproj` ตรง ๆ (ไม่ใช่แค่ `.Domain`) — `MerchantsArchitectureTests.cs` เขียนไว้ชัดว่า Merchants เป็น "PROVISIONING / composition module" ที่ยืนอยู่ *เหนือ* 5 โมดูลธุรกิจ เป็นจุดเดียวที่อนุญาตให้แตะ `Application` ของอีกโมดูลได้ตรง ๆ เพราะ `ProvisionMerchantHandler` ต้องสร้าง `PspConnection` + secret envelope ของ Payments ตอน provision merchant ใหม่ — จึงถูกกันออกจาก peer-ban set ของ `ArchitectureBoundaryTests` โดยเจตนา ไม่ใช่รูรั่วที่หลุดผ่าน CI

**ถ้าไม่มีชั้นนี้**: business logic ของ Products/Orders/Payments ฯลฯ จะกระจายไปปนอยู่กับ HTTP handler ที่ Host
หรือปนกับโค้ด EF ที่ Persistence โดยตรง — ทดสอบยากขึ้นมาก (ต้องบูต HTTP/DB จริงถึงจะทดสอบ business rule ได้)
และไม่มีเส้นแบ่งชัดว่าใครเป็นเจ้าของ invariant ไหน เช่น `Product.SumInsured` ต้อง currency เดียวกับ `Price`
จะกลายเป็นเช็คกระจัดกระจายหลายที่แทนที่จะอยู่ที่ `Product.Create` จุดเดียว.

**ทำงานยังไง** (เดินตัวอย่าง `Merchants` module ครบ vertical slice):
1. `Merchant.cs` (Domain) — aggregate มี constructor เป็น `private`, สร้างได้ผ่าน static factory `Create`/`CreateWithId` เท่านั้น ซึ่ง validate ข้างในทันที: code ต้องอยู่ใน allowlist, currency ต้องผ่าน `Iso4217.IsSupported` จริง (ตรวจกับ registry) — แต่ `country` เช็คแค่ **รูปแบบ 2 ตัวอักษรหลัง normalize** (`normalizedCountry.Length != 2`) เท่านั้น **ไม่ได้เช็คกับ ISO 3166 registry จริง** เหมือนที่ currency ทำ ดังนั้น `"ZZ"` (2 ตัวอักษร แต่ไม่มีในทะเบียนจริง) ผ่านการสร้างได้ — ห้ามอ่านว่า `Merchant` ที่สร้างสำเร็จแปลว่า country ถูกต้องตามมาตรฐาน ISO เสมอไป แปลได้แค่ "รูปแบบ 2 ตัวอักษร" เท่านั้น
2. `ProvisionMerchantCommand.cs` (Application) — `record : ICommand<ProvisionMerchantResult>` พก field ที่ handler ต้องใช้ทั้งหมด (spec ของ merchant, PSP connection, ใครเป็นคนสั่ง)
3. `ProvisionMerchantHandler.cs` (Application) — constructor รับแต่ **port ล้วน** (`IMerchantRepository`, `IProvisioningWriter`, `IPspSecretEnvelopeFactory`, `IClock`) ไม่รู้จัก EF Core เลย — validate ก่อน (pure, ไม่มี side effect) แล้วค่อย delegate การเขียนจริงให้ `IProvisioningWriter` (ซึ่ง implement จริงอยู่ที่ `Persistence.Provisioning`)
4. `MerchantsModuleRegistration.cs` (Infrastructure) — `AddMerchantsModule()` DI marker

จุดสำคัญ: **โมดูลไม่ map HTTP endpoint ของตัวเองเลย** — endpoint mapping ทั้งหมดอยู่ที่ Host (`Program.cs`, §6) เสมอ โมดูลไม่รู้จัก ASP.NET Core แม้แต่นิดเดียว นี่คือสิ่งที่ทำให้ Application layer ทดสอบได้ง่ายด้วย fake ของ port โดยไม่ต้องบูต HTTP server จริง.

**flow ธุรกิจ**: Products → Carts → Checkouts → Orders → Payments — คุยข้ามกันผ่าน `Contracts` (§2) เท่านั้น ไม่มีโมดูลไหน reference โมดูลถัดไปตรง ๆ.

**ทำงานร่วมกับ layer อื่นตรงไหน**: handler ในทุก use-case ข้างบน (`CreateProductHandler`, `ConfirmCheckoutHandler`, `HandlePspWebhookHandler` ฯลฯ) คือจุดที่ port จาก BuildingBlocks/Contracts มาบรรจบกับ entity ของ Domain เอง — เดินตัวอย่างเต็มดูที่ **B1-B6** ทั้งหมดด้านล่าง ซึ่งล้วนเริ่ม/จบที่ handler ของ Modules

### ลงรายละเอียดทีละโมดูล (12 โมดูล x 3 project)

ตารางด้านบนบอกแค่ "โมดูลนี้ทำอะไร" ทางธุรกิจ — ส่วนนี้ลงลึกระดับ project จริง (`.Domain`/`.Application`/
`.Infrastructure` ของแต่ละโมดูล) โฟกัสที่ "ทำไม" และ trap จริงจากโค้ด ไม่ใช่ตาราง "ไฟล์ | หน้าที่" ซ้ำกับ
[`src-structure.md`](src-structure.md) — ไฟล์นั้นคือที่ที่ควรไปดูถ้าต้องการรู้ว่าไฟล์ไหนอยู่ตรงไหน

#### 1. Products

**Domain**: `Product.cs` (81 บรรทัด) บน `Products.Domain.Product` — CRUD ธรรมดา ไม่มี state machine และไม่มี
concurrency token (ไม่มี `RowVersion` ทั้งใน aggregate และใน `ProductConfiguration`) invariant เด่นบังคับผ่าน
static factory `Create` เท่านั้น (ctor `private`, ตัว parameterless เปิดไว้ให้ EF materialize อย่างเดียว):
`SumInsured` ต้องมากกว่าศูนย์และ currency ตรงกับ `Price` เป๊ะ (`SameCurrencyAs`, ตามที่ B1 อธิบายไว้แล้ว),
`CoverageDurationDays` ต้องมากกว่าศูนย์, `MerchantId`/`Name`/`Insurer` ต้องไม่ว่าง — ผิดข้อใดข้อหนึ่ง throw
`ArgumentException` ทันทีตอน construct ไม่ปล่อยให้เป็น invalid state ค้างใน DB. หลังสร้างแล้วมีแค่ 2 mutation
คือ `Rename`/`Deactivate` (soft-delete แบบ flag `IsActive`, ไม่ hard delete)

**Application**: CQRS เต็มรูปแบบ — `CreateProductCommand`/`GetProductByIdQuery`/`GetProductsQuery`/
`ListProductsQuery` แยก handler ต่อ use-case ตรงไปตรงมา, port หลักคือ `IProductRepository` เดียว, ไม่มี
cross-module `.Domain` reference (`Products.Application.csproj` reference แค่ `Products.Domain`,
`Contracts`, `BuildingBlocks.Application` — ไม่แตะ `.Domain` โมดูลอื่นเลย). ที่น่าสนคือ `ListProductsQuery`
เป็น SFS exemplar ของทั้งระบบ (REQ-2/REQ-7 ใน `docs/reference/search-filter-sort.md`) — คู่กับ
`ProductFilterDto` ที่ deserialize+DataAnnotations-validate เอง จาก `productFilters` JSON query param แล้ว
throw `ArgumentException` (map เป็น 400) ถ้า JSON เพี้ยนหรือ validation ไม่ผ่าน แทนที่จะ silent-drop filter
นั้นทิ้งเงียบๆ; ผลลัพธ์ไป `ProductListItem` ซึ่งตั้งใจแยกเป็น read model ใหม่ ไม่ redefine `ProductView` เดิม
(comment ในโค้ดบอกตรงๆ ว่าจะไปพัง `GetProductsHandler` ถ้าทำแบบนั้น)

**Infrastructure**: `ProductsModuleRegistration.cs` -> `=> services` เปล่า แต่เปล่าด้วยเหตุผลที่ต่างจากโมดูล
reference-data ทั่วไป — comment บอกตรงๆ ว่า `IProductRepository` ตัวจริงย้ายไปอยู่ที่
`Persistence.MerchantRuntime` (`AddMerchantRuntimePersistence`, task 8.5.3) ไม่ใช่ที่นี่เลย สิ่งเดียวที่
project นี้มีคือ `ProductConfiguration` (EF mapping ของ `Product` — `Money` ถูก map เป็น complex type 2 ชุด
แยกกัน คือ `Price` และ `SumInsured`) ที่ host discover ผ่าน `HostModuleAssemblies.All` ตอน model-build

**จุดสังเกต**: คนใหม่ grep หา implementation ของ `IProductRepository` ใน `Products.Infrastructure` จะไม่เจอ —
ต้องรู้ว่ามันย้ายออกไปอยู่ `Persistence.MerchantRuntime` ทั้งก้อนแล้ว (ต่างจาก pattern ทั่วไปที่ Infrastructure
project ของโมดูลธุรกิจมักมี repository implementation ของตัวเอง)

#### 2. Carts

**Domain**: `Cart.cs` (122 บรรทัด) — aggregate root บน `AggregateRoot<Guid>` ที่ถือ `Item` (ใน
`Carts.Domain.Items`) เป็น owned collection ผ่าน field `_items`. State machine มีแค่ 2 states (Open →
CheckedOut) ทางเดียว และไม่สมมาตร: 4 เมธอด mutate (`AddItem`/`RemoveItem`/`SetItemQuantity`/`Clear`) guard
ด้วย `Status != CartStatus.Open` ทุกตัว แต่ `MarkCheckedOut()` เป็น one-liner ไม่มี guard เลย (เรียกซ้ำได้โดย
ไม่ throw) — รวม throw ในไฟล์นี้ 8 จุด (guard สถานะ, guard quantity ≤ 0, currency mismatch ใน
`EnsureCurrencyMatches`, product-not-found ใน `SetItemQuantity`) ถือว่าเป็น state guard ระดับปานกลาง ไม่มี
concurrency token (ไม่มี `RowVersion` หรือ field พิเศษใดๆ กัน race). Invariant เด่นอีกจุดคือ `AddItem` merge
quantity เข้า line เดิมเฉพาะเมื่อ `productId` และ `unitPrice.Amount` ตรงกันเป๊ะ (สินค้าเดียวกันคนละราคา = คนละ
line) และ `Subtotal` เป็น computed property คืน `null` เมื่อ cart ว่าง (ไม่มี currency ให้ denominate ศูนย์).
`Item` เองไม่มี navigation กลับไปหา `Cart` — มี `MerchantId` ของตัวเอง denormalize มาจาก parent ตอนสร้าง เพื่อ
เป็น tenant key ของ read floor (ตาม rls-to-query-filter REQ-6) และปิด drift ด้วย composite FK ที่ฝั่ง
Infrastructure

**Application**: CQRS เต็มรูปแบบ — `CreateCartCommand`, `AddItemToCartCommand`, `GetCartQuery` แยกไฟล์ต่อ
use-case ส่วน `RemoveItemFromCartCommand`/`SetCartItemQuantityCommand`/`ClearCartCommand` อยู่รวมไฟล์เดียว
(`CartEdits.cs`) แต่ยังแยก command/handler class ชัดเจนต่อ use-case ไม่ใช่ god handler ทุก command เป็น
`IMerchantScoped`. Port หลักมีตัวเดียวคือ `ICartRepository` (`Add` + `GetAsync`, ไม่มี save method เพราะปล่อย
ให้ `IUnitOfWork` flush) ไม่มี cross-module `.Domain` reference (`Carts.Application.csproj` reference แค่
`Carts.Domain`, `Contracts`, `BuildingBlocks.Application`). `CartEdits.cs` มี helper ภายใน
`CartLoad.RequireAsync` ให้ 3 handler (Remove/SetQuantity/Clear) เรียกร่วมกัน — คอมเมนต์บอกตรงๆ ว่า RLS กรอง
cross-merchant row อยู่แล้วเป็นชั้นแรก ตัว check `cart.MerchantId != merchantId` เป็น belt-and-braces ชั้นสอง
เท่านั้น และตั้งใจยุบ "not found"/"wrong merchant" ให้เป็นข้อความเดียว (`"was not found"`) ไม่ leak การมีอยู่ของ
cart ข้าม merchant — แต่ `AddItemToCartHandler` (เขียนก่อน) ยังไม่ได้ใช้ `CartLoad`, ใช้ inline check ของตัวเอง
ที่ throw ข้อความต่างกันระหว่างสองกรณี (`"was not found"` vs `"does not belong to the requesting merchant"`)

**Infrastructure**: มี body จริง 2 ไฟล์ — `CartConfiguration.cs` และ `Items/ItemConfiguration.cs` (ทั้งคู่เป็น
`IEntityTypeConfiguration`) map เข้า schema `shop` ที่ใช้ร่วมกับโมดูลอื่น ไม่ใช่ schema เฉพาะโมดูล; `Cart` มี
alternate key `(Id, MerchantId)` และ `Item` มี composite FK ไปที่ key นั้น (ปิด denormalization drift ตามที่
comment ใน Domain อ้างถึง, ระบุ REQ-6.5 + Codex-R1 #8 ตรงๆ ในโค้ด); `UnitPrice` แมปเป็น `ComplexProperty`
(`decimal(19,4)` + `char(3)`) ตาม EF money mapping rule; `Subtotal`/`LineTotal`/`DomainEvents` ทั้งหมด
`Ignore()` เพราะเป็น computed ส่วน `CartModuleRegistration.cs` เองเป็น `=> services` เปล่าจริง — comment บอก
เหตุผลตรงๆ ว่า repository ย้ายไปอยู่ `Persistence.MerchantRuntime` (task 8.5.3) แล้ว การมีไฟล์นี้อยู่มีไว้แค่ให้
assembly เข้า `HostModuleAssemblies.All` เพื่อให้ `PolDbContext` discover `CartConfiguration`/`ItemConfiguration`
ตอน model-build เท่านั้น

**จุดสังเกต**: `ICartRepository` ตัวจริงไม่ได้ bind อยู่ใน `Carts.Infrastructure` เลยแม้แต่บรรทัดเดียว —
implementation จริงอยู่ที่ `Persistence.MerchantRuntime/Carts/CartRepository.cs` และผูกผ่าน
`AddMerchantRuntimePersistence(...)` ที่ `Program.cs` เรียกแยกจาก `AddCartModule()` คนละบรรทัด — ต่างจาก
`Divisions` ตรงที่ `AddCartModule()` ยังถูกเรียกจริงใน `Program.cs` (ไม่ได้หายไปเฉยๆ) แต่มันแค่คืน `services`
เปล่าไม่ทำอะไร ดังนั้นคนใหม่ที่ grep หา DI registration ของ `ICartRepository` ใน `Carts.Infrastructure` จะไม่
เจอ ต้องรู้ว่า cart repository ถูกจัดกลุ่มรวมกับ repository ของโมดูลอื่นใน merchant runtime plane แทน

#### 3. Checkouts

**Domain**: `Session.cs` (91 บรรทัด) — aggregate root ชื่อ `Checkouts.Domain.Session` (ระวังสับสนกับ
`Payments.Domain.Session` คนละ CLR type คนละโมดูล ชื่อชนกันเฉยๆ) มี state machine 3 states เดินทางเดียว
`Started → Confirmed` หรือ `Started → Abandoned` (ทั้งคู่ throw `InvalidOperationException` ถ้าเรียกซ้ำหรือเรียก
จาก state อื่นที่ไม่ใช่ `Started`) — **ไม่มี concurrency token** แบบ `RowVersion` ที่ `Payments.Domain.Session`
มี invariant เด่นคือ `Items` ถูก snapshot ทั้งก้อนตอน `Start` (insurance-pivot REQ-6.5) ไม่อ่านสดซ้ำระหว่าง start
กับ confirm, `Start` reject cart ว่าง (defense in depth ซ้อนกับที่ endpoint เช็คไปแล้วชั้นหนึ่ง) และ nested
entity `Item` เช็ค invariant ของ insured person (ชื่อ/นามสกุล/เลขบัตรห้ามว่าง, วันเกิดห้ามเป็นอนาคต) ที่ระดับนี้
เอง ไม่รอไปเช็คตอนสร้าง Order — comment ในโค้ดยืนยันตรงๆ ว่าจงใจ enforce ที่นี่เพราะเกิดก่อนจุดที่ confirm จะ
reachable ได้เสมอ ทำให้ request ที่ผิดไม่มีทางไปถึง successful confirm ได้เลย (defense-in-depth ชั้นที่สองยังคง
อยู่ที่ `Order.Create` เหมือนเดิม)

**Application**: CQRS เต็มรูปแบบ — `StartCheckoutCommand`/`ConfirmCheckoutCommand` แยก handler ต่อ use-case,
port หลักมีแค่ `ICheckoutRepository` ตัวเดียว (ไม่มี external-provider port แบบ `IPspAdapter` ของ Payments) —
**ไม่มี cross-module `.Domain` reference** (`Checkouts.Application.csproj` reference แค่ `Checkouts.Domain`,
`Contracts`, `BuildingBlocks.Application`) และเป็นการตัดสินใจตั้งใจ ไม่ใช่ช่องว่าง: comment ใน `Item.cs` ยืนยัน
ตรงๆ ว่า `Checkouts.Domain.Items.Item` เป็นคนละ CLR type จาก `Orders.Domain.Items.Item` โดยเจตนา สองโมดูลคุย
กันผ่าน `Contracts.CheckoutConfirmed` DTO เท่านั้น — `ConfirmCheckoutHandler` เรียก `session.Confirm()` แล้ว
enqueue `CheckoutConfirmed` (พร้อม items snapshot) ผ่าน `IOutbox` ในหน่วย unit of work เดียวกัน (transactional
outbox) เพื่อให้ Orders เปิด order แบบ out-of-band โดยไม่ผูก Checkout เข้ากับ Orders ตรงๆ

**Infrastructure**: `CheckoutModuleRegistration.AddCheckoutModule()` เป็น `=> services` เปล่า เหมือน Divisions
— แต่คนละเหตุผลกัน: comment บอกตรงๆ ว่า repository ย้ายไปอยู่ `Persistence.MerchantRuntime` แล้ว (task 8.5.3)
bind ผ่าน `AddMerchantRuntimePersistence` แทน (ยืนยันแล้วว่า `CheckoutRepository` ตัวจริงอยู่ที่
`Persistence/Persistence.MerchantRuntime/Checkouts/CheckoutRepository.cs`) โปรเจกต์นี้จึงเหลือหน้าที่เดียวคือ
ให้ EF Core scan เจอ `SessionConfiguration`/`ItemConfiguration` (map `Session.Amount` เป็น complex type ตาม
Money mapping rule + composite alternate key `(Id, MerchantId)` ผูก owned `Items` collection)

**จุดสังเกต**: คนใหม่ที่ grep หา implementation ของ `ICheckoutRepository` ใน `Checkouts.Infrastructure` จะหา
ไม่เจอเลย เพราะ registration ในโมดูลว่างเปล่าจริง — ตัวจริงอยู่ข้ามโปรเจกต์คนละที่ (`Persistence.MerchantRuntime`)
ต้องรู้ pattern นี้ก่อนถึงจะตามเจอ

#### 4. Orders

**Domain**: `Order.cs` (176 บรรทัด) — aggregate root กับ state machine 3 states (`AwaitingPayment` →
`Paid`/`Cancelled`) บน `OrderStatus`, ทางเดียวออกจาก terminal state เสมอ ไม่มี concurrency token เลย (ต่างจาก
`Payments.Domain.Session` ที่มี `RowVersion`) invariant การเงินเด่นสุดอยู่ที่ `Order.Create`: sum ของ
`OrderItemInput.UnitPrice * Quantity` ทุก line ต้องเท่ากับ `amount` ที่ส่งเข้ามาเป๊ะ (ArgumentException ถ้าไม่เท่า)
และทุก line ต้องอยู่ currency เดียวกับ order เท่านั้น `MarkPaid` idempotent — เรียกซ้ำตอน Status เป็น Paid อยู่แล้ว
คือ no-op (return false ไม่ throw) แต่ throw ถ้า order ถูก Cancel ไปแล้ว และก่อน mark ทุกครั้ง re-verify ทั้ง amount
กับ currency กับยอดของ order เอง ไม่เชื่อแค่ id จาก event เพียงอย่างเดียว (ดู B3 — `OrderPaidConsumer` consume
`PaymentPaid` แล้วเรียก `MarkPaid` ซ้ำได้อย่างปลอดภัยเมื่อ event replay) โมดูลนี้ยังมี sub-entity ใน `Items/`:
`Item` (order line, insert-only, snapshot ราคา+ข้อมูลผู้เอาประกัน ณ ตอนซื้อ) กับ `ItemPolicy` (1:1 mutable แยกออกจาก
`Item` โดยเจตนา เพราะถูกกรอกโดย operator หลังขาย คนละช่วงเวลากับตอนขาย — พ่วง audit trail ของตัวเอง
`ItemPolicyAudit`/`RevealAudit` ทั้งคู่ append-only)

**Application**: CQRS เต็มรูปแบบ ไม่ bypass Mediator เลย — `CreateOrderCommand`/`ResendOrderSummaryCommand`/
`UpsertItemPolicyCommand` (พร้อม admin escape-hatch twin `UpsertItemPolicyAdminCommand`) แยก handler ต่อ use-case
ปกติ รวมถึง `GetOrderDetailCommand` ที่ถูกโมเดลเป็น command ทั้งที่เป็น read เพราะมี side-effect เขียน `RevealAudit`
ก่อน build response แบบ fail-closed (throw ถ้า save ไม่ผ่าน ไม่ยอมส่งข้อมูลที่ audit ไม่สำเร็จออกไป) มี consumer สอง
handler รับ integration event ข้ามโมดูล: `CheckoutConfirmedConsumer` (B2 — เปิด order จาก checkout, idempotent ผ่าน
filtered unique index บน `CheckoutSessionId`) กับ `OrderPaidConsumer` (B3 — consume `PaymentPaid` แล้ว re-verify
amount/currency ผ่าน `MarkPaid` ก่อน mark, join ด้วย `OrderId` จาก event ไม่ใช่ `PaymentSessionId` — ดู "จุดสังเกต")
port หลักคือ `IOrderRepository`/`IItemPolicyRepository` (merchant-scoped ปกติ) กับ `IAdminItemPolicyWriter` (admin
cross-merchant escape-hatch, bind คนละ `MerchantRuntimeDbContext` instance แยกจาก context ฝั่ง merchant-scoped) —
**ไม่มี cross-module `.Domain` reference เลย**: `Orders.Application.csproj` reference แค่ `Orders.Domain`/
`Contracts`/`BuildingBlocks.Application` เท่านั้น แม้จะมี admin escape-hatch ก็ส่ง accessible-merchant decision
เป็น plain data (`IsUnrestrictedAdmin`/`AccessibleMerchantIds`) แทนที่จะ reference `Admins.Application` ตรงๆ —
คอมเมนต์ในโค้ด (`UpsertItemPolicyAdminCommand.cs`) เขียนไว้ว่าไม่มีโมดูลไหนในเรโปนี้ reference Application project
ของโมดูลอื่นเลย แต่คำกล่าวนั้นตกหล่นจริง: `Merchants.Application` reference `Payments.Application` ตรงๆ อยู่ (ข้อยกเว้น
เดียวในระบบ — ดู #6) กฎเคร่งครัดนี้ยึดจริงแค่ฝั่ง Orders.Application เอง ไม่ใช่ภาพรวมทั้งระบบ

**Infrastructure**: `OrdersModuleRegistration.cs` เป็น `=> services` เปล่าจริง — Mediator handler ถูก
auto-discover โดย source generator ที่ host อยู่แล้ว ส่วน repository/summary reader ตัวจริงย้ายไปขึ้นทะเบียนที่
`Persistence.MerchantRuntime` (`AddMerchantRuntimePersistence`) แทน ไฟล์ที่มี body จริงในโปรเจกต์นี้คือ EF config
(`OrderConfiguration` + อีก 4 ไฟล์ใน `Items/`) แต่เป็นแค่ "migration-owner" — กำหนด column/index/schema (`shop`)
เท่านั้น ส่วน tenant-key/query-filter/append-only wiring ตัวจริงอยู่ใน runtime twin คนละไฟล์ที่
`Persistence.MerchantRuntime.Orders.Items.*` (dual-config pattern เดียวกับที่ใช้ทั้งเรโป)

**จุดสังเกต**: `Order.PaymentSessionId`/`AttachPaymentSession` เป็น legacy link ที่ "ไม่มี production writer" —
คอมเมนต์ในโค้ดบอกตรงๆ ว่า `OrderPaidConsumer` resolve order ด้วย `OrderId` จาก event เท่านั้น ไม่เคยใช้ field นี้
เป็น join key จริง (bugfix-order-paid-link F2) คนใหม่ที่เห็น field แล้วคิดว่าเป็น join path หลักของการ fulfil order
จะหลงทาง — และแม้ `OrdersModuleRegistration.cs` จะ `=> services` เปล่าเหมือน `Divisions`/`Iam` แต่คนละเหตุผล:
Orders ไม่ใช่ reference-data module เลย เป็น core business aggregate จริง แค่ persistence wiring ถูกรวมศูนย์ไปอยู่
`Persistence.MerchantRuntime` แทนที่จะอยู่ในโมดูลของตัวเอง

#### 5. Payments

**Domain**: `Session.cs` (179 บรรทัด) — aggregate root กับ state machine 5 states ทางเดียวเข้า terminal state
เสมอ (`Created → Redirected → Paid`/`Failed`/`Expired` บน `SessionStatus`; `MarkFailed`/`MarkExpired` throw ถ้า
Status เป็น terminal อยู่แล้ว) ใช้ SQL Server `RowVersion` เป็น optimistic-concurrency token — comment ในโค้ด
ระบุเหตุผลตรงๆ ว่ามีไว้ serialise การ "claim the redirect": `BeginRedirect` (Created→Redirected) ต้อง save
สำเร็จก่อนเสมอถึงจะไปสร้าง PSP charge ได้ ฝั่งที่แพ้ concurrency check ไม่มีทางเรียก PSP เลย กัน 2 request สร้าง
charge ซ้อนกัน (ดู B4 เต็มๆ) invariant เด่นอีกจุดคือทุก field (merchant/order/amount/method/psp) ผูกตั้งแต่
`Create` ครั้งเดียว ไม่มี "attach-race" ภายหลัง และ `SetPspCharge` bind PSP charge id ได้ครั้งเดียวเท่านั้น
(throw ถ้ามี `PspExternalChargeId` ผูกอยู่แล้ว) `MarkPaid` idempotent พร้อมเช็ค charge-id: เรียกซ้ำด้วย charge-id
เดิมตอน Paid อยู่แล้วคือ no-op แต่ถ้า charge-id ไม่ตรงกับที่ mark ไว้ก่อนหน้า throw ทันที ไม่ให้ paid event ของ
charge อื่นมา mark session ผิดตัว

**Application**: CQRS เต็มรูปแบบ — `CreateSessionCommand`/`StartRedirectCommand`/`HandlePspWebhookCommand`/
`GetSessionQuery` แยก handler ต่อ use-case ไม่ bypass `CreateSessionHandler` เด่นสุด: ลำดับการเช็คใน handler
เป็น contract ตรงๆ ตามคอมเมนต์ในโค้ด (400 malformed method, 404 order ไม่พบหรือเป็นของ merchant อื่นภายใต้ query
filter — สองกรณีนี้ตั้งใจให้แยกไม่ออกจากภายนอก, 409 ทุกกรณี server-state refuse) ห้ามสลับลำดับเพราะ status code
ที่ caller เห็นขึ้นกับลำดับนี้ตรงๆ และ dedupe open session ต่อ order ด้วย `GetOpenForOrderAsync` ที่คืน entity
ไม่ใช่ bool เพราะต้องเทียบ method+PSP: ช่องทางเดิมคืน session เดิมให้ resume ได้ ช่องทางอื่น 409 เพราะไม่มี void ที่
PSP ให้ swap channel กลางทาง port หลักคือ `IPayableOrderReader`/`IConnectionRepository`/`IPspAdapterFactory`/
`ISessionRepository` บวก `IPspSecretEnvelopeFactory` — ไม่มี cross-module `.Domain` reference
(`Payments.Application.csproj` reference แค่ `Payments.Domain`/`Contracts`/`BuildingBlocks.Application`)
`IPayableOrderReader` เจาะจงคืนแค่ amount+awaiting-payment flag เท่านั้น (ไม่มี line/PII) — comment ยืนยันตรงๆ
ว่าเจตนาไม่ให้ merchant-facing order-detail read (ที่เขียน reveal audit) มาปนกับ payment path

**Infrastructure**: 1 ใน 2 โมดูลที่ `PaymentsModuleRegistration.cs` มี body จริง (ไม่ใช่ `=> services` เปล่า —
อีกโมดูลคือ Merchants, ดู #6) — register named pooled `HttpClient` ต่อ PSP connection (`TwoCTwoP`/`Omise`,
timeout 30 วินาทีต่อ call, ไม่ retry ตอน create-charge เพราะเสี่ยง double-charge — retry เก็บไว้แค่ฝั่ง fetch GET),
`IPspAdapter` 2 ตัว + `IPspAdapterFactory` (registered เป็น singleton เพราะ adapter stateless — state ทั้งหมดอยู่
ใน method args) เพราะต้อง resolve adapter ให้ตรง PSP ของแต่ละ merchant connection ตอน runtime ไม่ใช่ DI แบบ
static เดียว บวก `IPspSecretEnvelopeFactory` (singleton เช่นกัน ใช้ตอน merchant provisioning)

**จุดสังเกต**: เป็นโมดูลเดียวที่มี test subfolder เฉพาะ external-provider (`tests/Payments.Tests/Psp/` —
`TwoCTwoPAdapterTests`/`OmiseAdapterTests`/`PspTestHttp`; โมดูลอื่นทั้งหมดมีแค่ `obj`/`bin` ใต้ test project) —
สะท้อนว่าเป็นจุดที่ระบบพึ่งพา third-party contract มากที่สุด ต้อง mock/replay เยอะกว่าโมดูลอื่น

#### 6. Merchants

**Domain**: ดูตัวอย่างเดินเต็ม vertical slice ของโมดูลนี้ด้านบนในหัวข้อนี้แล้ว (`Merchant.cs` factory) —
ไม่ขอย้ำซ้ำ

**Application**: ดูตัวอย่างด้านบนเช่นกัน (`ProvisionMerchantCommand`/`Handler`) — เสริมจุดที่ตัวอย่างเดิม
ยังไม่พูดตรงๆ: `Merchants.Application.csproj` reference `Payments.Application.csproj` ตรงๆ (ไม่ใช่แค่
`.Domain`) ซึ่งเป็นข้อยกเว้นเดียวในระบบที่เปิดให้ Application project หนึ่งอ้างอิง Application project ของอีก
โมดูลตรงๆ (คนละแกนกับกฎ published-language ที่สงวนไว้เฉพาะ `Iam.Domain` — ดู #8) — `ProvisionMerchantHandler`
ต้องสร้าง PSP connection ของ Payments ตรงจุดเดียวตอน provision merchant ใหม่

**Infrastructure**: มี body จริง (ไม่ใช่ `=> services` เปล่า) — `AddMerchantsModule()`
(`MerchantsModuleRegistration`) register `IPhotoStore` default (`LocalPhotoStore`, เขียนไฟล์ลง temp
directory ด้วย opaque key กัน path traversal) ให้ worker/local ใช้; Api override ด้วย adapter ที่อ่าน path
จาก config เอง (ตามคอมเมนต์ในไฟล์ registration)

**จุดสังเกต**: Domain มีจริง 3 aggregate ไม่ใช่แค่ `Merchant` ที่เดินตัวอย่างด้านบน (`User`/`Session` — merchant-user
identity + BFF session — แยกเต็ม) และ Application มี 2 คู่ port หน้าตาคล้ายกันแต่ใช้คนละที่โดยเจตนา:
`IUserRepository` (bound, ผ่าน merchant query filter ปกติ) กับ `IAccountResolver`/`IAccountStore` (filter-free,
สำหรับ flow ที่ยังไม่มี merchant actor ผูกกับ request เช่น login/registration/approve-reject) — สลับใช้คู่ผิด
แล้วแถวที่ `MerchantId` เป็น NULL (pending/rejected) จะหายไปเงียบๆ จาก query

#### 7. Admins

**Domain**: `User.cs` (146 บรรทัด) เป็น aggregate หลักของโมดูล — ไม่มี state machine เชิง sequence แบบ Payments'
`Session` แต่ปกครองด้วยสอง enum คู่กัน: `Tier` (Scoped/Super) กับ `UserStatus` (Active/Suspended เท่านั้น —
ตั้งใจไม่มี `PendingApproval` เพราะ admin ถูก bootstrap จาก allowlist หรือสร้างโดย Super เท่านั้น ไม่ผ่าน
self-approve แบบ merchant-user flow). Concurrency token คือ `AuthorizationVersion` (long counter ที่ bump เอง
ผ่าน `BumpAuthorizationVersion()`) ไม่ใช่ SQL Server `RowVersion` อัตโนมัติแบบ Payments — เป็น "authorization
lease" pattern จาก rls-to-query-filter REQ-4.11: ทุก write ที่กระทบ effective authorization ของ admin คนนั้น
(Status/Tier/Session/MerchantAccess/RoleAssignment/RolePermission) ต้อง bump version ในทรานแซกชันเดียวกัน
เพื่อให้ caller ที่ถือ version เก่าหลุดจาก in-tx authorization lease ที่ฝั่งอื่นเช็ค invariant เด่นอื่น:
`Suspend`/`ChangeTier` ปฏิเสธ self-target เสมอ (กัน Super คนเดียวลด tier ตัวเองจนไม่เหลือใคร oversight หรือ
suspend ตัวเองจนล็อกตัวเองออก) และ `BindSubject` เป็น one-shot — rebind ถูก reject เพราะ `Subject` (Google
`sub`) unique ทันทีที่ bind แล้ว ส่วน `Email` คือ invite key ก่อนหน้านั้น

`Session.cs` เป็นอีก aggregate แยกต่างหาก (ไม่ใช่ child ของ `User`) — family-based rotation state machine 3
states (Active/Superseded/Revoked), เก็บแค่ SHA-256 hash ของ cookie ไม่เก็บ token ดิบ; `Rotate` ออก successor
ใน family เดิมพร้อม mark ตัวเองเป็น Superseded และลิงก์ไป successor เพื่อให้ replay ของ token ที่ไม่ใช่
predecessor ตัวล่าสุดตรวจจับได้ว่าเป็น theft — แต่ transition จริงทำผ่าน atomic set-based UPDATE ที่ store
(`TrySupersedeAsync`) ไม่ใช่ mutate entity ที่ tracked เพราะ rotate/revoke แข่งกันได้ ยังมี `MerchantAccess`
(soft reference ไป Merchant ไม่มี DB FK — เป็น lookup table ของ RLS predicate ฝั่ง platform) กับ
`RoleAssignment` (mirror `MerchantAccess` เป๊ะ unique บน (PlatformUserId, RoleId)) และ audit สองตัวที่แยกกัน
จงใจ: `Audit` (ต้องมี actor เสมอ) กับ `AuthAudit` (actor เป็น optional เพราะ auth event บางอันเกิดก่อน resolve
admin ได้ เช่น allowlist denial)

**Application**: CQRS เต็มรูปแบบผ่าน Mediator (`ICommand`/`ICommandHandler`, `IQuery`/`IQueryHandler` แยกไฟล์
ต่อ use-case เหมือน Payments) — command เช่น `SelfProvisionSuperCommand`/`CreateScopedCommand`/
`ChangeAdminTierCommand`/`SetAdminRolesCommand`, query เช่น `ResolveQuery`/`ListAdminsQuery`/
`GetAdminByIdQuery`. Port หลัก: `IUserRepository` (bind ไป pol_admin bypass connection เพราะ resolve/provision
ข้าม merchant ได้), `ISessionStore` (atomic set-based transition), `IRoleRepository` ("resolution repository"
ที่อ่าน `iam.Roles` ตรงผ่าน `Iam.Domain` type — catalog CRUD ย้ายไป `Iam.Application` แล้วตาม rf2),
`IAuditWriter`/`IAuthAuditWriter`, `IAdminScope` (per-request resolved-admin holder ที่ host เขียนใน
middleware), และ `IAdminMerchantDirectory` (host implement เหนือ pol_admin connection กัน
Admins.Application ไม่ต้อง reference โมดูล Merchant ตรงๆ — mirror pattern ของ Identity เดิม)

Cross-module `.Domain` reference: อ้าง `Iam.Domain` ตรงๆ (เช่น `SelfProvisionSuperHandler` ใช้
`Iam.Domain.Roles.Role.PlatformAdminCode` ตอน bootstrap ผูก role ให้ account แรก) — คอมเมนต์ใน `.csproj`
ระบุเหตุผลตรงๆ ว่า `Iam.Domain` เป็น "published language" (rf2, design.md) จึงอ้างได้เหมือน `SharedKernel`
แต่ห้ามแตะ `Iam.Application`/`Iam.Infrastructure` เด็ดขาด ที่น่าสังเกตคือ Application กลับ "ไม่" reference
4 โมดูล reference-data (Divisions/Levels/Offices/Positions) เลยแม้แต่ `.Domain` — พอร์ต `IProfileLookup`
ออกแบบเป็น enum-keyed (`ProfileField.Position/Office/Level/Division`) แทนการอ้าง type ตรงๆ ตามคอมเมนต์
masterdata-split design.md ที่บอกตรงๆ ว่า "Admins.Application references no reference-data module at all"

**Infrastructure**: `AdminModuleRegistration.cs` เป็น `=> services` เปล่าเหมือนส่วนใหญ่ของระบบ — มีไว้บังคับ
assembly นี้ให้โหลดเพื่อให้ EF configuration (`Users`/`MerchantAccess`/`UserAudits`/`RoleAssignments`) ถูก
discover ตอน model-build ผ่าน `HostModuleAssemblies.All` เท่านั้น คอมเมนต์บอกตรงๆ ว่า "NO repositories are
registered here" เพราะทุก seam ของ admin ผูกกับ keyed pol_admin `DbContext` ที่ host รู้จักเท่านั้น
(resolve/provision ข้าม merchant ได้ — reuse keyed `"admin"` scope เดียวกับที่สร้างไว้ให้ Merchant
provisioning) แต่ตัว `.csproj` กลับเป็นจุดที่กว้างที่สุดในระบบ: reference `Iam.Domain` +
`Divisions.Domain` + `Levels.Domain` + `Offices.Domain` + `Positions.Domain` พร้อมกันทั้ง 5 โมดูล (ไม่มี
โมดูลอื่นอ้าง `.Domain` ข้ามมากเท่านี้) เพราะ `UserConfiguration.Configure` ต้อง `HasOne<Position/Office/
Level/Division>()` ผูก FK ของ 4 org-profile field ตรงๆ — คอมเมนต์ในไฟล์ยืนยันว่าเป็น "the ONLY Admins layer
that names the four reference entity types" — บวก `RoleAssignmentConfiguration` ที่ต้อง `HasOne<Role>()`
ผูกไป `iam.Roles`

**จุดสังเกต**: XML doc บน `User.AuthorizationVersion` (Domain) ยังเขียนไว้ว่า "Not yet a real column (task
8's migration adds it) … setting it today is a safe no-op" แต่ `UserConfiguration` (Infrastructure) ที่
mark `.IsConcurrencyToken()` ให้มันกลับระบุตรงๆ ว่า "rls-to-query-filter REQ-4.9/4.11 (task 8): real column"
— คอมเมนต์สองไฟล์ไม่ sync กัน (ตกค้างจากตอนฟีเจอร์ landed เป็นสองช่วง) คนใหม่ที่อ่านแค่ Domain อาจเข้าใจผิดว่า
concurrency token ยังไม่ทำงานจริงทั้งที่ Infrastructure ผูกมันเป็น real column แล้ว นอกจากนี้คอมเมนต์เดียวกัน
ยังบอกว่า config นี้ "mirrors Persistence.ControlPlane's own `UserConfiguration`" — คือมี EF config ของ
`User` อยู่คู่ขนานนอกโมดูลนี้อีกชุด (คนละ `DbContext`) ต้องแก้ทั้งสองที่เวลาผัง schema เปลี่ยน ไม่ใช่แก้จุด
เดียวแล้วจบ

#### 8. Iam

**Domain**: `Role.cs` (164 บรรทัด) — aggregate เดียวของโมดูล ไม่มี state machine เชิงลำดับ มีแค่ `RoleStatus`
toggle อิสระผ่าน `Activate`/`Deactivate` (ไม่ใช่ transition ที่มีทิศทางบังคับ) และไม่มี concurrency token
Invariant เด่นสุดคือ `SetPermissions`: permission key ที่ grant ต้องอยู่ใน catalog (`Keys.KeySide`) **และ**
ต้องอยู่ scope เดียวกับตัว role เอง (Platform/Merchant) ไม่งั้น `ArgumentException` — ปิดช่องทาง cross-side
grant ด้วยโครงสร้าง ไม่ใช่ convention. `Code` เป็น identity immutable ผ่าน regex `^[a-z0-9_]+$`, `Scope`
immutable ตั้งแต่ `Create`, และ Platform-scope role ห้ามมี `MerchantId` — enforce ซ้อน 2 ชั้น (domain
constructor + DB `CHECK` constraint `CK_Roles_ScopeMerchant`). สอง seed anchor กู้ระบบ (`platform_admin`/
`merchant_manager`, `IsSeedAnchor`) ห้าม deactivate/delete ตลอดไป และ anchor ต่อ "แถวที่ seed จริง"
(`MerchantId` null + code ตรง) ไม่ใช่ต่อ code เฉยๆ — merchant สร้าง role ชื่อ `merchant_manager` ของตัวเองซ้ำ
ได้ (unique index bucket ด้วย `MerchantId`) แล้ว role นั้น deactivate/delete ได้ปกติ. `RolePermission` เป็น
child entity สร้างได้เฉพาะผ่าน aggregate (constructor `internal`), ส่วน `Permission`/`PermissionGroup` เป็น
reference-data class เปล่า (seed จาก migration, ไม่มี invariant) และ `RoleVisibility.For` คือ predicate
เดียวที่นิยาม "ใครเห็น role แถวไหน" ไว้ที่นี่ที่เดียว ให้ทั้ง `Iam.Infrastructure` เองและ
`Admins`/`Merchants.Infrastructure` ที่ query `iam.Roles` ตรงก็ใช้ตัวเดียวกัน

**Application**: CQRS เต็มรูปแบบผ่าน Mediator ปกติ — `CreateRoleCommand`/`UpdateRoleCommand`/`DeleteRoleCommand`
+ `ListRolesQuery`/`GetRoleQuery`/`GetPermissionCatalogQuery` แยก handler ต่อ use-case (ไม่ bypass). Port หลัก
คือ `IRoleStore` (persistence ที่ scope ด้วย `RoleSideContext` ทุก read — ไม่มี "get by code" แบบไม่ scope
หลงเหลือเลย) บวกกับ port เชื่อมข้ามโมดูลอีก 2 ตัวที่มีอยู่เพราะกฎ module-reference: `IRoleAssignmentCounter`
(นับแถว assignment ทั้งฝั่ง admin/merchant, comment ในโค้ดบอกตรงๆ ว่า "mirrors `IRoleAuditSink`'s bridge
pattern") และ `IRoleAuditSink` (default เป็น no-op `NullRoleAuditSink`) — ทั้งคู่มีเพราะ `Iam.Application`
ห้าม reference `Admins.Application`/`Domain` หรือ `Merchants.Application`/`Domain` ตรงๆ ผลคือ
`Iam.Application.csproj` ไม่มี `ProjectReference` ไปโมดูลธุรกิจอื่นเลย (มีแค่ `Iam.Domain` +
`BuildingBlocks.Application`) — สวนทางกับทิศทางอื่น: `Iam.Domain` เองถูก `Admins.Application`/
`Admins.Infrastructure`/`Merchants.Infrastructure`/`Persistence.ControlPlane`/`Persistence.MerchantUsers`
reference ตรงในฐานะ published language (ตามกฎ module-reference ที่คอมเมนต์ในโค้ดระบุตรงๆ ว่า "only
`Iam.Domain` is a published language" — ไม่มีโมดูลอื่นใดในระบบถือสถานะนี้) ทั้ง 2 bridge port ต้อง implement
ที่ host (`Hosts/Api/Iam/RoleHostWiring.cs`) ซึ่งเป็นจุดเดียวที่เห็นทั้ง `IAdminScope`/`IUserScope` พร้อมกัน —
จุดเดียวกันนี้เองที่ derive `RoleSideContext` (record `Scope`+ `MerchantId?`) ให้ทุก command/query รับจาก
caller ตรงๆ แทนที่จะ derive เอง กัน client สวม scope ผ่าน wire body

**Infrastructure**: `IamModuleRegistration.AddIamModule()` เป็น `=> services` เปล่าเหมือน
`Divisions`/`Levels`/`Offices`/`Positions` — comment ในโค้ดบอกเหตุผลตรงๆ ว่ามีไว้ "forces this assembly to
load so its EF configurations (Permissions, PermissionGroups, Roles, RolePermissions) are discovered at
model-build time via `HostModuleAssemblies.All`" เท่านั้น ไม่ได้ register repository ใดๆ ในนี้เลย โปรเจกต์นี้
มีแค่ `IEntityTypeConfiguration` 4 ตัว (`RoleConfiguration`/`RolePermissionConfiguration`/
`PermissionConfiguration`/`PermissionGroupConfiguration`) กับ SFS pipeline (`RoleSfs.cs`) — `IRoleStore`
ตัวจริง (`RoleStore.cs`) ไม่ได้อยู่ใน `Iam.Infrastructure` ด้วยซ้ำ อยู่ที่ `Persistence.ControlPlane` และ bind
แบบ unkeyed ผ่าน `AddControlPlanePersistence` แทน

**จุดสังเกต**: ไม่ถูกเรียกผ่าน `Add{Module}Module()` ใน `Program.cs` เลย (เหมือน `Divisions`/`Levels`/
`Offices`/`Positions`) — wiring จริงที่ `Program.cs` เรียกคือ `AddIamRoleManagement()` (จาก
`Api.Iam.RoleHostWiring`) ซึ่ง register แค่ `IRoleAssignmentCounter`/`IRoleAuditSink` สองตัวเท่านั้น (comment
ในไฟล์เดียวกันบอกด้วยว่า "`IRoleStore` is no longer registered here: `AddControlPlanePersistence` already
registers it unkeyed, shared by both hosts"). คนใหม่ที่ grep หา `AddIamModule` ใน `Program.cs` จะไม่เจอเลย
ต้องรู้ว่า EF discovery มาจาก assembly-scan ของ `HostModuleAssemblies.All` ไม่ใช่จาก DI call ตรงๆ และต้องรู้
เพิ่มว่า business logic หลักของโมดูล (`IRoleStore`) ไปโผล่อยู่ที่ `Persistence.ControlPlane` ไม่ใช่
`Iam.Infrastructure`

#### 9. Divisions

**Domain**: `Division.cs` (48 บรรทัด, เหมือน `Level`/`Office`/`Position` เป๊ะ) — ไม่มี state machine, invariant
เดียวคือ `Code` ต้องผ่าน `^[a-z0-9_]+$` แล้ว immutable หลังสร้าง (identity), `IsActive` toggle ได้อิสระ

**Application**: **bypass Mediator ทั้งหมด** — มีแค่ `IDivisionStore` interface เดียว (ไม่มี Command/Handler
แยกไฟล์แบบโมดูลธุรกิจ) คอมเมนต์ในโค้ดให้เหตุผลตรงๆ ว่าเป็น "reference data ธรรมดา จึงตั้งใจ bypass Mediator"
แต่ยังคอมมิตผ่าน keyed `"admin"` `IUnitOfWork` เดิม ไม่ใช่ทางลัดที่ข้าม transaction boundary

**Infrastructure**: `=> services` เปล่า (เหมือน `Iam.Infrastructure`) — `IDivisionStore` ตัวจริง bind อยู่ที่
`Persistence.ControlPlane.ControlPlanePersistenceRegistration.AddControlPlanePersistence` แทน ไม่ใช่ที่นี่

**จุดสังเกต**: ไม่ถูกเรียกใน `Program.cs` ผ่าน `Add{Module}Module()` เลย (เหมือน `Levels`/`Offices`/`Positions`/
`Iam`) — คนใหม่ที่ grep หา wiring ใน `Program.cs` จะหาไม่เจอ ต้องรู้ว่า wiring จริงมาทาง
`HostModuleAssemblies.All` + `AddControlPlanePersistence` นอกจากนี้ (เหมือนกับ `Levels`/`Offices`/`Positions`
ทุกโมดูลในตระกูลนี้ — รายละเอียดเต็มดู #10) `DivisionConfiguration` มี 2 คลาสคนละ namespace ผูกกับคนละ
`DbContext` (`Divisions.Infrastructure.Persistence.DivisionConfiguration` กับ
`Persistence.ControlPlane.Divisions.DivisionConfiguration`) mapping เหมือนกันทุก field ที่ต้อง sync มือ —
คอมเมนต์ในโค้ดเตือนตรงๆ ว่า "must stay in lockstep"

#### 10. Levels

**Domain**: `Level.cs` (48 บรรทัด, เหมือน `Division`/`Office`/`Position` เป๊ะ) — ไม่มี state machine ไม่มี
concurrency token, invariant เดียวคือ `Code` ต้องผ่าน `^[a-z0-9_]+$` แล้ว immutable หลังสร้าง (identity) ส่วน
`Name`/`IsActive` แก้ได้อิสระผ่าน `Rename`/`Activate`/`Deactivate` — คอมเมนต์ในโค้ดยืนยันว่าเป็น "standalone
aggregate since masterdata-split — the retired shared base logic lives inline, verbatim"

**Application**: bypass Mediator ทั้งหมด เหมือน Divisions — มีแค่ interface `ILevelStore` ตัวเดียว (ไม่มี
Command/Handler แยกไฟล์) คอมเมนต์ในโค้ดให้เหตุผลตรงๆ คำต่อคำเดียวกับ Divisions ว่า "Simple control-plane
reference data, so it deliberately bypasses Mediator" แต่ยังคอมมิตผ่านกฎเดิมของ keyed `"admin"` `IUnitOfWork`
csproj อ้างอิงแค่ `Levels.Domain` + `BuildingBlocks.Application` ไม่มี cross-module `.Domain` reference ใดๆ

**Infrastructure**: `AddLevelsModule()` คืน `=> services` เปล่า (เหมือน Divisions/Iam) — `ILevelStore` ตัวจริง
bind อยู่ที่ `Persistence.ControlPlane.ControlPlanePersistenceRegistration.AddControlPlanePersistence` แทน
(implementation class `Persistence.ControlPlane.Levels.LevelStore` คอมมิตผ่าน `ControlPlaneUnitOfWork` จริง
พร้อม pre-check ก่อน `ConflictException` 409 เผื่อ unique-index race)

**จุดสังเกต**: ไม่ถูกเรียกใน `Program.cs` ผ่าน `Add{Module}Module()` เลย (เหมือน `Divisions`/`Offices`/
`Positions`/`Iam`) — wiring จริงมาทาง `HostModuleAssemblies.All` (ให้ `PolDbContext` เจอ `LevelConfiguration`
ผ่าน `ApplyConfigurationsFromAssembly`) + `AddControlPlanePersistence` (bind `ILevelStore` จริง) — เหมือนกับ
`Divisions`/`Offices`/`Positions` ทั้ง 3 โมดูล ไม่ใช่จุดเด่นเฉพาะ `Levels`. `LevelConfiguration` มี 2 คลาสคนละ
namespace ผูกกับคนละ `DbContext` จริง — `Levels.Infrastructure.Persistence.LevelConfiguration` (apply เข้า
`PolDbContext` ผ่านการสแกน assembly) กับ `Persistence.ControlPlane.Levels.LevelConfiguration` (apply ตรงใน
`ControlPlaneDbContext.OnModelCreating`) mapping เหมือนกันทุก field แต่เป็นคนละไฟล์ที่ต้อง sync มือ — คอมเมนต์
บอกตรงๆ ว่า "must stay in lockstep" แก้ field ที่ไฟล์เดียวแล้วลืมอีกไฟล์คือ schema drift เงียบข้าม migration
ความเสี่ยงนี้เกิดกับทั้ง 4 โมดูลอ้างอิงเท่ากัน

#### 11. Offices

**Domain**: `Office.cs` (48 บรรทัด, เหมือน `Division`/`Level`/`Position` เป๊ะ) — ไม่มี state machine, invariant
เดียวคือ `Code` ต้องผ่าน `^[a-z0-9_]+$` แล้ว immutable หลังสร้าง (identity), `IsActive` toggle ได้อิสระผ่าน
`Activate`/`Deactivate`

**Application**: **bypass Mediator ทั้งหมด** — มีแค่ `IOfficeStore` interface เดียว (ไม่มี Command/Handler
แยกไฟล์แบบโมดูลธุรกิจ) คอมเมนต์ในโค้ดให้เหตุผลตรงๆ ว่าเป็น "control-plane reference data ธรรมดา" จึงตั้งใจ
bypass Mediator แต่ยังคอมมิตผ่าน keyed `"admin"` `IUnitOfWork` เดิม ไม่ใช่ทางลัดที่ข้าม transaction boundary;
ไม่มี cross-module `.Domain` reference (แค่ `SharedKernel` + `BuildingBlocks.Application`) — คอมเมนต์เตือน
เพิ่มด้วยว่า existence/lookup สำหรับ admin-profile FK ไม่ได้อยู่ที่นี่ นั่นเป็น port ของฝั่ง caller เอง
(`Admins.Application.Users.IProfileLookup`)

**Infrastructure**: `=> services` เปล่า (เหมือน `Divisions`/`Levels`/`Iam`) — มีไว้บังคับให้ assembly นี้ถูกโหลด
เพื่อให้ EF config ของ `cfg.Offices` ถูก discover ตอน model-build ผ่าน `HostModuleAssemblies.All`; `IOfficeStore`
ตัวจริง bind อยู่ที่ `Persistence.ControlPlane.ControlPlanePersistenceRegistration.AddControlPlanePersistence`
แทน ไม่ใช่ที่นี่

**จุดสังเกต**: ไม่ถูกเรียกใน `Program.cs` ผ่าน `Add{Module}Module()` เลย (เหมือน `Divisions`/`Levels`/
`Positions`/`Iam`) — grep หา `AddOfficesModule` ใน `Program.cs` จะหาไม่เจอ เจอแค่ reference ไปที่ assembly
เฉยๆ ใน `DesignTimeDbContextFactories.cs`; ต้องรู้ว่า wiring จริงมาทาง `HostModuleAssemblies.All` +
`AddControlPlanePersistence` เช่นเดียวกับ `Divisions`/`Levels`/`Positions` — `OfficeConfiguration` ก็มี 2
คลาสคนละ namespace ผูกกับคนละ `DbContext` ที่ต้อง sync มือเหมือนกัน (รายละเอียดเต็มดู #10)

#### 12. Positions

**Domain**: `Position.cs` (48 บรรทัด, เหมือน `Division`/`Level`/`Office` เป๊ะ แม้แต่ตัวเลขบรรทัด) — ไม่มี state
machine, invariant เดียวคือ `Code` ต้องผ่าน `^[a-z0-9_]+$` แล้ว immutable หลังสร้าง (identity), `IsActive`
toggle ได้อิสระ ไม่มี cross-module `.Domain` reference (`Positions.Domain.csproj` reference แค่ `SharedKernel`)

**Application**: **bypass Mediator ทั้งหมด** — มีแค่ `IPositionStore` interface เดียว คอมเมนต์ในโค้ดให้เหตุผล
ตรงๆ ว่าเป็น "control-plane reference data ธรรมดา จึงตั้งใจ bypass Mediator" แต่ยังคอมมิตผ่าน keyed `"admin"`
`IUnitOfWork` เดิม ไม่ใช่ทางลัดที่ข้าม transaction boundary — และย้ำชัดว่า lookup สำหรับ admin-profile FK ไม่ได้
อยู่ที่นี่ (เป็นของ `Admins.Application.Users.IProfileLookup` แทน เพราะเป็น caller need ไม่ใช่ use case ของ
โมดูลนี้)

**Infrastructure**: `=> services` เปล่า (เหมือน `Iam.Infrastructure`) — `IPositionStore` ตัวจริง bind อยู่ที่
`Persistence.ControlPlane.ControlPlanePersistenceRegistration.AddControlPlanePersistence` แทน มีไว้แค่บังคับ
ให้ assembly นี้ถูก load เพื่อให้ EF config ของ `cfg.Positions` ถูก discover ตอน model-build ผ่าน
`HostModuleAssemblies.All`

**จุดสังเกต**: เหมือน `Divisions` ทุกประการ ต่างกันแค่ชื่อ — รวมถึงไม่ถูกเรียกใน `Program.cs` ผ่าน
`AddPositionsModule()` เลย (เหมือน `Divisions`/`Levels`/`Offices`/`Iam`) wiring จริงมาทาง
`HostModuleAssemblies.All` + `AddControlPlanePersistence` เท่านั้น รวมถึง `PositionConfiguration` 2 คลาสคนละ
namespace/`DbContext` ที่ต้อง sync มือแบบเดียวกัน (ดู #10)

---

## 6. Hosts — จุดเดียวที่ประกอบทุกอย่างเข้าด้วยกันแล้วรันจริง

**คืออะไร**: composition root — host เดียวในทั้งระบบคือ `Hosts/Api` (`Api.csproj`). เดิมมี host `Worker` แยกไว้รัน background job แต่ถูก retire ไปแล้วทั้งตัว (spec `multi-tier-deployment`, 2026-07-22) — โค้ดที่เคยอยู่ใน Worker ถูกย้ายเข้ามาเป็น `IHostedService` ในโปรเซส `Api` เดียวกัน วันนี้เหลือแค่ 2 deploy image: `api` กับ `migrate` (ไม่มี `worker`). ถ้าเจอโฟลเดอร์ `src/Hosts/Worker` บนเครื่อง dev อย่าตกใจ — `git ls-files src/Hosts/Worker` คืนค่าว่างเปล่าเสมอ (ไม่มีไฟล์ track ใน git แล้ว) นั่นคือซาก `bin/`/`obj/` จาก local build เก่าก่อน retire เท่านั้น ลบทิ้งได้ปลอดภัย ไม่ใช่โปรเจกต์ที่ยังใช้งานจริง.

**บทบาท**: จุดเดียวในทั้งระบบที่ reference ได้ **ทุกอย่างพร้อมกัน** — `Contracts`, ทั้ง 3 project ของ `BuildingBlocks`, ทั้ง 12 โมดูล (`.Application`+`.Infrastructure`), ทั้ง 4 `Persistence.*`. เป็นที่เดียวที่ผูก concrete implementation เข้ากับ port/interface ที่ทุก layer อื่นประกาศไว้ล่วงหน้า — โมดูลไม่รู้จัก EF Core, EF Core (Persistence) ไม่รู้จัก HTTP, Host คือที่เดียวที่รู้จักทุกฝ่ายแล้วผูกให้.

**ถ้าไม่มีชั้นนี้**: ไม่มีที่ไหนผูก concrete implementation เข้ากับ port ที่ Modules/BuildingBlocks ประกาศไว้ —
ระบบ compile ผ่านได้เพราะ interface ครบ แต่ boot ไม่ได้จริงเพราะไม่มีใคร register `IWriteAuthorizer`/
`IActorContext` ให้ DI container เห็น ตัวอย่างจริงว่าจุดผูกนี้ critical แค่ไหนคือ B6 — บั๊กเกิดเพราะจุดผูกใน
`WriteAuthorizers.cs`/`HttpActorContext.cs` (ทั้งคู่อยู่ที่ Hosts) ไม่ตรงกัน ทำให้ login flow พังเงียบ ๆ โดยไม่มี
error ให้เห็นตรง ๆ.

**ทำงานยังไง — เดินลำดับการประกอบใน `Program.cs`**:

1. `AddMediator` (source-generated) + register `MerchantGuardBehavior<,>` เป็น global pipeline behavior
2. `AddBuildingBlocksInfrastructure()` + security telemetry + resolve connection string **เดียว** (`ConnectionStrings:App` — ทั้งระบบใช้ SQL principal เดียวคือ `pol_app` หลัง RLS teardown ไม่มีแยก role ต่อ context อีกแล้ว)
3. เรียก `Add<Module>Module()` ของแต่ละโมดูล (ส่วนใหญ่เกือบว่างเปล่าแล้วตามที่อธิบายใน §5)
4. `AddControlPlanePersistence`/`AddMerchantUserPersistence`/`AddMerchantRuntimePersistence`/`AddProvisioning` — ตรงนี้เองที่ `IWriteAuthorizer` แต่ละ context ถูกผูก implementation จริง (`Persistence/WriteAuthorizers.cs` ของ Host เอง) โดยเลือกผ่าน `BackgroundDispatchScope.IsHttpRequest(sp)` ว่า scope นี้มาจาก HTTP request จริง (ใช้ `HttpMerchantWriteAuthorizer`) หรือมาจาก background outbox dispatcher (ใช้ `WorkerWriteAuthorizer` — โค้ดที่ย้ายมาจาก Worker host เดิม) — **จุดนี้นั่งอยู่บน security boundary ตรง ๆ** จึงมี composition-root test แยกเฉพาะคุมไว้
5. Admin BFF + MerchantUser BFF — สองชุดขนานกัน (OIDC provider-scoped scheme, session cookie คนละชื่อ `__Host-adm_session`/`__Host-mch_session`, CSRF filter, rate limiter แยกกัน)
6. Middleware pipeline (เรียงลำดับนี้เสมอ): `ForwardedHeaders → CorrelationId → ExceptionHandler → StatusCodePages → Cors → RateLimiter → Authentication → Authorization → HealthChecks(/health/live, /health/ready — อยู่นอก /api/v1) → MapGroup("/api/v1") → endpoint ทั้งหมด`
7. Route surface: ทุกเส้นทางอยู่ใต้ `/api/v1/{area}` — **version มาก่อนเสมอ area มาทีหลัง** (global version segment เดียว ไม่แยก version ต่อโมดูล), audience (admin/merchant-user) ไม่เคยเข้ารหัสอยู่ใน path — บังคับผ่าน `.RequireAuthorization(...)`/`.RequirePermission(Keys.*)` ต่อ endpoint เท่านั้น

**ตัวอย่าง endpoint จริง** (`POST /products`, ย่อจาก `Program.cs`):

```csharp
var createProduct = api.MapPost("/products", async (CreateProductRequest body, IActorContext actor, IMediator mediator, CancellationToken ct) =>
{
    var id = await mediator.Send(new CreateProductCommand(actor.MerchantId, body.Name, body.Price, ...), ct);
    return TypedResults.Ok(new CreateProductResponse(id));
});
createProduct.RequireAuthorization("merchant-user").RequirePermission(Keys.ProductCreate)...
```

merchant id มาจาก `IActorContext` (resolve จาก principal) เสมอ ไม่เคยรับมาจาก request body หรือ URL — นี่คือกฎเดียวกับ §3 ที่ถูกบังคับใช้จริงตรงจุด mapping endpoint.

**ทำงานร่วมกับ layer อื่นตรงไหน**: `WriteAuthorizers.cs` และ `BackgroundDispatchScope.cs` ในหัวข้อนี้คือจุดที่ Hosts ผูก policy จริงให้ BuildingBlocks/Persistence ใช้ — ตัวอย่างที่เห็นผลชัดสุดคือ **B6** ซึ่งเป็นบั๊กจริงที่เกิดจากจุดผูกตรงนี้พังแล้วเพิ่งแก้ไปในบรานช์เดียวกับที่กำลังอ่านคู่มือนี้

---

## ตัวอย่าง flow จริงข้ามเลเยอร์

เดินโค้ดจริงทีละ layer 6 เคส ตั้งแต่ endpoint ง่ายสุดไปจนถึงบั๊กจริงที่เพิ่งเกิดบน repo นี้ — โค้ดทุกก้อนคัดมาจากไฟล์จริงตรง ๆ (ตัดคอมเมนต์ยาว/บาง statement ออกด้วย `...` เพื่อความอ่านง่าย ไม่ได้แก้ logic).

### B1. flow ง่ายสุด — สร้างสินค้า (`POST /products`)

ครบ 5 layer ในคำขอเดียว:

**1. Hosts** (`Program.cs:599-610`) map endpoint, ดึง merchant จาก `IActorContext` ไม่ใช่จาก body:
```csharp
var createProduct = api.MapPost("/products", async (
    CreateProductRequest body, IActorContext actor, IMediator mediator, CancellationToken ct) =>
{
    var id = await mediator.Send(
        new CreateProductCommand(
            actor.MerchantId, body.Name, body.Price, body.SumInsured, body.CoverageDurationDays, body.Insurer),
        ct);
    return TypedResults.Ok(new CreateProductResponse(id));
});
createProduct.RequireAuthorization("merchant-user").RequirePermission(Keys.ProductCreate)...
```

**2. BuildingBlocks** — ก่อน handler จะรัน `MerchantGuardBehavior<,>` (§3) เช็คก่อนว่า `CreateProductCommand` (implement `IMerchantScoped`) มี actor ผูกไหม ถ้าไม่มี throw `MerchantBindingException` ทันที ไม่ถึง handler เลย

**3. Modules** (`Products.Application/CreateProductCommand.cs`) — handler สร้าง aggregate ผ่าน domain factory แล้วสั่งเก็บ:
```csharp
public sealed record CreateProductCommand(
    Guid MerchantId, string Name, Money Price, Money SumInsured, int CoverageDurationDays, string Insurer)
    : ICommand<Guid>, IMerchantScoped;

public sealed class CreateProductHandler : ICommandHandler<CreateProductCommand, Guid>
{
    public async ValueTask<Guid> Handle(CreateProductCommand command, CancellationToken cancellationToken)
    {
        var product = Product.Create(
            command.MerchantId, command.Name, command.Price, command.SumInsured, command.CoverageDurationDays,
            command.Insurer, _clock.UtcNow);
        _repository.Add(product);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return product.Id;
    }
}
```
`Product.Create` (Domain) validate invariant ก่อนคืน object กลับมา — เช่น `SumInsured` ต้อง currency เดียวกับ `Price` (`sumInsured.SameCurrencyAs(price)`) ถ้าไม่ผ่านคือ `ArgumentException` ตั้งแต่ในโดเมนเลย ไม่ต้องรอถึงชั้น DB

**4. Persistence** (`Persistence.MerchantRuntime/Products/ProductRepository.cs`) — `_repository.Add` แค่ track entity เข้า `MerchantRuntimeDbContext.Set<Product>()`, ยังไม่เขียนจริงจนกว่า `_unitOfWork.SaveChangesAsync` จะเรียก `GuardedRuntimeDbContext.SaveChanges` (§3) ซึ่งเช็ค `IWriteAuthorizer.CanWrite(typeof(Product), Insert, command.MerchantId)` ก่อนปล่อยให้ EF commit จริง

**5. SharedKernel** — `Money` เดินทางไปกับ `Product` ตั้งแต่ domain (`Product.Price: Money`) ผ่าน DB (EF complex type `decimal(19,4)` + `char(3)`) จนถึง `CreateProductResponse` กลับไปหา client (serialize ผ่าน `MoneyJsonConverter` เป็น `{"amount":"1500.0000","currency":"THB"}`)

### B2. flow ข้ามโมดูลผ่าน event — checkout confirm เปิด order เอง

`Checkouts` ไม่รู้จัก `Orders` เลย คุยกันผ่าน `Contracts` (§2) + outbox (§3/§4) เท่านั้น:

**1. Modules (Checkouts)** — `ConfirmCheckoutHandler` transition state แล้ว enqueue event ในทรานแซคชันเดียว:
```csharp
public async ValueTask<ConfirmCheckoutResult> Handle(ConfirmCheckoutCommand command, CancellationToken cancellationToken)
{
    var session = await _repository.GetByIdAsync(command.CheckoutSessionId, cancellationToken) ?? throw ...;
    session.Confirm();
    var items = session.Items.Select(i => new CheckoutConfirmedItem(...)).ToList();
    _outbox.Enqueue(new CheckoutConfirmed(session.MerchantId, session.Id, session.Amount, ...));
    await _unitOfWork.SaveChangesAsync(cancellationToken);
    return new ConfirmCheckoutResult(session.Id, session.Status);
}
```

**2. Persistence** — `OutboxDispatcher` (`BackgroundService` รันใน process `Api`) poll ทุก 2 วินาที, lease แถวด้วย SQL ตรง ๆ กันหลาย instance แย่งกันประมวลผลแถวเดียวกัน:
```sql
UPDATE TOP (50) o SET o.LeaseOwner = @Owner, o.LeaseExpiresAt = @LeaseUntil, o.Attempts = o.Attempts + 1
OUTPUT inserted.Id
FROM txn.OutboxMessages AS o WITH (READPAST, UPDLOCK, ROWLOCK)
WHERE o.ProcessedAt IS NULL AND (o.LeaseExpiresAt IS NULL OR o.LeaseExpiresAt < @Now) AND o.Attempts < 8;
```
แล้ว deserialize payload กลับเป็น CLR type จาก dictionary คงที่ (`nameof(CheckoutConfirmed) -> typeof(CheckoutConfirmed)` — เพิ่ม event ใหม่แค่เพิ่ม entry เดียว ไม่มี reflection scan) แล้ว `publisher.Publish(notification, ct)` — Mediator source-gen หา handler ของ runtime type เอง

**3. Modules (Orders)** — `CheckoutConfirmedConsumer` รับแล้วเปิด order แบบ idempotent:
```csharp
public async ValueTask Handle(CheckoutConfirmed notification, CancellationToken cancellationToken)
{
    var existing = await _orders.GetByCheckoutSessionIdAsync(notification.CheckoutSessionId, cancellationToken);
    if (existing is not null) return; // idempotent skip
    var order = Order.Create(notification.MerchantId, notification.Amount, _clock.UtcNow, items, ...);
    _orders.Add(order);
    await _unitOfWork.SaveChangesAsync(cancellationToken);
}
```
`if (existing is not null) return;` คือ defense ชั้นแรก, filtered UNIQUE index บน `CheckoutSessionId` คือ backstop ชั้นสอง — ต่อให้ dispatcher เผลอ publish event เดียวกันซ้อนกันจริง (at-least-once ไม่ใช่ exactly-once) ฝั่งแพ้ race จะโดน DB ปฏิเสธแล้ว retry เจอ order ที่มีอยู่แล้ว ไม่สร้างซ้ำ

### B3. flow การเงินซับซ้อนสุด — PSP webhook ยืนยันจ่าย

เส้นทางนี้พาดผ่านทั้ง 6 layer และเป็นจุดที่ idempotency/concurrency/outbox ทำงานพร้อมกันหมด — `HandlePspWebhookHandler` (`Payments.Application`):

```csharp
public async ValueTask<WebhookHandled> Handle(HandlePspWebhookCommand command, CancellationToken cancellationToken)
{
    var connection = await _connections.GetByIdAsync(command.PspConnectionId, cancellationToken) ?? throw ...;
    var adapter = _adapters.For(connection.Psp);
    var secret = await _vault.RevealAsync(connection.MerchantId, connection.SecretRefName, cancellationToken);

    if (!adapter.VerifyWebhook(command.RawPayload, command.Signature, secret))
        return new WebhookHandled(WebhookOutcome.Rejected);

    var outcome = await _unitOfWork.ExecuteInTransactionAsync(async ct =>
    {
        var evt = adapter.ParseWebhook(command.RawPayload);
        var keys = new[] {
            $"{pspCode}:{command.PspConnectionId}:event:{evt.EventId}",
            $"{pspCode}:{command.PspConnectionId}:charge:{evt.ExternalChargeId}:{evt.Status}",
        };
        if (!await _idempotency.TryBeginAsync(keys, IdempotencyContext, ct))
            return WebhookOutcome.Duplicate;

        var confirmed = await adapter.FetchChargeAsync(evt.ExternalChargeId, secret, ct);   // fetch-to-confirm
        if (confirmed != PspChargeStatus.Paid) return WebhookOutcome.Ignored;

        var session = await _sessions.GetByExternalChargeAsync(connection.Psp, evt.ExternalChargeId, ct) ?? throw ...;
        session.MarkPaid(evt.ExternalChargeId, occurredAt);
        _outbox.Enqueue(new PaymentPaid(session.Id, session.OrderId, session.MerchantId, session.Amount, ...));
        await _unitOfWork.SaveChangesAsync(ct);
        return WebhookOutcome.Processed;
    }, cancellationToken);

    return new WebhookHandled(outcome);
}
```

ทำงานร่วมกันของแต่ละ layer ในฟังก์ชันเดียว:
- **BuildingBlocks** (`IVaultSecretStore`, `IIdempotencyStore`, `IOutbox`, `IUnitOfWork`) — ports ทั้งหมดที่ handler เห็นมาจาก layer นี้ ตัว handler เองไม่รู้จัก EF/HTTP/crypto เลย
- **Persistence** — `IUnitOfWork.ExecuteInTransactionAsync` (impl จริงคือ `MerchantRuntimeUnitOfWork`) เปิด SQL transaction จริงครอบทั้ง claim idempotency + fetch-to-confirm + state change + outbox enqueue ให้เป็นก้อนเดียว atomic — พังกลางทาง rollback หมด ไม่มีทาง "จ่ายแล้วแต่ event หาย" หรือ "event ส่งแล้วแต่ session ยังไม่ Paid"
- **สำคัญที่สุด**: **ไม่เชื่อ webhook body เฉย ๆ** — แม้ signature ผ่านแล้ว ยังต้อง `FetchChargeAsync` ยิงกลับไปถาม PSP ตรง ๆ อีกครั้ง (fetch-to-confirm) เพราะ webhook เป็นแค่ "สัญญาณเตือนให้มาเช็ค" ไม่ใช่ source of truth ที่แท้จริง — source of truth คือคำตอบจาก PSP ตอน fetch เท่านั้น
- **Contracts + §4 dispatcher** — `PaymentPaid` เดินทางเหมือน B2 (outbox → dispatcher → publish) ไปเข้า `OrderPaidConsumer` (Orders) ซึ่ง **re-verify amount/currency อีกรอบ** ก่อนเรียก `Order.MarkPaid` — ไม่เชื่อแม้แต่ event ที่ผ่าน idempotency + fetch-to-confirm มาแล้ว เป็น defense-in-depth อีกชั้น

### B4. concurrency race จริง — กดปุ่มจ่ายซ้ำพร้อมกัน (double-click / retry)

`StartRedirectHandler` (Payments) ต้องกัน 2 request พร้อมกันสร้าง PSP charge ซ้อนกัน — ใช้ `RowVersion` (SQL Server rowversion) เป็น optimistic-concurrency token บน `Payments.Domain.Session`:

```csharp
session.BeginRedirect(_clock.UtcNow);        // Created -> Redirected, ยังไม่แตะ PSP
try
{
    await _unitOfWork.SaveChangesAsync(cancellationToken);   // เซฟใต้ RowVersion — claim ชนะ/แพ้ตัดสินตรงนี้
}
catch (ConcurrencyConflictException)
{
    var winner = await _sessions.GetByIdAsync(command.PaymentSessionId, cancellationToken);
    if (winner?.RedirectUrl is not null)
        return new StartRedirectResult(winner.RedirectUrl);   // แพ้ race -> คืน URL ของผู้ชนะ ไม่สร้างซ้ำ
    throw new InvalidOperationException("... redirect is already in progress; retry shortly.");
}
// ชนะ race แล้วเท่านั้นถึงแตะ PSP จริง
var charge = await adapter.CreateRedirectChargeAsync(session, secret, cancellationToken);
session.SetPspCharge(charge.ExternalChargeId, charge.RedirectUrl, _clock.UtcNow);
await _unitOfWork.SaveChangesAsync(cancellationToken);
```

`ConcurrencyConflictException` ไม่ใช่ EF exception ดิบ — `MerchantRuntimeUnitOfWork.SaveChangesAsync` (Persistence) เป็นจุดเดียวที่จับ `DbUpdateConcurrencyException` แล้วแปลเป็น exception ระดับ application (Modules ไม่รู้จัก EF Core type เลย) พร้อมแยกอีก 2 เคสข้าง ๆ กัน: unique-index violation (SQL 2627/2601) แปลเป็น `ConflictException`, CHECK/FK violation (SQL 547) ก็แปลเป็น `ConflictException` เหมือนกันแต่คนละ telemetry category — ทั้งสามเคสจบที่ `ProblemDetailsExceptionHandler` (§3) แปลงเป็น HTTP 409 เหมือนกันจากมุมมอง client แต่ log ฝั่ง server แยกเหตุผลชัดเจน

### B5. cross-context transaction — super-admin provision merchant ใหม่

จุดเดียวในระบบที่ `ControlPlaneDbContext` กับ `MerchantRuntimeDbContext` ใช้ connection/transaction เดียวกัน — `ProvisioningCoordinator` (`Persistence.Provisioning`, verify แล้วว่า wired จริงผ่าน `AddProvisioning(...)` ที่ `Program.cs:196` ไม่ใช่แค่ scaffolding ตามที่ doc-comment เก่าในไฟล์บอกไว้):

```csharp
await using var connection = await _openConnectionAsync(cancellationToken);
await using var controlPlane = _controlPlaneFactory(connection);
await using var merchantRuntime = _merchantRuntimeFactory(connection);
await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
await controlPlane.Database.UseTransactionAsync(transaction, cancellationToken);
await merchantRuntime.Database.UseTransactionAsync(transaction, cancellationToken);

await VerifyCallerIsActiveSuperAsync(controlPlane, callerAdminId, expectedAuthorizationVersion, ...);  // WITH (UPDLOCK, HOLDLOCK) ใน tx

var inserted = await TryInsertLedgerRowAsync(controlPlane, ledgerRow, cancellationToken);   // raw SQL insert กันซ้ำด้วย unique index
// ... สร้าง Merchant + PspConnection + VaultSecretBlob (เข้ารหัส DEK/KEK ด้วย BuildingBlocks.Infrastructure.Vault) + ProvisioningAudit

await controlPlane.SaveChangesAsync(acceptAllChangesOnSuccess: false, cancellationToken);
await merchantRuntime.SaveChangesAsync(acceptAllChangesOnSuccess: false, cancellationToken);
await transaction.CommitAsync(cancellationToken);            // commit เดียว ครอบทั้ง 2 context
controlPlane.ChangeTracker.AcceptAllChanges();
merchantRuntime.ChangeTracker.AcceptAllChanges();
```

จุดที่น่าสนใจ: `SaveChangesAsync(acceptAllChangesOnSuccess: false, ...)` ทั้งคู่เขียนลง DB ผ่าน transaction ที่ยังไม่ commit แล้ว `AcceptAllChanges()` (แปลว่า "change tracker ตรงกับ DB แล้วจริง") ถูกเรียก **หลัง** `CommitAsync` เท่านั้น — ถ้าเรียกก่อนแล้ว commit ดัน fail จริง change tracker ของทั้ง 2 context จะเชื่อว่า save สำเร็จทั้งที่ DB ไม่มีแถวจริง (bug class ที่ design ตรงนี้ตั้งใจปิดไว้). ส่วน retry ที่ layer บนสุด (`ProvisionAsync`) เจอ transient fault จะไม่ retry มั่ว ๆ — เช็คก่อนว่า operation นี้ commit สำเร็จไปแล้วหรือยัง (`TryVerifySucceededAsync` อ่าน idempotency ledger) กัน "commit สำเร็จจริงแต่ ack หาย แล้วสร้าง merchant code ซ้ำ" จาก retry ที่ไม่จำเป็น

### B6. write-guard เหตุการณ์จริงที่เพิ่งพังแล้วซ่อม (บนบรานช์เดียวกับคู่มือนี้)

ตัวอย่างนี้ไม่ใช่ hypothetical — คือบั๊กจริงที่แก้ไปใน PR #124/#137 บน repo นี้ ยัง**อยู่ในคอมเมนต์โค้ดปัจจุบันตรง ๆ** ให้เห็น 3 layer ต้องคุยกันแม่นแค่ไหนตรง security boundary:

**1. Hosts** (`HttpActorContext.cs`) — claim ต้องอ่านแบบ **lazy property**, ห้าม snapshot ตอน constructor:
```csharp
// Claims are read lazily PER ACCESS, never snapshotted at construction: this Scoped service gets
// constructed DURING session authentication (auth handler ctor -> ISessionStore -> MerchantUserDbContext ->
// IActorContext) -- BEFORE the handler sets the authenticated principal, so a constructor snapshot froze
// CurrentMerchant at Guid.Empty for the whole request (bugfix-merchant-prebind-wiring F5, defect D3).
private Guid? ClaimMerchantId => Guid.TryParse(
    _accessor.HttpContext?.User.FindFirstValue("merchant_id"), out var fromClaim) ? fromClaim : ...;
```
เหตุผล: DI construct `IActorContext` เป็น Scoped service **ก่อน** ที่ authentication handler จะผูก principal เสร็จ (จาก session cookie) — ถ้า resolve claim ครั้งเดียวตอน constructor ค่าจะเป็น `Guid.Empty` ค้างไปทั้ง request แม้ authenticate สำเร็จแล้วก็ตาม

**2. Hosts** (`WriteAuthorizers.cs`) — `ControlPlaneAdminWriteAuthorizer` ต้องมี unbound carve-out เฉพาะ login flow เพราะ OIDC callback **เขียนก่อน**มี admin scope ผูก (callback คือ request ที่สร้าง session เอง — ถ้า gate ด้วย `IsBound` ทุกกรณี ล็อกอินทั้งระบบพังเงียบ ๆ เพราะแม้แต่ audit ของการปฏิเสธก็เขียนไม่ได้):
```csharp
public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant)
{
    if (entityType == typeof(DataProtectionKey)) return true;
    if (_scope.IsBound) return BoundOnlyTypes.Contains(entityType);
    return UnboundLoginFlowWrites.Contains((entityType, operation));   // allowlist แคบมาก เฉพาะ login flow
}
```

**3. BuildingBlocks + Hosts ผสมกัน** — `HttpMerchantWriteAuthorizer` ตัดสินใจ **ต่อ call** ไม่ใช่ตอนสร้าง context ว่าจะใช้ authorizer ตัวไหน เพราะ `MerchantUserDbContext` (Persistence) อาจถูกสร้างขึ้นมา**ก่อน**ที่ authentication จะผูก `IAdminScope` เสร็จ (hazard เดียวกับข้อ 1):
```csharp
public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) =>
    _adminScope.IsBound
        ? _admin.CanWrite(entityType, operation, targetMerchant)      // admin approve/reject flow
        : _merchant.CanWrite(entityType, operation, targetMerchant);  // merchant-user ปกติ
```

ทั้ง 3 จุดคือ Hosts ที่ผูก policy จริงให้พอร์ต `IWriteAuthorizer` (ประกาศใน BuildingBlocks §3) แล้ว `GuardedRuntimeDbContext` (BuildingBlocks) เรียกใช้ทุกครั้งที่ Persistence จะ `SaveChanges` — สามชั้นนี้ต้องตรงกันเป๊ะ ไม่งั้นได้ผลลัพธ์แบบที่เคยเกิดจริง: reject → resubmit → approve พังเงียบ ๆ โดยไม่มี error ให้เห็นตรง ๆ (เพราะ deny แบบ opaque ตามที่ §3 อธิบายไว้)

---

## คำถามที่พบบ่อย

**ทำไม Domain reference ได้แค่ SharedKernel เท่านั้น (แม้แต่ BuildingBlocks ก็ห้าม)?**
เพราะ Domain ต้องทดสอบได้โดยไม่ต้องพึ่ง framework ใด ๆ เลย — เป็นหลักการออกแบบที่ตั้งใจไว้ แต่ **`Architecture.Tests`
วันนี้บังคับแค่ 2 เคสเจาะจง** (ดู §1): ห้ามพึ่ง `Microsoft.EntityFrameworkCore` โดยตรง กับห้ามพึ่ง namespace
`*.Infrastructure` ใด ๆ — ไม่ใช่ guard กว้างที่ครอบทุก framework (Domain อ้าง ASP.NET Core หรือ Mediator ตรง ๆ
จะไม่ถูก guard ปัจจุบันจับ) — ถ้า Domain อ้าง `BuildingBlocks.Application`
ได้ (แม้จะเป็นแค่ port/interface ไม่มี implementation จริง) ก็เปิดช่องให้ค่อย ๆ ดึง infrastructure concern เข้า
มาปนกับ business rule ทีละนิด เช่น aggregate เริ่ม inject `IClock`/`IOutbox` เข้ามาตรง ๆ แทนที่จะให้ Application
layer เป็นคนประสาน. SharedKernel ผ่านเกณฑ์นี้เพราะไม่มี dependency ออกไปหาใครเลยสักตัว.

**ทำไม `Money` ต้อง serialize เป็น JSON string ("1500.0000") ไม่ใช่ number?**
เพราะ IEEE754 double-precision (ที่ JSON number มาตรฐานใช้ตอน parse) ปัดเศษทศนิยมผิดได้ในบางค่า —
`MoneyJsonConverter` (§1) เลยบังคับ field `amount` เป็น string เท่านั้น ปฏิเสธถ้าเจอเป็น number ตั้งแต่
deserialize แล้วเขียนกลับ fix 4 ตำแหน่งเสมอ พร้อมเรียก `Money.Of()` ซ้ำทุกครั้งตอนอ่านกลับ (re-validate ไม่ใช่
trust ตรง ๆ) — ทั้งหมดนี้เพื่อรักษากฎห้าม float/double แทนเงินให้ครอบคลุมถึงจุดที่เงินออกไปนอกระบบ (wire
format) ด้วย ไม่ใช่แค่ในหน่วยความจำ.

**ทำไม merchant id ต้องมาจาก `IActorContext` เท่านั้น ห้ามรับจาก request body หรือ URL?**
กฎนี้คือกฎของ **merchant-facing tenant-scoped command** เท่านั้น (คำสั่งที่ implement `IMerchantScoped` แบบ
`CreateProductCommand` — ดู B1) — ถ้ารับ merchant target จาก body/URL ของคำสั่งกลุ่มนี้เท่ากับให้ client เป็น
คนบอกเองว่าตัวเองเป็น merchant ไหน ปลอมง่ายมาก (แก้ JSON body หรือ URL param ก็สวมรอยเป็น merchant อื่นได้ทันที).
`IActorContext.CurrentMerchant` มาจาก authenticated principal ที่ผ่านการ authenticate แล้วเท่านั้น (§3, §6)
แล้วยังโดนเช็คซ้ำอีกชั้นที่ `MerchantGuardBehavior` ก่อนเข้า handler กับ query filter ตอนอ่าน/เขียนจริงที่
Persistence (§4) — ดู B1 ที่ endpoint จริงไม่รับ `merchantId` จาก `CreateProductRequest` เลยสักฟิลด์.

กฎนี้ **ไม่ใช้กับ control-plane/admin endpoint** ที่ตั้งใจให้ admin ระบุ merchant เป้าหมายผ่าน body/path/query
ตรง ๆ เช่น `AssignMerchantRequest.MerchantId` ใน `POST /{id}/merchants`, `DELETE /{id}/merchants/{merchantId}`,
หรือ `?merchantId=` ใน admin policy report — endpoint กลุ่มนี้ authorize ผ่านคนละแกนคือ `IAdminScope`
(`scope.Current`/`scope.Accessible`) ไม่ใช่ `IActorContext.CurrentMerchant` เพราะ admin (โดยเฉพาะ Super)
มีสิทธิ์ทำงานข้าม merchant ได้โดยดีไซน์ — ถ้า Scoped admin ระบุ merchant นอก accessible set จะได้หน้าว่าง
(empty page) ไม่ใช่เห็นข้อมูลรั่ว.

**เพิ่ม dependency ข้ามชั้น/ข้ามโมดูลที่ยังไม่มีอยู่ ทำไมยาก?**
เพราะกฎ layering ไม่ได้เป็นแค่ convention ในเอกสาร — `Architecture.Tests` (NetArchTest) บังคับจริงและ fail CI
ทันทีถ้าผิด และ user เคยตัดสินใจไว้ตรง ๆ แล้วว่าจะไม่ hoist code ที่ดูซ้ำ pattern กันระหว่างโมดูลขึ้นไปเป็น
shared base class (ตอน spec `masterdata-split`) เพราะยอมรับความซ้ำเพื่อแลกกับความ isolate ของแต่ละโมดูล —
ทางที่เปิดไว้จริงมีทางเดียวคือผ่าน `Contracts` (event, §2) หรือ reference แค่ `.Domain` ของโมดูลอื่น (published
language, §5) ไม่ใช่การเพิ่ม `ProjectReference` ไปมาตรง ๆ.

**Outbox เป็น at-least-once (มีโอกาสส่งเหตุการณ์ซ้ำ) แล้วปลอดภัยได้ยังไงว่าจะไม่สร้าง order/รับเงินซ้ำ?**
เพราะความรับผิดชอบ "กันซ้ำ" ถูกย้ายไปให้ฝั่งรับ (consumer) แทนที่จะพึ่ง exactly-once จากตัว outbox เอง (§2) —
เห็นรูปธรรมที่ B2 (`if (existing is not null) return;` เป็นด่านแรก, filtered UNIQUE index เป็น backstop ด่าน
สอง) และหนักกว่านั้นที่ B3 ซึ่งมี idempotency key เช็คก่อน บวกกับ fetch-to-confirm ที่ไม่เชื่อ payload เดิมซ้ำ
บวกกับ consumer ฝั่ง Orders ที่ re-verify amount/currency อีกรอบก่อนเรียก `MarkPaid` — สามชั้นซ้อนกันเพื่อชดเชย
ว่า outbox เองรับประกันได้แค่ "ส่งอย่างน้อยหนึ่งครั้ง" ไม่ใช่ "ส่งพอดีหนึ่งครั้ง".

**ทำไม `Merchants.Application` reference `Payments.Application` ได้ตรง ๆ ทั้งที่กฎบอกห้าม cross-module?**
เพราะเป็นข้อยกเว้นที่ตั้งใจเปิดไว้ ไม่ใช่รูรั่วที่หลุดผ่าน CI (§5) — `Merchants` ถูกนิยามไว้เป็น
"PROVISIONING/composition module" ที่ยืนเหนือ 5 โมดูลธุรกิจ เพราะ `ProvisionMerchantHandler` ต้องสร้าง
`PspConnection` + secret envelope ของ Payments ตอน provision merchant ใหม่จริง ๆ (เห็นเต็ม flow ที่ B5) —
`MerchantsArchitectureTests.cs` เขียนกันเคสนี้ออกจาก peer-ban set ของ `ArchitectureBoundaryTests` โดยเจตนา
ไม่ใช่ทุกโมดูลจะได้สิทธิ์แบบนี้ มีแค่ Merchants เจ้าเดียว.

---

## เพิ่มโมดูลใหม่ ต้องแตะ layer ไหนบ้าง

สรุปสั้น ๆ (กฎเต็มดู [`src-structure.md` §4](src-structure.md)):

- **SharedKernel/Contracts**: แตะเฉพาะถ้าโมดูลใหม่ต้อง publish integration event ใหม่ (เพิ่ม record ใน `Contracts`) — ไม่แก้ `SharedKernel` เว้นแต่ต้องเพิ่ม primitive ที่ใช้ร่วมจริง ๆ (หายาก)
- **BuildingBlocks**: ปกติไม่ต้องแตะเลย — โมดูลใหม่แค่ *ใช้* port ที่มีอยู่แล้ว (`IActorContext`, `IUnitOfWork`, `IOutbox` ฯลฯ)
- **Modules**: สร้าง 3 project ใหม่ (`Domain`/`Application`/`Infrastructure`) ตามรูปทรงเดียวกับ 12 โมดูลที่มี — ชื่อ project/folder เป็นพหูพจน์ (naming law L1-L8 ใน ARCHITECTURE.md), **ไม่มี shared base class ระหว่างโมดูลที่หน้าตาคล้ายกัน** แม้จะซ้ำ pattern ก็ตาม (ตัดสินใจไว้ตอน `masterdata-split` — user ปฏิเสธการ hoist ไป SharedKernel/BuildingBlocks)
- **Persistence**: repository implementation ของโมดูลใหม่ไปอยู่ที่ `Persistence.ControlPlane` หรือ `Persistence.MerchantRuntime`/`MerchantUsers` (แล้วแต่ข้อมูลอยู่ฝั่งไหน) ไม่ใช่ใน `<Module>.Infrastructure` เอง — ตาม convention หลัง RLS teardown
- **Hosts**: (ก) เพิ่ม assembly ของโมดูลใหม่เข้า `HostModuleAssemblies.All` (`DesignTimeDbContextFactories.cs`) ไม่งั้น EF จะ discover entity config ไม่เจอ, (ข) map endpoint ใหม่ใต้ `/api/v1/{area}` พร้อม `RequireAuthorization`/`RequirePermission` ที่ถูกต้อง
- **บังคับด้วย CI ไม่ใช่แค่ review**: `Architecture.Tests` (NetArchTest) จะแดงทันทีถ้าโมดูลใหม่ผิดกฎ layering, ลืม GRANT ตารางใหม่ให้ `pol_app`, หรือแอบใช้ escape-hatch (`IgnoreQueryFilters()`/raw SQL) นอก allowlist
