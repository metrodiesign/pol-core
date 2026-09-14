# pol-core API — Identity BFF และ OAuth (Activity Diagrams)

> Source: `docs/reference/api-endpoints.md` section "Identity และ OAuth" บรรทัด L32-L33, L52-L64, L78-L80 และ source ที่อ้างต่อ § (`src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs`, `IdentityAccessWiring.cs`, `BffSessionAuthentication.cs`, `BffCsrfFilter.cs`, `OpenIddictRefreshTokenRotator.cs`, `src/Application/Modules/Accounts.Application/IdentityAccessContracts.cs`, `src/Infrastructure/Persistence/Persistence.ControlPlane/OpenIddictRegistration.cs`, `IdentityAccess/IdentityAccessStore.cs`)
> Scope: 18 endpoints ของ theme T01 — OAuth metadata 2, OIDC login start 2, OIDC callback 2, BFF session read 5, BFF session rotate 2, BFF session revoke 2, OAuth token / authorize / revoke 3
> Generated: 2026-09-14

| § | Diagram | Endpoints |
| --- | --- | --- |
| 1.1 | OAuth discovery และ JWKS (OpenIddict ตอบเอง) | `GET /.well-known/oauth-authorization-server`, `GET /.well-known/jwks.json` |
| 1.2 | เริ่ม OIDC login (employees / agents) | `GET /api/v1/auth/employees/login`, `GET /api/v1/auth/agents/login` |
| 1.3 | OIDC callback พนักงาน: JIT account + BFF session | `GET /api/v1/auth/employees/callback` |
| 1.4 | OIDC callback ตัวแทน: registration session | `GET /api/v1/auth/agents/callback` |
| 1.5 | อ่านบริบท session / account (identity-platform) | `GET /api/v1/auth/session`, `GET /api/v1/me`, `GET /api/v1/me/merchants`, `GET /api/v1/me/access`, `GET /api/v1/me/sessions` |
| 1.6 | หมุน BFF ticket: refresh และเลือก merchant context | `POST /api/v1/auth/session/refresh`, `POST /api/v1/auth/merchant-context` |
| 1.7 | เพิกถอน BFF session: logout และลบ session ที่เลือก | `POST /api/v1/auth/logout`, `DELETE /api/v1/me/sessions/{sessionId:guid}` |
| 1.8 | OAuth token: client_credentials ด้วย private_key_jwt | `POST /oauth/token` |
| 1.9 | OAuth authorize และ revoke | `GET /oauth/authorize`, `POST /oauth/revoke` |

---

## 1.1 OAuth discovery และ JWKS (OpenIddict ตอบเอง)

OpenIddict server ที่ผูกกับ UseAuthentication จับ path ของ configuration / JWKS endpoint แล้วเขียน response เองก่อนถึง endpoint routing, handler ที่ map ไว้ใน IdentityAccessEndpoints ให้ OpenAPI metadata เท่านั้น (source: `src/Infrastructure/Persistence/Persistence.ControlPlane/OpenIddictRegistration.cs:28-67`, `src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs:83-93,242-278`, `src/Api/Api/Program.cs:243,712`)

```mermaid
flowchart TD
    START((●)) --> REQ["GET /.well-known/oauth-authorization-server<br/>หรือ GET /.well-known/jwks.json<br/>(AllowAnonymous, ไม่มี rate limit)"]
    REQ --> MW["UseAuthentication: OpenIddict server handler<br/>InferEndpointType ตรง ConfigurationEndpointUris / JsonWebKeySetEndpointUris"]
    MW --> KIND{"endpoint?"}
    KIND -->|configuration| DISC["metadata จาก server options:<br/>issuer OAuth:Issuer (ไม่ตั้ง = scheme://host), authorization / token / revocation endpoint,<br/>jwks_uri, grant types 3 แบบ, code_challenge S256, scopes จาก SystemClientScopeRegistry"]
    KIND -->|jwks| JWKS["public JWK จาก signing credentials<br/>Development / Testing = ephemeral key, นอกนั้น certificate จาก OAuth:CertificatePath<br/>ไม่มี d / p / q"]
    DISC --> R200["200 application/json<br/>OpenIddict HandleRequest จบที่ middleware"]
    JWKS --> R200
    R200 --> END_S((◉))
    MAPPED["handler Discovery / JsonWebKeySet ที่ map ใน IdentityAccessEndpoints<br/>ให้ OpenAPI operation id + summary เท่านั้น<br/>OpenIddict 7.7.0 ไม่มี passthrough ของ 2 endpoint นี้"]

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef ext fill:#4a3b0f,stroke:#e3b341,color:#fff
    class R200,END_S ok
    class KIND gate
    class MW,DISC,JWKS ext
    class MAPPED fail
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/.well-known/oauth-authorization-server` | diagram หลัก: OpenIddict สร้าง configuration document (issuer, endpoints, grant types, PKCE, scopes) |
| GET | `/.well-known/jwks.json` | เส้น jwks: คืน `{keys}` เฉพาะ public part ของ signing credentials, ไม่มี metadata อื่น |

---

## 1.2 เริ่ม OIDC login (employees / agents)

endpoint public ที่ (agents) ตรวจ AgentMerchantId ใน config ก่อน แล้ว normalize returnTo แล้ว challenge OpenIdConnect scheme ของ provider เพื่อส่ง browser ไป Microsoft Entra (source: `src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs:44-47,449-505,717-720`, `IdentityAccessWiring.cs:115-153`, `IdentityAccessOptions.cs:13,45-54`)

```mermaid
flowchart TD
    START((●)) --> REQ["GET /api/v1/auth/employees/login?returnTo=<br/>หรือ GET /api/v1/auth/agents/login?returnTo=<br/>(AllowAnonymous, ไม่มี rate limit)"]
    REQ --> AGENTCFG{"agents: IdentityAccess:AgentMerchantId<br/>ตั้งค่าและไม่ใช่ Guid.Empty?"}
    AGENTCFG -->|no| R503["503 ProblemDetails Agent login is not configured<br/>code capability_not_configured"]
    AGENTCFG -->|"yes หรือ employees"| NORM["NormalizeReturnTo: ต้องขึ้นต้นด้วย / และไม่ใช่ //<br/>ไม่ผ่านใช้ / แทน (ไม่ตอบ 400)"]
    NORM --> PROV{"IdentityAccessProviders มี scheme ของ key<br/>employees / agents (ClientId + Authority ตั้งค่า)?"}
    PROV -->|no| R404["404 (Results.NotFound, provider ไม่ได้ลงทะเบียน)"]
    PROV -->|yes| PROPS["AuthenticationProperties: RedirectUri = returnTo,<br/>identity.expected_issuer = WorkforceIssuer / AgentIssuer,<br/>identity.realm workforce / external, agents เพิ่ม identity.merchant_id"]
    PROPS --> CHAL["Results.Challenge(properties, scheme)<br/>IdentityWorkforceMicrosoft / IdentityAgentMicrosoft"]
    CHAL --> IDP["302 ไป Authority authorize endpoint<br/>response_type code, PKCE, scope จาก provider options (default openid profile email),<br/>state data-protected ต่อ scheme"]
    IDP --> END_S((◉))
    R503 --> END_F((◉))
    R404 --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef ext fill:#4a3b0f,stroke:#e3b341,color:#fff
    class PROPS,CHAL,END_S ok
    class R503,R404,END_F fail
    class AGENTCFG,PROV gate
    class IDP ext
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/auth/employees/login` | diagram หลัก: ข้ามการตรวจ AgentMerchantId, realm `workforce`, expected_issuer = `IdentityAccess:WorkforceIssuer`, scheme `IdentityWorkforceMicrosoft` |
| GET | `/api/v1/auth/agents/login` | ตรวจ AgentMerchantId ก่อน (503 `capability_not_configured`), realm `external` + item `identity.merchant_id`, expected_issuer = `IdentityAccess:AgentIssuer`, scheme `IdentityAgentMicrosoft` |

---

## 1.3 OIDC callback พนักงาน: JIT account + BFF session

OpenIdConnect handler ตรวจ state / code / id_token แล้ว OnTicketReceived สร้างหรือค้น Employee account แบบ JIT, ออก OpenIddict refresh token reference, สร้าง BFF ticket แล้วส่ง browser กลับ SPA, ทุก policy failure เป็น 302 ไป login-error พร้อม reason (source: `src/Api/Api/IdentityAccess/IdentityAccessWiring.cs:48-57,132-202,219-256,289-310`, `IdentityAccessEndpoints.cs:107-110,303-310`, `src/Application/Modules/Accounts.Application/IdentityAccessContracts.cs:25-82`, `src/Infrastructure/Persistence/Persistence.ControlPlane/IdentityAccess/IdentityAccessStore.cs:69-146`, `src/Api/Api/IdentityAccess/OpenIddictRefreshTokenRotator.cs:11-24`, `BffSessionAuthentication.cs:46-72,135-156`)

```mermaid
flowchart TD
    START((●)) --> CB["GET /api/v1/auth/employees/callback (code + state)<br/>browser redirect จาก IdP, AllowAnonymous"]
    CB --> OIDC["UseAuthentication: OpenIdConnect handler IdentityWorkforceMicrosoft<br/>CallbackPath ตรง, SkipUnrecognizedRequests = true"]
    OIDC --> STATE{"unprotect state ของ scheme นี้ได้?"}
    STATE -->|no| SKIP["handler ข้าม request (อาจเป็นของ admin OIDC scheme ดู Notes)<br/>เมื่อไม่มี handler ใดรับ จึงถึง endpoint OidcCallbackFallback"]
    SKIP --> R401["401 ProblemDetails OIDC callback validation failed<br/>code invalid_authentication + traceId"]
    STATE -->|yes| ERRP{"IdP ตอบ error?"}
    ERRP -->|access_denied| DENY_AD["OnAccessDenied: 302 WorkforceWebAppBaseUrl/login-error?reason=access-denied"]
    ERRP -->|"error อื่น"| DENY_RF["OnRemoteFailure: 302 /login-error?reason=auth-failed"]
    ERRP -->|no| EXCH["แลก code + PKCE verifier ที่ IdP token endpoint (ClientSecret)<br/>validate id_token: issuer, signature, lifetime, nonce<br/>SaveTokens = true"]
    EXCH --> EXOK{"exchange และ validate สำเร็จ?"}
    EXOK -->|no| DENY_RF
    EXOK -->|yes| TICKET["OnTicketReceived: IdentityBffLoginService.CompleteAsync(Employee)<br/>FromPrincipal: tid, oid หรือ sub, iss / aud (fallback id_token), email, name<br/>returnTo = ReturnUri ที่ตั้งตอน login"]
    TICKET --> POLICY{"HumanIdentityPolicy.Validate: provider microsoft,<br/>tid = WorkforceTenantId, iss = WorkforceIssuer,<br/>aud = WorkforceAudience?"}
    POLICY -->|no| DENY_X["302 /login-error?reason=<br/>provider-not-allowed / tenant-mismatch / issuer-mismatch / audience-mismatch"]
    POLICY -->|yes| ELIG{"workforceEligible (kind Employee = true)?"}
    ELIG -->|no| DENY_E["302 /login-error?reason=workforce-not-eligible"]
    ELIG -->|yes| JIT["IEmployeeJitStore.GetOrCreateAsync<br/>transaction SERIALIZABLE, retry deadlock 1205 ไม่เกิน 2 ครั้ง"]
    JIT --> EXIST{"acct.LoginAccounts มี (provider, tid, oid) แล้ว?"}
    EXIST -->|yes| TYPE{"account เป็น Employee และ Active?"}
    TYPE -->|"ไม่ใช่ Employee"| DENY_C["302 /login-error?reason=identity-account-type-conflict"]
    TYPE -->|"ไม่ Active"| DENY_S["302 /login-error?reason=account-suspended"]
    TYPE -->|yes| OBS["login.Observe(email, name, now)<br/>SaveChanges + commit"]
    EXIST -->|no| CREATE["insert acct.Accounts (Employee, Active, AuthorizationVersion 0),<br/>acct.LoginAccounts, acct.Employees"]
    CREATE --> COMMITOK{"SaveChanges + commit สำเร็จ?"}
    COMMITOK -->|"no (DbUpdateException, แพ้ race, rollback แล้วหา winner)"| RACEOK{"winner (LoginAccounts ที่ชนะ) เป็น Employee และ Active?"}
    RACEOK -->|no| DENY_C
    RACEOK -->|yes| RT
    OBS --> RT
    COMMITOK -->|yes| RT["OpenIddictRefreshTokenRotator.IssueAsync<br/>insert oauth.OpenIddictTokens type refresh_token, Subject = accountId,<br/>ExpirationDate = now + BffSessionMinutes"]
    RT --> BFF["BffSessionManager.CreateAsync<br/>insert acct.BffSessionTickets (SHA-256 token, AuthorizationVersion, absolute ExpiresAt)<br/>payload ที่ protect: csrfHash, refreshToken, merchantId null, returnTo"]
    BFF --> COOKIES["WriteCookies: __Host-pol_session (HttpOnly) + pol_csrf (JS อ่านได้)<br/>SameSite Lax, Path /, dev HTTP ใช้ pol_session"]
    COOKIES --> R302["302 ไป ToWebApp(returnTo, WorkforceWebAppBaseUrl)<br/>context.HandleResponse"]
    R302 --> END_S((◉))
    R401 --> END_F((◉))
    DENY_AD --> END_F
    DENY_RF --> END_F
    DENY_X --> END_F
    DENY_E --> END_F
    DENY_C --> END_F
    DENY_S --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef ext fill:#4a3b0f,stroke:#e3b341,color:#fff
    class OBS,CREATE,RT,BFF,COOKIES,R302,END_S ok
    class R401,DENY_AD,DENY_RF,DENY_X,DENY_E,DENY_C,DENY_S,END_F fail
    class STATE,ERRP,EXOK,POLICY,ELIG,EXIST,TYPE,COMMITOK,RACEOK gate
    class OIDC,EXCH ext
```

---

## 1.4 OIDC callback ตัวแทน: registration session

หัวเดียวกับ § 1.3 แต่หลัง OnTicketReceived ไม่สร้าง account ไม่ออก BFF ticket, ตรวจว่า identity ยังไม่มีบัญชีแล้วออก registration session cookie อายุสั้นแล้วส่งไปหน้า register ของ agent SPA (source: `src/Api/Api/IdentityAccess/IdentityAccessWiring.cs:58-67,157-202,219-228,258-272,312-315`, `IdentityAccessEndpoints.cs:111-114,303-310`, `src/Application/Modules/Accounts.Application/IdentityAccessContracts.cs:95-119`, `src/Infrastructure/Persistence/Persistence.ControlPlane/IdentityAccess/IdentityAccessStore.cs:152-169`)

```mermaid
flowchart TD
    START((●)) --> CB["GET /api/v1/auth/agents/callback (code + state)<br/>browser redirect จาก IdP, AllowAnonymous"]
    CB --> OIDC["OpenIdConnect handler IdentityAgentMicrosoft<br/>state / code exchange / id_token validation เหมือน § 1.3"]
    OIDC --> HEAD{"ผลของ OIDC handler?"}
    HEAD -->|"state ไม่ใช่ของ scheme"| R401["401 ProblemDetails code invalid_authentication<br/>(OidcCallbackFallback)"]
    HEAD -->|access_denied| DENY_AD["302 AgentWebAppBaseUrl/login-error?reason=access-denied"]
    HEAD -->|"remote failure"| DENY_RF["302 /login-error?reason=auth-failed"]
    HEAD -->|"ticket received"| TICKET["CompleteAsync(Agent): FromPrincipal workforceEligible = false<br/>อ่าน properties identity.merchant_id ที่ตั้งตอน login"]
    TICKET --> MID{"identity.merchant_id parse เป็น Guid ไม่ว่าง?<br/>(ประเมินก่อนเรียก StartAsync)"}
    MID -->|no| DENY_M["302 /login-error?reason=registration-merchant-required"]
    MID -->|yes| POLICY{"HumanIdentityPolicy.Validate กับ<br/>AgentIssuer / AgentTenantId / AgentAudience?"}
    POLICY -->|no| DENY_X["302 /login-error?reason=<br/>provider-not-allowed / tenant-mismatch / issuer-mismatch / audience-mismatch"]
    POLICY -->|yes| APPROVED{"HasApprovedAccountAsync:<br/>acct.LoginAccounts มี identity นี้แล้ว?"}
    APPROVED -->|yes| DENY_A["302 /login-error?reason=account-already-approved"]
    APPROVED -->|no| ISSUE["IssueAsync: insert acct.RegistrationSessions<br/>SHA-256(rawReference), merchantId, ExpiresAt = now + RegistrationSessionMinutes (default 30)"]
    ISSUE --> COOKIE["cookie pol_registration_session = rawReference<br/>HttpOnly, Secure เมื่อ HTTPS, Path /<br/>ไม่ออก BFF cookie และไม่แตะ acct.Accounts"]
    COOKIE --> R302["302 ไป ToWebApp(/register, AgentWebAppBaseUrl)<br/>ต่อด้วย /api/v1/agent-registration (theme อื่น)"]
    R302 --> END_S((◉))
    R401 --> END_F((◉))
    DENY_AD --> END_F
    DENY_RF --> END_F
    DENY_M --> END_F
    DENY_X --> END_F
    DENY_A --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef ext fill:#4a3b0f,stroke:#e3b341,color:#fff
    class ISSUE,COOKIE,R302,END_S ok
    class R401,DENY_AD,DENY_RF,DENY_M,DENY_X,DENY_A,END_F fail
    class HEAD,MID,POLICY,APPROVED gate
    class OIDC ext
```

---

## 1.5 อ่านบริบท session / account (identity-platform)

GET ทั้ง 5 ใช้ policy identity-platform (BFF cookie หรือ Bearer) แล้ว handler อ่าน account สดอีกครั้ง, บัญชีไม่ Active ตอบ 401 แบบ bare, ไม่มีการเขียน DB (source: `src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs:49-50,68-73,116-121,312-319,507-525,629-690,708-715`, `IdentityAccessWiring.cs:75-80`, `src/Infrastructure/Persistence/Persistence.ControlPlane/IdentityAccess/IdentityAccessStore.cs:238-245,270-349,439-449`)

```mermaid
flowchart TD
    START((●)) --> REQ["GET /api/v1/me<br/>(รวม /auth/session, /me/merchants, /me/access, /me/sessions ตามตาราง)"]
    REQ --> AUTHZ["policy identity-platform: __Host-pol_session หรือ Authorization Bearer<br/>+ IdentityAccessRequirement (account Active, authz_version ตรง) ดู § 0.2"]
    AUTHZ --> SESSION{"auth/session: cookie pol_session และ<br/>BffSessionContext feature มี?"}
    SESSION -->|"no (เช่น Bearer)"| R401["401 bare status<br/>UseStatusCodePages แปลงเป็น ProblemDetails ไม่มี code ดู § 0.9"]
    SESSION -->|"yes หรือ endpoint อื่น"| SUB{"claim sub เป็น Guid ไม่ว่าง?"}
    SUB -->|no| R401
    SUB -->|yes| ACC["IIdentityAccessQuery.FindAccountAsync(sub) READ-ONLY<br/>(auth/session ไม่ query เพิ่ม ใช้ ticket ใน feature)"]
    ACC --> ACTIVE{"account พบและ Active?"}
    ACTIVE -->|no| R401
    ACTIVE -->|yes| READ["อ่านตาม endpoint: me = FindLoginEmail,<br/>merchants = ListMerchantAccess กรอง Active,<br/>access = ResolveAuthorization(merchant_id, client_id), sessions = ListBffSessions"]
    READ --> SNAP{"access: snapshot เป็น null?"}
    SNAP -->|yes| R401
    SNAP -->|no| R200["200 JSON เฉพาะ metadata<br/>ไม่คืน ticket, token หรือ secret"]
    R200 --> END_S((◉))
    R401 --> END_F((◉))

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class READ,R200,END_S ok
    class R401,END_F fail
    class AUTHZ,SESSION,SUB,ACTIVE,SNAP gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/auth/session` | อ่าน cookie ผ่าน `ReadSessionToken` + feature `BffSessionContext` เท่านั้น: Bearer ไม่มี cookie = 401 แม้ policy รับ Bearer, ไม่ query DB เพิ่ม, คืน accountId, clientId, merchantId (จาก payload), issuedAt, expiresAt |
| GET | `/api/v1/me` | diagram หลัก: FindAccount + FindLoginEmail, คืน accountId, accountType, displayName, email, status, authorizationVersion, merchantContext (claim `merchant_id`) |
| GET | `/api/v1/me/merchants` | FindAccount แล้ว `ListMerchantAccessAsync` กรอง `AccessStatus.Active`, คืน array `{merchantId, dataScope}` |
| GET | `/api/v1/me/access` | FindAccount แล้ว `ResolveAuthorizationAsync(account, merchant_id claim, client_id claim)`, snapshot null = 401, คืน AuthorizationSnapshot ทั้งก้อน (permissions, roles, branches, HasPlatformAccess) |
| GET | `/api/v1/me/sessions` | ไม่ re-check account ใน handler, `ListBffSessionsAsync(sub)` ใน admin store: account ไม่พบ = NotFoundException 404 ดู § 0.9, คืน `BffSessionAdminView` (id, clientId, issuedAt, expiresAt, revokedAt, isLive) เรียง IssuedAt ล่าสุดก่อน |

---

## 1.6 หมุน BFF ticket: refresh และเลือก merchant context

ทั้งสอง endpoint ค้น ticket ปัจจุบันจาก cookie แล้วออก ticket ใหม่แทน (revoke เดิม + insert ใหม่ใน transaction เดียว) พร้อม cookie คู่ใหม่, refresh หมุน OpenIddict refresh token ด้วย ส่วน merchant-context ตรวจ MerchantAccess ก่อน (source: `src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs:51-58,527-604,692-706`, `BffSessionAuthentication.cs:43-44,74-112,135-156`, `OpenIddictRefreshTokenRotator.cs:29-55`, `src/Infrastructure/Persistence/Persistence.ControlPlane/IdentityAccess/IdentityAccessStore.cs:207-209,220-229,270-342`)

```mermaid
flowchart TD
    START((●)) --> REQ["POST /api/v1/auth/session/refresh<br/>หรือ POST /api/v1/auth/merchant-context body {merchantId}"]
    REQ --> AUTHZ["policy identity-bff (BFF cookie เท่านั้น) ดู § 0.2<br/>BffCsrfFilter: pol_csrf = X-CSRF-Token, Origin ตรง host, SHA-256 ตรง ticket ดู § 0.3"]
    AUTHZ --> WHICH{"endpoint?"}
    WHICH -->|merchant-context| VAL{"body merchantId ไม่ใช่ Guid.Empty?"}
    VAL -->|no| R400["400 ValidationProblem<br/>errors.merchantId A non-empty MerchantId is required"]
    VAL -->|yes| FIND
    WHICH -->|refresh| FIND["FindCurrentSessionAsync: cookie, SHA-256, acct.BffSessionTickets<br/>แล้ว Unprotect payload"]
    FIND --> HAS{"ticket พบและ unprotect ได้?"}
    HAS -->|no| R401["401 bare status ดู § 0.9"]
    HAS -->|yes| ACC["FindAccountAsync(ticket.AccountId)"]
    ACC --> LANE{"endpoint?"}
    LANE -->|refresh| ACTIVE_R{"account พบและ Active?"}
    ACTIVE_R -->|no| R401
    ACTIVE_R -->|yes| HASRT{"payload มี RefreshToken?"}
    HASRT -->|no| R401_RU["401 ProblemDetails The BFF session cannot be refreshed<br/>code refresh_unavailable"]
    HASRT -->|yes| ROT["OpenIddictRefreshTokenRotator.RotateAsync<br/>FindByReferenceId + TryRedeem แถวเดิม (status Redeemed)<br/>insert successor ใน oauth.OpenIddictTokens, ExpirationDate = NextExpiresAt()"]
    ROT --> ROTOK{"แถวเดิมพบและ redeem ได้?"}
    ROTOK -->|no| R401
    ROTOK -->|yes| REPLACE
    LANE -->|merchant-context| AUTHM["ResolveAuthorizationAsync(account, merchantId)<br/>อ่าน acct.AccountMerchantAccess Active + roles"]
    AUTHM --> MOK{"account Active และ<br/>snapshot.MerchantId = merchantId?"}
    MOK -->|no| R403["403 (Results.Forbid ผ่าน default scheme)"]
    MOK -->|yes| REPLACE["BffSessionManager.RotateAsync แล้ว ReplaceAsync ใน transaction:<br/>ticket เดิม RevokedAt = now, insert ticket ใหม่ (AuthorizationVersion ปัจจุบัน,<br/>ExpiresAt = now + BffSessionMinutes, ClientId เดิม, merchantId ตาม lane)"]
    REPLACE --> COOKIES["WriteCookies: __Host-pol_session ใหม่ + pol_csrf ใหม่"]
    COOKIES --> R200["200 refresh = {expiresAt}<br/>merchant-context = {merchantId, expiresAt}"]
    R200 --> END_S((◉))
    R400 --> END_F((◉))
    R401 --> END_F
    R401_RU --> END_F
    R403 --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class ROT,REPLACE,COOKIES,R200,END_S ok
    class R400,R401,R401_RU,R403,END_F fail
    class AUTHZ,WHICH,VAL,HAS,LANE,ACTIVE_R,HASRT,ROTOK,MOK gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| POST | `/api/v1/auth/session/refresh` | lane refresh: ไม่มี body, ตรวจ account Active (401), ต้องมี RefreshToken ใน payload (401 `refresh_unavailable`), หมุน OpenIddict token ก่อน rotate ticket, merchantId คงเดิม, response `{expiresAt}` |
| POST | `/api/v1/auth/merchant-context` | lane merchant-context: validate body ก่อนค้น session (400), ไม่แตะ OpenIddict token, ResolveAuthorization ต้องคืน MerchantId ตรง (403), ticket ใหม่ผูก merchantId ที่เลือก, response `{merchantId, expiresAt}` |

---

## 1.7 เพิกถอน BFF session: logout และลบ session ที่เลือก

logout เพิกถอน ticket ปัจจุบัน + OpenIddict refresh token แล้วลบ cookie เสมอ (idempotent), DELETE me/sessions เพิกถอน ticket แถวที่เลือกของบัญชีตนเองโดยไม่แตะ cookie (source: `src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs:59-62,122-129,321-329,606-627,692-706`, `BffSessionAuthentication.cs:158-164`, `src/Infrastructure/Persistence/Persistence.ControlPlane/IdentityAccess/IdentityAccessStore.cs:213-218,451-458`, `src/Domain/Modules/Accounts.Domain/AccountModels.cs:433`)

```mermaid
flowchart TD
    START((●)) --> REQ["POST /api/v1/auth/logout<br/>หรือ DELETE /api/v1/me/sessions/{sessionId:guid}"]
    REQ --> AUTHZ["policy identity-bff ดู § 0.2<br/>BffCsrfFilter: pol_csrf + X-CSRF-Token + Origin ดู § 0.3"]
    AUTHZ --> WHICH{"endpoint?"}
    WHICH -->|logout| FIND["FindCurrentSessionAsync จาก cookie<br/>acct.BffSessionTickets + Unprotect"]
    FIND --> HAS{"ticket ปัจจุบันพบ?"}
    HAS -->|yes| RT{"payload มี RefreshToken และ<br/>FindByReferenceId พบใน oauth.OpenIddictTokens?"}
    RT -->|yes| REVOKE_RT["IOpenIddictTokenManager.TryRevokeAsync<br/>status Revoked"]
    RT -->|no| REVOKE_T
    REVOKE_RT --> REVOKE_T["IBffSessionStore.RevokeAsync: RevokedAt = now<br/>SaveChanges"]
    HAS -->|no| CLEAR
    REVOKE_T --> CLEAR["ClearCookies: ลบ __Host-pol_session + pol_csrf<br/>ทำเสมอแม้ไม่พบ ticket"]
    CLEAR --> R204["204 No Content"]
    WHICH -->|"DELETE me/sessions"| SUB{"claim sub เป็น Guid ไม่ว่าง?"}
    SUB -->|no| R401["401 bare status ดู § 0.9"]
    SUB -->|yes| ROW["IIdentityAccessAdminStore.RevokeBffSessionAsync(sub, sessionId)<br/>SingleOrDefault Id = sessionId และ AccountId = sub"]
    ROW --> FOUND{"แถวพบ (session ของบัญชีตนเอง)?"}
    FOUND -->|no| R404["404 ProblemDetails Resource not found<br/>(NotFoundException Session was not found ดู § 0.9)"]
    FOUND -->|yes| REV2["row.Revoke(now) idempotent (RevokedAt ??= now)<br/>SaveChanges, ไม่แตะ OpenIddict token, ไม่ลบ cookie"]
    REV2 --> R204
    R204 --> END_S((◉))
    R401 --> END_F((◉))
    R404 --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class REVOKE_RT,REVOKE_T,CLEAR,REV2,R204,END_S ok
    class R401,R404,END_F fail
    class AUTHZ,WHICH,HAS,RT,SUB,FOUND gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| POST | `/api/v1/auth/logout` | lane logout: ไม่มี 401 / 404 จาก handler (ticket หายก็ยัง 204), revoke OpenIddict refresh token ก่อน revoke ticket, ลบ cookie คู่เสมอ |
| DELETE | `/api/v1/me/sessions/{sessionId:guid}` | lane DELETE: 401 เมื่อ claim sub ไม่มี, 404 เมื่อ session ไม่ใช่ของบัญชี, revoke แถวเดียว, ไม่ลบ cookie แม้เลือก session ปัจจุบัน, มี `IdempotencyMutationMarker` แต่ handler ไม่เรียก `IdempotencyKeys.Require` (ดู Notes) |

---

## 1.8 OAuth token: client_credentials ด้วย private_key_jwt

OpenIddict ตรวจ protocol และ signature ของ client assertion ก่อน, SystemClientTokenRequestHandler ตรวจ Account / SystemClient / key policy / jti replay ใน DB, แล้ว passthrough ให้ IssueToken ตรวจ scope และ sign in principal ให้ OpenIddict ออก access token อายุ 5 นาที (source: `src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs:32-37,171-240`, `src/Infrastructure/Persistence/Persistence.ControlPlane/OpenIddictRegistration.cs:28-67,91-196`, `IdentityAccess/IdentityAccessStore.cs:171-205,247-268`, `src/Application/Modules/Accounts.Application/IdentityAccessContracts.cs:148-205`, `SystemClientScopeRegistry.cs:6-14`, `tests/IntegrationTests/Hosts.Tests/IdentityAccessOAuthTests.cs:131-152,205-221,544-547`)

```mermaid
flowchart TD
    START((●)) --> REQ["POST /oauth/token (form: grant_type, client_id,<br/>client_assertion_type jwt-bearer, client_assertion, scope)<br/>AllowAnonymous, ไม่มี rate limit"]
    REQ --> OI["UseAuthentication: OpenIddict server ExtractTokenRequest / ValidateTokenRequest<br/>grant_type ที่เปิด (authorization_code, client_credentials, refresh_token),<br/>client ใน oauth.OpenIddictApplications + permissions, assertion signature กับ JsonWebKeySet"]
    OI --> OIOK{"protocol validation ผ่าน?"}
    OIOK -->|no| RERR["OAuth error JSON: 400 invalid_request / unsupported_grant_type / invalid_grant<br/>หรือ 401 invalid_client + WWW-Authenticate"]
    OIOK -->|yes| GRANT{"grant_type?"}
    GRANT -->|refresh_token| RTSYS{"client_id เป็น SYSTEM account?"}
    RTSYS -->|yes| RUC["400 unauthorized_client<br/>SYSTEM clients cannot use refresh tokens"]
    RTSYS -->|no| PASS_OTHER
    GRANT -->|authorization_code| PASS_OTHER["passthrough IssueToken: SignIn ด้วย ClaimsIdentity ว่าง<br/>ผลลัพธ์ตาม OpenIddict sign-in validation (นอก frame ดู Notes)"]
    GRANT -->|client_credentials| ASSERT{"SystemClientTokenRequestHandler:<br/>client_id, client_assertion และ type jwt-bearer ครบ?"}
    ASSERT -->|no| RIC["401 invalid_client<br/>A private_key_jwt client assertion is required"]
    ASSERT -->|yes| RES{"FindSystemClientAsync: acct.SystemClients + acct.Accounts<br/>พบและ AccountType System?"}
    RES -->|no| RIC2["401 invalid_client<br/>The SYSTEM client is not registered"]
    RES -->|yes| ACT{"account Active?"}
    ACT -->|no| RIC3["401 invalid_client<br/>The SYSTEM account is suspended"]
    ACT -->|yes| JWT{"parse assertion ได้และมี jti, nbf, exp, kid, alg?"}
    JWT -->|no| RIC4["401 invalid_client<br/>malformed / incomplete"]
    JWT -->|yes| KEY{"kid ตรง acct.ClientKeyPolicies ของ client?"}
    KEY -->|no| RIC5["401 invalid_client<br/>The client assertion key is not registered"]
    KEY -->|yes| VAL["SystemClientAssertionService.ValidateAsync: account Active, client Active + grant client_credentials,<br/>key usable ตอนนี้, iss = client_id = ApplicationId, kid / alg ตรง, aud = issuer (token endpoint),<br/>lifetime ไม่เกิน 60s (skew 30s), jti claim ผ่าน insert oauth.AssertionReplays (unique ApplicationId + Jti)"]
    VAL --> VALOK{"IsValid?"}
    VALOK -->|no| RIC6["401 invalid_client, description = code:<br/>account_disabled / client_disabled / key_inactive / key_mismatch /<br/>audience_mismatch / assertion_expired / jti_required / assertion_replayed"]
    VALOK -->|yes| ISSUE["passthrough IssueToken (endpoint handler)"]
    ISSUE --> REQNULL{"GetOpenIddictServerRequest มี?"}
    REQNULL -->|no| R404["404 (Results.NotFound)"]
    REQNULL -->|yes| RES2{"FindSystemClientAsync อีกครั้ง:<br/>account Active และ client Active?"}
    RES2 -->|no| R401H["401 {error invalid_client}<br/>WWW-Authenticate Basic realm oauth"]
    RES2 -->|yes| SCOPE{"scope ที่ขอ (ไม่ส่ง = ทั้งหมดที่ลงทะเบียน)<br/>อยู่ใน acct.SystemClientScopes ทั้งหมด?"}
    SCOPE -->|no| R400S["400 {error invalid_scope}"]
    SCOPE -->|yes| SIGNIN["Results.SignIn principal: sub = accountId, account_type SYSTEM,<br/>authz_version, client_id, merchant_id, scopes,<br/>audience pol-core-api, destination access_token"]
    SIGNIN --> TOKEN["OpenIddict ออก access token อายุ 5 นาที<br/>200 {access_token, token_type, expires_in} ไม่มี refresh_token"]
    TOKEN --> END_S((◉))
    PASS_OTHER --> END_F((◉))
    RERR --> END_F
    RUC --> END_F
    RIC --> END_F
    RIC2 --> END_F
    RIC3 --> END_F
    RIC4 --> END_F
    RIC5 --> END_F
    RIC6 --> END_F
    R404 --> END_F
    R401H --> END_F
    R400S --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef ext fill:#4a3b0f,stroke:#e3b341,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    class VAL,SIGNIN,TOKEN,END_S ok
    class RERR,RUC,RIC,RIC2,RIC3,RIC4,RIC5,RIC6,R404,R401H,R400S,END_F fail
    class OIOK,GRANT,RTSYS,ASSERT,RES,ACT,JWT,KEY,VALOK,REQNULL,RES2,SCOPE gate
    class OI ext
    class PASS_OTHER warn
```

---

## 1.9 OAuth authorize และ revoke

authorize ให้ OpenIddict ตรวจ client / redirect_uri / PKCE ก่อน passthrough ให้ handler challenge workforce login หรือ sign in เพื่อออก authorization code, ส่วน revoke ไม่มี passthrough OpenIddict จัดการทั้งหมดและ handler ที่ map ไม่ถูกเรียก (source: `src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs:95-105,280-301`, `src/Infrastructure/Persistence/Persistence.ControlPlane/OpenIddictRegistration.cs:28-45`, `src/Api/Api/Program.cs:351`, `Iam/ConsoleSessionAuthentication.cs:33-68`)

```mermaid
flowchart TD
    START((●)) --> WHICH{"endpoint?"}
    WHICH -->|"GET /oauth/authorize"| OIA["OpenIddict server ExtractAuthorizationRequest / ValidateAuthorizationRequest:<br/>client_id ลงทะเบียน, redirect_uri ตรงที่ลงทะเบียน,<br/>response_type code, code_challenge_method S256 (RequireProofKeyForCodeExchange)"]
    OIA --> AOK{"ผ่าน?"}
    AOK -->|no| AERR["OAuth error: 400 หรือ 302 redirect_uri พร้อม error<br/>(OpenIddict ตัดสินก่อน passthrough)"]
    AOK -->|yes| AH["passthrough Authorize (endpoint handler)"]
    AH --> AREQ{"request มีและ redirect_uri ไม่ว่าง?"}
    AREQ -->|no| A400["400 {error invalid_request}"]
    AREQ -->|yes| AUTHED{"http.User.IsAuthenticated?<br/>(default scheme ConsoleSession ไม่มี policy = MerchantUserSession ดู Notes)"}
    AUTHED -->|no| CHAL["Results.Challenge(IdentityWorkforceMicrosoft)<br/>302 ไป Entra, callback ตาม § 1.3, RedirectUri = URL authorize นี้"]
    AUTHED -->|yes| SUBQ{"claim sub มี?"}
    SUBQ -->|no| A401["401 {error login_required}"]
    SUBQ -->|yes| ASIGN["Results.SignIn principal: sub, name, scopes ที่ขอ,<br/>audience pol-core-api, destination access_token"]
    ASIGN --> ACODE["OpenIddict ออก authorization code (oauth.OpenIddictTokens)<br/>302 redirect_uri พร้อม code + state"]
    ACODE --> END_S((◉))
    CHAL --> END_S
    WHICH -->|"POST /oauth/revoke"| OIR["OpenIddict server revocation endpoint (ไม่มี passthrough):<br/>client authentication, หา token จาก reference / payload ใน oauth.OpenIddictTokens"]
    OIR --> ROK{"client ผ่านและ token เป็นของ client?"}
    ROK -->|"client ล้ม"| RERR["OAuth error JSON 400 / 401 invalid_client"]
    ROK -->|"token ไม่พบ"| RUNK["ผลตาม OpenIddict / RFC 7009 (นอก frame)"]
    ROK -->|yes| RREV["status Revoked ใน oauth.OpenIddictTokens<br/>200 {} (handler Results.Empty ที่ map ไม่ถูกเรียก)"]
    RREV --> END_S
    RUNK --> END_S
    AERR --> END_F((◉))
    A400 --> END_F
    A401 --> END_F
    RERR --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef ext fill:#4a3b0f,stroke:#e3b341,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    class ASIGN,ACODE,RREV,END_S ok
    class AERR,A400,A401,RERR,END_F fail
    class WHICH,AOK,AREQ,AUTHED,SUBQ,ROK gate
    class OIA,OIR,CHAL ext
    class RUNK warn
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/oauth/authorize` | lane authorize: passthrough ถึง handler, 400 `invalid_request`, 302 challenge หรือ 401 `login_required`, success = 302 redirect_uri พร้อม code |
| POST | `/oauth/revoke` | lane revoke: OpenIddict ตอบเองทั้งหมด, handler `() => Results.Empty` ให้ OpenAPI metadata เท่านั้น, success = 200 body ว่าง |

---

## Deviations

| fullPath | เอกสารบอก | source บอก | อ้างอิง |
| --- | --- | --- | --- |
| ไม่พบ deviation ระหว่างเอกสารกับ source | - | - | - |

## Notes

| เรื่อง | ข้อเท็จจริงจาก source | source |
| --- | --- | --- |
| OpenIddict ตอบ metadata / JWKS / revoke เอง | server options ตั้ง Configuration / JsonWebKeySet / Revocation endpoint URIs และเปิด passthrough เฉพาะ authorization + token, OpenIddict 7.7.0 ไม่มี `Enable*Passthrough` สำหรับ 3 endpoint นี้ จึงตอบจบใน UseAuthentication ก่อนถึง endpoint routing, handler ที่ map ให้ OpenAPI operation id (test A1 ตรวจ operation id เหล่านี้) | `OpenIddictRegistration.cs:30-42`, `Program.cs:712`, `tests/IntegrationTests/Hosts.Tests/Task8IdentityAccessA1Tests.cs:32-35` |
| GET /api/v1/auth/session กับ Bearer | policy identity-platform รับ Bearer แต่ handler อ่าน cookie ผ่าน `ReadSessionToken` และต้องมี feature `BffSessionContext` จึงตอบ 401 เมื่อไม่มี BFF cookie | `IdentityAccessEndpoints.cs:507-515` |
| DELETE /api/v1/me/sessions/{sessionId:guid} กับ Idempotency-Key | endpoint ติด `IdempotencyMutationMarker` (OpenAPI บอก header required) แต่ handler ไม่เรียก `IdempotencyKeys.Require` จึงไม่บังคับ header ตอน runtime, การ revoke idempotent ด้วย `RevokedAt ??= now` แทน | `IdentityAccessEndpoints.cs:122-129,321-329`, `AccountModels.cs:433` |
| http.User บน GET /oauth/authorize | endpoint ไม่มี policy จึงใช้ default scheme `ConsoleSession` (Program.cs:351) ซึ่ง SelectScheme ไม่มี policy จะเลือก MerchantUserSession, BFF cookie ที่ได้จาก callback หลัง challenge จึงไม่ทำให้ IsAuthenticated บน route นี้, ไม่มี test ครอบ authorization code flow ครบวง จึงอยู่นอก frame | `Program.cs:351`, `Iam/ConsoleSessionAuthentication.cs:33-67`, `IdentityAccessEndpoints.cs:285-286` |
| grant authorization_code / refresh_token บน POST /oauth/token | `IssueToken` sign in ด้วย ClaimsIdentity ว่าง (ไม่มี sub) สำหรับ grant ที่ไม่ใช่ client_credentials, ผลลัพธ์ HTTP ขึ้นกับ OpenIddict sign-in validation ไม่มี test ครอบ จึงอยู่นอก frame, SYSTEM client ถูก reject `unauthorized_client` ก่อนถึงจุดนี้ | `IdentityAccessEndpoints.cs:223-225`, `OpenIddictRegistration.cs:102-113` |
| status code ของ OAuth error | handler เอง: `invalid_client` 401 + `WWW-Authenticate Basic`, `invalid_scope` 400 (ยืนยันด้วย test), ส่วน `context.Reject` ใน SystemClientTokenRequestHandler ให้ OpenIddict map status (test ยืนยัน 401 สำหรับ invalid_client), error อื่นเป็น 400 ตาม OpenIddict default | `IdentityAccessEndpoints.cs:228-240`, `IdentityAccessOAuthTests.cs:146-151,544-547` |
| callback path ที่ใช้ร่วมกับ admin OIDC | `SkipUnrecognizedRequests = true` เพราะ `/api/v1/auth/employees/callback` อาจเป็น redirect URI เดียวกับ legacy admin OIDC scheme (ขึ้นกับ config `AdminOidc:CallbackPath`), state ที่ scheme นี้ unprotect ไม่ได้จะปล่อยให้ handler ถัดไปหรือ fallback 401 รับ | `IdentityAccessWiring.cs:138-143`, `Admins/OidcAuthentication.cs:98` |
| reason บน login-error | code ของ `IdentityAccessException` ถูกแปลง `_` เป็น `-` ก่อนใส่ `?reason=`, OIDC handler failure ใช้ `auth-failed` / `access-denied` คงที่, ไม่มีรายละเอียด exception หลุดไป browser | `IdentityAccessWiring.cs:165-186,193-202` |
| registration_identity_not_agent | `StartAsync` โยน code นี้เมื่อ `WorkforceEligible = true` แต่ callback agent ส่ง `workforceEligible: false` เสมอ จึงไม่มีเส้นทางถึงใน flow นี้ | `IdentityAccessWiring.cs:224`, `IdentityAccessContracts.cs:108-109` |
| provider ไม่ได้ตั้งค่า | `AddHumanProvider` ข้ามการลงทะเบียน scheme เมื่อ ClientId หรือ Authority ว่าง, login ตอบ 404 และ callback ไปถึง fallback 401 เท่านั้น | `IdentityAccessWiring.cs:125-131`, `IdentityAccessEndpoints.cs:498-499` |
| อายุ session และ cookie | ticket absolute ตาม `IdentityAccess:BffSessionMinutes` (default 480), refresh ต้องเรียกขณะ ticket live, cookie `__Host-*` เฉพาะ HTTPS ส่วน dev HTTP ใช้ `pol_session`, registration session อายุ `RegistrationSessionMinutes` (default 30) | `IdentityAccessOptions.cs:14-15`, `BffSessionAuthentication.cs:35-44,127-133` |
| rate limit | ไม่มี endpoint ใน theme นี้ต่อ `RequireRateLimiting` (ต่างจาก admin-auth / merchant-user-auth ใน § 0.4) | `IdentityAccessEndpoints.cs:32-129` |

**Render**: GitHub / Obsidian / VS Code Mermaid
