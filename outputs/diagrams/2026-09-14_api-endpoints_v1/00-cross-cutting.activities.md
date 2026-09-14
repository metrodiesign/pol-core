# pol-core API — Cross-cutting behaviour (Activity Diagrams)

> Source: `docs/reference/api-endpoints.md` บรรทัด L24 (policy / permission vocabulary) และ source host ที่อ้างต่อ § (`src/Api/Api/Iam/*`, `IdentityAccess/*`, `Admins/*`, `Merchants/*`, `Webhooks/RateLimiting.cs`, `ConcurrencyEtags.cs`, `SfsQueryParser.cs`, `Governance/GovernanceEndpoints.cs`, `BackgroundDispatch/*`, `BuildingBlocks.Web/ProblemDetailsExceptionHandler.cs`)
> Scope: 9 § พฤติกรรมร่วมที่ทุก theme อ้างแทนการวาดซ้ำ (authn/authz, CSRF, rate limit, ETag/idempotency, SFS, maker-checker, outbox, error contract) ไม่มี endpoint ที่เป็น subject ของไฟล์นี้โดยตรง
> Generated: 2026-09-14

| § | Diagram | Endpoints |
| --- | --- | --- |
| 0.1 | Console session authentication + authorization | ทุก endpoint policy `admin` / `merchant-user` / `dual-console` ที่ต่อ RequirePermission, RequireAudiencePermission, RequirePlatformUserTier, BoundFilter |
| 0.2 | Identity platform (Bearer) authentication + identity-order permission | policy `identity-platform` / `admin-or-identity-order` และแถวที่มี `(identity: order.read / order.write / checkout.write)` |
| 0.3 | CSRF double-submit | merchant-user unsafe method ที่มี CSRF filter (RequireUserCsrf, RequireAudienceCsrf, RequireAdminOrIdentityMutation, RequireIdentityPlatformMutation) — admin เป็น Bearer ไม่มี CSRF |
| 0.4 | Rate limiting | แถวที่ระบุ `rate limit` (policy customer-payment, merchant-user-auth, psp-webhook) — admin-auth ถูก retire พร้อม route /admins/auth/** |
| 0.5 | ETag / If-Match / Idempotency-Key | mutation ที่มี IfMatchMutationMarker, AdminIfMatchMutationMarker, IdempotencyMutationMarker, GovernanceDecisionMarker และ GET detail ที่คืน ETag |
| 0.6 | SFS query parsing | GET list ที่อ่าน page / limit / filters / sort / search ผ่าน SfsQueryParser |
| 0.7 | Maker-checker approval | endpoint `*-requests`, `*-change-requests`, `activation-requests`, `secret-rotation-requests` และ `/approvals/{approvalId}/approve` / `reject` |
| 0.8 | Background dispatch / outbox | handler ที่ enqueue outbox หรือทิ้งงานให้ worker (PaymentPaid, notification delivery, webhook delivery, PSP inquiry) |
| 0.9 | Error contract | ทุก endpoint (ProblemDetails JSON) และ browser callback / return (302 reason, 303 checkout status) |

---

## 0.1 Console session authentication + authorization

ทุก request ใต้ policy `admin` / `merchant-user` / `dual-console` / `admin-or-identity-order` ผ่าน `ConsoleSession` policy scheme ที่ forward ไป real scheme ตาม policy + audience: Admin ใช้ `PlatformToken` (employee Bearer JWT) และ Merchant ใช้ `MerchantUserSession` (cookie) แล้ว re-resolve บัญชีสดต่อ request ก่อนถึง permission gate แบบ fail-closed (source: `src/Api/Api/Iam/ConsoleSessionAuthentication.cs:38-67`, `IdentityAccess/PlatformTokenAuthentication.cs:41-109`, `Merchants/UserSessionAuthenticationHandler.cs:70-175`, `Iam/PermissionAuthorization.cs:86-139`, `Admins/HostWiring.cs:101-116`, `Merchants/UserPermissionAuthorization.cs:16-24`, `Program.cs:660-663`)

```mermaid
flowchart TD
    START((●)) --> MW["middleware: UseRateLimiter ดู § 0.4<br/>UseAuthentication, UseIdentityAccess, UseAuthorization"]
    MW --> SEL{"policy ของ endpoint?"}
    SEL -->|admin| AUD_A["ConsoleSession.SelectScheme: audience = Admin"]
    SEL -->|merchant-user| AUD_M["audience = Merchant"]
    SEL -->|dual-console| DUAL{"มี Bearer และไม่มี __Host-mch_session?"}
    DUAL -->|yes| AUD_A
    DUAL -->|no| AUD_M
    SEL -->|admin-or-identity-order| AUD_A
    AUD_A --> IDR{"identity-order route และ identity request (Bearer)?"}
    IDR -->|yes| GO_ID["re-route ไป PlatformToken (audience Merchant)<br/>เส้น identity ดู § 0.2"]
    IDR -->|no| PT["PlatformToken scheme:<br/>OpenIddict validate JWT (signature, audience api, lifetime, token entry)"]
    PT --> PT_OK{"account Active และ<br/>AuthorizationVersion เท่ากับ authz_version ใน token?"}
    PT_OK -->|no| R401_A["401 Bearer error invalid_token<br/>SPA refresh หรือ login ใหม่"]
    PT_OK -->|yes| ADM_AUD{"audience Admin?"}
    ADM_AUD -->|no| BIND_ID["binding เส้น identity / order ดู § 0.2"]
    ADM_AUD -->|yes| BINDCHK{"account เป็น Employee และมี authorization snapshot?"}
    BINDCHK -->|no| R401_A
    BINDCHK -->|yes| BIND_A["bind IAdminScope: AdminId = AccountId,<br/>Permissions จาก platform-role,<br/>Tier Super = access.PlatformAccess active มิฉะนั้น Scoped"]
    AUD_M --> MCH_COOKIE{"มี __Host-mch_session?"}
    MCH_COOKIE -->|no| R401_M["401 (default challenge)"]
    MCH_COOKIE -->|yes| MCH_H["MerchantUserSession handler<br/>hash lookup + expiry<br/>re-resolve user READ-ONLY"]
    MCH_H --> MCH_LIFE{"user Active และ bound merchant?"}
    MCH_LIFE -->|no| R403_L["403 ProblemDetails<br/>code awaiting-approval / rejected / suspended / unbound"]
    MCH_LIFE -->|yes| MCH_OK{"session live, ไม่ reuse?"}
    MCH_OK -->|no| R401_M
    MCH_OK -->|yes| BIND_M["bind IUserScope<br/>claims merchant_id, sale_code<br/>rotate cookie / slide idle"]
    BIND_A --> FILTERS["endpoint filters ตามลำดับ chain ของ endpoint<br/>CSRF ดู § 0.3 (admin เป็น Bearer ไม่มี CSRF)"]
    BIND_M --> FILTERS
    FILTERS --> BOUND{"BoundFilter (group /merchants/users):<br/>scope ของ audience bound?"}
    BOUND -->|no| R403_B["403 The selected console account is not active"]
    BOUND -->|yes| PERM{"RequirePermission:<br/>scope ที่ bind มี key?"}
    PERM -->|no| R403_P["403 You do not have permission for this action"]
    PERM -->|yes| AUDP{"RequireAudiencePermission:<br/>Admin ใช้ adminKey, Merchant ใช้ merchantKey"}
    AUDP -->|no| R403_P
    AUDP -->|yes| TIER{"RequirePlatformUserTier:<br/>IAdminScope.Tier ∈ allowed?"}
    TIER -->|no| R403_T["403 code super_required"]
    TIER -->|yes| HANDLER["handler ทำงาน"]
    HANDLER --> END_S((◉))
    GO_ID --> END_S
    BIND_ID --> END_S
    R401_A --> END_F((◉))
    R401_M --> END_F
    R403_L --> END_F
    R403_B --> END_F
    R403_P --> END_F
    R403_T --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class BIND_A,BIND_M,HANDLER,END_S ok
    class R401_A,R401_M,R403_L,R403_B,R403_P,R403_T,END_F fail
    class SEL,DUAL,IDR,PT_OK,ADM_AUD,BINDCHK,MCH_COOKIE,MCH_LIFE,MCH_OK,BOUND,PERM,AUDP,TIER gate
```

| Gate | ใช้กับ | ผลเมื่อไม่ผ่าน | source |
| --- | --- | --- | --- |
| policy `admin` | `PlatformToken` (employee Bearer JWT) เท่านั้น ไม่มี cookie fallback | 401 `invalid_token` (account ไม่ Active หรือ version ไม่ตรง), 401 OpenIddict (JWT เสีย) | `Iam/ConsoleSessionAuthentication.cs:44-67`, `IdentityAccess/PlatformTokenAuthentication.cs:52-72` |
| policy `merchant-user` | MerchantUserSession cookie เท่านั้น (ไม่มี Bearer fallback) | 401 default หรือ 403 lifecycle code | `Merchants/UserSessionAuthenticationHandler.cs:157-175,249-252` |
| policy `dual-console` | Bearer ไม่มี mch cookie = Admin (PlatformToken), มิฉะนั้น Merchant (cookie) | ตาม audience ที่เลือก | `Iam/ConsoleSessionAuthentication.cs:50-52,72-74` |
| `RequirePermission(key)` | IAdminScope ก่อน แล้ว IUserScope, ไม่มี scope = 403 | 403 ProblemDetails | `Iam/PermissionAuthorization.cs:86-106` |
| `RequireAudiencePermission(adminKey, merchantKey)` | เฉพาะ dual-console, key คนละฝั่งต่อ audience | 403 ProblemDetails | `Iam/PermissionAuthorization.cs:113-139` |
| `RequirePlatformUserTier(Tier.Super)` | `IAdminScope.Current.Tier` resolve สดจาก auth handler (ไม่ใช่ claim), Super = access.PlatformAccess active | 403 `super_required` | `Admins/HostWiring.cs:101-116` |
| `BoundFilter` | group `/merchants/users` (dual-console + merchant-user) | 403 | `Merchants/UserPermissionAuthorization.cs:16-24` |

---

## 0.2 Identity platform (Bearer) authentication + identity-order permission

policy `identity-platform` รับเฉพาะ platform JWT (Bearer, OpenIddict) ผ่าน `PlatformToken` scheme — employee, agent และ SYSTEM client ใช้เส้นนี้ทั้งหมด ไม่มี BFF cookie แล้ว (BFF session ถูก retire) route ที่มี identity-order marker สลับจาก console ไปเส้นนี้เมื่อ request เป็น identity request (Bearer) โดย access token ผูก AuthorizationVersion ของบัญชี (source: `src/Api/Api/IdentityAccess/IdentityAccessWiring.cs:96-125`, `IdentityAccess/PlatformTokenAuthentication.cs:41-109`, `IdentityAccess/IdentityAccessAuthorization.cs:8-96`, `Iam/IdentityPermissionAuthorization.cs:16-96`, `Iam/OrderIdentityAccessScope.cs:17-27`, `src/Infrastructure/Persistence/Persistence.ControlPlane/OpenIddictRegistration.cs:28-75`)

```mermaid
flowchart TD
    START((●)) --> AMB{"UseIdentityAccess: identity-order route<br/>และมี Bearer พร้อม __Host-mch_session (merchant-user cookie)?"}
    AMB -->|yes| R400["400 ProblemDetails<br/>code ambiguous_authentication_context"]
    AMB -->|no| POL{"policy identity-platform<br/>(หรือ dual-console / admin-or-identity-order ที่เป็น identity request)?"}
    POL -->|yes| BEARER["PlatformToken scheme -> OpenIddict validation<br/>local server, audience api, token entry validation<br/>access token อายุ 5 นาที"]
    BEARER --> ACC{"account Active และ<br/>AuthorizationVersion เท่ากับ authz_version ใน token?"}
    ACC -->|no| R401["401 invalid_token"]
    ACC -->|yes| ADMAUD{"audience Admin (admin console)?"}
    ADMAUD -->|yes| BINDADM{"TryBindAdminScope: Employee และมี snapshot?"}
    BINDADM -->|no| R401
    BINDADM -->|yes| REQ
    ADMAUD -->|no| REQ["IdentityAccessRequirement (policy identity-platform):<br/>token_context PLATFORM = Employee + HasPlatformAccess,<br/>client_id มี = client + account Active,<br/>merchant_id = ResolveAuthorization(merchant) ตรง + Active,<br/>required_permission claim อยู่ใน Permissions"]
    REQ --> REQ_OK{"ทุกข้อผ่าน?"}
    REQ_OK -->|no| R403["403 (authorization fail)"]
    REQ_OK -->|yes| ORDER{"endpoint มี RequireOrderIdentityPermission /<br/>RequireIdentityPermission?"}
    ORDER -->|no| HANDLER
    ORDER -->|"เป็น console request (ไม่ใช่ Bearer)"| CONSOLE["fallback PermissionAuthorization.IsAllowed ตาม § 0.1"]
    CONSOLE --> HANDLER
    ORDER -->|"เป็น identity request (Bearer)"| RESOLVE["ResolveMerchantAsync(principal)<br/>account + client + merchant_id + snapshot"]
    RESOLVE --> MCTX{"merchant context ได้?"}
    MCTX -->|"ไม่มี merchant_id claim"| R403_M["403 ProblemDetails<br/>code merchant_context_missing"]
    MCTX -->|"resolve ล้ม"| R403
    MCTX -->|yes| SCOPE{"system token (client_id): scope claim มี systemScope<br/>human: Permissions มี humanPermission?"}
    SCOPE -->|no| R403
    SCOPE -->|yes| PROOF["CommerceAuthorizationProof ใน HttpContext.Items<br/>IActorScope.Begin(merchantId, accountId)<br/>IOrderIdentityAccessScope.Begin(accountId, snapshot)"]
    PROOF --> HANDLER["handler ทำงาน<br/>(console request บน route เดียวกันใช้ § 0.1)"]
    HANDLER --> END_S((◉))
    R400 --> END_F((◉))
    R401 --> END_F
    R403 --> END_F
    R403_M --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef ext fill:#4a3b0f,stroke:#e3b341,color:#fff
    class PROOF,CONSOLE,HANDLER,END_S ok
    class R400,R401,R403,R403_M,END_F fail
    class AMB,POL,ACC,ADMAUD,BINDADM,REQ_OK,ORDER,MCTX,SCOPE gate
    class BEARER ext
```

| Gate | human (employee / agent Bearer) | SYSTEM client (Bearer) | source |
| --- | --- | --- | --- |
| `RequireOrderIdentityPermission(payment.view, order.read)` | Permissions ต้องมี `payment.view` | scope claim ต้องมี `order.read` | `Iam/IdentityPermissionAuthorization.cs:24-40,56-96` |
| `RequireOrderIdentityPermission(payment.create, order.write)` | `payment.create` | `order.write` | เดียวกัน |
| `RequireOrderIdentityPermission(payment.create, checkout.write)` | `payment.create` | `checkout.write` | เดียวกัน |
| `RequireIdentityPermission(payment.create)` (POST /orders canonical) | `payment.create` | `order.write` คงที่ | `Iam/IdentityPermissionAuthorization.cs:43-49` |
| console request (ไม่มี Bearer) บน route เดียวกัน | ใช้ `PermissionAuthorization.IsAllowed` ตาม § 0.1 | ไม่มี | `Iam/IdentityPermissionAuthorization.cs:29-39` |

---

## 0.3 CSRF double-submit

unsafe method (POST / PUT / PATCH / DELETE) บน cookie session (merchant-user) ต้องส่ง `X-CSRF-Token` เท่ากับ cookie `mch_csrf` (double-submit) — admin console เป็น Bearer JWT ล้วน cross-site แนบ Bearer ไม่ได้ จึงไม่มี CSRF filter ฝั่ง admin; identity-platform mutation ตรวจแค่ว่ามี Bearer และ boot guard บังคับให้ทุก unsafe endpoint ใต้ cookie policy มี filter ฝั่งถูกต้อง (source: `src/Api/Api/Merchants/UserCsrfFilter.cs:11-40`, `Iam/CsrfParity.cs:11-125`, `IdentityAccess/BffCsrfFilter.cs:5-27`, `Program.cs:3007`)

```mermaid
flowchart TD
    START((●)) --> SAFE{"method เป็น GET / HEAD / OPTIONS / TRACE?"}
    SAFE -->|yes| PASS["ข้าม CSRF (filter ไม่ตรวจ)"]
    SAFE -->|no| KIND{"CSRF filter ที่ endpoint ต่อ chain?"}
    KIND -->|"RequireUserCsrf (merchant-user)"| MCH["UserCsrfFilter: cookie mch_csrf"]
    KIND -->|"RequireAudienceCsrf (dual-console)"| AUD{"identity request (Bearer)?"}
    KIND -->|"RequireIdentityPlatformMutation / RequireAdminOrIdentityMutation (Bearer)"| BEARER{"Authorization Bearer?"}
    BEARER -->|yes| PASS
    BEARER -->|no| R401["401 ProblemDetails code bearer_required"]
    AUD -->|yes| PASS
    AUD -->|no| SELAUD{"SelectedConsoleAudience?"}
    SELAUD -->|Admin| PASS
    SELAUD -->|Merchant| MCH
    SELAUD -->|none| R403_AUD["403 No authenticated console audience is bound"]
    MCH --> CMP{"cookie mch_csrf และ header X-CSRF-Token<br/>ไม่ว่างและเท่ากัน (constant-time)?"}
    CMP -->|no| R403["403 ProblemDetails Missing or invalid CSRF token"]
    CMP -->|yes| PASS
    PASS --> NEXT["filter ถัดไป / handler"]
    NEXT --> END_S((◉))
    R401 --> END_F((◉))
    R403 --> END_F
    R403_AUD --> END_F
    BOOT["boot: CsrfParity.Assert<br/>unsafe endpoint ใต้ cookie policy ต้องมี CsrfProtected ของ scheme ตัวเอง<br/>ยกเว้น PlatformToken (Bearer ไม่มีอะไรให้ปลอม)<br/>ผิดฝั่งหรือขาด = boot ล้ม (ไม่ใช่ runtime)"]

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class PASS,NEXT,END_S ok
    class R401,R403,R403_AUD,END_F fail
    class SAFE,KIND,AUD,BEARER,SELAUD,CMP,BOOT gate
```

| Filter | ตรวจ | ผลเมื่อไม่ผ่าน | ใช้กับ policy | source |
| --- | --- | --- | --- | --- |
| `RequireUserCsrf()` | cookie `mch_csrf` เท่ากับ header `X-CSRF-Token` (constant-time) | 403 Missing or invalid CSRF token | `merchant-user` | `Merchants/UserCsrfFilter.cs:23-33`, `Iam/CsrfParity.cs:22-24` |
| `RequireAudienceCsrf()` | identity request (Bearer) ผ่าน; audience Admin ผ่าน; audience Merchant รัน double-submit `mch_csrf` | 403 (Merchant ไม่ผ่าน หรือไม่มี audience) | `dual-console` | `Iam/CsrfParity.cs:26-33,44-66` |
| `RequireAdminOrIdentityMutation()` | ทั้งสอง audience เป็น Bearer จึงผ่าน (บันทึก marker IdentityPlatform) | ผ่าน | `admin-or-identity-order` | `Iam/CsrfParity.cs:35-42` |
| `RequireIdentityPlatformMutation()` | ต้องมี Authorization Bearer | 401 `bearer_required` | `identity-platform` | `IdentityAccess/BffCsrfFilter.cs:8-26` |
| boot guard `CsrfParity.Assert` | ทุก unsafe endpoint ใต้ cookie policy ต้องมี filter ฝั่งตัวเอง ยกเว้น PlatformToken (Bearer) | boot fail | ทุก policy | `Iam/CsrfParity.cs:82-90,94-125` |

---

## 0.4 Rate limiting

UseRateLimiter รันก่อน UseAuthentication, ทุก policy เป็น sliding window ต่อ source IP ไม่รอคิว และตอบ 429 พร้อม Retry-After จาก OnRejected กลางที่ตั้งครั้งเดียว (source: `src/Api/Api/Webhooks/RateLimiting.cs:22-58`, `Customers/PaymentRateLimiting.cs:19-38`, `Merchants/UserAuthRateLimiting.cs:13-32`, `Program.cs:711,802-1089,1305,1903-1938,2612,2746,2794`)

```mermaid
flowchart TD
    START((●)) --> MW["UseRateLimiter ก่อน UseAuthentication<br/>partition = source IP (RemoteIpAddress หรือ unknown)"]
    MW --> POL{"endpoint RequireRateLimiting policy?"}
    POL -->|"ไม่มี"| PASS["ไม่จำกัด"]
    POL -->|customer-payment| P1["sliding 10 req / 60s, 6 segments<br/>checkout access / confirm / status / summary / verify,<br/>orders/{token}/pay, payment-status, payment-returns"]
    POL -->|merchant-user-auth| P3["sliding 20 req / 60s, 6 segments<br/>POST merchants/users/register, GET merchants/auth/{provider}/login,<br/>POST merchants/auth/logout"]
    POL -->|psp-webhook| P4["sliding 60 req / 10s, 5 segments<br/>POST webhooks/{pspConnectionId},<br/>POST webhooks/payment-providers/{providerAccountId}"]
    P1 --> LEASE{"acquire permit ได้?<br/>QueueLimit 0 ไม่รอคิว"}
    P3 --> LEASE
    P4 --> LEASE
    LEASE -->|no| R429["429 + header Retry-After<br/>จาก limiter estimate หรือ 2s (OnRejected กลาง)"]
    LEASE -->|yes| PASS
    PASS --> AUTH["UseAuthentication ต่อ ดู § 0.1 / § 0.2"]
    AUTH --> END_S((◉))
    R429 --> END_F((◉))

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class PASS,AUTH,END_S ok
    class R429,END_F fail
    class POL,LEASE gate
```

| Policy | PermitLimit / Window | partition | endpoint | source |
| --- | --- | --- | --- | --- |
| `customer-payment` | 10 / 60s (6 segments) | source IP | checkout access, confirm, status, summary, verify, `orders/{token}/pay`, `payment-status`, `payment-returns` GET+POST | `Customers/PaymentRateLimiting.cs:23-37`, `Program.cs:802-1089,1903-1938` |
| `merchant-user-auth` | 20 / 60s (6 segments) | source IP | `POST merchants/users/register`, `GET merchants/auth/{provider}/login`, `POST merchants/auth/logout` | `Merchants/UserAuthRateLimiting.cs:17-31`, `Program.cs:2612,2746,2794` |
| `psp-webhook` | 60 / 10s (5 segments) | source IP (ไม่ใช่ pspConnectionId) | `POST webhooks/{pspConnectionId}`, `POST webhooks/payment-providers/{providerAccountId}` | `Webhooks/RateLimiting.cs:31-44`, `Program.cs:1062,1305` |

---

## 0.5 ETag / If-Match / Idempotency-Key

GET detail คืน ETag รูป `"vN"` แล้ว mutation ส่ง `If-Match` กลับพร้อม `Idempotency-Key`, executor เก็บ record ต่อ (merchant, actor, operation, key) ใน transaction เดียวกับ mutation เพื่อ replay หรือปฏิเสธ key ที่ reuse, version ไม่ตรง = 409 (source: `src/Api/Api/ConcurrencyEtags.cs:14-42`, `Program.cs:1526-1560,3835-3841,3967-3982`, `src/Infrastructure/Persistence/Persistence.MerchantRuntime/Idempotency/AdminOperationExecutor.cs:24-60`, `Persistence.ControlPlane/Governance/ControlPlaneOperationExecutor.cs:43-82`, `src/Api/BuildingBlocks.Web/ProblemDetailsExceptionHandler.cs:71-100`)

```mermaid
flowchart TD
    START((●)) --> GETD["GET detail: handler ตั้ง header ETag = vN<br/>(VersionEtags.Set / GovernanceEtagMarker)"]
    GETD --> MUT["mutation ที่มี If-Match / Idempotency marker<br/>gate auth + CSRF ดู § 0.1 / § 0.3"]
    MUT --> ADMONLY{"AdminIfMatchMutationMarker และ audience Merchant?"}
    ADMONLY -->|yes| DIRECT["merchant session: ส่ง command ตรง<br/>ไม่ต้องมี If-Match / Idempotency-Key"]
    ADMONLY -->|no| IFM{"endpoint ต้อง If-Match:<br/>รูป quoted vN (VersionEtags.Require)?"}
    IFM -->|no| R400_E["400 ProblemDetails<br/>code invalid_etag"]
    IFM -->|yes| IDEM{"endpoint ต้อง Idempotency-Key:<br/>ไม่ว่าง, ไม่เกิน 200 ตัว, ไม่มี control char?"}
    IDEM -->|no| R400_I["400 ProblemDetails<br/>code invalid_idempotency_key"]
    IDEM -->|yes| USEEXEC{"endpoint ใช้ operation executor?"}
    USEEXEC -->|no| RUN_CMD["ส่ง command พร้อม expectedVersion"]
    USEEXEC -->|yes| EXEC["executor ใน transaction เดียว<br/>AdminOperationExecutor (txn) / ControlPlaneOperationExecutor (admin)<br/>key = merchant + actor + operation + Idempotency-Key"]
    EXEC --> PRIOR{"มี OperationRecord เดิม?"}
    PRIOR -->|yes| HASHOK{"intent hash ตรง?"}
    HASHOK -->|no| R409_K["409 ProblemDetails<br/>code idempotency_key_reused"]
    HASHOK -->|yes| DONE{"record Succeeded และมี response?"}
    DONE -->|no| R409_P["409 ProblemDetails<br/>code operation_in_progress"]
    DONE -->|yes| REPLAY["คืน response เดิม (Replayed = true)<br/>+ ETag เดิม"]
    PRIOR -->|no| RUN["insert record แล้วรัน action<br/>ด้วย expectedVersion"]
    RUN --> VER{"version ของ resource ตรง?"}
    RUN_CMD --> VER
    VER -->|no| R409_C["409 ProblemDetails Conflict<br/>ConcurrencyConflictException"]
    VER -->|yes| SAVE["persist mutation + record.Succeed<br/>commit"]
    SAVE --> RESP["200 / 202 / 204 + ETag = vN ใหม่"]
    DIRECT --> RESP2["200 ไม่มี ETag"]
    RESP --> END_S((◉))
    RESP2 --> END_S
    REPLAY --> END_S
    R400_E --> END_F((◉))
    R400_I --> END_F
    R409_K --> END_F
    R409_P --> END_F
    R409_C --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    class SAVE,RESP,RESP2,END_S ok
    class R400_E,R400_I,R409_K,R409_P,R409_C,END_F fail
    class ADMONLY,IFM,IDEM,USEEXEC,PRIOR,HASHOK,DONE,VER gate
    class REPLAY warn
```

| Marker | ความหมาย | header ที่บังคับ | source |
| --- | --- | --- | --- |
| `EtagResponseMarker(status)` | response นั้นมี ETag | ไม่มี | `ConcurrencyEtags.cs:7,46-47` |
| `IfMatchMutationMarker(status)` | mutation ต้องส่ง If-Match, คืน ETag ใหม่ | `If-Match` | `ConcurrencyEtags.cs:8,49-54` |
| `IdempotencyMutationMarker` | mutation ต้องส่ง Idempotency-Key | `Idempotency-Key` | `ConcurrencyEtags.cs:9,56-57` |
| `AdminIfMatchMutationMarker` / `AdminIdempotencyMutationMarker` | บังคับเฉพาะ audience Admin, merchant session ข้าม | If-Match / Idempotency-Key (Admin เท่านั้น) | `ConcurrencyEtags.cs:10-12,62-71`, `Program.cs:1531-1552` |
| `GovernanceDecisionMarker(202)` | approve / reject ต้องส่งทั้งสอง header | `If-Match` + `Idempotency-Key` | `Governance/GovernanceEndpoints.cs:37-43,126-144` |

---

## 0.6 SFS query parsing

GET list ที่มี `SfsQueryParamsMarker` อ่าน page / limit / filters / sort / search จาก query string, clamp paging โดยไม่ตอบ 400, ส่วน JSON ผิดรูปหรือเกิน cap เป็น ArgumentException ที่ handler กลาง map เป็น 400 (source: `src/Api/Api/SfsQueryParser.cs:16-85`, `SfsOpenApi.cs:11-25`, `Governance/GovernanceEndpoints.cs:184-206,286`, `src/Api/BuildingBlocks.Web/ProblemDetailsExceptionHandler.cs:85-88`)

```mermaid
flowchart TD
    START((●)) --> LIST["GET list endpoint ที่มี SfsQueryParamsMarker<br/>gate auth ดู § 0.1 / § 0.2"]
    LIST --> PARSE["SfsQueryParser.Parse(query, maxLimit)"]
    PARSE --> PAGING["clamp limit เป็น 1..maxLimit (default 25)<br/>clamp page ให้ ≥ 1 และไม่เกิน offset ceiling<br/>ไม่มี 400 จากค่าเพี้ยน"]
    PAGING --> JSON{"filters / sort / search<br/>เป็น JSON ถูกรูป?"}
    JSON -->|no| R400["400 ProblemDetails Invalid request<br/>(ArgumentException Malformed SFS query parameter)"]
    JSON -->|yes| CAPS{"filters ≤ 50, values ≤ 200 ต่อ filter,<br/>sort keys ≤ 10?"}
    CAPS -->|no| R400_C["400 ProblemDetails Invalid request<br/>(Too many filters / filter values / sort keys)"]
    CAPS -->|yes| QUERY["handler สร้าง query ตาม scope ของ caller<br/>ThenBy(Id) ปิด chain ทุกครั้ง"]
    QUERY --> RESP["200 PagedResult (items, page, limit, total)"]
    RESP --> END_S((◉))
    R400 --> END_F((◉))
    R400_C --> END_F
    VAR["variant: products list ใช้ ParsePaging + productFilters typed (ไม่มี filters / sort / search)<br/>approvals / audits ใช้ typed filter เอง: page ≥ 1, limit 1..100 ไม่งั้น 400 invalid_filter"]

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class QUERY,RESP,END_S ok
    class R400,R400_C,END_F fail
    class JSON,CAPS,VAR gate
```

---

## 0.7 Maker-checker approval

maker ยื่นคำขอผ่าน endpoint `*-requests` ที่ stage target เป็น pending และเขียน ApprovalRequested ลง governance outbox ใน transaction เดียว, checker คนละคนตัดสินผ่าน `/approvals/{approvalId}/approve` หรือ `reject` ด้วย If-Match + Idempotency-Key แล้ว executor ของ target owner apply ผลแบบ async และรายงานกลับเป็น Succeeded / Failed / Unknown (source: `src/Api/Api/Governance/GovernanceEndpoints.cs:123-182`, `src/Application/Modules/Governance.Application/GovernanceHandlers.cs:26-75`, `src/Domain/Modules/Governance.Domain/ApprovalRequest.cs:74-113`, `src/Infrastructure/Persistence/Persistence.ControlPlane/Governance/GovernanceStore.cs:72-196`, `GovernanceOutboxDispatcher.cs:44-140`, `Iam/ApiClientStore.cs:135-170`, `Iam/ApiClientApprovalExecutor.cs:25-80`)

```mermaid
flowchart TD
    START((●)) --> MAKER["maker เรียก endpoint *-requests<br/>gate policy admin + permission + CSRF ดู § 0.1 / § 0.3<br/>If-Match + Idempotency-Key ดู § 0.5"]
    MAKER --> EXEC["ControlPlaneOperationExecutor ใน transaction เดียว<br/>ตรวจ target version แล้ว mark target pending"]
    EXEC --> VER{"expectedVersion ตรง<br/>และ target ไม่ pending อยู่แล้ว?"}
    VER -->|no| R409_M["409 ProblemDetails<br/>ConcurrencyConflict / state_conflict"]
    VER -->|yes| OUT1["เขียน ApprovalRequested ลง admin.GovernanceOutboxMessages<br/>commit พร้อม pending state + OperationRecord"]
    OUT1 --> R202_M["202 pending + approvalId"]
    R202_M --> END_S((◉))
    OUT1 -.async.-> DISP1["GovernanceOutboxDispatcher<br/>poll 2s, batch 50, lease 1m, Attempts น้อยกว่า 8"]
    DISP1 --> RECV["GovernanceStore.ReceiveAsync(ApprovalRequested)<br/>dedupe ด้วย SourceEventId"]
    RECV --> PENDING["ApprovalRequest Pending v1<br/>ApprovalEvent requested + audit approval.created"]
    PENDING --> CHECKER["checker GET /approvals/{approvalId}<br/>รับ ETag v1 + targetVersion"]
    CHECKER --> DECIDE["POST /approvals/{approvalId}/approve หรือ /reject<br/>If-Match, Idempotency-Key, body reason + targetVersion<br/>gate admin (Bearer) + settings.manage"]
    DECIDE --> VALID{"If-Match รูป vN, key ไม่เกิน 200,<br/>reason 1..1000, targetVersion 1..200?"}
    VALID -->|no| R400["400 ProblemDetails<br/>code invalid_request"]
    VALID -->|yes| IDEM{"OperationRecord เดิม<br/>(actor, operation, key)?"}
    IDEM -->|"hash ต่าง"| R409_K["409 code idempotency_key_reused"]
    IDEM -->|"InProgress"| R409_P["409 code operation_in_progress"]
    IDEM -->|"Succeeded"| REPLAY["202 response เดิม (Replayed)"]
    IDEM -->|"ไม่มี"| FOUND{"approval พบ?"}
    FOUND -->|no| R404["404 ProblemDetails<br/>code not_found"]
    FOUND -->|yes| SCOPE{"approval.MerchantId อยู่ใน<br/>accessible ของ checker?"}
    SCOPE -->|no| R403_S["403 code merchant_scope_forbidden"]
    SCOPE -->|yes| PERM{"checker มี approval.RequiredPermission?"}
    PERM -->|no| R403_P["403 code underlying_permission_forbidden"]
    PERM -->|yes| RULES{"ApprovalRequest.Decide:<br/>checker ไม่ใช่ maker, status Pending,<br/>version ตรง, targetVersion ตรง?"}
    RULES -->|"maker เอง"| R403_M["403 code maker_cannot_decide"]
    RULES -->|"ไม่ Pending / version stale"| R409_A["409 code approval_not_pending"]
    RULES -->|"targetVersion เปลี่ยน"| R409_T["409 code target_version_changed"]
    RULES -->|yes| DECIDED["status Approved / Rejected, Version++<br/>ApprovalEvent decided, outbox ApprovalDecided,<br/>audit approval.decided, OperationRecord 202"]
    DECIDED --> R202["202 ApprovalDetail + ETag v2"]
    R202 --> END_S
    REPLAY --> END_S
    DECIDED -.async.-> DISP2["GovernanceOutboxDispatcher"]
    DISP2 --> HANDLER["ApprovalDecidedHandler:<br/>ต้องมี IApprovalDecisionExecutor ตรง TargetType 1 ตัวพอดี<br/>ไม่งั้น throw แล้ว retry จนถึง 8 ครั้ง"]
    HANDLER --> DEC{"decision?"}
    DEC -->|rejected| REVERT["executor คืน target จาก pending<br/>(เช่น RejectRotation, ticket.Reject)"]
    DEC -->|approved| APPLY["executor apply การเปลี่ยนแปลงใน transaction ของ owner<br/>ตรวจ TargetVersion อีกครั้ง (ConcurrencyConflict ถ้าเปลี่ยน)"]
    REVERT --> REPORT["outbox ApprovalExecutionReported<br/>(Succeeded / Failed / Unknown)"]
    APPLY --> REPORT
    REPORT -.async.-> DISP3["GovernanceOutboxDispatcher"]
    DISP3 --> FINAL["GovernanceStore.ReceiveAsync(ApprovalExecutionReported)<br/>status Succeeded / Failed / Unknown, Version++<br/>audit approval.executed"]
    FINAL --> END_S
    R409_M --> END_F((◉))
    R400 --> END_F
    R409_K --> END_F
    R409_P --> END_F
    R404 --> END_F
    R403_S --> END_F
    R403_P --> END_F
    R403_M --> END_F
    R409_A --> END_F
    R409_T --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    class OUT1,PENDING,DECIDED,APPLY,FINAL,END_S ok
    class R409_M,R400,R409_K,R409_P,R404,R403_S,R403_P,R403_M,R409_A,R409_T,END_F fail
    class VER,VALID,IDEM,FOUND,SCOPE,PERM,RULES,DEC gate
    class R202_M,R202,REPLAY,REVERT warn
```

| สถานะ ApprovalRequest | เปลี่ยนโดย | ผลต่อ target | source |
| --- | --- | --- | --- |
| Pending (v1) | ReceiveAsync(ApprovalRequested) | target ถูก mark pending โดย owner ตอนยื่นคำขอ | `GovernanceStore.cs:148-173` |
| Approved / Rejected (v2) | DecideAsync ผ่าน approve / reject | ยังไม่ apply, รอ executor | `GovernanceStore.cs:72-146`, `ApprovalRequest.cs:74-98` |
| Succeeded / Failed / Unknown (v3) | ReceiveAsync(ApprovalExecutionReported) | approved = apply แล้ว, rejected = คืน pending, unknown = ต้อง reconcile | `GovernanceStore.cs:175-196`, `ApprovalRequest.cs:100-113` |

---

## 0.8 Background dispatch / outbox

handler ใน HTTP scope เขียน state และแถว outbox ใน transaction เดียวแล้วตอบทันที, dispatcher ใน background scope (ไม่มี HttpContext จึงได้ WorkerActorContext + WorkerWriteAuthorizer) lease แถวแบบ at-least-once แล้ว publish ให้ consumer ต่อ merchant, ล้มเหลวเกิน 8 ครั้งหยุด lease รอ review (source: `src/Application/BuildingBlocks.Application/IOutbox.cs:11-14`, `src/Infrastructure/Persistence/Persistence.MerchantRuntime/Outbox/EfOutbox.cs:25-44`, `Outbox/OutboxDispatcher.cs:21-164`, `src/Api/Api/BackgroundDispatch/BackgroundDispatchScope.cs:14-18`, `WorkerActorContext.cs:15-24`, `WorkerWriteAuthorizer.cs:39-56`, `Program.cs:246-271,341-346`)

```mermaid
flowchart TD
    START((●)) --> HTTP["endpoint handler ใน HTTP scope<br/>IActorContext = HttpActorContext<br/>IWriteAuthorizer = HttpMerchantWriteAuthorizer / ControlPlaneAdminWriteAuthorizer"]
    HTTP --> ACTOR{"actor bound (merchant_id claim<br/>หรือ IActorScope.Begin)?"}
    ACTOR -->|no| ERR["InvalidOperationException<br/>Cannot enqueue without a bound actor (409 ผ่าน § 0.9)"]
    ACTOR -->|yes| ENQ["IOutbox.Enqueue(event)<br/>track แถว txn.OutboxMessages (MerchantId = actor)"]
    ENQ --> SAVE["SaveChanges: state change + outbox row<br/>commit atomically"]
    SAVE --> RESP["HTTP response 2xx (งาน background ยังไม่เริ่ม)"]
    RESP --> END_S((◉))
    SAVE -.async.-> LEASE["OutboxDispatcher (BackgroundService, poll 2s)<br/>lease batch 50 ด้วย READPAST + UPDLOCK<br/>LeaseExpiresAt +1m, Attempts น้อยกว่า 8"]
    LEASE --> SCOPE["scope ใหม่ต่อ message<br/>BackgroundDispatchScope.IsHttpRequest = false<br/>WorkerActorContext + WorkerWriteAuthorizer"]
    SCOPE --> BIND["IActorScope.Begin(message.MerchantId)"]
    BIND --> PUB["IPublisher.Publish(event)<br/>consumer เช่น OrderPaidConsumer, notification, webhook"]
    PUB --> OK{"consumer สำเร็จ?"}
    OK -->|yes| DONE["MarkProcessed(now)"]
    OK -->|no| FAIL["MarkFailed(error), Attempts เพิ่ม<br/>PaymentReconciliationRequiredException = LogCritical"]
    FAIL --> POISON{"Attempts ถึง 8?"}
    POISON -->|yes| DLQ["หยุด lease รอ poison / DLQ review"]
    POISON -->|no| LEASE
    DONE --> END_S
    DLQ --> END_F((◉))
    ERR --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class SAVE,RESP,DONE,END_S ok
    class ERR,DLQ,END_F fail
    class ACTOR,OK,POISON gate
```

| Worker (BackgroundService) | ตาราง / แหล่งงาน | จังหวะ | งานที่ทำ | source |
| --- | --- | --- | --- | --- |
| `OutboxDispatcher` | `txn.OutboxMessages` | poll 2s, batch 50, lease 1m, max 8 | publish PaymentPaid / PaymentFailed / PaymentExpired ฯลฯ ให้ consumer ต่อ merchant | `Persistence.MerchantRuntime/Outbox/OutboxDispatcher.cs` |
| `MerchantUserOutboxDispatcher` | `merch` outbox (registration events) | เดียวกัน | RegistrationConsumer เขียน notice | `Persistence.MerchantUsers/Outbox/MerchantUserOutboxDispatcher.cs` |
| `GovernanceOutboxDispatcher` | `admin.GovernanceOutboxMessages` | poll 2s, batch 50, lease 1m, max 8 | ApprovalRequested / Decided / ExecutionReported ดู § 0.7 | `Persistence.ControlPlane/Governance/GovernanceOutboxDispatcher.cs` |
| `NotificationDeliveryDispatcher` | `txn.Deliveries` (lease ต่อแถว 1m) | poll | email / sms / business webhook ตาม template snapshot | `Persistence.MerchantRuntime/Notifications/NotificationDeliveryDispatcher.cs` |
| `WebhookDeliveryDispatcher` | `admin.WebhookDeliveries` (claim READPAST, lease 30s) | poll 2s | ส่ง outbound webhook + retry ตาม NextAttemptAt | `Persistence.ControlPlane/Notifications/WebhookDeliveryDispatcher.cs` |
| `TransactionInquiryWorker` | Transactions ที่ due (50 ต่อรอบ) | poll 5s | PSP inquiry ผ่าน CheckoutTransactionService.ResumeDueAsync โดยไม่พึ่ง browser | `Persistence.MerchantRuntime/Payments/TransactionInquiryWorker.cs` |

---

## 0.9 Error contract

API ตอบ error เป็น RFC7807 ProblemDetails เสมอ (handler คืน Results.Problem เอง หรือ exception ถูก map ที่ handler กลางด้วย detail คงที่ + code + traceId), ส่วน browser callback ของ OIDC ตอบด้วย 302 ไป returnTo ที่ผ่าน allowlist หรือ error page `?reason=` และ PSP browser return ตอบ 303 ไป checkout status (source: `src/Api/BuildingBlocks.Web/ProblemDetailsExceptionHandler.cs:30-100`, `Program.cs:701-705,903-979`, `ReturnUrlPolicy.cs:6-15`, `Admins/LoginService.cs:160-229`, `Admins/OidcAuthentication.cs:188-227`, `Merchants/UserLoginService.cs:202-231`, `IdentityAccess/IdentityAccessWiring.cs:155-202`)

```mermaid
flowchart TD
    START((●)) --> KIND{"ชนิด response?"}
    KIND -->|"API JSON"| SRC{"ที่มาของ error?"}
    SRC -->|"handler คืน Results.Problem"| EXPLICIT["ProblemDetails status + title<br/>extensions code + traceId"]
    SRC -->|"handler throw"| MAP["ProblemDetailsExceptionHandler.Map<br/>detail คงที่ ไม่ใส่ exception message"]
    MAP --> TABLE["NotFound 404, Gone 410,<br/>ConcurrencyConflict / Conflict / InvalidOperation 409,<br/>AccessDenied 403, InvalidRequest / Argument / BadHttpRequest 400,<br/>Upstream / DependencyUnavailable 503, อื่น 500"]
    TABLE --> CODE["extensions.code จาก exception ที่มี Code<br/>+ traceId เสมอ"]
    SRC -->|"framework bare status 401 / 403 / 404"| SCP["UseStatusCodePages แปลงเป็น ProblemDetails"]
    SRC -->|"auth challenge"| CHAL["admin/identity 401 Bearer error invalid_token (หรือ bearer_required)<br/>merchant 403 lifecycle code<br/>identity-order 400 ambiguous_authentication_context"]
    EXPLICIT --> JSON["application/problem+json"]
    CODE --> JSON
    SCP --> JSON
    CHAL --> JSON
    JSON --> END_F((◉))
    KIND -->|"browser OIDC callback"| CB{"login สำเร็จ?"}
    CB -->|yes| RET["302 ไป returnTo ที่ผ่าน ReturnUrlPolicy<br/>(same-origin path ใน allowlist) ไม่งั้น default path"]
    CB -->|no| DENY["302 ไป error page ?reason=code<br/>admin: not-provisioned, suspended, identity-conflict, access-denied, auth-failed, resolve-failed<br/>merchant: awaiting-approval, access-denied, failure code<br/>identity BFF: /login-error?reason=code"]
    RET --> END_S((◉))
    DENY --> END_F
    KIND -->|"PSP browser return"| PR{"return binding state ถูกต้อง,<br/>ไม่หมดอายุ, transaction ตรง provider?"}
    PR -->|no| R401_PR["401 ProblemDetails<br/>code checkout_return_invalid"]
    PR -->|yes| R303["set cookie checkout_status<br/>303 Location /api/v1/checkout/status"]
    R303 --> END_S
    R401_PR --> END_F
    KIND -->|"PSP webhook"| WH["200 OK / 202 pending / 401 signature invalid /<br/>404 unknown connection / 503 deferred"]
    WH --> END_S

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class RET,R303,WH,END_S ok
    class JSON,DENY,R401_PR,END_F fail
    class KIND,SRC,CB,PR gate
```

| Exception | status | title | code ใน extensions | source |
| --- | --- | --- | --- | --- |
| `NotFoundException` | 404 | Resource not found | จาก exception (ถ้ามี) | `ProblemDetailsExceptionHandler.cs:73-74` |
| `GoneException` | 410 | Gone | ไม่มี | `:75-76` |
| `ConcurrencyConflictException` | 409 | Conflict | จาก exception | `:77-78` |
| `ConflictException` | 409 | Conflict | จาก exception (ถ้ามี) | `:79-80` |
| `AccessDeniedException` | 403 | Forbidden | จาก exception (default `permission_denied`) | `:81-82` |
| `InvalidRequestException` (ArgumentException) | 400 | Invalid request | จาก exception | `:87-88`, `InvalidRequestException.cs:4` |
| `ArgumentException` / `BadHttpRequestException` | 400 | Invalid request | ไม่มี | `:85-88` |
| `InvalidOperationException` | 409 | The operation is not allowed in the resource's current state | ไม่มี | `:89-90` |
| `UpstreamUnavailableException` / `DependencyUnavailableException` | 503 | Upstream dependency unavailable | ไม่มี | `:91-97` |
| `MerchantBindingException` / อื่น ๆ | 500 | An unexpected error occurred | ไม่มี | `:83-84,98-99` |

---

## Deviations

ไม่มี deviation ค้างสำหรับ § 0.1–0.3 หลัง retire admin cookie/BFF stack — แถวเดิมของ group `/api/v1/admins/*` ที่ติด `RequireCsrf()` ถูกลบ เพราะทั้ง route group และ admin CSRF filter (`Admins/CsrfFilter.cs`) ถูกถอดออก admin console ใช้ Bearer JWT ล้วน

## Notes

| เรื่อง | ข้อเท็จจริงจาก source | source |
| --- | --- | --- |
| ลำดับ middleware | UseRateLimiter, UseAuthentication, UseIdentityAccess, UseAuthorization แล้วจึง endpoint filters ตามลำดับที่ endpoint ต่อ chain (cart mutation ต่อ RequirePermission ก่อน RequireAudienceCsrf) จึงห้ามสรุปว่า CSRF มาก่อน permission เสมอ | `Program.cs:660-663` |
| Bearer กับ console | SelectScheme routes audience Admin และ admin-or-identity-order ไป PlatformToken, merchant audience ไป MerchantUserSession; identity-order route ที่เป็น identity request สลับไป PlatformToken (audience Merchant) | `Iam/ConsoleSessionAuthentication.cs:44-67` |
| 429 กลาง | `RejectionStatusCode = 429` และ `OnRejected` (Retry-After จาก limiter หรือ 2s) ตั้งครั้งเดียวบน RateLimiterOptions ร่วม มีผลทุก policy | `Webhooks/RateLimiting.cs:29,46-56` |
| browser return ไม่ใช่ 302 + reason | `/api/v1/payment-returns/{providerCode}` ตอบ 303 + cookie `checkout_status` + Location `/api/v1/checkout/status` หรือ 401 `checkout_return_invalid`, รูป 302 + reason มีเฉพาะ OIDC callback | `Program.cs:903-979`, `Merchants/UserLoginService.cs:209,231`, `IdentityAccess/IdentityAccessWiring.cs:193-202` |
| 412 ของ POST /orders | เห็นเฉพาะใน `ProducesProblem` metadata ไม่พบใน handler path ที่อ่าน จึงอยู่นอก frame | `Program.cs:2116` |
| ชื่อ cookie บน dev HTTP | merchant-user ใช้ `__Host-mch_session`/`mch_csrf` เมื่อ HTTPS, dev HTTP ใช้ `mch_session` แทน, พฤติกรรม CSRF เหมือนกัน (admin console ไม่มี cookie แล้ว) | `Merchants/UserSessionCookies.cs:15-30` |
| ขอบเขตไฟล์นี้ | ค่า permission key / marker รายตัวของ endpoint ให้ดูตาราง flow ประกอบของ theme นั้น, ไฟล์นี้ให้เฉพาะกลไกกลาง | - |

**Render**: GitHub / Obsidian / VS Code Mermaid
