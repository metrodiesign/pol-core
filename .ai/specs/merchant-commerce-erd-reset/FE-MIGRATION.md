# Frontend Migration: Merchant-Commerce ERD Reset

> Big-bang cutover. ไม่มี compatibility alias และห้าม fallback ไป route เก่า.

## Contract source

- Runtime document: `GET /openapi/v1.json` ใน Development
- Published Cart/Order subset: `openapi-cart-order.yaml`
- API base: `/api/v1`
- Auth: merchant-user BFF cookie
- Mutating routes: ส่ง CSRF cookie + header ตาม BFF contract เดิม

## Route mapping

| งาน frontend | Route ใหม่ | Route เก่า | การย้าย |
|---|---|---|---|
| เปิด Cart | `POST /carts` | เดิม | คง route |
| เพิ่มสินค้า | `POST /carts/{cartId}/items` | payload แบบเอกสารประกันเดิม | ส่ง `productCode`, `variantCode`, `quantity` เท่านั้น |
| อ่าน Cart | `GET /carts/{cartId}` | เดิม | render generic product/variant line |
| เปลี่ยนจำนวน | `PUT /carts/{cartId}/items/{itemId}` | เดิม | ใช้ `itemId`; ห้ามใช้ `productCode` เป็น key |
| ลบรายการ | `DELETE /carts/{cartId}/items/{itemId}` | เดิม | ใช้ `itemId` |
| ล้าง Cart | `POST /carts/{cartId}/clear` | เดิม | คง route |
| สร้าง Order | `POST /orders` | `POST /checkouts`, `POST /checkouts/{id}/confirm` | เรียกครั้งเดียวจาก Cart |
| ดู/ทิ้ง Checkout | ไม่มี | `GET /checkouts/{id}`, `POST /checkouts/{id}/abandon` | ลบ screen/state/API client |
| Policy CRUD/report | ไม่มี | route ที่มี `/policy` หรือ `/reports/policies` | ลบ screen/state/API client; ไม่มี replacement |

Route เก่าตอบ `404`. ห้าม retry ด้วย route เก่า.

## Add Cart item

Request:

```json
{
  "productCode": "DOC-000123",
  "variantCode": "Motor",
  "quantity": 2
}
```

`variantCode` รับ `Motor` หรือ `NonMotor`. ห้ามส่ง `unitPrice`, `variantName`, `saleCode`, `metadata`, PII หรือ credential. Server อ่าน source ปัจจุบันและกำหนดค่าทั้งหมดเอง.

Response `200`:

```json
{
  "cartId": "0198...",
  "itemCount": 1,
  "subtotal": { "amount": 2500.0000, "currency": "THB" }
}
```

อ่าน Cart ใหม่หลัง mutation เมื่อต้อง render line detail. `CartView.items[]` ใช้ `itemId`, `productCode`, `variantCode`, `variantName`, `quantity`, `unitPrice`, `lineTotal`, `metadata`.

## Cart to Order

Request `POST /orders`:

```json
{
  "cartId": "0198...",
  "customer": {
    "name": "Somchai Jaidee",
    "phone": "0812345678",
    "email": "buyer@example.com"
  }
}
```

`customer` optional. `amount` optionalและใช้เป็น claimed total เพื่อตรวจ mismatch เท่านั้น. แนะนำไม่ส่ง `amount`; server ใช้ยอด Cart ปัจจุบันเสมอ. Unknown field ถูก reject.

Response `201` พร้อม `Location: /api/v1/orders/{orderId}`:

```json
{
  "orderId": "0198...",
  "orderNo": "ORD-000001",
  "status": "Pending",
  "amount": { "amount": 2500.0000, "currency": "THB" }
}
```

สำเร็จแล้ว Cart เปลี่ยนเป็น `CheckedOut`. ปิดปุ่ม submit ทันทีระหว่าง request. ถ้า response หาย ให้ refresh Cart/Order state; ห้ามสร้าง Cart ใหม่อัตโนมัติ.

## Status mapping

| Wire status | UI state | การกระทำ |
|---|---|---|
| Cart `Open` | แก้รายการได้ | เปิด mutation controls |
| Cart `CheckedOut` | สร้าง Order แล้ว | ปิด mutation controls |
| Order `Pending` | รอชำระ | เปิด payment flow |
| Order `Paid` | ชำระแล้ว | terminal success |
| Order `Failed` | จ่ายไม่สำเร็จ | retry payment ตาม payment API |
| Order `Expired` | session หมดอายุ | สร้าง payment attempt ใหม่ได้ตาม API |
| Order `Cancelled` | ยกเลิก | terminal |
| Order `Refunded` | คืนเงิน | terminal |

## Error mapping

ทุก error ใช้ `application/problem+json`. UI ใช้ HTTP status เป็นหลัก; `title`/`detail` เป็นข้อความประกอบ ไม่ใช้ branch logic.

| HTTP | ความหมาย | UX/action |
|---|---|---|
| `400` | payload ผิด, Cart ว่าง, claimed amount mismatch, สินค้าไม่พร้อมขาย | แสดง validation; refresh Cart/Product ก่อน retry |
| `401` | session หมดอายุ/ไม่มี auth | เริ่ม login flow |
| `403` | ไม่มี permission, CSRF ผิด, merchant user ไม่มี `saleCode` | ปิด action; ให้ผู้ดูแลแก้สิทธิ์/profile |
| `404` | Cart/item ไม่พบหรือไม่ใช่ของ merchant นี้ | กลับ Cart list; ห้ามเผย existence ข้าม merchant |
| `409` | Cart ถูกแก้พร้อมกัน, Cart ไม่ `Open`, Order ถูกสร้างแล้ว | refresh Cart/Order; ห้าม auto-resubmit |
| `503` | source/DB/PSP dependency ใช้งานไม่ได้ | แสดง retryable outage; exponential backoff แบบจำกัด |

## Cutover checklist

- Generate client จาก OpenAPI ใหม่.
- ลบ Checkout model/store/screen/routes และ policy clientทั้งหมด.
- เปลี่ยน line identity เป็น `itemId`; แสดง generic `productCode`/`variantCode`.
- ไม่ส่ง price, metadata, `saleCode`, merchant ID หรือ credential จาก browser.
- รองรับ `201` + `Location` ของ Order.
- รองรับ error matrix และหยุด auto-retry บน `400/403/404/409`.
- ทดสอบ Cart -> Order -> Payment บน staging ก่อน release.
- ไม่มีการแก้ frontend repository ในงาน backend นี้; ทีม frontend consume artifact นี้แยก PR.
