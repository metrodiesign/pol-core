# pol-core API — Canonical commerce และ transactions (Activity Diagrams)

> Source: `docs/reference/api-endpoints.md` section "Canonical commerce และ transactions" บรรทัด L132-L144 และ source ที่อ้างต่อ § (`src/Api/Api/ControlPlane/CanonicalCommerceEndpoints.cs`, `Orders.Application/OrderWorkflow.cs`, `Platform.Application/Transactions/CheckoutTransactionService.cs`, `TransactionResultReducer.cs`, `Persistence.MerchantRuntime/Idempotency/AdminOperationExecutor.cs`, `Persistence.ControlPlane/Payments/Capabilities/EffectivePaymentCapabilityResolver.cs`)
> Scope: 9 endpoints — Checkout payment methods, Draft Order patch (dual identity/admin), Order child reads, Transaction list/detail/events, Transaction verify + review-notes (admin support writes)
> Generated: 2026-09-14

| § | Diagram | Endpoints |
| --- | --- | --- |
| 6.1 | รายการ Payment methods ของ Checkout | `GET /api/v1/checkout/payment-methods` |
| 6.2 | แก้ไข Draft Order (dual identity/admin) | `PATCH /api/v1/orders/{orderId:guid}` |
| 6.3 | อ่าน Order child resources (items + history) | `GET /api/v1/orders/{orderId:guid}/items` + 1 composed |
| 6.4 | รายการ Transaction ตาม Merchant scope | `GET /api/v1/transactions` |
| 6.5 | อ่าน Transaction + events ตาม parent visibility | `GET /api/v1/transactions/{transactionId:guid}` + 1 composed |
| 6.6 | ตรวจสอบ Transaction กับ PSP (admin verify) | `POST /api/v1/transactions/{transactionId:guid}/verify` |
| 6.7 | เพิ่ม Transaction review note (idempotent) | `POST /api/v1/transactions/{transactionId:guid}/review-notes` |

---

## 6.1 รายการ Payment methods ของ Checkout

Merchant user อ่าน method ที่ enable จริงสำหรับ merchant/user ปัจจุบัน โดย resolve ทีละ canonical method (card, promptpay, installment) ตาม `cfg.PaymentAuthorizationStates.Mode` โดยไม่เรียก PSP หรือ network (source: `src/Api/Api/ControlPlane/CanonicalCommerceEndpoints.cs:231-248`, `Persistence.ControlPlane/Payments/Capabilities/EffectivePaymentCapabilityResolver.cs:36-52,109-135`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy merchant-user + permission payment.view ดู § 0.1"]
    AUTHZ --> BOUND{"actor.HasActor และ actor.UserId ไม่ null?"}
    BOUND -->|no| R404["404 (Results.NotFound ไม่มี body)"]
    BOUND -->|yes| RESOLVE["ListMethodsAsync(merchantId, User, userId)<br/>วน 3 canonical method, ResolveByModeAsync ต่อตัวตาม cfg.PaymentAuthorizationStates"]
    RESOLVE --> R200["200 methods ที่ allowed เท่านั้น, อาจว่าง เช่น []"]
    R200 --> END_S((◉))
    R404 --> END_F((◉))

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class RESOLVE,R200,END_S ok
    class R404,END_F fail
    class BOUND gate
```

---

## 6.2 แก้ไข Draft Order (dual identity/admin)

Identity request ตรวจ ownership ด้วย `AccessEvaluator.CanReadOrder` ก่อนเข้า handler เดียวกับ admin, admin request ห่อด้วย `AdminOperationExecutor` (idempotent apply, ไม่ใช่ maker-checker), ทั้งสองทางจบที่ `PatchDraftOrderHandler` ซึ่ง validate, reprice ผ่าน trusted pricing source แล้ว persist ใน transaction เดียว (source: `CanonicalCommerceEndpoints.cs:35-123`, `Orders.Application/OrderWorkflow.cs:188-201,1123-1252,298-328`, `Persistence.ControlPlane/Orders/OrderOwnerResolver.cs:23-128`, `Persistence.MerchantRuntime/Authorization/CommerceAuthorizationLease.cs:22-50`, `Persistence.MerchantRuntime/Idempotency/AdminOperationExecutor.cs:24-60`, `Api/ConcurrencyEtags.cs:18-26`, `CanonicalCommerceEndpoints.cs:500-509` สำหรับ 412 ครอบทั้งกลุ่ม)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy admin-or-identity-order<br/>RequireOrderIdentityPermission payment.create ตรง identity: order.write ดู § 0.1 / § 0.2<br/>RequireAdminOrIdentityCsrf ดู § 0.3"]
    AUTHZ --> KIND{"IsIdentityRequest(http)?"}

    KIND -->|yes| IDCHK{"requestActor.UserId มีค่า,<br/>order มีจริง (identityOrders.GetAsync),<br/>ResolveAuthorizationAsync + CanReadOrder Allowed?"}
    IDCHK -->|"UserId ว่าง"| R403C["403 ProblemDetails<br/>account_context_missing"]
    IDCHK -->|"ไม่พบ order หรือ CanReadOrder ปฏิเสธ"| R404A["404 ProblemDetails<br/>Order was not found, ไม่ leak ownership"]
    IDCHK -->|ok| VER["VersionEtags.Require(If-Match) ดู § 0.5"]

    KIND -->|no| ADM["ResolveOrderAsync ผ่าน IAdminOrderReader ตาม scope ดู § 0.1"]
    ADM --> ADMFOUND{"พบใน scope?"}
    ADMFOUND -->|no| R404C["404 ProblemDetails<br/>Order was not found"]
    ADMFOUND -->|yes| VER

    VER --> PKIND{"admin path หรือไม่ ไม่ใช่ identity?"}

    PKIND -->|yes| KEY["operationKey = Idempotency-Key header<br/>หรือ order.patch:{orderId}:v{version} เมื่อไม่ส่ง"]
    KEY --> AOE["AdminOperationExecutor.ExecuteAsync<br/>replay lookup ตาม merchantId, adminId, order.patch, key"]
    AOE --> PRIOR{"มี record เดิมของ key นี้?"}
    PRIOR -->|"มี, intent hash ไม่ตรง"| R409REUSE["409 ProblemDetails<br/>idempotency_key_reused"]
    PRIOR -->|"มี, ยังไม่ Succeeded"| R409PROG["409 ProblemDetails<br/>operation_in_progress"]
    PRIOR -->|"มี, Succeeded แล้ว"| REPLAY["คืน response เดิม 200, ไม่ execute ซ้ำ"]
    PRIOR -->|"ไม่มี"| V0{"PatchDraftOrderHandler.Handle บรรทัดแรก:<br/>version parse ได้ แต่ไม่เกิน 0?"}

    PKIND -->|"no, identity"| V0

    V0 -->|yes| R400IFM["400 ProblemDetails<br/>if_match_required"]
    V0 -->|no| VALID["PatchDraftOrderHandler: โหลด order เดิม<br/>ตรวจ businessType/items ไม่ว่าง, parse metadata ของ order และ item,<br/>normalize notification intent NotificationIntentNormalizer"]

    VALID --> VOK{"ผ่านทุกข้อ?"}
    VOK -->|"businessType หรือ items ว่าง"| R400VAL["400 ProblemDetails<br/>validation_failed / items_required"]
    VOK -->|"metadata ผิด VersionedMetadata envelope"| R400MD["400 ProblemDetails<br/>metadata_invalid"]
    VOK -->|"send=true, email/phone รูปแบบผิด หรือไม่มี recipient เลย"| R400NOTIF["400 ProblemDetails<br/>validation_failed / notification_recipient_required"]
    VOK -->|ok| OWNER["IOrderOwnerResolver.ResolveAsync<br/>identity ผูก Agent Sale หรือ scope ownership,<br/>admin ผูกจาก actor.SaleCode หรือ body owner"]

    OWNER --> OWNOK{"owner ผ่าน guard?"}
    OWNOK -->|"context/scope ไม่ผ่าน"| R403OWN["403 ProblemDetails<br/>owner_context_denied / owner_scope_denied / owner_sale_conflict"]
    OWNOK -->|"branch ไม่ตรงกับ sale ที่ระบุ"| R400OWN["400 ProblemDetails<br/>owner_branch_mismatch"]
    OWNOK -->|yes| PRICE["reprice ผ่าน ITrustedOrderPricingSource เมื่อแก้ items/businessType/owner/discount/charge,<br/>ไม่งั้นใช้ persisted pricing เดิม"]

    PRICE --> ADJ{"OrderDiscountAmount/OrderChargeAmount ที่ส่งมา<br/>ตรงกับ trusted pricing?"}
    ADJ -->|no| R409PRICE["409 ProblemDetails<br/>pricing_mismatch"]
    ADJ -->|yes| TXN["ExecuteInTransactionAsync"]

    TXN --> LEASE{"identity request:<br/>CommerceAuthorizationLease.VerifyAsync<br/>revalidate AuthorizationVersion + MerchantAccess ดู § 0.2"}
    LEASE -->|"stale หรือ revoked ระหว่างทาง"| R403STALE["403 ProblemDetails<br/>authorization_stale"]
    LEASE -->|"ผ่าน, หรือ admin path ไม่มี proof"| RELOAD["GetForUpdateAsync ล็อกแถวจริง"]

    RELOAD --> VERCHK{"order.Version = ExpectedVersion<br/>และยังเป็น Draft ไม่ Frozen?"}
    VERCHK -->|"version ไม่ตรง"| R412["412 ProblemDetails<br/>precondition_failed, HandleKnownErrors ครอบทั้งกลุ่ม แทน 409 ดู § 0.5 / § 0.9"]
    VERCHK -->|"ไม่ใช่ Draft หรือ Frozen"| R409DRAFT["409 ProblemDetails<br/>order_not_draft"]
    VERCHK -->|ok| PATCH["order.PatchDraft(...) + SaveChanges<br/>admin path: AdminOperationRecord.Succeed บันทึกผล replay"]

    PATCH --> R200["200 OrderView + ETag ใหม่, VersionEtags.Set"]
    R200 --> END_S((◉))
    REPLAY --> END_S
    R403C --> END_F((◉))
    R404A --> END_F
    R404C --> END_F
    R400IFM --> END_F
    R409REUSE --> END_F
    R409PROG --> END_F
    R400VAL --> END_F
    R400MD --> END_F
    R400NOTIF --> END_F
    R403OWN --> END_F
    R400OWN --> END_F
    R409PRICE --> END_F
    R403STALE --> END_F
    R412 --> END_F
    R409DRAFT --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class PATCH,R200,REPLAY,END_S ok
    class R403C,R404A,R404C,R400IFM,R409REUSE,R409PROG,R400VAL,R400MD,R400NOTIF,R403OWN,R400OWN,R409PRICE,R403STALE,R412,R409DRAFT,END_F fail
    class KIND,IDCHK,ADMFOUND,V0,PKIND,PRIOR,VOK,OWNOK,ADJ,LEASE,VERCHK gate
```

---

## 6.3 อ่าน Order child resources (items + history)

ทั้งสอง endpoint ตรวจ ownership ผ่าน parent Order ก่อนเสมอ (ไม่มี authorization surface แยกของ child) แล้วคืนเฉพาะข้อมูลปลอดภัย, `items` มี pagination ในหน่วยความจำแบบ manual ที่ไม่ผ่าน `SfsQueryParser` แม้ endpoint จะประกาศ `SfsQueryParamsMarker` ไว้ก็ตาม ดู Notes (source: `CanonicalCommerceEndpoints.cs:127-226,397-401,440-444`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy admin-or-identity-order<br/>RequireOrderIdentityPermission payment.view ตรง identity: order.read ดู § 0.1 / § 0.2<br/>safe method ไม่มี CSRF"]
    AUTHZ --> PAGE{"page มากกว่าเท่ากับ 1 และ limit อยู่ 1 ถึง 100?"}
    PAGE -->|no| R400["400 ProblemDetails<br/>invalid_filter, ValidatePage เอง ไม่ผ่าน SfsQueryParser ดู Notes"]
    PAGE -->|yes| KIND{"IsIdentityRequest(http)?"}

    KIND -->|yes| IDU{"requestActor.UserId มีค่า?"}
    IDU -->|no| R403["403 ProblemDetails<br/>account_context_missing"]
    IDU -->|yes| IDGET["repository.GetAsync(orderId)<br/>ResolveAuthorizationAsync + CanReadOrder"]
    IDGET --> IDOK{"พบ order และ Allowed?"}
    IDOK -->|no| R404A["404 ProblemDetails<br/>Order was not found, ไม่ leak ownership"]
    IDOK -->|yes| IDSLICE["Skip/Take ใน memory จาก order.Items ที่โหลดมาแล้ว"]

    KIND -->|no| ADM["ResolveOrderAsync ผ่าน IAdminOrderReader ตาม scope ดู § 0.1"]
    ADM --> ADMFOUND{"พบใน scope?"}
    ADMFOUND -->|no| R404B["404 ProblemDetails<br/>Order was not found"]
    ADMFOUND -->|yes| ADMBIND["actorScope.Begin(order.MerchantId)"]
    ADMBIND --> ADMGET["repository.GetAsync(orderId) อีกครั้งเพื่ออ่าน Items"]
    ADMGET --> ADMFOUND2{"พบ?"}
    ADMFOUND2 -->|no| R404C["404 ProblemDetails<br/>Order was not found"]
    ADMFOUND2 -->|yes| ADMSLICE["Skip/Take ใน memory"]

    IDSLICE --> R200["200 PagedResult CanonicalOrderItemView array"]
    ADMSLICE --> R200
    R200 --> END_S((◉))
    R400 --> END_F((◉))
    R403 --> END_F
    R404A --> END_F
    R404B --> END_F
    R404C --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class IDSLICE,ADMSLICE,R200,END_S ok
    class R400,R403,R404A,R404B,R404C,END_F fail
    class PAGE,KIND,IDU,IDOK,ADMFOUND,ADMFOUND2 gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/orders/{orderId:guid}/history` | ไม่มี page/limit, ไม่มี PAGE gate, ไม่ Skip/Take, คืน `[{status: "created", occurredAt}]` แล้วเพิ่มแถวสถานะปัจจุบัน ตัวพิมพ์เล็ก เมื่อ UpdatedAt ต่างจาก CreatedAt |

---

## 6.4 รายการ Transaction ตาม Merchant scope

List คนละ path จาก detail/events: ไม่มี parent-order resolve, แต่ตัดสิน merchant เป้าหมายจาก query หรือ scope ของ Admin เอง ซึ่งซ่อน edge case จำนวน merchant ที่ assign ให้ Scoped admin (source: `CanonicalCommerceEndpoints.cs:253-278,440-444`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy admin + permission payment.view ดู § 0.1<br/>SfsQueryParamsMarker ประกาศไว้แต่ handler ไม่ใช้ SfsQueryParser ดู Notes"]
    AUTHZ --> PAGE{"page มากกว่าเท่ากับ 1 และ limit อยู่ 1 ถึง 100?"}
    PAGE -->|no| R400["400 ProblemDetails invalid_filter"]
    PAGE -->|yes| MID{"query merchantId ถูกส่งมา?"}

    MID -->|yes| ALLOW1{"scope.Accessible.Allows(merchantId)?"}
    ALLOW1 -->|no| R403["403 ProblemDetails<br/>merchant_scope_forbidden"]
    ALLOW1 -->|yes| LIST

    MID -->|no| UNREST{"scope.Accessible.IsUnrestricted, Super?"}
    UNREST -->|yes| R400B["400 ProblemDetails<br/>invalid_filter, merchantId is required for unrestricted reads"]
    UNREST -->|no| COUNT{"scope.Accessible.Merchants.SingleOrDefault()<br/>ตามจำนวน merchant ที่ assign ให้ Scoped admin"}
    COUNT -->|"0 merchant, selected = Guid.Empty"| R403B["403 ProblemDetails<br/>merchant_scope_forbidden"]
    COUNT -->|"1 merchant"| LIST
    COUNT -->|"2 merchant ขึ้นไป"| R409["409 ProblemDetails ทั่วไป ไม่มี code เฉพาะ<br/>InvalidOperationException จาก SingleOrDefault ดู Notes"]

    LIST["transactions.ListAsync(selected, page, limit, status)"]
    LIST --> R200["200 PagedResult TransactionView array"]
    R200 --> END_S((◉))
    R400 --> END_F((◉))
    R400B --> END_F
    R403 --> END_F
    R403B --> END_F
    R409 --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class LIST,R200,END_S ok
    class R400,R400B,R403,R403B,R409,END_F fail
    class PAGE,MID,ALLOW1,UNREST,COUNT gate
```

---

## 6.5 อ่าน Transaction + events ตาม parent visibility

`ResolveTransactionAsync` เป็น helper กลางของ detail และ events: เมื่อไม่ส่ง `merchantId` และ Admin เป็น Scoped จะสแกนทุก merchant ใน scope ทีละตัวจนเจอ แล้วยังต้อง resolve parent Order ซ้ำเพื่อยืนยัน visibility (source: `CanonicalCommerceEndpoints.cs:280-319,403-438`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy admin + permission payment.view ดู § 0.1"]
    AUTHZ --> MID{"query merchantId ถูกส่งมา?"}

    MID -->|yes| ALLOW{"scope.Accessible.Allows(merchantId)?"}
    ALLOW -->|no| R403["403 ProblemDetails<br/>merchant_scope_forbidden"]
    ALLOW -->|yes| GETONE["transactions.GetByIdAsync(merchantId, transactionId)"]
    GETONE --> FOUND1{"พบ?"}
    FOUND1 -->|no| R404A["404 ProblemDetails<br/>Transaction was not found"]
    FOUND1 -->|yes| PARENT

    MID -->|no| UNREST{"scope.Accessible.IsUnrestricted?"}
    UNREST -->|yes| R400["400 ProblemDetails<br/>invalid_filter, merchantId is required for unrestricted reads"]
    UNREST -->|no| SCAN["วนทุก merchant ใน scope.Accessible.Merchants ทีละตัว<br/>GetByIdAsync จนเจอ, อาจหลาย round-trip ดู Notes"]
    SCAN --> SFOUND{"เจอในบาง merchant?"}
    SFOUND -->|no| R404D["404 ProblemDetails<br/>Transaction was not found"]
    SFOUND -->|yes| PARENT

    PARENT["orders.ResolveAsync(transaction.OrderId, scope=merchant ที่พบ) ตรวจ parent"]
    PARENT --> PFOUND{"parent order resolve ได้?"}
    PFOUND -->|no| R404B["404 ProblemDetails<br/>Order was not found"]
    PFOUND -->|yes| R200["VersionEtags.Set(transaction.Version)<br/>200 TransactionView"]
    R200 --> END_S((◉))
    R403 --> END_F((◉))
    R404A --> END_F
    R400 --> END_F
    R404D --> END_F
    R404B --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class R200,END_S ok
    class R403,R404A,R400,R404D,R404B,END_F fail
    class MID,ALLOW,FOUND1,UNREST,SFOUND,PFOUND gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/transactions/{transactionId:guid}/events` | resolve เหมือนกันทุกทาง, ต่อจากนั้นเรียก `ListEventsAsync(merchantId, transactionId)` แทน, คืน array ของ `CanonicalTransactionEventView` (SafeDetails สรุปแล้ว ไม่ใช่ raw webhook payload), ไม่มี ETag |

---

## 6.6 ตรวจสอบ Transaction กับ PSP (admin verify)

Endpoint นี้เป็น explicit support inquiry ไม่สร้าง charge ใหม่: เรียก PSP ผ่าน `CheckoutTransactionService.VerifyAsync` ซึ่งทุกความล้มเหลวของ PSP หรือ vault แปลงเป็น event `PendingConfirmation` แล้วตอบ 200 เสมอ มีเพียง connection ที่ pin ไว้ไม่ตรงเท่านั้นที่เป็น 409, แต่ผลตรวจที่เป็น Failed ครั้งแรกยังเขียนกลับ Order เป็น Unpaid ได้จริงเมื่อ order ยังไม่ Paid และไม่มี transaction อื่นที่ potential (source: `CanonicalCommerceEndpoints.cs:321-350`, `Platform.Application/Transactions/CheckoutTransactionService.cs:188-247,535-577`, `TransactionResultReducer.cs:22-121`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy admin (Bearer) + permission payment.view<br/>ดู § 0.1"]
    AUTHZ --> RESOLVE["ResolveTransactionAsync ดู § 6.5"]
    RESOLVE --> RFOUND{"resolve สำเร็จ?"}
    RFOUND -->|no| R40X["403 / 404 / 400 ตามเงื่อนไข § 6.5"]
    RFOUND -->|yes| BIND["actorScope.Begin(transaction.MerchantId)"]
    BIND --> VER["VersionEtags.Require(If-Match) ดู § 0.5"]
    VER --> VEROK{"transaction.Version = expected?"}
    VEROK -->|no| R412["412 ProblemDetails<br/>precondition_failed, HandleKnownErrors ครอบทั้งกลุ่ม"]
    VEROK -->|yes| IDEM["IdempotencyKeys.Require(Idempotency-Key) ดู § 0.5"]
    IDEM --> CALL["CheckoutTransactionService.VerifyAsync(merchantId, transactionId, admin-verify, key)"]

    CALL --> SUCC{"transaction.Status = Succeeded อยู่แล้ว?"}
    SUCC -->|yes| DUPE["RecordDuplicateEvidenceAsync: เขียน event duplicate<br/>เฉพาะเมื่อ key นี้ยังไม่เคยเห็น"]
    DUPE --> REFRESH
    SUCC -->|no| HASREF{"transaction.ProviderReference มีค่า?"}
    HASREF -->|no| REFRESH
    HASREF -->|yes| CONN["โหลด pinned PspConnection ตาม ProviderAccountId"]
    CONN --> CONNOK{"connection มีจริงและ Psp/MerchantId ตรงกับที่ pin ไว้?"}
    CONNOK -->|no| R409EV["409 ProblemDetails<br/>payment_capability_unavailable / payment_evidence_mismatch"]
    CONNOK -->|yes| VAULT["ReadVersionForServerAsync(secret)"]
    VAULT --> VAULTOK{"อ่าน secret สำเร็จ?"}
    VAULTOK -->|no| PEND1["MarkPendingAsync credential_unavailable<br/>+ FlagNeedsReview, เขียน event + SaveChanges"]
    PEND1 --> REFRESH
    PEND1 -.async.-> INQ["TransactionInquiryWorker background, § 0.8<br/>poll ทุก 5s: ListDueAsync แล้ว ResumeDueAsync แล้ว VerifyAsync source=inquiry<br/>สำหรับ transaction ที่อยู่ PendingConfirmation และถึงกำหนด NextInquiryAt"]
    VAULTOK -->|yes| FETCH["adapters.For(provider).FetchChargeAsync, PSP inquiry"]

    FETCH -->|timeout| PEND2["MarkPendingAsync provider_timeout<br/>เขียน event + SaveChanges"]
    FETCH -->|"exception อื่น"| PEND3["MarkPendingAsync provider_inquiry_ambiguous<br/>เขียน event + SaveChanges"]
    FETCH -->|ok| EVCHK{"key นี้เคยมี event บันทึกแล้ว, admin-verify ซ้ำ?"}
    EVCHK -->|yes| REFRESH
    EVCHK -->|no| REDUCE["TransactionResultReducer.Apply<br/>Paid+amount ไม่ตรง เป็น PendingConfirmation evidence_mismatch,<br/>Paid เป็น Succeeded, Failed เป็น Failed, อื่น เป็น PendingConfirmation"]

    PEND2 --> REFRESH
    PEND2 -.async.-> INQ
    PEND3 --> REFRESH
    PEND3 -.async.-> INQ
    REDUCE --> WRITEEVT["AddEvent(reduction) + SaveChanges"]
    WRITEEVT -.async.-> BG["outbox TransactionSucceeded เมื่อ EmitNormalSuccess เท่านั้น<br/>Paid จริง, order เปลี่ยนเป็น Paid, ไม่ needsReview ดู § 0.8"]
    WRITEEVT -.async.-> INQ
    WRITEEVT --> FAILCHK{"Failed ครั้งแรก (TransactionBecameFailed),<br/>order ยังไม่ Paid และไม่มี transaction อื่นที่ potential?"}
    FAILCHK -->|yes| REVERT["order.ReturnToUnpaidIfNoPotentialTransaction(now)<br/>sync ร่วม SaveChanges เดียวกับ WRITEEVT"]
    FAILCHK -->|no| REFRESH
    REVERT --> REFRESH

    REFRESH["GetByIdAsync ใหม่ + VersionEtags.Set"]
    REFRESH --> R200["200 TransactionView, สถานะล่าสุดแม้ PSP call ล้มเหลว"]
    R200 --> END_S((◉))
    R40X --> END_F((◉))
    R412 --> END_F
    R409EV --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef ext fill:#4a3a1f,stroke:#d29922,color:#fff
    class WRITEEVT,REVERT,R200,END_S ok
    class R40X,R412,R409EV,END_F fail
    class RFOUND,VEROK,SUCC,HASREF,CONNOK,VAULTOK,EVCHK,FAILCHK gate
    class CONN,VAULT,FETCH ext
```

---

## 6.7 เพิ่ม Transaction review note (idempotent)

Append-only support event: ตรวจ version และ note ก่อนเช็ค idempotency replay ผ่าน `EventExistsAsync`, ไม่แก้ financial field ใด ๆ และไม่เก็บ raw provider payload (source: `CanonicalCommerceEndpoints.cs:352-394`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy admin (Bearer) + permission payment.view<br/>ดู § 0.1"]
    AUTHZ --> RESOLVE["ResolveTransactionAsync ดู § 6.5"]
    RESOLVE --> RFOUND{"resolve สำเร็จ?"}
    RFOUND -->|no| R40X["403 / 404 / 400 ตามเงื่อนไข § 6.5"]
    RFOUND -->|yes| BIND["actorScope.Begin(transaction.MerchantId)"]
    BIND --> VER["VersionEtags.Require(If-Match) ดู § 0.5"]
    VER --> VEROK{"transaction.Version = expected?"}
    VEROK -->|no| R412["412 ProblemDetails<br/>precondition_failed, HandleKnownErrors ครอบทั้งกลุ่ม"]
    VEROK -->|yes| NOTE{"body.Note ไม่ว่างและยาวไม่เกิน 2000 ตัวอักษร?"}
    NOTE -->|no| R400["400 ProblemDetails validation_failed"]
    NOTE -->|yes| IDEM["IdempotencyKeys.Require(Idempotency-Key) ดู § 0.5"]
    IDEM --> REPLAY{"EventExistsAsync merchantId, id, admin-review, key?"}
    REPLAY -->|yes| R200REPLAY["200 CanonicalTransactionReviewView เดิม, ไม่เขียนซ้ำ"]
    REPLAY -->|no| ADD["AddEvent admin-review, key, note + SaveChanges<br/>อ่าน transaction ใหม่"]
    ADD --> R200["200 CanonicalTransactionReviewView + ETag ใหม่"]
    R200 --> END_S((◉))
    R200REPLAY --> END_S
    R40X --> END_F((◉))
    R412 --> END_F
    R400 --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class ADD,R200,R200REPLAY,END_S ok
    class R40X,R412,R400,END_F fail
    class RFOUND,VEROK,NOTE,REPLAY gate
```

---

## Deviations

ไม่พบ deviation ระหว่างเอกสารกับ source: ทุกแถวของ theme นี้ตรงกับ policy, permission และ CSRF filter จริงใน `CanonicalCommerceEndpoints.cs`

## Notes

- PATCH ของ identity path ตรวจ ownership ด้วย `AccessEvaluator.CanReadOrder` ตัวเดียวกับที่ใช้ gate การอ่าน (history/items) ไม่มี `CanWriteOrder` แยก, สิทธิ์เขียนจริงมาจาก `RequireOrderIdentityPermission(..., "order.write")` ที่ § 0.2 คุมไว้ก่อนแล้ว
- PATCH ของ admin path ใช้ `AdminOperationExecutor` เป็น idempotent-apply pattern เฉพาะของ theme นี้ ไม่ใช่ marker pattern ของ § 0.5, ไม่มี `IdempotencyMutationMarker` บน endpoint, `Idempotency-Key` เป็น optional พร้อม fallback key `order.patch:{orderId}:v{version}`
- PATCH ของ admin path จริง ๆ ตรวจ metadata (`SerializePatchIntent`) ก่อนเข้า AdminOperationExecutor เลย ไม่ใช่ทีหลังแบบที่ผังย่อรวมไว้ที่ VALID, ผลลัพธ์ 400 metadata_invalid เหมือนกันแต่ยังไม่มี operation record ถูกสร้าง
- V0 (`if_match_required`) คือบรรทัดแรกจริงของ `PatchDraftOrderHandler.Handle` ซึ่งเป็น delegate `operation(ct)` ที่ `AdminOperationExecutor.ExecuteAsync` เรียกเท่านั้น: admin path จึงเช็ค V0 ได้ก็ต่อเมื่อ PRIOR ไม่พบ record เดิม (มี record แล้วจะ short-circuit คืน replay/409 โดยไม่เรียก Handle เลย), identity path ไม่มี AOE คั่นจึงเช็ค V0 ทันทีหลัง VER (source: `Orders.Application/OrderWorkflow.cs:1127-1128`, `Persistence.MerchantRuntime/Idempotency/AdminOperationExecutor.cs:32-56`)
- Identity path ของ PATCH ไม่มี replay record เลย retry บน identity path จึง execute ซ้ำเสมอ ต่างจาก admin path ที่ replay จาก record เดิมได้
- `GET /transactions` และ `GET /orders/{orderId}/items` ประกาศ marker ของ § 0.6 ไว้ แต่ handler ไม่เคยเรียก SfsQueryParser จริง อ่านแค่ page/limit เป็น int ธรรมดา
- ทั้งสอง endpoint clamp ด้วย ValidatePage เอง ตอบ 400 invalid_filter เมื่อผิดช่วง ต่างจาก § 0.6 ที่ clamp เงียบไม่ตอบ 400
- filters, sort, search ที่ client ส่งมาจะถูกเพิกเฉยเสมอสำหรับสองแถวนี้ ไม่ควรอ้าง § 0.6 กับมัน
- `GET /transactions` ที่ Scoped admin มี merchant ที่ assign ตั้งแต่ 2 merchant ขึ้นไปและไม่ส่ง `merchantId`: `scope.Accessible.Merchants.SingleOrDefault()` โยน `InvalidOperationException` กลายเป็น 409 ทั่วไปไม่มี code เฉพาะ ต่างจาก 0 merchant ที่ตอบ 403 `merchant_scope_forbidden` อย่างชัดเจน
- `ResolveTransactionAsync` เมื่อไม่ส่ง `merchantId` และ Admin เป็น Scoped จะวนเรียก `GetByIdAsync` ทีละ merchant ใน scope จนเจอ ต้นทุนแปรผันตามจำนวน merchant ที่ Admin คนนั้นดูแล
- `GET /transactions/{transactionId:guid}` มี defensive check `merchantId != transaction.MerchantId` หลัง resolve ที่คืน `Results.NotFound()` เปล่า แต่ไม่ reachable จริงในโค้ดปัจจุบัน เพราะ `GetByIdAsync` กรองด้วย `merchantId` เดียวกันไปแล้วตั้งแต่ resolve
- ธีมนี้มี 404 สองรูปแบบ: `Results.NotFound()` เปล่า (checkout/payment-methods เมื่อไม่มี actor, และ defensive check ข้างบน) กับ `NotFoundException` ที่กลายเป็น ProblemDetails ตาม § 0.9 สำหรับกรณี order/transaction ไม่พบจริง
- `POST .../verify`: PSP หรือ vault ล้มเหลว (`credential_unavailable`, `provider_timeout`, `provider_inquiry_ambiguous`) ไม่เคยกลายเป็น HTTP error, endpoint ยังตอบ 200 ด้วยสถานะ `PendingConfirmation` ล่าสุดเสมอ, มีเพียง connection ที่ pin ไว้ไม่ตรงเท่านั้นที่เป็น 409
- `POST .../verify` ที่ผลลัพธ์เป็น Failed ครั้งแรก (`TransactionBecameFailed`) จะเรียก `order.ReturnToUnpaidIfNoPotentialTransaction(now)` เมื่อ order ยังไม่ Paid และไม่มี transaction อื่นที่ potential อยู่ เป็นการเขียน sync ร่วม `SaveChanges` เดียวกับ `AddEvent` ไม่ใช่ async แยก (source: `CheckoutTransactionService.cs:564-568`, `TransactionResultReducer.cs:90-102`)
- `POST .../verify` ที่ผลเป็น `PendingConfirmation` (PEND1/PEND2/PEND3 จาก `MarkPendingAsync`, หรือ REDUCE เมื่อผลเป็น evidence_mismatch/อื่น) ไม่ใช่จุดจบ synchronous: `MarkPendingConfirmation` ตั้ง `NextInquiryAt` ให้ `TransactionInquiryWorker` (background, poll ทุก 5s ดู § 0.8) ดึงไปเรียก `ResumeDueAsync` แล้ว `VerifyAsync(source="inquiry")` ซ้ำเองได้โดยไม่มี admin action เพิ่ม อาจเปลี่ยนเป็น `Succeeded` และยิง outbox `TransactionSucceeded` เอง (source: `CheckoutTransactionService.cs:294-324,353-364,382-384`, `TransactionInquiryWorker.cs:44-69`)
- PATCH ของ identity path ยัง revalidate `AuthorizationVersion` ซ้ำอีกครั้งกลาง transaction ผ่าน `CommerceAuthorizationLease` (row lock บน `acct.Accounts`) เพื่อกัน permission revoke ที่มาถึงระหว่างผ่าน § 0.2 กับตอน persist จริง, admin path ไม่มีขั้นนี้เพราะไม่มี `CommerceAuthorizationProof`
- `EffectivePaymentCapabilityResolver.ListMethodsAsync` ไม่เคยตอบ error code เมื่อไม่มี method ใด allow เลย จะตอบ 200 พร้อม array ว่างเสมอ, เกณฑ์ allow ต่อ method ขึ้นกับ `cfg.PaymentAuthorizationStates.Mode` ที่ตั้งค่าใน DB จึงอยู่นอก frame ของ diagram

**Render**: GitHub / Obsidian / VS Code Mermaid
