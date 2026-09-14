# pol-core API — Customer checkout, payment sessions และ PSP callbacks (Activity Diagrams)

> Source: `docs/reference/api-endpoints.md` section "Commerce, checkout และ orders" (L90-108), "Payment compatibility" (L116-121), "Payment webhooks" (L127-128) และ source ที่อ้างต่อ § (`src/Api/Api/Program.cs:784-1948`, `Application/Modules/Checkouts.Application/*`, `Application/Modules/Platform.Application/Transactions/CheckoutTransactionService.cs`, `Application/Modules/Payments.Application/*`, `Application/Modules/Orders.Application/OrderWorkflow.cs`)
> Scope: 17 endpoints ของ customer checkout (opaque capability), order-link payment, payment session (dual-console), PSP browser return และ PSP webhook — 10 §
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

ลูกค้าแลก payment-link token (จาก body เท่านั้น) เป็น checkout capability อายุสั้นที่ผูก cookie, ไม่มี session หรือ CSRF filter มาตรฐาน (source: `src/Api/Api/Program.cs:784-810`, `Application/Modules/Checkouts.Application/CheckoutAccess.cs:39-73`)

```mermaid
flowchart TD
    START((●)) --> RL["rate limit customer-payment<br/>ดู § 0.4"]
    RL --> TOKLEN{"token ว่าง หรือยาวเกิน 512 ตัว?"}
    TOKLEN -->|yes| R404_1["404 Payment link was not found"]
    TOKLEN -->|no| LOOKUP["FindByHashAsync(hash ของ token)"]
    LOOKUP --> FOUND{"พบ snapshot และ<br/>LinkStatus เป็น Active?"}
    FOUND -->|no| R404_1
    FOUND -->|yes| EXP{"clock.UtcNow ถึง LinkExpiresAt แล้ว?"}
    EXP -->|yes| R410["410 Gone<br/>Payment link has expired"]
    EXP -->|no| CANCEL{"OrderStatus เป็น Cancelled?"}
    CANCEL -->|yes| R404_1
    CANCEL -->|no| ISSUE["ออก capability (Proof + CsrfToken)<br/>ผูก OrderId, OrderVersion, LinkId, ExpiresAt"]
    ISSUE --> COOKIE["Set-Cookie checkout_capability<br/>HttpOnly, Secure, SameSite=Strict<br/>Path=/api/v1/checkout"]
    COOKIE --> R200["200 CheckoutAccessResult<br/>orderId, orderVersion, proof, csrfToken, expiresAt"]
    R200 --> END_S((◉))
    R404_1 --> END_F((◉))
    R410 --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class ISSUE,COOKIE,R200,END_S ok
    class R404_1,R410,END_F fail
    class TOKLEN,FOUND,EXP,CANCEL gate
```

---

## 5.2 Checkout confirm / verify — เริ่มหรือ re-check การชำระผ่าน PSP

confirm สร้าง Transaction ใหม่แล้วเปิด charge กับ PSP, verify re-inquire Transaction ที่มีอยู่แล้วเท่านั้น (ไม่สร้างใหม่) ทั้งสองทางลงเอยที่กลไกแปลผล PSP เดียวกัน (source: `src/Api/Api/Program.cs:812-857,981-1036`, `Checkouts.Application/CheckoutConfirm.cs:35-94`, `Platform.Application/Transactions/CheckoutTransactionService.cs:63-247,414-577`)

```mermaid
flowchart TD
    START((●)) --> RL["rate limit customer-payment<br/>ดู § 0.4"]
    RL --> CTX{"cookie checkout_capability<br/>ตรงกับ body.Proof (FixedTimeEquals)?"}
    CTX -->|no| R403_CTX["403 code checkout_context_mismatch"]
    CTX -->|yes| CAP{"capabilities.TryRead(proof) สำเร็จ?"}
    CAP -->|no| R403_CAP["403 code checkout_capability_invalid"]
    CAP -->|yes| SUM{"GetSummaryAsync พบ Order?"}
    SUM -->|no| R404["404 Checkout order was not found"]
    SUM -->|yes| SEND["actorScope.Begin(merchantId)<br/>mediator CheckoutConfirmCommand<br/>(Idempotency-Key บังคับ)"]
    SEND --> CSRF{"capability.CsrfToken ตรงกับ<br/>command.CsrfToken?"}
    CSRF -->|no| R403_CSRF["403 code checkout_csrf_invalid"]
    CSRF -->|yes| MATCH{"capability.OrderId/OrderVersion<br/>ตรงกับ command?"}
    MATCH -->|no| R409_CTX["409 code checkout_context_mismatch"]
    MATCH -->|yes| DBTXN["DB transaction: โหลด checkout for update"]
    DBTXN --> STATE{"link Active ไม่หมดอายุ,<br/>order Open ไม่ Paid,<br/>method ตรง channel, Version ตรง capability?"}
    STATE -->|no| R4XX_STATE["403 checkout_link_revoked / 410 Gone /<br/>409 order_not_payable, order_already_paid,<br/>payment_method_mismatch, checkout_context_stale"]
    STATE -->|yes| IDEM{"idempotency TryBeginAsync<br/>(orderId+Idempotency-Key) ครั้งแรก?"}
    IDEM -->|"ไม่ใช่ครั้งแรก"| EXIST1{"มี Transaction เดิมของ Order<br/>(GetPotentialForOrderAsync)?"}
    EXIST1 -->|ไม่มี| R409_REPLAY["409 code idempotency_replay"]
    EXIST1 -->|มี| REUSE{"Transaction เดิมมี ProviderReference แล้ว?"}
    IDEM -->|ครั้งแรก| EXIST2{"มี Transaction เดิมของ Order?"}
    EXIST2 -->|มี| REUSE
    EXIST2 -->|ไม่มี| ROUTE["เลือก PSP route (routes.SelectAsync)<br/>ตรวจ connection ตรง merchant/PSP"]
    ROUTE --> ROUTEOK{"connection พร้อมใช้งาน?"}
    ROUTEOK -->|no| R409_ROUTE["409 code payment_capability_unavailable"]
    ROUTEOK -->|yes| CREATE["สร้าง Transaction (Created)<br/>claim provider call<br/>order.MarkPaymentProcessing, commit"]
    CREATE --> CALLPSP
    REUSE -->|มีแล้ว| REUSEDONE["คืนสถานะ Transaction เดิมตรง ๆ<br/>ไม่เรียก PSP"]
    REUSEDONE --> MAP
    REUSE -->|ยังไม่มี| REUSECLAIM["TryClaimProviderCall บน Transaction เดิม"]
    REUSECLAIM --> CALLPSP
    CALLPSP["เรียก PSP นอก DB transaction<br/>CreateRedirectChargeAsync (charge ใหม่)"]
    CALLPSP --> RESULT{"ผลจาก PSP?"}
    RESULT -->|สำเร็จ| BIND["BindRedirectAsync<br/>บันทึก ExternalChargeId+RedirectUrl<br/>สถานะยังเป็น Created"]
    RESULT -->|"provider ปฏิเสธชัดเจน"| FAILED["MarkFailedAsync: Failed<br/>คืน Order เป็น Unpaid ถ้าไม่มี Transaction อื่นค้าง"]
    RESULT -->|"timeout / ไม่แน่ใจ"| PENDING["MarkPendingAsync: PendingConfirmation<br/>ตั้งกำหนด inquiry ครั้งถัดไป"]
    BIND --> MAP{"TransactionStatus สุดท้าย?"}
    FAILED --> MAP
    PENDING --> MAP
    MAP -->|PendingConfirmation| R202["202 CheckoutConfirmResult<br/>PollAfterSeconds"]
    MAP -->|"Created (มี RedirectUrl) / Failed"| R200["200 CheckoutConfirmResult"]
    R200 --> END_S((◉))
    R202 --> END_S
    R403_CTX --> END_F((◉))
    R403_CAP --> END_F
    R404 --> END_F
    R403_CSRF --> END_F
    R409_CTX --> END_F
    R4XX_STATE --> END_F
    R409_REPLAY --> END_F
    R409_ROUTE --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    classDef ext fill:#4a3b0f,stroke:#e3b341,color:#fff
    class BIND,R200,R202,END_S ok
    class R403_CTX,R403_CAP,R404,R403_CSRF,R409_CTX,R4XX_STATE,R409_REPLAY,R409_ROUTE,END_F fail
    class CTX,CAP,SUM,CSRF,MATCH,STATE,IDEM,EXIST1,EXIST2,REUSE,ROUTEOK,RESULT,MAP gate
    class FAILED,PENDING warn
    class CALLPSP ext
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| POST | `/api/v1/checkout/verify` | ไม่สร้าง Transaction ใหม่: หา latest Transaction ของ Order ก่อน — ไม่มีเลยตอบ 409 `transaction_missing`; ข้าม gate STATE/IDEM/ROUTE ทั้งหมด; **PSP call มีเงื่อนไข**: Succeeded อยู่แล้ว → คืนผลเดิม (หรือบันทึก duplicate evidence เมื่อ event ซ้ำ) โดยไม่เรียก PSP; ยังไม่มี `ProviderReference` (ยังไม่เคยเปิด charge) → คืนสถานะปัจจุบันเลยโดยไม่เรียก PSP เช่นกัน (`CheckoutTransactionService.cs:198-204`); เหลือกรณีเดียวที่เรียก PSP จริง (ยังไม่ Succeeded และมี `ProviderReference` แล้ว) เป็น `FetchChargeAsync` (inquiry) ไม่ใช่สร้าง charge; ไม่มี branch "provider ปฏิเสธ" ผ่าน exception เพราะ `FetchChargeAsync` ที่ throw จะ map เป็น Pending เสมอ ไม่ใช่ Failed (`CheckoutTransactionService.cs:231-242`) แต่ transaction ยังจบเป็น **FAILED ได้** ผ่าน `TransactionResultReducer.ReduceFailure` เมื่อ inquiry สำเร็จ (ไม่ throw) แต่ PSP รายงานว่า charge นั้น failed ไปแล้ว (`TransactionResultReducer.cs:56-61,90-102`); สำเร็จจริงจะ enqueue outbox `TransactionSucceeded` ผ่าน `ApplyVerificationAsync` (ดู § 0.8); รองรับ path ลัดผ่าน cookie `checkout_status` (return-binding) ที่ข้าม checkout capability และ CSRF ไปเรียก `VerifyAsync` ตรงด้วย MerchantId จาก Transaction เอง — ดู Notes |

---

## 5.3 Checkout reads (status / summary)

status อ่านสถานะ Transaction ล่าสุดโดยไม่มี PSP call และรองรับ return-binding cookie เป็นทางเข้าอีกทาง, summary อ่านสรุป Order พร้อม re-validate link/version ที่ status ไม่ทำ (source: `src/Api/Api/Program.cs:859-901,1073-1098`, `Checkouts.Application/CheckoutConfirm.cs:58-71`, `Checkouts.Application/CheckoutAccess.cs:75-111`, `Platform.Application/Transactions/CheckoutTransactionService.cs:249-292`)

```mermaid
flowchart TD
    START((●)) --> RL["rate limit customer-payment<br/>ดู § 0.4"]
    RL --> SRC{"proof มาจากไหน?<br/>cookie checkout_capability / header X-Checkout-Proof /<br/>cookie checkout_status"}
    SRC -->|"checkout_capability เป็น null และ<br/>proof เป็น return-binding ที่ decode ได้"| RB["return-binding path"]
    RB --> RBFOUND{"GetByReturnBindingAsync พบ Transaction<br/>และ Id/OrderId ตรง binding?"}
    RBFOUND -->|no| R403_RB["403 checkout_capability_invalid /<br/>checkout_context_mismatch"]
    RBFOUND -->|yes| RBACTOR["actorScope.Begin(returned.MerchantId)<br/>GetStatusForTransactionAsync"]
    RBACTOR --> R200_RB["200 TransactionStatusView<br/>(ไม่มี PSP call)"]
    SRC -->|"ปกติ: checkout_capability cookie หรือ header"| CAP{"capabilities.TryRead(proof) สำเร็จ?"}
    CAP -->|no| R403_CAP["403 code checkout_capability_invalid"]
    CAP -->|yes| ORDFOUND{"GetSummaryAsync พบ Order?"}
    ORDFOUND -->|no| R404["404 Checkout order was not found"]
    ORDFOUND -->|yes| BINDACT["actorScope.Begin(merchantId)<br/>mediator CheckoutStatusQuery"]
    BINDACT --> READ["GetStatusAsync: อ่าน Transaction ล่าสุด<br/>+ Order.PaymentStatus (ไม่มี PSP call)"]
    READ --> R200["200 TransactionStatusView"]
    R200_RB --> END_S((◉))
    R200 --> END_S
    R403_RB --> END_F((◉))
    R403_CAP --> END_F
    R404 --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class RBACTOR,R200_RB,BINDACT,READ,R200,END_S ok
    class R403_RB,R403_CAP,R404,END_F fail
    class SRC,RBFOUND,CAP,ORDFOUND gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/checkout/summary` | ไม่มี return-binding branch (มีแค่ cookie/header proof, mismatch ระหว่างสองค่าตอบ 403 `checkout_context_mismatch`); handler re-validate เพิ่ม 3 เงื่อนไขที่ status ไม่เช็ก — `LinkStatus != Active` → 403 `checkout_link_revoked`, หมดอายุ → 410 Gone, `OrderVersion` ไม่ตรง capability → 409 `checkout_context_stale`; ผลลัพธ์คือ `CheckoutSummaryView` (รายการสินค้า+ยอด) ไม่ใช่ `TransactionStatusView`; ไม่มี PSP call เหมือนกัน |

---

## 5.4 Order-link customer payment (pay / payment-status / summary)

pay เปิด payment session ใหม่ (ใช้กลไกเดียวกับ § 5.8/5.9) แล้วเริ่ม redirect ทันที; payment-status/summary เป็น read ที่ผูก token เดียวกันแต่ไม่มี branch สร้าง session (source: `src/Api/Api/Program.cs:1824-1946`, `Application/Modules/Payments.Application/CreateSession/CreateSessionHandler.cs:67-148`, `Application/Modules/Payments.Application/StartRedirect/StartRedirectHandler.cs:74-177`)

```mermaid
flowchart TD
    START((●)) --> RL["rate limit customer-payment<br/>ดู § 0.4"]
    RL --> LOOKUP["reader.GetByTokenAsync(token)"]
    LOOKUP --> FOUND{"พบ summary และยังไม่หมดอายุ?"}
    FOUND -->|no| R404["404 (ไม่พบ token หรือหมดอายุ)"]
    FOUND -->|yes| CANCEL{"summary.Status เป็น Cancelled?"}
    CANCEL -->|yes| R404
    CANCEL -->|no| PAID{"summary.Status เป็น Paid?"}
    PAID -->|yes| R409_PAID["409 This order is already paid"]
    PAID -->|no| CHAN{"summary.PaymentChannel มีค่า?"}
    CHAN -->|no| R409_CHAN["409 This order has no payment channel<br/>to charge through"]
    CHAN -->|yes| BINDACT["actorScope.Begin(merchantId)"]
    BINDACT --> CREATE["mediator CreateSessionCommand<br/>(order/method จาก channel)<br/>ดูกลไกภายในที่ § 5.8"]
    CREATE --> CREATERES{"ผล CreateSessionCommand?"}
    CREATERES -->|"ConflictException / NotFoundException / AccessDeniedException"| R4XX_CREATE["404/403/409 ตาม § 0.9<br/>(routing_unavailable, payment_method_not_allowed ฯลฯ)"]
    CREATERES -->|สำเร็จ| REDIR["mediator StartRedirectCommand(paymentSessionId)<br/>ดูกลไกภายในที่ § 5.9"]
    REDIR --> REDIRRES{"ผล StartRedirectCommand?"}
    REDIRRES -->|NotFoundException| R404_REDIR["404 PaymentSession not found"]
    REDIRRES -->|AccessDeniedException| R403_REDIR["403 payment_method_not_allowed"]
    REDIRRES -->|"ConflictException / ConcurrencyConflictException /<br/>InvalidOperationException"| R409_REDIR["409 ตาม § 0.9<br/>(payment_authorization_context_missing,<br/>payment_method_mismatch, order_not_payable,<br/>payment_capability_unavailable, ConcurrencyConflict,<br/>redirect is already in progress ฯลฯ)"]
    REDIRRES -->|"PspRejectedException / vault-secret exception<br/>ระหว่าง claim แรก (proven ไม่มี charge)"| R500_REDIR["500 ตาม § 0.9<br/>(mark Failed เฉพาะกรณีนี้ — exception อื่น<br/>หรือตอน resume session ที่ claim ค้างอยู่แล้ว<br/>ปล่อยผ่านไม่ mark Failed, ดู § 5.9)"]
    REDIRRES -->|สำเร็จ| R200["200 StartRedirectResponse(RedirectUrl)"]
    R200 --> END_S((◉))
    R404 --> END_F((◉))
    R409_PAID --> END_F
    R409_CHAN --> END_F
    R4XX_CREATE --> END_F
    R404_REDIR --> END_F
    R403_REDIR --> END_F
    R409_REDIR --> END_F
    R500_REDIR --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class R200,END_S ok
    class R404,R409_PAID,R409_CHAN,R4XX_CREATE,R404_REDIR,R403_REDIR,R409_REDIR,R500_REDIR,END_F fail
    class FOUND,CANCEL,PAID,CHAN,CREATERES,REDIRRES gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| POST | `/api/v1/orders/{token}/payment-status` | ไม่สร้าง session: อ่าน Order ก่อน — status ปลายทาง (Paid/Refunded/Cancelled/Failed/Expired) ตอบทันทีไม่มี PSP call; ถ้ายังเปิดอยู่และไม่มี open Session ตอบ `pending`; มี open Session เรียก `PaymentConfirmationService.ConfirmAsync` (อาจ inquiry PSP) แล้ว map เหลือ 4 ค่า `paid/failed/pending/cancelled`; error ที่ ambiguous (timeout/`PspAmbiguousException`) map เป็น `pending` เสมอ ไม่ใช่ error response; handler ดักเฉพาะ `HttpRequestException`/`TaskCanceledException`/`JsonException`/`PspAmbiguousException` — wiring/config fault เช่นไม่มี PSP connection ผูกไว้ (`InvalidOperationException`) ไม่ถูกดัก หลุดออกไปเป็น 409 ผ่าน ProblemDetails handler กลาง (ดู § 0.9) ไม่ใช่ 200 pending เสมอไป |
| GET | `/api/v1/orders/{token}/summary` | อ่านอย่างเดียว ไม่มี rate limit (ตรงกับเอกสาร), ไม่มี actorScope/mediator, ไม่คืน MerchantId หรือ payment session id; หมดอายุตอบ 410 Gone (คนละโค้ดกับ pay ที่ตอบ 404 เมื่อหมดอายุ) |

---

## 5.5 Payment link revoke

console (dual-console) หรือ identity-order เรียกปิดสิทธิ์เริ่มชำระจากลิงก์โดยตรง ไม่ผ่าน maker-checker และไม่มี If-Match (มีแค่ Idempotency-Key) (source: `src/Api/Api/Program.cs:1228-1265,3747-3764`, `Application/Modules/Orders.Application/OrderWorkflow.cs:1050-1094`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy dual-console<br/>RequireOrderIdentityPermission(payment.create, checkout.write)<br/>RequireAudienceCsrf ดู § 0.1 / § 0.2 / § 0.3"]
    AUTHZ --> REASON{"body.Reason ส่งมา และว่าง<br/>หรือยาวเกิน 1000 ตัว?"}
    REASON -->|yes| R400["400 code validation_failed"]
    REASON -->|no| IDREQ{"เป็น identity request<br/>(IsIdentityRequest)?"}
    IDREQ -->|yes| LINKFIRST{"GetLinkAsync พบ link<br/>ของ merchant นี้?"}
    LINKFIRST -->|no| R404_L["404 Payment link was not found"]
    LINKFIRST -->|yes| OWNER{"EnsureIdentityOrderOwnerAsync:<br/>actor.UserId ผูก Account identity อยู่?"}
    OWNER -->|"ไม่ผูก (UserId null)"| R403_ACC["403 code account_context_missing<br/>(ไม่ mask เป็น 404)"]
    OWNER -->|ผูกแล้ว| CANREAD{"order พบ และ CanReadOrder<br/>ผ่าน scope/branch?"}
    CANREAD -->|no| R404_O["404 Order was not found<br/>(ซ่อน 403 เป็น 404 กัน probe)"]
    CANREAD -->|yes| SEND
    IDREQ -->|no| SEND["mediator RevokePaymentLinkCommand<br/>(Idempotency-Key บังคับ)"]
    SEND --> DELIVERY{"OrderCommandGuards.RequireFirstDeliveryAsync:<br/>Idempotency-Key ยังไม่เคยใช้กับ operation นี้?"}
    DELIVERY -->|no| R409_D["409 (replay guard)"]
    DELIVERY -->|yes| LINK2{"GetLinkAsync พบ link?"}
    LINK2 -->|no| R404_L
    LINK2 -->|yes| ORD{"GetForUpdateAsync พบ Order?"}
    ORD -->|no| R404_ORD["404 Order was not found"]
    ORD -->|yes| REVOKE["link.Revoke, order.RegisterLinkRotation<br/>commit ใน transaction เดียว"]
    REVOKE --> R200["200 PaymentLinkView"]
    R200 --> END_S((◉))
    R400 --> END_F((◉))
    R404_L --> END_F
    R403_ACC --> END_F
    R404_O --> END_F
    R409_D --> END_F
    R404_ORD --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class REVOKE,R200,END_S ok
    class R400,R404_L,R403_ACC,R404_O,R409_D,R404_ORD,END_F fail
    class REASON,IDREQ,LINKFIRST,OWNER,CANREAD,DELIVERY,LINK2,ORD gate
```

---

## 5.6 PSP browser return (payment-returns)

browser ที่กลับจาก PSP ผ่าน protected return binding เท่านั้น ไม่ trust query/form success flag และไม่สร้างหรือยืนยัน Transaction เอง เพียงออก status-only cookie แล้ว 303 ไปอ่านสถานะจริง (source: `src/Api/Api/Program.cs:903-979`)

```mermaid
flowchart TD
    START((●)) --> RL["rate limit customer-payment<br/>ดู § 0.4"]
    RL --> READSTATE["อ่าน state จาก query string"]
    READSTATE --> BIND{"returnBindings.TryRead(state) สำเร็จ<br/>และยังไม่หมดอายุ?"}
    BIND -->|no| R401["401 code checkout_return_invalid"]
    BIND -->|yes| TXLOOK["GetByReturnBindingAsync(state)"]
    TXLOOK --> MATCH{"พบ Transaction, Id/OrderId ตรง binding<br/>และ Provider ตรง providerCode?"}
    MATCH -->|no| R401
    MATCH -->|yes| COOKIE["Set-Cookie checkout_status = state<br/>HttpOnly, Secure, SameSite=Strict<br/>Path=/api/v1/checkout"]
    COOKIE --> R303["303 See Other<br/>Location /api/v1/checkout/status"]
    R303 --> END_S((◉))
    R401 --> END_F((◉))

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class COOKIE,R303,END_S ok
    class R401,END_F fail
    class BIND,MATCH gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| POST | `/api/v1/payment-returns/{providerCode}` | อ่าน `state` จาก query string ก่อน ถ้าว่างและ request เป็น form content-type จึงอ่านจาก form body แทน (`ReadFormAsync`); ตรรกะ validate/response ที่เหลือเหมือนกันทุกจุด (401/303) |

---

## 5.7 Payment session list

merchant-user เท่านั้น (ไม่ใช่ dual-console) อ่านรายการ payment session ของร้านค้าตัวเองผ่าน SFS (source: `src/Api/Api/Program.cs:1697-1722`, `Application/Modules/Payments.Application/GetSession/ListSessions.cs:22-47`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy merchant-user<br/>permission payment.view ดู § 0.1"]
    AUTHZ --> PARSE["SfsQueryParser.Parse(query, maxLimit 100)<br/>ดู § 0.6"]
    PARSE --> BADQ{"filters/sort/search ผิดรูป หรือเกิน cap?"}
    BADQ -->|yes| R400["400 ProblemDetails Invalid request"]
    BADQ -->|no| QUERY["ListSessionsHandler: query ตาม<br/>merchant scope ของผู้เรียก (IMerchantScoped)"]
    QUERY --> R200["200 PagedResult of PaymentSessionListItem"]
    R200 --> END_S((◉))
    R400 --> END_F((◉))

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class QUERY,R200,END_S ok
    class R400,END_F fail
    class BADQ gate
```

---

## 5.8 Payment session create (dual-console)

Merchant Console เปิด session ตรง, Admin Console ต้องมี merchantId + ผ่าน operation executor แบบ idempotent เท่านั้น (server เป็นผู้เลือก PSP เสมอทั้งสองฝั่ง) (source: `src/Api/Api/Program.cs:1632-1695,3719-3728,3967-3982`, `Application/Modules/Payments.Application/CreateSession/CreateSessionHandler.cs:67-148`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy dual-console<br/>permission payment.create<br/>RequireAudienceCsrf ดู § 0.1 / § 0.3"]
    AUTHZ --> AUD{"IsAdminCommerceRequest<br/>(SelectedConsoleAudience = Admin)?"}
    AUD -->|no| MFIELD{"body.MerchantId ส่งมาด้วย?"}
    MFIELD -->|yes| R400_M["400 code validation_failed<br/>Merchant payment session forbids merchantId"]
    MFIELD -->|no| MFLAG["FlagLegacyPsp: body.Psp มีค่า<br/>-> header Deprecation: true + telemetry"]
    MFLAG --> MSEND["mediator CreateSessionCommand<br/>(orderId, actor.MerchantId, method)"]
    AUD -->|yes| AFIELD{"merchantId ว่าง/Empty<br/>หรือ body.Psp มีค่า?"}
    AFIELD -->|yes| R400_A["400 code validation_failed<br/>Admin payment session requires merchantId<br/>and forbids psp"]
    AFIELD -->|no| ORDSCOPE{"RequireAdminOrderAsync:<br/>Order อยู่ใน scope และ merchant ตรง?"}
    ORDSCOPE -->|no| R4XX_SCOPE["404 Order was not found /<br/>403 merchant_scope_forbidden"]
    ORDSCOPE -->|yes| ABINDACT["actorScope.Begin(merchantId)"]
    ABINDACT --> AEXEC["ExecuteAdminCommerceAsync:<br/>AdminOperationExecutor + Idempotency-Key<br/>(AdminIdempotencyMutationMarker) ดู § 0.5"]
    AEXEC --> ASEND["mediator CreateSessionCommand<br/>(orderId, merchantId, method) ภายใน executor"]
    MSEND --> INNER["CreateSessionHandler:<br/>method match, order mintable, ตรวจ document ผูกอยู่ที่เดียว,<br/>resume session ที่เปิดอยู่หรือ mint ใหม่ภายใต้ merchant lock<br/>เลือก PSP route เอง ดูรายละเอียดที่ § 5.4"]
    ASEND --> INNER
    INNER --> INNERRES{"ผลลัพธ์?"}
    INNERRES -->|"NotFoundException / ConflictException / AccessDeniedException"| R4XX_INNER["404/409/403 ตาม § 0.9<br/>(routing_unavailable, payment_method_not_allowed ฯลฯ)"]
    INNERRES -->|สำเร็จ| R200["200 CreatePaymentSessionResponse(paymentSessionId)"]
    R200 --> END_S((◉))
    R400_M --> END_F((◉))
    R400_A --> END_F
    R4XX_SCOPE --> END_F
    R4XX_INNER --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    class R200,END_S ok
    class R400_M,R400_A,R4XX_SCOPE,R4XX_INNER,END_F fail
    class AUD,MFIELD,AFIELD,ORDSCOPE,INNERRES gate
    class MFLAG warn
```

---

## 5.9 Payment session read + redirect (claim แล้วเรียก PSP)

redirect claim session ก่อนแตะ PSP กัน double charge, ผลลัพธ์ที่ claim ค้างไว้ (ไม่มี URL) จะถูก settle ด้วย key เดิมเสมอ (source: `src/Api/Api/Program.cs:1727-1819`, `Application/Modules/Payments.Application/StartRedirect/StartRedirectHandler.cs:74-177`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy dual-console<br/>permission payment.redirect<br/>RequireAudienceCsrf ดู § 0.1 / § 0.3"]
    AUTHZ --> AUD{"IsAdminCommerceRequest?"}
    AUD -->|no| MSEND["mediator StartRedirectCommand(paymentSessionId)<br/>(merchant scope อัตโนมัติ)"]
    AUD -->|yes| ARES{"adminSessions.ResolveAsync พบ session<br/>ใน scope ที่ query merchantId?"}
    ARES -->|no| R404_A["404 Payment session was not found"]
    ARES -->|yes| AMATCH{"session.MerchantId ตรง<br/>query merchantId?"}
    AMATCH -->|no| R403_A["403 code merchant_scope_forbidden"]
    AMATCH -->|yes| AIFM["VersionEtags.Require (If-Match)<br/>AdminIfMatchMutationMarker + Idempotency-Key<br/>ดู § 0.5"]
    AIFM --> AEXEC["ExecuteRecoverableAdminCommerceAsync:<br/>AdminOperationExecutor"]
    AEXEC --> ASEND["mediator StartRedirectCommand(paymentSessionId, expectedVersion)<br/>ภายใน executor"]
    MSEND --> LOOKUP
    ASEND --> LOOKUP["StartRedirectHandler: GetByIdAsync session"]
    LOOKUP --> SFOUND{"พบ session?"}
    SFOUND -->|no| R404_S["404 PaymentSession not found"]
    SFOUND -->|yes| VEROK{"ExpectedVersion (admin เท่านั้น)<br/>ตรงกับ session.Version?"}
    VEROK -->|no| R409_VER["409 ConcurrencyConflict"]
    VEROK -->|yes| ALREADY{"status Redirected และมี RedirectUrl แล้ว?"}
    ALREADY -->|yes| R200_IDEM["200 คืน RedirectUrl เดิม<br/>(idempotent re-entry, ไม่เรียก PSP ซ้ำ)"]
    ALREADY -->|no| CLAIMST{"status Redirected แต่ไม่มี URL<br/>(claim ค้าง, ambiguous ครั้งก่อน)?"}
    CLAIMST -->|yes| SETTLE["settle ด้วย connection เดิม<br/>ไม่เรียก ClaimFirstRedirectAsync ซ้ำ"]
    CLAIMST -->|no| STATUSCHK{"status เป็น Created?"}
    STATUSCHK -->|no| R409_STATUS["409 InvalidOperationException<br/>(status ไม่ถูกต้องสำหรับ redirect)"]
    STATUSCHK -->|yes| CLAIM["DB transaction: โหลด order+connection ที่ pin ไว้,<br/>resolve payment capability (ResolveMethodAsync)"]
    CLAIM --> CLAIMDOM{"ผ่านทุกเงื่อนไข domain?<br/>order พบ+payable, connection eligible,<br/>capability allowed"}
    CLAIMDOM -->|ปฏิเสธ| R4XX_CLAIMDOM["409 payment_authorization_context_missing /<br/>payment_method_mismatch / order_not_payable /<br/>payment_capability_unavailable<br/>หรือ 403 payment_method_not_allowed"]
    CLAIMDOM -->|ผ่าน| BEGINREDIRECT["session.BeginRedirect(Redirected), commit"]
    BEGINREDIRECT --> CLAIMOK{"commit สำเร็จ<br/>(ไม่มี concurrency conflict)?"}
    CLAIMOK -->|"conflict, winner มี URL แล้ว"| R200_WIN["200 คืน RedirectUrl ของผู้ชนะ"]
    CLAIMOK -->|"conflict, winner ยังไม่มี URL"| R409_PROG["409 redirect is already in progress"]
    CLAIMOK -->|yes| SECRET["อ่าน vault secret (pinned version บน session)"]
    SETTLE --> SECRET
    SECRET --> SECOK{"อ่าน secret สำเร็จ?"}
    SECOK -->|"ไม่สำเร็จ (ยังไม่ settling)"| FAILSESS["FailSessionAsync + rethrow"]
    SECOK -->|yes| CALLPSP["เรียก PSP: CreateRedirectChargeAsync<br/>ด้วย idempotency key = Session.Id"]
    CALLPSP --> PSPRES{"ผล PSP?"}
    PSPRES -->|"PspRejectedException, ยังไม่ settling<br/>(proven ไม่มี charge)"| FAILSESS
    PSPRES -->|"exception อื่น (timeout/ambiguous)<br/>หรือ PspRejectedException ตอน settling"| UNCAUGHT["exception หลุดไม่ถูกจับ<br/>claim คงสถานะเดิม ไม่ mark Failed"]
    PSPRES -->|"สำเร็จ (ไม่ throw)"| SETPSP["session.SetPspCharge(bind redirect)<br/>commit"]
    SETPSP --> FCONLY{"PSP เป็น FetchConfirmOnly (เช่น Omise)?"}
    FCONLY -->|yes| OUTBOX["outbox PspChargeBound<br/>-.async.-> InboundWebhookRematcher ดู § 0.8 / § 5.10"]
    FCONLY -->|no| R200["200 StartRedirectResult(RedirectUrl)"]
    OUTBOX --> R200
    R200 --> END_S((◉))
    R200_IDEM --> END_S
    R200_WIN --> END_S
    R404_A --> END_F((◉))
    R403_A --> END_F
    R404_S --> END_F
    R409_VER --> END_F
    R409_STATUS --> END_F
    R4XX_CLAIMDOM --> END_F
    R409_PROG --> END_F
    FAILSESS --> R500["500 (proven ไม่มี charge, session ถูก mark Failed ก่อน rethrow)"]
    UNCAUGHT --> R500B["500 (claim ค้างสถานะเดิม ไม่ mark Failed<br/>รอ redirect ครั้งถัดไป settle ด้วย key เดิม)"]
    R500 --> END_F
    R500B --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    classDef ext fill:#4a3b0f,stroke:#e3b341,color:#fff
    class SETPSP,R200,R200_IDEM,R200_WIN,END_S ok
    class R404_A,R403_A,R404_S,R409_VER,R409_STATUS,R4XX_CLAIMDOM,R409_PROG,R500,R500B,END_F fail
    class AUD,ARES,AMATCH,SFOUND,VEROK,ALREADY,CLAIMST,STATUSCHK,CLAIMDOM,CLAIMOK,SECOK,PSPRES,FCONLY gate
    class OUTBOX warn
    class CALLPSP ext
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/payments/sessions/{paymentSessionId:guid}` | อ่านอย่างเดียว ไม่มี CSRF/If-Match บังคับส่ง; merchant path เรียก `GetSessionQuery` ตรง, admin path resolve scope เหมือนกันแล้วตั้ง `VersionEtags.Set` (ETag ขาออกเท่านั้น) ดู § 0.5; ไม่พบหรือ merchant ไม่ตรง scope -> 404 เดียว (ไม่แยก 403 เหมือน redirect เพราะเป็น query ที่ scope ผ่าน `Accessible.Merchants` อยู่แล้ว); ไม่มี PSP call |

---

## 5.10 PSP webhooks (connection / provider account)

webhook คือ source of truth: resolve merchant จาก trusted id ก่อนแตะข้อมูลใด ๆ, verify signature ด้วย secret ที่ session/Transaction ปักหมุดไว้ แล้วจึง fetch-to-confirm กับ PSP เพื่อยืนยันจริง (source: `src/Api/Api/Program.cs:1038-1071,1271-1316`, `Application/Modules/Payments.Application/HandlePspWebhook/HandlePspWebhookHandler.cs:67-222`, `Application/Modules/Payments.Application/Transactions/TransactionWebhook.cs:37-109`)

```mermaid
flowchart TD
    START((●)) --> RL["rate limit psp-webhook<br/>ดู § 0.4"]
    RL --> RESOLVE["merchantResolver.ResolveMerchantAsync(pspConnectionId)"]
    RESOLVE --> CFOUND{"พบ merchant จาก connection id?"}
    CFOUND -->|no| R404["404 (ไม่ leak เหตุผล)"]
    CFOUND -->|yes| BINDACT["actorScope.Begin(merchantId)"]
    BINDACT --> REF["adapter.ExtractWebhookReference(rawPayload)<br/>(bounded, untrusted, ไม่ state change)"]
    REF --> REFOK{"reference ผิดรูปแบบ<br/>หรือยาวเกิน bound?"}
    REFOK -->|yes| R400_REF["400 code validation_failed<br/>(InvalidRequestException)"]
    REFOK -->|no| SESLOOK{"reference resolve เป็น Session ได้?<br/>(2C2P: invoiceNo=SessionId deterministic,<br/>Omise: by ExternalChargeId)"}
    SESLOOK -->|"ไม่พบ + FetchConfirmOnly (Omise)"| PENDMATCH["record PendingMatch<br/>-.async.-> InboundWebhookRematcher (รอ bind)"]
    PENDMATCH --> R202["202 webhook_pending_match"]
    SESLOOK -->|"ไม่พบ + SignedDeterministic (2C2P)"| R503_UNRES["503 code webhook_verification_deferred"]
    SESLOOK -->|พบ| SECRET["อ่าน vault secret ที่ session ปักหมุด (pinned version)"]
    SECRET --> SECOK{"อ่าน secret สำเร็จ?"}
    SECOK -->|no| R503_SEC["503 code webhook_verification_deferred"]
    SECOK -->|yes| VERIFY{"adapter.VerifyWebhook(payload, signature, secret)?"}
    VERIFY -->|no| R401["401 code webhook_signature_invalid<br/>record Rejected"]
    VERIFY -->|yes| BOUND{"session.PspExternalChargeId มีค่าแล้ว?"}
    BOUND -->|no| R503_BIND["503 (2C2P มาก่อน bind ของเรา)"]
    BOUND -->|yes| PARSE{"ParseWebhook สำเร็จ?"}
    PARSE -->|no| R401_PAYLOAD["401 (record Rejected, invalid_payload)"]
    PARSE -->|yes| PREPARE["PrepareAsync: fetch-to-confirm กับ PSP<br/>นอก transaction ก่อน"]
    PREPARE --> PREPOK{"fetch สำเร็จ (ไม่ ambiguous)?"}
    PREPOK -->|no| R503_AMB["503 (PspAmbiguousException,<br/>ไม่ ack ไม่ fallback body)"]
    PREPOK -->|yes| CLAIMTX["DB transaction: ClaimAsync inbound event<br/>เทียบ fingerprint กัน event id reuse"]
    CLAIMTX --> DUP{"claim ซ้ำ (event id reuse หรือ<br/>เคย Complete แล้ว)?"}
    DUP -->|"fingerprint ไม่ตรง"| R401_REUSE["401 code webhook_signature_invalid<br/>(record Rejected, event_id_reuse)"]
    DUP -->|"เคย complete แล้ว"| R200_DUP["200 (Duplicate)"]
    DUP -->|no| APPLY["ApplyPreparedAsync: lock+reload session,<br/>map เป็น Paid/Failed/Expired<br/>-.async.-> outbox PaymentPaid/PaymentFailed/PaymentExpired ดู § 0.8"]
    APPLY --> R200_OK["200 (Processed/Duplicate/Ignored)"]
    R202 --> END_S((◉))
    R200_DUP --> END_S
    R200_OK --> END_S
    R404 --> END_F((◉))
    R400_REF --> END_F
    R503_UNRES --> END_F
    R503_SEC --> END_F
    R401 --> END_F
    R503_BIND --> END_F
    R401_PAYLOAD --> END_F
    R503_AMB --> END_F
    R401_REUSE --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    classDef ext fill:#4a3b0f,stroke:#e3b341,color:#fff
    class APPLY,R200_OK,R200_DUP,R202,END_S ok
    class R404,R400_REF,R503_UNRES,R503_SEC,R401,R503_BIND,R401_PAYLOAD,R503_AMB,R401_REUSE,END_F fail
    class CFOUND,REFOK,SESLOOK,SECOK,VERIFY,BOUND,PARSE,PREPOK,DUP gate
    class PENDMATCH warn
    class PREPARE ext
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| POST | `/api/v1/webhooks/payment-providers/{providerAccountId:guid}` | เส้นทาง Transaction รุ่นเก่า ไม่ใช่ Session แต่ gate แรกไม่ได้ถูกแทนที่: (1) `connections.GetByIdAsync(providerAccountId)` คือ 404 gate เดียวกับ CFOUND ของ § กลางทุกประการ (resolve ซ้ำสองครั้ง ทั้งใน endpoint `Program.cs:1048` และในตัว handler `TransactionWebhook.cs:41-43`); (2) `GetByProviderReferenceAsync` (providerAccountId+environment=null+reference) คือสิ่งที่แทน SESLOOK ของ § กลาง — ไม่พบ Transaction ตอบ 202 `transaction_unresolved` (ไม่ใช่ 503/PendingMatch แบบ § กลาง); reference ผิดรูปหรือยาวเกินตอบ 400 `validation_failed` เหมือน § กลาง เพราะเรียก `ExtractWebhookReference` ตัวเดียวกัน (`TransactionWebhook.cs:45`); ไม่มี PendingMatch/FetchConfirmOnly branch เลย; ใช้กลไก `CheckoutTransactionService.VerifyAsync` ตัวเดียวกับ `/checkout/verify` ใน § 5.2 (inquiry ไม่ fetch-to-confirm แบบ session) — สำเร็จ enqueue outbox `TransactionSucceeded` เท่านั้น (ไม่มี PaymentFailed/PaymentExpired แยก); status (ตามลำดับที่ตรวจจริงใน `TransactionWebhook.cs:49-108`): พบ Transaction แต่ `ProviderAccountId`/`Provider` ไม่ตรง connection → Rejected 401 `code=binding_mismatch` (เช็คทันทีหลัง resolve Transaction ก่อนเงื่อนไขอื่นทั้งหมด รวมถึงก่อน charge_unbound, `TransactionWebhook.cs:51-53`); ยังไม่มี `ProviderReference` → Deferred 202 `code=charge_unbound`; event ซ้ำ → Duplicate 200; อ่าน secret ไม่ได้ → Deferred 202 `code=credential_unavailable`; signature ไม่ผ่าน → Rejected 401 `code=signature_invalid`; parse payload ไม่ได้ → Rejected 401 `code=payload_invalid`; reference ไม่ตรง → Processed 200 `code=reference_mismatch` (flag ผ่าน `MarkEvidenceMismatchAsync` ไม่ reject) |

---

## Deviations

ไม่พบ deviation ระหว่างเอกสารกับ source สำหรับ 17 endpoint ของ theme นี้ — คอลัมน์ caller/auth policy ในเอกสารตรงกับ metadata ที่ endpoint ประกาศจริงทุกแถว (ตรวจโดยเทียบ `docs/reference/api-endpoints.md:90-121,127-128` กับ `src/Api/Api/Program.cs:784-1948`)

## Notes

- **สอง payment engine คู่ขนาน**: `/checkout/*`, `/orders/{token}/pay|summary` (บางส่วน) และ `/webhooks/payment-providers/*` เดินบน `Transaction` (`CheckoutTransactionService`, legacy), ส่วน `/orders/{token}/payment-status`, `/payments/sessions/*` และ `/webhooks/{pspConnectionId}` เดินบน `Session` (`PaymentConfirmationService`) — คนละ aggregate คนละ outbox event (`TransactionSucceeded` vs `PaymentPaid/PaymentFailed/PaymentExpired`) แม้ปลายทางจะเป็น Order เดียวกัน
- **checkout CSRF ไม่ใช่ § 0.3**: `/checkout/confirm` และ `/checkout/verify` เทียบ `CsrfToken` ที่ฝังในตัว capability กับค่าที่ client ส่งมาเอง (`CryptographicOperations.FixedTimeEquals`) เป็นกลไกเฉพาะของ checkout ไม่ใช่ cookie/header double-submit ของ § 0.3 เพราะ endpoint กลุ่มนี้ `AllowAnonymous` ไม่มี session cookie
- **return-binding เป็น capability คู่ขนาน**: cookie `checkout_status` (ออกโดย § 5.6) ใช้แทน `checkout_capability` ได้ใน `/checkout/status` และ `/checkout/verify` เท่านั้น — ไม่ใช้กับ `/checkout/summary` หรือ `/checkout/confirm`
- **redirect ที่อ่าน secret ไม่สำเร็จหรือ PSP ปฏิเสธ (ไม่ settling)** จะ mark session Failed ก่อน rethrow exception ดิบซึ่งไม่อยู่ใน map ของ § 0.9 โดยตรง จึงตกไปที่ 500 ค่าเริ่มต้น — อยู่นอก frame ว่ามี custom exception filter เพิ่มเติมหรือไม่ (`StartRedirectHandler.cs:139-163`)
- **admin idempotency ไม่มี If-Match คู่กันเสมอ**: `POST /payments/sessions` (create) มีแค่ `AdminIdempotencyMutationMarker` แต่ `POST .../redirect` มีทั้ง `AdminIfMatchMutationMarker` และ `AdminIdempotencyMutationMarker` — สร้าง session ใหม่ไม่มี resource เดิมให้ version เทียบ

**Render**: GitHub / Obsidian / VS Code Mermaid
