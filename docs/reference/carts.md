# โมดูล Carts

> As-built 2026-08-13. Cart เป็น aggregate ของ merchant สำหรับเก็บรายการก่อนสร้าง Order โดยตรง.

## ขอบเขตปัจจุบัน

- ไม่มี persisted `Checkout` หรือ `CheckoutSession`.
- Cart flow คือ `Products → Cart → Order → Payment`.
- `MerchantId` และ `SaleCode` มาจาก authenticated merchant user; client ส่งสองค่านี้ไม่ได้.
- `Cart.Status` มี `Open` และ `CheckedOut`.
- `Cart.Version` เป็น application-managed optimistic concurrency token และเพิ่มทุก mutation รวมถึง line mutation.
- Cart ใช้ `MerchantRuntimeDbContext`; query filter และ guarded write จำกัด merchant.

## Domain

`Cart` เก็บ `MerchantId`, nullable `SaleCode`, nullable `OriginatorId`, `Status`, `CreatedAt`, `Version` และ `Items`.

`Cart.AddItem` รับค่าที่ server resolve แล้ว:

| Field | กฎ |
|---|---|
| `ProductCode` | identifier จาก upstream, trim แล้ว, ยาวไม่เกิน 150 |
| `SaleCode` | ค่า upstream/server, ยาวไม่เกิน 20 |
| `VariantCode` | ค่า upstream เช่น `CMI`, `VMI`, `FIRE`, `MISC`, ยาวไม่เกิน 64 |
| `VariantName` | display snapshot, nullable, ยาวไม่เกิน 128 |
| `Quantity` | มากกว่า 0 |
| `UnitPrice` | `Money` จาก `TotalPremium`, ไม่รับจาก client |
| `Metadata` | typed `CommerceItemMetadata`, ไม่รับ arbitrary JSON |

Cart ปฏิเสธ `ProductCode` ซ้ำในตะกร้าเดียวโดยไม่สนตัวพิมพ์. ทุก line ต้องใช้ currency เดียวกัน. line `Id`
เป็น server-minted opaque `Guid` และใช้เป็น mutation handle.

Mutation ที่ aggregate รองรับ: add, remove, set quantity, clear และ `MarkCheckedOut`. Cart ที่ไม่ใช่ `Open`
แก้ไขไม่ได้.

## API

ทุก route ใช้ `/api/v1` และ policy `merchant-user`. Mutation ใช้ user CSRF.

| Method | Path | ผลลัพธ์ |
|---|---|---|
| `POST` | `/api/v1/carts` | สร้าง Cart เปล่า, `200` |
| `POST` | `/api/v1/carts/{cartId}/items` | เพิ่ม line, `200` |
| `GET` | `/api/v1/carts/{cartId}` | อ่าน Cart, `200`; ไม่พบหรือไม่ใช่ merchant เดียวกัน `404` |
| `DELETE` | `/api/v1/carts/{cartId}/items/{itemId}` | ลบ line, `200`; ไม่พบ `itemId` `404` |
| `PUT` | `/api/v1/carts/{cartId}/items/{itemId}` | set quantity, `200`; ไม่พบ `itemId` `404` |
| `POST` | `/api/v1/carts/{cartId}/clear` | ล้าง lines, `200` |

### Add item

Request body ตรงกับ `AddItemToCartRequest`:

```json
{
  "productCode": "...",
  "variantCode": "CMI",
  "quantity": 1
}
```

`Program.cs` เรียก `LookupDocumentQuery` แบบ live แล้วตรวจ:

1. user มี `SaleCode` และ quantity/variant ถูกต้อง
2. document มีอยู่และ upstream ยังไม่รายงาน `PAID`
3. `IDocumentSaleProbe` ไม่พบเอกสารขายแล้วหรือ payment in flight

จากนั้นใช้ `DocumentNo` ของ upstream เป็น `ProductCode`, ใช้ `TotalPremium` เป็น price และสร้าง metadata
server-side. client ส่ง price, `SaleCode`, `VariantName` หรือ metadata ไม่ได้.

### Response

`CartView` คืน `CartId`, `SaleCode`, `Status`, `Version`, `Items`, `Subtotal`.
แต่ละ item คืน `ItemId`, `ProductCode`, `VariantCode`, `VariantName`, `Quantity`, `UnitPrice`, `LineTotal`
และ typed `Metadata`.

## Order handoff

`POST /api/v1/orders` สร้าง Order จาก Cart โดยตรง ไม่มี checkout endpoint. `OrderCreationCoordinator` lookup
document สดและ probe ซ้ำก่อนเปิด transaction. ใน transaction เดียว:

1. reload Cart และตรวจ `Status`, `Version` และ lines
2. สร้าง `shop.Orders` สถานะ `Pending`
3. สร้าง immutable `shop.OrderItems`
4. enqueue notification ใน `txn.OutboxMessages`
5. เรียก `MarkCheckedOut()`

การแก้ Cart ที่แข่งกับ order creation ได้ `ConcurrencyConflictException`; ไม่มี silent interleave.

## Persistence

`shop.Carts` และ `shop.CartItems` อยู่ใน `MerchantRuntimeDbContext`.

`shop.CartItems` มี `Id`, `CartId`, `MerchantId`, `ProductCode`, `SaleCode`, `VariantCode`, `VariantName`,
`Quantity`, `UnitPriceAmount`, `UnitPriceCurrency` และ `Metadata` native `json`. มี composite parent boundary
`(CartId, MerchantId)` และ line `Id` ไม่ generated โดย database.

Current migration chain ถึง `20260811024015_AdminDeliveryRuntimeGrants`; รายการเต็มอยู่ใน
[`entity-fields.md`](entity-fields.md).

ไม่มี SQL RLS; isolation ใช้ app query filter, actor context และ sealed write guard.

## Source of truth

- `src/Modules/Carts/Carts.Domain/Cart.cs`
- `src/Modules/Carts/Carts.Domain/Items/Item.cs`
- `src/Modules/Carts/Carts.Application/GetCart.cs`
- `src/Modules/Carts/Carts.Application/AddItemToCartCommand.cs`
- `src/Modules/Carts/Carts.Infrastructure/Items/ItemConfiguration.cs`
- `src/Hosts/Api/Program.cs`
- `src/Hosts/Api/Orders/OrderCreationCoordinator.cs`
