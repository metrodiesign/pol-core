# โมดูล Orders — คำสั่งซื้อ ตั้งแต่ยืนยัน checkout ถึงชำระเงินสำเร็จ

> **[สร้างครั้งแรก 2026-08-01]** sync กับโค้ดจริงที่ commit ล่าสุดที่แตะ business logic ของโมดูลนี้
> (`2fe3dfa`, "feat(orders): mark sold documents PAID when an order is paid (Codex F2)", 2026-07-30).
> แหล่งความจริง: `src/Modules/Orders/**`, `src/Persistence/Persistence.MerchantRuntime/Orders/**`,
> `src/Hosts/Api/Program.cs` (route mapping)

## บริบท

Order คือจุดที่ funnel ธุรกิจ `Carts → CheckoutSessions → Orders → PaymentSessions` เปลี่ยนจาก
"กำลังเลือกซื้อ" เป็น "ผูกพันแล้ว" — เกิดจาก event `CheckoutConfirmed` ของโมดูล Checkouts เท่านั้น (ไม่มี
endpoint สร้าง order ตรงๆ ในระบบจริง) และจบเมื่อ Payments module ยืนยัน PSP ชำระสำเร็จ (`PaymentPaid`) หรือ
ถูกยกเลิก

ต่างจาก Carts (ephemeral, ไม่มี PII, mutate ได้อิสระ), Order เป็น **INSERT-only snapshot ของรายการที่
ซื้อจริง** (`Item`) บวก **PII ผู้เอาประกัน** บวก **capability token** ให้ลูกค้า anonymous เปิดดูสรุปได้โดย
ไม่ต้อง login บวก entity ลูกอีกตัว (`ItemPolicy`) ที่ mutable และเขียนโดยคนละ actor หลังการขาย — ความ
ซับซ้อนของโมดูลนี้ส่วนใหญ่มาจากการแยก "สิ่งที่ snapshot ตอนขาย" ออกจาก "ข้อมูลอ้างอิงกรมธรรม์ที่กรอกทีหลัง"
อย่างเด็ดขาด

**เส้นทางวิวัฒนาการ** (เรียงเวลา จาก `.ai/specs/`):
- `insurance-pivot` (approved 2026-07-20) — กำเนิด order line (ตอนนั้นชื่อ `OrderLine`) พก PII ผู้เอา
  ประกันต่อ line, masked list + full detail-read + reveal-audit
- `bugfix-order-paid-link` (approved 2026-07-04, ทำจริงหลัง insurance-pivot) — แก้บั๊ก order ค้าง
  `AwaitingPayment` ตลอดกาล เพราะ fulfillment เดิม resolve ด้วย field ที่ไม่มี production writer เลย
- `policy-reference-record` (approved 2026-07-22, amended 2026-07-23) — เพิ่ม `ItemPolicy` entity (เลข
  อ้างอิงกรมธรรม์ภายนอก + เบี้ยประกัน + สถานะตัดชำระ) และ fold rename `OrderLine` → `OrderItem` ทั้งระบบ
  (entity/table/column/route, behavior-preserving)
- `checkout-chain-document-fields` (approved 2026-07-30) — เปลี่ยน field ที่ snapshot จาก "แผนประกัน" เก่า
  (`SumInsured`/`CoverageDurationDays`/`Insurer`) เป็นฟิลด์เอกสารประกันจริง (`DocumentNo`/`ProductGroup`/
  `DocumentType`/`PolicyNumber`/`StartDate`/`EndDate`) ให้ตรงกับ Product ที่ pivot เป็นเอกสารไปแล้ว + wire
  `OrderPaid` ให้ Products module mark เอกสาร PAID

โมดูลต้นทาง (ดึงราคา/ProductGroup มาก่อนถึง Order ผ่าน Carts→Checkouts): [`carts.md`](carts.md)

## Domain model (`Orders.Domain`)

`Order` (`Order.cs`, 177 บรรทัด) — `public sealed class Order : AggregateRoot<Guid>` ถือ `Items` เป็น
owned collection ผ่าน field `_items`

| Property | Type | หมายเหตุ |
|---|---|---|
| `MerchantId` | `Guid` | ตั้งตอน ctor เท่านั้น |
| `PaymentSessionId` | `Guid?` | **legacy, ไม่มี production writer** — ดูหัวข้อ "จุดที่ไม่สมมาตร" ข้อ 2 |
| `CheckoutSessionId` | `Guid?` | idempotency key กัน `CheckoutConfirmed` ซ้ำสร้าง order สอง (unique filtered index ที่ DB) |
| `Amount` | `Money` | ยอดรวมทั้งใบ, ต้องเท่ากับผลรวม `Items` เป๊ะตอนสร้าง |
| `Status` | `OrderStatus` | ดู state machine ด้านล่าง |
| `CreatedAt` | `DateTime` | ตั้งตอน ctor เท่านั้น |
| `PaidAt` | `DateTime?` | ตั้งตอน `MarkPaid` transition จริงเท่านั้น |
| `SummaryToken` | `string` | opaque capability token (GUID N-format) ให้ลูกค้า anonymous เปิดสรุปได้ — หมุนได้ผ่าน `ReissueSummary` |
| `SummaryTokenExpiresAt` | `DateTime` | TTL = `Order.SummaryTokenTtl` = 72 ชั่วโมง (`:50`) |
| `NotificationRecipient` | `string?` | contact ลูกค้า เก็บไว้ให้ resend แจ้งซ้ำได้ |
| `Items` | `IReadOnlyCollection<Item>` | `_items.AsReadOnly()` — mutate ได้แค่ตอน `Create` เท่านั้น |

**State machine** (`OrderStatus.cs`: `AwaitingPayment=0`, `Paid=1`, `Cancelled=2`) — ทางเดียว terminal
ทั้งคู่:
```
AwaitingPayment ──MarkPaid(ยอด/currency ตรง)──▶ Paid        [terminal]
AwaitingPayment ──Cancel()────────────────────▶ Cancelled   [terminal]
```

**Mutate methods**:

| Method | Guard/throw | หมายเหตุ |
|---|---|---|
| `Create(merchantId, amount, createdAt, items, ...)` (`:94-126`, static factory) | `ArgumentException` ถ้า `items` ว่าง/null (REQ-6.7); `Quantity != 1` (defense-in-depth, checkout เช็คมาก่อนแล้ว); currency ของแต่ละ item ต้องตรง `amount`; ผลรวม `UnitPrice*Quantity` ทุก item ต้องเท่ากับ `amount` **เป๊ะ** ไม่มี tolerance (REQ-6.3) | จุดเดียวที่สร้าง `Item` ได้ |
| `ReissueSummary(now)` (`:78-85`) | `InvalidOperationException` ถ้า `Status != AwaitingPayment` | หมุน token ใหม่ + ต่อ TTL อีก 72 ชม. |
| `AttachPaymentSession(paymentSessionId)` (`:133-140`) | `InvalidOperationException` ถ้า `Status != AwaitingPayment` | **legacy — ดูหัวข้อ known gaps ข้อ 2** |
| `MarkPaid(paidAmount, occurredAt)` (`:148-164`) | `Status==Paid` → return `false` no-op (idempotent); `Status==Cancelled` → throw; amount/currency ไม่ตรง `Amount` เป๊ะ → throw | transition จริงเท่านั้นถึง `Raise(new OrderPaid(...))` และคืน `true` |
| `Cancel()` (`:167-176`) | no-op ถ้า cancelled แล้ว; `InvalidOperationException` ถ้า `Paid` | **0 caller ใน Application layer — ดูหัวข้อ known gaps ข้อ 1** |
| `IsSummaryExpired(now)` (`:74`) | — | read-only helper |

`Item` (namespace `Orders.Domain.Items`, คือสิ่งที่ทั่วโปรเจกต์เรียก "OrderItem") — `Entity<Guid>`, owned
โดย `Order` เท่านั้น ไม่มี navigation กลับหา parent (`Item.cs:13-87`) เป็น **purchase-time snapshot
INSERT-only**: `OrderId`, `MerchantId` (denormalize จาก parent — mirror `Carts.Domain.Items.Item`
ป้องกัน drift ด้วย composite FK), `ProductId`, `Quantity` (บังคับ=1), `UnitPrice: Money`, `DocumentNo`,
`ProductGroup`/`DocumentType` (wire value string ของ Products enum — ไม่ reference cross-module type),
`PolicyNumber: string?`, `StartDate`/`EndDate: DateTime?`, `InsuredFirstName`/`InsuredLastName`/
`InsuredIdNumber: string`, `InsuredDateOfBirth: DateTime` — ทั้งหมด copy จาก `Product`/`Cart.Item` ตอน
checkout-start เท่านั้น ไม่ re-read live ทีหลัง

Ctor `internal Item(...)` (`:50-86`) invariant: `DocumentNo`/`ProductGroup`/`DocumentType` ห้ามว่าง;
`StartDate > EndDate` → reject; `InsuredFirstName`/`InsuredLastName`/`InsuredIdNumber` ห้ามว่าง —
error message **ไม่ echo ค่าที่ invalid กลับ** มีแค่ชื่อ field (REQ-7.3, กัน PII หลุดผ่าน error); `InsuredDateOfBirth`
เป็นอนาคตไม่ได้

`ItemPolicy` (`ItemPolicy.cs`, 188 บรรทัด) — entity **แยกจาก** `Item` แบบ 1:1 (ADR-1) เพราะ `Item` เป็น
INSERT-only snapshot แต่ policy-reference data mutable เขียนโดยคนละ actor (operator หลังการขาย ไม่ผ่าน
checkout เลย) และมี audit trail ของตัวเอง — ฟอลด์เข้า `Item` จะบังคับ UPDATE grant บน snapshot aggregate ที่
ตั้งใจให้ immutable

| Property | Type | หมายเหตุ |
|---|---|---|
| `OrderItemId` | `Guid` | unique 1:1 กับ `Item` |
| `MerchantId` | `Guid` | ตั้งครั้งเดียวตอน `Create`, `Apply` ไม่แตะ |
| `InsuranceCategory` | `InsuranceCategory?` | `null` = ยังไม่กรอก (REQ-1.7/1.11) |
| `ReferenceNumberType` | `ReferenceNumberType?` | ต้องมาคู่กับ `ReferenceNumber` ทั้งสองทิศ |
| `ReferenceNumber` | `string?` | เลขกรมธรรม์/เลขรับแจ้ง |
| `EndorsementNumber` | `string?` | สลักหลัง — ต้องมี `ReferenceNumber` ตั้งไว้ก่อน |
| `RenewalReminderNumber` | `string?` | เลขใบเตือนต่ออายุ — ต้องมี `ReferenceNumber` ตั้งไว้ก่อน |
| `InsuredObjectReference` | `string?` | generic (ไม่ผูก Motor เท่านั้น, REQ-1.8) |
| `NetPremiumAmount`/`NetPremiumCurrency` | `decimal?`/`string?` | scalar pair ไม่ใช่ `ComplexProperty<Money?>` — เลี่ยง EF Core 10 bug (efcore#38043/#37249) |
| `GrossPremiumAmount`/`GrossPremiumCurrency` | `decimal?`/`string?` | เช่นเดียวกัน |
| `PremiumRemittanceStatus` | `PremiumRemittanceStatus` | default `NotApplicable` |
| `DeductedAt` | `DateOnly?` | client-supplied local date เท่านั้น ไม่ใช่ server timestamp |
| `NetPremium`/`GrossPremium` | `Money?` (computed) | ไม่ map ลง DB (`Ignore`) |

`Create(id, orderItemId, merchantId, nowUtc)` (`:87-97`) เริ่มแบบ all-unset

`Apply(input, nowUtc)` (`:104-179`) — mutator เดียวที่บังคับทุก invariant, throw `ArgumentException`
เสมอ (→ 400 ผ่าน `ProblemDetailsExceptionHandler`, ไม่ใช่ `BadHttpRequestException` ที่จะกลาย 500):
normalize (trim, blank→null) ทุก reference string; `ReferenceNumberType`↔`ReferenceNumber` ต้องมาคู่กัน
(REQ-3.9/3.10); `EndorsementNumber`/`RenewalReminderNumber` ต้องมี `ReferenceNumber` ตั้งไว้ก่อน
(REQ-3.11); `NetPremium`/`GrossPremium` ต้อง both-or-neither (REQ-3.12) และถ้าตั้งทั้งคู่ต้องเป็น THB
ทั้งคู่ (REQ-3.8) กับ `Net <= Gross` (REQ-3.7); `PremiumRemittanceStatus==Deducted` ต้องมี `DeductedAt`
และห้ามเป็นอนาคต — basis = วันที่ไทย local (`nowUtc.AddHours(7)`, REQ-2.5); revert เป็น `NotApplicable`
เคลียร์ `DeductedAt` ให้อัตโนมัติ (REQ-2.6)

`ItemPolicyAudit` — append-only fact ต่อการเขียน `ItemPolicy` แต่ละครั้ง: `OrderItemId`, `MerchantId`,
`ActorId`, `ActorKind`, `Operation`, `ChangeSummary` (comma-list **ชื่อ** field ที่เปลี่ยน ไม่ใช่ค่า — กัน
PII/เบี้ยประกันหลุดเข้า audit log), `CorrelationId`, `OccurredAt`

`RevealAudit` — append-only fact เขียนทุกครั้งที่ full insured-person data ถูกเปิดเผยผ่าน detail-read
(REQ-7.5): `OrderItemId`, `MerchantId`, `ActorType` (string `"admin"`/`"merchant-user"`), `ActorId`,
`CorrelationId`, `RevealedAt`

**Enum ทั้งหมด**:

| Enum | ค่า |
|---|---|
| `OrderStatus` | `AwaitingPayment=0`, `Paid=1`, `Cancelled=2` |
| `InsuranceCategory` | `Voluntary=0` (ภาคสมัครใจ), `Compulsory=1` (ภาคบังคับ/พ.ร.บ.) |
| `ReferenceNumberType` | `PolicyNumber=0` (เลขกรมธรรม์), `NotificationNumber=1` (เลขรับแจ้ง) |
| `PremiumRemittanceStatus` | `NotApplicable=0`, `Deducted=1` |
| `ActorKind` | `Admin=0`, `Merchant=1` |
| `AuditOperation` | `Created=0`, `Updated=1` |

Input record (`OrderItemInput`, `ItemPolicyInput`) อยู่ใน `Orders.Domain` (ไม่ใช่ Application) เพราะ
Domain factory (`Order.Create`, `ItemPolicy.Apply`) เรียก type ฝั่ง Application ไม่ได้ — csproj ไม่
reference ย้อนกลับ

**Domain event**: `Orders.Domain.OrderPaid` (`sealed record OrderPaid(Guid OrderId, DateTime PaidAt) :
IDomainEvent`) raise ผ่าน `Order.MarkPaid` แต่**ไม่เคยถูก dispatch จริง** — ทุก config `Ignore(x =>
x.DomainEvents)` เหมือนทุกโมดูลอื่นในระบบ (Carts, Checkouts) เป็นแค่ marker เอกสาร ไม่ใช่ mechanism จริง;
integration event ตัวจริงที่ Products module consume คือ `Contracts.OrderPaid` (คนละ type) ซึ่งเขียนผ่าน
outbox ตรงจาก `OrderPaidConsumer` เอง

## Application layer (`Orders.Application`)

| Handler | Input/Output | HTTP | Auth/Permission | Error |
|---|---|---|---|---|
| `CreateOrderHandler` (`CreateOrderCommand.cs`) | `CreateOrderCommand(MerchantId,Amount,Lines,Recipient?,CheckoutSessionId?)` → `CreateOrderResult(OrderId)` | **ไม่มี endpoint mapped** — เรียกได้แค่จาก test | `IMerchantScoped` (pipeline behavior) | — |
| `GetOrdersHandler` (`GetOrders.cs`) | `GetOrdersQuery(MerchantId)` → `OrdersListView` (`InsuredIdNumber` mask ทุกแถว) | `GET /orders` | `merchant-user` | 401 |
| `GetOrderDetailHandler` (`GetOrderDetail.cs`) | `GetOrderDetailCommand(MerchantId,OrderId,ActorType,ActorId)` → `OrderDetailView` (`InsuredIdNumber` เต็ม) | `GET /orders/{orderId}` | `merchant-user` | 401, `NotFoundException`→404; เขียน `RevealAudit` 1 แถว/item **ก่อน** build response — fail-closed |
| `ResendOrderSummaryHandler` (`ResendOrderSummary.cs`) | `ResendOrderSummaryCommand(OrderId,MerchantId)` → `ResendOrderSummaryResult(SummaryToken,ExpiresAt)` | `POST /orders/{orderId}/summary/resend` | `merchant-user` | `NotFoundException`→404; `ReissueSummary` throw→409 ถ้าไม่ `AwaitingPayment` |
| `GetReconciliationSummaryHandler` (`GetReconciliationSummary.cs`) | `GetReconciliationSummaryQuery(MerchantId)` → `ReconciliationView` (group by status+currency) | `GET /reports/reconciliation` | `merchant-user` | 401 |
| `UpsertItemPolicyHandler` (`UpsertItemPolicyCommand.cs`, merchant plane) | `UpsertItemPolicyCommand(MerchantId,OrderItemId,Input,ActorId)` → `UpsertItemPolicyResult(PolicyId)` | `PUT /orders/{orderId}/items/{itemId}/policy` | `merchant-user` + `Keys.PoliciesWrite` | item ไม่พบ/merchant อื่น → 404 (no existence leak); ไม่เช็ค `Order.Status` เลย (เขียนได้แม้ order ถูก cancel แล้ว, REQ-3.4); `Apply` throw `ArgumentException`→400 |
| `UpsertItemPolicyAdminHandler` (`UpsertItemPolicyAdminCommand.cs`, admin plane) | `UpsertItemPolicyAdminCommand(OrderItemId,Input,ActorId,IsUnrestrictedAdmin,AccessibleMerchantIds)` → เหมือนกัน | admin `PUT /orders/{orderId}/items/{itemId}/policy` | `admin` + `Keys.MerchantsPoliciesWrite` | item ไม่พบ **หรือ** merchant นอก accessible set → 404 เดียวกัน (no existence leak ข้าม scope ด้วย) |
| `ListPolicyReportHandler` (`ListPolicyReportQuery.cs`, merchant) | `ListPolicyReportQuery` (`PagedQuery`) → `PagedResult<PolicyReportItem>` | `GET /reports/policies` | `merchant-user` + `Keys.PoliciesRead` | 400 (SFS parse), 401, 403 |
| `ListPolicyReportAdminHandler` (`ListPolicyReportAdminQuery.cs`, admin) | + `IsUnrestrictedAdmin`/`AccessibleMerchantIds`/optional `MerchantId` → เหมือนกัน | admin `GET /reports/policies` | `admin` + `Keys.MerchantsPoliciesRead` | scoped admin นอก accessible set → คืนหน้าว่าง ไม่ leak |

Read-only anonymous (ไม่ผ่าน mediator, เรียก `IOrderSummaryReader` ตรงที่ host): `GET
/orders/{token}/summary` — token ไม่รู้จัก → 404, หมดอายุ → 410 Gone (`clock.UtcNow >=
summary.ExpiresAt` เช็คที่ host)

**Consumer** (event-driven, ไม่ผูก HTTP):
- `CheckoutConfirmedConsumer` — consume `Contracts.CheckoutConfirmed`, idempotent lookup ผ่าน
  `CheckoutSessionId` (skip ถ้ามีแล้ว), ไม่งั้น `Order.Create` + enqueue `CustomerOrderNotification` ถ้ามี
  recipient
- `OrderPaidConsumer` — consume `Contracts.PaymentPaid`, load order ด้วย `notification.OrderId` (**ไม่ใช่
  `PaymentSessionId`** — นี่คือ root fix ของ `bugfix-order-paid-link`) ไม่เจอ → return เงียบ (at-least-once
  tolerance, ไม่ throw กัน dispatcher retry ทุกครั้ง); เจอ → `order.MarkPaid(...)` (re-verify
  amount+currency ที่ domain เอง ไม่เชื่อ event เปล่าๆ); transition จริงเท่านั้น → enqueue
  `Contracts.OrderPaid(MerchantId,ProductIds,OccurredAt)` ให้ Products module retire เอกสาร (REQ-7 ของ
  `checkout-chain-document-fields`)
- `CustomerOrderNotificationConsumer` — background, ส่งผ่าน `INotificationSender`, throw ให้
  dispatcher retry/DLQ

Repository/port interface (implement จริงใน `Persistence.MerchantRuntime/Orders`): `IOrderRepository`,
`IItemPolicyRepository`, `IAdminItemPolicyWriter` (+ `AdminItemPolicyLoad` record), `IOrderSummaryReader`,
`IRevealAuditWriter` — validation หลักอยู่ที่ Domain (`Order.Create`, `ItemPolicy.Apply`) handler แค่
orchestrate load/save/outbox

## Infrastructure

Dual-config pattern เหมือน Carts: `Orders.Infrastructure` (migration-owner, ผูก `PolDbContext`) กับ
`Persistence.MerchantRuntime/Orders` (runtime, มี query filter — `HasQueryFilter(x => x.MerchantId ==
context.CurrentMerchant)` + `TenantKeyDescriptor.Require(...)`) — mapping ต้องตรงกันเป๊ะทั้งสองที่

| Table | Entity | Key point |
|---|---|---|
| `shop.Orders` | `Order` | PK `Id` (`ValueGeneratedNever`), alternate key `(Id,MerchantId)`; `Amount` เป็น complex type (`AmountAmount decimal(19,4)` + `AmountCurrency char(3)`); index: `SummaryToken` unique, `CheckoutSessionId` unique **filtered** (`WHERE [CheckoutSessionId] IS NOT NULL` — idempotency backstop), `PaymentSessionId` filtered, `MerchantId` ธรรมดา (`OrderConfiguration.cs:14-72`) |
| `shop.OrderItems` | `Item` | FK composite `(OrderId,MerchantId) → Orders(Id,MerchantId)` cascade delete |
| `shop.OrderItemPolicies` | `ItemPolicy` | unique index `OrderItemId` (1:1), index `MerchantId`; premium เป็น scalar pair (ไม่ complex type ตามที่กล่าวข้างต้น); `NetPremium`/`GrossPremium` computed property `Ignore` |
| `shop.OrderItemPolicyAudits` | `ItemPolicyAudit` | index `OrderItemId`, composite `(MerchantId,OccurredAt)`; append-only |
| `shop.OrderItemRevealAudits` | `RevealAudit` | index `OrderItemId`, composite `(MerchantId,RevealedAt)`; append-only |

Repository ทุกตัวเป็น thin wrapper รอบ `MerchantRuntimeDbContext.Set<T>()` ไม่มี save logic เอง (save ผ่าน
`IUnitOfWork.SaveChangesAsync` ที่ handler เรียก)

**Raw SQL**: `OrderSummaryReader` ใช้ `db.Database.SqlQueryRaw<T>(...)` (parameterized) อ่าน
`shop.Orders`/`shop.OrderItems` ผ่าน token ตรงๆ — bypass query filter เพราะลูกค้า anonymous ไม่มี
merchant binding เลย รันใน fresh DI scope ทุกครั้ง

**SFS**: `PolicyReportSfs` (`Persistence.MerchantRuntime/Orders/Items/PolicyReportSfs.cs`) join
`OrderItems JOIN Orders LEFT JOIN OrderItemPolicies` แบบ **hybrid**: confine-to-merchant เป็น SQL จริง,
ส่วน filter/sort เป็น **in-memory LINQ-to-Objects** (ponytail comment ในโค้ดอธิบายว่า EF Core + SQLite
provider แปล `Where`/`OrderBy` หลัง positional-record `Select` ไม่ได้ — deliberate limitation, revisit
ถ้า row count โต) Filter field allowlist: `insuranceCategory`, `referenceNumberType`,
`premiumRemittanceStatus`, `paymentStatus`, `createdAt` — ไม่มี `merchantId` ใน whitelist (mirror
`ProductSfs.cs`, ฝั่ง admin ใช้ query param `?merchantId=` แยกต่างหาก)

**Escape-hatch admin cross-merchant**: `AdminItemPolicyWriter`/`AdminItemPolicyReader` — ผูก admin
write-authorizer เอง, `IgnoreQueryFilters()` ทุก query, emit `DenialEvent` ทุกครั้งที่ cross floor
(allowlisted ใน `Architecture.Tests.BypassPrimitiveTests`)

## API endpoints (`src/Hosts/Api/Program.cs`)

Tag `"คำสั่งซื้อ"`. Merchant plane:

| Method | Route | Line | Command/Query | Auth | Error |
|---|---|---|---|---|---|
| GET | `/api/v1/orders/{token}/summary` | 861-880 | `IOrderSummaryReader.GetByTokenAsync` (ตรง ไม่ผ่าน mediator) | `AllowAnonymous()` | 404 (token ไม่รู้จัก), 410 Gone (หมดอายุ) |
| POST | `/api/v1/orders/{orderId}/summary/resend` | 882-893 | `ResendOrderSummaryCommand` | `merchant-user` | 401 (+404/409 undeclared ใน OpenAPI) |
| GET | `/api/v1/orders` | 896-906 | `GetOrdersQuery` | `merchant-user` | 401 |
| GET | `/api/v1/orders/{orderId}` | 911-924 | `GetOrderDetailCommand` | `merchant-user` | 401, 404 |
| PUT | `/api/v1/orders/{orderId}/items/{itemId}/policy` | 929-949 | `UpsertItemPolicyCommand` | `merchant-user` + `Keys.PoliciesWrite` | 400, 401, 403, 404 |
| GET | `/api/v1/reports/reconciliation` | 952-962 | `GetReconciliationSummaryQuery` | `merchant-user` | 401 |
| GET | `/api/v1/reports/policies` | 966-985 | `ListPolicyReportQuery` (SFS: page/limit/filters/sort/search) | `merchant-user` + `Keys.PoliciesRead` | 400, 401, 403 |

Admin plane (twin ของ policy endpoint, cross-merchant escape hatch):

| Method | Route | Line | Auth |
|---|---|---|---|
| PUT | `/api/v1/admins/orders/{orderId}/items/{itemId}/policy` | ~1540 | `admin` + `Keys.MerchantsPoliciesWrite` |
| GET | `/api/v1/admins/reports/policies` | ~1566 | `admin` + `Keys.MerchantsPoliciesRead` |

**หมายเหตุ endpoint สำคัญ**:
- `GET /orders/{token}/summary` เป็นเส้นเดียวในโมดูลที่ `AllowAnonymous()` — capability token เองคือ
  หลักฐานสิทธิ์ ไม่มี merchant binding เลย response ไม่มี `MerchantId`/`DateOfBirth` และ `InsuredIdNumber`
  ถูก mask เสมอ
- `GET /orders/{orderId}` (detail) คืน `InsuredIdNumber` **เต็ม** และเขียน `RevealAudit` — ต่างจาก `GET
  /orders` (list) ที่ mask เสมอไม่มี audit
- `PUT .../items/{itemId}/policy` (ทั้ง merchant + admin) **ไม่ gate บน `Order.Status`** — เขียนได้แม้ order
  ถูกยกเลิกไปแล้ว (REQ-3.4, insurance-pivot's state machine ไม่ถูกแตะ)
- ไม่มี `POST /orders` — สร้าง order ได้ทางเดียวคือผ่าน `CheckoutConfirmedConsumer` (ดู Application layer)

## จุดที่ไม่สมมาตร (known gaps)

5 จุดจริงจากโค้ด ไม่ใช่ข้อเสนอแนะ:

1. **ไม่มี cancel/refund flow ใดๆ ในระบบ** — `Order.Cancel()` มีอยู่ใน domain (`Order.cs:167-176`) ครบ
   guard แต่ **0 caller** ใน `Orders.Application` ทั้งหมด (grep ยืนยันแล้ว) — order เข้าสถานะ
   `Cancelled` ไม่ได้เลยจากโค้ดปัจจุบัน
2. **`PaymentSessionId`/`AttachPaymentSession` เป็น legacy field ไม่มี production writer เลย** —
   ทุก caller ที่สร้าง order ส่ง `paymentSessionId: null`, `AttachPaymentSession` (`Order.cs:133-140`) มี
   0 caller จริง fulfillment จริงพึ่ง `PaymentPaid.OrderId` (first-class contract field) เท่านั้น เป็น
   root cause ของ `bugfix-order-paid-link` ที่แก้แล้ว — field นี้ยังอยู่ในโค้ดและทำให้เข้าใจผิดได้ถ้าไม่รู้
   ประวัติ
3. **`CreateOrderCommand`/`CreateOrderHandler` ไม่มี endpoint mapped จริง** — production path สร้าง
   order มีทางเดียวคือ `CheckoutConfirmedConsumer` (event-driven) โค้ด command/handler นี้ถูกเรียกแค่จาก
   test เท่านั้น
4. **`Orders.Domain.OrderPaid` in-aggregate event ไม่เคย dispatch จริง** — `Ignore(DomainEvents)` ทุก
   config เป็นแค่ marker เอกสาร ไม่ใช่ mechanism (เหมือน pattern เดียวกับ Carts) integration event ตัวจริง
   คือ `Contracts.OrderPaid` ที่เขียนผ่าน outbox แยกต่างหากใน `OrderPaidConsumer`
5. **`PolicyReportSfs` filter/sort เป็น in-memory LINQ ไม่ใช่ SQL จริง** — ceiling ชัดเจนถ้า row count โต
   (ponytail comment ในโค้ดระบุ EF Core + SQLite แปล query หลัง positional `Select` ไม่ได้ ไม่ใช่ bug แต่
   เป็น deliberate limitation ที่ยังไม่ revisit)

## Migration history

Migration owner เดียว: `BuildingBlocks.Infrastructure/Persistence/Migrations/` (ผูกกับ `PolDbContext`)
เรียงเวลา เฉพาะที่แตะตาราง Orders:

| Migration | ผล |
|---|---|
| `20260720171458_OrderLinesAndCheckoutSessionLines` | สร้างตาราง `OrderLines`/`CheckoutSessionLines` ครั้งแรก (ยุค insurance-pivot) พร้อม field เก่า `SumInsuredAmount/Currency`, `CoverageDurationDays`, `InsurerName` |
| `20260720175721_RevealAudits` | สร้าง `OrderLineRevealAudits` |
| `20260720180545_GrantInsuranceLineTables` | GRANT ตาราง `OrderLines`/`CheckoutSessionLines`/`OrderLineRevealAudits` ให้ `pol_app` |
| `20260723122929_RenameOrderLinesToOrderItems` | **rename** `OrderLines`→`OrderItems`, `OrderLineRevealAudits`→`OrderItemRevealAudits`, `CheckoutSessionLines`→`CheckoutSessionItems`, คอลัมน์ `OrderLineId`→`OrderItemId` ด้วย `sp_rename` (hand-edited จาก scaffold Drop+Create เพื่อรักษา rows+GRANTs) |
| `20260723150000_SeedPolicyPermissions` | seed permission key `merchants.policies.read/write`, `policies.read/write` |
| `20260723160000_OrderItemPolicies` | สร้างตาราง `OrderItemPolicies`, `OrderItemPolicyAudits` |
| `20260723160500_GrantOrderItemPolicyTables` | GRANT SELECT/INSERT/UPDATE ให้ `pol_app` |
| `20260726151538_OneOpenPaymentSessionPerOrder` | unique filtered index `IX_PaymentSessions_OrderId_Open` (schema `txn` — แตะ Order lifecycle ทางอ้อม, จาก `captive-payment-alignment`) |
| `20260730081227_CheckoutChainDocumentFields` | **migration ล่าสุดที่แตะ `OrderItems` โดยตรง** — drop `CoverageDurationDays`/`InsurerName`/`SumInsuredAmount`/`SumInsuredCurrency`; add `DocumentNo`/`DocumentType`/`EndDate`/`PolicyNumber`/`ProductGroup`/`StartDate` (ทำเหมือนกันกับ `CheckoutSessionItems`) |

`OrderLine` (ชื่อเก่า) เจอเป็น historical note เท่านั้นในเอกสาร/migration ที่เหลือ — โค้ดปัจจุบันไม่มี
identifier เก่าเหลืออยู่แล้ว

## Cross-reference

- ภาพรวม 6-layer เทียบกับ Carts/Products/Checkouts: [`layers-guide.md`](layers-guide.md) §4 "Orders"
- Field-level schema เต็ม (`shop.Orders`/`OrderItems`/`OrderItemPolicies`/`OrderItemPolicyAudits`/
  `OrderItemRevealAudits` + flow diagram F3/F4): [`entity-fields.md`](entity-fields.md)
- ภาพรวมธุรกิจ + feature-status table + โมเดลเป้าหมาย: [`platform-modules.md`](platform-modules.md) §8
  "Order"
- File inventory: [`src-structure.md`](src-structure.md) §4.4
- โมดูลต้นทาง (ดึงราคา/ProductGroup ผ่าน Carts→Checkouts): [`carts.md`](carts.md)
- Spec ต้นกำเนิด: `.ai/specs/insurance-pivot/`, `.ai/specs/bugfix-order-paid-link/`,
  `.ai/specs/policy-reference-record/` (ADR-1, rename REQ-7), `.ai/specs/checkout-chain-document-fields/`
- Spec ที่แตะ Order lifecycle ทางอ้อม (ไม่แก้ `Orders.Domain` เอง): `.ai/specs/captive-payment-alignment/`
  REQ-2.4 (one open payment session per order)

## Source of truth

`src/Modules/Orders/Orders.Domain/{Order.cs,OrderStatus.cs,OrderPaid.cs,Items/*.cs}`,
`src/Modules/Orders/Orders.Application/*.cs`,
`src/Modules/Orders/Orders.Infrastructure/{OrderConfiguration.cs,Items/*.cs}`,
`src/Persistence/Persistence.MerchantRuntime/Orders/**`,
`src/Hosts/Api/Program.cs` (route mapping, `:858-985`, admin twin `:1540,1566`) — ตัวเลข/พฤติกรรมในไฟล์นี้
ต้อง sync กับโค้ด 5 จุดนี้เสมอ
