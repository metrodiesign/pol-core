# โมดูล Carts — ตะกร้าสินค้าก่อนเข้าสู่ Checkout

> **[สร้างครั้งแรก 2026-08-01]** sync กับโค้ดจริงที่ commit ล่าสุดที่แตะโมดูลนี้ (`1af6dc2`,
> "feat(rls-to-query-filter): replace SQL Server RLS with an app-layer isolation floor",
> 2026-07-19). แหล่งความจริง: `src/Modules/Carts/**`, `src/Persistence/Persistence.MerchantRuntime/Carts/**`,
> `src/Hosts/Api/Program.cs` (route mapping)

## บริบท

Cart คือพื้นที่ "ลองจัดของ" ของตัวแทนขาย (merchant-user) ก่อนกดยืนยันราคาเป็นเรื่องจริงจังที่
`CheckoutSession` — อยู่ต้นสุดของ funnel ธุรกิจ `Carts → CheckoutSessions → Orders →
PaymentSessions` (ถัดจาก `Products` ซึ่งเป็นแคตตาล็อกต้นทาง)

ต่างจาก Divisions/Levels/Offices/Positions (reference master data, schema `cfg`, ไม่มี merchant
dimension), Cart เป็นส่วนหนึ่งของ **data plane** จริง (schema `shop`, มี `MerchantId` เต็มรูปแบบ
ผ่าน EF global query filter — ดู [`db-connection-and-rls.md`](db-connection-and-rls.md)) และ
**ไม่มี spec ชื่อ "carts" โดยตรง** — โมดูลนี้เกิดสมบูรณ์ตั้งแต่ scaffold แรกของระบบแล้วถูกแก้ต่อแค่
ผ่าน cross-cutting spec เท่านั้น: `rls-to-query-filter` (REQ-6 — ต้นกำเนิด `Item.MerchantId`
denormalization), `products-sp-53-alignment` (REQ-8.4 — currency mint boundary), และ
`hierarchical-naming` (task 3/6 — rename โปรเจกต์ ไม่ใช่ feature) โค้ด business logic จริงนิ่งมา
ตั้งแต่ commit `1af6dc2` (2026-07-19)

โมดูลต้นทาง (`Products`, ที่ดึงราคามา) มีไฟล์ reference แยกอยู่แล้ว: [`products.md`](products.md)
— โมดูลปลายทาง `Orders` ก็มีไฟล์แยกแล้วเช่นกัน: [`orders.md`](orders.md) ส่วน `Checkouts` ยังไม่มีไฟล์
แยกลักษณะนี้ ภาพรวมยังอยู่ใน [`layers-guide.md`](layers-guide.md) §5 และ
[`platform-modules.md`](platform-modules.md) §7

## Domain model (`Carts.Domain`)

`Cart` (`Cart.cs`, 122 บรรทัด) — `public sealed class Cart : AggregateRoot<Guid>` ถือ `Items`
เป็น owned collection ผ่าน field `_items`

| Property | Type | แก้ได้ยังไง |
|---|---|---|
| `Id` | `Guid` | ตั้งตอน ctor (จาก `AggregateRoot<Guid>`) |
| `MerchantId` | `Guid` | ตั้งตอน ctor เท่านั้น — immutable ตลอดชีวิต aggregate |
| `Status` | `CartStatus` | `Open` ตอน ctor เสมอ → `CheckedOut` ผ่าน `MarkCheckedOut()` เท่านั้น |
| `CreatedAt` | `DateTime` | ตั้งตอน ctor เท่านั้น |
| `Items` | `IReadOnlyCollection<Item>` | `_items.AsReadOnly()` — mutate ผ่าน aggregate method เท่านั้น |
| `Subtotal` | `Money?` (computed, `:98-111`) | คำนวณสด ไม่มี field เก็บ — `null` เมื่อตะกร้าว่าง (ไม่มี currency ให้ denominate ศูนย์) |

Ctor สาธารณะ `Cart(Guid id, Guid merchantId, DateTime createdAt) : base(id)` (`:26-32`) ตั้ง
`Status = Open` เสมอ — **ไม่มีทางสร้าง cart ที่เริ่มด้วยสถานะอื่น**; มี private parameterless ctor
(`:24`) สำหรับ EF materialise เท่านั้น

**State machine** — `CartStatus` (`Open=0`, `CheckedOut=1`), ทางเดียว ผ่าน `MarkCheckedOut()`
(`:92`) ซึ่งเป็น **one-liner ไม่มี guard เลย** (เรียกซ้ำได้ไม่ throw, ไม่มีทาง `CheckedOut → Open`)
— grep ทั้ง repo (src + tests) พบ caller เดียวคือ `tests/Carts.Tests/CartTests.cs:74` (unit test)
**ไม่มี production code เรียกเลย** ผลคือ cart ทุกใบในระบบจริงยังเป็น `Open` ต่อไปเรื่อยๆ แม้ผู้ใช้
กด checkout ไปแล้วและมี `CheckoutSession` ล็อกยอดคู่ขนานอยู่ก็ตาม (`POST /checkouts` อ่าน cart
ผ่าน `GetCartQuery` แค่เพื่อคำนวณ subtotal เท่านั้น ไม่แตะ status เลย)

**Mutate methods** (รวม throw 8 จุดในไฟล์):

| Method | Guard/throw | หมายเหตุ |
|---|---|---|
| `AddItem(productId, quantity, unitPrice)` (`:39-57`) | `InvalidOperationException` ถ้า `Status != Open`; `ArgumentOutOfRangeException(quantity)` ถ้า `quantity <= 0`; `InvalidOperationException` จาก `EnsureCurrencyMatches` (`:113-121`) ถ้า currency ไม่ตรง line แรก (cart ว่างข้ามเช็คนี้) | merge rule: หา line ที่ `ProductId == productId && UnitPrice.Amount == unitPrice.Amount` ตรงเป๊ะ — เจอ → `IncreaseQuantity` (checked overflow), ไม่เจอ → สร้าง `Item` ใหม่ (`Guid.CreateVersion7()`) |
| `RemoveItem(productId)` (`:60-66`) | `InvalidOperationException` ถ้า `Status != Open` | ไม่เจอ `productId` → no-op เงียบ (idempotent) |
| `SetItemQuantity(productId, quantity)` (`:70-80`) | `InvalidOperationException` ถ้า `Status != Open`; `ArgumentOutOfRangeException(quantity)` ถ้า `quantity <= 0`; **`ArgumentException(productId)`** ("not in the cart") ถ้าไม่เจอ line | จุดเดียวในไฟล์ที่ throw `ArgumentException` ไม่ใช่ `ArgumentOutOfRangeException` |
| `Clear()` (`:83-89`) | `InvalidOperationException` ถ้า `Status != Open` | no-op ถ้าว่างอยู่แล้ว |
| `MarkCheckedOut()` (`:92`) | ไม่มี guard เลย | ดูหัวข้อ state machine ด้านบน |

ไม่มี `RowVersion` column แยกทั้ง `Cart` และ `Item` — **แต่ไม่ได้แปลว่าไม่มี concurrency token
เลย**: `TenantKeyDescriptor.Require(...)` (เรียกที่ runtime persistence เท่านั้น — ดูหัวข้อ Runtime
persistence ด้านล่าง) ตั้ง `property.IsConcurrencyToken = true` บน `MerchantId` ของทั้งคู่จริง
(model-only token ไม่มี column เพิ่ม) ผสมกับ composite FK relationship ทำให้ flow เขียนปกติพังจริง
บน SQL Server — รายละเอียดเต็มดูหัวข้อ "จุดที่ไม่สมมาตร" ข้อ 1 ด้านล่าง ไม่มี domain event ถูก
`Raise()` จริง (มี `Raise()`/`DomainEvents` มาจาก `AggregateRoot<Guid>` แต่ไม่มีจุดไหนในไฟล์เรียก —
ถูก `Ignore()` ที่ EF config ด้วย)

`Item` (`Items/Item.cs`) — `public sealed class Item : Entity<Guid>` (ไม่ใช่ `AggregateRoot` —
เป็นลูกของ `Cart`, **ไม่มี navigation กลับไปหา parent เลย**)

| Property | Type | หมายเหตุ |
|---|---|---|
| `CartId` | `Guid` | FK (composite) |
| `MerchantId` | `Guid` | **denormalize จาก parent `Cart` ตอน construct** — ดูด้านล่าง |
| `ProductId` | `Guid` | |
| `Quantity` | `int` | |
| `UnitPrice` | `Money` | snapshot ตอน add, ไม่ขยับตาม catalog ทีหลัง |
| `LineTotal` | `Money` (computed) | `UnitPrice.Amount * Quantity` — ไม่ persist |

Ctor `internal Item(Guid id, Guid cartId, Guid merchantId, Guid productId, int quantity, Money
unitPrice)` (`Item.cs:30-38`) และเมธอด `IncreaseQuantity`/`SetQuantity` (`Item.cs:40-42`) เป็น
**`internal` ทั้งหมด** — สร้าง/แก้ได้เฉพาะจากภายใน assembly `Carts.Domain` (คือจาก `Cart.AddItem`
เท่านั้น) กัน caller ภายนอกสร้าง `Item` เองข้าม aggregate boundary

**MerchantId denormalization** — comment ในโค้ดตรงๆ ที่ `Item.cs:14-17`: เป็นผลจาก spec
`rls-to-query-filter` REQ-6 — `Item` ไม่มี CLR navigation ไป `Cart` เลย จึงต้องมี tenant key ของ
ตัวเองให้ `MerchantRuntimeDbContext`'s query filter กรองได้โดยไม่ต้อง join ผ่าน parent stamp ได้
จุดเดียวคือใน `Cart.AddItem` (จาก `this.MerchantId` เสมอ) จึง **ไม่มีทาง drift จาก parent ได้ใน
ทางแอปพลิเคชัน** และปิดที่ DB ชั้นด้วย composite FK `(CartId, MerchantId) → Cart(Id, MerchantId)`
(ดูหัวข้อ Infrastructure)

## Application layer (`Carts.Application`)

`Carts.Application.csproj` reference แค่ `Carts.Domain`, `Contracts`, `BuildingBlocks.Application`,
`Mediator.Abstractions` — **ไม่มี cross-module `.Domain` reference เลย**. **ไม่ใช้ SFS**
(Search/Filter/Sort) เพราะไม่มี list/paged endpoint ใน Carts เลย (`GetCartQuery` เป็น
single-resource read by id)

| Command/Query | Handler | Input/Output | Error |
|---|---|---|---|
| `CreateCartCommand(MerchantId)` — `ICommand<Guid>`, `IMerchantScoped` | `CreateCartHandler` (`:8-28`) | สร้าง `new Cart(Guid.CreateVersion7(), merchantId, clock.UtcNow)`, `Add`, save, return `Id` | **ไม่มี error case เลย** — สำเร็จเสมอ |
| `AddItemToCartCommand(CartId, MerchantId, ProductId, Quantity, UnitPrice)` — `ICommand<AddItemResult>`, `IMerchantScoped` | `AddItemToCartHandler` (`:11-37`) | `GetAsync` → `cart.AddItem(...)` → save → `AddItemResult(CartId, ItemCount, Subtotal)` | null → throw `InvalidOperationException("...was not found.")` (`:24-25`); merchant ไม่ตรง → throw `InvalidOperationException("...does not belong to the requesting merchant.")` (`:27-28`, **ข้อความคนละแบบจาก `CartLoad`** — ดูหมายเหตุด้านล่าง); domain อาจ throw เพิ่มจาก `AddItem` |
| `GetCartQuery(CartId, MerchantId)` — `IQuery<CartView?>`, `IMerchantScoped` | `GetCartHandler` (`GetCart.cs:24-38`) | คืน `CartView.From(cart)` (`:15-21`) | null **หรือ** merchant ไม่ตรง → คืน **`null` เฉยๆ ไม่ throw** (comment: "no existence leak" — caller/`Program.cs` map `null` → 404 เอง) |
| `RemoveItemFromCartCommand(CartId, MerchantId, ProductId)` — `ICommand<CartView>` (`CartEdits.cs:31-49`) | ผ่าน `CartLoad.RequireAsync` | `cart.RemoveItem` (no-op ถ้าไม่เจอ) → save → `CartView.From` | cart ไม่พบ/merchant ไม่ตรง → throw ผ่าน `CartLoad` |
| `SetCartItemQuantityCommand(CartId, MerchantId, ProductId, Quantity)` — `ICommand<CartView>` (`:51-69`) | ผ่าน `CartLoad.RequireAsync` | `cart.SetItemQuantity` → save → `CartView.From` | เช่นเดียวกัน + domain throw (`quantity<=0` → `ArgumentOutOfRangeException`, comment inline ยืนยัน `-> 400`) |
| `ClearCartCommand(CartId, MerchantId)` — `ICommand<CartView>` (`:71-89`) | ผ่าน `CartLoad.RequireAsync` | `cart.Clear` → save → `CartView.From` | เช่นเดียวกัน |

`CartView(CartId, Status:string, Items:IReadOnlyList<CartLineView>, Subtotal:Money?)` +
`CartLineView(ProductId, Quantity, UnitPrice, LineTotal)` (`GetCart.cs`) — `Status` แปลงเป็น
`.ToString()` (string "Open"/"CheckedOut")

`CartLoad.RequireAsync` (`internal static class`, `CartEdits.cs:19-29`) — helper ที่ 3 handler ใน
`CartEdits.cs` เรียกร่วมกัน: โหลด cart แล้ว throw `InvalidOperationException($"Cart {cartId} was
not found.")` **ข้อความเดียวทั้ง 2 กรณี** (ไม่พบจริง / merchant ไม่ตรง — comment ยืนยันตรงๆ ว่า
"RLS already filters cross-merchant rows, the explicit owner check is belt-and-braces" และตั้งใจ
ไม่ leak existence ข้าม merchant)

**หมายเหตุความไม่สม่ำเสมอ**: `AddItemToCartHandler` (เขียนก่อน `CartEdits.cs`) **ไม่ได้ใช้
`CartLoad.RequireAsync`** — มี inline check ของตัวเองที่ throw ข้อความต่างกันระหว่าง 2 กรณี
ต่างจาก 3 handler ใน `CartEdits.cs` ที่ยุบเป็นข้อความเดียว รายละเอียด HTTP-level ดูหัวข้อ
"จุดที่ไม่สมมาตร" ด้านล่าง

`ICartRepository` (`ICartRepository.cs`) — `void Add(Cart cart)` (sync, ไม่ save) +
`Task<Cart?> GetAsync(Guid cartId, CancellationToken ct)` (**ไม่มี `merchantId` parameter** —
filter ผ่าน query filter ของ context เอง) comment ยืนยัน: **ไม่มี save method บน interface นี้เลย**
("Adding/tracking is flushed by the shared `IUnitOfWork`")

## Infrastructure (`Carts.Infrastructure`, migration-owner)

`CartConfiguration` (`CartConfiguration.cs:13-41`, apply เข้า `PolDbContext`):
- `ToTable("Carts", SchemaNames.Shop)`, `HasKey(Id)`
- `Status` เก็บเป็น **string** (`HasConversion<string>().HasMaxLength(16)`) — ไม่ใช่ตารางเดียวที่
  ทำแบบนี้ (`shop.Products` ก็เก็บ `ProductGroup`/`DocumentType`/`PaymentStatus` เป็น string
  เหมือนกัน, `Products.Infrastructure/ProductConfiguration.cs:23-24,59`) แต่ต่างจากตารางส่วนใหญ่
  ในระบบที่เก็บ enum เป็น `int`
- `Ignore(Subtotal)`, `Ignore(DomainEvents)`
- `HasAlternateKey(Id, MerchantId)` (`AK_Carts_Id_MerchantId`) — ต้องมีก่อนถึงจะทำ composite FK
  ได้ เพราะ `MerchantId` ไม่ใช่ PK ของ `Cart` เอง
- `HasMany(Items).WithOne().HasForeignKey(CartId, MerchantId).HasPrincipalKey(Id, MerchantId).OnDelete(Cascade)`
- `Navigation(Items).UsePropertyAccessMode(Field)` — เข้าถึง `_items` field ตรง (property ไม่มี
  setter)

`ItemConfiguration` (`Items/ItemConfiguration.cs:13-33`): `ToTable("CartItems", SchemaNames.Shop)`,
`UnitPrice` เป็น `ComplexProperty` (`decimal(19,4)` + `char(3)` ไม่ unicode fixed-length ตาม EF
money mapping rule ทั่ว repo), `Ignore(LineTotal)`

`CartModuleRegistration.AddCartModule()` (`:14`) เป็น **no-op จริง** (`=> services`) — comment
บอกตรงๆ ว่า repository ย้ายไป `Persistence.MerchantRuntime` แล้ว (task 8.5.3) ไฟล์นี้มีไว้แค่ให้
assembly ถูก add เข้า `HostModuleAssemblies.All` เพื่อให้ `PolDbContext` discover
`CartConfiguration`/`ItemConfiguration` ตอน model-build เท่านั้น — **ยังถูกเรียกจริงที่
`Program.cs:152`** (ไม่ได้หายไปเงียบๆ)

## Runtime persistence (`Persistence.MerchantRuntime/Carts`)

Configuration ของ Cart มี **2 ชุดโดยตั้งใจ** (pattern เดียวกับทุกโมดูล data-plane อื่นหลัง spec
`rls-to-query-filter`):

| Config | ที่อยู่ | ใช้กับ context | query filter |
|---|---|---|---|
| Migration-owner | `Carts.Infrastructure/CartConfiguration.cs` | `PolDbContext` (migration/schema เท่านั้น) | ไม่มี |
| Runtime | `Persistence.MerchantRuntime/Carts/CartConfiguration.cs` (`:15-44`) | `MerchantRuntimeDbContext` (query path จริง) | **มี** `HasQueryFilter(x => x.MerchantId == context.CurrentMerchant)` + `TenantKeyDescriptor.Require(...)` |

`TenantKeyDescriptor.Require(...)` ไม่ได้แค่ validate โครง property — มันตั้ง
`property.IsConcurrencyToken = true` บน `MerchantId` จริงด้วย (`TenantKeyDescriptor.cs:50`) นี่คือ
ต้นเหตุของ bug การเขียนจริงที่ระบุไว้ในหัวข้อ "จุดที่ไม่สมมาตร" ข้อ 1 ด้านล่าง

Mapping ของ runtime config เหมือน migration-owner เป๊ะทุก field/index/FK — **ต่างจาก entity อื่น
ตรงที่ `Items` relationship ไม่ถูกตัดออก** (comment ยืนยัน `:9-13`: entity อื่นในระบบมักตัด
relationship ทิ้งเหลือ scalar-only ที่ runtime config แต่ `Cart._items` เป็น CLR navigation จริงที่
domain logic (`AddItem`/`RemoveItem`/`Clear`) พึ่งพา และ `shop.CartItems` ถูก map โดย context
เดียวกันนี้เอง — ถือเป็น "same-cluster, aggregate-internal relationship" ไม่ใช่ cross-module)

`CartRepository : ICartRepository` (`internal sealed`, `CartRepository.cs:12-24`):
```csharp
public void Add(Cart cart) => _db.Set<Cart>().Add(cart);
public Task<Cart?> GetAsync(Guid cartId, CancellationToken ct) =>
    _db.Set<Cart>().Include(c => c.Items).FirstOrDefaultAsync(c => c.Id == cartId, ct);
```
`GetAsync` **ไม่กรอง `MerchantId` เอง** — พึ่ง global query filter บน `MerchantRuntimeDbContext`
ทำหน้าที่กรองแทน (การเช็ค `cart.MerchantId != merchantId` ใน Application layer จึงเป็นแค่
belt-and-braces ชั้นสอง)

`MerchantRuntimeDbContext.OnModelCreating` (`:93-94`) เรียก `ApplyConfiguration(new
CartConfiguration(this))` + `ApplyConfiguration(new ItemConfiguration(this))`; DbSets
`Carts`/`CartItems` (`:69-70`)

DI: `ICartRepository` bind ที่ `MerchantRuntimePersistenceRegistration.AddMerchantRuntimePersistence`
(`:53`, `services.AddScoped<ICartRepository, CartRepository>()`) — `Program.cs` เรียก
`AddMerchantRuntimePersistence(...)` **คนละบรรทัด** จาก `AddCartModule()` (`:152`) **คนที่ grep
หา DI registration ของ `ICartRepository` ใน `Carts.Infrastructure` จะไม่เจอเลย** ต้องรู้ว่าไปรวม
กับ repository ของโมดูลอื่น (`Products`, `Checkouts`, `Orders`) ใน merchant-runtime persistence
cluster แทน `IUnitOfWork` ที่ handler ทุกตัวใช้ = `MerchantRuntimeUnitOfWork` (bind บรรทัดเดียวกัน)

## API endpoints (`src/Hosts/Api/Program.cs:656-745`)

Tag `"ตะกร้าสินค้า"`. ทุก endpoint gate ด้วย `.RequireAuthorization("merchant-user")` เท่านั้น —
**ไม่มี `.RequirePermission(...)` เพิ่มเติมบนเส้นใดเลย** (ต่างจาก payments/policy endpoint ที่มี
permission gate เสริม) route จริงมี prefix `/api/v1` เสมอ (`var api = app.MapGroup("/api/v1")`
ที่ `Program.cs:583`)

| Method | Route | Line | Command/Query | Success | Error |
|---|---|---|---|---|---|
| POST | `/api/v1/carts` | 658-668 | `CreateCartCommand(actor.MerchantId)` | 200 `CreateCartResponse(CartId)` | 401 (ไม่มี body — merchant มาจาก principal) |
| POST | `/api/v1/carts/{cartId:guid}/items` | 670-692 | `AddItemToCartCommand` | 200 `AddItemResult` | 400, 401 (+409 undeclared — ดูด้านล่าง) |
| GET | `/api/v1/carts/{cartId:guid}` | 694-706 | `GetCartQuery` | 200 `CartView` | 404 (explicit `Results.NotFound()`), 401 |
| DELETE | `/api/v1/carts/{cartId:guid}/items/{productId:guid}` | 708-719 | `RemoveItemFromCartCommand` | 200 `CartView` | 401 (+409 undeclared) |
| PUT | `/api/v1/carts/{cartId:guid}/items/{productId:guid}` | 721-732 | `SetCartItemQuantityCommand` | 200 `CartView` | 401 (+400/409 undeclared) |
| POST | `/api/v1/carts/{cartId:guid}/clear` | 734-745 | `ClearCartCommand` | 200 `CartView` | 401 (+409 undeclared) |

**`POST /api/v1/carts/{cartId}/items`** (`:670-692`) — request `AddItemToCartRequest(ProductId,
Quantity)`. ราคา**ไม่ได้มาจาก client เลย**: endpoint เรียก `GetProductByIdQuery(body.ProductId)`
ก่อนเสมอ, เช็ค `product is null || product.PaymentStatus != PaymentStatus.UNPAID` → 400 "Unknown
or inactive product." (เอกสารที่ `PAID` แล้วคือขายไปแล้ว เพิ่มซ้ำไม่ได้, REQ-2.1) แล้วค่อย mint
`Money.Of(product.TotalPremium, "THB")` — **THB ถูก hardcode ที่จุดนี้จุดเดียวในระบบทั้งหมด**
(comment ยืนยันตรงๆ ว่า "the single currency boundary... THB is minted here and nowhere else",
REQ-8.4)

**`GET /api/v1/carts/{cartId}`** (`:694-706`) — จุดเดียวใน 6 endpoint ที่ทำ `null → 404` แบบ
explicit ถูกต้อง (`view is null ? Results.NotFound() : Results.Ok(view)`)

**`DELETE`/`PUT`/`POST .../clear`** คืน `CartView` เสมอเมื่อสำเร็จ (`DELETE` แม้ `productId` ไม่เจอ
ในตะกร้าก็ไม่ throw — ตาม domain rule ที่ `RemoveItem` เป็น no-op)

## จุดที่ไม่สมมาตร (known gaps)

4 จุดจริงจากโค้ด ไม่ใช่ข้อเสนอแนะ — เก็บไว้ให้คนที่จะแก้ endpoint นี้เห็นก่อนเริ่มงาน (ข้อ 1 คือ
bug ที่ทำให้ endpoint ใช้งานจริงไม่ได้ ไม่ใช่แค่ metadata gap):

1. **`POST .../items` ปกติพังจริงบน SQL Server (bug เดิม ยังไม่ได้แก้)** —
   `TenantKeyDescriptor.Require` ตั้ง `property.IsConcurrencyToken = true` บน `MerchantId` ของทั้ง
   `Cart` และ `Item` (model-only token ไม่มี column `RowVersion` เพิ่ม — ดูหัวข้อ Domain model/
   Runtime persistence) ผสมกับ composite FK relationship ทำให้ flow ปกติ (สร้าง cart ด้วย
   `CreateCartCommand` แล้วเพิ่ม item ด้วย `AddItemToCartCommand` เป็นคนละ request — การใช้งานจริง
   ของ production) โยน `DbUpdateConcurrencyException` ("expected to affect 1 row(s), but actually
   affected 0") → `ConcurrencyConflictException` → **409 ทุกครั้ง** ยืนยันแล้วทั้งบน SQLite และ
   SQL Server จริง (`pol-db` container) เป็น bug ที่มีมาตั้งแต่ `rls-to-query-filter` (PR #112,
   commit `1af6dc2`) ถูก flag ซ้ำระหว่าง `insurance-pivot` และ `products-sp-53-alignment` แต่ยังไม่
   มี dedicated bugfix spec เปิดแก้ (ดู `.ai/specs/insurance-pivot/tasks.md` บรรทัด 39 และ
   `.ai/specs/products-sp-53-alignment/HANDOFF.md` §"ปัญหาที่เจอและไม่ได้แก้" ข้อ 2)
2. **409 vs 404 สำหรับ "cart ไม่พบ"** — Cart module ไม่ใช้ custom exception type
   (`NotFoundException`/`ConflictException`) เลยแม้แต่จุดเดียว ใช้ BCL exception ล้วน
   (`InvalidOperationException`, `ArgumentException`) `ProblemDetailsExceptionHandler.Map`
   (`BuildingBlocks.Web/ProblemDetailsExceptionHandler.cs:54-74`) map `InvalidOperationException
   → 409`, `ArgumentException`(รวม subclass `ArgumentOutOfRangeException`) `→ 400` ผลคือ "cart
   ไม่พบ" ผ่าน **ทุก write endpoint** คืน **409 Conflict** ไม่ใช่ 404 — ต่างจาก `GetCartQuery` ที่
   คืน 404 จริงทาง explicit null check สำหรับ cart เดียวกันที่ไม่มีอยู่จริง
3. **OpenAPI under-declare** — `.ProducesProblem` ของ `AddCartItem`/`RemoveCartItem`/
   `SetCartItemQuantity`/`ClearCart` ไม่มีเส้นไหนประกาศ `Status409Conflict` เลย ทั้งที่ runtime
   คืนจริงได้บ่อย (cart ไม่พบ, cart ไม่ `Open`, currency mismatch, และ concurrency conflict ตาม
   ข้อ 1 ด้านบน); `SetCartItemQuantity` ก็ไม่ประกาศ `Status400BadRequest` ทั้งที่ `quantity <= 0`
   คืน 400 จริง (`AddCartItem` ประกาศ 400 ไว้ครบแล้วที่ `Program.cs:691`) — เป็น documentation gap
   ใน OpenAPI/Scalar metadata ไม่ใช่ bug เชิงพฤติกรรม
4. **ข้อความ throw ไม่สม่ำเสมอ** — `AddItemToCartHandler` (inline check) กับ `CartEdits.cs`'s
   `CartLoad.RequireAsync` throw ข้อความคนละแบบสำหรับ "cart ไม่พบ"/"merchant ไม่ตรง" (ดูหัวข้อ
   Application layer) — runtime HTTP status เหมือนกันทั้งคู่ (409) ไม่รั่วออก response จริงเพราะ
   `ProblemDetailsExceptionHandler` ไม่ echo `exception.Message` (log เต็มอยู่ฝั่ง server เท่านั้น)

## Migration history

Migration owner เดียว: `BuildingBlocks.Infrastructure/Persistence/Migrations/` (ผูกกับ
`PolDbContext`) มี 3 migration แตะ `shop.Carts`/`shop.CartItems`:

| Migration | ผล |
|---|---|
| `20260712185344_InitialSchema` | สร้าง `shop.Carts` (`Id` PK, `MerchantId`, `Status` nvarchar(16), `CreatedAt`) + `shop.CartItems` (`Id` PK, `CartId`, `ProductId`, `Quantity`, `UnitPriceAmount decimal(19,4)`, `UnitPriceCurrency char(3)`) + FK เดี่ยว `FK_CartItems_Carts_CartId → Carts(Id)` cascade + index `IX_CartItems_CartId` |
| `20260712185646_SecurityObjects` | เพิ่ม SQL Server RLS: predicate function `sec.fn_merchant_predicate` (Carts) และ `sec.fn_cartitem_predicate` (CartItems — join ผ่าน parent Cart เพราะตอนนั้นยังไม่มี `MerchantId` ของตัวเอง) + GRANT ให้ 3 principal เดิม |
| `20260719081817_RlsTeardownAndOnePrincipal` | **การเปลี่ยนแปลง schema ใหญ่สุดของ Carts** — ถอด RLS ทั้งหมด; `DropForeignKey`+`DropIndex` FK เดี่ยวเดิม; **`AddColumn MerchantId`** (nullable ก่อน) บน `CartItems`; backfill `UPDATE` จาก parent `Cart`; `AlterColumn` เป็น NOT NULL; `AddUniqueConstraint AK_Carts_Id_MerchantId`; `CreateIndex IX_CartItems_CartId_MerchantId`; **`AddForeignKey` composite ใหม่** `FK_CartItems_Carts_CartId_MerchantId (CartId, MerchantId) → Carts(Id, MerchantId)` cascade; collapse principal เหลือ `pol_app` เดียว |

ไม่มี migration อื่นแตะตาราง `Carts`/`CartItems` อีกเลยหลังจากนั้น (grep ทั้ง repo ยืนยันแล้ว) —
สาเหตุของ migration ที่ 3 มาจาก spec `rls-to-query-filter` REQ-6: เปลี่ยนจาก DB-level RLS
(predicate function join ผ่าน parent) เป็น app-layer EF global query filter ต้องมี `MerchantId`
denormalized บน `CartItems` เองถึงจะ filter ได้โดยไม่ join

## Cross-reference

- ภาพรวม 6-layer เทียบกับ Products/Checkouts/Orders: [`layers-guide.md`](layers-guide.md) §5
  "2. Carts"
- Field-level schema เต็ม (`shop.Carts`/`shop.CartItems`, ตัวอย่างจาก seed data):
  [`entity-fields.md`](entity-fields.md)
- ภาพรวมธุรกิจ + feature-status table (gap: cart abandon/expiry job, revalidate ตอน checkout,
  `If-Match` concurrency — ทั้งหมดยังไม่มีจริง): [`platform-modules.md`](platform-modules.md) §6
  "Cart"
- File inventory: [`src-structure.md`](src-structure.md) §4.2
- โมดูลต้นทาง (ดึงราคา): [`products.md`](products.md)
- Spec ต้นกำเนิด `Item.MerchantId` denormalization: `.ai/specs/rls-to-query-filter/requirements.md`
  REQ-6
- Spec ยืนยัน currency mint boundary: `.ai/specs/products-sp-53-alignment/requirements.md` REQ-8.4
- Spec ที่อ้าง Cart เป็น baseline upstream (ไม่ได้แก้ `Carts.Domain` เอง):
  `checkout-chain-document-fields`, `policy-reference-record`, `insurance-pivot`
- Concurrency-token production bug (§"จุดที่ไม่สมมาตร" ข้อ 1): บันทึกครั้งแรกที่
  `rls-to-query-filter` (PR #112), ยืนยันซ้ำที่ `.ai/specs/insurance-pivot/tasks.md` และ
  `.ai/specs/products-sp-53-alignment/HANDOFF.md`

## Source of truth

`src/Modules/Carts/Carts.Domain/{Cart.cs,CartStatus.cs,Items/Item.cs}`,
`src/Modules/Carts/Carts.Application/{CreateCartCommand.cs,CreateCartHandler.cs,AddItemToCartCommand.cs,AddItemToCartHandler.cs,GetCart.cs,CartEdits.cs,AddItemResult.cs,ICartRepository.cs}`,
`src/Modules/Carts/Carts.Infrastructure/{CartConfiguration.cs,Items/ItemConfiguration.cs,CartModuleRegistration.cs}`,
`src/Persistence/Persistence.MerchantRuntime/Carts/{CartConfiguration.cs,Items/ItemConfiguration.cs,CartRepository.cs}`,
`src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/TenantKeyDescriptor.cs`,
`src/Hosts/Api/Program.cs` (route mapping, `:656-745`) — ตัวเลข/พฤติกรรมในไฟล์นี้ต้อง sync กับโค้ด
6 จุดนี้เสมอ
