# pol-core API — Identity platform login และ OAuth (Sequence Diagrams)

> Source: `docs/reference/api-endpoints.md` section "Identity และ OAuth" และ source ที่อ้างต่อ § (`src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs`, `IdentityAccessWiring.cs`, `PlatformTokenAuthentication.cs`, `BffCsrfFilter.cs`, `src/Application/Modules/Accounts.Application/IdentityAccessContracts.cs`, `src/Infrastructure/Persistence/Persistence.ControlPlane/OpenIddictRegistration.cs`, `IdentityAccess/IdentityAccessStore.cs`)
> Scope: 8 § เดียวกับ `01-identity-bff-oauth.activities.md` (หมายเลข § ตรงกัน, §1.6 BFF ticket ถูก retire) แสดงลำดับข้าม actor / browser / API / handler / DB / OpenIddict / IdP
> Generated: 2026-09-14

| § | Diagram | Endpoints |
| --- | --- | --- |
| 1.1 | OAuth discovery และ JWKS (OpenIddict ตอบเอง) | `GET /.well-known/oauth-authorization-server`, `GET /.well-known/jwks.json` |
| 1.2 | เริ่ม OIDC login (agents) | `GET /api/v1/auth/agents/login` |
| 1.3 | OIDC callback พนักงาน: JIT account + platform authorization code | `GET /api/v1/auth/employees/callback` |
| 1.4 | OIDC callback ตัวแทน: registration session | `GET /api/v1/auth/agents/callback` |
| 1.5 | อ่านบริบท session / account (identity-platform) | `GET /api/v1/me`, `GET /api/v1/me/merchants`, `GET /api/v1/me/access`, `GET /api/v1/me/sessions` |
| 1.7 | เพิกถอน session: logout และลบ login session ที่เลือก | `POST /api/v1/auth/logout`, `DELETE /api/v1/me/sessions/{sessionId}` |
| 1.8 | OAuth token: client_credentials ด้วย private_key_jwt | `POST /oauth/token` |
| 1.9 | OAuth authorize และ revoke | `GET /oauth/authorize`, `POST /oauth/revoke` |

---

## 1.1 OAuth discovery และ JWKS (OpenIddict ตอบเอง)

OpenIddict server จับ path metadata/JWKS ใน UseAuthentication ก่อนถึง endpoint routing เสมอ handler ที่ map ไว้ให้ OpenAPI metadata เท่านั้น (source: `src/Infrastructure/Persistence/Persistence.ControlPlane/OpenIddictRegistration.cs:28-67`, `src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs:83-93,242-278`)

```mermaid
sequenceDiagram
    autonumber
    participant SPA as SPA / integration client
    participant API as API<br/>UseAuthentication pipeline
    participant OI as OpenIddict server handler

    Note over SPA,OI: Phase A — metadata / JWKS ก่อนถึง endpoint routing
    SPA->>API: GET /.well-known/oauth-authorization-server<br/>หรือ GET /.well-known/jwks.json (AllowAnonymous, ไม่มี rate limit)
    API->>OI: InferEndpointType ตรง ConfigurationEndpointUris หรือ JsonWebKeySetEndpointUris
    alt configuration endpoint
        OI->>OI: สร้าง metadata จาก server options,<br/>issuer, authorization/token/revocation endpoint, jwks_uri, grant types 3 แบบ, PKCE S256, scopes
    else jwks endpoint
        OI->>OI: แปลง signing credentials เป็น public JWK เท่านั้น,<br/>Development/Testing = ephemeral key, อื่น = certificate จาก OAuth:CertificatePath
    end
    OI-->>SPA: 200 application/json
    Note over API: handler Discovery/JsonWebKeySet ที่ map ใน IdentityAccessEndpoints<br/>ให้ OpenAPI operation id เท่านั้น ไม่ถูกเรียกจริง (OpenIddict ตอบจบใน middleware ก่อน)
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/.well-known/oauth-authorization-server` | diagram หลัก: OpenIddict สร้าง configuration document (issuer, endpoints, grant types, PKCE, scopes) |
| GET | `/.well-known/jwks.json` | เส้น jwks: คืน `{keys}` เฉพาะ public part ของ signing credentials, ไม่มี metadata อื่น |

---

## 1.2 เริ่ม OIDC login (agents)

endpoint public ของ agents ตรวจ AgentMerchantId ตั้งค่าก่อน แล้ว normalize returnTo แล้ว challenge OpenIdConnect scheme ของ provider ให้ browser ไป Microsoft Entra — employee เริ่มที่ `/oauth/authorize` (§ 1.9) (source: `src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs:45-46,564-604,695-698`, `IdentityAccessWiring.cs:115-153`)

```mermaid
sequenceDiagram
    autonumber
    actor U as Agent applicant
    participant Browser
    participant API
    participant IdP as Microsoft Entra<br/>(external)

    Note over U,API: Phase A — ตรวจ agent config ก่อน normalize returnTo แล้วตรวจ provider
    U->>Browser: คลิก login
    Browser->>API: GET /api/v1/auth/agents/login?returnTo= (AllowAnonymous)
    alt IdentityAccess:AgentMerchantId ไม่ได้ตั้งค่าหรือ Guid.Empty
        API-->>Browser: 503 ProblemDetails code capability_not_configured
    else agent config พร้อม
        API->>API: NormalizeReturnTo, ต้องขึ้นต้นด้วย / และไม่ใช่ //, ไม่ผ่านใช้ / แทน
        alt provider scheme ยังไม่ลงทะเบียน (ClientId หรือ Authority ว่าง)
            API-->>Browser: 404 (Results.NotFound)
        else provider พร้อม
            Note over API: Phase B — เตรียม AuthenticationProperties แล้ว challenge
            API->>API: RedirectUri=returnTo, identity.expected_issuer=AgentIssuer,<br/>identity.realm=external, identity.merchant_id
            API-->>Browser: 302 Results.Challenge(scheme) IdentityAgentMicrosoft
            Browser->>IdP: GET authorize (response_type=code, PKCE,<br/>scope จาก provider options, state data-protected ต่อ scheme)
        end
    end
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/auth/agents/login` | ตรวจ AgentMerchantId ก่อน (503 `capability_not_configured`), realm `external` + item `identity.merchant_id`, expected_issuer = `IdentityAccess:AgentIssuer`, scheme `IdentityAgentMicrosoft` |

> employee login-start `GET /api/v1/auth/employees/login` ถูก retire — employee เริ่มที่ `/oauth/authorize` (§ 1.9)

---

## 1.3 OIDC callback พนักงาน: JIT account + platform authorization code

OpenIdConnect handler ตรวจ state/code/id_token แล้ว OnTicketReceived สร้างหรือค้น Employee account แบบ JIT, sign บัญชีเข้า short-lived login cookie แล้ววนกลับไป `/oauth/authorize` ให้ OpenIddict ออก authorization code ที่ SPA แลกเป็น platform JWT (source: `src/Api/Api/IdentityAccess/IdentityAccessWiring.cs:18-20,167-194,221-260`, `IdentityAccessEndpoints.cs:348-376`, `src/Application/Modules/Accounts.Application/IdentityAccessContracts.cs:25-80`, `Persistence.ControlPlane/IdentityAccess/IdentityAccessStore.cs:72-152`)

```mermaid
sequenceDiagram
    autonumber
    participant IdP as Microsoft Entra<br/>(external)
    participant Browser
    participant API as API<br/>OpenIdConnect handler IdentityWorkforceMicrosoft
    participant JIT as EmployeeJitService + DB
    participant AZ as /oauth/authorize + OpenIddict

    Note over IdP,API: Phase A — callback และแลก token
    IdP-->>Browser: 302 callback?code&state
    Browser->>API: GET /api/v1/auth/employees/callback?code&state (AllowAnonymous)
    API->>API: unprotect state ของ scheme นี้ (SkipUnrecognizedRequests=true)
    alt unprotect ไม่ได้ (state เป็นของ scheme อื่น)
        API-->>Browser: 401 ProblemDetails code invalid_authentication (fallback OidcCallbackFallback)
    else IdP ตอบ access_denied
        API-->>Browser: 302 WorkforceWebAppBaseUrl/login-error?reason=access-denied
    else remote failure อื่น
        API-->>Browser: 302 /login-error?reason=auth-failed
    else state ถูกต้อง
        API->>IdP: แลก code + PKCE verifier (ClientSecret) ที่ token endpoint
        IdP-->>API: id_token + access_token (SaveTokens=true)
        API->>API: validate id_token, issuer, signature, lifetime, nonce
        alt exchange หรือ validate ล้มเหลว
            API-->>Browser: 302 /login-error?reason=auth-failed
        else สำเร็จ (OnTicketReceived)
            Note over API,JIT: Phase B — policy gate แล้ว JIT account
            API->>API: FromPrincipal, tid, oid หรือ sub, iss/aud (fallback id_token), email, name
            API->>API: HumanIdentityPolicy.Validate provider=microsoft,<br/>tid=WorkforceTenantId, iss=WorkforceIssuer, aud=WorkforceAudience
            alt policy ไม่ผ่าน
                API-->>Browser: 302 /login-error?reason=provider-not-allowed/tenant-mismatch/issuer-mismatch/audience-mismatch
            else ผ่าน (workforceEligible เป็นจริงเสมอในเลนนี้)
                API->>JIT: GetOrCreateAsync (transaction SERIALIZABLE, retry deadlock 1205 สูงสุด 2 ครั้ง)
                JIT->>JIT: หา LoginAccounts (provider, tid, oid)
                alt พบแล้วแต่ไม่ใช่ Employee
                    JIT-->>API: throw identity_account_type_conflict
                    API-->>Browser: 302 /login-error?reason=identity-account-type-conflict
                else พบแล้วแต่ไม่ Active
                    JIT-->>API: throw account_suspended
                    API-->>Browser: 302 /login-error?reason=account-suspended
                else พบแล้วและ Active Employee
                    JIT->>JIT: login.Observe(email, name, now) + SaveChanges + commit
                else ยังไม่มี
                    JIT->>JIT: insert Accounts(Employee, Active, AuthorizationVersion 0)<br/>+ LoginAccounts + Employees
                    alt commit สำเร็จ (ไม่มี race)
                        JIT->>JIT: commit
                    else DbUpdateException (แพ้ race), rollback แล้วหา winner จาก LoginAccounts
                        alt winner เป็น Employee และ Active
                            JIT->>JIT: ใช้ winner เป็นผลลัพธ์
                        else winner ไม่ใช่ Employee หรือไม่ Active
                            JIT-->>API: throw identity_account_type_conflict
                            API-->>Browser: 302 /login-error?reason=identity-account-type-conflict
                        end
                    end
                end
                Note over API,AZ: Phase C — sign login cookie แล้ววนกลับไป /oauth/authorize
                API->>API: sign accountId เข้า identity-login cookie (pol_login, 2 นาที), Entra tokens ถูกทิ้ง
                API-->>Browser: 302 ไป pending /oauth/authorize (ReturnUri)
                Browser->>AZ: GET /oauth/authorize (แนบ login cookie)
                AZ->>AZ: SignOut(identity-login) แล้ว SignIn OpenIddict ออก authorization code
                AZ-->>Browser: 302 กลับ SPA พร้อม code แล้ว SPA แลกที่ /oauth/token เป็น platform JWT + refresh
            end
        end
    end
```

---

## 1.4 OIDC callback ตัวแทน: registration session

หัวเดียวกับ § 1.3 แต่หลัง OnTicketReceived ไม่สร้าง account ไม่ออก BFF ticket ตรวจว่า identity ยังไม่มีบัญชีแล้วออก registration session cookie อายุสั้น (source: `src/Api/Api/IdentityAccess/IdentityAccessWiring.cs:58-67,157-202,219-228,258-272,312-315`, `src/Application/Modules/Accounts.Application/IdentityAccessContracts.cs:95-119`, `Persistence.ControlPlane/IdentityAccess/IdentityAccessStore.cs:152-169`)

```mermaid
sequenceDiagram
    autonumber
    participant IdP as Microsoft Entra<br/>(external)
    participant Browser
    participant API as API<br/>OpenIdConnect handler IdentityAgentMicrosoft
    participant REG as RegistrationSessionService + DB

    Note over IdP,API: Phase A — callback และแลก token (state/code/id_token เหมือน § 1.3)
    IdP-->>Browser: 302 callback?code&state
    Browser->>API: GET /api/v1/auth/agents/callback?code&state (AllowAnonymous)
    alt unprotect state ไม่ได้
        API-->>Browser: 401 ProblemDetails code invalid_authentication (OidcCallbackFallback)
    else access_denied
        API-->>Browser: 302 AgentWebAppBaseUrl/login-error?reason=access-denied
    else remote failure อื่น
        API-->>Browser: 302 /login-error?reason=auth-failed
    else ticket received (OnTicketReceived)
        Note over API,REG: Phase B — merchant context, policy แล้ว registration session
        API->>API: FromPrincipal(workforceEligible=false),<br/>อ่าน properties identity.merchant_id ที่ตั้งตอน login
        alt identity.merchant_id parse ไม่ได้หรือว่าง
            API-->>Browser: 302 /login-error?reason=registration-merchant-required
        else parse เป็น Guid ได้
            API->>API: HumanIdentityPolicy.Validate กับ AgentIssuer/AgentTenantId/AgentAudience
            alt policy ไม่ผ่าน
                API-->>Browser: 302 /login-error?reason=provider-not-allowed/tenant-mismatch/issuer-mismatch/audience-mismatch
            else ผ่าน
                API->>REG: HasApprovedAccountAsync (acct.LoginAccounts มี identity นี้แล้ว?)
                alt มี account อนุมัติแล้ว
                    REG-->>API: true
                    API-->>Browser: 302 /login-error?reason=account-already-approved
                else ยังไม่มี
                    REG->>REG: IssueAsync insert acct.RegistrationSessions,<br/>SHA-256(rawReference), merchantId, ExpiresAt=now+RegistrationSessionMinutes (default 30)
                    API-->>Browser: cookie pol_registration_session=rawReference (HttpOnly, Secure เมื่อ HTTPS)<br/>ไม่ออก BFF cookie ไม่แตะ acct.Accounts<br/>302 ToWebApp(/register, AgentWebAppBaseUrl)
                end
            end
        end
    end
```

---

## 1.5 อ่านบริบท session / account (identity-platform)

GET ทั้ง 4 ใช้ policy identity-platform (Bearer platform JWT) แล้ว handler อ่าน account สดอีกครั้ง ไม่มีการเขียน DB — `/auth/session` ถูก retire (source: `src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs:59-64,107-112,403-426`, `Persistence.ControlPlane/IdentityAccess/IdentityAccessStore.cs:217,249-346`)

```mermaid
sequenceDiagram
    autonumber
    actor U as Employee / Agent / SYSTEM client
    participant Client as SPA / integration client
    participant API
    participant DB

    Note over Client,API: Phase A — authenticate (ดู § 0.2)
    Client->>API: GET /api/v1/me (รวม /me/merchants, /me/access, /me/sessions)<br/>policy identity-platform, Authorization Bearer (platform JWT)
    alt claim sub ไม่ใช่ Guid ที่ไม่ว่าง
        API-->>Client: 401 bare status (ดู § 0.9)
    else sub ถูกต้อง
        Note over API,DB: Phase B — อ่าน account สด (READ-ONLY)
        API->>DB: FindAccountAsync(sub)
        DB-->>API: account
        alt account ไม่พบหรือไม่ Active
            API-->>Client: 401 bare status
        else Active
            Note over API,DB: Phase C — อ่านตาม endpoint
            alt /api/v1/me
                API->>DB: FindLoginEmailAsync(accountId)
                DB-->>API: email
                API-->>Client: 200 accountId, accountType, displayName, email,<br/>status, authorizationVersion, merchantContext (claim merchant_id)
            else /api/v1/me/merchants
                API->>DB: ListMerchantAccessAsync(accountId) กรอง AccessStatus.Active
                DB-->>API: merchant access rows
                API-->>Client: 200 array {merchantId, dataScope}
            else /api/v1/me/access
                API->>DB: ResolveAuthorizationAsync(merchant_id claim, client_id claim)
                DB-->>API: AuthorizationSnapshot หรือ null
                alt snapshot null
                    API-->>Client: 401 bare status
                else มี snapshot
                    API-->>Client: 200 AuthorizationSnapshot ทั้งก้อน (permissions, roles, branches, HasPlatformAccess)
                end
            else /api/v1/me/sessions
                API->>DB: OpenIddict authorizations ของ (accountId, WorkforceClientId)
                DB-->>API: EmployeeSessionView[] เรียง IssuedAt ล่าสุดก่อน
                API-->>Client: 200 array {sessionId, issuedAt, status, isLive}<br/>(ไม่พบ client = array ว่าง)
            end
        end
    end
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/me` | diagram หลัก: `FindAccountAsync` + `FindLoginEmailAsync` |
| GET | `/api/v1/me/merchants` | `FindAccountAsync` แล้ว `ListMerchantAccessAsync` กรอง `AccessStatus.Active` |
| GET | `/api/v1/me/access` | `FindAccountAsync` แล้ว `ResolveAuthorizationAsync`, snapshot null = 401 |
| GET | `/api/v1/me/sessions` | `ListMySessions`: OpenIddict authorizations ของ (accountId, `WorkforceClientId`), คืน `EmployeeSessionView`, ไม่พบ client = array ว่าง |

---


## 1.7 เพิกถอน session: logout และลบ login session ที่เลือก

logout เพิกถอน OpenIddict authorization ปัจจุบัน (ทุก token ของ login นั้น) แล้วตอบ 204 idempotent, DELETE me/sessions เพิกถอน authorization แถวที่เลือกของบัญชีตนเอง — ไม่มี cookie แล้ว (source: `src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs:48-50,113-119,428-444,609-620`)

```mermaid
sequenceDiagram
    autonumber
    actor U as Employee / Agent / SYSTEM client
    participant Client as SPA
    participant API
    participant AM as IOpenIddictAuthorizationManager

    Note over Client,API: Phase A — authenticate policy identity-platform + RequireIdentityPlatformMutation (ดู § 0.2, § 0.3)
    Client->>API: POST /api/v1/auth/logout<br/>หรือ DELETE /api/v1/me/sessions/{sessionId}
    alt lane logout
        API->>AM: FindByIdAsync(claim authorization_id)
        alt authorization พบและ subject = sub
            API->>AM: TryRevokeAsync (เพิกถอนทุก token ของ login นั้น)
        end
        API-->>Client: 204 No Content (idempotent, ไม่มี cookie)
    else lane DELETE me/sessions
        alt claim sub ไม่ใช่ Guid ที่ไม่ว่าง
            API-->>Client: 401 Unauthorized (ดู § 0.9)
        else sub ถูกต้อง
            API->>AM: FindByIdAsync(sessionId = authorizationId)
            alt authorization ไม่พบ หรือ subject ไม่ใช่ accountId
                AM-->>API: not found
                API-->>Client: 404 NotFound
            else เป็นของบัญชีตนเอง
                API->>AM: TryRevokeAsync (idempotent)
                API-->>Client: 204 No Content (ไม่แตะ cookie)
            end
        end
    end
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| POST | `/api/v1/auth/logout` | lane logout: อ่าน `authorization_id` จาก JWT, ตรวจ subject = sub, `TryRevokeAsync`, ไม่มี 401/404 จาก handler, ไม่มี cookie |
| DELETE | `/api/v1/me/sessions/{sessionId}` | lane DELETE: sessionId = authorizationId, 401 เมื่อ claim sub ไม่มี, 404 เมื่อ authorization ไม่ใช่ของบัญชี, `TryRevokeAsync` แถวเดียว |

---

## 1.8 OAuth token: client_credentials ด้วย private_key_jwt

OpenIddict ตรวจ protocol และ signature ของ client assertion ก่อน `SystemClientTokenRequestHandler` ตรวจ Account/SystemClient/key policy/jti replay ใน DB แล้ว passthrough ให้ `IssueToken` ตรวจ scope และ sign in principal (source: `src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs:32-37,171-240`, `Persistence.ControlPlane/OpenIddictRegistration.cs:28-67,91-196`, `Persistence.ControlPlane/IdentityAccess/IdentityAccessStore.cs:171-205,247-268`, `src/Application/Modules/Accounts.Application/IdentityAccessContracts.cs:148-205`)

```mermaid
sequenceDiagram
    autonumber
    participant Client as SYSTEM client<br/>(integration / service)
    participant API
    participant OI as OpenIddict server<br/>ValidateTokenRequest
    participant SCH as SystemClientTokenRequestHandler
    participant AS as SystemClientAssertionService
    participant DB
    participant EP as IssueToken handler

    Note over Client,OI: Phase A — protocol validation
    Client->>API: POST /oauth/token (grant_type, client_id,<br/>client_assertion_type jwt-bearer, client_assertion, scope) AllowAnonymous
    API->>OI: ExtractTokenRequest / ValidateTokenRequest,<br/>grant_type เปิด (authorization_code/client_credentials/refresh_token), client ลงทะเบียน, assertion signature ตรง JsonWebKeySet
    alt protocol validation ไม่ผ่าน
        OI-->>Client: 400 invalid_request/unsupported_grant_type/invalid_grant<br/>หรือ 401 invalid_client + WWW-Authenticate
    else grant_type=refresh_token และ client_id เป็น SYSTEM account
        OI->>SCH: ValidateTokenRequestContext event
        SCH-->>Client: 400 unauthorized_client<br/>SYSTEM clients cannot use refresh tokens
    else grant_type=client_credentials
        Note over OI,DB: Phase B — SystemClientTokenRequestHandler (SetOrder 100000)
        OI->>SCH: ValidateTokenRequestContext event
        SCH->>DB: FindSystemClientAsync(client_id)
        alt client_id/assertion/assertion_type ไม่ครบ
            SCH-->>Client: 401 invalid_client<br/>A private_key_jwt client assertion is required
        else client ไม่พบหรือไม่ใช่ System account
            SCH-->>Client: 401 invalid_client The SYSTEM client is not registered
        else account ไม่ Active
            SCH-->>Client: 401 invalid_client The SYSTEM account is suspended
        else parse assertion ล้มเหลวหรือ jti/nbf/exp/kid/alg ไม่ครบ
            SCH-->>Client: 401 invalid_client malformed/incomplete
        else kid ไม่ตรง ClientKeyPolicies ของ client
            SCH-->>Client: 401 invalid_client The client assertion key is not registered
        else kid ตรง
            SCH->>AS: ValidateAsync(account, client, key, assertion,<br/>now, tokenEndpoint, skew 30s, maxLifetime 60s)
            AS->>DB: TryClaimAsync(applicationId, jti)<br/>insert oauth.AssertionReplays (unique ApplicationId+Jti)
            alt ไม่ valid
                AS-->>SCH: code account_disabled/client_disabled/key_inactive/key_mismatch/<br/>audience_mismatch/assertion_expired/jti_required/assertion_replayed
                SCH-->>Client: 401 invalid_client + description=code
            else valid
                SCH-->>OI: ผ่าน (ไม่ reject)
                Note over OI,EP: Phase C — passthrough IssueToken
                OI->>EP: GetOpenIddictServerRequest
                alt request ไม่มี
                    EP-->>Client: 404 (Results.NotFound)
                else มี
                    EP->>DB: FindSystemClientAsync อีกครั้ง (account Active และ client Active)
                    alt ไม่ผ่าน
                        EP-->>Client: 401 {error invalid_client} WWW-Authenticate Basic realm oauth
                    else ผ่าน
                        EP->>EP: scope ที่ขอ (ไม่ส่ง=ทั้งหมดที่ลงทะเบียน) อยู่ใน SystemClientScopes ทั้งหมด?
                        alt scope เกินสิทธิ์ที่ลงทะเบียน
                            EP-->>Client: 400 {error invalid_scope}
                        else ผ่าน
                            EP->>OI: Results.SignIn principal (sub=accountId, account_type SYSTEM,<br/>authz_version, client_id, merchant_id, scopes, audience pol-core-api)
                            OI-->>Client: 200 {access_token, token_type, expires_in 300} ไม่มี refresh_token
                        end
                    end
                end
            end
        end
    end
```

---

## 1.9 OAuth authorize และ revoke

authorize ให้ OpenIddict ตรวจ client/redirect_uri/PKCE ก่อน passthrough ให้ handler challenge workforce login หรือ sign in เพื่อออก authorization code ส่วน revoke ไม่มี passthrough OpenIddict จัดการทั้งหมด (source: `src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs:95-105,280-301`, `Persistence.ControlPlane/OpenIddictRegistration.cs:28-45`, `src/Api/Api/Iam/ConsoleSessionAuthentication.cs:33-68`)

```mermaid
sequenceDiagram
    autonumber
    actor U as ผู้ใช้ (resource owner ผ่าน browser)
    participant Browser
    participant API
    participant OI as OpenIddict server
    participant IdP as Microsoft Entra<br/>(external, ดู § 1.3)

    Note over Browser,OI: Phase A — GET /oauth/authorize
    Browser->>API: GET /oauth/authorize (client_id, redirect_uri, response_type=code, PKCE)
    API->>OI: ExtractAuthorizationRequest / ValidateAuthorizationRequest,<br/>client_id ลงทะเบียน, redirect_uri ตรงที่ลงทะเบียน, code_challenge_method S256
    alt validation ไม่ผ่าน
        OI-->>Browser: 400 หรือ 302 redirect_uri พร้อม error (OpenIddict ตัดสินก่อน passthrough)
    else ผ่าน (passthrough Authorize handler)
        API->>API: request มีและ redirect_uri ไม่ว่าง?
        alt ไม่มีหรือว่าง
            API-->>Browser: 400 {error invalid_request}
        else มี
            alt http.User ยังไม่ authenticated<br/>(default scheme ConsoleSession เมื่อไม่มี policy = MerchantUserSession)
                API-->>Browser: 302 Results.Challenge(IdentityWorkforceMicrosoft)
                Browser->>IdP: ไป Entra authorize (callback ตาม § 1.3, RedirectUri=authorize URL นี้)
            else authenticated แต่ claim sub ไม่มี
                API-->>Browser: 401 {error login_required}
            else มี claim sub
                API->>OI: Results.SignIn principal (sub, name, scopes ที่ขอ, audience pol-core-api)
                OI-->>Browser: 302 redirect_uri พร้อม authorization code + state<br/>(insert oauth.OpenIddictTokens)
            end
        end
    end

    Note over Browser,OI: Phase B — POST /oauth/revoke (ไม่มี passthrough, OpenIddict จัดการทั้งหมด)
    Browser->>OI: POST /oauth/revoke (client authentication + token จาก reference/payload)
    alt client authentication ล้มเหลว
        OI-->>Browser: 400/401 invalid_client
    else token ไม่พบ
        OI-->>Browser: ผลตาม OpenIddict / RFC 7009 (นอก frame)
    else token เป็นของ client
        OI->>OI: set status Revoked ใน oauth.OpenIddictTokens
        OI-->>Browser: 200 {} (handler () => Results.Empty ที่ map ไม่ถูกเรียก)
    end
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/oauth/authorize` | lane authorize: passthrough ถึง handler, 400 `invalid_request`, 302 challenge หรือ 401 `login_required`, success = 302 redirect_uri พร้อม code |
| POST | `/oauth/revoke` | lane revoke: OpenIddict ตอบเองทั้งหมด, handler `() => Results.Empty` ให้ OpenAPI metadata เท่านั้น, success = 200 body ว่าง |

---

## Notes

Deviations และรายละเอียด edge-case ทั้งหมด (เช่น reason ของ login-error, provider ไม่ได้ตั้งค่า, อายุ cookie/session, `IdempotencyMutationMarker` ที่ handler ไม่บังคับจริงบน DELETE me/sessions) อยู่ที่ `01-identity-bff-oauth.activities.md` (หัวข้อ Deviations และ Notes) — ไฟล์นี้แสดงเฉพาะลำดับข้าม actor / browser / API / handler / DB / OpenIddict / IdP ตาม § เดียวกัน

- § 1.3 / § 1.4: gate `workforceEligible` ของ activity diagram เป็นจริงเสมอในเลนนี้ (kind กำหนดตายตัวจาก endpoint) จึงไม่วาดเป็น alt แยกในลำดับนี้ ดูรายละเอียดเต็มที่ activities.md
- § 1.1, § 1.8 lane `authorization_code`/`refresh_token` อื่นนอกเลนที่ตาราง endpoint ของ theme นี้ครอบ, § 1.9 revoke lane token ไม่พบ อยู่นอก frame (ไม่มี test ครอบ) ตามที่บันทึกไว้ใน Notes ของ activities.md
- authenticate/authorization ของ policy `identity-platform` (Bearer validation, IdentityAccessRequirement) อ้างที่ § 0.2 ในไฟล์ `00-cross-cutting.sequences.md`, CSRF/Bearer-mutation guard อ้างที่ § 0.3, error contract ทั่วไป (ProblemDetails, OIDC 302 reason) อ้างที่ § 0.9

**Render**: GitHub / Obsidian / VS Code Mermaid
