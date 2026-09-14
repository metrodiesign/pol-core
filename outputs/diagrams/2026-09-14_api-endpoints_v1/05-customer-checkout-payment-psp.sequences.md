# pol-core API — Customer checkout, payment sessions และ PSP callbacks (Sequence Diagrams)

> Source: `docs/reference/api-endpoints.md` section "Commerce, checkout และ orders" (L90-108), "Payment compatibility" (L116-121), "Payment webhooks" (L127-128) และ source ที่อ้างต่อ § (`src/Api/Api/Program.cs:784-1948`, `Application/Modules/Checkouts.Application/*`, `Application/Modules/Platform.Application/Transactions/CheckoutTransactionService.cs`, `Application/Modules/Payments.Application/*`, `Application/Modules/Orders.Application/OrderWorkflow.cs`)
> Scope: 10 § เดียวกับ `05-customer-checkout-payment-psp.activities.md` (หมายเลข § ตรงกัน) แสดงลำดับข้าม actor / handler / DB / PSP / worker
> Generated: 2026-09-14

| § | Diagram | Endpoints |
| --- | --- | --- |
| 5.1 | Checkout capability exchange | `POST /api/v1/checkout/access` |
| 5.2 | Checkout confirm / verify — เริ่มหรือ re-check การชำระผ่าน PSP | `POST /api/v1/checkout/confirm` (กลาง), `POST /api/v1/checkout/verify` (ประกอบ) |
| 5.3 | Checkout reads (status / summary) | `GET /api/v1/checkout/status` (กลาง), `GET /api/v1/checkout/summary` (ประกอบ) |
| 5.4 | Order-link customer payment (pay / payment-status / summary) | `POST /api/v1/orders/{token}/pay` (กลาง), `POST /api/v1/orders/{token}/payment-status` (ประกอบ), `GET /api/v1/orders/{token}/summary` (ประกอบ) |
| 5.5 | Payment link revoke | `POST /api/v1/payment-links/{linkId:guid}/revoke` |
| 5.6 | PSP browser return (payment-returns) | `GET /api/v1/payment-returns/{providerCode}` (กลาง), `POST /api/v1/payment-returns/{providerCode}` (ประกอบ) |
| 5.7 | Payment session list | `GET /api/v1/payments/sessions` |
| 5.8 | Payment session create (dual-console) | `POST /api/v1/payments/sessions` |
| 5.9 | Payment session read + redirect (claim แล้วเรียก PSP) | `POST /api/v1/payments/sessions/{paymentSessionId:guid}/redirect` (กลาง), `GET /api/v1/payments/sessions/{paymentSessionId:guid}` (ประกอบ) |
| 5.10 | PSP webhooks (connection / provider account) | `POST /api/v1/webhooks/{pspConnectionId:guid}` (กลาง), `POST /api/v1/webhooks/payment-providers/{providerAccountId:guid}` (ประกอบ) |

---

## 5.1 Checkout capability exchange

token จาก body เท่านั้นแลกเป็น capability อายุสั้นที่ผูก cookie, ไม่มี console session (source: `src/Api/Api/Program.cs:784-810`, `Application/Modules/Checkouts.Application/CheckoutAccess.cs:39-73`)

```mermaid
sequenceDiagram
    autonumber
    actor CU as Customer
    participant BR as Browser
    participant API as API /checkout/access
    participant DB as DB (PaymentLinks, Orders)

    Note over BR,API: Phase A — แลก token เป็น capability
    CU->>BR: เปิดลิงก์ชำระเงิน
    BR->>API: POST /api/v1/checkout/access {token} (rate limit customer-payment)
    API->>DB: FindByHashAsync(hash ของ token)
    DB-->>API: snapshot (LinkStatus, LinkExpiresAt, OrderStatus)
    break ไม่พบ, token ไม่ Active, หมดอายุ หรือ Order Cancelled
        API-->>BR: 404 Payment link was not found / 410 Gone
    end
    API->>API: ออก capability (Proof + CsrfToken ผูก OrderId, OrderVersion)
    API-->>BR: 200 + Set-Cookie checkout_capability (HttpOnly, Secure, SameSite=Strict)<br/>body มี proof, csrfToken, expiresAt
```

---

## 5.2 Checkout confirm / verify — เริ่มหรือ re-check การชำระผ่าน PSP

confirm สร้าง Transaction ใหม่แล้วเปิด charge, verify re-inquire Transaction เดิม ทั้งสองทางลงเอยที่กลไกแปลผล PSP เดียวกัน (source: `src/Api/Api/Program.cs:812-857,981-1036`, `Platform.Application/Transactions/CheckoutTransactionService.cs:63-247,414-577`)

```mermaid
sequenceDiagram
    autonumber
    actor CU as Customer
    participant BR as Browser
    participant API as API /checkout/confirm หรือ /verify
    participant CTS as CheckoutTransactionService
    participant DB as DB (Transactions, Orders)
    participant PSP as PSP (external)

    Note over BR,API: Phase A — ตรวจ capability + CSRF
    CU->>BR: กดยืนยันชำระเงิน
    BR->>API: POST /checkout/confirm {proof, csrfToken, orderId, orderVersion, method} + Idempotency-Key (rate limit)
    API->>API: เทียบ cookie checkout_capability กับ body.Proof (FixedTimeEquals)
    break ไม่ตรง, capability อ่านไม่ได้ หรือไม่พบ Order
        API-->>BR: 403 checkout_context_mismatch / checkout_capability_invalid / 404
    end
    API->>API: เทียบ capability.CsrfToken กับ command.CsrfToken, ตรวจ OrderId/OrderVersion
    break CSRF ไม่ตรง หรือ context เพี้ยน
        API-->>BR: 403 checkout_csrf_invalid / 409 checkout_context_mismatch
    end
    Note over API,DB: Phase B — สร้างหรือ reuse Transaction (confirm) / หา Transaction เดิม (verify)
    API->>CTS: StartAsync (confirm) หรือ VerifyOrderAsync (verify)
    CTS->>DB: transaction: ตรวจ link/order state, idempotency key, (confirm) เลือก PSP route + สร้าง Transaction
    break state ไม่ผ่าน (revoked/expired/paid/mismatch) หรือไม่มี Transaction ให้ verify
        DB-->>CTS: refuse
        CTS-->>API: exception
        API-->>BR: 403/409/410 ตามเงื่อนไข (ดูตาราง § 5.2 ใน activities.md)
    end
    DB-->>CTS: commit (Transaction Created หรือเดิม)
    Note over CTS,PSP: Phase C — confirm: เรียก PSP นอก DB transaction ข้ามถ้า reuse Transaction ที่มี ProviderReference แล้ว<br/>verify: เรียก PSP เฉพาะเมื่อยังไม่ Succeeded และมี ProviderReference แล้วเท่านั้น
    alt ไม่เรียก PSP (confirm reuse ที่มี ProviderReference แล้ว / verify ที่ Succeeded แล้วหรือยังไม่มี ProviderReference)
        CTS-->>API: CheckoutConfirmResult (สถานะปัจจุบันตรง ๆ หรือ duplicate evidence, ไม่เรียก PSP)
    else เรียก PSP (confirm สร้าง Transaction ใหม่ / reuse ที่ยังไม่มี ProviderReference, หรือ verify ที่มี ProviderReference และยังไม่ Succeeded)
        CTS->>PSP: CreateRedirectChargeAsync (confirm) / FetchChargeAsync (verify)
        alt PspRejectedException (confirm เท่านั้น, proven ไม่มี charge)
            PSP-->>CTS: rejected
            CTS->>DB: MarkFailedAsync (Failed)
        else exception อื่น (timeout/ไม่แน่ใจ — confirm และ verify map เป็น Pending เสมอ ไม่เคย Failed)
            PSP-->>CTS: ambiguous / timeout
            CTS->>DB: MarkPendingAsync (PendingConfirmation)
        else สำเร็จ (ไม่ throw)
            PSP-->>CTS: charge confirmation / redirect
            CTS->>DB: BindRedirectAsync (confirm, เสมอ Created+RedirectUrl)<br/>หรือ ApplyVerificationAsync (verify, reducer map เป็น Succeeded/Failed/Pending)
            opt verify ยืนยันสำเร็จจริง (Succeeded)
                CTS-->>CTS: -.async.-> outbox TransactionSucceeded ดู § 0.8
            end
        end
        CTS-->>API: CheckoutConfirmResult
    end
    API-->>BR: 200 (Created/Failed) หรือ 202 (PendingConfirmation)
```

---

## 5.3 Checkout reads (status / summary)

status อ่าน Transaction ล่าสุดโดยไม่มี PSP call และรับ proof จาก return-binding ได้ด้วย, summary re-validate DB ลึกกว่าแต่ไม่มีทางเข้า return-binding (source: `src/Api/Api/Program.cs:859-901,1073-1098`, `Platform.Application/Transactions/CheckoutTransactionService.cs:249-292`)

```mermaid
sequenceDiagram
    autonumber
    actor CU as Customer
    participant BR as Browser
    participant API as API /checkout/status หรือ /summary
    participant CTS as CheckoutTransactionService
    participant DB as DB (Transactions, Orders)

    Note over BR,API: Phase A — status: อาจมาจาก return-binding cookie
    BR->>API: GET /checkout/status (cookie checkout_capability หรือ checkout_status, rate limit)
    alt checkout_capability ไม่มี และ proof เป็น return-binding ที่ decode ได้
        API->>DB: GetByReturnBindingAsync(state)
        break ไม่พบ หรือ Id/OrderId ไม่ตรง binding
            API-->>BR: 403 checkout_capability_invalid / checkout_context_mismatch
        end
        API->>CTS: GetStatusForTransactionAsync(merchantId, transactionId, orderId)
        CTS-->>API: TransactionStatusView (ไม่มี PSP call)
        API-->>BR: 200
    else ปกติ (capability proof จาก cookie/header)
        API->>API: capabilities.TryRead(proof)
        break อ่านไม่ได้ หรือไม่พบ Order
            API-->>BR: 403 checkout_capability_invalid / 404
        end
        API->>CTS: GetStatusAsync(orderId, linkId)
        CTS-->>API: TransactionStatusView
        API-->>BR: 200
    end
    Note over BR,API: Phase B — summary: capability อย่างเดียว + re-validate DB
    BR->>API: GET /checkout/summary (cookie/header proof, rate limit)
    API->>API: capabilities.TryRead(proof)
    break อ่านไม่ได้, ไม่พบ Order, link ไม่ Active, หมดอายุ หรือ Version เพี้ยน
        API-->>BR: 403 checkout_capability_invalid / checkout_link_revoked / 404 / 410 Gone / 409 checkout_context_stale
    end
    API-->>BR: 200 CheckoutSummaryView (รายการสินค้า+ยอด, ไม่มี MerchantId)
```

---

## 5.4 Order-link customer payment (pay / payment-status / summary)

pay เปิด payment session ใหม่แล้วเริ่ม redirect ทันที (ใช้กลไกเดียวกับ § 5.8/5.9), payment-status/summary เป็น read ที่ผูก token เดียวกัน (source: `src/Api/Api/Program.cs:1824-1946`, `Application/Modules/Payments.Application/CreateSession/CreateSessionHandler.cs:67-148`, `Application/Modules/Payments.Application/StartRedirect/StartRedirectHandler.cs:74-177`, `ConfirmPaymentStatus/ConfirmPaymentStatusHandler.cs:22-89`)

```mermaid
sequenceDiagram
    autonumber
    actor CU as Customer
    participant BR as Browser
    participant API as API /orders/{token}/*
    participant CS as CreateSessionHandler
    participant SR as StartRedirectHandler
    participant CONF as PaymentConfirmationService
    participant DB as DB (Orders, Sessions)
    participant PSP as PSP (external)

    Note over BR,API: Phase A — pay: เปิด session แล้ว redirect ทันที (ไม่มี cookie/CSRF)
    BR->>API: POST /orders/{token}/pay (rate limit customer-payment)
    API->>DB: GetByTokenAsync(token)
    break ไม่พบ/หมดอายุ/Cancelled/Paid/ไม่มี PaymentChannel
        API-->>BR: 404 / 409 (already paid / no channel)
    end
    API->>CS: CreateSessionCommand(orderId, merchantId, method จาก channel)
    CS->>DB: mint หรือ resume Session ภายใต้ merchant lock (ดู § 5.8)
    break refuse (order ไม่ mintable, document ถูกขายที่อื่น, ไม่มี route, ช่องทางชนกัน)
        CS-->>API: NotFoundException / ConflictException / AccessDeniedException
        API-->>BR: 404 / 409 / 403 ตาม § 0.9
    end
    CS-->>API: paymentSessionId
    API->>SR: StartRedirectCommand(paymentSessionId)
    SR->>DB: claim redirect (BeginRedirect)
    break refuse ก่อน claim (session ไม่พบ, order ไม่ payable, method ไม่ตรง, connection ไม่ eligible, method ไม่ได้รับอนุญาต)
        SR-->>API: 404 NotFoundException / 409 ConflictException / 403 AccessDeniedException
        API-->>BR: 404 / 409 / 403 ตาม § 0.9
    end
    SR->>PSP: CreateRedirectChargeAsync (ดู § 5.9)
    break อ่าน secret ไม่สำเร็จ หรือ PSP ปฏิเสธ (pre-charge, ไม่ ambiguous)
        SR-->>API: 500 (session ถูก mark Failed) ตาม § 0.9
    end
    PSP-->>SR: redirectUrl
    SR->>DB: SetPspCharge, commit
    SR-->>API: RedirectUrl
    API-->>BR: 200 StartRedirectResponse(RedirectUrl)

    Note over BR,API: Phase B — payment-status: อ่านสถานะ อาจ inquiry PSP
    BR->>API: POST /orders/{token}/payment-status (rate limit)
    API->>DB: GetByTokenAsync(token) แล้วอ่าน Order.Status
    alt Order ปลายทางแล้ว (Paid/Refunded/Cancelled/Failed/Expired)
        API-->>BR: 200 paid / cancelled / failed (ไม่มี PSP call)
    else ยังเปิดอยู่ ไม่มี open Session
        API-->>BR: 200 pending
    else มี open Session
        API->>CONF: ConfirmAsync(session)
        CONF->>PSP: fetch-to-confirm (อาจ ambiguous)
        PSP-->>CONF: ผลลัพธ์ หรือ exception
        CONF-->>API: outcome (Paid/Failed/Pending, ambiguous -> pending)
        API-->>BR: 200 paid / failed / pending
    end

    Note over BR,API: Phase C — summary: อ่านอย่างเดียว ไม่มี rate limit
    BR->>API: GET /orders/{token}/summary
    API->>DB: GetByTokenAsync(token)
    break ไม่พบ token
        API-->>BR: 404
    end
    break หมดอายุ
        API-->>BR: 410 Gone
    end
    API-->>BR: 200 OrderSummaryResponse (ไม่มี MerchantId/paymentSessionId)
```

---

## 5.5 Payment link revoke

console (dual-console) หรือ identity-order เรียกปิดสิทธิ์เริ่มชำระจากลิงก์โดยตรง ไม่ผ่าน maker-checker (source: `src/Api/Api/Program.cs:1228-1265,3747-3764`, `Application/Modules/Orders.Application/OrderWorkflow.cs:1050-1094`)

```mermaid
sequenceDiagram
    autonumber
    actor U as Admin / Merchant user / Employee (identity)
    participant SPA as Console หรือ Web app
    participant API as API
    participant DB as DB (PaymentLinks, Orders)

    Note over SPA,API: Phase A — gate (§ 0.1/§ 0.2 dual-console หรือ identity-order) + CSRF
    U->>SPA: ขอเพิกถอน PaymentLink
    SPA->>API: POST /payment-links/{linkId}/revoke {reason?} + Idempotency-Key + X-CSRF-Token
    break reason ส่งมาแต่ว่าง หรือยาวเกิน 1000 ตัว
        API-->>SPA: 400 code validation_failed
    end
    opt เป็น identity request
        API->>DB: GetLinkAsync(merchantId, linkId)
        break ไม่พบ link
            API-->>SPA: 404 Payment link was not found
        end
        API->>API: EnsureIdentityOrderOwnerAsync: ตรวจ actor.UserId ผูก Account identity อยู่หรือไม่
        break UserId เป็น null (ไม่มี Account identity ผูก)
            API-->>SPA: 403 code account_context_missing (ไม่ mask)
        end
        API->>DB: หา Order + ตรวจ CanReadOrder ผ่าน scope/branch
        break order ไม่พบ หรือ CanReadOrder ปฏิเสธ
            API-->>SPA: 404 Order was not found (ซ่อน 403 จริง)
        end
    end
    Note over API,DB: Phase B — mediator RevokePaymentLinkCommand
    API->>DB: RequireFirstDeliveryAsync(Idempotency-Key)
    break key เคยใช้กับ operation นี้แล้ว
        API-->>SPA: 409 (replay guard)
    end
    API->>DB: GetLinkAsync + GetForUpdateAsync(order)
    break ไม่พบ link หรือ order
        API-->>SPA: 404
    end
    API->>DB: link.Revoke, order.RegisterLinkRotation, commit
    API-->>SPA: 200 PaymentLinkView
```

---

## 5.6 PSP browser return (payment-returns)

browser กลับจาก PSP ผ่าน protected return binding เท่านั้น ไม่ trust query/form success flag (source: `src/Api/Api/Program.cs:903-979`)

```mermaid
sequenceDiagram
    autonumber
    actor CU as Customer
    participant BR as Browser
    participant API as API /payment-returns/{providerCode}
    participant DB as DB (Transactions)

    Note over BR,API: Phase A — browser กลับจาก PSP
    BR->>API: GET หรือ POST /payment-returns/{providerCode}?state=... (rate limit customer-payment)
    opt POST และ query state ว่างและเป็น form content-type
        API->>API: อ่าน state จาก form body แทน (ReadFormAsync)
    end
    API->>API: returnBindings.TryRead(state) + ตรวจไม่หมดอายุ
    break binding อ่านไม่ได้ หรือหมดอายุ
        API-->>BR: 401 checkout_return_invalid
    end
    API->>DB: GetByReturnBindingAsync(state)
    break ไม่พบ, Id/OrderId ไม่ตรง binding หรือ Provider ไม่ตรง providerCode
        API-->>BR: 401 checkout_return_invalid
    end
    API-->>BR: Set-Cookie checkout_status=state (HttpOnly, Secure, SameSite=Strict)<br/>303 Location /api/v1/checkout/status
```

---

## 5.7 Payment session list

merchant-user เท่านั้น (ไม่ใช่ dual-console) อ่านรายการ payment session ของร้านค้าตัวเองผ่าน SFS (source: `src/Api/Api/Program.cs:1697-1722`, `Application/Modules/Payments.Application/GetSession/ListSessions.cs:22-47`)

```mermaid
sequenceDiagram
    autonumber
    actor MU as Merchant user
    participant SPA as Merchant Console
    participant API as API /payments/sessions
    participant P as SfsQueryParser
    participant DB as DB (Sessions)

    Note over SPA,API: policy merchant-user เท่านั้น (ดู § 0.1)
    MU->>SPA: เปิดรายการ payment session
    SPA->>API: GET /api/v1/payments/sessions?page=...&filters=... (permission payment.view)
    API->>P: Parse(query, maxLimit 100) ดู § 0.6
    break filters/sort/search ผิดรูป หรือเกิน cap
        API-->>SPA: 400 Invalid request
    end
    API->>DB: query ตาม merchant scope ของผู้เรียก (IMerchantScoped)
    DB-->>API: rows + total
    API-->>SPA: 200 PagedResult of PaymentSessionListItem
```

---

## 5.8 Payment session create (dual-console)

Merchant Console เปิด session ตรง, Admin Console ต้องมี merchantId + ผ่าน operation executor แบบ idempotent เท่านั้น (source: `src/Api/Api/Program.cs:1632-1695,3719-3728,3967-3982`, `Application/Modules/Payments.Application/CreateSession/CreateSessionHandler.cs:67-148`)

```mermaid
sequenceDiagram
    autonumber
    actor MU as Merchant user
    actor AD as Admin
    participant SPA as Merchant / Admin Console
    participant API as API POST /payments/sessions
    participant EXE as AdminOperationExecutor (admin เท่านั้น)
    participant CS as CreateSessionHandler
    participant DB as DB (Orders, Sessions)

    Note over SPA,API: gate dual-console + payment.create + audience CSRF (ดู § 0.1/§ 0.3)
    alt Merchant Console
        MU->>SPA: เปิด payment session
        SPA->>API: POST {orderId, method, psp?} (audience Merchant)
        break body มี merchantId
            API-->>SPA: 400 validation_failed
        end
        opt body.Psp มีค่า (compatibility window)
            API->>API: FlagLegacyPsp: header Deprecation:true + telemetry
        end
        API->>CS: CreateSessionCommand(orderId, actor.MerchantId, method)
    else Admin Console
        AD->>SPA: เปิด payment session ให้ merchant
        SPA->>API: POST {orderId, merchantId, method} + Idempotency-Key (audience Admin)
        break merchantId ว่าง หรือมี psp
            API-->>SPA: 400 validation_failed
        end
        API->>DB: RequireAdminOrderAsync (order อยู่ใน scope + merchant ตรง)
        break ไม่ผ่าน
            API-->>SPA: 404 / 403 merchant_scope_forbidden
        end
        API->>EXE: ExecuteAsync(operation=payment-session.create, Idempotency-Key) ดู § 0.5
        EXE->>CS: CreateSessionCommand(orderId, merchantId, method)
    end
    Note over CS,DB: กลไกภายใน CreateSessionHandler ดูรายละเอียดที่ § 5.4
    CS->>DB: ตรวจ order mintable, document ผูกที่เดียว, resume หรือ mint ใหม่ภายใต้ merchant lock, เลือก PSP route
    break refuse (order ไม่ mintable, document ถูกขายที่อื่น, ไม่มี route, ช่องทางชนกัน)
        CS-->>API: NotFoundException / ConflictException / AccessDeniedException
        API-->>SPA: 404 / 409 / 403 ตาม § 0.9
    end
    DB-->>CS: paymentSessionId
    CS-->>API: CreateSessionResult
    API-->>SPA: 200 CreatePaymentSessionResponse(paymentSessionId)
```

---

## 5.9 Payment session read + redirect (claim แล้วเรียก PSP)

redirect claim session ก่อนแตะ PSP กัน double charge, claim ที่ค้างไว้ (ไม่มี URL) settle ด้วย key เดิมเสมอ (source: `src/Api/Api/Program.cs:1727-1819`, `Application/Modules/Payments.Application/StartRedirect/StartRedirectHandler.cs:74-177`)

```mermaid
sequenceDiagram
    autonumber
    actor MU as Merchant user
    actor AD as Admin
    participant SPA as Merchant / Admin Console
    participant API as API /payments/sessions/{id}(/redirect)
    participant EXE as AdminOperationExecutor (admin เท่านั้น)
    participant SR as StartRedirectHandler
    participant DB as DB (Sessions, Connections)
    participant PSP as PSP (external)

    Note over SPA,API: Phase A — GET อ่านอย่างเดียว
    alt Merchant Console
        MU->>SPA: ดู payment session
        SPA->>API: GET /payments/sessions/{id} (audience Merchant, payment.view)
        API-->>SPA: 200 SessionView (merchant scope อัตโนมัติ) หรือ 404
    else Admin Console
        AD->>SPA: ดู payment session ของ merchant
        SPA->>API: GET /payments/sessions/{id}
        API->>DB: adminSessions.ResolveAsync (scope check)
        API-->>SPA: 200 SessionView + ETag vN (ดู § 0.5) หรือ 404
    end

    Note over SPA,PSP: Phase B — POST redirect: claim ก่อนแตะ PSP
    SPA->>API: POST /payments/sessions/{id}/redirect (+ If-Match, Idempotency-Key เมื่อ audience Admin)
    alt Merchant Console
        API->>SR: StartRedirectCommand(paymentSessionId)
    else Admin Console
        API->>DB: resolve scope + ตรวจ merchantId ตรง query
        break ไม่ตรงหรือไม่พบ
            API-->>SPA: 404 / 403 merchant_scope_forbidden
        end
        API->>EXE: ExecuteRecoverableAsync (If-Match version, Idempotency-Key) ดู § 0.5
        EXE->>SR: StartRedirectCommand(paymentSessionId, expectedVersion)
    end
    SR->>DB: GetByIdAsync session
    break ไม่พบ หรือ version ไม่ตรง
        SR-->>API: 404 / 409 ConcurrencyConflict
    end
    break status Redirected และมี RedirectUrl แล้ว
        SR-->>API: คืน URL เดิม (idempotent, ไม่เรียก PSP ซ้ำ)
    end
    opt status เป็น Created (ยังไม่เคย claim)
        SR->>DB: โหลด order+connection ที่ pin ไว้, resolve payment capability
        break order ไม่พบ/ไม่ payable, method ไม่ตรง,<br/>connection ไม่ eligible หรือ capability ไม่อนุญาต
            SR-->>API: 409 payment_authorization_context_missing /<br/>payment_method_mismatch / order_not_payable /<br/>payment_capability_unavailable หรือ 403 payment_method_not_allowed
        end
        SR->>DB: claim: BeginRedirect ภายใต้ merchant lock, commit
        break claim ชนกัน (concurrency conflict)
            SR-->>API: คืน URL ผู้ชนะ หรือ 409 already in progress
        end
    end
    SR->>PSP: CreateRedirectChargeAsync (key = Session.Id)
    alt PspRejectedException, ยังไม่ settling (proven ไม่มี charge)
        PSP-->>SR: rejected
        SR->>DB: FailSessionAsync (Failed) แล้ว rethrow
        SR-->>API: 500
    else exception อื่น (timeout/ambiguous) หรือ PspRejectedException ตอน settling
        PSP-->>SR: ambiguous / rejected ระหว่าง settle claim เดิม
        Note over SR,DB: when clause ไม่ match — ไม่ถูกจับ, claim คงสถานะเดิม ไม่ mark Failed
        SR-->>API: 500 (unhandled, รอ redirect ครั้งถัดไป settle ด้วย key เดิม)
    else สำเร็จ
        PSP-->>SR: redirectUrl
        SR->>DB: SetPspCharge, commit
        opt PSP เป็น FetchConfirmOnly (เช่น Omise)
            SR-->>SR: -.async.-> outbox PspChargeBound (ดู § 0.8 / § 5.10)
        end
        SR-->>API: RedirectUrl
    end
    API-->>SPA: 200 StartRedirectResponse(RedirectUrl) หรือ error ข้างต้น
```

---

## 5.10 PSP webhooks (connection / provider account)

webhook คือ source of truth: resolve merchant จาก trusted id ก่อน แล้ว verify signature ด้วย secret ที่ปักหมุดไว้ก่อนจะ fetch-to-confirm กับ PSP เพื่อยืนยันจริง (source: `src/Api/Api/Program.cs:1038-1071,1271-1316`, `Application/Modules/Payments.Application/HandlePspWebhook/HandlePspWebhookHandler.cs:67-222`)

```mermaid
sequenceDiagram
    autonumber
    participant PSP as PSP (2C2P / Omise)
    participant API as API /webhooks/{pspConnectionId}
    participant DB as DB (Connections, Sessions, InboundWebhookEvents)
    participant CONF as PaymentConfirmationService

    Note over PSP,DB: Phase A — resolve merchant + session จาก id ที่ trust ได้
    PSP->>API: POST /webhooks/{pspConnectionId} + X-Signature (rate limit psp-webhook)
    API->>DB: ResolveMerchantAsync(pspConnectionId)
    break ไม่พบ connection
        API-->>PSP: 404
    end
    API->>API: ExtractWebhookReference(payload) (bounded, ไม่ trust)
    API->>DB: resolve Session จาก reference (deterministic หรือ by ExternalChargeId)
    break ไม่พบ session และ PSP เป็น FetchConfirmOnly (Omise)
        API->>DB: record PendingMatch
        API-->>PSP: 202 webhook_pending_match (rematch async เมื่อ charge bind ดู § 5.9)
    end
    break ไม่พบ session และ PSP เป็น SignedDeterministic (2C2P)
        API-->>PSP: 503 webhook_verification_deferred
    end
    Note over API,DB: Phase B — verify signature ด้วย secret ที่ session ปักหมุด
    API->>DB: อ่าน pinned secret (version บน session)
    break อ่าน secret ไม่ได้
        API-->>PSP: 503 webhook_verification_deferred
    end
    API->>API: VerifyWebhook(payload, signature, secret)
    break signature ไม่ผ่าน
        API->>DB: record Rejected
        API-->>PSP: 401 webhook_signature_invalid
    end
    break charge ยังไม่ bind (session.PspExternalChargeId ว่าง)
        API-->>PSP: 503 (2C2P มาก่อน bind ของเรา)
    end
    Note over API,PSP: Phase C — fetch-to-confirm นอก transaction
    API->>CONF: PrepareAsync (fetch-to-confirm)
    CONF->>PSP: fetch charge
    alt ambiguous (timeout/5xx/unreadable)
        PSP-->>CONF: -
        CONF-->>API: PspAmbiguousException
        API-->>PSP: 503 (ไม่ ack, ไม่ fallback body)
    else สำเร็จ
        PSP-->>CONF: confirmation
        CONF-->>API: prepared
        API->>DB: transaction: ClaimAsync inbound event (fingerprint dedupe)
        alt fingerprint ไม่ตรง (event id reuse)
            DB-->>API: rejected
            API->>DB: record Rejected
            API-->>PSP: 401 webhook_signature_invalid
        else เคย complete แล้ว
            DB-->>API: duplicate
            API-->>PSP: 200 (Duplicate)
        else ใหม่
            API->>DB: ApplyPreparedAsync: map Paid/Failed/Expired, commit
            API-->>PSP: 200 (Processed)
            API-->>API: -.async.-> outbox PaymentPaid/PaymentFailed/PaymentExpired ดู § 0.8
        end
    end
```

---

## Notes

- Deviations ของ theme นี้อยู่ที่ `05-customer-checkout-payment-psp.activities.md` (ไม่พบ deviation) ไฟล์นี้ไม่ทำซ้ำ
- § 5.2/§ 5.4/§ 5.9/§ 5.10 ใช้ `break` แทนการซ้อน `alt` ลึกหลายชั้น เพื่อสะท้อน guard-clause ของโค้ดจริง (early throw/return) — อ่านว่าเงื่อนไขใน `break` เป็นทางออกจบ flow ทันที ไม่ตกไปยังบรรทัดถัดไป
- ลูกศร `-.async.->` แทน "การเขียนลง outbox ที่มี consumer/worker แยกต่างหาก" ไม่ใช่ syntax ลูกศรจริงของ mermaid sequence (mermaid sequence ไม่มีเส้นประ) — คือ self-message ที่บรรยายว่างานถัดไปเกิด "async" ดู § 0.8 สำหรับกลไก dispatcher จริง
- § 5.2/§ 5.10 ใช้กลไกเดียวกันคนละ aggregate: `/checkout/verify` (Transaction, `CheckoutTransactionService.VerifyAsync`) กับ `/webhooks/payment-providers/*` (Transaction เดียวกัน) VS `/webhooks/{pspConnectionId}` และ `/orders/{token}/payment-status` (Session, `PaymentConfirmationService`) — ดู Notes ของ activities.md

**Render**: GitHub / Obsidian / VS Code Mermaid
