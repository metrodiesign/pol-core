# pol-core API — Carts, products และ order lifecycle (Activity Diagrams)

> Source: `docs/reference/api-endpoints.md` section "Commerce, checkout และ orders" บรรทัด L84-L89, L95-L104, L109-L110 และ source ที่อ้างต่อ § (`src/Api/Api/Program.cs`, `src/Application/Modules/Carts.Application/*.cs`, `src/Domain/Modules/Carts.Domain/Cart.cs`, `src/Application/Modules/Orders.Application/*.cs`, `src/Api/Api/Orders/OrderCreationCoordinator.cs`, `src/Application/Modules/Products.Application/ListProducts.cs`, `src/Infrastructure/Modules/Products.Infrastructure/Orders/TrustedOrderPricingSource.cs`, `src/Infrastructure/Persistence/Persistence.ControlPlane/Orders/OrderOwnerResolver.cs`)
> Scope: 18 endpoints ของตะกร้าสินค้า คำสั่งซื้อ (canonical + compatibility) และแคตตาล็อกผลิตภัณฑ์ เกือบทุก endpoint เป็น dual-console (Admin Console ผ่าน query `merchantId`/`originatorId`, Merchant Console ผ่าน session, บาง endpoint ของ order รับ identity BFF/Bearer เพิ่ม) ยกเว้น `POST /orders` (identity-platform เท่านั้น), `GET /orders/export` (admin เท่านั้น) และ `GET /products` (merchant-user เท่านั้น)
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

เปิดตะกร้าใหม่ (dual audience): Merchant Console สร้างทันทีด้วย session, Admin Console ต้องส่ง `merchantId`+`originatorId` แล้วผ่าน `AdminOperationExecutor` เพื่อ idempotency (source: `Program.cs:1395-1434,3719-3872`, `AdminOperationExecutor.cs:24-60`, `CreateCartHandler.cs:8-29`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy dual-console + permission payment.create<br/>RequireAudienceCsrf ดู § 0.1 / § 0.3"]
    AUTHZ --> AUD{"SelectedConsoleAudience = Admin?<br/>(IsAdminCommerceRequest)"}
    AUD -->|"no, Merchant"| CREATE_M["CreateCartCommand(actor.MerchantId, actor.SaleCode)<br/>CreateCartHandler: cart ใหม่ Status Open, Version 0"]
    CREATE_M --> R200_M["200 CreateCartResponse{cartId}"]
    AUD -->|"yes, Admin"| Q{"query merchantId, originatorId<br/>เป็น GUID ไม่ว่างทั้งคู่?"}
    Q -->|no| R400_Q["400 InvalidRequestException<br/>merchantId/originatorId must be a non-empty UUID"]
    Q -->|yes| SCOPE{"scope.Accessible.Allows(merchantId)?"}
    SCOPE -->|no| R403_SC["403 AccessDeniedException<br/>code merchant_scope_forbidden"]
    SCOPE -->|yes| ORIG{"Originator พบ, Status active,<br/>มี SaleCode?"}
    ORIG -->|no| R403_OR["403 AccessDeniedException<br/>code originator_scope_forbidden"]
    ORIG -->|yes| BIND["actorScope.Begin(merchantId)"]
    BIND --> IDEMP{"Idempotency-Key ไม่ว่าง<br/>ไม่เกิน 200 ไม่มี control char? ดู § 0.5"}
    IDEMP -->|no| R400_I["400 invalid_idempotency_key"]
    IDEMP -->|yes| EXEC["AdminOperationExecutor.ExecuteAsync(cart.create)<br/>ในธุรกรรมเดียว ดู § 0.5<br/>key = merchant + admin + operation + Idempotency-Key"]
    EXEC --> PRIOR{"มี AdminOperationRecord เดิม (key ซ้ำ)?"}
    PRIOR -->|"hash ต่าง"| R409_K["409 idempotency_key_reused"]
    PRIOR -->|"InProgress"| R409_P["409 operation_in_progress"]
    PRIOR -->|"Succeeded"| REPLAY["200 ผลเดิม (Replayed = true)<br/>ไม่เขียน DB ซ้ำ"]
    PRIOR -->|"ไม่มี"| RUN["CreateCartCommand(merchantId, originator.SaleCode, originatorId)<br/>CreateCartHandler ผูก OriginatorId"]
    RUN --> R200_A["200 CreateCartResponse{cartId} (Replayed = false)"]
    R200_M --> END_S((◉))
    R200_A --> END_S
    REPLAY --> END_S
    R400_Q --> END_F((◉))
    R403_SC --> END_F
    R403_OR --> END_F
    R400_I --> END_F
    R409_K --> END_F
    R409_P --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    class CREATE_M,BIND,RUN,R200_M,R200_A,END_S ok
    class R400_Q,R403_SC,R403_OR,R400_I,R409_K,R409_P,END_F fail
    class AUTHZ,AUD,Q,SCOPE,ORIG,IDEMP,PRIOR gate
    class REPLAY warn
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/carts/{cartId:guid}` | permission `payment.view` (ไม่ใช่ `payment.create`), safe method จึงไม่มี CSRF ดู § 0.3, ไม่มี idempotency/If-Match; Merchant: `GetCartQuery(cartId, actor.MerchantId)` คืน `null` ทั้งกรณีไม่พบและเป็นของ merchant อื่น -> 404 `Results.NotFound` ตรง (ไม่ผ่าน exception handler) — คืนผลตรง ๆ ไม่เคยตั้ง `ETag`; Admin: ไม่ต้องส่ง query `merchantId`/`originatorId` เลย ใช้ `RequireAdminCartAsync(cartId, expectedMerchantId: null, mutation: false)` เช็คเฉพาะว่า cart อยู่ใน merchant ที่ scope เข้าถึงได้ ไม่พบ -> `NotFoundException` 404; **เฉพาะ Admin เท่านั้น** ที่ตอบ header `ETag = vN` จาก `cart.Version` เมื่อพบ (`VersionEtags.Set`) — ฝั่ง Merchant ไม่มี ETag เลย (source: `Program.cs:1491-1524`, `GetCart.cs:42-56`) |

---

## 4.2 แก้ไขรายการในตะกร้า

`AddCartItem` เป็นตัวแทน § กลาง: เดินผ่านการยืนยันเอกสารจากต้นทางสด (SP) ก่อนเขียนลงตะกร้า และ **ไม่มี** `If-Match` เลยทั้งสองด้าน; ต่างจาก remove/set-quantity/clear ที่ไม่มีขั้น lookup เอกสารแต่ (เฉพาะ Admin) บังคับ `If-Match` เสมอ (source: `Program.cs:1436-1489,1526-1630,3835-3965`, `AddItemToCartHandler.cs`, `CartEdits.cs`, `Cart.cs:58-129`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy dual-console + permission payment.create<br/>RequireAudienceCsrf ดู § 0.1 / § 0.3"]
    AUTHZ --> AUD{"Admin request? (IsAdminCommerceRequest)"}
    AUD -->|"no, Merchant"| SALE{"actor.SaleCode ไม่ว่าง?"}
    SALE -->|no| R403_S["403 sale-code-missing"]
    SALE -->|yes| VALID_M{"Quantity > 0 และ VariantCode<br/>เป็นชื่อ enum ProductGroup ที่ถูกต้อง?"}
    AUD -->|"yes, Admin"| Q{"query merchantId ไม่ว่าง GUID?"}
    Q -->|no| R400_Q["400 invalid_filter"]
    Q -->|yes| CART{"RequireAdminCartAsync(cartId, merchantId, mutation:true):<br/>cart พบและ merchantId ตรง?"}
    CART -->|"ไม่พบ"| R404_C["404 Cart was not found"]
    CART -->|"merchant ไม่ตรง"| R403_M["403 merchant_scope_forbidden"]
    CART -->|yes| ORIGIN{"cart.OriginatorId มีค่า?"}
    ORIGIN -->|no| R409_NO["409 state_conflict<br/>Cart has no originator"]
    ORIGIN -->|yes| ORIGLOOK{"Originator พบ, active,<br/>มี SaleCode? (ดู § 4.1)"}
    ORIGLOOK -->|no| R403_OR["403 originator_scope_forbidden"]
    ORIGLOOK -->|yes| VALID_A{"Quantity > 0 และ VariantCode<br/>เป็นชื่อ enum ProductGroup ที่ถูกต้อง?"}
    VALID_M -->|no| R400_V["400 validation_failed"]
    VALID_A -->|no| R400_V
    VALID_M -->|yes| LOOKUP["เรียก LookupDocumentQuery(ProductCode, ProductGroup, SaleCode)"]
    VALID_A -->|yes| LOOKUP
    LOOKUP --> SP["Upstream SP เอกสารประกัน<br/>ค้นสด ไม่มีสำเนาในระบบ"]
    SP --> UP{"ต้นทางตอบสำเร็จ?"}
    UP -->|no| R503["503 UpstreamUnavailableException"]
    UP -->|yes| DOC{"document พบ และ<br/>PaymentStatus ไม่ใช่ PAID?"}
    DOC -->|"no, merchant"| R400_P["400 product_unpayable"]
    DOC -->|"no, admin"| R409_PA["409 product_unpayable"]
    DOC -->|yes| PROBE["DocumentSaleProbe.ProbeAsync([document])<br/>เช็คว่าขายผ่านแพลตฟอร์มนี้ไปแล้วหรือยัง"]
    PROBE --> SOLD{"probe คืน record (ขายแล้ว)?"}
    SOLD -->|"yes, merchant"| R400_P
    SOLD -->|"yes, admin"| R409_PA
    SOLD -->|no| META["สร้าง CommerceItemMetadata จากเอกสาร<br/>DocumentType, PolicyNumber, StartDate, EndDate"]
    META -->|Merchant| DUP{"AddItemToCartHandler:<br/>cart Status Open? ProductCode<br/>ยังไม่อยู่ในตะกร้า?"}
    DUP -->|"ไม่ Open"| R409_ST["409 Cannot modify a cart that is not open"]
    DUP -->|"ซ้ำ"| R400_DUP["400 The product is already in this cart"]
    DUP -->|ok| ADD["cart.AddItem: เพิ่มบรรทัดใหม่ Version+1"]
    ADD --> R200_M["200 CartView (ไม่มี ETag)"]
    META -->|Admin| BINDA["actorScope.Begin(merchantId)"]
    BINDA --> IDEMP{"Idempotency-Key ถูกต้อง? (admin เท่านั้น) ดู § 0.5"}
    IDEMP -->|no| R400_I["400 invalid_idempotency_key"]
    IDEMP -->|yes| EXECA["AdminOperationExecutor(cart.item.add) ดู § 0.5"]
    EXECA --> PRIOR{"key ซ้ำ?"}
    PRIOR -->|"hash ต่าง"| R409_K["409 idempotency_key_reused"]
    PRIOR -->|"InProgress"| R409_IP["409 operation_in_progress"]
    PRIOR -->|"Succeeded"| REPLAY["200 ผลเดิม Replayed=true"]
    PRIOR -->|"ไม่มี"| DUP_A{"AddItemToCartHandler:<br/>cart Status Open? ProductCode<br/>ยังไม่อยู่ในตะกร้า? (เขียนจริง<br/>ในธุรกรรมเดียวกับ record)"}
    DUP_A -->|"ไม่ Open"| R409_ST
    DUP_A -->|"ซ้ำ"| R400_DUP
    DUP_A -->|ok| ADD_A["cart.AddItem: เพิ่มบรรทัดใหม่ Version+1"]
    ADD_A --> ETAG["VersionEtags.Set(cart.Version)"]
    ETAG --> R200_A["200 CartView + ETag = vN ใหม่"]
    R200_M --> END_S((◉))
    R200_A --> END_S
    REPLAY --> END_S
    R403_S --> END_F((◉))
    R400_Q --> END_F
    R404_C --> END_F
    R403_M --> END_F
    R409_NO --> END_F
    R403_OR --> END_F
    R400_V --> END_F
    R503 --> END_F
    R400_P --> END_F
    R409_PA --> END_F
    R409_ST --> END_F
    R400_DUP --> END_F
    R400_I --> END_F
    R409_K --> END_F
    R409_IP --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    classDef ext fill:#4a3b0f,stroke:#e3b341,color:#fff
    class ADD,ADD_A,BINDA,ETAG,R200_M,R200_A,END_S ok
    class R403_S,R400_Q,R404_C,R403_M,R409_NO,R403_OR,R400_V,R503,R400_P,R409_PA,R409_ST,R400_DUP,R400_I,R409_K,R409_IP,END_F fail
    class AUTHZ,AUD,SALE,Q,CART,ORIGIN,ORIGLOOK,VALID_M,VALID_A,UP,DOC,SOLD,DUP,DUP_A,IDEMP,PRIOR gate
    class REPLAY warn
    class SP ext
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| DELETE | `/api/v1/carts/{cartId:guid}/items/{itemId:guid}` | ไม่มีขั้น document lookup/probe เลย; Merchant: `RemoveItemFromCartCommand(cartId, actor.MerchantId, itemId)` ไม่มี `ExpectedVersion` (ไม่ตรวจ ETag เลย) — cart ไม่พบ/merchant ไม่ตรง -> `InvalidOperationException` **409** (ไม่ใช่ 404), item ไม่พบ -> `NotFoundException` 404, cart ไม่ Open -> 409; Admin: ต้องส่ง `If-Match` (`RequireCartVersion`) เสมอผ่าน `AdminIfMatchMutationMarker` + `Idempotency-Key` ผ่าน `AdminOperationExecutor(cart.item.remove)`, version ไม่ตรง -> 409 `ConcurrencyConflict`; ตอบ 200 CartView + ETag ใหม่ (source: `Program.cs:1526-1560`, `CartEdits.cs:38-58`) |
| PUT | `/api/v1/carts/{cartId:guid}/items/{itemId:guid}` | โครงเดียวกับ DELETE ทุกจุด ต่างที่ตรวจ `Quantity > 0` ก่อนส่งคำสั่ง (ไม่ผ่าน -> 400 `ArgumentOutOfRangeException`) แล้วส่ง `SetCartItemQuantityCommand`; operation ของ Admin คือ `cart.item.quantity` (source: `Program.cs:1562-1597`, `CartEdits.cs:60-81`) |
| POST | `/api/v1/carts/{cartId:guid}/clear` | โครงเดียวกับ DELETE ทุกจุด ไม่มี `itemId` จึงไม่มี branch "item ไม่พบ"; ส่ง `ClearCartCommand` ลบทุกบรรทัด, operation ของ Admin คือ `cart.clear` (source: `Program.cs:1599-1630`, `CartEdits.cs:83-102`) |

---

## 4.3 อ่านคำสั่งซื้อ (รายละเอียด/รายการ/สถานะลิงก์)

`GetOrderDetail` เขียน reveal-audit ต่อ item ก่อนตอบ (fail-closed) เป็นตัวแทน § กลาง; ต่างจาก list (ไม่มี audit, ผ่าน SFS) และ payment-links (ไม่มี audit, ไม่มี Admin branch เลยในซอร์ส) (source: `GetOrderDetail.cs`, `GetOrders.cs`, `Program.cs:1100-1124,2311-2373`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy dual-console + RequireOrderIdentityPermission(payment.view, order.read)<br/>ดู § 0.1 / § 0.2"]
    AUTHZ --> AUD{"Admin request? (IsAdminCommerceRequest)"}
    AUD -->|no| IDREQ{"IsIdentityRequest (Bearer)?"}
    IDREQ -->|yes| OWNER{"EnsureIdentityOrderOwnerAsync:<br/>order พบและ AccessEvaluator.CanReadOrder<br/>(OwnerSaleId/OwnerBranchIdAtCreation) อนุญาต?"}
    OWNER -->|no| R404_O["404 Order was not found"]
    OWNER -->|yes| DETAIL_M["GetOrderDetailCommand(actor.MerchantId, orderId,<br/>merchant-user, actor.UserId)"]
    IDREQ -->|no| DETAIL_M
    AUD -->|yes| RESOURCE{"RequireAdminOrderAsync(orderId,<br/>expectedMerchantId: null, mutation:false):<br/>order พบใน accessible merchants?"}
    RESOURCE -->|no| R404_A["404 Order was not found"]
    RESOURCE -->|yes| BINDA["actorScope.Begin(resource.MerchantId)"]
    BINDA --> DETAIL_A["GetOrderDetailCommand(resource.MerchantId, orderId,<br/>admin, adminScope.Current.AdminId)"]
    DETAIL_M --> FOUND{"order พบ (GetAsync)?"}
    DETAIL_A --> FOUND
    FOUND -->|no| R404_H["404 Order was not found"]
    FOUND -->|yes| AUDIT["เขียน RevealAudit 1 แถวต่อ item<br/>SaveChanges (fail-closed, ดู Notes)"]
    AUDIT --> LINES["map OrderItemDetail<br/>server-owned metadata + requestMetadata"]
    LINES --> SHAPE{"audience?"}
    SHAPE -->|"merchant/identity"| R200_M["200 OrderDetailView (ไม่มี ETag)"]
    SHAPE -->|admin| SESSION{"มี PaymentSessionId?"}
    SESSION -->|yes| GETSESSION["GetPaymentSessionQuery"]
    SESSION -->|no| ETAG
    GETSESSION --> ETAG["VersionEtags.Set(order.Version)"]
    ETAG --> R200_A["200 AdminOrderDetailResponse<br/>+ paymentSession, lifecycle, capabilities,<br/>summary status expired/active"]
    R200_M --> END_S((◉))
    R200_A --> END_S
    R404_O --> END_F((◉))
    R404_A --> END_F
    R404_H --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class BINDA,AUDIT,LINES,ETAG,R200_M,R200_A,END_S ok
    class R404_O,R404_A,R404_H,END_F fail
    class AUTHZ,AUD,IDREQ,OWNER,RESOURCE,FOUND,SESSION,SHAPE gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/orders` | subject: list แบ่งหน้าแทน detail เดี่ยว ผ่าน SFS (`SfsQueryParser.Parse`, maxLimit 100) allowlist `orderNo` (eq/contains), `status` (eq/in), `paymentChannel` (eq/in), sort `createdAt`/`orderNo` ดู § 0.6; ไม่มี reveal-audit, ไม่มี ETag; Merchant -> `GetOrdersQuery` -> `PagedResult<OrderListItem>` (ไม่มี metadata); Admin -> query `merchantId` optional (guid ผิด -> 400 `invalid_filter`) -> `AdminOrderQuery` ตาม accessible merchants -> `PagedResult<AdminOrderListResponse>`; ไม่มี owner-identity check ราย order (source: `Program.cs:2184-2238`, `GetOrders.cs`) |
| GET | `/api/v1/orders/{orderId:guid}/payment-links` | subject: คืน array `PaymentLinkView` ของ order (ไม่มี raw token), ไม่มี reveal-audit, ไม่มี ETag; identity request ตรวจ owner ด้วย `EnsureIdentityOrderOwnerAsync` เหมือน § กลาง; **ไม่มี branch Admin ในซอร์สเลย** (ไม่เรียก `IsAdminCommerceRequest`) — Admin console (platform Bearer) ที่เรียก endpoint นี้จะได้ `actor.MerchantId` throw `InvalidOperationException` เพราะไม่มี `merchant_id` claim และไม่มีการ `actorScope.Begin` ในนี้ ต่างจาก sibling POST (§ 4.8) ที่จัดการ Admin ชัดเจน — ตอบ **409** ตาม § 0.9 ไม่ใช่ 404/403 (ดู Deviations) (source: `Program.cs:1049-1074`, `HttpActorContext.cs:56-58`) |

---

## 4.4 สร้างคำสั่งซื้อแบบ canonical

ใช้ trusted pricing เท่านั้น (ไม่รับราคาจาก client) มี idempotency 2 ชั้น (persisted replay + in-flight claim, ดู Notes) และแตก draft/issue-now ในธุรกรรมเดียว (source: `Program.cs:2060-2117,3766-3833`, `OrderWorkflow.cs:593-822`, `OrderOwnerResolver.cs`, `TrustedOrderPricingSource.cs`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy identity-platform (Bearer platform JWT)<br/>RequireIdentityPermission(payment.create)<br/>RequireIdentityPlatformMutation ดู § 0.2 / § 0.3"]
    AUTHZ --> VALID{"ValidateCanonicalCreateRequest:<br/>businessType, currency 3 ตัวอักษร, items>=1,<br/>metadata VersionedMetadata, money field ทุกตัว?"}
    VALID -->|no| R400_V["400 validation_failed / items_required / metadata_invalid"]
    VALID -->|yes| NOTIF{"NotificationIntentNormalizer:<br/>email/phone รูปแบบถูกต้อง,<br/>มี recipient เมื่อ send=true?"}
    NOTIF -->|no| R400_N["400 validation_failed /<br/>notification_recipient_required"]
    NOTIF -->|yes| MCTX{"ResolveIdentityMerchantId:<br/>claim merchant_id มีและตรง query (ถ้าส่งมา)?"}
    MCTX -->|"ไม่มี claim"| R403_MM["403 merchant_context_missing"]
    MCTX -->|"ไม่ตรง"| R403_CM["403 merchant_context_mismatch"]
    MCTX -->|yes| ACC{"actor.UserId (accountId) ไม่ว่าง?"}
    ACC -->|no| R403_AC["403 account_context_missing"]
    ACC -->|yes| OWNER["OrderOwnerResolver.ResolveAsync:<br/>Agent ใช้ AgentSaleId เสมอ, Employee/SYSTEM<br/>ตรวจ requested Sale/Branch กับ DataScope"]
    OWNER --> OWNEROK{"owner resolve ผ่าน?"}
    OWNEROK -->|"no, access denied"| R403_OW["403 owner_context_denied / owner_sale_missing /<br/>owner_sale_conflict / owner_branch_conflict / owner_scope_denied /<br/>owner_sale_denied / owner_branch_denied"]
    OWNEROK -->|"no, branch ไม่ตรง sale"| R400_OWB["400 owner_branch_mismatch"]
    OWNEROK -->|yes| PRICE["TrustedOrderPricingSource.PriceAsync:<br/>businessType=insurance, Sale/Merchant active,<br/>currency THB, qty=1 ต่อบรรทัด, เอกสารยังไม่ถูกขาย"]
    PRICE --> PRICEOK{"pricing ผ่าน?"}
    PRICEOK -->|no| R409_PR["409 unsupported_business_type / owner_required /<br/>merchant_unavailable / source_currency_unsupported /<br/>source_quantity_invalid / product_unavailable /<br/>source_owner_mismatch / product_unpayable / source_ambiguous"]
    PRICEOK -->|yes| SNAP{"client ส่ง ClientSnapshot มา:<br/>ProductCode/Name/ราคา/ส่วนลด/ภาษี/ยอด<br/>ตรงกับ trusted pricing ทุกบรรทัด และ<br/>orderDiscount/orderCharge (ถ้าส่งมา) ตรง?"}
    SNAP -->|no| R409_SN["409 pricing_mismatch"]
    SNAP -->|yes| REPLAY1{"PaymentLinkReplayService.TryReplayAsync<br/>(merchant, order.create, key):<br/>มี replay record เดิม?"}
    REPLAY1 -->|"expired"| R409_EXP["409 idempotent_secret_expired"]
    REPLAY1 -->|"hash ต่าง"| R409_CONF["409 idempotency_conflict"]
    REPLAY1 -->|"match"| R201_REPLAY["201 ผลเดิม (Replayed = true)"]
    REPLAY1 -->|"ไม่มี"| CLAIM{"RequireFirstDeliveryAsync:<br/>claim scoped key ครั้งแรก?"}
    CLAIM -->|"ซ้ำ"| R409_REP["409 idempotency_replay"]
    CLAIM -->|yes| DRAFT["Order.CreateDraft: Draft, OrderNo ใหม่,<br/>line snapshot จาก trusted pricing"]
    DRAFT --> ISSUE{"body.issueNow?"}
    ISSUE -->|true| ISSUENOW["order.Issue -> Open<br/>OrderLinkIssuer.Issue: สร้าง PaymentLink 72h<br/>persist replay record (protected raw token)"]
    ISSUENOW --> NOTIFY{"NotifyOnIssue?"}
    NOTIFY -->|"yes, ไม่มี recipient"| R409_NR["409 notification_recipient_required"]
    NOTIFY -->|"yes, outbox ports ไม่ครบ"| R503["503 DependencyUnavailableException"]
    NOTIFY -->|"yes, ok"| OUTBOX["Outbox.Enqueue PaymentLinkNotificationRequestedV1"]
    NOTIFY -->|no| SAVE
    OUTBOX --> SAVE["SaveChanges + commit"]
    ISSUE -->|false| DRAFTREPLAY["persist replay record ชนิด draft (ไม่มี link)"]
    DRAFTREPLAY --> SAVE
    SAVE --> R201["201 Created Location /api/v1/orders/{orderId}<br/>OrderCommandResult (+ link/rawToken เมื่อ issueNow)<br/>header ETag = vN"]
    R201 --> END_S((◉))
    R201_REPLAY --> END_S
    SAVE -.async.-> BG["ผู้บริโภค outbox ส่ง PaymentLink notification ดู § 0.8"]
    R400_V --> END_F((◉))
    R400_N --> END_F
    R403_MM --> END_F
    R403_CM --> END_F
    R403_AC --> END_F
    R403_OW --> END_F
    R400_OWB --> END_F
    R409_PR --> END_F
    R409_SN --> END_F
    R409_EXP --> END_F
    R409_CONF --> END_F
    R409_REP --> END_F
    R409_NR --> END_F
    R503 --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    class DRAFT,ISSUENOW,OUTBOX,SAVE,DRAFTREPLAY,R201,END_S ok
    class R400_V,R400_N,R403_MM,R403_CM,R403_AC,R403_OW,R400_OWB,R409_PR,R409_SN,R409_EXP,R409_CONF,R409_REP,R409_NR,R503,END_F fail
    class AUTHZ,VALID,NOTIF,MCTX,ACC,OWNEROK,PRICEOK,SNAP,REPLAY1,CLAIM,ISSUE,NOTIFY gate
    class R201_REPLAY,BG warn
```

---

## 4.5 สร้างคำสั่งซื้อจากตะกร้า

สอง phase: `PrepareAsync` ตรวจสถานะเอกสารสดก่อนเปิด transaction, `CommitAsync` ล็อกและเขียนจริงในธุรกรรมเดียว; เฉพาะ Admin ที่มี idempotency คั่นกลาง (source: `Program.cs:2121-2182`, `OrderCreationCoordinator.cs`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy dual-console + permission payment.create<br/>RequireAudienceCsrf ดู § 0.1 / § 0.3"]
    AUTHZ --> AUD{"Admin request? (IsAdminCommerceRequest)"}
    AUD -->|"no, Merchant"| FORBID{"body.merchantId หรือ originatorId ถูกส่งมา?"}
    FORBID -->|yes| R400_F["400 validation_failed<br/>Merchant order creation forbids merchantId/originatorId"]
    FORBID -->|no| SALE{"actor.SaleCode ไม่ว่าง และมี actor.UserId?"}
    SALE -->|no| R403_S["403 sale-code-missing / merchant-user-unbound"]
    SALE -->|yes| PREPARE_M["coordinator.CreateAsync<br/>(PrepareAsync + CommitAsync ต่อกัน) InitiatingAudience.User"]
    AUD -->|"yes, Admin"| BODY{"body.merchantId, originatorId<br/>เป็น GUID ไม่ว่างทั้งคู่?"}
    BODY -->|no| R400_B["400 validation_failed"]
    BODY -->|yes| ORIGLOOK{"RequireCommerceOriginatorAsync:<br/>scope อนุญาต, Originator active, มี SaleCode?"}
    ORIGLOOK -->|no| R403_OR["403 merchant_scope_forbidden / originator_scope_forbidden"]
    ORIGLOOK -->|yes| CARTCHK{"RequireAdminCartAsync(cartId, merchantId, mutation:true):<br/>cart พบและ merchant ตรง?"}
    CARTCHK -->|"ไม่พบ"| R404_CART["404 Cart was not found"]
    CARTCHK -->|"merchant ไม่ตรง"| R403_CM["403 merchant_scope_forbidden"]
    CARTCHK -->|yes| ORIGMATCH{"cart.OriginatorId = originatorId ที่ส่งมา?"}
    ORIGMATCH -->|no| R409_ST["409 state_conflict<br/>Cart originator does not match the request"]
    ORIGMATCH -->|yes| BIND["actorScope.Begin(merchantId)"]
    BIND --> PREPARE_A["coordinator.PrepareAsync (ก่อนเปิด transaction)<br/>InitiatingAudience.PlatformAdmin"]
    PREPARE_M --> CARTOPEN{"PrepareAsync: cart พบ (GetCartQuery)?"}
    PREPARE_A --> CARTOPEN
    CARTOPEN -->|no| R404_2["404 Cart was not found"]
    CARTOPEN -->|yes| OPEN2{"cart.Status = Open?"}
    OPEN2 -->|no| R409_NOTOPEN["409 Cart is not open"]
    OPEN2 -->|yes| HASITEMS{"cart.Items.Count > 0?"}
    HASITEMS -->|no| R400_EMPTY["400 Cannot create an order from an empty cart"]
    HASITEMS -->|yes| ORIGPARAM{"PrepareAsync: originatorId (param) =<br/>cart.OriginatorId? (Merchant ส่ง null เสมอ,<br/>Admin ส่ง originatorId ที่ตรวจแล้ว)"}
    ORIGPARAM -->|no| R409_OP["409 state_conflict<br/>Cart originator does not match the requested originator"]
    ORIGPARAM -->|yes| DOCCHK["ต่อบรรทัด: LookupDocumentQuery(ProductCode, ProductGroup, SaleCode)"]
    DOCCHK --> DOCOK{"เอกสารพบ, ยังไม่ PAID,<br/>DocumentSaleProbe ไม่พบว่าขายแล้ว (ทั้งชุด)?"}
    DOCOK -->|no| R409_UNAVAIL["409 Cart product is no longer available"]
    DOCOK -->|yes| EXECM{"Admin?"}
    EXECM -->|no| COMMIT_M["coordinator.CommitAsync ตรงทันที"]
    EXECM -->|yes| IDEMP{"Idempotency-Key ถูกต้อง? ดู § 0.5"}
    IDEMP -->|no| R400_I["400 invalid_idempotency_key"]
    IDEMP -->|yes| EXECA["AdminOperationExecutor(order.create) ดู § 0.5"]
    EXECA --> PRIOR{"key ซ้ำ?"}
    PRIOR -->|"hash ต่าง"| R409_K["409 idempotency_key_reused"]
    PRIOR -->|"InProgress"| R409_P["409 operation_in_progress"]
    PRIOR -->|"Succeeded"| REPLAY["201 ผลเดิม (Replayed = true)"]
    PRIOR -->|"ไม่มี"| COMMIT_A["coordinator.CommitAsync"]
    COMMIT_M --> LOCK["AcquireMerchantSharedAsync"]
    COMMIT_A --> LOCK
    LOCK --> CAP{"EffectivePaymentCapabilityResolver:<br/>paymentMethod อนุญาตสำหรับ subject นี้?"}
    CAP -->|"no, denied policy"| R403_PM["403 payment_method_not_allowed"]
    CAP -->|"no, unavailable"| R409_PC["409 payment_capability_unavailable"]
    CAP -->|yes| RELOAD{"ReloadTrackedAsync: cart พบ, merchant ตรง, Open,<br/>Version และบรรทัดตรงกับที่ตรวจไว้ใน PrepareAsync?"}
    RELOAD -->|"ไม่พบ/merchant ไม่ตรง"| R404_3["404 Cart was not found"]
    RELOAD -->|"ไม่ Open"| R409_NOTOPEN2["409 Cart is not open"]
    RELOAD -->|"เปลี่ยนระหว่างตรวจ"| R409_RACE["409 ConcurrencyConflict<br/>Cart changed after validation"]
    RELOAD -->|yes| ORDER["Order.Create: OrderNo ใหม่, บรรทัดจาก cart snapshot,<br/>Status Pending"]
    ORDER --> OUTBOX["Outbox.Enqueue CustomerOrderNotification"]
    OUTBOX --> CHECKOUT["cart.MarkCheckedOut (Status CheckedOut, Version+1)"]
    CHECKOUT --> SAVE["SaveChanges + commit"]
    SAVE --> R201["201 Created Location /api/v1/orders/{orderId}<br/>DirectOrderResult"]
    R201 --> END_S((◉))
    REPLAY --> END_S
    SAVE -.async.-> BG["ผู้บริโภค outbox ส่งอีเมล/SMS สรุปคำสั่งซื้อ ดู § 0.8"]
    R400_F --> END_F((◉))
    R403_S --> END_F
    R400_B --> END_F
    R403_OR --> END_F
    R404_CART --> END_F
    R403_CM --> END_F
    R409_ST --> END_F
    R404_2 --> END_F
    R409_NOTOPEN --> END_F
    R400_EMPTY --> END_F
    R409_OP --> END_F
    R409_UNAVAIL --> END_F
    R400_I --> END_F
    R409_K --> END_F
    R409_P --> END_F
    R403_PM --> END_F
    R409_PC --> END_F
    R404_3 --> END_F
    R409_NOTOPEN2 --> END_F
    R409_RACE --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    class BIND,ORDER,OUTBOX,CHECKOUT,SAVE,R201,END_S ok
    class R400_F,R403_S,R400_B,R403_OR,R404_CART,R403_CM,R409_ST,R404_2,R409_NOTOPEN,R400_EMPTY,R409_OP,R409_UNAVAIL,R400_I,R409_K,R409_P,R403_PM,R409_PC,R404_3,R409_NOTOPEN2,R409_RACE,END_F fail
    class AUTHZ,AUD,FORBID,SALE,BODY,ORIGLOOK,CARTCHK,ORIGMATCH,CARTOPEN,OPEN2,HASITEMS,ORIGPARAM,DOCOK,EXECM,IDEMP,PRIOR,CAP,RELOAD gate
    class REPLAY,BG warn
```

---

## 4.6 ส่งออกรายการคำสั่งซื้อ

Admin-only ต้องระบุช่วงวันที่ไม่เกิน 31 วัน แล้วส่งออกสูงสุด 10,000 แถวเป็น CSV (source: `Program.cs:2240-2308,4027-4045`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy admin + permission txn.export ดู § 0.1"]
    AUTHZ --> WINDOW{"query from, to เป็น ISO-8601 instant,<br/>to >= from, to-from <= 31 วัน?"}
    WINDOW -->|no| R400_W["400 invalid_filter<br/>from/to must be an ISO-8601 instant"]
    WINDOW -->|yes| SFS["SfsQueryParser.Parse(maxLimit:100) ดู § 0.6"]
    SFS --> MID{"query merchantId (ถ้าส่งมา)<br/>เป็น GUID ไม่ว่าง?"}
    MID -->|no| R400_M["400 invalid_filter"]
    MID -->|yes| QUERY["AdminOrderQuery: filters เดิม + createdAt<br/>ระหว่าง from..to, Page 1, Limit 10001<br/>ตาม accessible merchants ของ admin"]
    QUERY --> SIZE{"result.Total > 10,000?"}
    SIZE -->|yes| R422["422 export_too_large<br/>Export contains too many rows"]
    SIZE -->|no| CSV["สร้าง CSV: orderId,orderNo,merchantId,<br/>originatorId,amount,currency,status,<br/>itemCount,createdAt,updatedAt"]
    CSV --> R200["200 text/csv attachment<br/>orders-{from}-{to}.csv"]
    R200 --> END_S((◉))
    R400_W --> END_F((◉))
    R400_M --> END_F
    R422 --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class QUERY,CSV,R200,END_S ok
    class R400_W,R400_M,R422,END_F fail
    class AUTHZ,WINDOW,SFS,MID,SIZE gate
```

---

## 4.7 ยกเลิกคำสั่งซื้อ

มี 3 เส้นจริง: merchant console ธรรมดา (ไม่มี version/idempotency), identity request (version+idempotency, `CancelManagedOrderCommand`), admin (version+idempotency ผ่าน `ExecuteRecoverableAdminCommerceAsync`) — ทุกเส้นปล่อย payment session ที่ค้างก่อนเสมอ โดยพิสูจน์กับ PSP ว่าไม่มีเงินเข้าได้แล้ว (source: `Program.cs:1993-2056`, `CancelOrder.cs`, `OrderWorkflow.cs:1301-1361`, `ReleaseOpenSessionHandler.cs`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy dual-console + RequireOrderIdentityPermission(payment.create, order.write)<br/>RequireAudienceCsrf ดู § 0.1 / § 0.2 / § 0.3"]
    AUTHZ --> REASON{"body.reason (ถ้าส่งมา) ไม่ว่าง<br/>และไม่เกิน 1000 ตัวอักษร?"}
    REASON -->|no| R400_R["400 validation_failed"]
    REASON -->|yes| AUD{"Admin request? (IsAdminCommerceRequest)"}

    AUD -->|no| IDREQ{"IsIdentityRequest (Bearer)?"}
    IDREQ -->|yes| OWNER{"EnsureIdentityOrderOwnerAsync ผ่าน?"}
    OWNER -->|no| R404_O["404 Order was not found"]
    OWNER -->|yes| REASON_ID{"command.Reason ไม่ว่าง?<br/>(RequireReason บังคับเฉพาะ identity path)"}
    REASON_ID -->|no| R400_IR["400 invalid_reason"]
    REASON_ID -->|yes| CLAIM_ID{"CancelManagedOrderCommand: version + Idempotency-Key<br/>บังคับ, RequireFirstDeliveryAsync claim key ครั้งแรก?"}
    CLAIM_ID -->|ซ้ำ| R409_REPID["409 idempotency_replay"]
    CLAIM_ID -->|yes| LOCK_ID["GetForUpdateAsync(orderId) ล็อก row"]
    LOCK_ID --> STATE_ID{"order พบ? Cancelled อยู่แล้ว?<br/>Version ตรง? ยังไม่ Paid/Refunded?<br/>ไม่มี blocking payment session?"}
    STATE_ID -->|"ไม่พบ"| R404_NF["404 Order was not found"]
    STATE_ID -->|"Cancelled อยู่แล้ว"| R200_IDEM["200 ผลเดิม (idempotent)"]
    STATE_ID -->|"version ไม่ตรง"| R409_CC["409 ConcurrencyConflict"]
    STATE_ID -->|"Paid/Refunded"| R409_PAID["409 order_already_paid"]
    STATE_ID -->|"blocking session"| R409_PEND["409 payment_pending_verification"]
    STATE_ID -->|ok| CANCEL_ID["order.Cancel + revoke active link ถ้ามี<br/>SaveChanges + commit"]
    CANCEL_ID --> R200_ID["200 CancelOrderResult"]

    IDREQ -->|no| RELEASE_P["ReleaseOpenSessionCommand(orderId)<br/>plain merchant, ไม่มี version/idempotency"]
    RELEASE_P --> RELCHK_P["ไม่มี open session -> ผ่านทันที<br/>มี session -> PaymentConfirmationService ยิง PSP ยืนยัน"]
    RELCHK_P --> RELOK_P{"ผลคือ Expired หรือ Failed<br/>(พิสูจน์ได้ว่าไม่มีเงินเข้า)?"}
    RELOK_P -->|no| R409_SESS["409 Conflict<br/>session ยังจ่ายได้ / ยืนยันกับ PSP ไม่สำเร็จ (ambiguous)"]
    RELOK_P -->|yes| STATE_P{"CancelOrderCommand(orderId, ไม่มี ExpectedVersion):<br/>order พบ? สถานะเป็น Pending/Draft/Open<br/>และยังไม่ Paid? ไม่มี blocking session ใหม่?"}
    STATE_P -->|"ไม่พบ"| R404_NF
    STATE_P -->|"สถานะไม่ให้ยกเลิก"| R409_STP["409 Order cannot be cancelled from status ..."]
    STATE_P -->|"blocking session ใหม่"| R409_PEND2["409 active payment session"]
    STATE_P -->|ok| CANCEL_PLAIN["order.Cancel + revoke active link<br/>SaveChanges + commit"]
    CANCEL_PLAIN --> R200_P["200 CancelOrderResult"]

    AUD -->|yes| Q{"query merchantId ไม่ว่าง GUID?"}
    Q -->|no| R400_Q["400 invalid_filter"]
    Q -->|yes| RESOURCE{"RequireAdminOrderAsync(orderId, merchantId, mutation:true)<br/>พบและตรง merchant?"}
    RESOURCE -->|no| R404_RES["404 Order was not found / 403 merchant_scope_forbidden"]
    RESOURCE -->|yes| VER_A{"If-Match ถูกต้อง?"}
    VER_A -->|no| R400_E["400 invalid_etag"]
    VER_A -->|yes| BIND["actorScope.Begin(merchantId)"]
    BIND --> IDEMP_A{"Idempotency-Key ถูกต้อง? ดู § 0.5"}
    IDEMP_A -->|no| R400_I["400 invalid_idempotency_key"]
    IDEMP_A -->|yes| EXECA["ExecuteRecoverableAdminCommerceAsync(order.cancel):<br/>claim/replay ก่อนเปิด transaction ดู § 0.5"]
    EXECA --> PRIOR{"key ซ้ำ?"}
    PRIOR -->|"hash ต่าง"| R409_K["409 idempotency_key_reused"]
    PRIOR -->|"Succeeded"| REPLAY_A["200 ผลเดิม (Replayed = true)"]
    PRIOR -->|"ไม่มี, claim InProgress"| RELEASE_A["ReleaseOpenSessionCommand แล้ว<br/>PaymentConfirmationService ยิง PSP (เหมือน RELCHK_P)"]
    RELEASE_A --> RELOK_A{"Expired หรือ Failed?"}
    RELOK_A -->|no| R409_SESSA["409 session ยังจ่ายได้/ยืนยันไม่สำเร็จ<br/>(record ค้าง InProgress ไว้ retry ปลอดภัย)"]
    RELOK_A -->|yes| STATE_A{"Cancelled อยู่แล้ว?<br/>Version ตรง? สถานะ Pending/Draft/Open<br/>และยังไม่ Paid? ไม่มี blocking session ใหม่?"}
    STATE_A -->|"Cancelled อยู่แล้ว"| R200_IDEM_A["200 ผลเดิม (idempotent)"]
    STATE_A -->|no| R409_STA["409 ตามเหตุ (version / status / blocking)"]
    STATE_A -->|yes| CANCEL_A["order.Cancel + revoke link<br/>record Succeeded, SaveChanges + commit"]
    CANCEL_A --> UPDATED["RequireAdminOrderAsync ซ้ำ อ่าน Version ล่าสุด"]
    UPDATED --> ETAG_A["VersionEtags.Set(updated.Version)"]
    ETAG_A --> R200_A["200 CancelOrderResult"]

    R200_ID --> END_S((◉))
    R200_IDEM --> END_S
    R200_IDEM_A --> END_S
    R200_P --> END_S
    R200_A --> END_S
    REPLAY_A --> END_S
    R400_R --> END_F((◉))
    R404_O --> END_F
    R400_IR --> END_F
    R409_REPID --> END_F
    R404_NF --> END_F
    R409_CC --> END_F
    R409_PAID --> END_F
    R409_PEND --> END_F
    R409_SESS --> END_F
    R409_STP --> END_F
    R409_PEND2 --> END_F
    R400_Q --> END_F
    R404_RES --> END_F
    R400_E --> END_F
    R400_I --> END_F
    R409_K --> END_F
    R409_SESSA --> END_F
    R409_STA --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    class CANCEL_ID,CANCEL_PLAIN,CANCEL_A,BIND,UPDATED,ETAG_A,R200_ID,R200_P,R200_A,END_S ok
    class R400_R,R404_O,R400_IR,R409_REPID,R404_NF,R409_CC,R409_PAID,R409_PEND,R409_SESS,R409_STP,R409_PEND2,R400_Q,R404_RES,R400_E,R400_I,R409_K,R409_SESSA,R409_STA,END_F fail
    class AUTHZ,REASON,AUD,IDREQ,OWNER,REASON_ID,CLAIM_ID,STATE_ID,RELOK_P,STATE_P,Q,RESOURCE,VER_A,IDEMP_A,PRIOR,RELOK_A,STATE_A gate
    class R200_IDEM,R200_IDEM_A,REPLAY_A gate
```

---

## 4.8 ออก/หมุน PaymentLink ของคำสั่งซื้อ

`RotatePaymentLink` เพิกถอนลิงก์ active เดิมแล้วออกใหม่ในธุรกรรมเดียว มี idempotency 2 ชั้นเหมือน § 4.4 (persisted replay + in-flight claim) และบังคับ `If-Match`/`Idempotency-Key` ทั้งสอง audience เสมอ (source: `Program.cs:1126-1177`, `OrderWorkflow.cs:928-1047`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy dual-console + RequireOrderIdentityPermission(payment.create, checkout.write)<br/>RequireAudienceCsrf ดู § 0.1 / § 0.2 / § 0.3"]
    AUTHZ --> AUD{"Admin request? (IsAdminCommerceRequest)"}
    AUD -->|yes| Q{"query merchantId ไม่ว่าง GUID?"}
    Q -->|no| R400_Q["400 invalid_filter"]
    Q -->|yes| RESOURCE{"RequireAdminOrderAsync(orderId, merchantId, mutation:true)<br/>พบและตรง merchant?"}
    RESOURCE -->|no| R404_RES["404 Order was not found / 403 merchant_scope_forbidden"]
    RESOURCE -->|yes| BIND["actorScope.Begin(merchantId)"]
    AUD -->|no| IDREQ{"IsIdentityRequest (Bearer)?"}
    IDREQ -->|yes| OWNER{"EnsureIdentityOrderOwnerAsync ผ่าน?"}
    OWNER -->|no| R404_O["404 Order was not found"]
    OWNER -->|yes| MID_M["merchantId = actor.MerchantId"]
    IDREQ -->|no| MID_M
    BIND --> IFM{"If-Match รูป quoted vN ถูกต้อง? (ทั้งสอง audience เสมอ)"}
    MID_M --> IFM
    IFM -->|no| R400_E["400 invalid_etag"]
    IFM -->|yes| IDEMP{"Idempotency-Key ถูกต้อง? (ทั้งสอง audience เสมอ)"}
    IDEMP -->|no| R400_I["400 invalid_idempotency_key"]
    IDEMP -->|yes| REPLAY1{"PaymentLinkReplayService.TryReplayAsync<br/>(merchant, payment-link.rotate, key):<br/>มี replay record เดิม?"}
    REPLAY1 -->|"expired"| R409_EXP["409 idempotent_secret_expired"]
    REPLAY1 -->|"hash ต่าง"| R409_CONF["409 idempotency_conflict"]
    REPLAY1 -->|"match"| R201_REPLAY["201 ผลเดิม (Replayed = true)"]
    REPLAY1 -->|"ไม่มี"| CLAIM{"RequireFirstDeliveryAsync claim key ครั้งแรก?"}
    CLAIM -->|"ซ้ำ"| R409_REP["409 idempotency_replay"]
    CLAIM -->|yes| LOAD{"GetForUpdateAsync(merchantId, orderId) พบ?"}
    LOAD -->|no| R404_H["404 Order was not found"]
    LOAD -->|yes| VER{"order.Version ตรง expected?"}
    VER -->|no| R409_CC["409 ConcurrencyConflict"]
    VER -->|yes| STATE{"order.Status = Open และยังไม่ Paid?"}
    STATE -->|no| R409_ST["409 order_state_conflict<br/>Only an unpaid issued order can rotate a link"]
    STATE -->|yes| NOTIF{"SendNotification=true และไม่มี recipient?"}
    NOTIF -->|yes| R409_NR["409 notification_recipient_required"]
    NOTIF -->|no| ACTIVE{"มี active PaymentLink ของ order นี้?"}
    ACTIVE -->|no| R409_MISS["409 payment_link_missing"]
    ACTIVE -->|yes| REVOKE["active.Revoke + order.RegisterLinkRotation<br/>OrderLinkIssuer.Issue: ลิงก์ใหม่ 72h, rotatedFrom=active.Id"]
    REVOKE --> PERSIST["persist replay record (protected raw token)"]
    PERSIST --> SENDN{"SendNotification=true?"}
    SENDN -->|"yes, port ไม่ครบ"| R503["503 DependencyUnavailableException"]
    SENDN -->|"yes, ok"| OUTBOX["Outbox.Enqueue PaymentLinkNotificationRequestedV1"]
    SENDN -->|no| SAVE
    OUTBOX --> SAVE["SaveChanges + commit"]
    SAVE --> ETAG["VersionEtags.Set(order.Version)"]
    ETAG --> R201["201 Created Location .../payment-links/{linkId}<br/>OrderCommandResult"]
    R201 --> END_S((◉))
    R201_REPLAY --> END_S
    SAVE -.async.-> BG["ผู้บริโภค outbox ส่งลิงก์ใหม่ ดู § 0.8"]
    R400_Q --> END_F((◉))
    R404_RES --> END_F
    R404_O --> END_F
    R400_E --> END_F
    R400_I --> END_F
    R409_EXP --> END_F
    R409_CONF --> END_F
    R409_REP --> END_F
    R404_H --> END_F
    R409_CC --> END_F
    R409_ST --> END_F
    R409_NR --> END_F
    R409_MISS --> END_F
    R503 --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    class REVOKE,SAVE,ETAG,R201,BIND,END_S ok
    class R400_Q,R404_RES,R404_O,R400_E,R400_I,R409_EXP,R409_CONF,R409_REP,R404_H,R409_CC,R409_ST,R409_NR,R409_MISS,R503,END_F fail
    class AUTHZ,AUD,Q,RESOURCE,IDREQ,OWNER,IFM,IDEMP,REPLAY1,CLAIM,LOAD,VER,STATE,NOTIF,ACTIVE,SENDN gate
    class R201_REPLAY,BG warn
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| POST | `/api/v1/orders/{orderId:guid}/issue` | permission `payment.create (identity: order.write)` (ไม่ใช่ `checkout.write`); ไม่มีขั้น "หา active link เดิม" หรือ revoke — เป็นการออกลิงก์ **แรก**; precondition ต่างกัน: order ต้อง `Status = Draft` เท่านั้น (`Open` อยู่แล้ว -> 409 `order_already_issued`, สถานะอื่น -> 409 `order_state_conflict`); เรียก `order.Issue` (Draft -> Open) ก่อนออกลิงก์; ไม่มี `SendNotification` จาก body (ใช้ `order.NotifyOnIssue` ที่ตั้งไว้ตอนสร้างเท่านั้น); ตอบ **200** (ไม่ใช่ 201) พร้อม `OrderCommandResult`, ไม่มี `Location` header; replay operation name `order.issue` (source: `Program.cs:1179-1226`, `OrderWorkflow.cs:824-926`) |

---

## 4.9 ส่งลิงก์สรุปคำสั่งซื้อซ้ำ

หมุน token สรุปคำสั่งซื้อและต่ออายุ TTL แล้ว re-notify ลูกค้า เฉพาะ Admin ที่บังคับ `If-Match`/`Idempotency-Key` ผ่าน `AdminOperationExecutor` (source: `Program.cs:1948-1983`, `ResendOrderSummary.cs`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy dual-console + permission payment.create<br/>RequireAudienceCsrf ดู § 0.1 / § 0.3"]
    AUTHZ --> AUD{"Admin request? (IsAdminCommerceRequest)"}
    AUD -->|"no, Merchant"| RUN_M["ResendOrderSummaryCommand(orderId, actor.MerchantId)<br/>ไม่มี ExpectedVersion"]
    AUD -->|"yes, Admin"| Q{"query merchantId ไม่ว่าง GUID?"}
    Q -->|no| R400_Q["400 invalid_filter"]
    Q -->|yes| RESOURCE{"RequireAdminOrderAsync(orderId, merchantId, mutation:true)<br/>พบและตรง merchant?"}
    RESOURCE -->|no| R404_RES["404 Order was not found / 403 merchant_scope_forbidden"]
    RESOURCE -->|yes| IFM{"If-Match ถูกต้อง? (admin เท่านั้น)"}
    IFM -->|no| R400_E["400 invalid_etag"]
    IFM -->|yes| BIND["actorScope.Begin(merchantId)"]
    BIND --> IDEMP{"Idempotency-Key ถูกต้อง? (admin เท่านั้น) ดู § 0.5"}
    IDEMP -->|no| R400_I["400 invalid_idempotency_key"]
    IDEMP -->|yes| EXEC["AdminOperationExecutor(order.summary.resend) ดู § 0.5"]
    EXEC --> PRIOR{"key ซ้ำ?"}
    PRIOR -->|"hash ต่าง"| R409_K["409 idempotency_key_reused"]
    PRIOR -->|"InProgress"| R409_P["409 operation_in_progress"]
    PRIOR -->|"Succeeded"| REPLAY["200 ผลเดิม (Replayed = true)"]
    PRIOR -->|"ไม่มี"| RUN_A["ResendOrderSummaryCommand(orderId, merchantId, expected)"]
    RUN_M --> FOUND{"order พบ (GetAsync)?"}
    RUN_A --> FOUND
    FOUND -->|no| R404_H["404 Order was not found"]
    FOUND -->|yes| VER{"ExpectedVersion (มีเฉพาะ admin)<br/>ตรงกับ order.Version หรือไม่ได้ส่งมา?"}
    VER -->|"ส่งมาแต่ไม่ตรง"| R409_CC["409 ConcurrencyConflict"]
    VER -->|yes| REISSUE{"order.ReissueSummary:<br/>order อยู่ในสถานะที่ resend ได้ (awaiting)?"}
    REISSUE -->|no| R409_ST["409 InvalidOperationException ไม่มี code"]
    REISSUE -->|yes| ROTATE["token ใหม่ + ต่ออายุ SummaryTokenExpiresAt"]
    ROTATE --> NOTIF{"มี NotificationRecipient และ SummaryToken?"}
    NOTIF -->|yes| OUTBOX["Outbox.Enqueue CustomerOrderNotification"]
    NOTIF -->|no| SAVE
    OUTBOX --> SAVE["SaveChanges + commit"]
    SAVE --> UPDATED["(admin) RequireAdminOrderAsync ซ้ำ อ่าน Version ล่าสุด"]
    UPDATED --> ETAG["VersionEtags.Set(updated.Version)"]
    ETAG --> R200["200 ResendOrderSummaryResult{summaryToken, expiresAt}"]
    R200 --> END_S((◉))
    REPLAY --> END_S
    SAVE -.async.-> BG["ผู้บริโภค outbox ส่งลิงก์สรุปใหม่ ดู § 0.8"]
    R400_Q --> END_F((◉))
    R404_RES --> END_F
    R400_E --> END_F
    R400_I --> END_F
    R409_K --> END_F
    R409_P --> END_F
    R404_H --> END_F
    R409_CC --> END_F
    R409_ST --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    class ROTATE,SAVE,ETAG,BIND,R200,END_S ok
    class R400_Q,R404_RES,R400_E,R400_I,R409_K,R409_P,R404_H,R409_CC,R409_ST,END_F fail
    class AUTHZ,AUD,Q,RESOURCE,IFM,IDEMP,PRIOR,FOUND,VER,REISSUE,NOTIF gate
    class REPLAY gate
```

---

## 4.10 ผลิตภัณฑ์ (แคตตาล็อกเอกสารประกัน)

รายการเอกสารประกันค้นสดจากต้นทาง SP ไม่มีสำเนาในระบบ; § กลางคือ Merchant Console (`ListProducts`), composed คือ Admin variant ที่ resolve `SaleCode` จาก Originator แทน `actor.SaleCode` (source: `Program.cs:1330-1391`, `ListProducts.cs`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy merchant-user + permission payment.view ดู § 0.1"]
    AUTHZ --> SALE{"actor.SaleCode ไม่ว่าง?"}
    SALE -->|no| R403_S["403 sale-code-missing"]
    SALE -->|yes| PAGING["SfsQueryParser.ParsePaging ดู § 0.6"]
    PAGING --> FILTER{"ProductFilterDto.Parse(productFilters):<br/>JSON ถูกรูป, PaymentStatus/CountMode<br/>เป็นค่าที่รู้จัก, ช่วงวันที่ From<=To ทุกคู่?"}
    FILTER -->|no| R400_F["400 Malformed productFilters / Invalid productFilters"]
    FILTER -->|yes| TARGET{"ResolveTarget: มี productGroup<br/>หรือ insuranceType อย่างน้อยหนึ่ง<br/>และไม่ขัดแย้งกัน?"}
    TARGET -->|no| R400_T["400 insuranceType is required when productGroup is absent /<br/>productGroup is not a {insuranceType} product group"]
    TARGET -->|yes| SEARCH["SpDocumentGateway.SearchAsync (SaleCode ของ actor เอง)"]
    SEARCH --> UP{"ต้นทางตอบสำเร็จ?"}
    UP -->|no| R503["503 UpstreamUnavailableException"]
    UP -->|yes| MAP["SpDocumentItemMapper.Map: แถวที่แปลงไม่ได้ถูกข้าม<br/>(totalRows ของต้นทางไม่เปลี่ยน)"]
    MAP --> PROBE["DocumentSaleProbe.ProbeAsync (ครั้งเดียวทั้งหน้า)<br/>หาเอกสารที่ order สถานะ Paid ถืออยู่แล้ว"]
    PROBE --> DROP{"paymentStatus ที่ขอ = UNPAID (ค่าเริ่มต้น)?"}
    DROP -->|yes| FILTEROUT["ตัดเอกสารที่ probe บอกว่าขายแล้วออกจากหน้า"]
    DROP -->|no| FLAG["คงทุกแถว ติด soldByPlatform=true เฉพาะที่ probe พบ"]
    FILTEROUT --> R200["200 ProductPage (items, totalRows/totalPages<br/>null เมื่อ countMode FAST)"]
    FLAG --> R200
    R200 --> END_S((◉))
    R403_S --> END_F((◉))
    R400_F --> END_F
    R400_T --> END_F
    R503 --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class FILTEROUT,FLAG,R200,END_S ok
    class R403_S,R400_F,R400_T,R503,END_F fail
    class AUTHZ,SALE,FILTER,TARGET,UP,DROP gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/products/documents` | permission `txn.view`, policy `admin` (ไม่ใช่ `merchant-user`); ต้องส่ง query `merchantId` + `originatorId` (GUID ไม่ว่างทั้งคู่ ไม่งั้น 400 `invalid_filter`) แล้วผ่าน `RequireCommerceOriginatorAsync` (scope, active, มี SaleCode ไม่งั้น 403 `merchant_scope_forbidden`/`originator_scope_forbidden`) แทนการเช็ค `actor.SaleCode`; ใช้ `originator.SaleCode` แทน `actor.SaleCode`; ผลลัพธ์ห่อเป็น `AdminProductPage` (มี merchantId/originatorId ติดไปด้วย); ไม่มี 403 `sale-code-missing` (source: `Program.cs:1358-1391`) |

---

## Deviations

| fullPath | เอกสารบอก | source บอก | อ้างอิง |
| --- | --- | --- | --- |
| `GET /api/v1/orders/{orderId:guid}/payment-links` | policy `dual-console` (รองรับทั้ง Admin และ Merchant console เหมือน endpoint พี่น้องในกลุ่ม order) | ไม่มี branch `IsAdminCommerceRequest` เลยในซอร์ส และไม่เรียก `actorScope.Begin`; Admin console เป็น platform Bearer ที่ไม่มี claim `merchant_id` เมื่อเรียก endpoint นี้จะได้ `actor.MerchantId` throw `InvalidOperationException` — ตอบ **409** ผ่าน exception handler กลาง ไม่ใช่ผลลัพธ์ปกติของ dual-console | `src/Api/Api/Program.cs:1049-1074`, `src/Api/Api/HttpActorContext.cs:56-58`, `src/Api/BuildingBlocks.Web/ProblemDetailsExceptionHandler.cs:89-90` |

## Notes

| เรื่อง | ข้อเท็จจริงจาก source | source |
| --- | --- | --- |
| idempotency 2 ชั้นของ order.create/issue/rotate | `PaymentLinkReplayService.TryReplayAsync` เช็ค record ที่ persist แล้วก่อน (คืนผลเดิมรวม raw token ที่ protect ไว้ ตรง intent hash), ถ้าไม่มีค่อย `RequireFirstDeliveryAsync` claim key ชั่วคราวกันสองคำขอพร้อมกันชนกันก่อน record ถูกเขียน; ReplayTtl 30 นาที (หรือสั้นกว่าตามอายุ link) หมดอายุ = `idempotent_secret_expired` ต้องเริ่ม key ใหม่ — อยู่นอก frame ของ diagram | `OrderWorkflow.cs:473-591`, `OrderCommandGuards.cs` (`OrderWorkflow.cs:298-357`) |
| idempotency ชั้นเดียวของ cancel/resend/cart | `CancelManagedOrderCommand` ใช้ `RequireFirstDeliveryAsync` อย่างเดียว (ไม่มี persisted replay) เพราะผลลัพธ์ไม่มี secret ต้อง cache — ยึด idempotency ที่ domain state (`Cancelled` แล้ว = คืนผลเดิมได้เอง); `AdminOperationExecutor` (cart, resend) ใช้ record เดียวเก็บทั้ง claim และ cached response | `OrderWorkflow.cs:1330-1361`, `AdminOperationExecutor.cs:24-60` |
| cart ไม่พบ = 409 ไม่ใช่ 404 (เส้น merchant) | Merchant path ของ item mutation ไม่มีการเช็คว่า cart มีอยู่จริงก่อนส่ง command — handler เท่านั้นที่เช็คแล้ว throw `InvalidOperationException` (409 ตาม § 0.9) เมื่อ cart ไม่พบหรือเป็นของ merchant อื่น ต่างจาก Admin path ที่เช็คก่อนด้วย `RequireAdminCartAsync` แล้วได้ 404 | `CartEdits.cs:23-36`, `AddItemToCartHandler.cs:24-28` |
| currency mismatch ในตะกร้า | `Cart.EnsureCurrencyMatches` โยน `InvalidOperationException` (409) เมื่อบรรทัดใหม่คนละสกุลเงินกับบรรทัดเดิม — ไม่วาดเป็น branch เพราะทุกเอกสารในระบบตั้งราคาเป็น THB เท่านั้นจริง (ไม่เคย trigger ได้จาก endpoint เหล่านี้) | `Cart.cs:158-166` |
| `412 Precondition Failed` เป็น dead metadata | `POST /orders`, `POST /orders/{id}/issue`, `POST /orders/{id}/payment-links` ประกาศ `.ProducesProblem(StatusCodes.Status412PreconditionFailed)` แต่ไม่มี path ในโค้ด (handler หรือ `ProblemDetailsExceptionHandler`) ที่ throw หรือคืน 412 จริงสำหรับ 3 endpoint นี้ — เป็น metadata ที่ตกค้างจาก endpoint อื่น (เช่น `IdentityAccessEndpoints.cs`) ไม่ใช่ branch จริง | `Program.cs:1177,1226,2116`, `ProblemDetailsExceptionHandler.cs:71-100` |
| reveal-audit ล้มเหลว (GET order detail) | `GetOrderDetailHandler` เขียน audit ก่อน map response แบบ fail-closed — ถ้า `SaveChangesAsync` ล้ม handler throw ต่อ ไม่มี code เฉพาะ ตกไปเป็น 5xx ทั่วไปของ § 0.9 ไม่ใช่ branch ที่แยกแสดงในไดอะแกรม เพราะไม่มีเงื่อนไขให้ตัดสินใจ (fail ทุกครั้งที่ save ล้ม) | `GetOrderDetail.cs:53-64` |
| PSP confirmation ของ cancel | `ReleaseOpenSessionHandler` เรียก `PaymentConfirmationService.ConfirmAsync` ซึ่งเป็นกลไกร่วมกับ theme checkout/payment (นอก scopeไฟล์นี้); เอกสารนี้แสดงเฉพาะผลลัพธ์สองทาง (Expired/Failed = ปล่อยได้, อื่น ๆ/เรียกไม่สำเร็จ = 409) ที่ cancel ต้องใช้ | `ReleaseOpenSessionHandler.cs` |
| paging ของ products | `GET /products` และ `/products/documents` ใช้ `SfsQueryParser.ParsePaging` (page/limit เท่านั้น, clamp ไม่ 400) ไม่ใช่ `SfsQueryParser.Parse` เต็มรูป (ไม่มี filters/sort/search) ตรงกับ variant ที่ระบุไว้แล้วใน § 0.6 | `Program.cs:1336,1369`, `00-cross-cutting §0.6` |

**Render**: GitHub / Obsidian / VS Code Mermaid
