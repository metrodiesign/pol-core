# pol-core API — Cross-cutting behaviour (Activity Diagrams)

> Source: `docs/reference/api-endpoints.md` บรรทัด L24 (policy / permission vocabulary) และ source host ที่อ้างต่อ § (`src/Api/Api/Iam/*`, `IdentityAccess/*`, `Admins/*`, `Merchants/*`, `Webhooks/RateLimiting.cs`, `ConcurrencyEtags.cs`, `SfsQueryParser.cs`, `Governance/GovernanceEndpoints.cs`, `BackgroundDispatch/*`, `BuildingBlocks.Web/ProblemDetailsExceptionHandler.cs`)
> Scope: 9 § พฤติกรรมร่วมที่ทุก theme อ้างแทนการวาดซ้ำ (authn/authz, CSRF, rate limit, ETag/idempotency, SFS, maker-checker, outbox, error contract) ไม่มี endpoint ที่เป็น subject ของไฟล์นี้โดยตรง
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

ทุก request ใต้ policy `admin` / `merchant-user` / `dual-console` ผ่าน ConsoleSession policy scheme เลือก handler จาก policy + cookie แล้ว re-resolve บัญชีสดต่อ request ก่อนถึง permission gate แบบ fail-closed (source: `src/Api/Api/Iam/ConsoleSessionAuthentication.cs:33-68`, `Admins/SessionAuthenticationHandler.cs:62-138`, `Merchants/UserSessionAuthenticationHandler.cs:70-175`, `Iam/PermissionAuthorization.cs:83-139`, `Admins/HostWiring.cs:98-117`, `Merchants/UserPermissionAuthorization.cs:16-30`, `Program.cs:711-714`)

```mermaid
flowchart TD
    START((●)) --> MW["middleware: UseRateLimiter ดู § 0.4<br/>UseAuthentication, UseIdentityAccess, UseAuthorization"]
    MW --> SEL{"policy ของ endpoint?"}
    SEL -->|admin| AUD_A["audience = Admin"]
    SEL -->|merchant-user| AUD_M["audience = Merchant"]
    SEL -->|dual-console| DUAL{"มี adm cookie หรือ<br/>BFF cookie โดยไม่มี mch cookie?"}
    DUAL -->|yes| AUD_A
    DUAL -->|no| AUD_M
    SEL -->|admin-or-identity-order| AUD_A
    AUD_A --> IDR{"identity-order route และ<br/>identity request (Bearer หรือ BFF cookie)?"}
    IDR -->|yes| GO_ID["เส้น identity ดู § 0.2"]
    IDR -->|no| ADM_COOKIE{"มี __Host-adm_session?"}
    ADM_COOKIE -->|yes| ADM_H["AdminSession handler<br/>hash lookup + SessionDecisionPolicy<br/>re-resolve admin READ-ONLY"]
    ADM_COOKIE -->|no| BFF_COOKIE{"มี __Host-pol_session?"}
    BFF_COOKIE -->|yes| BFF_H["IdentityAccessBff scheme ดู § 0.2<br/>TryBindAdminScope เฉพาะ Employee"]
    BFF_COOKIE -->|no| R401_A["401 ProblemDetails<br/>code admin_session_required"]
    ADM_H --> ADM_OK{"session live, ไม่ reuse,<br/>admin ยัง Active?"}
    ADM_OK -->|no| R401_A
    ADM_OK -->|yes| BIND_A["bind IAdminScope<br/>claims admin_tier, NameIdentifier<br/>rotate cookie / slide idle"]
    BFF_H --> BFF_OK{"ticket live และ Employee?"}
    BFF_OK -->|no| R401_B["401 default (BFF scheme ไม่มี challenge เฉพาะ)<br/>UseStatusCodePages แปลงเป็น ProblemDetails ไม่มี code"]
    BFF_OK -->|yes| BIND_A
    AUD_M --> MCH_COOKIE{"มี __Host-mch_session?"}
    MCH_COOKIE -->|no| R401_M["401 (default challenge)"]
    MCH_COOKIE -->|yes| MCH_H["MerchantUserSession handler<br/>hash lookup + expiry<br/>re-resolve user READ-ONLY"]
    MCH_H --> MCH_LIFE{"user Active และ bound merchant?"}
    MCH_LIFE -->|no| R403_L["403 ProblemDetails<br/>code awaiting-approval / rejected / suspended / unbound"]
    MCH_LIFE -->|yes| MCH_OK{"session live, ไม่ reuse?"}
    MCH_OK -->|no| R401_M
    MCH_OK -->|yes| BIND_M["bind IUserScope<br/>claims merchant_id, sale_code<br/>rotate cookie / slide idle"]
    BIND_A --> FILTERS["endpoint filters ตามลำดับ chain ของ endpoint<br/>CSRF ดู § 0.3 (gate ด้านล่างมีเฉพาะที่ endpoint ต่อไว้)"]
    BIND_M --> FILTERS
    FILTERS --> BOUND{"BoundFilter (group /merchants/users):<br/>scope ของ audience bound?"}
    BOUND -->|no| R403_B["403 The selected console account is not active"]
    BOUND -->|yes| PERM{"RequirePermission:<br/>scope ที่ bind มี key?"}
    PERM -->|no| R403_P["403 You do not have permission for this action"]
    PERM -->|yes| AUDP{"RequireAudiencePermission:<br/>Admin ใช้ adminKey, Merchant ใช้ merchantKey"}
    AUDP -->|no| R403_P
    AUDP -->|yes| TIER{"RequirePlatformUserTier:<br/>admin_tier ∈ allowed?"}
    TIER -->|no| R403_T["403 code super_required"]
    TIER -->|yes| HANDLER["handler ทำงาน"]
    HANDLER --> END_S((◉))
    GO_ID --> END_S
    R401_A --> END_F((◉))
    R401_B --> END_F
    R401_M --> END_F
    R403_L --> END_F
    R403_B --> END_F
    R403_P --> END_F
    R403_T --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class BIND_A,BIND_M,HANDLER,END_S ok
    class R401_A,R401_B,R401_M,R403_L,R403_B,R403_P,R403_T,END_F fail
    class SEL,DUAL,IDR,ADM_COOKIE,BFF_COOKIE,ADM_OK,BFF_OK,MCH_COOKIE,MCH_LIFE,MCH_OK,BOUND,PERM,AUDP,TIER gate
```

| Gate | ใช้กับ | ผลเมื่อไม่ผ่าน | source |
| --- | --- | --- | --- |
| policy `admin` | AdminSession cookie หรือ BFF cookie (Employee) | 401 `admin_session_required` (ไม่มี cookie หรือ adm cookie ไม่ผ่าน), 401 default เมื่อ BFF cookie ไม่ผ่าน | `Admins/SessionAuthenticationHandler.cs:126-138,200-203`, `Iam/ConsoleSessionAuthentication.cs:65-67` |
| policy `merchant-user` | MerchantUserSession cookie เท่านั้น (ไม่มี Bearer fallback) | 401 default หรือ 403 lifecycle code | `Merchants/UserSessionAuthenticationHandler.cs:157-175,249-252` |
| policy `dual-console` | เลือก audience จาก cookie ที่มี, permission key ต้องเป็น Scope.Shared | ตาม audience ที่เลือก | `Iam/ConsoleSessionAuthentication.cs:44-49`, `Iam/PermissionAuthorization.cs:47-55` |
| `RequirePermission(key)` | IAdminScope ก่อน แล้ว IUserScope, ไม่มี scope = 403 | 403 ProblemDetails | `Iam/PermissionAuthorization.cs:83-106` |
| `RequireAudiencePermission(adminKey, merchantKey)` | เฉพาะ dual-console, key คนละฝั่งต่อ audience | 403 ProblemDetails | `Iam/PermissionAuthorization.cs:108-139` |
| `RequirePlatformUserTier(Tier.Super)` | claim admin_tier จาก AdminSession | 403 `super_required` | `Admins/HostWiring.cs:98-117` |
| `BoundFilter` | group `/merchants/users` (dual-console + merchant-user) | 403 | `Merchants/UserPermissionAuthorization.cs:16-30` |

---

## 0.2 Identity BFF / Bearer authentication + identity-order permission

policy `identity-bff` รับเฉพาะ BFF cookie, `identity-platform` รับ BFF cookie หรือ Bearer (OpenIddict) และ route ที่มี identity-order marker สลับจาก console ไปเส้นนี้เมื่อ request เป็น identity request โดย ticket มี absolute expiry และผูก AuthorizationVersion ของบัญชี (source: `src/Api/Api/IdentityAccess/BffSessionAuthentication.cs:43-44,195-271`, `IdentityAccessAuthorization.cs:10-92`, `IdentityAccessWiring.cs:70-113`, `Iam/IdentityPermissionAuthorization.cs:15-97`, `Iam/IdentityRequestAuthorization.cs:15-50`, `Iam/OrderIdentityAccessScope.cs:17-27`, `src/Domain/Modules/Accounts.Domain/AccountModels.cs:430-431`, `src/Infrastructure/Persistence/Persistence.ControlPlane/OpenIddictRegistration.cs:28-75`)

```mermaid
flowchart TD
    START((●)) --> AMB{"UseIdentityAccess: identity-order route<br/>และมี auth context มากกว่า 1<br/>(Bearer + cookie หรือ BFF + console cookie)?"}
    AMB -->|yes| R400["400 ProblemDetails<br/>code ambiguous_authentication_context"]
    AMB -->|no| POL{"policy?"}
    POL -->|identity-bff| BFF["IdentityAccessBff handler"]
    POL -->|"identity-platform (BFF หรือ Bearer)"| KIND{"มี Authorization Bearer?"}
    POL -->|"dual-console / admin-or-identity-order<br/>ที่เป็น identity request"| KIND
    KIND -->|yes| BEARER["OpenIddict validation scheme<br/>local server, audience api, token entry validation<br/>access token อายุ 5 นาที"]
    KIND -->|no| BFF
    BFF --> BOTH{"Bearer + BFF cookie พร้อมกัน?"}
    BOTH -->|yes| R401["401 (Fail หรือ NoResult ที่ชั้น auth)"]
    BOTH -->|no| COOKIE{"มี __Host-pol_session?"}
    COOKIE -->|no| R401
    COOKIE -->|yes| TICKET["FindByHash(SHA-256 cookie)<br/>BffSessionTicket + Account"]
    TICKET --> LIVE{"account Active และ ticket.IsLiveAt:<br/>RevokedAt null, now ก่อน ExpiresAt (absolute ไม่ slide),<br/>AuthorizationVersion เท่ากับของ account?"}
    LIVE -->|no| R401
    LIVE -->|yes| UNP{"unprotect ticket สำเร็จ<br/>และ AccountId ตรง?"}
    UNP -->|no| R401
    UNP -->|yes| ADMAUD{"audience Admin (admin console)?"}
    ADMAUD -->|yes| BINDADM{"TryBindAdminScope:<br/>Employee และมี snapshot?"}
    BINDADM -->|no| R401
    BINDADM -->|yes| CLAIMS
    ADMAUD -->|no| CLAIMS["claims sub, account_type, authz_version,<br/>token_context, scope, client_id, merchant_id"]
    BEARER --> CLAIMS
    CLAIMS --> REQ["IdentityAccessRequirement (policy identity-bff / identity-platform):<br/>account Active + authz_version ตรง,<br/>client_id มี = client + account Active,<br/>token_context PLATFORM = Employee + HasPlatformAccess,<br/>merchant_id = ResolveAuthorization(merchant) ตรง + Active,<br/>required_permission claim อยู่ใน Permissions"]
    REQ --> REQ_OK{"ทุกข้อผ่าน?"}
    REQ_OK -->|no| R403["403 (authorization fail)"]
    REQ_OK -->|yes| ORDER{"endpoint มี RequireOrderIdentityPermission /<br/>RequireIdentityPermission?"}
    ORDER -->|no| HANDLER
    ORDER -->|yes| RESOLVE["ResolveMerchantAsync(principal)<br/>account + client + merchant_id + snapshot"]
    RESOLVE --> MCTX{"merchant context ได้?"}
    MCTX -->|"ไม่มี merchant_id claim"| R403_M["403 ProblemDetails<br/>code merchant_context_missing"]
    MCTX -->|"resolve ล้ม"| R403
    MCTX -->|yes| SCOPE{"system token (client_id):<br/>scope claim มี systemScope<br/>human: Permissions มี humanPermission?"}
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
    class PROOF,HANDLER,END_S ok
    class R400,R401,R403,R403_M,END_F fail
    class AMB,POL,KIND,BOTH,COOKIE,LIVE,UNP,ADMAUD,BINDADM,REQ_OK,ORDER,MCTX,SCOPE gate
    class BEARER ext
```

| Gate | human (BFF cookie) | SYSTEM client (Bearer) | source |
| --- | --- | --- | --- |
| `RequireOrderIdentityPermission(payment.view, order.read)` | Permissions ต้องมี `payment.view` | scope claim ต้องมี `order.read` | `Iam/IdentityPermissionAuthorization.cs:24-41,77-82` |
| `RequireOrderIdentityPermission(payment.create, order.write)` | `payment.create` | `order.write` | เดียวกัน |
| `RequireOrderIdentityPermission(payment.create, checkout.write)` | `payment.create` | `checkout.write` | เดียวกัน |
| `RequireIdentityPermission(payment.create)` (POST /orders canonical) | `payment.create` | `order.write` คงที่ | `Iam/IdentityPermissionAuthorization.cs:43-49` |
| console request (ไม่มี Bearer / BFF cookie) บน route เดียวกัน | ใช้ `PermissionAuthorization.IsAllowed` ตาม § 0.1 | ไม่มี | `Iam/IdentityPermissionAuthorization.cs:29-39` |

---

## 0.3 CSRF double-submit

unsafe method (POST / PUT / PATCH / DELETE) ต้องส่ง `X-CSRF-Token` เท่ากับ CSRF cookie ของ scheme ตัวเอง, filter เลือกตาม audience, Bearer ข้ามได้เฉพาะ identity route และ boot guard บังคับให้ทุก unsafe endpoint ใต้ cookie policy มี filter ฝั่งถูกต้อง (source: `src/Api/Api/Admins/CsrfFilter.cs:13-48`, `Merchants/UserCsrfFilter.cs:14-37`, `Iam/CsrfParity.cs:21-125`, `IdentityAccess/BffCsrfFilter.cs:10-82`, `Program.cs:3713-3715`)

```mermaid
flowchart TD
    START((●)) --> SAFE{"method เป็น GET / HEAD / OPTIONS / TRACE?"}
    SAFE -->|yes| PASS["ข้าม CSRF (filter ไม่ตรวจ)"]
    SAFE -->|no| KIND{"CSRF filter ที่ endpoint ต่อ chain?"}
    KIND -->|"RequireCsrf (policy admin)"| ADM["CsrfFilter: cookie adm_csrf<br/>หรือ pol_csrf เมื่อ auth ด้วย BFF cookie อย่างเดียว"]
    KIND -->|"RequireUserCsrf (policy merchant-user)"| MCH["UserCsrfFilter: cookie mch_csrf"]
    KIND -->|"RequireAudienceCsrf / RequireAdminOrIdentityCsrf"| AUD{"identity-order route และ<br/>identity request?"}
    KIND -->|"BffCsrfFilter (policy identity-bff)"| BFF["BffCsrfFilter: cookie pol_csrf"]
    KIND -->|"RequireIdentityPlatformMutation (identity-platform)"| BEARER{"Authorization Bearer?"}
    BEARER -->|yes| PASS
    BEARER -->|no| BFF
    AUD -->|yes| BEARER
    AUD -->|no| SELAUD{"SelectedConsoleAudience?"}
    SELAUD -->|Admin| ADM
    SELAUD -->|Merchant| MCH
    SELAUD -->|none| R403_AUD["403 No authenticated console audience is bound"]
    ADM --> CMP_A{"cookie และ header X-CSRF-Token<br/>ไม่ว่างและเท่ากัน (constant-time)?"}
    MCH --> CMP_A
    CMP_A -->|no| R403["403 ProblemDetails Missing or invalid CSRF token<br/>code csrf_failed (admin), ไม่มี code (merchant)"]
    CMP_A -->|yes| PASS
    BFF --> CMP_B{"cookie pol_csrf เท่ากับ header?"}
    CMP_B -->|no| R403_B["403 ProblemDetails<br/>code csrf_failed"]
    CMP_B -->|yes| ORIGIN{"Origin header (ถ้ามี)<br/>ตรง scheme://host?"}
    ORIGIN -->|no| R403_B
    ORIGIN -->|yes| HASH{"BffSessionContext มี และ<br/>SHA-256(header) ตรง ticket.CsrfHash?"}
    HASH -->|no| R403_B
    HASH -->|yes| PASS
    PASS --> NEXT["filter ถัดไป / handler"]
    NEXT --> END_S((◉))
    R403 --> END_F((◉))
    R403_B --> END_F
    R403_AUD --> END_F
    BOOT["boot: CsrfParity.Assert<br/>unsafe endpoint ใต้ policy ที่รู้จักต้องมี CsrfProtected ของ scheme ตัวเอง<br/>ผิดฝั่งหรือขาด = boot ล้ม (ไม่ใช่ runtime)"]

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class PASS,NEXT,END_S ok
    class R403,R403_B,R403_AUD,END_F fail
    class SAFE,KIND,AUD,BEARER,SELAUD,CMP_A,CMP_B,ORIGIN,HASH,BOOT gate
```

| Filter | cookie ที่เทียบ | ตรวจเพิ่ม | ใช้กับ policy | source |
| --- | --- | --- | --- | --- |
| `RequireCsrf()` | `adm_csrf` หรือ `pol_csrf` (BFF cookie อย่างเดียว) | ไม่มี | `admin` (group `/admins` ติดทั้ง group) | `Admins/CsrfFilter.cs:20-44`, `Program.cs:2418` |
| `RequireUserCsrf()` | `mch_csrf` | ไม่มี | `merchant-user` | `Merchants/UserCsrfFilter.cs:21-33` |
| `RequireAudienceCsrf()` | ตาม audience ที่เลือก | identity request บน identity-order route ไป BffCsrfFilter, Bearer ข้าม | `dual-console` | `Iam/CsrfParity.cs:29-33,43-68` |
| `RequireAdminOrIdentityCsrf()` | เดียวกับ AudienceCsrf | marker AdminSession + IdentityPlatform | `admin-or-identity-order` | `Iam/CsrfParity.cs:35-40` |
| `BffCsrfFilter` (ต่อเอง) | `pol_csrf` | Origin ตรง host, SHA-256(header) ตรง CsrfHash ใน ticket | `identity-bff` | `IdentityAccess/BffCsrfFilter.cs:18-70`, `IdentityAccessEndpoints.cs:51-62,122-125` |
| `RequireIdentityPlatformMutation()` | `pol_csrf` | Bearer ข้ามทั้ง filter | `identity-platform` | `IdentityAccess/BffCsrfFilter.cs:10-16,72-82` |

---

## 0.4 Rate limiting

UseRateLimiter รันก่อน UseAuthentication, ทุก policy เป็น sliding window ต่อ source IP ไม่รอคิว และตอบ 429 พร้อม Retry-After จาก OnRejected กลางที่ตั้งครั้งเดียว (source: `src/Api/Api/Webhooks/RateLimiting.cs:22-58`, `Customers/PaymentRateLimiting.cs:19-38`, `Admins/AuthRateLimiting.cs:13-72`, `Merchants/UserAuthRateLimiting.cs:13-32`, `Program.cs:711,802-1089,1305,1903-1938,2437,2471,2612,2746,2794`)

```mermaid
flowchart TD
    START((●)) --> MW["UseRateLimiter ก่อน UseAuthentication<br/>partition = source IP (RemoteIpAddress หรือ unknown)"]
    MW --> POL{"endpoint RequireRateLimiting policy?"}
    POL -->|"ไม่มี"| PASS["ไม่จำกัด"]
    POL -->|customer-payment| P1["sliding 10 req / 60s, 6 segments<br/>checkout access / confirm / status / summary / verify,<br/>orders/{token}/pay, payment-status, payment-returns"]
    POL -->|admin-auth| P2["sliding 20 req / 60s, 6 segments<br/>GET admins/auth/{provider}/login, POST admins/auth/logout"]
    POL -->|merchant-user-auth| P3["sliding 20 req / 60s, 6 segments<br/>POST merchants/users/register, GET merchants/auth/{provider}/login,<br/>POST merchants/auth/logout"]
    POL -->|psp-webhook| P4["sliding 60 req / 10s, 5 segments<br/>POST webhooks/{pspConnectionId},<br/>POST webhooks/payment-providers/{providerAccountId}"]
    P1 --> LEASE{"acquire permit ได้?<br/>QueueLimit 0 ไม่รอคิว"}
    P2 --> LEASE
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
| `admin-auth` | 20 / 60s (6 segments) | source IP | `GET admins/auth/{provider}/login`, `POST admins/auth/logout` | `Admins/AuthRateLimiting.cs:27-31`, `Program.cs:2437,2471` |
| `merchant-user-auth` | 20 / 60s (6 segments) | source IP | `POST merchants/users/register`, `GET merchants/auth/{provider}/login`, `POST merchants/auth/logout` | `Merchants/UserAuthRateLimiting.cs:17-31`, `Program.cs:2612,2746,2794` |
| `psp-webhook` | 60 / 10s (5 segments) | source IP (ไม่ใช่ pspConnectionId) | `POST webhooks/{pspConnectionId}`, `POST webhooks/payment-providers/{providerAccountId}` | `Webhooks/RateLimiting.cs:31-44`, `Program.cs:1062,1305` |
| `admin-identity-mutation-ip` + per-admin `PartitionedRateLimiter<Guid>` | 20 / 60s | IP แล้ว AdminId | นิยามไว้แต่ไม่มี caller ใน `src/Api/Api` | `Admins/AuthRateLimiting.cs:32-61` |

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
    CHECKER --> DECIDE["POST /approvals/{approvalId}/approve หรือ /reject<br/>If-Match, Idempotency-Key, body reason + targetVersion<br/>gate admin + settings.manage + RequireCsrf"]
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
    SRC -->|"auth challenge"| CHAL["admin 401 code admin_session_required<br/>merchant 403 lifecycle code<br/>identity 400 ambiguous_authentication_context"]
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

| fullPath | เอกสารบอก | source บอก | อ้างอิง |
| --- | --- | --- | --- |
| ทุก child ของ group `/api/v1/admins/*` ที่ map ใน Program.cs (เช่น `POST /api/v1/admins/auth/logout` L179, `POST /api/v1/admins/roles` L188, `POST /api/v1/admins/{id:guid}/merchants` L194) | แถวส่วนใหญ่ไม่ระบุ CSRF filter (ยกเว้น L178 `POST /api/v1/admins` และ L202 `POST /api/v1/merchants`) | group ติด `RequireCsrf()` ระดับ group จึงมี CsrfFilter ทุก child, unsafe method ต้องส่ง `X-CSRF-Token` แม้ endpoint จะ AllowAnonymous (logout) | `src/Api/Api/Program.cs:2418,2446-2451`, `Iam/CsrfParity.cs:23-24` |

## Notes

| เรื่อง | ข้อเท็จจริงจาก source | source |
| --- | --- | --- |
| ลำดับ middleware | UseRateLimiter, UseAuthentication, UseIdentityAccess, UseAuthorization แล้วจึง endpoint filters ตามลำดับที่ endpoint ต่อ chain (governance ต่อ RequireCsrf ก่อน RequirePermission, cart mutation ต่อ RequirePermission ก่อน RequireAudienceCsrf) จึงห้ามสรุปว่า CSRF มาก่อน permission เสมอ | `Program.cs:711-714,1547-1548`, `Governance/GovernanceEndpoints.cs:168` |
| Bearer กับ console | SelectScheme ส่ง Bearer ไป OpenIddict เฉพาะ identity-order route ที่เป็น identity request, นอกนั้น Bearer ถูกมองเป็น request ไม่มี cookie | `Iam/ConsoleSessionAuthentication.cs:50-67` |
| BFF cookie บน admin console | ticket ไม่ผ่านตอบ 401 default (BFF handler ไม่มี HandleChallengeAsync), ไม่ใช่ `admin_session_required` | `Iam/ConsoleSessionAuthentication.cs:65-67`, `IdentityAccess/BffSessionAuthentication.cs:167-241` |
| อายุ ticket BFF | absolute expiry ตาม `IdentityAccess:BffSessionMinutes` (เอกสาร L57: default 480 นาที, dev 1440) ไม่ slide, refresh ต้องเรียกขณะ ticket ยัง live ไม่งั้น 401 | `IdentityAccess/BffSessionAuthentication.cs:43-44`, `IdentityAccessEndpoints.cs:527-566` |
| CsrfParity กับ identity-bff | ไม่ตรวจ endpoint policy `identity-bff` เพราะ `AuthPolicyScheme.AllFor("identity-bff")` คืนว่าง, กลุ่มนี้พึ่ง `.AddEndpointFilter<BffCsrfFilter>()` + `BffCsrfProtected` ที่ต่อเอง | `Iam/PermissionAuthorization.cs:33-41`, `IdentityAccessEndpoints.cs:51-62,122-125` |
| per-admin rate limit | `RequireAdminIdentityMutationRateLimit` นิยามไว้แต่ไม่มี caller ใน `src/Api/Api` จึงไม่วาดเป็น gate | `Admins/AuthRateLimiting.cs:40-61` |
| 429 กลาง | `RejectionStatusCode = 429` และ `OnRejected` (Retry-After จาก limiter หรือ 2s) ตั้งครั้งเดียวบน RateLimiterOptions ร่วม มีผลทุก policy | `Webhooks/RateLimiting.cs:29,46-56` |
| browser return ไม่ใช่ 302 + reason | `/api/v1/payment-returns/{providerCode}` ตอบ 303 + cookie `checkout_status` + Location `/api/v1/checkout/status` หรือ 401 `checkout_return_invalid`, รูป 302 + reason มีเฉพาะ OIDC callback | `Program.cs:903-979`, `Admins/LoginService.cs:225`, `Merchants/UserLoginService.cs:209,231`, `IdentityAccess/IdentityAccessWiring.cs:193-202` |
| 412 ของ POST /orders | เห็นเฉพาะใน `ProducesProblem` metadata ไม่พบใน handler path ที่อ่าน จึงอยู่นอก frame | `Program.cs:2116` |
| ชื่อ cookie บน dev HTTP | `__Host-*` ใช้เมื่อ HTTPS, dev HTTP ใช้ `pol_session` / dev name ของ admin และ merchant แทน, พฤติกรรม CSRF เหมือนกัน | `IdentityAccess/BffSessionAuthentication.cs:35-36,127-133` |
| ขอบเขตไฟล์นี้ | ค่า permission key / marker รายตัวของ endpoint ให้ดูตาราง flow ประกอบของ theme นั้น, ไฟล์นี้ให้เฉพาะกลไกกลาง | - |

**Render**: GitHub / Obsidian / VS Code Mermaid
