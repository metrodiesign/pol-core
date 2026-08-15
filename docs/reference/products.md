# โมดูล Products

> As-built 2026-08-13. โมดูลนี้อ่านเอกสารประกันจากระบบต้นทางแบบ live; ไม่มี product catalogue ในฐานข้อมูลของเรา.

## บทบาทปัจจุบัน

- `ISpDocumentGateway` เรียก stored procedure ต้นทาง `usp_Motor_SearchDocument` หรือ
  `usp_NonMotor_SearchDocument`.
- `GET /api/v1/products` เป็น read-only endpoint เดียวของโมดูล.
- ไม่มี `shop.Products`, surrogate `Guid` หรือ repository สำหรับ product.
- source identifier ในผลลัพธ์คือ `DocumentNo`; downstream ใช้ `ProductCode` เป็นชื่อ field ของเอกสารเดียวกัน.
- `SaleCode` มาจาก authenticated merchant user ฝั่ง server; client เลือกเองไม่ได้.

ไม่มี `Checkout` ใน current flow. เส้นทางซื้อคือ `Products → Carts → Orders → Payments`.

## List contract

`GET /api/v1/products` อยู่ใต้ `/api/v1`, authorization policy `merchant-user` และรับ:

- `page`, `limit`; `limit` ถูก cap ที่ 25
- `productFilters` JSON ตาม typed `ProductFilterDto`

`productFilters` รองรับ `searchText`, `insuredName`, `policyNo`, `applicationNo`, `documentType`,
`productGroup`, `insuranceType`, `countMode`, `paymentStatus`, ช่วงวันที่ coverage และช่วง `paidDate`.

กฎสำคัญ:

- ต้องเลือกฝั่งด้วย `insuranceType` หรือ `productGroup`; ถ้าระบุทั้งคู่ต้องสอดคล้องกัน.
- `paymentStatus` รับ `UNPAID`, `PAID`, `ALL`; ค่าเริ่มต้น `UNPAID`.
- `countMode` รับ `EXACT`, `FAST`; ค่าเริ่มต้น `EXACT`; `FAST` ทำให้ `totalRows` และ `totalPages` เป็น `null`.
- `saleCode` และ `branchCode` เป็น server/config value; field จาก client ถูกเมิน.
- ไม่มี generic SFS `filters`/`sort` บน endpoint นี้.
- merchant user ที่ไม่มี `SaleCode` ได้ `403` ก่อน parse filter; malformed หรือผิดกฎได้ `400`; upstream ใช้งานไม่ได้ได้ `503`.

Response คือ `ProductPage` โดยแต่ละ `ProductListItem` เป็น field snapshot จาก upstream และมี `soldByPlatform` เพิ่มเติม.
ไม่มี `id` ของระบบเรา. เมื่อ `paymentStatus=UNPAID` ระบบตัดเอกสารที่ platform ขายแล้วออก; เมื่อ `ALL` หรือ `PAID`
คงรายการและตั้ง `soldByPlatform=true`.

## Cart integration

`POST /api/v1/carts/{cartId}/items` รับ body ที่ตรงกับโค้ด:

```json
{
  "productCode": "...",
  "variantCode": "CMI",
  "quantity": 1
}
```

server ทำงานดังนี้:

1. validate `quantity` และ `variantCode`.
2. เรียก `LookupDocumentQuery` ไปยัง upstream ด้วย `ProductCode`, variant และ authenticated `SaleCode`.
3. ใช้ `TotalPremium` เป็น `UnitPrice`; สร้าง typed `CommerceItemMetadata` จาก document facts.
4. ตรวจ upstream `PaymentStatus` และ `IDocumentSaleProbe` กันเอกสารขายแล้วหรือกำลังจ่าย.
5. เพิ่ม line ด้วย server-owned `ProductCode`, `SaleCode`, `VariantCode`, `VariantName`, price และ metadata.

client ห้ามส่ง price, `SaleCode`, `VariantName` หรือ metadata เพื่อกำหนดค่า line. `itemId` เป็น server-minted
opaque `Guid` สำหรับ `DELETE`/`PUT`; ไม่ใช้ `ProductCode` เป็น path segment.

`GET /api/v1/carts/{cartId}` คืน `itemId`, `productCode`, `variantCode`, `variantName`, `quantity`, `unitPrice`,
`lineTotal` และ typed `metadata` พร้อม `Version` ของ Cart.

## Direct Cart-to-Order

`POST /api/v1/orders` ไม่มี persisted checkout session. `OrderCreationCoordinator` ทำก่อนเปิด transaction:

- reload Cart และตรวจสถานะ/line
- lookup document สดทุก line
- ตรวจ upstream payment state และ `IDocumentSaleProbe`

จากนั้น transaction เดียวทำ:

1. ตรวจ Cart `Version` และ line snapshot ซ้ำ
2. สร้าง `shop.Orders` สถานะ `Pending`
3. สร้าง immutable `shop.OrderItems`
4. enqueue customer notification ใน `txn.OutboxMessages`
5. เปลี่ยน Cart เป็น `CheckedOut`

`OrderItems` เก็บ `ProductCode`, `VariantCode`, `VariantName`, quantity, `UnitPrice`, zero `Discount` และ typed
metadata. `OrderItems` ไม่เก็บ upstream document ทุก field; field เฉพาะ document อยู่ใน metadata ที่ allowlist.

## กันขายซ้ำ

`IDocumentSaleProbe` อยู่ใน `BuildingBlocks.Application`; implementation อยู่
`Persistence.MerchantRuntime/Orders/DocumentSaleProbe.cs`.

- probe ใช้คู่ `(ProductCode, VariantCode)`.
- อ่าน `shop.OrderItems`, `shop.Orders` และ `txn.PaymentSessions` ข้าม merchant ด้วย `IgnoreQueryFilters()` ตาม
  cross-merchant sale rule.
- Cart add และ order creation ปฏิเสธ document ที่ขายแล้วหรือมี payment in flight.
- Products list ใช้ probe หนึ่งครั้งต่อหน้า ไม่ยิง query ต่อแถว.
- `DoubleSellAuditor` บันทึก critical evidence หากพบ order `Paid` ซ้ำ; ไม่ใช่กลไกแทน transaction guard.

## Persistence shape

ไม่มี table `shop.Products`. Current line tables:

| Table | Field หลัก |
|---|---|
| `shop.CartItems` | `ProductCode` (`nvarchar(150)`), `SaleCode` (`varchar(20)`), `VariantCode` (`varchar(64)`), `VariantName`, `Quantity`, `UnitPriceAmount`, `UnitPriceCurrency`, `Metadata` (`json`) |
| `shop.OrderItems` | `ProductCode` (`nvarchar(150)`), `VariantCode` (`varchar(64)`), `VariantName`, `Quantity`, `UnitPrice*`, `Discount*`, `Metadata` (`json`) |

`ProductCode` เป็น source identifier ที่ trim แล้ว; comparison สำหรับ Cart duplicate ไม่สนตัวพิมพ์.
`Metadata` เป็น typed allowlist; arbitrary JSON และ PII ไม่ได้รับอนุญาต.

## โครงสร้างไฟล์

| Path | หน้าที่ |
|---|---|
| `src/Modules/Products/Products.Application/ListProducts.cs` | `ProductFilterDto`, `ListProductsQuery`, `ProductPage`, `ProductListItem`, live list handler |
| `src/Modules/Products/Products.Application/LookupDocument.cs` | internal document lookup สำหรับ Cart และ Order creation |
| `src/Modules/Products/Products.Application/Ports/ISpDocumentGateway.cs` | upstream port |
| `src/Modules/Products/Products.Infrastructure/Sp/SpDocumentGateway.cs` | ADO.NET stored-procedure adapter |
| `src/Hosts/Api/Program.cs` | `/api/v1/products` และ Cart add-item composition |
| `src/Hosts/Api/Orders/OrderCreationCoordinator.cs` | live revalidation และ atomic Cart-to-Order |
| `src/Persistence/Persistence.MerchantRuntime/Orders/DocumentSaleProbe.cs` | cross-module sale probe |

## Retired contract

ไม่มี current project/table/route สำหรับ `CheckoutSession`, `CheckoutSessionItems`, `CheckoutConfirmed` หรือ
`/api/v1/checkouts*`. เอกสารหรือ client ที่ยังใช้ `ProductId`, `documentNo` ใน Cart body หรือ `productGroup` เป็น
ชื่อ request field ต้อง migrate เป็น `productCode` และ `variantCode` ตาม contract ปัจจุบัน.

## Source of truth

- `src/Modules/Products/Products.Application/ListProducts.cs`
- `src/Modules/Products/Products.Application/LookupDocument.cs`
- `src/Modules/Products/Products.Infrastructure/Sp/SpDocumentGateway.cs`
- `src/Hosts/Api/Program.cs`
- `src/Hosts/Api/Orders/OrderCreationCoordinator.cs`
- `src/Persistence/Persistence.MerchantRuntime/Orders/DocumentSaleProbe.cs`
