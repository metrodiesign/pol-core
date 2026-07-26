# คู่มือ 6 Layer หลักของ pol-core — คืออะไร บทบาทอะไร ทำงานยังไง

> เอกสารนี้อธิบาย **ทำไม** แต่ละ top-level folder ใต้ `src/` ถึงมีอยู่ และ **ทำงานยังไง** ในเชิง narrative — เขียนสำหรับคนที่กำลังทำความเข้าใจระบบครั้งแรก ไม่ใช่ file-by-file reference.
>
> ถ้าต้องการรู้ว่าไฟล์ไหนอยู่ตรงไหน อ่าน [`src-structure.md`](src-structure.md) (ground truth = ไฟล์จริง, สรุปเป็นตารางต่อไฟล์).
> ถ้าต้องการรายละเอียดเชิงธุรกิจของแต่ละโมดูล อ่าน [`platform-modules.md`](platform-modules.md).
> ถ้าต้องการกลไก isolation floor เต็ม (query filter/write guard/escape-hatch) อ่าน [`db-connection-and-rls.md`](db-connection-and-rls.md).
> canonical target architecture: [`.ai/shared/ARCHITECTURE.md`](../../.ai/shared/ARCHITECTURE.md).

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

---

## 1. SharedKernel — พื้นล่างสุด ไม่มีใครให้พึ่ง

**คืออะไร**: project เดียว (`SharedKernel.csproj`) ที่ไม่มี `ProjectReference` ออกไปหาใครเลย — เป็นจุดต่ำสุดของทั้ง dependency graph. มีแค่ 4 ไฟล์ ไม่มี subfolder: `Entity.cs`, `Money.cs`, `Iso4217.cs`, `MoneyJsonConverter.cs`.

**บทบาท**: เป็นจุดเดียวที่นิยาม domain primitive ที่ต้อง "เหมือนกันทุกที่" ข้ามทั้ง 12 โมดูล — จะได้ไม่มีโมดูลไหนคิด `Money` เองแล้วเงินหมุนข้าม seam ผิดรูปแบบ. โดยเฉพาะกฎที่ user ตัดสินไว้ตั้งแต่ 2026-07-05 (as-built ตรงตามนี้แล้วตั้งแต่ rf1): **ห้าม float/double ที่ไหนก็ตามที่แทนเงิน**.

**หน้าที่แต่ละไฟล์**:
- `Entity<TId>` — base class DDD ที่เทียบ "ตัวตน" ด้วย runtime type + Id (ไม่ใช่เทียบ field ทีละตัว) — constructor ว่างเป็น `protected` ไว้ให้ EF Core materialize เท่านั้น
- `AggregateRoot<TId>` — สืบทอดจาก `Entity<TId>` เพิ่ม `Raise(IDomainEvent)` (เก็บลง list ภายใน) และ `ClearDomainEvents()` (drain ออก). **ของจริงวันนี้**: ไม่มี aggregate ไหนเรียก `Raise()` เลยสักที่ (grep ทั้ง repo เจอแค่ definition), และทุก EF config เรียก `builder.Ignore(x => x.DomainEvents)` ตรง ๆ พร้อมคอมเมนต์ "events are enqueued by the handler in-tx (REQ-20), not via the aggregate" — เท่ากับ hook นี้เป็น dead machinery ที่ยังไม่มีใครต่อสาย ไม่ใช่กลไก auto-translate เป็น outbox row จริง; ของจริงคือ handler เรียก `IOutbox.Enqueue(...)` เองตรง ๆ ทุกจุด (ดู §2, B2, B3)
- `Money` — `readonly record struct { decimal Amount; string Currency }`, สร้างได้ทางเดียวคือ `Money.Of(amount, currency)` ซึ่ง validate currency ต้องรู้จักใน ISO-4217, amount ห้ามติดลบ, scale ห้ามเกิน 4 ตำแหน่งทศนิยม. `default(Money)` เป็นค่า sentinel ที่ `Currency` เป็น `null` — **แต่การ์ดนี้ครอบแค่ arithmetic** (`Add()` เช็ค null แล้ว throw `InvalidOperationException` ก่อนบวก) เท่านั้น: อ่าน `.Amount`, เรียก `ToString()`, หรือ serialize ผ่าน `MoneyJsonConverter.Write()` **ไม่ throw** เลย (converter จะเขียน `{"amount":"0.0000","currency":null}` เงียบ ๆ) — ถ้า default `Money` หลุดเข้า response จริงจะไม่มี exception เตือน ต้องระวังตอนสร้าง object ให้ผ่าน `Money.Of()` เสมอ ไม่ใช่พึ่งการ throw ของ sentinel
- `Iso4217` — registry สกุลเงินขั้นต่ำ (THB/USD/JPY วันนี้), บอกจำนวนทศนิยมต่อสกุล
- `MoneyJsonConverter` — บังคับ JSON field `amount` ต้องเป็น **string** เท่านั้น (ปฏิเสธถ้าเจอเป็น number กัน IEEE754 double precision loss ตอน parse), เขียนกลับ fix 4 ตำแหน่งเสมอ, และตอนอ่านกลับก็เรียก `Money.Of()` ซ้ำ — เท่ากับ re-validate ทุกครั้งที่ deserialize ไม่ใช่ trust JSON เฉย ๆ

**ทำงานยังไง**: ทุกโมดูลใน `src/Modules/*.Domain` และ `*.Application` reference `SharedKernel` ได้ (21 `.csproj` อ้างถึงจริง) — แต่ `SharedKernel` เองไม่ reference กลับไปหาใคร. กติกานี้ไม่ใช่แค่คำแนะนำในเอกสาร — `Architecture.Tests` (NetArchTest) เช็คจริงว่า `*.Domain` ห้ามพึ่ง `Microsoft.EntityFrameworkCore` หรือ framework ใด ๆ เลย ซึ่งเป็นไปได้เพราะ Domain มีแค่ `SharedKernel` ให้พึ่งเท่านั้น. `Contracts.csproj` (§2) ก็ reference `SharedKernel` เช่นกัน เพื่อให้ event ข้ามโมดูลพก `Money` แบบ value object ได้ตรง ๆ (เช่น `PaymentPaid.Amount: Money`) แทนที่จะต้องแปลงเป็น `decimal`/`long` ดิบตรง seam.

**ทำงานร่วมกับ layer อื่นตรงไหน**: ทุก aggregate ในทั้ง 12 โมดูล (`Product`, `Merchant`, `Order`, `Payments.Session` ฯลฯ) สืบทอด `AggregateRoot<TId>` จากที่นี่ และเก็บฟิลด์เงินเป็น `Money` เสมอ — เห็นตัวจริงที่ตัวอย่าง **B1 (สร้างสินค้า)** ด้านล่าง ที่ `Product.Price`/`Product.SumInsured` เป็น `Money` ตั้งแต่ domain ยัน wire response.

---

## 2. Contracts — ภาษากลางที่โมดูลใช้คุยกัน

**คืออะไร**: project เดียว (`Contracts.csproj`) reference แค่ `SharedKernel` + package `Mediator.Abstractions`. เก็บ record 4 ตัว: `PaymentPaid`, `CheckoutConfirmed`, `CustomerOrderNotification`, `MerchantUserRegistrationSubmitted` — **ไม่ใช่** API request/response DTO (พวกนั้นไม่มีบ้านแยก ประกาศ inline อยู่ที่ `Hosts/Api` ตรงจุด map endpoint). Contracts คือ payload ของ **event ข้ามโมดูลใน process เดียวกัน** เท่านั้น.

**บทบาท**: เป็น "ภาษากลาง" (published language) ที่โมดูลหนึ่งใช้บอกอีกโมดูลว่าเกิดอะไรขึ้น โดยไม่ต้อง reference โมดูลปลายทางตรง ๆ เลย — เช่น `Checkouts.Application` ไม่รู้จัก `Orders.Application` เลยแม้แต่นิดเดียว แค่ publish `CheckoutConfirmed` ไปเข้า outbox แล้วปล่อยให้ `Orders` ไปสมัคร consumer ของตัวเองมารับ.

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

**หน้าที่ตัวอย่างสำคัญที่สุด 2 กลไก**:

1. **Actor/merchant isolation** (`BuildingBlocks.Application`): `IActorContext`/`IActorScope` คือแกนกลาง — merchant ปัจจุบันของ request มาจาก **authenticated principal เท่านั้น ไม่ใช่จาก URL**. `IMerchantScoped` เป็น marker interface ว่าง ๆ ที่ command/query ต้อง implement ถ้าแตะข้อมูลของ merchant เดียว แล้ว `MerchantGuardBehavior<,>` (Mediator `IPipelineBehavior`) จะเช็คก่อนทุกครั้งว่ามี actor ผูกอยู่ไหม — ถ้าไม่มี throw `MerchantBindingException` ทันทีก่อนแม้แต่จะเข้า handler พร้อมยิง security telemetry event ว่ามี unbound actor พยายามเข้าถึง
2. **Write guard** (`BuildingBlocks.Infrastructure`): `GuardedRuntimeDbContext` คือ abstract base class ที่ seal ทั้ง 4 overload ของ `SaveChanges`/`SaveChangesAsync` ผ่านจุดเดียว (`GuardPendingChanges()` — derived class เขียนทับไม่ได้) ทุก DbContext ที่ runtime จริงต้อง inherit ตัวนี้ แล้วจะได้การเช็คฟรี 3 ชั้นต่อทุก entity ที่ tracked: (ก) ห้ามแก้/ลบ entity ที่ mark เป็น append-only เช่น audit trail, (ข) ห้าม tenant key เป็น `Guid.Empty` และห้ามแก้ tenant key หลัง insert แล้ว, (ค) เรียก `IWriteAuthorizer.CanWrite(entityType, operation, targetMerchant)` ซึ่ง default-deny — implementation จริงของ port นี้อยู่ที่ Host เท่านั้น (§6)

**ไฟล์อื่นที่น่ารู้**: `PolDbContext` (migration-owner ตัวเดียวของทั้งระบบ ไม่ registered runtime), `SchemaNames` (constant ชื่อ schema ที่ทุก `IEntityTypeConfiguration` ต้องใช้), `Vault/` (envelope encryption AES-256-GCM สำหรับ secret ของ PSP), `Outbox/`+`Idempotency/` (entity รองรับกลไก §2), `Observability/` (ส่ง denial event ไป Seq), และฝั่ง `.Web`: `ProblemDetailsExceptionHandler` (จุดเดียวทั้งระบบที่แปลง exception type → HTTP status ตาม RFC7807 — `Detail` เป็น string คงที่เสมอ ไม่ใช่ `exception.Message` กัน leak ข้อมูล merchant/SQL ออกไปกับ response).

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

**บทบาท**: แทนที่ SQL RLS เดิมด้วย 2 ชั้นที่ทำงานที่ app layer ทั้งคู่ — read floor = EF global query filter (deny-by-default: ถ้า actor ไม่ผูก merchant, `CurrentMerchant` จะเป็น `Guid.Empty` และ query filter จะคืน 0 แถวเสมอ ไม่ใช่ error), write floor = `GuardedRuntimeDbContext` (§3) ผสมกับ `IWriteAuthorizer` ที่ implement จริงอยู่ที่ Host.

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

**ทำงานยังไง** (เดินตัวอย่าง `Merchants` module ครบ vertical slice):
1. `Merchant.cs` (Domain) — aggregate มี constructor เป็น `private`, สร้างได้ผ่าน static factory `Create`/`CreateWithId` เท่านั้น ซึ่ง validate ข้างในทันที: code ต้องอยู่ใน allowlist, currency ต้องผ่าน `Iso4217.IsSupported` จริง (ตรวจกับ registry) — แต่ `country` เช็คแค่ **รูปแบบ 2 ตัวอักษรหลัง normalize** (`normalizedCountry.Length != 2`) เท่านั้น **ไม่ได้เช็คกับ ISO 3166 registry จริง** เหมือนที่ currency ทำ ดังนั้น `"ZZ"` (2 ตัวอักษร แต่ไม่มีในทะเบียนจริง) ผ่านการสร้างได้ — ห้ามอ่านว่า `Merchant` ที่สร้างสำเร็จแปลว่า country ถูกต้องตามมาตรฐาน ISO เสมอไป แปลได้แค่ "รูปแบบ 2 ตัวอักษร" เท่านั้น
2. `ProvisionMerchantCommand.cs` (Application) — `record : ICommand<ProvisionMerchantResult>` พก field ที่ handler ต้องใช้ทั้งหมด (spec ของ merchant, PSP connection, ใครเป็นคนสั่ง)
3. `ProvisionMerchantHandler.cs` (Application) — constructor รับแต่ **port ล้วน** (`IMerchantRepository`, `IProvisioningWriter`, `IPspSecretEnvelopeFactory`, `IClock`) ไม่รู้จัก EF Core เลย — validate ก่อน (pure, ไม่มี side effect) แล้วค่อย delegate การเขียนจริงให้ `IProvisioningWriter` (ซึ่ง implement จริงอยู่ที่ `Persistence.Provisioning`)
4. `MerchantsModuleRegistration.cs` (Infrastructure) — `AddMerchantsModule()` DI marker

จุดสำคัญ: **โมดูลไม่ map HTTP endpoint ของตัวเองเลย** — endpoint mapping ทั้งหมดอยู่ที่ Host (`Program.cs`, §6) เสมอ โมดูลไม่รู้จัก ASP.NET Core แม้แต่นิดเดียว นี่คือสิ่งที่ทำให้ Application layer ทดสอบได้ง่ายด้วย fake ของ port โดยไม่ต้องบูต HTTP server จริง.

**flow ธุรกิจ**: Products → Carts → Checkouts → Orders → Payments — คุยข้ามกันผ่าน `Contracts` (§2) เท่านั้น ไม่มีโมดูลไหน reference โมดูลถัดไปตรง ๆ.

**ทำงานร่วมกับ layer อื่นตรงไหน**: handler ในทุก use-case ข้างบน (`CreateProductHandler`, `ConfirmCheckoutHandler`, `HandlePspWebhookHandler` ฯลฯ) คือจุดที่ port จาก BuildingBlocks/Contracts มาบรรจบกับ entity ของ Domain เอง — เดินตัวอย่างเต็มดูที่ **B1-B6** ทั้งหมดด้านล่าง ซึ่งล้วนเริ่ม/จบที่ handler ของ Modules

---

## 6. Hosts — จุดเดียวที่ประกอบทุกอย่างเข้าด้วยกันแล้วรันจริง

**คืออะไร**: composition root — host เดียวในทั้งระบบคือ `Hosts/Api` (`Api.csproj`). เดิมมี host `Worker` แยกไว้รัน background job แต่ถูก retire ไปแล้วทั้งตัว (spec `multi-tier-deployment`, 2026-07-22) — โค้ดที่เคยอยู่ใน Worker ถูกย้ายเข้ามาเป็น `IHostedService` ในโปรเซส `Api` เดียวกัน วันนี้เหลือแค่ 2 deploy image: `api` กับ `migrate` (ไม่มี `worker`).

**บทบาท**: จุดเดียวในทั้งระบบที่ reference ได้ **ทุกอย่างพร้อมกัน** — `Contracts`, ทั้ง 3 project ของ `BuildingBlocks`, ทั้ง 12 โมดูล (`.Application`+`.Infrastructure`), ทั้ง 4 `Persistence.*`. เป็นที่เดียวที่ผูก concrete implementation เข้ากับ port/interface ที่ทุก layer อื่นประกาศไว้ล่วงหน้า — โมดูลไม่รู้จัก EF Core, EF Core (Persistence) ไม่รู้จัก HTTP, Host คือที่เดียวที่รู้จักทุกฝ่ายแล้วผูกให้.

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

**1. Hosts** (`Program.cs:595-606`) map endpoint, ดึง merchant จาก `IActorContext` ไม่ใช่จาก body:
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

จุดเดียวในระบบที่ `ControlPlaneDbContext` กับ `MerchantRuntimeDbContext` ใช้ connection/transaction เดียวกัน — `ProvisioningCoordinator` (`Persistence.Provisioning`, verify แล้วว่า wired จริงผ่าน `AddProvisioning(...)` ที่ `Program.cs:192` ไม่ใช่แค่ scaffolding ตามที่ doc-comment เก่าในไฟล์บอกไว้):

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

## เพิ่มโมดูลใหม่ ต้องแตะ layer ไหนบ้าง

สรุปสั้น ๆ (กฎเต็มดู [`src-structure.md` §4](src-structure.md)):

- **SharedKernel/Contracts**: แตะเฉพาะถ้าโมดูลใหม่ต้อง publish integration event ใหม่ (เพิ่ม record ใน `Contracts`) — ไม่แก้ `SharedKernel` เว้นแต่ต้องเพิ่ม primitive ที่ใช้ร่วมจริง ๆ (หายาก)
- **BuildingBlocks**: ปกติไม่ต้องแตะเลย — โมดูลใหม่แค่ *ใช้* port ที่มีอยู่แล้ว (`IActorContext`, `IUnitOfWork`, `IOutbox` ฯลฯ)
- **Modules**: สร้าง 3 project ใหม่ (`Domain`/`Application`/`Infrastructure`) ตามรูปทรงเดียวกับ 12 โมดูลที่มี — ชื่อ project/folder เป็นพหูพจน์ (naming law L1-L8 ใน ARCHITECTURE.md), **ไม่มี shared base class ระหว่างโมดูลที่หน้าตาคล้ายกัน** แม้จะซ้ำ pattern ก็ตาม (ตัดสินใจไว้ตอน `masterdata-split` — user ปฏิเสธการ hoist ไป SharedKernel/BuildingBlocks)
- **Persistence**: repository implementation ของโมดูลใหม่ไปอยู่ที่ `Persistence.ControlPlane` หรือ `Persistence.MerchantRuntime`/`MerchantUsers` (แล้วแต่ข้อมูลอยู่ฝั่งไหน) ไม่ใช่ใน `<Module>.Infrastructure` เอง — ตาม convention หลัง RLS teardown
- **Hosts**: (ก) เพิ่ม assembly ของโมดูลใหม่เข้า `HostModuleAssemblies.All` (`DesignTimeDbContextFactories.cs`) ไม่งั้น EF จะ discover entity config ไม่เจอ, (ข) map endpoint ใหม่ใต้ `/api/v1/{area}` พร้อม `RequireAuthorization`/`RequirePermission` ที่ถูกต้อง
- **บังคับด้วย CI ไม่ใช่แค่ review**: `Architecture.Tests` (NetArchTest) จะแดงทันทีถ้าโมดูลใหม่ผิดกฎ layering, ลืม GRANT ตารางใหม่ให้ `pol_app`, หรือแอบใช้ escape-hatch (`IgnoreQueryFilters()`/raw SQL) นอก allowlist
