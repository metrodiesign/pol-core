# pol-core API — Admin accounts, roles, sessions และ admin OIDC (Activity Diagrams)

> Source: `docs/reference/api-endpoints.md` section "Admin และ merchant identity" บรรทัด L179-L203 และ section "OIDC callback ที่ middleware จัดการ" บรรทัด L392, source ที่อ้างต่อ § (`src/Api/Api/Program.cs:2418-2496,3148-3706`, `src/Api/Api/Admins/*`, `src/Application/Modules/Admins.Application/Users/*`, `Iam.Application/Roles/*`, `Merchants.Application/Users/ApproveReject.cs`, `GetRegistrationHistory.cs`)
> Scope: 26 endpoints — admin OIDC login + middleware callback, logout, read model ของ admin / role / session, สร้าง Scoped admin, Super-only account mutations, session revoke, role CRUD, merchant-user approve / reject และ registration history
> Generated: 2026-09-14

| § | Diagram | Endpoints |
| --- | --- | --- |
| 8.1 | Admin OIDC login + middleware callback | `GET /api/v1/admins/auth/{provider}/login`, `GET /api/v1/admins/auth/microsoft/callback` |
| 8.2 | Logout เครื่องนี้ / ทุกเครื่อง | `POST /api/v1/admins/auth/logout`, `POST /api/v1/admins/auth/logout-all` |
| 8.3 | Admin read model (me / list / detail) | `GET /api/v1/admins/me`, `GET /api/v1/admins`, `GET /api/v1/admins/{id:guid}` + 5 composed |
| 8.4 | สร้าง Scoped Microsoft admin แบบ pre-bound | `POST /api/v1/admins` |
| 8.5 | Super account mutations + set roles (If-Match) | `POST /api/v1/admins/{id:guid}/tier` + 5 composed |
| 8.6 | เพิกถอน session family ของ admin (Idempotency-Key) | `DELETE /api/v1/admins/{id:guid}/sessions/{sessionId:guid}` |
| 8.7 | Role CRUD ฝั่ง Platform | `POST /api/v1/admins/roles`, `PUT /api/v1/admins/roles/{code}`, `DELETE /api/v1/admins/roles/{code}` |
| 8.8 | Merchant-user approve / reject โดย admin | `POST .../merchants/users/{merchantUserId:guid}/approve`, `POST .../reject` |
| 8.9 | Registration history พร้อม reveal audit | `GET /api/v1/admins/merchants/users/{merchantUserId:guid}/registrations` |

---

## 8.1 Admin OIDC login + middleware callback

login endpoint ตรวจ provider + returnTo แล้ว Challenge ไป Microsoft, callback ไม่มี endpoint map แต่ OIDC middleware ตรวจ protocol, workforce gate (tid / oid), Graph employeeId แล้วให้ LoginService JIT provision + สร้าง session ใน keyed admin transaction ก่อน 302 กลับ SPA, ทุก denial เขียน AuthDenied audit บน scope ใหม่แล้ว 302 ไป error page (source: `src/Api/Api/Program.cs:2425-2444`, `Admins/OidcAuthentication.cs:91-227`, `Admins/MicrosoftWorkforceClaims.cs:21-42,76-82`, `Admins/MicrosoftGraphEmployeeIdReader.cs:11-18,45-59`, `Admins/LoginService.cs:127-226`, `ReturnUrlPolicy.cs:8-14`, `Admins/SessionCookies.cs:45-68`, `src/Application/Modules/Admins.Application/Users/ResolveMicrosoftAdmin.cs:52-163`, `src/Api/appsettings.json:15,21,34-35`)

```mermaid
flowchart TD
    START((●)) --> LOGIN["GET /api/v1/admins/auth/{provider}/login?returnTo=<br/>AllowAnonymous, rate limit admin-auth ดู § 0.4"]
    LOGIN --> PROV{"provider slug อยู่ใน AdminOidcProviders<br/>(microsoft ที่ตั้ง ClientId แล้ว)?"}
    PROV -->|no| R404["404 (Results.NotFound ไม่มี body)"]
    PROV -->|yes| RET["ReturnUrlPolicy.Resolve: returnTo ต้องเป็น same-origin path ใน allowlist<br/>ไม่ผ่านใช้ DefaultReturnPath (ไม่ 400)"]
    RET --> CHAL["302 Challenge ไป Microsoft authorize<br/>response_type=code, PKCE S256, state, nonce, prompt=select_account<br/>scope openid email profile User.Read"]
    CHAL --> IDP["ผู้ใช้ยืนยันตัวที่ Entra ID (นอก frame)"]
    IDP --> CB["GET /api/v1/admins/auth/microsoft/callback?code&state<br/>OIDC middleware scheme AdminMicrosoft (ไม่มี endpoint map)"]
    CB --> PROTO{"state / correlation cookie ตรง, code exchange สำเร็จ,<br/>id_token ผ่าน issuer / aud / nonce / signature / lifetime?"}
    PROTO -->|"error=access_denied"| DENY_AD["reason access-denied (OnAccessDenied)"]
    PROTO -->|no| DENY_RF["OnRemoteFailure: reason จาก BrowserReason<br/>auth-failed หรือ workforce-access-denied (issuer ผิด)<br/>หรือ employee-profile-unavailable (consent_required)"]
    PROTO -->|yes| GATE{"OnTokenValidated: tid = tenant ที่ pin<br/>และ oid เป็น UUID ค่าเดียว?"}
    GATE -->|no| DENY_WF["reason workforce-access-denied"]
    GATE -->|yes| GRAPH["GET Graph /v1.0/me?$select=employeeId,mail,userPrincipalName<br/>ด้วย access token ของ callback (timeout 10 s, SaveTokens=false)"]
    GRAPH --> EMP{"employeeId ผ่าน EmployeeIdPolicy?"}
    EMP -->|"transport / status / ไม่มี token"| DENY_EU["reason employee-profile-unavailable"]
    EMP -->|missing| DENY_EM["reason employee-profile-missing"]
    EMP -->|invalid| DENY_EI["reason employee-profile-invalid"]
    EMP -->|ok| MAIL{"มี email จาก id_token หรือ Graph mail / UPN?"}
    MAIL -->|no| DENY_MAIL["reason workforce-email-unavailable"]
    MAIL -->|yes| TICKET["OnTicketReceived: LoginService.EstablishMicrosoftSessionAsync<br/>ResolveMicrosoftAdminCommand ใน keyed admin txn"]
    TICKET --> LOCK["AcquireIdentityMutationLock<br/>GetByMicrosoftIdentity(tenantId, objectId)"]
    LOCK --> RX{"txn โยน exception (DB / lock)?"}
    RX -->|yes| DENY_RS["reason resolve-failed"]
    RX -->|no| EXIST{"พบบัญชี?"}
    EXIST -->|no| JIT["JIT provision: User.JitProvisionMicrosoft (Scoped, ไม่มี role)<br/>audit JitProvision"]
    EXIST -->|yes| SUSP{"Status Suspended?"}
    SUSP -->|yes| DENY_SUSP["reason suspended"]
    SUSP -->|no| HR
    JIT --> HR{"employeeId ซ้ำกับ admin คนอื่น?"}
    HR -->|yes| DENY_IC["reason identity-conflict<br/>audit reason employee-taken"]
    HR -->|no| HRLOOK{"HR mirror lookup (IEmployeeProfileReader)?"}
    HRLOOK -->|missing| DENY_EM
    HRLOOK -->|invalid| DENY_EI
    HRLOOK -->|"source unavailable"| DENY_HR["reason employee-profile-unavailable<br/>audit reason hr-source-unavailable"]
    HRLOOK -->|found| APPLY["ApplyEmployeeProfile (employeeId, ชื่อ, สกุล)<br/>audit EmployeeBind / EmployeeProfileSync เมื่อเปลี่ยน<br/>SaveChanges"]
    APPLY --> RESOLVE["resolve accessible merchants (Super = All, Scoped = assigned)<br/>+ effective permissions + AuthorizationVersion"]
    RESOLVE --> SESS["Session.Start (token hash SHA-256, ip, user-agent 256)<br/>+ AuthAudit LoginSuccess SaveChanges ร่วมกัน"]
    SESS --> SESSOK{"บันทึก session สำเร็จ?"}
    SESSOK -->|no| DENY_SW["reason session-write-failed"]
    SESSOK -->|yes| COOKIE["Set-Cookie __Host-adm_session (HttpOnly) + adm_csrf (JS อ่านได้)<br/>302 WebAppBaseUrl + returnTo (SafeReturn)"]
    COOKIE --> END_S((◉))
    DENY_AD --> DENY
    DENY_RF --> DENY
    DENY_WF --> DENY
    DENY_EU --> DENY
    DENY_EM --> DENY
    DENY_EI --> DENY
    DENY_MAIL --> DENY
    DENY_RS --> DENY
    DENY_SUSP --> DENY
    DENY_IC --> DENY
    DENY_HR --> DENY
    DENY_SW --> DENY
    DENY["LoginService.DenyAsync: audit AuthDenied บน scope ใหม่<br/>302 WebAppBaseUrl + ErrorPath?reason=... (ค่าเริ่มต้น /login-error)"]
    DENY --> END_F((◉))
    R404 --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef ext fill:#4a3a1f,stroke:#d29922,color:#fff
    class CHAL,JIT,APPLY,RESOLVE,SESS,COOKIE,END_S ok
    class R404,DENY_AD,DENY_RF,DENY_WF,DENY_EU,DENY_EM,DENY_EI,DENY_MAIL,DENY_RS,DENY_SUSP,DENY_IC,DENY_HR,DENY_SW,DENY,END_F fail
    class PROV,PROTO,GATE,EMP,MAIL,RX,EXIST,SUSP,HR,HRLOOK,SESSOK gate
    class IDP,GRAPH ext
```

| reason (query `?reason=`) | trigger | audit reason | source |
| --- | --- | --- | --- |
| `access-denied` | OAuth `error=access_denied` จาก provider | เท่ากับ reason | `OidcAuthentication.cs:209-214` |
| `auth-failed` | state ไม่ตรง, code exchange ล้ม, remote failure ที่จำแนกไม่ได้, ticket ไม่มี claims | เท่ากับ reason | `OidcAuthentication.cs:191-198,217-226`, `MicrosoftWorkforceClaims.cs:82` |
| `workforce-access-denied` | tid ไม่ตรง tenant pin, oid ไม่ใช่ UUID เดียว หรือ `SecurityTokenInvalidIssuerException` | เท่ากับ reason | `MicrosoftWorkforceClaims.cs:27-33,99-102` |
| `employee-profile-unavailable` | Graph transport / status / parse, ไม่มี access token, `consent_required` หรือ HR mirror ล่ม | `hr-source-unavailable` เมื่อมาจาก HR lookup | `MicrosoftGraphEmployeeIdReader.cs:13`, `ResolveMicrosoftAdmin.cs:141`, `ResolveAdmin.cs:53-54` |
| `employee-profile-missing` / `employee-profile-invalid` | Graph ไม่มี employeeId / รูปผิด หรือ HR lookup ตอบ Missing / Invalid | เท่ากับ reason | `MicrosoftGraphEmployeeIdReader.cs:14-15`, `ResolveMicrosoftAdmin.cs:139-140` |
| `workforce-email-unavailable` | ไม่มี email ทั้งจาก id_token และ Graph | เท่ากับ reason | `OidcAuthentication.cs:176-181` |
| `suspended` | บัญชีมีอยู่แต่ Status Suspended (ไม่ re-provision) | เท่ากับ reason | `ResolveMicrosoftAdmin.cs:111-114`, `LoginService.cs:166` |
| `identity-conflict` | employeeId ถูกผูกกับ admin คนอื่น | `employee-taken` | `ResolveMicrosoftAdmin.cs:132-133`, `ResolveAdmin.cs:45` |
| `resolve-failed` / `session-write-failed` | command โยน exception / SaveChanges session ล้ม | เท่ากับ reason | `LoginService.cs:153-158,195-201` |

---

## 8.2 Logout เครื่องนี้ / ทุกเครื่อง

logout เป็น AllowAnonymous แบบ idempotent (ไม่มี cookie หรือไม่พบ session ก็ 204) แต่ยังผ่าน rate limit + CSRF ของ group, logout-all ต้อง session admin แล้ว revoke ทุก session ของ admin คนนั้น (source: `src/Api/Api/Program.cs:2452-2496`, `Admins/SessionCookies.cs:70-93`)

```mermaid
flowchart TD
    START((●)) --> WHICH{"endpoint?"}
    WHICH -->|"POST /api/v1/admins/auth/logout"| RL["AllowAnonymous, rate limit admin-auth ดู § 0.4<br/>CSRF adm_csrf จาก group ดู § 0.3"]
    RL --> RLOK{"ผ่าน rate limit + CSRF?"}
    RLOK -->|no| R4XX["429 Retry-After หรือ 403 csrf_failed"]
    RLOK -->|yes| COOKIE{"มี cookie __Host-adm_session?"}
    COOKIE -->|no| CLEAR
    COOKIE -->|yes| FIND["FindByTokenHashAsync(SHA-256 token)"]
    FIND --> FOUND{"พบ session?"}
    FOUND -->|no| CLEAR
    FOUND -->|yes| REVF["RevokeFamilyAsync(session.FamilyId) เฉพาะเครื่องนี้<br/>AuthAudit Logout, SaveChanges"]
    REVF --> CLEAR
    WHICH -->|"POST /api/v1/admins/auth/logout-all"| AUTHZ["policy admin ดู § 0.1<br/>CSRF adm_csrf ดู § 0.3"]
    AUTHZ --> AZOK{"ผ่าน auth + CSRF?"}
    AZOK -->|no| R401["401 admin_session_required หรือ 403 csrf_failed"]
    AZOK -->|yes| REVA["RevokeAllForAdminAsync(scope.Current.AdminId) ทุกเครื่อง<br/>AuthAudit LogoutAll, SaveChanges"]
    REVA --> CLEAR["cookies.Clear: ลบ __Host-adm_session + adm_csrf"]
    CLEAR --> R204["204 No Content"]
    R204 --> END_S((◉))
    R4XX --> END_F((◉))
    R401 --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class REVF,REVA,CLEAR,R204,END_S ok
    class R4XX,R401,END_F fail
    class WHICH,RLOK,COOKIE,FOUND,AZOK gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| POST | `/api/v1/admins/auth/logout` | AllowAnonymous + rate limit `admin-auth`, cookie ไม่มีหรือ session ไม่พบ = ข้าม revoke แต่ล้าง cookie เสมอ, revoke เฉพาะ family ของ cookie ที่ส่งมา |
| POST | `/api/v1/admins/auth/logout-all` | policy `admin` (401 เมื่อ session หมดอายุ), revoke ทุก family ของ admin, audit `LogoutAll` |

---

## 8.3 Admin read model (me / list / detail)

reads ทั้งหมดผ่าน gate § 0.1 แล้วส่ง query แบบไม่มี transaction: `/me` อ่านจาก IAdminScope ที่ middleware bind ไว้ (403 ถ้าไม่ bound), list ผ่าน SfsQueryParser, detail ตรวจ existence ก่อนแล้วคืน 404 หรือ 200 + ETag (source: `src/Api/Api/Program.cs:3234-3264,3312-3392,3506-3520,3568-3624`, `src/Application/Modules/Admins.Application/Users/UserQueries.cs:11-88`, `Iam.Application/Roles/RoleQueries.cs:11-51`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy admin + RequirePermission / RequirePlatformUserTier ตามแถว ดู § 0.1<br/>(safe method ไม่มี CSRF)"]
    AUTHZ --> KIND{"endpoint?"}
    KIND -->|"GET /api/v1/admins/me"| BOUND{"scope.IsBound?"}
    BOUND -->|no| R403["403 ProblemDetails<br/>Your admin account is not active"]
    BOUND -->|yes| ME["อ่าน IAdminScope.Current (ไม่ query ใหม่)<br/>Super = isUnrestricted, Scoped = map merchant id ไปเป็น code<br/>permissions = effective permissions"]
    ME --> R200
    KIND -->|"GET /api/v1/admins (list)"| SFS["SfsQueryParser.Parse ดู § 0.6<br/>page, limit, filters, sort, search"]
    SFS --> SFSOK{"parse ผ่าน (JSON ถูก, ไม่เกิน cap)?"}
    SFSOK -->|no| R400["400 Invalid request ดู § 0.6"]
    SFSOK -->|yes| LIST["query handler อ่านหน้าเดียว (ไม่มี transaction)<br/>roles เติม UserCount ต่อรายการ"]
    LIST --> R200
    KIND -->|"GET /api/v1/admins/{id:guid} (detail)"| Q["query handler: existence / GetById ก่อน<br/>แล้วอ่าน projection (accessible, role codes)"]
    Q --> NULLQ{"ผลเป็น null?"}
    NULLQ -->|yes| R404["404 ProblemDetails"]
    NULLQ -->|no| ETAG["VersionEtags.Set เมื่อมี EtagResponseMarker ดู § 0.5"]
    ETAG --> R200["200 JSON wire form<br/>tier / status / role status ตัวพิมพ์เล็ก"]
    R200 --> END_S((◉))
    R403 --> END_F((◉))
    R400 --> END_F
    R404 --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class ME,LIST,ETAG,R200,END_S ok
    class R403,R400,R404,END_F fail
    class KIND,BOUND,SFSOK,NULLQ gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/admins/me` | diagram — ไม่มี permission, 403 เมื่อ scope ไม่ bound, 503 เมื่ออ่าน merchant directory ล้ม (§ 0.9) |
| GET | `/api/v1/admins` | diagram — permission `user.view`, SFS filters email / tier / status, sort email / createdAt, search email, ค่านอกโดเมน 400 |
| GET | `/api/v1/admins/{id:guid}` | diagram — permission `user.view`, 404 เมื่อไม่พบ id, ETag `vN`, roleCodes รวม role Inactive, 503 เมื่อ directory ล้ม |
| GET | `/api/v1/admins/{id:guid}/effective-permissions` | permission `user.view`, ExistsAsync ก่อน (404), union permission ของ role ACTIVE เรียง ordinal, ใช้ได้กับบัญชี Suspended, ไม่มี ETag |
| GET | `/api/v1/admins/{id:guid}/sessions` | RequirePlatformUserTier(Super) แทน permission, ExistsAsync ก่อน (404), admin จริงที่ไม่มี session = 200 + [], isLive คำนวณตอนอ่าน, ไม่คืน token hash |
| GET | `/api/v1/admins/permissions` | ไม่มี permission, catalog `Scope.Platform`, ไม่มี 404 / SFS |
| GET | `/api/v1/admins/roles` | ไม่มี permission, SFS, RoleSideContext Platform (visible set), UserCount ต่อ role |
| GET | `/api/v1/admins/roles/{code}` | ไม่มี permission, 404 เมื่อ code ไม่อยู่ใน visible set, ETag `vN`, UserCount |

---

## 8.4 สร้าง Scoped Microsoft admin แบบ pre-bound

Super สร้างบัญชี Scoped จาก Entra object ID ที่ตรวจแล้ว: validate ที่ handler (400) ก่อนเข้า keyed admin txn ที่ lock identity, อ่าน tenant pin, กัน duplicate (409) แล้ว 201 (source: `src/Api/Api/Program.cs:3269-3283`, `src/Application/Modules/Admins.Application/Users/CreateScopedAdmin.cs:42-72`, `src/Domain/Modules/Admins.Domain/Users/User.cs:111-127`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy admin + RequirePlatformUserTier(Super) ดู § 0.1<br/>RequireCsrf adm_csrf ดู § 0.3"]
    AUTHZ --> BODY["body objectId, email, identityApprovalReference<br/>actingAdminId + correlationId จาก server"]
    BODY --> V1{"objectId ไม่ใช่ Guid.Empty?"}
    V1 -->|no| R400["400 ProblemDetails<br/>(ArgumentException ดู § 0.9)"]
    V1 -->|yes| V2{"identityApprovalReference มีค่า<br/>และยาวไม่เกิน 128?"}
    V2 -->|no| R400
    V2 -->|yes| V3{"email ผ่าน AdminContactEmail.TryNormalize?"}
    V3 -->|no| R400
    V3 -->|yes| TXN["keyed admin txn: AcquireIdentityMutationLock<br/>tenantId จาก IWorkforceTenantBindingStore (tenant pin)"]
    TXN --> DUP{"มี admin ของ (tenantId, objectId) แล้ว?"}
    DUP -->|yes| R409["409 ProblemDetails<br/>An admin account already exists"]
    DUP -->|no| CREATE["User.CreateScopedMicrosoft (Scoped, Active, Subject = oid)<br/>audit CreateScoped (correlation = approval reference)<br/>SaveChanges"]
    CREATE --> R201["201 Created Location /api/v1/admins/{adminId}<br/>body adminId, email"]
    R201 --> END_S((◉))
    R400 --> END_F((◉))
    R409 --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class CREATE,R201,END_S ok
    class R400,R409,END_F fail
    class V1,V2,V3,DUP gate
```

---

## 8.5 Super account mutations + set roles (If-Match)

mutation ต่อ admin หนึ่งคน: host pre-check (self 403, tier 400) ก่อน If-Match, assign merchant ตรวจ merchant Active นอก txn ก่อน, จากนั้นใน keyed admin txn โหลด admin (404) เทียบ version (409 state_conflict) ทำกติกา domain แล้ว bump AuthorizationVersion + Version พร้อม audit (source: `src/Api/Api/Program.cs:3395-3502,3688-3706`, `src/Application/Modules/Admins.Application/Users/AssignMerchant.cs:39-69`, `UnassignMerchant.cs:36-58`, `SuspendAdmin.cs:35-53`, `ReactivateAdmin.cs:40-65`, `ChangeAdminTier.cs:39-57`, `SetAdminRoles.cs:41-94`, `src/Domain/Modules/Admins.Domain/Users/User.cs:146-184`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy admin + RequirePlatformUserTier(Super)<br/>หรือ permission user.roles (PUT roles) ดู § 0.1<br/>CSRF adm_csrf ดู § 0.3"]
    AUTHZ --> HOST{"host pre-check (suspend / tier):<br/>id = scope.Current.AdminId?"}
    HOST -->|yes| R403["403 ProblemDetails<br/>cannot suspend / change their own account"]
    HOST -->|no| TIERV{"POST tier: body.tier เป็น super หรือ scoped?"}
    TIERV -->|no| R400T["400 ProblemDetails Unknown tier"]
    TIERV -->|yes| IFM["VersionEtags.Require(If-Match) ดู § 0.5"]
    IFM --> PRE{"POST merchants: IsActiveMerchantAsync(merchantId)<br/>(นอก txn)?"}
    PRE -->|no| R409M["409 ProblemDetails<br/>merchant does not exist or is not active"]
    PRE -->|yes| TXN["keyed admin txn: GetByIdAsync(id)"]
    TXN --> FOUND{"พบ admin?"}
    FOUND -->|no| R404["404 ProblemDetails<br/>admin account was not found"]
    FOUND -->|yes| VER{"admin.Version = If-Match?"}
    VER -->|no| R409V["409 ProblemDetails code state_conflict"]
    VER -->|yes| DOMAIN{"กติกาต่อ endpoint (ตารางด้านล่าง)?"}
    DOMAIN -->|"409"| R409D["409 ProblemDetails<br/>Scoped only / duplicate assignment"]
    DOMAIN -->|"404"| R404A["404 assignment ไม่พบ (DELETE merchants)"]
    DOMAIN -->|"400"| R400R["400 Unknown role codes (PUT roles)"]
    DOMAIN -->|ok| MUT["mutate aggregate / assignment rows (idempotent no-op เมื่อค่าเท่าเดิม)<br/>reactivate: RevokeAllForAdminAsync เมื่อ Suspended ไป Active<br/>BumpAuthorizationVersion + BumpResourceVersion เมื่อเปลี่ยนจริง"]
    MUT --> AUDIT["audit ต่อ action: AssignMerchant, UnassignMerchant, Suspend,<br/>Reactivate, TierChanged, RoleAssigned / RoleUnassigned<br/>SaveChanges"]
    AUDIT --> RESP["ETag vN ใหม่ (VersionEtags.Set)<br/>200 (POST merchants, POST tier) หรือ 204 (อื่น)"]
    RESP --> END_S((◉))
    R403 --> END_F((◉))
    R400T --> END_F
    R409M --> END_F
    R404 --> END_F
    R409V --> END_F
    R409D --> END_F
    R404A --> END_F
    R400R --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class MUT,AUDIT,RESP,END_S ok
    class R403,R400T,R409M,R404,R409V,R409D,R404A,R400R,END_F fail
    class HOST,TIERV,PRE,FOUND,VER,DOMAIN gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| POST | `/api/v1/admins/{id:guid}/tier` | diagram — Super, self 403 ที่ host, tier ผิด 400 ที่ host, tier เท่าเดิม no-op (ไม่ bump), audit `TierChanged`, 200 adminId / tier / version + ETag |
| POST | `/api/v1/admins/{id:guid}/merchants` | Super, body merchantId, IsActiveMerchantAsync 409 ก่อน txn, admin ต้อง Scoped (409), assignment ซ้ำ 409, MerchantAccess.Create + audit `AssignMerchant`, 200 assignmentId / version + ETag |
| DELETE | `/api/v1/admins/{id:guid}/merchants/{merchantId:guid}` | Super, assignment ไม่พบ 404 หลัง version check, hard delete + audit `UnassignMerchant`, 204 + ETag |
| POST | `/api/v1/admins/{id:guid}/suspend` | Super, self 403 ที่ host, Suspended อยู่แล้ว = no-op แต่ยัง audit `Suspend`, 204 + ETag, ไม่ revoke session (session handler ปฏิเสธเองต่อ request) |
| POST | `/api/v1/admins/{id:guid}/reactivate` | Super, ไม่มี self-check, Suspended ไป Active = RevokeAllForAdminAsync ใน txn เดียว (ต้อง login ใหม่), Active อยู่แล้ว idempotent (audit `Reactivate` ยังเขียน), 204 + ETag |
| PUT | `/api/v1/admins/{id:guid}/roles` | permission `user.roles` แทน Super, body roleCodes (trim / distinct), role code ไม่รู้จัก 400 หลัง version check, add / remove assignment + audit ต่อรายการ, bump เฉพาะเมื่อ set เปลี่ยน, 204 + ETag |

---

## 8.6 เพิกถอน session family ของ admin (Idempotency-Key)

Super ส่ง Idempotency-Key (ไม่มี If-Match) แล้ว handler ทำ replay lookup ใน keyed admin txn ก่อนตรวจ admin / session แล้ว revoke ทั้ง rotation family + audit + บันทึก operation record, host เขียน security log หลัง commit (source: `src/Api/Api/Program.cs:3524-3547`, `src/Application/Modules/Admins.Application/Users/RevokeAdminSession.cs:50-96`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy admin + RequirePlatformUserTier(Super) ดู § 0.1<br/>CSRF adm_csrf ดู § 0.3"]
    AUTHZ --> IDEM["IdempotencyKeys.Require(Idempotency-Key) ดู § 0.5<br/>(ไม่มี If-Match)"]
    IDEM --> TXN["keyed admin txn: IAdminOperationStore.AcquireAsync<br/>(actingAdminId, RevokePlatformUserSession, key)"]
    TXN --> PRIOR{"มี record ของ key นี้?"}
    PRIOR -->|"request hash ไม่ตรง"| R409R["409 code idempotency_key_reused"]
    PRIOR -->|"in progress"| R409P["409 code operation_in_progress"]
    PRIOR -->|"succeeded"| REPLAY["คืน response เดิม (204)"]
    PRIOR -->|no| EXISTS{"admin id มีอยู่ (ExistsAsync)?"}
    EXISTS -->|no| R404A["404 admin account was not found"]
    EXISTS -->|yes| SESSQ{"FindByIdAsync(sessionId) พบ<br/>และ AdminUserId = id?"}
    SESSQ -->|no| R404S["404 session was not found (ไม่ leak เจ้าของ)"]
    SESSQ -->|yes| REV["RevokeFamilyAsync(FamilyId) ทั้ง rotation family (no-op ถ้า revoke แล้ว)<br/>audit SessionRevoke<br/>AddSucceeded record 204 หมดอายุ 24 h, SaveChanges"]
    REV --> LOG["security log Admin.SessionManagement<br/>sessionId, familyId, targetAdminId, correlationId"]
    LOG --> R204["204 No Content"]
    REPLAY --> R204
    R204 --> END_S((◉))
    R409R --> END_F((◉))
    R409P --> END_F
    R404A --> END_F
    R404S --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class REPLAY,REV,LOG,R204,END_S ok
    class R409R,R409P,R404A,R404S,END_F fail
    class PRIOR,EXISTS,SESSQ gate
```

---

## 8.7 Role CRUD ฝั่ง Platform

mutation ของ role ใช้ handler ร่วมกับ merchant console ผ่าน RoleSideContext Platform: host แปลง status (400) ก่อน If-Match, create กัน code ซ้ำ (409) และ permission key นอก catalog (400), update / delete โหลดจาก visible set (404) เทียบ version (409) แล้วกัน seed anchor `platform_admin` และ role ที่มีผู้ใช้ผูก (409) (source: `src/Api/Api/Program.cs:3555-3565,3627-3685`, `src/Application/Modules/Iam.Application/Roles/CreateRole.cs:35-50`, `UpdateRole.cs:40-76`, `DeleteRole.cs:36-59`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy admin + permission user.roles ดู § 0.1<br/>CSRF adm_csrf ดู § 0.3"]
    AUTHZ --> METHOD{"method?"}
    METHOD -->|"POST /api/v1/admins/roles"| STATUS{"ParseRoleStatus(body.status) ที่ host:<br/>active หรือ inactive?"}
    METHOD -->|"PUT /api/v1/admins/roles/{code}"| STATUS
    METHOD -->|"DELETE /api/v1/admins/roles/{code}"| IFM
    STATUS -->|no| R400S["400 Invalid role status (ArgumentException)"]
    STATUS -->|"yes, POST"| TXN_C["keyed admin txn: CodeExistsAsync(Platform, code)"]
    STATUS -->|"yes, PUT"| IFM["VersionEtags.Require(If-Match) ดู § 0.5"]
    IFM --> TXN_U["keyed admin txn: GetByCodeAsync(Platform, code)"]
    TXN_C --> DUP{"code ซ้ำใน visible set?"}
    DUP -->|yes| R409C["409 A role with code already exists<br/>(UNIQUE index เป็น backstop)"]
    DUP -->|no| CREATE{"Role.Create: ทุก permission key อยู่ใน catalog ฝั่ง Platform?"}
    CREATE -->|no| R400P["400 ProblemDetails (ArgumentException)"]
    CREATE -->|yes| SAVE_C["_roles.Add + audit RoleCreated, SaveChanges"]
    SAVE_C --> R201["201 Created Location /api/v1/admins/roles/{code}<br/>RoleResponse + ETag vN"]
    TXN_U --> FOUND{"พบ role ใน visible set?"}
    FOUND -->|no| R404["404 Role was not found"]
    FOUND -->|yes| VER{"role.Version = If-Match?"}
    VER -->|no| R409V["409 code state_conflict"]
    VER -->|yes| ANCHOR{"role เป็น seed anchor (platform_admin)<br/>และ PUT status inactive หรือ DELETE?"}
    ANCHOR -->|yes| R409A["409 role cannot be deactivated / deleted"]
    ANCHOR -->|no| OP{"method?"}
    OP -->|PUT| INV["InvalidateAssignedAccountsAsync (lock Account rows ก่อน)<br/>Rename, SetDescription, SetColor, SetPermissions,<br/>Activate / Deactivate, BumpVersion"]
    INV --> PERMOK{"permission keys อยู่ใน catalog ฝั่งเดียวกับ role?"}
    PERMOK -->|no| R400P
    PERMOK -->|yes| SAVE_U["audit RoleUpdated, SaveChanges, CountAsync users"]
    SAVE_U --> R200["200 RoleResponse + ETag vN"]
    OP -->|DELETE| BOUND{"CountAsync(role) = 0 ทั้งสองฝั่ง?"}
    BOUND -->|no| R409B["409 A role with bound users cannot be deleted"]
    BOUND -->|yes| SAVE_D["_roles.Remove (cascade permission grants)<br/>audit RoleDeleted, SaveChanges"]
    SAVE_D --> R204["204 No Content (ไม่มี ETag)"]
    R201 --> END_S((◉))
    R200 --> END_S
    R204 --> END_S
    R400S --> END_F((◉))
    R409C --> END_F
    R400P --> END_F
    R404 --> END_F
    R409V --> END_F
    R409A --> END_F
    R409B --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class SAVE_C,R201,INV,SAVE_U,R200,SAVE_D,R204,END_S ok
    class R400S,R409C,R400P,R404,R409V,R409A,R409B,END_F fail
    class METHOD,STATUS,DUP,CREATE,FOUND,VER,ANCHOR,OP,PERMOK,BOUND gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| POST | `/api/v1/admins/roles` | diagram — ไม่มี If-Match, code จาก body (trim), 409 code ซ้ำก่อน Role.Create, 400 permission key นอก catalog, 201 + Location + ETag, UserCount = 0 |
| PUT | `/api/v1/admins/roles/{code}` | diagram — code จาก route (body ไม่เปลี่ยน code), If-Match, 404 นอก visible set, 409 state_conflict / deactivate platform_admin, lock Account rows ที่ผูก role ก่อนแก้, 200 + ETag |
| DELETE | `/api/v1/admins/roles/{code}` | diagram — If-Match (EmitsEtag false), ไม่แตะ body.status, 409 seed anchor / มีผู้ใช้ผูก, 204 ไม่มี ETag |

---

## 8.8 Merchant-user approve / reject โดย admin

approve ตรวจ merchantCode + accessible-merchant floor + merchant Active ที่ host ก่อนเข้า txn (พร้อม If-Match + Idempotency-Key และ actor scope), reject เข้า txn ตรง, ทั้งสองทำ replay lookup ตาม key, เทียบ version แล้วเดิน state machine PendingApproval ก่อน audit + operation record ใน txn เดียว (source: `src/Api/Api/Program.cs:3148-3202`, `src/Application/Modules/Merchants.Application/Users/ApproveReject.cs:62-151,188-234,246-260`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy admin + permission merchants.users.approve / merchants.users.reject ดู § 0.1<br/>CSRF adm_csrf ดู § 0.3"]
    AUTHZ --> WHICH{"endpoint?"}
    WHICH -->|"POST .../{merchantUserId:guid}/approve"| CODE{"body.merchantCode มีค่า?"}
    CODE -->|no| R400C["400 A merchant code is required to approve"]
    CODE -->|yes| FLOOR["IAdminQuery.GetMerchantByCodeAsync<br/>accessible-merchant floor (Scoped เห็นเฉพาะที่ assign)"]
    FLOOR --> FOUNDM{"พบ merchant ใน scope?"}
    FOUNDM -->|no| R404M["404 Merchant not found or not in your scope"]
    FOUNDM -->|yes| ACTIVE{"merchant.Status = Active?"}
    ACTIVE -->|no| R409M["409 The selected merchant is not active"]
    ACTIVE -->|yes| HDR["VersionEtags.Require + IdempotencyKeys.Require ดู § 0.5<br/>actorScope.Begin(merchant.Id, adminId)"]
    HDR --> TXN_A["txn pol_admin: AcquirePaymentAuthorizationExclusive(merchantId)<br/>IAccountStore.FindByIdAsync(merchantUserId)<br/>roleCodes ว่าง = ใช้ IntendedRoleCodes ของ invitation ที่ accept"]
    WHICH -->|"POST .../{merchantUserId:guid}/reject"| HDR_R["VersionEtags.Require + IdempotencyKeys.Require ดู § 0.5"]
    HDR_R --> TXN_R["txn pol_admin: IAccountStore.FindByIdAsync(merchantUserId)<br/>reason trim + ตัดที่ 1024 (ว่าง = null)"]
    TXN_A --> FOUNDU{"พบ merchant user?"}
    TXN_R --> FOUNDU
    FOUNDU -->|no| R404U["404 merchant-user registration was not found"]
    FOUNDU -->|yes| REPLAY{"มี AdminUserOperationRecord ของ (merchant, admin, operation, key)?"}
    REPLAY -->|"intent hash ไม่ตรง"| R409K["409 code idempotency_key_reused"]
    REPLAY -->|"ตรง"| RESP_OLD["คืน result เดิม (200)"]
    REPLAY -->|no| VER{"account.EnsureVersion(If-Match) ผ่าน?"}
    VER -->|no| R409V["409 ConcurrencyConflict ดู § 0.5"]
    VER -->|yes| OPK{"endpoint?"}
    OPK -->|approve| STATE_A{"status ของ merchant user?"}
    STATE_A -->|"Active, merchant เดิม"| IDEMA["no-op: 200 alreadyActive=true<br/>+ record Succeeded, SaveChanges"]
    STATE_A -->|"Active, merchant อื่น"| R409O["409 (InvalidOperationException จาก User.Approve)"]
    STATE_A -->|"Rejected / Suspended"| R409S["409 must be PendingApproval"]
    STATE_A -->|PendingApproval| ROLES{"roleCodes ว่าง?"}
    ROLES -->|yes| R400R["400 At least one role must be assigned"]
    ROLES -->|no| RES{"ทุก code visible + Active<br/>(GetActiveRoleIdsByCodesAsync)?"}
    RES -->|no| R409R["409 Role(s) unknown or inactive"]
    RES -->|yes| APPROVE["User.Approve: PendingApproval ไป Active + MerchantId<br/>RoleAssignment ต่อ role, RegistrationAudit Approved<br/>record Succeeded 200, SaveChanges"]
    OPK -->|reject| STATE_R{"status = PendingApproval?"}
    STATE_R -->|no| R409S
    STATE_R -->|yes| REJECT["User.Reject: PendingApproval ไป Rejected<br/>RevokeAllForUserAsync, RegistrationAudit Rejected (reason)<br/>record Succeeded 200, SaveChanges"]
    APPROVE --> R200["200 + ETag vN<br/>approve: userId, status, alreadyActive<br/>reject: userId, status"]
    REJECT --> R200
    IDEMA --> R200
    RESP_OLD --> R200
    R200 --> END_S((◉))
    R400C --> END_F((◉))
    R404M --> END_F
    R409M --> END_F
    R404U --> END_F
    R409K --> END_F
    R409V --> END_F
    R409O --> END_F
    R409S --> END_F
    R400R --> END_F
    R409R --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class IDEMA,RESP_OLD,APPROVE,REJECT,R200,END_S ok
    class R400C,R404M,R409M,R404U,R409K,R409V,R409O,R409S,R400R,R409R,END_F fail
    class WHICH,CODE,FOUNDM,ACTIVE,FOUNDU,REPLAY,VER,OPK,STATE_A,ROLES,RES,STATE_R gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| POST | `/api/v1/admins/merchants/users/{merchantUserId:guid}/approve` | diagram — permission `merchants.users.approve`, body merchantCode + roleCodes, floor 404 / merchant ไม่ Active 409 ที่ host, actor scope bind ก่อน dispatch, AcquirePaymentAuthorizationExclusive ใน txn, idempotent 200 alreadyActive |
| POST | `/api/v1/admins/merchants/users/{merchantUserId:guid}/reject` | diagram — permission `merchants.users.reject`, body reason (optional), ไม่มี host pre-check, revoke ทุก session ของ merchant user, 200 userId / status |

---

## 8.9 Registration history พร้อม reveal audit

GET ที่อาจเขียน DB: หา merchant user แบบไม่ผ่าน merchant filter (404), บังคับ accessible-merchant floor (404 เดียวกัน ไม่ leak), ถ้า `?reveal=true` ต้องบันทึก RegistrationAudit Revealed ก่อนประกอบ response, ค่าเริ่มต้น mask PII (source: `src/Api/Api/Program.cs:3209-3227`, `src/Application/Modules/Merchants.Application/Users/GetRegistrationHistory.cs:68-139`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy admin + permission merchants.users.view ดู § 0.1<br/>(safe method ไม่มี CSRF)"]
    AUTHZ --> Q["GetRegistrationHistoryQuery(merchantUserId, reveal ค่าเริ่มต้น false,<br/>IsUnrestricted, accessible merchant ids จาก IAdminScope)"]
    Q --> FIND["IAccountResolver.FindByIdAsync (ไม่ผ่าน merchant query filter)"]
    FIND --> FOUND{"พบ merchant user?"}
    FOUND -->|no| R404["404 (ไม่เขียน audit ใด)"]
    FOUND -->|yes| FLOOR{"MerchantId ผูกแล้ว และ admin เป็น Scoped<br/>ที่ไม่มี merchant นี้ใน accessible set?"}
    FLOOR -->|yes| R404
    FLOOR -->|no| READ["ListAttemptsAsync + ListAuditsAsync"]
    READ --> REVEAL{"?reveal=true"}
    REVEAL -->|yes| AUD["RegistrationAudit Revealed (actor admin)<br/>SaveChanges ก่อนประกอบ response (fail-closed)"]
    AUD --> AUDOK{"บันทึก audit สำเร็จ?"}
    AUDOK -->|no| R5XX["5xx ProblemDetails ดู § 0.9<br/>(ไม่มี PII ออกจาก handler)"]
    AUDOK -->|yes| FULL["attempts ค่าเต็ม"]
    REVEAL -->|no| MASK["mask PII: identity / license / phone เหลือ 4 ตัวท้าย<br/>email เหลือตัวแรก + domain"]
    FULL --> R200["200 RegistrationHistoryResult<br/>subject, status, attempts (เรียง attemptNo), timeline"]
    MASK --> R200
    R200 --> END_S((◉))
    R404 --> END_F((◉))
    R5XX --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class READ,AUD,FULL,MASK,R200,END_S ok
    class R404,R5XX,END_F fail
    class FOUND,FLOOR,REVEAL,AUDOK gate
```

---

## Deviations

| fullPath | เอกสารบอก | source บอก | อ้างอิง |
| --- | --- | --- | --- |
| `/api/v1/admins` (POST) | policy `admin` · CSRF filter | เพิ่ม `RequirePlatformUserTier(Tier.Super)` เฉพาะ Super สร้างได้ (403 `super_required`) | `src/Api/Api/Program.cs:3275` |
| `/api/v1/admins/{id:guid}/merchants` (POST) | policy `admin` | `RequirePlatformUserTier(Tier.Super)` + IfMatchMutationMarker | `src/Api/Api/Program.cs:3402-3403` |
| `/api/v1/admins/{id:guid}/merchants/{merchantId:guid}` (DELETE) | policy `admin` | `RequirePlatformUserTier(Tier.Super)` + IfMatchMutationMarker | `src/Api/Api/Program.cs:3422-3423` |
| `/api/v1/admins/{id:guid}/suspend` (POST) | policy `admin` | `RequirePlatformUserTier(Tier.Super)` + IfMatchMutationMarker | `src/Api/Api/Program.cs:3445-3446` |
| `/api/v1/admins/{id:guid}/reactivate` (POST) | policy `admin` | `RequirePlatformUserTier(Tier.Super)` + IfMatchMutationMarker | `src/Api/Api/Program.cs:3466-3467` |
| `/api/v1/admins/{id:guid}/tier` (POST) | policy `admin` | `RequirePlatformUserTier(Tier.Super)` + IfMatchMutationMarker | `src/Api/Api/Program.cs:3492-3493` |
| `/api/v1/admins/{id:guid}/sessions` (GET) | policy `admin` | `RequirePlatformUserTier(Tier.Super)` | `src/Api/Api/Program.cs:3512` |
| `/api/v1/admins/{id:guid}/sessions/{sessionId:guid}` (DELETE) | policy `admin` | `RequirePlatformUserTier(Tier.Super)` + IdempotencyMutationMarker | `src/Api/Api/Program.cs:3536-3537` |
| `/api/v1/admins/*` (unsafe method ทุกตัวใน group รวม POST `/auth/logout` ที่ AllowAnonymous) | ระบุ CSRF filter เฉพาะ POST `/api/v1/admins` | group `api.MapGroup("/admins").RequireCsrf()` บังคับ adm_csrf ทุก unsafe method ใต้ `/admins` (logout, logout-all, approve, reject, roles, merchants, suspend, reactivate, tier, roles ของ admin, sessions) | `src/Api/Api/Program.cs:2418,2451` |

## Notes

- Callback `/api/v1/admins/auth/microsoft/callback` เป็น path จาก `AdminAuth:Providers:Microsoft:CallbackPath` (`appsettings.json:21`) ที่ production boot guard บังคับ, ไม่มี `MapGet` — handler จริงคือ event ของ OpenIdConnect scheme `AdminMicrosoft`
- reason `not-provisioned` ใน `LoginService.cs:165` มีไว้สำหรับ historical non-Microsoft resolver (`ResolveQuery`) เท่านั้น: `ResolveMicrosoftAdminHandler` JIT provision เสมอจึงไม่คืน `NotFound` ใน Microsoft path
- race ตอน JIT provision: `ConflictException` ใน txn แรกให้ retry ผ่าน `IAdminIdentityRecoveryReader.ResolveAfterConflictAsync` (ไม่มี employeeId) หรือ run ซ้ำหนึ่งครั้ง (มี employeeId) ก่อนตอบ `identity-conflict` (`ResolveMicrosoftAdmin.cs:74-93`)
- ที่อยู่ปลายทางของ 302 (WebAppBaseUrl, ScalarBaseUrl สำหรับ `/scalar` ใน Development, ErrorPath, ReturnUrlAllowlist) มาจาก config ต่อ environment จึงอยู่นอก frame ของ diagram
- คอลัมน์ caller / policy ของเอกสารไม่ระบุ header marker (`IfMatchMutationMarker`, `IdempotencyMutationMarker`, `EtagResponseMarker`) ทุก § วาดตาม source และอ้าง § 0.5 แทน
- `AuthRateLimiting.RequireAdminIdentityMutationRateLimit` (policy `admin-identity-mutation-ip`) ถูกประกาศไว้แต่ไม่มี endpoint ใน theme นี้เรียกใช้ (`Admins/AuthRateLimiting.cs:40-61`)
- error body ทุก 4xx / 5xx เป็น ProblemDetails ตาม § 0.9: `ArgumentException` 400, `NotFoundException` 404, `ConflictException` / `InvalidOperationException` 409, dependency ล้ม 503 (`/me`, `/{id}` ระบุ 503 ใน metadata)
- session table ฝั่ง admin คือ `PlatformUserSessions` (เก็บ SHA-256 ของ token, rotation family) และ audit login คือ `AuthAudits`, audit การจัดการบัญชีคือ `Audit` ของ Admins.Domain (action ต่อ command)

**Render**: GitHub / Obsidian / VS Code Mermaid
