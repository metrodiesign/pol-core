# pol-core API — Carts, products และ order lifecycle (Sequence Diagrams)

> Source: `docs/reference/api-endpoints.md` section "Commerce, checkout และ orders" บรรทัด L84-L89, L95-L104, L109-L110 และ source ที่อ้างต่อ § (`src/Api/Api/Program.cs`, `src/Application/Modules/Carts.Application/*.cs`, `src/Domain/Modules/Carts.Domain/Cart.cs`, `src/Application/Modules/Orders.Application/*.cs`, `src/Api/Api/Orders/OrderCreationCoordinator.cs`, `src/Application/Modules/Products.Application/ListProducts.cs`)
> Scope: 18 endpoints เดียวกับ `04-carts-products-orders.activities.md` (หมายเลข § ตรงกัน) แสดงลำดับข้าม actor / API / service / DB / PSP / async worker
> Generated: 2026-09-14

| § | Diagram | Endpoints |
| --- | --- | --- |
| 4.1 | เปิด/ดูตะกร้าสินค้า (dual audience) | `POST /api/v1/carts`, `GET /api/v1/carts/{cartId:guid}` |
| 4.2 | แก้ไขรายการในตะกร้า (เพิ่ม/ลบ/ปรับจำนวน/ล้าง) | `POST /api/v1/carts/{cartId:guid}/items`, `DELETE /api/v1/carts/{cartId:guid}/items/{itemId:guid}`, `PUT /api/v1/carts/{cartId:guid}/items/{itemId:guid}`, `POST /api/v1/carts/{cartId:guid}/clear` |
| 4.3 | อ่านคำสั่งซื้อ (รายละเอียด/รายการ/สถานะลิงก์) | `GET /api/v1/orders/{orderId:guid}`, `GET /api/v1/orders`, `GET /api/v1/orders/{orderId:guid}/payment-links` |
| 4.4 | สร้างคำสั่งซื้อแบบ canonical | `POST /api/v1/orders` |
| 4.5 | สร้างคำสั่งซื้อจากตะกร้า | `POST /api/v1/orders/from-cart` |
| 4.6 | ส่งออกรายการคำสั่งซื้อ | `GET /api/v1/orders/export` |
| 4.7 | ยกเลิกคำสั่งซื้อ | `POST /api/v1/orders/{orderId:guid}/cancel` |
| 4.8 | ออก/หมุน PaymentLink ของคำสั่งซื้อ | `POST /api/v1/orders/{orderId:guid}/payment-links`, `POST /api/v1/orders/{orderId:guid}/issue` |
| 4.9 | ส่งลิงก์สรุปคำสั่งซื้อซ้ำ | `POST /api/v1/orders/{orderId:guid}/summary/resend` |
| 4.10 | ผลิตภัณฑ์ (แคตตาล็อกเอกสารประกัน) | `GET /api/v1/products`, `GET /api/v1/products/documents` |

---

## 4.1 เปิด/ดูตะกร้าสินค้า

Merchant Console สร้างตะกร้าทันที ส่วน Admin Console ต้องผ่าน originator + idempotency (source: `Program.cs:1395-1434,3719-3872`, `AdminOperationExecutor.cs:24-60`, `CreateCartHandler.cs:8-29`)

```mermaid
sequenceDiagram
    autonumber
    actor U as Merchant user / Admin
    participant CON as Merchant/Admin Console
    participant API as API<br/>Program.cs (carts)
    participant EXE as AdminOperationExecutor<br/>ดู § 0.5
    participant SVC as CartService<br/>CreateCartHandler
    participant DB as SQL Server<br/>merch.Carts

    Note over CON,API: Phase A — authz (ดู § 0.1) + CSRF (ดู § 0.3)
    U->>CON: เปิดตะกร้าใหม่
    CON->>API: POST /api/v1/carts (+ query merchantId, originatorId เมื่อ Admin Console)
    alt Merchant Console
        API->>SVC: CreateCartCommand(actor.MerchantId, actor.SaleCode)
        SVC->>DB: INSERT Cart Status Open, Version 0
        DB-->>SVC: cartId
        API-->>CON: 200 CreateCartResponse{cartId}
    else Admin Console
        API->>API: merchantId/originatorId เป็น GUID ไม่ว่าง, อยู่ใน scope.Accessible, RequireCommerceOriginatorAsync (active, มี SaleCode)
        alt query ผิดรูป
            API-->>CON: 400 invalid merchantId/originatorId
        else นอก scope
            API-->>CON: 403 merchant_scope_forbidden
        else originator ไม่พร้อม
            API-->>CON: 403 originator_scope_forbidden
        else ผ่านทุกเงื่อนไข
            API->>API: actorScope.Begin(merchantId), Idempotency-Key ถูกต้อง ดู § 0.5
            alt Idempotency-Key ผิดรูป
                API-->>CON: 400 invalid_idempotency_key
            else ถูกรูป
                API->>EXE: ExecuteAsync(cart.create) ดู § 0.5
                EXE->>DB: SELECT AdminOperationRecords ตรง key = merchant+admin+operation+key
                alt มี record เดิม, hash ต่าง
                    EXE-->>API: idempotency_key_reused
                    API-->>CON: 409 idempotency_key_reused
                else มี record เดิม, InProgress
                    API-->>CON: 409 operation_in_progress
                else มี record เดิม, Succeeded
                    EXE-->>API: ผลเดิม (Replayed=true)
                    API-->>CON: 200 ผลเดิม
                else ไม่มี record
                    EXE->>SVC: CreateCartCommand(merchantId, originator.SaleCode, originatorId)
                    SVC->>DB: INSERT Cart ผูก OriginatorId, commit ในธุรกรรมเดียวกับ record
                    DB-->>SVC: cartId
                    API-->>CON: 200 CreateCartResponse{cartId} (Replayed=false)
                end
            end
        end
    end
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/carts/{cartId:guid}` | permission `payment.view`, ไม่มี CSRF (safe method), ไม่มี idempotency/If-Match; Merchant: `GetCartQuery` คืน `null` ทั้งไม่พบและเป็นของ merchant อื่น -> 404 ตรง (คืนผลตรง ไม่เคยตั้ง ETag); Admin: ไม่ต้องส่ง query `merchantId`/`originatorId`, ใช้ `RequireAdminCartAsync(expectedMerchantId: null)`; **เฉพาะ Admin เท่านั้น** ที่ตอบ `ETag = vN` เมื่อพบ (`VersionEtags.Set`) — ฝั่ง Merchant ไม่มี ETag เลย (source: `Program.cs:1491-1524`) |

---

## 4.2 แก้ไขรายการในตะกร้า

`AddCartItem` เดินผ่านการยืนยันเอกสารจากต้นทางสด (SP) ก่อนเขียน; remove/set-quantity/clear ไม่มีขั้นนี้แต่ (เฉพาะ Admin) บังคับ `If-Match` เสมอ (source: `Program.cs:1436-1489,1526-1630`, `AddItemToCartHandler.cs`, `CartEdits.cs`)

```mermaid
sequenceDiagram
    autonumber
    actor U as Merchant user / Admin
    participant CON as Merchant/Admin Console
    participant API as API<br/>Program.cs (carts/items)
    participant SP as Upstream SP<br/>เอกสารประกัน (ext)
    participant EXE as AdminOperationExecutor<br/>ดู § 0.5
    participant SVC as CartService<br/>AddItemToCartHandler
    participant DB as SQL Server<br/>merch.Carts

    Note over CON,API: Phase A — authz (ดู § 0.1) + CSRF (ดู § 0.3)
    U->>CON: เพิ่มสินค้าลงตะกร้า
    CON->>API: POST /carts/{cartId}/items (+ query merchantId เมื่อ Admin)
    alt Admin Console
        API->>API: query merchantId ถูกรูป, RequireAdminCartAsync (พบ, merchant ตรง), cart.OriginatorId มีค่า, RequireCommerceOriginatorAsync ผ่าน
        alt เงื่อนไขข้อใดข้อหนึ่งล้มเหลว
            API-->>CON: 400 invalid_filter / 404 Cart was not found / 403 merchant_scope_forbidden / 409 state_conflict / 403 originator_scope_forbidden
        else ผ่านทุกเงื่อนไข
            Note over API: ไปต่อ Phase B ด้วย merchantId, SaleCode ของ Originator
        end
    else Merchant Console
        API->>API: actor.SaleCode ไม่ว่าง?
        alt ไม่มี SaleCode
            API-->>CON: 403 sale-code-missing
        else มี
            Note over API: ไปต่อ Phase B ด้วย actor.MerchantId, actor.SaleCode
        end
    end
    Note over API,SP: Phase B — ยืนยันเอกสารกับต้นทางสด (ทั้งสอง audience เดินเหมือนกัน)
    API->>API: Quantity>0, VariantCode เป็นชื่อ enum ProductGroup ที่ถูกต้อง?
    alt validation ล้มเหลว
        API-->>CON: 400 validation_failed
    else ผ่าน
        API->>SP: LookupDocumentQuery(ProductCode, ProductGroup, SaleCode)
        alt ต้นทางล่ม
            API-->>CON: 503 UpstreamUnavailableException
        else ตอบสำเร็จ
            SP-->>API: document หรือ null
            API->>API: document พบ, ยังไม่ PAID, DocumentSaleProbe ไม่พบว่าขายแล้ว?
            alt เอกสารไม่พร้อมขาย
                API-->>CON: 400 product_unpayable (merchant) / 409 product_unpayable (admin)
            else พร้อมขาย
                alt Merchant Console
                    Note over API,DB: Phase C — เขียนตะกร้า (merchant, เขียนตรง)
                    API->>SVC: AddItemToCartCommand(...)
                    SVC->>DB: ตรวจ cart Status Open, ProductCode ยังไม่ซ้ำ
                    alt cart ไม่ Open หรือ product ซ้ำ
                        SVC-->>API: InvalidOperationException / ArgumentException
                        API-->>CON: 409 cart ไม่ Open / 400 product ซ้ำในตะกร้า
                    else ผ่าน
                        SVC->>DB: INSERT Item, cart.Version+1
                        API-->>CON: 200 CartView (ไม่มี ETag)
                    end
                else Admin Console
                    API->>API: Idempotency-Key ถูกต้อง ดู § 0.5
                    alt ผิดรูป
                        API-->>CON: 400 invalid_idempotency_key
                    else ถูกรูป
                        API->>EXE: ExecuteAsync(cart.item.add) ดู § 0.5
                        alt key ซ้ำ (hash ต่าง / InProgress)
                            API-->>CON: 409 idempotency_key_reused / operation_in_progress
                        else replay Succeeded
                            API-->>CON: 200 ผลเดิม (Replayed=true)
                        else claim ใหม่, รันสำเร็จ
                            Note over API,DB: Phase C — เขียนตะกร้า (admin, ในธุรกรรมเดียวกับ record)
                            EXE->>SVC: AddItemToCartCommand(...)
                            SVC->>DB: ตรวจ cart Status Open, ProductCode ยังไม่ซ้ำ
                            alt cart ไม่ Open หรือ product ซ้ำ
                                SVC-->>API: InvalidOperationException / ArgumentException
                                API-->>CON: 409 cart ไม่ Open / 400 product ซ้ำในตะกร้า
                            else ผ่าน
                                SVC->>DB: INSERT Item, cart.Version+1
                                API-->>CON: 200 CartView + ETag ใหม่
                            end
                        end
                    end
                end
            end
        end
    end
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| DELETE | `/api/v1/carts/{cartId:guid}/items/{itemId:guid}` | ไม่มีขั้น document lookup/probe; Merchant: `RemoveItemFromCartCommand` ไม่มี `ExpectedVersion` เลย — cart ไม่พบ/merchant ไม่ตรง -> `InvalidOperationException` **409** (ไม่ใช่ 404), item ไม่พบ -> 404; Admin: บังคับ `If-Match` + `Idempotency-Key` เสมอผ่าน `AdminOperationExecutor(cart.item.remove)` (source: `CartEdits.cs:38-58`) |
| PUT | `/api/v1/carts/{cartId:guid}/items/{itemId:guid}` | โครงเดียวกับ DELETE ต่างที่ตรวจ `Quantity > 0` ก่อน (ไม่ผ่าน -> 400) แล้วส่ง `SetCartItemQuantityCommand`, operation ของ Admin คือ `cart.item.quantity` |
| POST | `/api/v1/carts/{cartId:guid}/clear` | โครงเดียวกับ DELETE ไม่มี `itemId` จึงไม่มี branch "item ไม่พบ", operation ของ Admin คือ `cart.clear` |

---

## 4.3 อ่านคำสั่งซื้อ (รายละเอียด/รายการ/สถานะลิงก์)

`GetOrderDetail` เขียน reveal-audit ต่อ item ก่อนตอบ (fail-closed) เป็นตัวแทน § กลาง (source: `GetOrderDetail.cs`, `Program.cs:1100-1124,2311-2373`)

```mermaid
sequenceDiagram
    autonumber
    actor U as Merchant user / Admin / Employee-Agent
    participant CON as Console/SPA หรือ integration client
    participant API as API<br/>Program.cs (orders)
    participant SVC as OrderService<br/>GetOrderDetailHandler
    participant DB as SQL Server<br/>merch.Orders

    Note over CON,API: Phase A — authz (ดู § 0.1 / § 0.2): dual-console + RequireOrderIdentityPermission(payment.view, order.read)
    U->>CON: เปิดดูรายละเอียดคำสั่งซื้อ
    CON->>API: GET /orders/{orderId} (+ query merchantId เมื่อ Admin, Bearer/BFF cookie เมื่อ identity)
    alt Admin Console
        API->>DB: RequireAdminOrderAsync(orderId, expectedMerchantId: null): order อยู่ใน accessible merchants?
        alt ไม่พบ
            API-->>CON: 404 Order was not found
        else พบ
            API->>API: actorScope.Begin(resource.MerchantId)
        end
    else Merchant Console หรือ identity request
        alt identity request (Bearer/BFF)
            API->>DB: EnsureIdentityOrderOwnerAsync: AccessEvaluator.CanReadOrder(OwnerSaleId, OwnerBranchIdAtCreation)
            alt ไม่ผ่านหรือไม่พบ
                API-->>CON: 404 Order was not found
            else ผ่าน
                Note over API: ไปต่อด้วย actor.MerchantId
            end
        else console ปกติ
            Note over API: ไปต่อด้วย actor.MerchantId
        end
    end
    Note over API,DB: Phase B — อ่านและเขียน reveal-audit (fail-closed, ดู Notes ของ activities.md)
    API->>SVC: GetOrderDetailCommand(merchantId, orderId, actorType, actorId)
    SVC->>DB: SELECT Order + Items
    alt ไม่พบ
        SVC-->>API: NotFoundException
        API-->>CON: 404 Order was not found
    else พบ
        SVC->>DB: INSERT RevealAudit ต่อ item + commit
        alt Merchant Console หรือ identity request
            API-->>CON: 200 OrderDetailView (ไม่มี ETag)
        else Admin Console
            alt มี PaymentSessionId
                SVC->>DB: GetPaymentSessionQuery
            end
            API->>API: VersionEtags.Set(order.Version)
            API-->>CON: 200 AdminOrderDetailResponse (+ paymentSession, lifecycle, capabilities)
        end
    end
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/orders` | list แบ่งหน้าแทน detail เดี่ยว ผ่าน SFS (`SfsQueryParser.Parse`, maxLimit 100) ดู § 0.6; ไม่มี reveal-audit, ไม่มี ETag; Merchant -> `GetOrdersQuery`, Admin -> `AdminOrderQuery` ตาม accessible merchants (source: `GetOrders.cs`) |
| GET | `/api/v1/orders/{orderId:guid}/payment-links` | คืน array `PaymentLinkView` (ไม่มี raw token), ไม่มี audit/ETag; identity request ตรวจ owner เหมือน § กลาง; **ไม่มี branch Admin ในซอร์สเลย** — Admin console (cookie ล้วน) ที่เรียกจะได้ `actor.MerchantId` throw แล้วตอบ **409** ตาม § 0.9 (ดู Deviations ของ activities.md) |

---

## 4.4 สร้างคำสั่งซื้อแบบ canonical

ใช้ trusted pricing เท่านั้น มี idempotency 2 ชั้น (persisted replay + in-flight claim) และแตก draft/issue-now ในธุรกรรมเดียว (source: `Program.cs:2060-2117`, `OrderWorkflow.cs:593-822`, `OrderOwnerResolver.cs`, `TrustedOrderPricingSource.cs`)

```mermaid
sequenceDiagram
    autonumber
    actor E as Employee / Agent / SYSTEM client
    participant C as Web app หรือ integration client
    participant API as API<br/>Program.cs (orders canonical)
    participant OWN as OrderOwnerResolver
    participant PRC as TrustedOrderPricingSource
    participant REPLAY as PaymentLinkReplayService<br/>+ IIdempotencyStore
    participant DB as SQL Server<br/>merch.Orders
    participant OB as Outbox/Worker<br/>ดู § 0.8

    Note over C,API: Phase A — authz (ดู § 0.1 / § 0.2 / § 0.3): identity-platform + RequireIdentityPermission(payment.create) + RequireIdentityPlatformMutation
    E->>C: สร้างคำสั่งซื้อ
    C->>API: POST /api/v1/orders (+ Idempotency-Key, Bearer หรือ BFF cookie)
    API->>API: ValidateCanonicalCreateRequest + NotificationIntentNormalizer + ResolveIdentityMerchantId + actor.UserId ไม่ว่าง
    alt validation ล้มเหลวข้อใดข้อหนึ่ง
        API-->>C: 400 validation_failed / items_required / metadata_invalid / notification_recipient_required<br/>403 merchant_context_missing / merchant_context_mismatch / account_context_missing
    else ผ่าน
        Note over API,OWN: Phase B — resolve owner + trusted pricing
        API->>OWN: ResolveAsync(merchantId, accountId, requested owner)
        alt owner access denied
            OWN-->>API: AccessDeniedException
            API-->>C: 403 owner_context_denied / owner_sale_missing / owner_sale_conflict / owner_branch_conflict /<br/>owner_scope_denied / owner_sale_denied / owner_branch_denied
        else owner branch ไม่ตรง sale
            OWN-->>API: InvalidRequestException
            API-->>C: 400 owner_branch_mismatch
        else ผ่าน
            API->>PRC: PriceAsync(merchantId, businessType, items, owner)
            alt pricing ล้มเหลว
                PRC-->>API: ConflictException
                API-->>C: 409 unsupported_business_type / owner_required / merchant_unavailable /<br/>source_currency_unsupported / source_quantity_invalid / product_unavailable /<br/>source_owner_mismatch / product_unpayable / source_ambiguous
            else ผ่าน
                API->>API: ตรวจ ClientSnapshot + orderDiscount/orderCharge ตรงกับ trusted pricing ทุกบรรทัด
                alt ไม่ตรง
                    API-->>C: 409 pricing_mismatch
                else ตรง
                    Note over API,REPLAY: Phase C — idempotency 2 ชั้น (ดู Notes)
                    API->>REPLAY: TryReplayAsync(merchant, order.create, key)
                    alt มี replay record, หมดอายุ
                        API-->>C: 409 idempotent_secret_expired
                    else มี replay record, hash ต่าง
                        API-->>C: 409 idempotency_conflict
                    else มี replay record, match
                        API-->>C: 201 ผลเดิม (Replayed=true)
                    else ไม่มี replay record
                        API->>REPLAY: RequireFirstDeliveryAsync (claim key)
                        alt claim ซ้ำ
                            API-->>C: 409 idempotency_replay
                        else claim สำเร็จ
                            Note over API,DB: Phase D — สร้าง Order
                            API->>DB: Order.CreateDraft (Draft, OrderNo ใหม่)
                            alt issueNow = true
                                API->>DB: order.Issue -> Open, OrderLinkIssuer.Issue (PaymentLink 72h)
                                API->>REPLAY: persist replay record (protected raw token)
                                alt NotifyOnIssue และไม่มี recipient
                                    API-->>C: 409 notification_recipient_required
                                else NotifyOnIssue และ port ไม่ครบ
                                    API-->>C: 503 DependencyUnavailableException
                                else ok
                                    API->>DB: Outbox.Enqueue PaymentLinkNotificationRequestedV1 (เมื่อ NotifyOnIssue), commit
                                    API-->>C: 201 Created OrderCommandResult (+ link, rawToken) + ETag
                                end
                            else issueNow = false
                                API->>REPLAY: persist replay record ชนิด draft
                                API->>DB: commit
                                API-->>C: 201 Created OrderCommandResult (draft) + ETag
                            end
                        end
                    end
                end
            end
        end
    end
    Note over OB,DB: Phase E — async dispatch (ดู § 0.8)
    OB->>DB: lease outbox แล้วส่ง PaymentLink notification (เมื่อ issueNow + NotifyOnIssue)
```

---

## 4.5 สร้างคำสั่งซื้อจากตะกร้า

สอง phase: `PrepareAsync` ตรวจสถานะเอกสารสดก่อนเปิด transaction, `CommitAsync` ล็อกและเขียนจริงในธุรกรรมเดียว (source: `Program.cs:2121-2182`, `OrderCreationCoordinator.cs`)

```mermaid
sequenceDiagram
    autonumber
    actor U as Merchant user / Admin
    participant CON as Merchant/Admin Console
    participant API as API<br/>Program.cs (orders/from-cart)
    participant COORD as OrderCreationCoordinator
    participant CAP as EffectivePaymentCapabilityResolver
    participant EXE as AdminOperationExecutor<br/>ดู § 0.5
    participant DB as SQL Server<br/>merch.Carts / Orders
    participant OB as Outbox/Worker<br/>ดู § 0.8

    Note over CON,API: Phase A — authz (ดู § 0.1 / § 0.3)
    U->>CON: สร้างคำสั่งซื้อจากตะกร้า
    CON->>API: POST /orders/from-cart (+ query merchantId, originatorId เมื่อ Admin)
    alt Admin Console
        API->>API: body.merchantId/originatorId ถูกรูป, RequireCommerceOriginatorAsync ผ่าน, RequireAdminCartAsync พบ+merchant ตรง, cart.OriginatorId ตรง originatorId
        alt เงื่อนไขข้อใดข้อหนึ่งล้มเหลว
            API-->>CON: 400 validation_failed / 403 merchant_scope_forbidden / originator_scope_forbidden /<br/>404 Cart was not found / 409 state_conflict
        else ผ่าน
            API->>API: actorScope.Begin(merchantId)
        end
    else Merchant Console
        API->>API: body ต้องไม่มี merchantId/originatorId, actor.SaleCode และ actor.UserId ไม่ว่าง
        alt เงื่อนไขล้มเหลว
            API-->>CON: 400 validation_failed / 403 sale-code-missing / merchant-user-unbound
        end
    end
    Note over API,COORD: Phase B — PrepareAsync ก่อนเปิด transaction
    API->>COORD: PrepareAsync(merchantId, cartId, saleCode, ...)
    COORD->>DB: cart พบ, Status Open, มีบรรทัด, originatorId (param) ตรง cart.OriginatorId
    alt cart ไม่พร้อม
        COORD-->>API: NotFoundException / InvalidOperationException / ArgumentException / ConflictException
        API-->>CON: 404 Cart was not found / 409 Cart is not open / 400 empty cart /<br/>409 state_conflict (Cart originator does not match)
    else พร้อม
        COORD->>COORD: ต่อบรรทัด LookupDocumentQuery + DocumentSaleProbe (ทั้งชุด)
        alt เอกสารไม่พร้อมขาย
            COORD-->>API: InvalidOperationException
            API-->>CON: 409 Cart product is no longer available
        else พร้อมขายทั้งชุด
            alt Merchant Console
                API->>COORD: CommitAsync ตรงทันที
            else Admin Console
                API->>API: Idempotency-Key ถูกต้อง ดู § 0.5
                API->>EXE: ExecuteAsync(order.create)
                alt key ซ้ำ (hash ต่าง / InProgress)
                    API-->>CON: 409 idempotency_key_reused / operation_in_progress
                else replay Succeeded
                    API-->>CON: 201 ผลเดิม (Replayed=true)
                else claim ใหม่
                    EXE->>COORD: CommitAsync
                end
            end
            Note over COORD,DB: Phase C — CommitAsync ในธุรกรรมเดียว
            COORD->>CAP: AcquireMerchantSharedAsync แล้ว ResolveMethodAsync (paymentMethod อนุญาต?)
            alt payment method ไม่อนุญาต
                CAP-->>COORD: PaymentMethodDecision.Denied
                API-->>CON: 403 payment_method_not_allowed / 409 payment_capability_unavailable
            else อนุญาต
                COORD->>DB: ReloadTrackedAsync: cart พบ, merchant ตรง, Open, Version/บรรทัดตรงกับ PrepareAsync
                alt cart เปลี่ยนระหว่างตรวจ
                    COORD-->>API: NotFoundException / InvalidOperationException / ConcurrencyConflictException
                    API-->>CON: 404 Cart was not found / 409 Cart is not open / ConcurrencyConflict
                else ไม่เปลี่ยน
                    COORD->>DB: INSERT Order (Pending) + lines จาก cart snapshot
                    COORD->>DB: Outbox.Enqueue CustomerOrderNotification
                    COORD->>DB: cart.MarkCheckedOut, commit
                    API-->>CON: 201 Created DirectOrderResult
                end
            end
        end
    end
    Note over OB,DB: Phase D — async dispatch (ดู § 0.8)
    OB->>DB: lease outbox แล้วส่งอีเมล/SMS สรุปคำสั่งซื้อ
```

---

## 4.6 ส่งออกรายการคำสั่งซื้อ

Admin-only ต้องระบุช่วงวันที่ไม่เกิน 31 วัน แล้วส่งออกสูงสุด 10,000 แถวเป็น CSV (source: `Program.cs:2240-2308,4027-4045`)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant CON as Admin console
    participant API as API<br/>Program.cs (orders/export)
    participant DB as SQL Server<br/>merch.Orders

    Note over CON,API: Phase A — authz (ดู § 0.1): policy admin + permission txn.export
    A->>CON: ขอส่งออกรายการคำสั่งซื้อ
    CON->>API: GET /orders/export?from=...&to=...&merchantId=... (ดู § 0.6)
    API->>API: from/to เป็น ISO-8601, to>=from, ช่วงไม่เกิน 31 วัน, merchantId (ถ้าส่งมา) เป็น GUID
    alt query ผิด
        API-->>CON: 400 invalid_filter
    else ถูก
        API->>DB: AdminOrderQuery (filters + createdAt ช่วงที่ขอ, Page 1, Limit 10001)
        DB-->>API: rows + Total
        alt Total > 10,000
            API-->>CON: 422 export_too_large
        else ไม่เกิน
            API->>API: แปลงเป็น CSV
            API-->>CON: 200 text/csv attachment
        end
    end
```

---

## 4.7 ยกเลิกคำสั่งซื้อ

3 เส้นจริง: merchant console ธรรมดา, identity request, admin — ทุกเส้นปล่อย payment session ที่ค้างก่อนเสมอโดยพิสูจน์กับ PSP ว่าไม่มีเงินเข้าได้แล้ว (source: `Program.cs:1993-2056`, `CancelOrder.cs`, `OrderWorkflow.cs:1301-1361`, `ReleaseOpenSessionHandler.cs`)

```mermaid
sequenceDiagram
    autonumber
    actor U as Merchant user / Admin / Employee-Agent
    participant CON as Console/SPA หรือ integration client
    participant API as API<br/>Program.cs (orders/cancel)
    participant REL as ReleaseOpenSessionHandler<br/>+ PaymentConfirmationService
    participant PSP as PSP (external)
    participant EXE as AdminOperationExecutor<br/>ดู § 0.5
    participant DB as SQL Server<br/>merch.Orders

    Note over CON,API: Phase A — authz (ดู § 0.1 / § 0.2 / § 0.3)
    U->>CON: ยกเลิกคำสั่งซื้อ
    CON->>API: POST /orders/{orderId}/cancel (+ reason, query merchantId เมื่อ Admin, Bearer/BFF เมื่อ identity)
    API->>API: body.reason (ถ้าส่งมา) ไม่ว่างไม่เกิน 1000 ตัวอักษร
    alt reason ผิด
        API-->>CON: 400 validation_failed
    else ผ่าน
        alt Admin Console
            API->>DB: RequireAdminOrderAsync(orderId, merchantId, mutation:true) + If-Match ถูกต้อง
            alt เงื่อนไขล้มเหลว
                API-->>CON: 404 Order was not found / 403 merchant_scope_forbidden / 400 invalid_etag
            else ผ่าน
                API->>API: actorScope.Begin(merchantId), Idempotency-Key ถูกต้อง ดู § 0.5
                alt Idempotency-Key ผิด
                    API-->>CON: 400 invalid_idempotency_key
                else ถูก
                    API->>EXE: ExecuteRecoverableAdminCommerceAsync(order.cancel)
                    alt key ซ้ำ, hash ต่าง
                        API-->>CON: 409 idempotency_key_reused
                    else Succeeded ก่อนหน้า
                        API-->>CON: 200 ผลเดิม (Replayed=true)
                    else claim InProgress ใหม่
                        Note over API,PSP: Phase B — ปล่อย payment session ก่อนยกเลิกเสมอ
                        API->>REL: ReleaseOpenSessionCommand(orderId)
                        REL->>PSP: ยิงยืนยันสถานะ session ที่เปิดอยู่ (ถ้ามี)
                        PSP-->>REL: Expired / Failed / ยังจ่ายได้ / settled / เรียกไม่สำเร็จ
                        alt ยังจ่ายได้ / settled / เรียกไม่สำเร็จ (ambiguous)
                            API-->>CON: 409 session ยังจ่ายได้ / ยืนยันกับ PSP ไม่สำเร็จ (record ค้าง InProgress)
                        else Expired หรือ Failed (พิสูจน์ว่าไม่มีเงินเข้า)
                            API->>DB: ตรวจ Cancelled อยู่แล้ว, version, สถานะ, blocking session
                            alt Cancelled อยู่แล้ว
                                API-->>CON: 200 ผลเดิม (idempotent)
                            else สถานะ/version ไม่ให้ยกเลิก
                                API-->>CON: 409 ตามเหตุ (version / status / blocking)
                            else ผ่าน
                                API->>DB: order.Cancel + revoke link, commit
                                API->>API: RequireAdminOrderAsync ซ้ำ อ่าน Version ล่าสุด
                                API-->>CON: 200 CancelOrderResult + ETag
                            end
                        end
                    end
                end
            end
        else identity request (Bearer/BFF)
            API->>DB: EnsureIdentityOrderOwnerAsync ผ่าน?
            alt ไม่ผ่าน
                API-->>CON: 404 Order was not found
            else ผ่าน
                API->>API: command.Reason ไม่ว่าง? (RequireReason บังคับเฉพาะ identity path)
                alt reason ว่าง
                    API-->>CON: 400 invalid_reason
                else ไม่ว่าง
                    API->>API: CancelManagedOrderCommand: version + Idempotency-Key บังคับ, RequireFirstDeliveryAsync claim
                    alt claim ซ้ำ
                        API-->>CON: 409 idempotency_replay
                    else claim สำเร็จ
                        API->>DB: GetForUpdateAsync ล็อก row, ตรวจ Cancelled/version/Paid-Refunded/blocking session
                        alt Cancelled อยู่แล้ว
                            API-->>CON: 200 ผลเดิม (idempotent)
                        else เงื่อนไขอื่นไม่ผ่าน
                            API-->>CON: 409 ConcurrencyConflict / order_already_paid / payment_pending_verification
                        else ผ่าน
                            API->>DB: order.Cancel + revoke link, commit
                            API-->>CON: 200 CancelOrderResult
                        end
                    end
                end
            end
        else Merchant Console ปกติ
            Note over API,PSP: Phase B — ปล่อย payment session ก่อนยกเลิกเสมอ (ไม่มี version/idempotency)
            API->>REL: ReleaseOpenSessionCommand(orderId)
            REL->>PSP: ยิงยืนยันสถานะ session ที่เปิดอยู่ (ถ้ามี)
            PSP-->>REL: Expired / Failed / ยังจ่ายได้ / settled / เรียกไม่สำเร็จ
            alt ยังจ่ายได้ / settled / เรียกไม่สำเร็จ
                API-->>CON: 409 session ยังจ่ายได้ / ยืนยันกับ PSP ไม่สำเร็จ
            else Expired หรือ Failed
                API->>DB: CancelOrderCommand (ไม่มี ExpectedVersion): order พบ, สถานะ Pending/Draft/Open และยังไม่ Paid, ไม่มี blocking session ใหม่
                alt เงื่อนไขไม่ผ่าน
                    API-->>CON: 404 Order was not found / 409 ตามสถานะ / blocking session
                else ผ่าน
                    API->>DB: order.Cancel + revoke link, commit
                    API-->>CON: 200 CancelOrderResult
                end
            end
        end
    end
```

---

## 4.8 ออก/หมุน PaymentLink ของคำสั่งซื้อ

`RotatePaymentLink` เพิกถอนลิงก์ active เดิมแล้วออกใหม่ในธุรกรรมเดียว มี idempotency 2 ชั้นเหมือน § 4.4 และบังคับ `If-Match`/`Idempotency-Key` ทั้งสอง audience เสมอ (source: `Program.cs:1126-1177`, `OrderWorkflow.cs:928-1047`)

```mermaid
sequenceDiagram
    autonumber
    actor U as Merchant user / Admin / Employee-Agent
    participant CON as Console/SPA หรือ integration client
    participant API as API<br/>Program.cs (orders/payment-links)
    participant REPLAY as PaymentLinkReplayService<br/>+ IIdempotencyStore
    participant DB as SQL Server<br/>merch.Orders / PaymentLinks
    participant OB as Outbox/Worker<br/>ดู § 0.8

    Note over CON,API: Phase A — authz (ดู § 0.1 / § 0.2 / § 0.3): RequireOrderIdentityPermission(payment.create, checkout.write)
    U->>CON: หมุน PaymentLink ของคำสั่งซื้อ
    CON->>API: POST /orders/{orderId}/payment-links (+ If-Match, Idempotency-Key เสมอทั้งสอง audience, query merchantId เมื่อ Admin)
    alt Admin Console
        API->>DB: RequireAdminOrderAsync(orderId, merchantId, mutation:true)
        alt ไม่พบ/merchant ไม่ตรง
            API-->>CON: 404 Order was not found / 403 merchant_scope_forbidden
        else พบ
            API->>API: actorScope.Begin(merchantId)
        end
    else identity request (Bearer/BFF)
        API->>DB: EnsureIdentityOrderOwnerAsync ผ่าน?
        alt ไม่ผ่าน
            API-->>CON: 404 Order was not found
        end
    end
    API->>API: If-Match ถูกต้อง, Idempotency-Key ถูกต้อง (ทั้งสอง audience เสมอ)
    alt header ผิดรูป
        API-->>CON: 400 invalid_etag / invalid_idempotency_key
    else ถูกต้อง
        API->>REPLAY: TryReplayAsync(merchant, payment-link.rotate, key)
        alt มี replay record, หมดอายุ
            API-->>CON: 409 idempotent_secret_expired
        else มี replay record, hash ต่าง
            API-->>CON: 409 idempotency_conflict
        else มี replay record, match
            API-->>CON: 201 ผลเดิม (Replayed=true)
        else ไม่มี replay record
            API->>REPLAY: RequireFirstDeliveryAsync (claim key)
            alt claim ซ้ำ
                API-->>CON: 409 idempotency_replay
            else claim สำเร็จ
                API->>DB: GetForUpdateAsync(merchantId, orderId): พบ, Version ตรง, Status=Open และยังไม่ Paid
                alt เงื่อนไขไม่ผ่าน
                    API-->>CON: 404 Order was not found / 409 ConcurrencyConflict / order_state_conflict
                else ผ่าน
                    API->>API: SendNotification=true ต้องมี recipient, ต้องมี active PaymentLink
                    alt เงื่อนไขไม่ผ่าน
                        API-->>CON: 409 notification_recipient_required / payment_link_missing
                    else ผ่าน
                        API->>DB: active.Revoke, order.RegisterLinkRotation, OrderLinkIssuer.Issue (ลิงก์ใหม่ 72h)
                        API->>REPLAY: persist replay record (protected raw token)
                        alt SendNotification=true และ port ไม่ครบ
                            API-->>CON: 503 DependencyUnavailableException
                        else ok
                            API->>DB: Outbox.Enqueue PaymentLinkNotificationRequestedV1 (เมื่อ SendNotification), commit
                            API-->>CON: 201 Created OrderCommandResult + ETag
                        end
                    end
                end
            end
        end
    end
    Note over OB,DB: Phase B — async dispatch (ดู § 0.8)
    OB->>DB: lease outbox แล้วส่งลิงก์ใหม่
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| POST | `/api/v1/orders/{orderId:guid}/issue` | permission `payment.create (identity: order.write)`; ไม่มีขั้นหา active link เดิม/revoke — ออกลิงก์ **แรก**; precondition: order ต้อง `Status = Draft` เท่านั้น (`Open` -> 409 `order_already_issued`); เรียก `order.Issue` (Draft -> Open) ก่อนออกลิงก์; ไม่มี `SendNotification` จาก body; ตอบ **200** (ไม่ใช่ 201), ไม่มี `Location`; replay operation name `order.issue` (source: `Program.cs:1179-1226`, `OrderWorkflow.cs:824-926`) |

---

## 4.9 ส่งลิงก์สรุปคำสั่งซื้อซ้ำ

หมุน token สรุปคำสั่งซื้อและต่ออายุ TTL แล้ว re-notify ลูกค้า เฉพาะ Admin ที่บังคับ `If-Match`/`Idempotency-Key` (source: `Program.cs:1948-1983`, `ResendOrderSummary.cs`)

```mermaid
sequenceDiagram
    autonumber
    actor U as Merchant user / Admin
    participant CON as Merchant/Admin Console
    participant API as API<br/>Program.cs (orders/summary/resend)
    participant EXE as AdminOperationExecutor<br/>ดู § 0.5
    participant DB as SQL Server<br/>merch.Orders
    participant OB as Outbox/Worker<br/>ดู § 0.8

    Note over CON,API: Phase A — authz (ดู § 0.1 / § 0.3): permission payment.create
    U->>CON: ขอส่งลิงก์สรุปคำสั่งซื้อซ้ำ
    CON->>API: POST /orders/{orderId}/summary/resend (+ query merchantId, If-Match, Idempotency-Key เมื่อ Admin)
    alt Admin Console
        API->>DB: RequireAdminOrderAsync(orderId, merchantId, mutation:true) + If-Match ถูกต้อง
        alt เงื่อนไขล้มเหลว
            API-->>CON: 404 Order was not found / 403 merchant_scope_forbidden / 400 invalid_etag
        else ผ่าน
            API->>API: actorScope.Begin(merchantId), Idempotency-Key ถูกต้อง
            alt ผิดรูป
                API-->>CON: 400 invalid_idempotency_key
            else ถูก
                API->>EXE: ExecuteAsync(order.summary.resend)
                alt key ซ้ำ (hash ต่าง / InProgress)
                    API-->>CON: 409 idempotency_key_reused / operation_in_progress
                else replay Succeeded
                    API-->>CON: 200 ผลเดิม (Replayed=true)
                else claim ใหม่
                    Note over API,DB: ไปต่อ Phase B ด้วย ExpectedVersion
                end
            end
        end
    else Merchant Console
        Note over API: ไปต่อ Phase B โดยไม่มี ExpectedVersion
    end
    Note over API,DB: Phase B — หมุน token
    API->>DB: ResendOrderSummaryCommand: order พบ, (admin) Version ตรง
    alt ไม่พบ / version ไม่ตรง
        API-->>CON: 404 Order was not found / 409 ConcurrencyConflict
    else ผ่าน
        API->>DB: order.ReissueSummary (ต้องอยู่สถานะที่ resend ได้)
        alt สถานะไม่ให้ resend
            API-->>CON: 409 InvalidOperationException ไม่มี code
        else ผ่าน
            API->>DB: token ใหม่ + ต่ออายุ, Outbox.Enqueue CustomerOrderNotification (เมื่อมี recipient), commit
            alt Admin Console
                API->>API: RequireAdminOrderAsync ซ้ำ อ่าน Version ล่าสุด
            end
            API-->>CON: 200 ResendOrderSummaryResult + ETag (admin)
        end
    end
    Note over OB,DB: Phase C — async dispatch (ดู § 0.8)
    OB->>DB: lease outbox แล้วส่งลิงก์สรุปใหม่
```

---

## 4.10 ผลิตภัณฑ์ (แคตตาล็อกเอกสารประกัน)

ค้นสดจากต้นทาง SP ไม่มีสำเนาในระบบ; § กลางคือ Merchant Console (source: `Program.cs:1330-1391`, `ListProducts.cs`)

```mermaid
sequenceDiagram
    autonumber
    actor MU as Merchant user
    participant CON as Merchant Console
    participant API as API<br/>Program.cs (products)
    participant SP as Upstream SP<br/>เอกสารประกัน (ext)
    participant DB as SQL Server<br/>merch.Orders (probe)

    Note over CON,API: Phase A — authz (ดู § 0.1): policy merchant-user + permission payment.view
    MU->>CON: ค้นหาผลิตภัณฑ์
    CON->>API: GET /products?page=..&limit=..&productFilters=.. (ดู § 0.6)
    API->>API: actor.SaleCode ไม่ว่าง?
    alt ไม่มี SaleCode
        API-->>CON: 403 sale-code-missing
    else มี
        API->>API: ProductFilterDto.Parse + ResolveTarget (productGroup/insuranceType)
        alt parse/target ผิด
            API-->>CON: 400 Malformed productFilters / insuranceType is required / productGroup ขัดแย้ง
        else ถูก
            API->>SP: SpDocumentGateway.SearchAsync(SaleCode ของ actor, filters)
            alt ต้นทางล่ม
                API-->>CON: 503 UpstreamUnavailableException
            else ตอบสำเร็จ
                SP-->>API: page ของเอกสาร
                API->>DB: DocumentSaleProbe.ProbeAsync (ครั้งเดียวทั้งหน้า)
                DB-->>API: เอกสารที่ order สถานะ Paid ถืออยู่แล้ว
                API->>API: paymentStatus=UNPAID (ค่าเริ่มต้น) ตัดเอกสารที่ขายแล้วออก มิฉะนั้นติด soldByPlatform
                API-->>CON: 200 ProductPage
            end
        end
    end
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/products/documents` | permission `txn.view`, policy `admin`; ต้องส่ง query `merchantId` + `originatorId` แล้วผ่าน `RequireCommerceOriginatorAsync` แทนการเช็ค `actor.SaleCode`; ใช้ `originator.SaleCode`; ผลลัพธ์ห่อเป็น `AdminProductPage`; ไม่มี 403 `sale-code-missing` (source: `Program.cs:1358-1391`) |

---

## Notes

- Deviations ของ theme นี้อยู่ที่ `04-carts-products-orders.activities.md` (มี 1 รายการ: `GET /orders/{orderId}/payment-links` ไม่มี branch Admin ในซอร์ส) — sequences.md ไม่ทำซ้ำตาราง
- ทุก diagram ย่อ handler + store ของแต่ละ module ไว้ใน participant เดียว (เช่น `CartService`, `OrderService`) เพื่อความกระชับ รายละเอียด method/file แยกชั้นดู source ต่อ § ใน activities.md
- `EXE` (`AdminOperationExecutor`) และ `REPLAY` (`PaymentLinkReplayService` + `IIdempotencyStore`) เป็นกลไก idempotency คนละแบบกัน — ดู § 0.5 ของ cross-cutting และ Notes ของ activities.md สำหรับความต่าง
- `OB` (Outbox/Worker) วาดเป็น note บรรทัดเดียวปิดท้ายแต่ละ diagram ที่มี enqueue จริง (§ 4.4, 4.5, 4.8, 4.9) ปลายทาง dispatcher เต็มอยู่ที่ § 0.8 ของ cross-cutting ไม่ใช่ scope ของ theme นี้
- § 4.6 (export) และ § 4.10 (products) ไม่มี CSRF arrow เพราะเป็น GET (safe method) ดู § 0.3
- checker `check-mermaid.mjs` ในเครื่องนี้ผ่าน `OK` ครบ 10/10 block ของไฟล์นี้ (`sequenceDiagram`); แต่ error `purify.addHook is not a function` กับทุก block `flowchart TD` ใน `04-carts-products-orders.activities.md` (ยืนยันซ้ำหลังแก้ must-issue แล้วยัง FAIL เท่าเดิม) — เป็นปัญหา dependency ของ `mmdc`/`dompurify` ที่กระทบเฉพาะ flowchart parser ในเครื่องนี้ ไม่ใช่ syntax error ของเนื้อหา รอผู้ดูแล pipeline ซ่อม dependency แล้วรัน checker ซ้ำ

**Render**: GitHub / Obsidian / VS Code Mermaid
