# pol-core API — Admin accounts, roles, sessions และ admin OIDC (Sequence Diagrams)

> Source: `docs/reference/api-endpoints.md` section "Admin และ merchant identity" บรรทัด L179-L203 และ section "OIDC callback ที่ middleware จัดการ" บรรทัด L392, source ที่อ้างต่อ § (`src/Api/Api/Program.cs:2418-2496,3148-3706`, `src/Api/Api/Admins/*`, `src/Application/Modules/Admins.Application/Users/*`, `Iam.Application/Roles/*`, `Merchants.Application/Users/ApproveReject.cs`, `GetRegistrationHistory.cs`)
> Scope: 9 § เดียวกับ `08-admin-accounts-auth.activities.md` (หมายเลข § ตรงกัน) แสดงลำดับข้าม actor / API / handler / DB / external
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

ลำดับข้าม browser, OIDC middleware scheme `AdminMicrosoft`, Microsoft Graph และ `LoginService` ตั้งแต่ challenge จนออก cookie session หรือ 302 ไปหน้า error (source: `src/Api/Api/Program.cs:2425-2444`, `Admins/OidcAuthentication.cs:91-227`, `Admins/LoginService.cs:127-226`, `src/Application/Modules/Admins.Application/Users/ResolveMicrosoftAdmin.cs:52-163`)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin console SPA
    participant API as API admin group
    participant MW as OIDC middleware<br/>AdminMicrosoft
    participant IDP as Entra ID<br/>(external)
    participant GRAPH as Microsoft Graph<br/>(external)
    participant LS as LoginService
    participant DB as DB<br/>PlatformUsers, Sessions, AuthAudits

    Note over A,API: Phase A — เริ่ม login
    A->>SPA: คลิกเข้าสู่ระบบ
    SPA->>API: GET /api/v1/admins/auth/{provider}/login?returnTo=, AllowAnonymous, rate limit admin-auth ดู § 0.4
    alt provider ไม่อยู่ใน AdminOidcProviders
        API-->>SPA: 404
    else provider ตั้งค่าแล้ว
        API->>API: ReturnUrlPolicy.Resolve(returnTo, allowlist)
        API-->>SPA: 302 Challenge ไป Microsoft authorize, PKCE S256 + state + nonce, scope openid email profile User.Read
    end

    Note over SPA,GRAPH: Phase B — callback ที่ middleware, protocol + workforce gate ก่อน Graph
    SPA->>IDP: ผู้ใช้ยืนยันตัวที่ Entra (นอก frame)
    IDP-->>MW: 302 code, state ไป /api/v1/admins/auth/microsoft/callback
    MW->>MW: ตรวจ state, code exchange, id_token issuer/aud/nonce/signature/lifetime, workforce gate tid ตรง tenant pin และ oid เป็น UUID เดียว

    alt protocol หรือ workforce gate ล้ม (access-denied, auth-failed, workforce-access-denied, employee-profile-unavailable จาก consent_required ก่อน token validate)
        MW->>LS: DenyAsync(reason) บน scope ใหม่
        LS->>DB: AuthAudit AuthDenied, SaveChanges
        LS-->>SPA: 302 WebAppBaseUrl + ErrorPath?reason=... ดู § 0.9
    else ผ่าน protocol + workforce gate
        MW->>GRAPH: GET /v1.0/me select employeeId mail userPrincipalName, ใช้ access token ของ callback, timeout 10s
        GRAPH-->>MW: employeeId, mail หรือ error

        alt Graph หรือ email gate ล้ม (employee-profile-missing/invalid/unavailable, workforce-email-unavailable)
            MW->>LS: DenyAsync(reason) บน scope ใหม่
            LS->>DB: AuthAudit AuthDenied, SaveChanges
            LS-->>SPA: 302 WebAppBaseUrl + ErrorPath?reason=... ดู § 0.9
        else ผ่านทุก gate
            Note over LS,DB: Phase C — resolve admin + establish session
            MW->>LS: OnTicketReceived, EstablishMicrosoftSessionAsync
            LS->>DB: keyed admin txn, AcquireIdentityMutationLock, GetByMicrosoftIdentity(tenantId, objectId)
            alt txn โยน exception, พบแล้ว Suspended, employeeId ซ้ำกับ admin อื่น หรือ HR mirror lookup ผิด/ล่ม
                LS->>DB: AuthAudit AuthDenied, SaveChanges
                LS-->>SPA: 302 ?reason=resolve-failed/suspended/identity-conflict/employee-profile-* ดูตาราง activities.md § 8.1
            else ไม่พบบัญชี หรือพบและ Active ผ่านทุกเช็ค
                LS->>DB: ไม่พบบัญชี = User.JitProvisionMicrosoft (Scoped, ไม่มี role) + audit JitProvision ก่อน
                LS->>DB: ทั้งสองกรณี (ใหม่หรือมีอยู่แล้ว) เรียก ApplyEmployeeProfile จาก HR mirror ต่อ (audit EmployeeBind เมื่อเพิ่งผูกครั้งแรก / EmployeeProfileSync เมื่อค่าที่มีอยู่เปลี่ยน), SaveChanges
                LS->>DB: Session.Start (token hash SHA-256, ip, user-agent) + AuthAudit LoginSuccess, SaveChanges
                alt เขียน session ล้ม
                    LS-->>SPA: 302 ?reason=session-write-failed
                else สำเร็จ
                    LS-->>SPA: Set-Cookie __Host-adm_session (HttpOnly) + adm_csrf, 302 WebAppBaseUrl + returnTo
                end
            end
        end
    end
```

รายละเอียด 12 reason, race JIT และตารางความต่างดู `08-admin-accounts-auth.activities.md` § 8.1

---

## 8.2 Logout เครื่องนี้ / ทุกเครื่อง

logout เป็น AllowAnonymous แบบ idempotent, logout-all ต้องมี session admin แล้ว revoke ทุก session (source: `src/Api/Api/Program.cs:2452-2496`)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin console SPA
    participant API as API admin group
    participant DB as DB<br/>PlatformUserSessions, AuthAudits

    Note over SPA,API: Phase A — เลือก endpoint
    alt POST /api/v1/admins/auth/logout
        SPA->>API: POST /api/v1/admins/auth/logout, AllowAnonymous, rate limit admin-auth + CSRF adm_csrf ดู § 0.4, § 0.3
        alt rate limit หรือ CSRF ไม่ผ่าน
            API-->>SPA: 429 Retry-After หรือ 403 csrf_failed
        else ไม่มี cookie หรือไม่พบ session
            API-->>SPA: ข้าม revoke, ล้างคุกกี้เสมอ, 204
        else พบ session
            API->>DB: RevokeFamilyAsync(session.FamilyId) เฉพาะเครื่องนี้, AuthAudit Logout, SaveChanges
            API-->>SPA: ล้างคุกกี้, 204
        end
    else POST /api/v1/admins/auth/logout-all
        SPA->>API: POST /api/v1/admins/auth/logout-all + cookie __Host-adm_session + X-CSRF-Token adm_csrf ดู § 0.1, § 0.3
        alt session หมดอายุ หรือ CSRF ไม่ผ่าน
            API-->>SPA: 401 admin_session_required หรือ 403 csrf_failed
        else ผ่าน
            API->>DB: RevokeAllForAdminAsync(scope.Current.AdminId) ทุกเครื่อง, AuthAudit LogoutAll, SaveChanges
            API-->>SPA: ล้างคุกกี้, 204
        end
    end
```

ตารางความต่างระหว่างสอง endpoint ดู `08-admin-accounts-auth.activities.md` § 8.2

---

## 8.3 Admin read model (me / list / detail)

reads ผ่าน gate § 0.1 แบบไม่มี transaction, `/me` อ่านจาก scope ที่ middleware bind ไว้, list ผ่าน SFS, detail ตรวจ existence ก่อนคืน ETag (source: `src/Api/Api/Program.cs:3234-3264,3312-3392`)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin console SPA
    participant API as API admin group
    participant H as Query handler
    participant DB as DB<br/>read-only

    Note over SPA,API: Phase A — gate เดียวกันทุก endpoint
    SPA->>API: GET (me / list / detail) + cookie __Host-adm_session
    API->>API: policy admin + RequirePermission ตามแถว (safe method ไม่มี CSRF) ดู § 0.1

    alt GET /api/v1/admins/me
        alt scope.IsBound = false
            API-->>SPA: 403 Your admin account is not active
        else bound
            API->>API: อ่าน IAdminScope.Current (ไม่ query DB ใหม่), map merchant id เป็น code
            API-->>SPA: 200 AdminMeResponse
        end
    else GET /api/v1/admins (list, permission user.view)
        API->>API: SfsQueryParser.Parse page/limit/filters/sort/search ดู § 0.6
        alt parse ไม่ผ่าน
            API-->>SPA: 400 Invalid request
        else parse ผ่าน
            API->>H: ListAdminsQuery
            H->>DB: อ่านหน้าเดียว, เติม role UserCount
            DB-->>H: PagedResult
            H-->>API: PagedResult
            API-->>SPA: 200 PagedResult
        end
    else GET /api/v1/admins/{id:guid} (detail, permission user.view)
        API->>H: GetAdminByIdQuery(id)
        H->>DB: existence / GetById
        DB-->>H: detail หรือ null
        alt ไม่พบ id
            API-->>SPA: 404
        else พบ
            API->>API: VersionEtags.Set (ETag vN) ดู § 0.5
            API-->>SPA: 200 AdminDetailResponse + ETag
        end
    end
```

composed (`effective-permissions`, `sessions` Super only, `permissions`, `roles`, `roles/{code}`) ใช้ gate และรูปแบบเดียวกัน — ตาราง flow ประกอบดู `08-admin-accounts-auth.activities.md` § 8.3

---

## 8.4 สร้าง Scoped Microsoft admin แบบ pre-bound

Super สร้างบัญชี Scoped จาก Entra object ID: validate ที่ handler ก่อนเข้า keyed admin txn ที่ lock identity แล้วกัน duplicate (source: `src/Api/Api/Program.cs:3269-3283`, `src/Application/Modules/Admins.Application/Users/CreateScopedAdmin.cs:42-72`)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin (Super)
    participant SPA as Admin console SPA
    participant API as API<br/>POST /api/v1/admins
    participant H as CreateScopedAdmin handler
    participant DB as DB<br/>PlatformUsers

    Note over SPA,API: Phase A — gate ที่ host
    SPA->>API: POST /api/v1/admins body objectId, email, identityApprovalReference + cookie + X-CSRF-Token
    API->>API: policy admin + RequirePlatformUserTier(Super) ดู § 0.1, CSRF adm_csrf ดู § 0.3
    API->>H: CreateScopedCommand

    Note over H,DB: Phase B — validate ที่ handler ก่อน keyed admin txn
    H->>H: objectId ไม่ Empty, identityApprovalReference ไม่ว่างและ <=128, email ผ่าน AdminContactEmail.TryNormalize
    alt validate ไม่ผ่าน
        H-->>API: ArgumentException
        API-->>SPA: 400 ProblemDetails
    else ผ่าน
        H->>DB: keyed admin txn, AcquireIdentityMutationLock, tenantId จาก IWorkforceTenantBindingStore
        H->>DB: มี admin ของ (tenantId, objectId) แล้วหรือไม่
        alt มีอยู่แล้ว
            H-->>API: ConflictException
            API-->>SPA: 409 An admin account already exists
        else ไม่ซ้ำ
            H->>DB: User.CreateScopedMicrosoft (Scoped, Active, Subject=oid), audit CreateScoped, SaveChanges
            H-->>API: adminId, email
            API-->>SPA: 201 Created Location /api/v1/admins/{adminId}
        end
    end
```

---

## 8.5 Super account mutations + set roles (If-Match)

mutation ต่อ admin หนึ่งคน: host pre-check (self 403, tier 400) ก่อน If-Match, assign merchant ตรวจ merchant Active ที่ handler นอก txn ก่อน จากนั้นเข้า keyed admin txn โหลด-เทียบ version-ทำกติกา domain (source: `src/Api/Api/Program.cs:3395-3502,3688-3706`)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin (Super, permission user.roles สำหรับ PUT roles)
    participant SPA as Admin console SPA
    participant API as API<br/>/admins/{id}
    participant H as Command handler
    participant DB as DB<br/>PlatformUsers, MerchantAccess

    Note over SPA,API: Phase A — gate + host pre-check (self / tier)
    SPA->>API: request tier/merchants/suspend/reactivate/roles + If-Match vN + cookie + X-CSRF-Token
    API->>API: policy admin + RequirePlatformUserTier(Super) หรือ permission user.roles (PUT roles) ดู § 0.1, CSRF ดู § 0.3

    alt suspend หรือ tier กับ id เดียวกับผู้เรียก
        API-->>SPA: 403 cannot suspend/change their own account
    else tier และ body.tier ไม่ใช่ super หรือ scoped
        API-->>SPA: 400 Unknown tier
    else ผ่าน pre-check
        API->>API: VersionEtags.Require(If-Match) ดู § 0.5
        API->>H: command(id, ...)
        alt POST merchants และร้านค้าไม่ Active (IsActiveMerchantAsync ที่ handler นอก txn)
            H-->>API: ConflictException
            API-->>SPA: 409 merchant does not exist or is not active
        else ผ่าน (หรือ endpoint อื่นไม่มีเช็คนี้)
            Note over H,DB: Phase B — keyed admin txn
            H->>DB: GetByIdAsync(id)
            alt ไม่พบ admin
                H-->>API: NotFoundException
                API-->>SPA: 404
            else version ไม่ตรง If-Match
                H-->>API: ConcurrencyConflict
                API-->>SPA: 409 state_conflict
            else version ตรง
                H->>DB: กติกาต่อ endpoint (assign/unassign merchant, suspend, reactivate, tier, set roles) ตารางความต่างดู activities.md § 8.5
                alt กติกา domain ล้ม (admin ไม่ใช่ Scoped, assignment ซ้ำ, role ไม่รู้จัก, assignment ไม่พบ)
                    H-->>API: 400/404/409 ตามตาราง
                    API-->>SPA: 4xx ProblemDetails
                else สำเร็จ (idempotent no-op เมื่อค่าเท่าเดิม)
                    H->>DB: BumpAuthorizationVersion + BumpResourceVersion เมื่อเปลี่ยนจริง, audit ต่อ action, SaveChanges
                    H-->>API: version ใหม่
                    API->>API: VersionEtags.Set (ETag vN ใหม่) ดู § 0.5
                    API-->>SPA: 200 หรือ 204 + ETag
                end
            end
        end
    end
```

ตารางความต่างของ 6 endpoint (tier, merchants POST/DELETE, suspend, reactivate, roles PUT) ดู `08-admin-accounts-auth.activities.md` § 8.5

---

## 8.6 เพิกถอน session family ของ admin (Idempotency-Key)

Super ส่ง Idempotency-Key แล้ว handler ทำ replay lookup ใน keyed admin txn ก่อนตรวจ admin/session แล้ว revoke ทั้ง rotation family (source: `src/Api/Api/Program.cs:3524-3547`, `src/Application/Modules/Admins.Application/Users/RevokeAdminSession.cs:50-96`)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin (Super)
    participant SPA as Admin console SPA
    participant API as API<br/>DELETE /admins/{id}/sessions/{sessionId}
    participant H as RevokeSessionHandler
    participant DB as DB<br/>PlatformUserSessions, AdminOperations

    Note over SPA,API: Phase A — gate
    SPA->>API: DELETE .../sessions/{sessionId} + Idempotency-Key + cookie + X-CSRF-Token
    API->>API: policy admin + RequirePlatformUserTier(Super) ดู § 0.1, CSRF ดู § 0.3, IdempotencyKeys.Require ดู § 0.5

    Note over H,DB: Phase B — keyed admin txn
    API->>H: RevokeSessionCommand
    H->>DB: AcquireAsync(actingAdminId, RevokePlatformUserSession, key), FindReplayAsync

    alt key เคยใช้ hash ไม่ตรง
        H-->>API: idempotency_key_reused
        API-->>SPA: 409
    else key เคยใช้ ยัง in progress
        H-->>API: operation_in_progress
        API-->>SPA: 409
    else key เคยสำเร็จแล้ว
        H-->>API: response เดิม
        API-->>SPA: 204 (replay)
    else key ใหม่, ไม่พบ admin (ExistsAsync)
        H-->>API: NotFoundException
        API-->>SPA: 404 admin account was not found
    else key ใหม่, ไม่พบ session หรือเจ้าของไม่ตรง (FindByIdAsync)
        H-->>API: NotFoundException (ไม่ leak เจ้าของ)
        API-->>SPA: 404 session was not found
    else key ใหม่, พบและตรงเจ้าของ
        H->>DB: RevokeFamilyAsync(FamilyId) ทั้ง rotation family, audit SessionRevoke, AddSucceeded 204, SaveChanges
        H-->>API: FamilyId
        API->>API: security log Admin.SessionManagement (sessionId, familyId, targetAdminId, correlationId)
        API-->>SPA: 204 No Content
    end
```

---

## 8.7 Role CRUD ฝั่ง Platform

mutation role ใช้ handler ร่วมกับ merchant console ผ่าน `RoleSideContext.Platform`: host แปลง status (400) ก่อน If-Match, create กัน code ซ้ำ, update/delete โหลดจาก visible set แล้วกัน seed anchor และ role ที่มีผู้ใช้ผูก (source: `src/Api/Api/Program.cs:3555-3565,3627-3685`)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin (permission user.roles)
    participant SPA as Admin console SPA
    participant API as API<br/>/admins/roles
    participant H as Role command handler
    participant DB as DB<br/>Roles, Accounts

    Note over SPA,API: Phase A — gate
    SPA->>API: POST/PUT/DELETE /api/v1/admins/roles(/code) + cookie + X-CSRF-Token
    API->>API: policy admin + permission user.roles ดู § 0.1, CSRF ดู § 0.3

    alt POST /api/v1/admins/roles (create)
        API->>API: ParseRoleStatus(body.status)
        alt status ผิดรูป
            API-->>SPA: 400 Invalid role status
        else status ถูก
            API->>H: CreateRoleCommand
            H->>DB: CodeExistsAsync(Platform, code)
            alt code ซ้ำใน visible set
                H-->>API: 409 A role with code already exists
            else permission key นอก catalog
                H-->>API: 400 ProblemDetails
            else ผ่าน
                H->>DB: _roles.Add, audit RoleCreated, SaveChanges
                API-->>SPA: 201 Created Location /admins/roles/{code} + ETag vN
            end
        end
    else PUT /api/v1/admins/roles/{code} (update, code จาก route)
        API->>API: ParseRoleStatus(body.status), VersionEtags.Require(If-Match) ดู § 0.5
        alt status ผิดรูป
            API-->>SPA: 400 Invalid role status
        else status ถูก
            API->>H: UpdateRoleCommand
            H->>DB: GetByCodeAsync(Platform, code)
            alt ไม่พบใน visible set
                H-->>API: 404 Role was not found
            else version ไม่ตรง If-Match
                H-->>API: 409 state_conflict
            else seed anchor platform_admin ปิดใช้งาน
                H-->>API: 409 role cannot be deactivated / deleted
            else permission key นอก catalog
                H-->>API: 400 ProblemDetails
            else ผ่าน
                H->>DB: lock Account rows ที่ผูก role (InvalidateAssignedAccountsAsync), Rename/SetDescription/SetColor/SetPermissions/Activate/Deactivate, BumpVersion, audit RoleUpdated, SaveChanges
                API-->>SPA: 200 RoleResponse + ETag vN
            end
        end
    else DELETE /api/v1/admins/roles/{code}
        API->>API: VersionEtags.Require(If-Match) ดู § 0.5
        API->>H: DeleteRoleCommand
        H->>DB: GetByCodeAsync(Platform, code)
        alt ไม่พบ, version ไม่ตรง หรือ seed anchor
            H-->>API: 404 หรือ 409 ตามตาราง
        else มีผู้ใช้ผูกอยู่ (CountAsync สองฝั่ง มากกว่า 0)
            H-->>API: 409 A role with bound users cannot be deleted
        else ผ่าน
            H->>DB: _roles.Remove (cascade permission grants), audit RoleDeleted, SaveChanges
            API-->>SPA: 204 No Content (ไม่มี ETag)
        end
    end
```

---

## 8.8 Merchant-user approve / reject โดย admin

approve ตรวจ merchantCode + accessible-merchant floor + merchant Active ที่ host ก่อนเข้า txn (พร้อม If-Match + Idempotency-Key), reject เข้า txn ตรง, ทั้งสองเดิน state machine PendingApproval ก่อน audit ในทรานแซกชันเดียว (source: `src/Api/Api/Program.cs:3148-3202`, `src/Application/Modules/Merchants.Application/Users/ApproveReject.cs:62-151,188-234,246-260`)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin (permission merchants.users.approve/reject)
    participant SPA as Admin console SPA
    participant API as API<br/>/admins/merchants/users/{id}
    participant H as ApproveReject handler
    participant DB as DB<br/>Accounts, AdminUserOperations

    Note over SPA,API: Phase A — gate + host pre-check (เฉพาะ approve)
    SPA->>API: POST .../approve หรือ .../reject + If-Match + Idempotency-Key + cookie + X-CSRF-Token
    API->>API: policy admin + permission merchants.users.approve/reject ดู § 0.1, CSRF ดู § 0.3

    alt POST .../approve และ body.merchantCode ว่าง
        API-->>SPA: 400 A merchant code is required to approve
    else POST .../approve, code มีค่าแต่ไม่พบใน accessible-merchant floor
        API-->>SPA: 404 Merchant not found or not in your scope
    else POST .../approve, merchant ไม่ Active
        API-->>SPA: 409 The selected merchant is not active
    else POST .../approve ผ่าน host pre-check
        API->>API: VersionEtags.Require + IdempotencyKeys.Require ดู § 0.5, actorScope.Begin(merchant, admin)
        API->>H: ApproveCommand ใน txn pol_admin, AcquirePaymentAuthorizationExclusive(merchantId)
    else POST .../reject
        API->>API: VersionEtags.Require + IdempotencyKeys.Require ดู § 0.5
        API->>H: RejectCommand ใน txn pol_admin
    end

    H->>DB: FindByIdAsync(merchantUserId)
    alt ไม่พบ merchant user
        H-->>API: 404 merchant-user registration was not found
    else replay ตาม (merchant, admin, operation, key) hash ไม่ตรง
        H-->>API: 409 idempotency_key_reused
    else replay ตรง (เคยทำสำเร็จแล้ว)
        H-->>API: result เดิม, 200
    else version ไม่ตรง If-Match
        H-->>API: 409 ConcurrencyConflict ดู § 0.5
    else approve, สถานะ Rejected/Suspended หรือ role ว่าง/ไม่รู้จัก
        H-->>API: 409 must be PendingApproval หรือ role unknown/inactive, หรือ 400 role ว่าง
    else approve, PendingApproval และ role ผ่าน
        H->>DB: User.Approve (PendingApproval ไป Active + MerchantId + RoleAssignment), RegistrationAudit Approved, record Succeeded, SaveChanges
        H-->>API: 200 userId, status, alreadyActive=false
    else approve, Active ร้านค้าเดิมอยู่แล้ว
        H-->>API: 200 alreadyActive=true (no-op, ยัง record Succeeded)
    else reject, สถานะไม่ใช่ PendingApproval
        H-->>API: 409 must be PendingApproval
    else reject, PendingApproval
        H->>DB: User.Reject (PendingApproval ไป Rejected), RevokeAllForUserAsync, RegistrationAudit Rejected (reason), record Succeeded, SaveChanges
        H-->>API: 200 userId, status
    end
    API-->>SPA: 200 + ETag vN หรือ 4xx ProblemDetails ตามผล H
```

---

## 8.9 Registration history พร้อม reveal audit

GET ที่อาจเขียน DB: หา merchant user ไม่ผ่าน merchant filter แล้วบังคับ accessible-merchant floor, `?reveal=true` ต้องบันทึก audit ก่อนประกอบ response แบบ fail-closed (source: `src/Api/Api/Program.cs:3209-3227`, `src/Application/Modules/Merchants.Application/Users/GetRegistrationHistory.cs:68-139`)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin (permission merchants.users.view)
    participant SPA as Admin console SPA
    participant API as API<br/>GET .../registrations
    participant H as GetRegistrationHistory handler
    participant DB as DB<br/>Accounts, RegistrationAudits

    Note over SPA,API: Phase A — gate (safe method ไม่มี CSRF)
    SPA->>API: GET .../registrations?reveal= + cookie __Host-adm_session
    API->>API: policy admin + permission merchants.users.view ดู § 0.1
    API->>H: GetRegistrationHistoryQuery(merchantUserId, reveal ค่าเริ่มต้น false, accessible merchant ids)

    H->>DB: IAccountResolver.FindByIdAsync (ไม่ผ่าน merchant query filter)
    alt ไม่พบ merchant user
        H-->>API: null
        API-->>SPA: 404 (ไม่เขียน audit)
    else MerchantId ผูกแล้วและ admin Scoped ไม่มี merchant นี้ใน accessible set
        H-->>API: null
        API-->>SPA: 404 (floor เดียวกับไม่พบ, ไม่ leak)
    else ผ่าน floor
        H->>DB: ListAttemptsAsync + ListAuditsAsync
        alt reveal=true
            H->>DB: RegistrationAudit Revealed (actor admin), SaveChanges ก่อนประกอบ response
            alt เขียน audit ล้ม
                API-->>SPA: 5xx ProblemDetails ดู § 0.9 (ไม่มี PII หลุด)
            else สำเร็จ
                H-->>API: attempts ค่าเต็ม
                API-->>SPA: 200 RegistrationHistoryResult
            end
        else reveal=false
            H->>H: mask PII (identity/license/phone เหลือ 4 ตัวท้าย, email ตัวแรก+domain)
            H-->>API: attempts ค่ามาสก์
            API-->>SPA: 200 RegistrationHistoryResult
        end
    end
```

---

## Notes

- ไฟล์นี้แสดงเฉพาะลำดับการเรียกข้าม actor/ระบบ ตารางความต่างของ endpoint composed และ Deviations ระหว่างเอกสารกับ source ดู `08-admin-accounts-auth.activities.md`
- reason `?reason=` ทั้ง 12 ค่าของ § 8.1 และ error code ของทุก § ตรงกับ ProblemDetails ตาม § 0.9 (`ArgumentException` 400, `NotFoundException` 404, `ConflictException`/`InvalidOperationException` 409, dependency ล้ม 503)
- ปลายทางของ 302 (WebAppBaseUrl, ErrorPath, ReturnUrlAllowlist) มาจาก config ต่อ environment จึงอยู่นอก frame ของ diagram เหมือนกับ activities.md
- session table ฝั่ง admin คือ `PlatformUserSessions`, audit login คือ `AuthAudits`, audit จัดการบัญชีคือ `Audit` ของ `Admins.Domain`

**Render**: GitHub / Obsidian / VS Code Mermaid
