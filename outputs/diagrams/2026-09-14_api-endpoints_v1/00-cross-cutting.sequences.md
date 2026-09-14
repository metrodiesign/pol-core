# pol-core API — Cross-cutting behaviour (Sequence Diagrams)

> Source: `docs/reference/api-endpoints.md` บรรทัด L24 (policy / permission vocabulary) และ source host ที่อ้างต่อ § (`src/Api/Api/Iam/*`, `IdentityAccess/*`, `Admins/*`, `Merchants/*`, `Webhooks/RateLimiting.cs`, `ConcurrencyEtags.cs`, `SfsQueryParser.cs`, `Governance/GovernanceEndpoints.cs`, `BackgroundDispatch/*`, `BuildingBlocks.Web/ProblemDetailsExceptionHandler.cs`)
> Scope: 9 § เดียวกับ `00-cross-cutting.activities.md` (หมายเลข § ตรงกัน) แสดงลำดับข้าม actor / middleware / handler / DB / worker
> Generated: 2026-09-14

| § | Diagram | Endpoints |
| --- | --- | --- |
| 0.1 | Console session authentication + authorization | ทุก endpoint policy `admin` / `merchant-user` / `dual-console` ที่ต่อ RequirePermission, RequireAudiencePermission, RequirePlatformUserTier, BoundFilter |
| 0.2 | Identity BFF / Bearer authentication + identity-order permission | policy `identity-bff` / `identity-platform` / `admin-or-identity-order` และแถวที่มี `(identity: order.read / order.write / checkout.write)` |
| 0.3 | CSRF double-submit | ทุก unsafe method ที่มี CSRF filter (RequireCsrf, RequireUserCsrf, RequireAudienceCsrf, RequireAdminOrIdentityCsrf, BffCsrfFilter, RequireIdentityPlatformMutation) |
| 0.4 | Rate limiting | แถวที่ระบุ `rate limit` (policy customer-payment, admin-auth, merchant-user-auth, psp-webhook) |
| 0.5 | ETag / If-Match / Idempotency-Key | mutation ที่มี IfMatchMutationMarker, AdminIfMatchMutationMarker, IdempotencyMutationMarker, GovernanceDecisionMarker และ GET detail ที่คืน ETag |
| 0.6 | SFS query parsing | GET list ที่อ่าน page / limit / filters / sort / search ผ่าน SfsQueryParser |
| 0.7 | Maker-checker approval | endpoint `*-requests`, `*-change-requests`, `activation-requests`, `secret-rotation-requests` และ `/approvals/{approvalId}/approve` / `reject` |
| 0.8 | Background dispatch / outbox | handler ที่ enqueue outbox หรือทิ้งงานให้ worker (PaymentPaid, notification delivery, webhook delivery, PSP inquiry) |
| 0.9 | Error contract | ทุก endpoint (ProblemDetails JSON) และ browser callback / return (302 reason, 303 checkout status) |

---

## 0.1 Console session authentication + authorization

ConsoleSession policy scheme เลือก handler จาก policy + cookie, handler re-resolve บัญชีสดต่อ request แล้ว endpoint filters ตัดสิน permission แบบ fail-closed (source: `src/Api/Api/Iam/ConsoleSessionAuthentication.cs:33-68`, `Admins/SessionAuthenticationHandler.cs:62-168`, `Merchants/UserSessionAuthenticationHandler.cs:70-205`, `Iam/PermissionAuthorization.cs:83-139`, `Admins/HostWiring.cs:98-117`)

```mermaid
sequenceDiagram
    autonumber
    actor U as Admin / Merchant user
    participant SPA as Console SPA
    participant MW as API pipeline<br/>UseAuthentication + UseAuthorization
    participant SEL as ConsoleSession<br/>policy scheme
    participant AH as AdminSession handler
    participant MH as MerchantUserSession handler
    participant DB as DB (admin.Sessions / merch.Sessions)
    participant F as Endpoint filters
    participant H as Handler

    Note over SPA,SEL: Phase A — เลือก scheme จาก policy + cookie
    U->>SPA: ใช้งานหน้า console
    SPA->>MW: request + cookie __Host-adm_session หรือ __Host-mch_session (+ X-CSRF-Token เมื่อ unsafe)
    MW->>SEL: SelectScheme(policy metadata, cookies)
    alt policy admin หรือ dual-console ที่มี adm cookie
        SEL-->>MW: AdminSession (BFF cookie อย่างเดียวไป IdentityAccessBff ดู § 0.2)
    else policy merchant-user หรือ dual-console ที่ไม่มี adm cookie
        SEL-->>MW: MerchantUserSession
    end
    Note over MW,DB: Phase B — authenticate + bind scope ต่อ request
    alt AdminSession
        MW->>AH: HandleAuthenticateAsync
        AH->>DB: FindByTokenHash(SHA-256 cookie)
        DB-->>AH: session (family, status, expiry)
        AH->>AH: SessionDecisionPolicy.Decide (Reject / ReuseRevokeFamily / Serve)
        AH->>DB: ResolveByIdAsync(adminId) READ-ONLY
        DB-->>AH: Resolution (Tier, Accessible, Permissions)
        AH->>AH: AdminScope.Set + claims admin_tier, NameIdentifier
        AH->>DB: rotate token หรือ slide idle (เมื่อถึงกำหนด)
        AH-->>MW: Success หรือ Fail
    else MerchantUserSession
        MW->>MH: HandleAuthenticateAsync
        MH->>DB: FindByTokenHash + ResolveByIdAsync(userId) READ-ONLY
        DB-->>MH: session + Resolution (MerchantId, Permissions, SaleCode)
        MH->>MH: UserScope.Set + claims merchant_id, sale_code
        MH-->>MW: Success หรือ Fail (+ MerchantLifecycleChallenge)
    end
    alt authenticate ล้มเหลว
        MW-->>SPA: 401 ProblemDetails admin_session_required หรือ 401 default
    else merchant user ไม่ Active
        MW-->>SPA: 403 ProblemDetails code awaiting-approval / rejected / suspended / unbound
    else ผ่าน
        Note over F,H: Phase C — permission gates (fail-closed)
        MW->>F: run endpoint filters ตามลำดับ chain ของ endpoint
        F->>F: BoundFilter / RequirePermission / RequireAudiencePermission / RequirePlatformUserTier
        alt scope ที่ bind ไม่มี key หรือ tier ไม่ตรง
            F-->>SPA: 403 ProblemDetails (permission หรือ super_required)
        else ผ่านทุก gate
            F->>H: invoke handler
            H-->>SPA: 2xx
        end
    end
```

---

## 0.2 Identity BFF / Bearer authentication + identity-order permission

request ที่มี BFF cookie หรือ Bearer ผ่าน UseIdentityAccess (กัน context ซ้อน), authenticate ด้วย IdentityAccessBff หรือ OpenIddict, ตรวจ IdentityAccessRequirement สดต่อ request แล้ว filter identity-order ตัดสิน human permission หรือ system scope (source: `src/Api/Api/IdentityAccess/IdentityAccessWiring.cs:70-113`, `BffSessionAuthentication.cs:195-271`, `IdentityAccessAuthorization.cs:10-92`, `Iam/IdentityPermissionAuthorization.cs:24-97`, `Iam/IdentityRequestAuthorization.cs:15-50`)

```mermaid
sequenceDiagram
    autonumber
    actor U as Employee / Agent / SYSTEM client
    participant C as Web app หรือ integration client
    participant MW as API pipeline<br/>UseAuthentication + UseIdentityAccess + UseAuthorization
    participant BH as IdentityAccessBff handler
    participant OI as OpenIddict validation
    participant AR as IdentityAccessRequirement handler
    participant DB as DB (BffSessionTickets, Accounts, MerchantAccess)
    participant F as RequireOrderIdentityPermission filter
    participant H as Handler

    Note over C,MW: Phase A — ตรวจ context ซ้อน
    C->>MW: request + cookie __Host-pol_session หรือ Authorization Bearer
    alt identity-order route และมีทั้ง Bearer และ cookie (หรือ BFF + console cookie)
        MW-->>C: 400 ProblemDetails ambiguous_authentication_context
    end
    Note over MW,DB: Phase B — authenticate
    alt cookie BFF (policy identity-bff หรือ identity-platform)
        MW->>BH: HandleAuthenticateAsync
        BH->>DB: FindByHash(ticket) + FindAccount
        DB-->>BH: ticket + account
        BH->>BH: IsLiveAt(now, account.AuthorizationVersion) + Unprotect payload
        opt audience Admin (admin console ด้วย BFF cookie)
            BH->>DB: ResolveAuthorization(accountId) + ListMerchantAccess
            BH->>BH: AdminScope.Set (Employee เท่านั้น)
        end
        BH-->>MW: principal (sub, authz_version, token_context, scope, merchant_id)
    else Bearer (policy identity-platform หรือ identity-order route)
        MW->>OI: validate access token (issuer local, audience api, token entry)
        OI-->>MW: principal จาก token (sub, client_id, scope, merchant_id)
    end
    alt authenticate ล้มเหลว
        MW-->>C: 401
    end
    Note over AR,DB: Phase C — IdentityAccessRequirement (fresh ต่อ request)
    MW->>AR: HandleRequirementAsync
    AR->>DB: FindAccount + FindSystemClient + ResolveAuthorization(merchant)
    DB-->>AR: account, client, snapshot
    alt account ไม่ Active, authz_version stale, client ไม่ Active, merchant ไม่ตรง หรือ required_permission ขาด
        AR-->>C: 403
    else ผ่าน
        Note over F,H: Phase D — identity-order permission (เฉพาะ route ที่มี marker)
        MW->>F: filter
        F->>DB: ResolveMerchantAsync(principal)
        DB-->>F: IdentityMerchantAuthorization (snapshot, IsSystemClient)
        alt ไม่มี merchant_id claim
            F-->>C: 403 ProblemDetails merchant_context_missing
        else system client ไม่มี scope ที่ต้องการ หรือ human ไม่มี permission
            F-->>C: 403
        else ผ่าน
            F->>F: CommerceAuthorizationProof + IActorScope.Begin + IOrderIdentityAccessScope.Begin
            F->>H: invoke handler
            H-->>C: 2xx
        end
    end
```

---

## 0.3 CSRF double-submit

safe method ข้าม, unsafe method ด้วย cookie ต้องส่ง `X-CSRF-Token` เท่ากับ CSRF cookie ของ scheme (BFF เพิ่ม Origin + hash ใน ticket), Bearer ข้ามเฉพาะ identity route (source: `src/Api/Api/Admins/CsrfFilter.cs:20-44`, `Merchants/UserCsrfFilter.cs:21-33`, `Iam/CsrfParity.cs:43-68`, `IdentityAccess/BffCsrfFilter.cs:23-82`)

```mermaid
sequenceDiagram
    autonumber
    actor U as Admin / Merchant user / Employee
    participant SPA as Console หรือ Web app
    participant F as CSRF endpoint filter
    participant S as Session feature<br/>(BffSessionContext)
    participant H as Handler

    Note over SPA,F: Phase A — safe method
    SPA->>F: GET /api/v1/... (cookie session)
    F->>H: ข้ามการตรวจ (GET / HEAD / OPTIONS / TRACE)
    H-->>SPA: 200
    Note over SPA,H: Phase B — unsafe method ด้วย cookie session
    SPA->>F: POST /api/v1/... + cookie adm_csrf / mch_csrf / pol_csrf + header X-CSRF-Token
    alt RequireCsrf หรือ RequireUserCsrf หรือ AudienceCsrf (console)
        F->>F: เลือก cookie ตาม audience แล้วเทียบ header แบบ constant-time
    else BffCsrfFilter หรือ RequireIdentityPlatformMutation (cookie)
        F->>F: เทียบ cookie pol_csrf กับ header + ตรวจ Origin ตรง scheme://host
        F->>S: อ่าน ProtectedTicket.CsrfHash
        S-->>F: hash
        F->>F: SHA-256(header) ต้องเท่ากับ CsrfHash
    end
    alt ไม่ตรง หรือขาด
        F-->>SPA: 403 ProblemDetails Missing or invalid CSRF token (code csrf_failed)
    else ตรง
        F->>H: invoke handler
        H-->>SPA: 2xx
    end
    Note over SPA,H: Phase C — Bearer ข้าม CSRF
    SPA->>F: POST /api/v1/orders + Authorization Bearer (ไม่มี cookie)
    F->>H: RequireIdentityPlatformMutation / AudienceCsrf ปล่อยผ่านเมื่อ Bearer
    H-->>SPA: 201
```

---

## 0.4 Rate limiting

UseRateLimiter ตัดสินก่อน authentication ด้วย sliding window ต่อ source IP, เกินโควตาตอบ 429 + Retry-After ทันทีโดยไม่ถือ connection (source: `src/Api/Api/Webhooks/RateLimiting.cs:26-57`, `Customers/PaymentRateLimiting.cs:23-37`, `Admins/AuthRateLimiting.cs:25-38`, `Merchants/UserAuthRateLimiting.cs:17-31`, `Program.cs:711`)

```mermaid
sequenceDiagram
    autonumber
    actor U as Customer / Admin / Merchant user / PSP
    participant C as Browser หรือ PSP server
    participant RL as UseRateLimiter<br/>(sliding window per source IP)
    participant MW as Auth + endpoint

    Note over C,RL: Phase A — ภายในโควตา
    C->>RL: request ไป endpoint ที่มี RequireRateLimiting
    RL->>RL: partition key = RemoteIpAddress, AttemptAcquire (QueueLimit 0)
    RL->>MW: ผ่าน (permit acquired)
    MW-->>C: ตอบตามปกติ (auth ดู § 0.1 / § 0.2)
    Note over C,RL: Phase B — เกินโควตา
    C->>RL: request ซ้ำเกิน PermitLimit ในหน้าต่างเดียวกัน
    RL-->>C: 429 Too Many Requests + Retry-After (limiter estimate หรือ 2s)
```

---

## 0.5 ETag / If-Match / Idempotency-Key

อ่าน detail รับ ETag แล้ว mutate ด้วย If-Match + Idempotency-Key, executor เก็บ OperationRecord ใน transaction เดียวกับ mutation เพื่อ replay หรือปฏิเสธ key ที่ reuse, version stale ตอบ 409 (source: `src/Api/Api/ConcurrencyEtags.cs:18-42`, `Program.cs:1526-1560`, `Persistence.MerchantRuntime/Idempotency/AdminOperationExecutor.cs:24-60`, `Persistence.ControlPlane/Governance/ControlPlaneOperationExecutor.cs:43-82`)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin Console
    participant API as API endpoint
    participant EX as Operation executor<br/>(AdminOperationExecutor / ControlPlaneOperationExecutor)
    participant DB as DB (resource + OperationRecords)

    Note over SPA,DB: Phase A — อ่าน detail เพื่อรับ ETag
    SPA->>API: GET /api/v1/{resource}/{id}
    API->>DB: load resource (Version)
    DB-->>API: resource v3
    API-->>SPA: 200 + ETag v3
    Note over SPA,DB: Phase B — mutation ด้วย If-Match + Idempotency-Key
    SPA->>API: POST / PUT / PATCH + If-Match v3 + Idempotency-Key k1 (+ X-CSRF-Token)
    API->>API: VersionEtags.Require + IdempotencyKeys.Require
    alt header ผิดรูป
        API-->>SPA: 400 ProblemDetails invalid_etag / invalid_idempotency_key
    end
    API->>EX: ExecuteAsync(actor, merchant, operation, k1, intent, action)
    EX->>DB: begin transaction, lookup OperationRecord (actor, operation, k1)
    alt มี record เดิม intent ต่างกัน
        EX-->>API: ConflictException idempotency_key_reused
        API-->>SPA: 409 ProblemDetails
    else มี record เดิม Succeeded
        EX-->>API: stored response (Replayed)
        API-->>SPA: 200 / 202 เดิม + ETag เดิม
    else ไม่มี record
        EX->>DB: insert record InProgress
        EX->>DB: run action with expectedVersion 3
        alt version ใน DB ไม่ใช่ 3
            DB-->>EX: ConcurrencyConflictException
            EX-->>API: rollback
            API-->>SPA: 409 ProblemDetails Conflict
        else ตรง
            DB-->>EX: resource v4
            EX->>DB: record.Succeed(status, response) + commit
            EX-->>API: value v4
            API-->>SPA: 200 / 202 / 204 + ETag v4
        end
    end
```

---

## 0.6 SFS query parsing

GET list parse page / limit / filters / sort / search ที่ host แล้ว handler query ตาม scope ของ caller, paging ถูก clamp ส่วน JSON ผิดหรือเกิน cap เป็น 400 (source: `src/Api/Api/SfsQueryParser.cs:30-85`, `src/Api/BuildingBlocks.Web/ProblemDetailsExceptionHandler.cs:85-88`)

```mermaid
sequenceDiagram
    autonumber
    actor U as Admin / Merchant user
    participant SPA as Console SPA
    participant API as GET list endpoint
    participant P as SfsQueryParser
    participant H as Query handler
    participant DB as DB

    Note over SPA,P: Phase A — parse query string
    SPA->>API: GET /api/v1/{resource}?page=2&limit=50&filters=[...]&sort=[...]&search={...}
    API->>P: Parse(query, maxLimit 25)
    P->>P: clamp limit 50 เป็น 25, page อย่างน้อย 1
    alt JSON ผิดรูป หรือเกิน cap (50 filters / 200 values / 10 sort keys)
        P-->>API: ArgumentException
        API-->>SPA: 400 ProblemDetails Invalid request
    else ถูกต้อง
        P-->>API: (page, limit, filters, sort, search)
        Note over H,DB: Phase B — query ตาม scope ของ caller
        API->>H: query + scope (IAdminScope / IUserScope)
        H->>DB: WHERE scope + filters, ORDER BY sort ThenBy Id, OFFSET / FETCH
        DB-->>H: rows + total
        H-->>API: PagedResult
        API-->>SPA: 200 PagedResult
    end
```

---

## 0.7 Maker-checker approval

maker ยื่นคำขอ (202 pending) ผ่าน owner store ที่เขียน ApprovalRequested ลง governance outbox, dispatcher ส่งให้ GovernanceStore สร้าง ApprovalRequest, checker คนละคนตัดสินด้วย If-Match + Idempotency-Key แล้ว executor ของ owner apply ผลและรายงานกลับ (source: `src/Api/Api/Governance/GovernanceEndpoints.cs:123-182`, `Persistence.ControlPlane/Governance/GovernanceStore.cs:72-196`, `GovernanceOutboxDispatcher.cs:81-139`, `Iam/ApiClientStore.cs:135-170`, `Iam/ApiClientApprovalExecutor.cs:25-80`, `src/Application/Modules/Governance.Application/GovernanceHandlers.cs:44-75`)

```mermaid
sequenceDiagram
    autonumber
    actor M as Maker (Admin)
    actor K as Checker (Admin)
    participant SPA as Admin Console
    participant API as API
    participant OWN as Owner store<br/>(เช่น ApiClientStore, AdminPaymentsControlStore)
    participant GDB as DB admin.* (Approvals, GovernanceOutboxMessages, OperationRecords)
    participant DISP as GovernanceOutboxDispatcher
    participant GOV as GovernanceStore
    participant EXE as IApprovalDecisionExecutor (target owner)

    Note over M,GDB: Phase A — maker ยื่นคำขอ
    M->>SPA: ขอเปลี่ยน (เช่น หมุน secret)
    SPA->>API: POST /api/v1/.../*-requests + If-Match + Idempotency-Key + X-CSRF-Token
    API->>OWN: RequestAsync(id, expectedVersion, actorId, key)
    OWN->>GDB: transaction: OperationRecord + mark target pending + outbox ApprovalRequested
    GDB-->>OWN: commit
    OWN-->>API: approvalId, status pending
    API-->>SPA: 202 + approvalId
    Note over DISP,GOV: Phase B — governance รับคำขอ (async)
    DISP->>GDB: lease batch (READPAST, UPDLOCK)
    DISP->>GOV: Publish ApprovalRequested
    GOV->>GDB: ApprovalRequest Pending v1 + ApprovalEvent requested + audit approval.created
    Note over K,GDB: Phase C — checker ตัดสิน
    K->>SPA: เปิดรายการคำขอ
    SPA->>API: GET /api/v1/approvals/{approvalId}
    API-->>SPA: 200 ApprovalDetail + ETag v1
    SPA->>API: POST /api/v1/approvals/{approvalId}/approve + If-Match v1 + Idempotency-Key + body reason, targetVersion
    API->>GOV: DecideAsync(intent)
    GOV->>GDB: app lock + lookup OperationRecord
    alt checker เป็น maker / นอก scope / ไม่มี permission ของ action
        GOV-->>API: GovernanceAccessDeniedException
        API-->>SPA: 403 ProblemDetails (maker_cannot_decide / merchant_scope_forbidden / underlying_permission_forbidden)
    else status ไม่ Pending / version stale / targetVersion เปลี่ยน
        GOV-->>API: ConflictException
        API-->>SPA: 409 ProblemDetails (approval_not_pending / target_version_changed)
    else ผ่าน
        GOV->>GDB: Approved v2 + ApprovalEvent decided + outbox ApprovalDecided + audit + OperationRecord 202
        GOV-->>API: ApprovalDetail
        API-->>SPA: 202 + ETag v2
    end
    Note over DISP,EXE: Phase D — execute (async)
    DISP->>EXE: Publish ApprovalDecided (executor ตรง TargetType 1 ตัวพอดี)
    alt approved
        EXE->>GDB: apply change ใน transaction ของ owner (ตรวจ TargetVersion อีกครั้ง)
    else rejected
        EXE->>GDB: คืน target จาก pending
    end
    EXE->>GDB: outbox ApprovalExecutionReported
    DISP->>GOV: Publish ApprovalExecutionReported
    GOV->>GDB: status Succeeded / Failed / Unknown + audit approval.executed
```

---

## 0.8 Background dispatch / outbox

handler commit state + outbox row ใน transaction เดียวแล้วตอบ, OutboxDispatcher ใน background scope lease แถวแบบ at-least-once แล้ว publish ให้ consumer ต่อ merchant (source: `Persistence.MerchantRuntime/Outbox/EfOutbox.cs:25-44`, `Outbox/OutboxDispatcher.cs:66-164`, `src/Api/Api/BackgroundDispatch/BackgroundDispatchScope.cs:16-17`, `Program.cs:268-271,343-346`)

```mermaid
sequenceDiagram
    autonumber
    participant H as Endpoint handler<br/>(HTTP scope)
    participant DB as DB (txn.* + txn.OutboxMessages)
    participant D as OutboxDispatcher<br/>(background scope)
    participant CONS as Consumer<br/>(OrderPaidConsumer / Notification / Webhook)
    participant EXT as ปลายทางภายนอก<br/>(PSP / Email / SMS / merchant webhook)

    Note over H,DB: Phase A — commit state + outbox ใน transaction เดียว
    H->>DB: UPDATE state + INSERT OutboxMessages (EventId, MerchantId, Type, Payload)
    DB-->>H: commit
    H-->>H: ตอบ 2xx ให้ caller
    Note over D,EXT: Phase B — drain แบบ at-least-once
    loop ทุก 2s
        D->>DB: UPDATE TOP(50) lease (READPAST, UPDLOCK) WHERE ProcessedAt IS NULL AND Attempts น้อยกว่า 8
        DB-->>D: leased ids + MerchantId
        D->>D: scope ใหม่ต่อ message + IActorScope.Begin(MerchantId)
        D->>CONS: Publish(event)
        CONS->>EXT: ส่งงาน (ถ้ามี)
        alt สำเร็จ
            EXT-->>CONS: OK
            CONS-->>D: done
            D->>DB: MarkProcessed
        else ล้มเหลว
            CONS-->>D: exception
            D->>DB: MarkFailed (Attempts +1) รอ lease รอบถัดไป
        end
    end
```

---

## 0.9 Error contract

API error ทุกชนิดออกเป็น ProblemDetails (explicit หรือผ่าน exception handler กลาง), OIDC callback ตอบ 302 ไป returnTo หรือ error page `?reason=`, PSP browser return ตอบ 303 ไป checkout status (source: `src/Api/BuildingBlocks.Web/ProblemDetailsExceptionHandler.cs:30-100`, `Program.cs:701-705,903-979,2425-2444`, `Admins/LoginService.cs:206-229`, `Admins/OidcAuthentication.cs:188-227`, `IdentityAccess/IdentityAccessWiring.cs:155-202`)

```mermaid
sequenceDiagram
    autonumber
    actor U as Caller
    participant C as SPA / integration client / browser
    participant API as API endpoint
    participant EH as ProblemDetailsExceptionHandler<br/>+ UseStatusCodePages
    participant IDP as Microsoft Entra<br/>(external)

    Note over C,EH: Phase A — API error เป็น ProblemDetails เสมอ
    C->>API: request
    alt handler คืน Results.Problem
        API-->>C: 4xx application/problem+json (title, code, traceId)
    else handler throw exception
        API->>EH: exception
        EH->>EH: Map ชนิด exception เป็น status + fixed detail + code
        EH-->>C: 4xx / 5xx application/problem+json (traceId)
    else framework bare 401 / 403 / 404
        API->>EH: status code page
        EH-->>C: ProblemDetails ของ status นั้น
    end
    Note over C,IDP: Phase B — browser callback ตอบด้วย redirect
    C->>API: GET /api/v1/{console}/auth/{provider}/login?returnTo=/x
    API-->>C: 302 ไป IdP (Authorization Code + PKCE)
    C->>IDP: login
    IDP-->>C: 302 กลับ callback path
    C->>API: GET callback?code=...&state=...
    alt resolve / bind สำเร็จ
        API-->>C: Set-Cookie session + csrf, 302 ไป returnTo (ReturnUrlPolicy)
    else ปฏิเสธ
        API-->>C: 302 ไป error page ?reason=code (ไม่มี body JSON)
    end
    Note over C,API: Phase C — PSP browser return
    C->>API: GET / POST /api/v1/payment-returns/{providerCode}?state=...
    alt binding ถูกต้อง
        API-->>C: Set-Cookie checkout_status, 303 Location /api/v1/checkout/status
    else binding ผิด / หมดอายุ
        API-->>C: 401 ProblemDetails checkout_return_invalid
    end
```

---

## Notes

- Deviations ของ cross-cutting อยู่ที่ `00-cross-cutting.activities.md` (group `/admins` ติด `RequireCsrf()` ทั้ง group) ไฟล์นี้ไม่ทำซ้ำ
- ลำดับ participant สะท้อน middleware จริง: UseRateLimiter, UseAuthentication, UseIdentityAccess, UseAuthorization แล้ว endpoint filters ตามลำดับที่ endpoint ต่อ chain (`Program.cs:711-714`)
- § 0.7 / § 0.8 มี phase async: ลูกศรจาก dispatcher เกิดหลัง HTTP response แล้ว ไม่มี caller รอ, ผลสุดท้ายอ่านได้จาก `GET /api/v1/approvals/{approvalId}` หรือ delivery / transaction status endpoint ของ theme นั้น
- `<br/>` ใน participant alias ใช้ตัดบรรทัดชื่อเท่านั้น ไม่ใช่ path จริง

**Render**: GitHub / Obsidian / VS Code Mermaid
