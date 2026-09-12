# Orders Module Reference

เอกสารนี้อธิบาย Order flow ปัจจุบัน โดย canonical create คือ `CreateOrderCommand` ที่ `POST /api/v1/orders`; `/api/v1/orders/from-cart` เป็น compatibility route ที่ยังเก็บไว้ตาม inventory.

## Canonical create และ owner authorization

Request ใช้ `CreateOrderRequest` และมี `businessType`, `currency`, `items`, adjustment, optional owner, metadata, notification intent และ `issueNow`. Client ส่ง product reference และ client snapshot ได้เพื่อให้ระบบตรวจ mismatch แต่ไม่สามารถกำหนด trusted price, source currency หรือ Agent owner เอง.

ลำดับของ `CreateOrderHandler` คือ:

1. resolve `Account`/`Access` authorization และ merchant context จาก authenticated identity
2. resolve owner ผ่าน `IOrderOwnerResolver`; Agent ใช้ Sale ที่ผูกกับ Account, Employee/System/merchant path ใช้ policy และ branch access
3. เรียก `IOrderSourcePolicy`/`ITrustedOrderPricingSource` เพื่อ lookup source และสร้าง trusted line snapshot
4. ตรวจ currency, quantity, line formula, adjustment และ client snapshot mismatch
5. acquire Account authorization lease/idempotency แล้วสร้าง Order + items ใน transaction เดียว
6. ถ้า `issueNow=true` freeze Order เป็น `Open`, ออก PaymentLink แรก และ enqueue notification intent ใน outbox เดียวกัน

`issueNow=false` สร้าง `Draft` โดยไม่มี PaymentLink. Success คือ `201` พร้อม `OrderCommandResult`; replay ด้วย idempotency key เดิมคืนผลที่เก็บไว้. Missing owner, wrong merchant, inactive Account/Access หรือ stale authorization เป็น deny/conflict ตาม error contract.

## Legacy Cart route

`POST /api/v1/orders/from-cart` ใช้ `OrderCreationCoordinator` สำหรับ Cart compatibility. Merchant-user branch รับ Cart ของ actor และ SaleCode จาก server; Admin branch ต้องส่ง `merchantId`/`originatorId` และตรวจ scope. Coordinator reload Cart, revalidate upstream document/payment state/sale probe แล้ว commit Order, immutable lines, customer notification outbox และ `Cart.CheckedOut` ใน transaction เดียว.

เส้นทางนี้ไม่ใช่ canonical Account order DTO และไม่มีสิทธิ์ให้ client override trusted price.

## Aggregate และ lifecycle

`Order` เก็บ `MerchantId`, `OrderNo`, business/owner snapshot, customer contact, `Money` totals, `PaymentStatus`, `SuccessfulTransactionId`, `PaymentSessionId` compatibility pointer, `Version`, line snapshots และ notification intent.

สถานะ lifecycle มี `Draft`, `Open`, `Pending`, `Paid`, `Failed`, `Expired`, `Refunded`, `Cancelled`; flow ใหม่ใช้ `Draft -> Open` ก่อน payment และแยก `PaymentStatus` (`Unpaid`, `Processing`, `Paid`) จาก lifecycle.

Order item เก็บ `ProductCode`, `VariantCode`, display name, quantity, `UnitPrice`, `Discount`, `TaxAmount`, `LineAmount`, trusted metadata และ `VersionedMetadata` ของ client request. `Metadata` สองชุดแยกกันเพื่อไม่ให้ client JSON กลายเป็น trusted product fact.

Order detail ที่เปิด server-owned metadata ต้อง append `OrderItemRevealAudit` ต่อ line ก่อนสร้าง response; ถ้า audit save ไม่สำเร็จ handler fail closed. List/summary ใช้ข้อมูล mask และ customer capability summary ไม่คืน merchant/session internals.

## PaymentLink และ endpoint matrix

| Method | Path | พฤติกรรม |
|---|---|---|
| `POST` | `/api/v1/orders` | canonical create; `issueNow` branch; identity-platform authorization |
| `PATCH` | `/api/v1/orders/{orderId}` | patch Draft พร้อม trusted repricing และ `If-Match` |
| `POST` | `/api/v1/orders/{orderId}/issue` | freeze Draft และสร้าง PaymentLink แรก |
| `GET` | `/api/v1/orders/{orderId}/payment-links` | รายการ link โดยไม่คืน raw token |
| `POST` | `/api/v1/orders/{orderId}/payment-links` | revoke active/rotate link ใน transaction เดียว |
| `POST` | `/api/v1/payment-links/{linkId}/revoke` | revoke capability โดยไม่ย้อน PSP result |
| `POST` | `/api/v1/orders/{orderId}/cancel` | cancel หลังปล่อย open payment session ตาม guard |
| `POST` | `/api/v1/orders/{token}/summary/resend` | compatibility summary resend/rotation |
| `POST` | `/api/v1/orders/from-cart` | legacy Cart-to-Order compatibility |

`PaymentLink` เก็บ keyed `TokenHash`, status, expiry, rotation pointer และ versionใน `checkout.PaymentLinks`. `PaymentLinkReplay` เก็บ request hash และ Data Protection ciphertext ของผล replay; raw token ไม่อยู่ durable row. Customer แลก token ที่ `/api/v1/checkout/access` แล้วได้ short-lived capability ผูก Order/link/version/cookie.

## Read isolation

Merchant-user Order read ใช้ `MerchantId` และ `InitiatingMerchantUserId` filter. Canonical identity read ตรวจ `AccessEvaluator.CanReadOrder` จาก owner Sale/Branch ก่อน query/detail/items/history. Admin read ใช้ accessible merchant scope และ named admin reader. Child, history, transaction และ export path ต้อง resolve parent Order/merchant ก่อนตอบ เพื่อกัน IDOR.

## Persistence และ events

| Data | Current owner |
|---|---|
| `shop.Orders`, `shop.OrderItems`, `shop.OrderItemRevealAudits` | `CommerceDbContext` |
| `checkout.PaymentLinks`, `checkout.PaymentLinkReplays` | `CommerceDbContext` |
| `txn.OutboxMessages` | `CommerceDbContext` |
| `PaymentPaid`, `PaymentFailed`, `PaymentExpired` | `src/Application/Contracts/` compatibility event seam |
| `Transaction`/`TransactionEvent` | `Payments.Domain` + `Platform.Application.Transactions`; canonical payment attempt evidence |

Payment event consumers continue to verify OrderId and amount/currency. For the new checkout transaction flow, `Order.ApplySuccessfulTransaction` pins the first successful Transaction and keeps duplicate success evidence without silently moving the pointer.

## Source of truth

- `src/Application/Modules/Orders.Application/OrderWorkflow.cs`
- `src/Application/Modules/Orders.Application/OrderPaidConsumer.cs`
- `src/Domain/Modules/Orders.Domain/Order.cs`
- `src/Domain/Modules/Orders.Domain/Items/Item.cs`
- `src/Domain/Modules/Checkouts.Domain/PaymentLink.cs`
- `src/Domain/Modules/Checkouts.Domain/PaymentLinkReplay.cs`
- `src/Api/Api/Program.cs`
- `src/Api/Api/ControlPlane/CanonicalCommerceEndpoints.cs`
- `src/Api/Api/Iam/IdentityRequestAuthorization.cs`
- `src/Infrastructure/Modules/Products.Infrastructure/Orders/TrustedOrderPricingSource.cs`
- `src/Infrastructure/Persistence/Persistence.MerchantRuntime/Orders/`
