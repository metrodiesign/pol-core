# pol-core API — Canonical commerce และ transactions (Sequence Diagrams)

> Source: `docs/reference/api-endpoints.md` section "Canonical commerce และ transactions" บรรทัด L132-L144 และ source ที่อ้างต่อ § เดียวกับ `06-canonical-commerce-transactions.activities.md` (หมายเลข § ตรงกัน)
> Scope: 9 endpoints เดียวกับไฟล์ activities แสดงลำดับข้าม actor / API / Application handler / DB / PSP
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

```mermaid
sequenceDiagram
    autonumber
    actor U as Merchant user
    participant SPA as Merchant Console หรือ Checkout UI
    participant API as API<br/>policy merchant-user
    participant RES as EffectivePaymentCapabilityResolver
    participant DB as DB, cfg.PaymentMethods ฯลฯ

    Note over U,API: Phase A — gate ดู § 0.1
    U->>SPA: เปิดหน้า checkout
    SPA->>API: GET /api/v1/checkout/payment-methods
    alt ไม่มี actor bound หรือ UserId ว่าง
        API-->>SPA: 404 ไม่มี body
    else มี actor
        Note over API,DB: Phase B — resolve capability ต่อ method
        API->>RES: ListMethodsAsync(merchantId, User, userId)
        loop card, promptpay, installment
            RES->>DB: อ่าน mode + merchant/method/user policy
            DB-->>RES: แถวการตั้งค่า
        end
        RES-->>API: methods ที่ allowed เท่านั้น อาจว่าง
        API-->>SPA: 200 methods array
    end
```

---

## 6.2 แก้ไข Draft Order (dual identity/admin)

```mermaid
sequenceDiagram
    autonumber
    actor U as Employee/Agent/SYSTEM หรือ Admin
    participant C as Web app หรือ Admin Console
    participant API as API pipeline<br/>policy admin-or-identity-order
    participant APP as PatchDraftOrderHandler<br/>+ IOrderOwnerResolver
    participant LEASE as CommerceAuthorizationLease<br/>identity path เท่านั้น
    participant AOE as AdminOperationExecutor<br/>admin path เท่านั้น
    participant DB as DB, Orders + AdminOperationRecords

    Note over C,API: Phase A — gate ดู § 0.1 / § 0.2 / § 0.3
    C->>API: PATCH /api/v1/orders/{orderId}, If-Match + Idempotency-Key เฉพาะ admin

    alt identity request
        Note over API,DB: Phase B1 — ownership บน order เดิม
        API->>DB: GetAsync(orderId) + ResolveAuthorizationAsync
        DB-->>API: order + authorization snapshot
        alt UserId ว่าง
            API-->>C: 403 account_context_missing
        else CanReadOrder ปฏิเสธ หรือไม่พบ order
            API-->>C: 404 Order was not found, ไม่ leak ownership
        else ผ่าน
            API->>APP: Handle PatchDraftOrderCommand, Authorization = proof
        end
    else admin request
        Note over API,DB: Phase B2 — scope + idempotent wrapper
        API->>DB: ResolveOrderAsync ตาม scope ดู § 0.1
        DB-->>API: order หรือ null
        alt ไม่พบใน scope
            API-->>C: 404 Order was not found
        else พบ
            API->>AOE: ExecuteAsync order.patch, key หรือ fallback key
            AOE->>DB: replay lookup ตาม merchantId, adminId, order.patch, key
            DB-->>AOE: record เดิม หรือไม่มี
            alt มี record, hash ไม่ตรง
                AOE-->>C: 409 idempotency_key_reused
            else มี record, ยังไม่ Succeeded
                AOE-->>C: 409 operation_in_progress
            else มี record, Succeeded แล้ว
                AOE-->>C: 200 response เดิม, replay
            else ไม่มี record
                AOE->>APP: Handle PatchDraftOrderCommand, Authorization = null
            end
        end
    end

    Note over APP,DB: Phase C — validate + reprice + persist ผ่าน handler เดียวกันทั้งสอง path
    APP->>APP: version <= 0 เป็น 400, businessType/items ว่างเป็น 400, metadata ผิด envelope เป็น 400, notification email/phone ผิดหรือไม่มี recipient เป็น 400
    APP->>DB: IOrderOwnerResolver.ResolveAsync แล้ว reprice ผ่าน trusted pricing
    Note right of APP: owner ผิดเป็น 403 หรือ 400, discount/charge ไม่ตรง trusted pricing เป็น 409 pricing_mismatch
    APP->>DB: ExecuteInTransactionAsync
    opt identity request
        APP->>LEASE: VerifyAsync proof ดู § 0.2
        LEASE->>DB: UPDLOCK HOLDLOCK เทียบ AuthorizationVersion
        DB-->>LEASE: จำนวนแถวที่ affect
        alt affected = 0
            LEASE-->>C: 403 authorization_stale
        end
    end
    APP->>DB: GetForUpdateAsync order เทียบ Version
    alt version ไม่ตรง
        APP-->>C: 412 precondition_failed, HandleKnownErrors ครอบทั้งกลุ่ม แทน § 0.5
    else ไม่ใช่ Draft หรือ Frozen
        APP-->>C: 409 order_not_draft
    else ผ่าน
        APP->>DB: PatchDraft + SaveChanges, admin path บันทึก record.Succeed
        APP-->>C: 200 OrderView + ETag ใหม่
    end
```

---

## 6.3 อ่าน Order child resources (items + history)

```mermaid
sequenceDiagram
    autonumber
    actor U as Employee/Agent/SYSTEM หรือ Admin
    participant C as Web app หรือ Admin Console
    participant API as API pipeline<br/>policy admin-or-identity-order
    participant DB as DB, Orders

    Note over C,API: Phase A — gate ดู § 0.1 / § 0.2, safe method ไม่มี CSRF
    C->>API: GET /api/v1/orders/{orderId}/items, page + limit
    API->>API: ValidatePage page, limit ดู Notes ของ activities.md
    alt page/limit นอกช่วง
        API-->>C: 400 invalid_filter
    else ผ่าน
        Note over API,DB: Phase B — resolve order ตาม identity/admin แล้วอ่าน items
        alt identity request
            API->>DB: repository.GetAsync orderId + ResolveAuthorizationAsync
            DB-->>API: order + authorization
            alt UserId ว่าง
                API-->>C: 403 account_context_missing
            else CanReadOrder ปฏิเสธ หรือไม่พบ order
                API-->>C: 404 Order was not found, ไม่ leak ownership
            else ผ่าน
                API->>API: Skip/Take ใน memory
                API-->>C: 200 PagedResult items array
            end
        else admin request
            API->>DB: ResolveOrderAsync ตาม scope + repository.GetAsync orderId
            DB-->>API: order หรือ null
            alt ไม่พบ
                API-->>C: 404 Order was not found
            else พบ
                API->>API: Skip/Take ใน memory
                API-->>C: 200 PagedResult items array
            end
        end
    end
```

`GET /api/v1/orders/{orderId:guid}/history` ใช้ gate และ branch identity/admin เดียวกันทุกประการ ต่างเพียงไม่มี page/limit และคืน `[{status, occurredAt}]` แทน items — ไม่วาดซ้ำ ดูตาราง flow ประกอบใน activities.md

---

## 6.4 รายการ Transaction ตาม Merchant scope

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant C as Admin Console
    participant API as API<br/>policy admin
    participant DB as DB, Transactions

    Note over C,API: Phase A — gate ดู § 0.1
    C->>API: GET /api/v1/transactions, page + limit + status + merchantId
    API->>API: ValidatePage page, limit
    alt page/limit นอกช่วง
        API-->>C: 400 invalid_filter
    else ผ่าน
        alt merchantId ถูกส่งมา
            API->>API: scope.Accessible.Allows merchantId
            alt ไม่ผ่าน
                API-->>C: 403 merchant_scope_forbidden
            else ผ่าน
                API->>DB: ListAsync merchantId, page, limit, status
                DB-->>API: PagedResult
                API-->>C: 200 PagedResult TransactionView array
            end
        else merchantId ไม่ได้ส่ง
            alt scope.Accessible.IsUnrestricted, Super
                API-->>C: 400 invalid_filter, merchantId is required
            else Scoped admin ตามจำนวน merchant ที่ assign
                alt 0 merchant
                    API-->>C: 403 merchant_scope_forbidden
                else 1 merchant
                    API->>DB: ListAsync merchant เดียวนั้น, page, limit, status
                    DB-->>API: PagedResult
                    API-->>C: 200 PagedResult TransactionView array
                else 2 merchant ขึ้นไป
                    API-->>C: 409 InvalidOperationException ไม่มี code เฉพาะ
                end
            end
        end
    end
```

---

## 6.5 อ่าน Transaction + events ตาม parent visibility

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant C as Admin Console
    participant API as API<br/>policy admin
    participant DB as DB, Transactions + Orders

    Note over C,API: Phase A — gate ดู § 0.1
    C->>API: GET /api/v1/transactions/{transactionId}, merchantId
    alt merchantId ถูกส่งมา
        API->>API: scope.Accessible.Allows merchantId
        alt ไม่ผ่าน
            API-->>C: 403 merchant_scope_forbidden
        else ผ่าน
            API->>DB: GetByIdAsync merchantId, transactionId
            DB-->>API: transaction หรือ null
            alt ไม่พบ
                API-->>C: 404 Transaction was not found
            else พบ
                API->>DB: orders.ResolveAsync transaction.OrderId, scope={merchantId}
                DB-->>API: order หรือ null
                alt parent resolve ไม่ได้
                    API-->>C: 404 Order was not found
                else parent ok
                    API-->>C: 200 TransactionView + ETag vN
                end
            end
        end
    else merchantId ไม่ได้ส่ง
        alt scope.Accessible.IsUnrestricted
            API-->>C: 400 invalid_filter, merchantId is required
        else Scoped admin
            loop ทุก merchant ใน scope.Accessible.Merchants จนเจอ
                API->>DB: GetByIdAsync merchant, transactionId
                DB-->>API: transaction หรือ null
            end
            alt ไม่เจอเลยทุก merchant
                API-->>C: 404 Transaction was not found
            else เจอแต่ parent resolve ไม่ได้
                API-->>C: 404 Order was not found
            else เจอและ parent ok
                API-->>C: 200 TransactionView + ETag vN
            end
        end
    end
```

`GET .../events` ใช้ resolve เดียวกันทุกประการ แล้วเรียก `ListEventsAsync` แทนการคืน view เดียว และไม่มี ETag — ดูตาราง flow ประกอบใน activities.md

---

## 6.6 ตรวจสอบ Transaction กับ PSP (admin verify)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant C as Admin Console
    participant API as API<br/>policy admin
    participant SVC as CheckoutTransactionService
    participant VAULT as Vault secret store
    participant PSP as PSP adapter, external
    participant DB as DB, Transactions
    participant INQ as TransactionInquiryWorker<br/>(background, § 0.8)

    Note over C,API: Phase A — gate + resolve + version ดู § 0.1 / § 0.3 / § 6.5
    C->>API: POST /api/v1/transactions/{id}/verify, If-Match + Idempotency-Key
    API->>API: ResolveTransactionAsync ดู § 6.5
    alt resolve ล้มเหลว
        API-->>C: 403 หรือ 404 หรือ 400 ตาม § 6.5
    else resolve สำเร็จ
        API->>API: transaction.Version = expected
        alt ไม่ตรง
            API-->>C: 412 precondition_failed
        else ตรง
            Note over API,SVC: Phase B — verify กับ PSP ไม่สร้าง charge ใหม่
            API->>SVC: VerifyAsync merchantId, transactionId, admin-verify, key
            alt status Succeeded อยู่แล้ว
                SVC->>DB: เขียน event duplicate เฉพาะ key ที่ยังไม่เคยเห็น
            else ยังไม่มี ProviderReference
                SVC-->>API: คืนสถานะปัจจุบัน ไม่เรียก PSP
            else มี ProviderReference
                SVC->>DB: โหลด pinned PspConnection
                alt connection ไม่ตรงหรือไม่มี
                    SVC-->>C: 409 payment_capability_unavailable หรือ payment_evidence_mismatch
                else ตรง
                    SVC->>VAULT: ReadVersionForServerAsync secret
                    alt อ่านล้ม
                        SVC->>DB: MarkPendingAsync credential_unavailable + FlagNeedsReview
                    else อ่านสำเร็จ
                        SVC->>PSP: FetchChargeAsync inquiry
                        alt timeout
                            SVC->>DB: MarkPendingAsync provider_timeout
                        else exception อื่น
                            SVC->>DB: MarkPendingAsync provider_inquiry_ambiguous
                        else ตอบกลับ
                            PSP-->>SVC: PspChargeConfirmation
                            SVC->>DB: EventExistsAsync source, key
                            alt key นี้เคยมี event แล้ว, admin-verify ซ้ำ
                                DB-->>SVC: พบ, ไม่เขียนซ้ำ
                            else ยังไม่เคยมี
                                SVC->>SVC: TransactionResultReducer.Apply
                                SVC->>DB: AddEvent + Enqueue TransactionSucceeded เมื่อ EmitNormalSuccess ดู § 0.8 + SaveChanges
                                opt Failed ครั้งแรก (TransactionBecameFailed), order ยังไม่ Paid, ไม่มี transaction อื่นที่ potential
                                    SVC->>DB: order.ReturnToUnpaidIfNoPotentialTransaction, sync ร่วม SaveChanges เดียวกัน
                                end
                            end
                        end
                    end
                end
            end
            Note over SVC,INQ: PendingConfirmation ไม่ใช่จุดจบ synchronous<br/>INQ poll ทุก 5s (ListDueAsync) แล้วเรียก ResumeDueAsync -> VerifyAsync source=inquiry ซ้ำเองตาม NextInquiryAt ได้ ดู § 0.8<br/>อาจเปลี่ยนเป็น Succeeded + outbox TransactionSucceeded โดยไม่มี admin action เพิ่ม
            SVC-->>API: CheckoutConfirmResult ล่าสุด
            API->>DB: GetByIdAsync ใหม่ + VersionEtags.Set
            API-->>C: 200 TransactionView, สถานะล่าสุดแม้ PSP call ล้มเหลว
        end
    end
```

---

## 6.7 เพิ่ม Transaction review note (idempotent)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant C as Admin Console
    participant API as API<br/>policy admin
    participant DB as DB, Transactions

    Note over C,API: Phase A — gate + resolve + version ดู § 0.1 / § 0.3 / § 6.5
    C->>API: POST /api/v1/transactions/{id}/review-notes, If-Match + Idempotency-Key
    API->>API: ResolveTransactionAsync ดู § 6.5
    alt resolve ล้มเหลว
        API-->>C: 403 หรือ 404 หรือ 400 ตาม § 6.5
    else resolve สำเร็จ
        API->>API: transaction.Version = expected
        alt ไม่ตรง
            API-->>C: 412 precondition_failed
        else ตรง
            API->>API: body.Note ไม่ว่างและยาวไม่เกิน 2000
            alt ไม่ผ่าน
                API-->>C: 400 validation_failed
            else ผ่าน
                API->>DB: EventExistsAsync admin-review, key
                alt เคยมี event นี้แล้ว
                    DB-->>API: พบ
                    API-->>C: 200 view เดิม ไม่เขียนซ้ำ
                else ยังไม่เคยมี
                    DB-->>API: ไม่พบ
                    API->>DB: AddEvent admin-review, key, note + SaveChanges
                    API-->>C: 200 view ใหม่ + ETag ใหม่
                end
            end
        end
    end
```

## Notes

- Deviations ของ theme นี้อยู่ใน `06-canonical-commerce-transactions.activities.md` ทั้งหมด ไฟล์นี้ไม่ซ้ำ
- § 0.8 (background dispatch) เป็น cross-cutting mechanic กลาง ไม่วาด drain loop ซ้ำใน § 6.6 ดู `00-cross-cutting.sequences.md`
- `POST .../verify` ที่ผลเป็น `PendingConfirmation` ไม่ใช่จุดจบ: `TransactionInquiryWorker` (ดู § 0.8) ดึงไปเรียก `VerifyAsync(source="inquiry")` ซ้ำเองในพื้นหลังตาม `NextInquiryAt` รายละเอียดครบใน `06-canonical-commerce-transactions.activities.md`

**Render**: GitHub / Obsidian / VS Code Mermaid
