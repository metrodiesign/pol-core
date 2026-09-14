# pol-core API — Canonical access, roles และ SYSTEM clients (Sequence Diagrams)

> Source: `docs/reference/api-endpoints.md` section "Identity และ OAuth" บรรทัด L32-L40,L63-L75 พร้อม source ที่อ้างต่อ § (`src/Api/Api/IdentityAccess/CanonicalAccessEndpoints.cs`, `src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs:131-169,331-447`, `Persistence.ControlPlane/IdentityAccess/IdentityAccessStore.cs`, `Persistence.ControlPlane/Admins/AdminOperationStore.cs`, `src/Application/Modules/Iam.Application/Roles/CreateRole.cs`, `UpdateRole.cs`, `Persistence.ControlPlane/Iam/RoleAuthorizationInvalidator.cs`)
> Scope: 6 § เดียวกับ `02-canonical-access-system-clients.activities.md` (หมายเลข § ตรงกัน) แสดงลำดับข้าม actor (Admin) / API / store หรือ mediator handler / OpenIddict / DB
> Generated: 2026-09-14

| § | Diagram | Endpoints |
| --- | --- | --- |
| 2.1 | อ่านรายการ/รายละเอียดระดับ Platform | `GET /api/v1/accounts`, `GET /api/v1/accounts/{accountId:guid}`, `GET /api/v1/accounts/{accountId:guid}/merchant-access`, `GET /api/v1/accounts/{accountId:guid}/platform-access`, `GET /api/v1/permissions`, `GET /api/v1/roles`, `GET /api/v1/roles/{roleId:guid}` |
| 2.2 | อ่านรายการ/รายละเอียด SYSTEM client ภายใน merchant scope | `GET /api/v1/system-clients`, `GET /api/v1/system-clients/{clientId:guid}`, `GET /api/v1/system-clients/{clientId:guid}/keys` |
| 2.3 | แก้ไข/เพิกถอนสิทธิ์ระดับ account (idempotent executor จริง) | `PATCH /api/v1/accounts/{accountId:guid}`, `PUT /api/v1/accounts/{accountId:guid}/merchant-access/{merchantId:guid}`, `DELETE /api/v1/accounts/{accountId:guid}/merchant-access/{merchantId:guid}`, `PUT /api/v1/accounts/{accountId:guid}/platform-access`, `POST /api/v1/accounts/{accountId:guid}/session-revocations` |
| 2.4 | สร้าง/แก้ไข Platform role (ไม่มี idempotent replay จริง) | `POST /api/v1/roles`, `PUT /api/v1/roles/{roleId:guid}` |
| 2.5 | สร้าง SYSTEM client + ลงทะเบียน scope | `POST /api/v1/system-clients` |
| 2.6 | แก้ไข/แทนที่สิทธิ์ SYSTEM client และจัดการ public key | `PATCH /api/v1/system-clients/{clientId:guid}`, `PUT /api/v1/system-clients/{clientId:guid}/access`, `POST /api/v1/system-clients/{clientId:guid}/keys`, `DELETE /api/v1/system-clients/{clientId:guid}/keys/{keyId:guid}` |

---

## 2.1 อ่านรายการ/รายละเอียดระดับ Platform

ลำดับเดียวกันสำหรับทั้ง 7 endpoint: gate แล้ว list หรือ read-only catalog (Phase B) หรือ detail ผ่าน lookup ตรง/ResolveRoleAsync (Phase C) (source: `CanonicalAccessEndpoints.cs:28-93,162-226`, `IdentityAccessStore.cs:344-393`)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin Console
    participant API as GET endpoint (7 แบบ)
    participant ST as IdentityAccessStore / mediator
    participant DB as DB (acct.Accounts, iam.Roles, access.MerchantAccess, access.PlatformAccess)

    Note over A,API: Phase A - gate (ดู § 0.1, GET ข้าม CSRF ดู § 0.3)
    A->>SPA: เปิดรายการหรือรายละเอียด
    SPA->>API: GET + cookie session (permission user.manage หรือ user.roles)
    Note over API,DB: Phase B - list (accounts / roles / merchant-access) หรือ read-only catalog (permissions)
    alt accounts list
        API->>API: SfsQueryParser.Parse ดู § 0.6 (ใช้แค่ Page/Limit)
        API->>ST: ListAccountsAsync(Page, Limit)
        ST->>DB: ORDER BY Id, join LoginAccounts + SystemClients
        DB-->>ST: items, total
        ST-->>API: PagedResult AccountAdminView
        API-->>SPA: 200 PagedResult
    else roles list
        API->>API: SfsQueryParser.Parse ดู § 0.6
        API->>ST: mediator.Send(ListRolesQuery)
        ST->>DB: filter/sort/search จริง + CountManyAsync ต่อ role
        DB-->>ST: items, total
        ST-->>API: PagedResult RoleListItem
        API-->>SPA: 200 PagedResult
    else merchant-access list
        API->>ST: ListMerchantAccessAsync(accountId)
        ST->>DB: WHERE AccountId (ไม่ตรวจว่า account มีจริง)
        DB-->>ST: access rows (อาจว่าง)
        ST-->>API: MerchantAccessAdminView[]
        API-->>SPA: 200 [] หรือรายการ
    else permissions catalog
        API->>ST: mediator.Send(GetPermissionCatalogQuery(Platform))
        ST-->>API: CanonicalPermissionCatalog
        API-->>SPA: 200 groups + permissions
    end
    Note over API,DB: Phase C - detail (accounts / platform-access / roles)
    alt roles detail
        API->>ST: ResolveRoleAsync
        ST->>DB: roles.ListAsync(Limit=int.MaxValue) ดึงทุก role
        DB-->>ST: rows ทั้งหมด
        ST->>ST: filter ด้วย roleId ใน memory (ดู Notes)
        alt ไม่พบ
            ST-->>API: null
            API-->>SPA: 404 bare NotFound
        else พบ
            ST->>DB: counter.CountAsync(roleId) แยกอีก 1 เส้น
            DB-->>ST: userCount
            ST-->>API: view + version
            API-->>SPA: 200 body + ETag = vN (ดู § 0.5)
        end
    else accounts หรือ platform-access detail
        API->>ST: query ตรงด้วย accountId
        ST->>DB: SingleOrDefault(id)
        alt ไม่พบ
            DB-->>ST: null
            ST-->>API: null
            API-->>SPA: 404 bare NotFound
        else พบ
            DB-->>ST: row
            ST-->>API: view + version
            API-->>SPA: 200 body + ETag = vN (ดู § 0.5)
        end
    end
```

---

## 2.2 อ่านรายการ/รายละเอียด SYSTEM client ภายใน merchant scope

scope check ใช้ `scope.Accessible.Allows` ตอบ 404 เพื่อซ่อนการมีอยู่ (source: `IdentityAccessEndpoints.cs:134-158,331-361,409-416`)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin Console
    participant API as GET /system-clients (3 แบบ)
    participant ST as IdentityAccessStore
    participant DB as DB (iam.SystemClients, iam.ClientKeyPolicies)

    Note over A,API: Phase A - gate policy admin + permission user.manage (ดู § 0.1)
    A->>SPA: เปิดรายการ SYSTEM client หรือ keys
    SPA->>API: GET + cookie session
    Note over API,DB: Phase B - list (merchantId เป็น optional)
    alt ระบุ merchantId และไม่ผ่าน scope.Accessible.Allows
        API-->>SPA: 404 (ซ่อนการมีอยู่)
    else ระบุ merchantId และผ่าน
        API->>ST: ListSystemClientsAsync(merchantId)
        ST->>DB: WHERE MerchantId = ที่ระบุ
        DB-->>ST: rows
        ST-->>API: SystemClientAdminView[]
        API-->>SPA: 200 (ไม่มี secret)
    else ไม่ระบุ merchantId
        API->>ST: ListSystemClientsAsync(null)
        ST->>DB: ทุก client ข้าม merchant (ไม่กรอง scope ของ admin ดู Notes)
        DB-->>ST: rows
        ST-->>API: SystemClientAdminView[]
        API-->>SPA: 200
    end
    Note over API,DB: Phase C - detail หรือ keys
    API->>ST: GetSystemClientAsync(clientId)
    ST->>DB: SingleOrDefault(Id)
    DB-->>ST: client หรือ null
    alt ไม่พบ หรือ scope.Accessible.Allows(MerchantId) เป็น false
        ST-->>API: null / ปฏิเสธ
        API-->>SPA: 404 (ซ่อนการมีอยู่)
    else detail
        API-->>SPA: 200 SystemClientAdminView (ไม่มี ETag header แม้ marker ประกาศไว้ ดู Notes)
    else keys
        API->>ST: ListClientKeysAsync(clientId)
        ST->>DB: WHERE SystemClientId
        DB-->>ST: key rows
        ST-->>API: ClientKeyAdminView[]
        API-->>SPA: 200 (ไม่มี ETag)
    end
```

---

## 2.3 แก้ไข/เพิกถอนสิทธิ์ระดับ account (idempotent executor จริง)

`ExecuteIdempotentAsync` ทำ lock, replay, โหลด resource, ตรวจ domain และเทียบ version ทั้งหมดในทรานแซกชันเดียว แล้ว audit เป็นรอบแยกหลังจากนั้น (source: `CanonicalAccessEndpoints.cs:120-160,242-329`, `IdentityAccessStore.cs:395-437,769-844,854-924`)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin Console
    participant API as PATCH/PUT/DELETE/POST (5 endpoint)
    participant ST as IdentityAccessStore.ExecuteIdempotentAsync
    participant DB as DB (acct.Accounts, access.MerchantAccess, access.PlatformAccess, admin.OperationRecords)

    Note over A,API: Phase A - gate + header (ดู § 0.1, § 0.3)
    A->>SPA: แก้ไข/เพิกถอนสิทธิ์ หรือ revoke session
    SPA->>API: request + Idempotency-Key (+ If-Match ยกเว้น session-revocations)
    alt Idempotency-Key หรือ If-Match ผิดรูป
        API-->>SPA: 400 invalid_idempotency_key / invalid_etag
    else ผ่าน
        API->>ST: ExecuteIdempotentAsync(actor, operation, key, intent, action)
        Note over ST,DB: Phase B - idempotency + โหลด resource + ตรวจ domain + version ในทรานแซกชันเดียว
        ST->>DB: app lock admin-operation(actor, operation) + lookup OperationRecord (key)
        alt record เดิม hash ต่างกัน
            ST-->>API: ConflictException idempotency_key_reused
            API-->>SPA: 409 ProblemDetails
        else record เดิม InProgress หรือไม่มี response
            ST-->>API: ConflictException operation_in_progress
            API-->>SPA: 409 ProblemDetails
        else record เดิม Succeeded
            ST-->>API: response เดิม (Replayed)
            Note over API,DB: Phase C - audit แยกทรานแซกชัน (ไม่ atomic กับ Phase B, บาง endpoint เขียนซ้ำตอน replay ดู Notes)
            API->>DB: audit.Append + SaveChanges
            API-->>SPA: 200/202/204 + ETag เดิม
        else ไม่พบ resource (account / access row / platform-access row)
            ST-->>API: NotFoundException
            API-->>SPA: 404
        else domain validation ไม่ผ่าน (cross-merchant role/branch, Employee-only)
            ST-->>API: InvalidRequestException
            API-->>SPA: 400 cross_merchant_role / cross_merchant_branch / invalid_target
        else version ไม่ตรง (เฉพาะ endpoint ที่มีให้เทียบ)
            ST-->>API: ConcurrencyConflictException
            API-->>SPA: 412 precondition_failed (ไม่ใช่ 409 ตาม § 0.5 ดู Notes)
        else ผ่านทุกเงื่อนไข
            ST->>DB: mutate + bump AuthorizationVersion + OperationRecord Succeeded, commit
            Note over ST,DB: PATCH /accounts bump เฉพาะเมื่อ Status เปลี่ยนจริง (Rename เองไม่ bump) อีก 4 endpoint bump ไม่มีเงื่อนไข
            ST-->>API: response ใหม่
            Note over API,DB: Phase C - audit แยกทรานแซกชัน (ไม่ atomic กับ Phase B ดู Notes)
            API->>DB: audit.Append + SaveChanges
            API-->>SPA: 200/202/204 + ETag ใหม่
        end
    end
```

---

## 2.4 สร้าง/แก้ไข Platform role (ไม่มี idempotent replay จริง)

`Idempotency-Key` validate รูปแบบแล้วทิ้ง เพราะ POST/PUT ไปเรียก `IMediator` (ใช้ร่วมกับฝั่ง merchant-role) แทน `ExecuteIdempotentAsync` (source: `CanonicalAccessEndpoints.cs:187-226`, `CreateRole.cs`, `UpdateRole.cs`, `RoleAuthorizationInvalidator.cs`)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin Console
    participant API as POST /roles หรือ PUT /roles/{id}
    participant MED as CreateRoleHandler / UpdateRoleHandler
    participant INV as RoleAuthorizationInvalidator
    participant DB as DB (iam.Roles, access.AccessRoles, access.PlatformAccessRoles, acct.Accounts)

    Note over A,API: Phase A - gate + format-only idempotency (ไม่มี replay จริง ดู Notes)
    A->>SPA: สร้างหรือแก้ไข Platform role
    SPA->>API: POST/PUT + Idempotency-Key (validate รูปแบบเท่านั้น) + X-CSRF-Token
    alt POST
        alt Idempotency-Key ผิดรูป
            API-->>SPA: 400 invalid_idempotency_key
        else ผ่าน
            API->>MED: mediator.Send(CreateRoleCommand)
        end
    else PUT: snapshot pre-check ก่อน (ResolveRoleAsync มาก่อน idempotency-key format check ดู Notes)
        API->>API: ResolveRoleAsync (list ทั้งหมด filter ใน memory)
        alt ไม่พบ
            API-->>SPA: 404
        else พบ
            alt Idempotency-Key ผิดรูปหรือ If-Match ผิดรูปหรือไม่ตรง snapshot.Version
                API-->>SPA: 400 invalid_idempotency_key / invalid_etag / 412 precondition_failed
            else ผ่าน
                API->>MED: mediator.Send(UpdateRoleCommand, ExpectedVersion)
            end
        end
    end
    Note over MED,DB: Phase B - transaction สด (ตรวจซ้ำเพื่อกัน race กับ Phase A)
    alt CreateRoleCommand
        MED->>DB: CodeExistsAsync ใน context เดียวกัน
        alt code ซ้ำ
            MED-->>API: ConflictException
            API-->>SPA: 409 already exists
        else ไม่ซ้ำ
            MED->>DB: Role.Create (permission key ต้องอยู่ catalog + side ตรง Scope) + RoleCreated audit + SaveChanges
            MED-->>API: RoleListItem
            API-->>SPA: 201 Location /roles/{id} + ETag v1
        end
    else UpdateRoleCommand
        MED->>DB: GetByCodeAsync(code) แบบสด
        alt ไม่พบ, Merchant ไม่เป็นเจ้าของ, version ไม่ตรงสด, หรือ seed anchor ปิดไม่ได้
            MED-->>API: NotFoundException / ConflictException
            API-->>SPA: 404 / 409 state_conflict / 409 seed anchor
        else ผ่านทุกเงื่อนไข ownership/version/seed anchor
            MED->>INV: InvalidateAssignedAccountsAsync(roleId)
            INV->>DB: ล็อก Account ทุกตัวที่ผูก role (UPDLOCK/HOLDLOCK เรียงตาม AccountId) + bump AuthorizationVersion
            INV-->>MED: เสร็จ
            MED->>DB: Role.Rename/SetDescription/SetColor
            alt permission key ไม่อยู่ catalog หรือ side ไม่ตรง Scope (SetPermissions)
                MED-->>API: ArgumentException
                API-->>SPA: 400
            else permission ผ่าน
                MED->>DB: Activate หรือ Deactivate, BumpVersion + RoleUpdated audit + SaveChanges
                MED-->>API: RoleListItem ใหม่
                API-->>SPA: 200 + ETag version ใหม่
            end
        end
    end
```

---

## 2.5 สร้าง SYSTEM client + ลงทะเบียน scope

`ExecuteIdempotentAsync` จริง (ต่างจาก § 2.4) แล้ว sync OpenIddict เฉพาะ scope เพราะยังไม่มี key (source: `IdentityAccessEndpoints.cs:137-141,339-352`, `IdentityAccessStore.cs:476-501,610-664`, `SystemClientScopeRegistry.cs`)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin Console
    participant API as POST /system-clients
    participant ST as IdentityAccessStore
    participant OID as OpenIddict scope/application manager
    participant DB as DB (acct.Accounts, iam.SystemClients, iam.SystemClientScopes, admin.OperationRecords)

    Note over A,API: Phase A - gate + scope check (นอกทรานแซกชัน)
    A->>SPA: สร้าง SYSTEM client
    SPA->>API: POST + Idempotency-Key + body merchantId/clientId/scopes + X-CSRF-Token (ดู § 0.1, § 0.3)
    alt scope.Accessible.Allows(body.MerchantId) เป็น false
        API-->>SPA: 404 (ซ่อนการมีอยู่ของ merchant)
    else Idempotency-Key ผิดรูป
        API-->>SPA: 400 invalid_idempotency_key
    else ผ่าน
        API->>ST: ExecuteIdempotentAsync(actor, system-client.create, key, intent)
        Note over ST,DB: Phase B - idempotent create ในทรานแซกชันเดียว
        ST->>DB: app lock admin-operation + lookup OperationRecord
        alt record เดิม hash ต่างกัน
            ST-->>API: ConflictException idempotency_key_reused
            API-->>SPA: 409
        else record เดิมยังไม่ Succeeded
            ST-->>API: ConflictException operation_in_progress
            API-->>SPA: 409
        else record เดิม Succeeded
            ST-->>API: body เดิม (Replayed)
            API-->>SPA: 200 body เดิม
        else ClientId ซ้ำใน DB
            ST-->>API: ConflictException client_id_exists
            API-->>SPA: 409
        else ClientId ซ้ำใน OpenIddict application
            ST-->>API: ConflictException application_id_exists
            API-->>SPA: 409
        else scope ไม่อยู่ใน SystemClientScopeRegistry
            ST-->>API: InvalidRequestException invalid_scope
            API-->>SPA: 400
        else ผ่านทุกเงื่อนไข
            ST->>DB: Account(System) + SystemClient + SystemClientScope ต่อ scope
            ST->>OID: SyncOpenIddictApplicationAsync(addedJwkJson=null)
            OID-->>ST: ลงทะเบียนเฉพาะ scope (ยังไม่สร้าง application ดู Notes)
            ST->>DB: OperationRecord Succeeded, commit
            ST-->>API: SystemClientAdminView (ไม่มี secret)
            API-->>SPA: 201 Location /system-clients/{id} + ETag = AccountAuthorizationVersion
        end
    end
```

---

## 2.6 แก้ไข/แทนที่สิทธิ์ SYSTEM client และจัดการ public key

scope 404 เกิดนอกทรานแซกชัน (ต่างจาก § 2.3), If-Match ใช้จริงเฉพาะ PATCH update (source: `IdentityAccessEndpoints.cs:146-168,363-447`, `IdentityAccessStore.cs:503-609`)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin Console
    participant API as PATCH/PUT/POST/DELETE (4 endpoint)
    participant ST as IdentityAccessStore
    participant OID as OpenIddict application manager
    participant DB as DB (iam.SystemClients, SystemClientScopes, ClientKeyPolicies, OperationRecords)

    Note over A,API: Phase A - private-key check (เฉพาะ CreateClientKey) + scope 404 (นอกทรานแซกชัน)
    A->>SPA: แก้ไข/แทนที่สิทธิ์ client หรือจัดการ public key
    SPA->>API: request + Idempotency-Key (+ If-Match เฉพาะ PATCH update) + X-CSRF-Token
    alt CreateClientKey และ Jwk มี private material
        API-->>SPA: 400 private_key_not_allowed
    else client ไม่พบหรือ scope.Accessible.Allows(MerchantId) เป็น false
        API-->>SPA: 404 (ซ่อนการมีอยู่)
    else Idempotency-Key ผิดรูป
        API-->>SPA: 400 invalid_idempotency_key
    else PATCH update และ If-Match ผิดรูป
        API-->>SPA: 400 invalid_etag
    else ผ่าน
        API->>ST: ExecuteIdempotentAsync(actor, operation, key, intent)
        Note over ST,DB: Phase B - idempotent mutate ในทรานแซกชันเดียว
        ST->>DB: app lock admin-operation + lookup OperationRecord
        alt record เดิม hash ต่างกัน
            ST-->>API: ConflictException idempotency_key_reused
            API-->>SPA: 409
        else record เดิมยังไม่ Succeeded
            ST-->>API: ConflictException operation_in_progress
            API-->>SPA: 409
        else record เดิม Succeeded
            ST-->>API: response เดิม (Replayed)
            API-->>SPA: 200/201/204 + ETag เดิม (ถ้ามี)
        else DELETE key: key ไม่พบ
            ST-->>API: NotFoundException (ภายในทรานแซกชัน หลัง PRIOR=ไม่มี ไม่ใช่ที่ LOOK)
            API-->>SPA: 404
        else domain validation ไม่ผ่าน (key application mismatch / public JWK ไม่ถูกต้อง / key id ซ้ำ)
            ST-->>API: ConflictException / InvalidRequestException
            API-->>SPA: 409 application_mismatch / key_id_exists หรือ 400 invalid_public_jwk
        else PATCH update และ AuthorizationVersion ไม่ตรง If-Match
            ST-->>API: ConcurrencyConflictException
            API-->>SPA: 412 precondition_failed
        else ผ่านทุกเงื่อนไข (PUT access ไม่มี version ให้เทียบเลย ดู Notes)
            ST->>DB: mutate (rename / replace scopes / add key / revoke key) + bump AuthorizationVersion (POST .../keys ไม่ bump)
            ST->>OID: SyncOpenIddictApplicationAsync (rename / replace scope / add key / remove key)
            OID-->>ST: sync เสร็จ (สร้าง application ครั้งแรกถ้าเพิ่ง add key แรก, ลบถ้าลบ key active ตัวสุดท้าย ดู Notes)
            ST->>DB: OperationRecord Succeeded, commit (ไม่มี audit เลยทั้ง 4 endpoint ดู Notes)
            ST-->>API: response ใหม่
            API-->>SPA: 200 (update/access) / 201 Location (key create) / 204 (key revoke) + ETag ใหม่ (update/access)
        end
    end
```

---

## Notes

- Deviations ของ theme นี้อยู่ที่ `02-canonical-access-system-clients.activities.md` ไฟล์นี้ไม่ทำซ้ำ
- theme นี้ไม่มี endpoint ไหนใช้ maker-checker หรือ background outbox (§ 0.7 / § 0.8) เลย ทุกการเขียนเป็น synchronous ภายในทรานแซกชันเดียวของ request นั้น (`session-revocations` ลงท้ายคล้าย maker-checker แต่เป็น direct write ตรวจแล้วใน `IdentityAccessEndpoints.cs`/`IdentityAccessStore.cs`)
- § 2.3: ความเสี่ยง `.ContinueWith(x => x.Result.Value)` ที่อาจทำให้ 404/412 ที่ตั้งใจไว้ของ DELETE merchant-access และ POST session-revocations กลายเป็น 500 ยังไม่ยืนยันด้วย test จริง (ดูรายละเอียดเต็มใน Notes ของ activities.md)
- § 2.5/§ 2.6: OpenIddict application ผูกกับ public key ไม่ใช่กับการสร้าง client — ลำดับ POST create -> POST keys คือเส้นทางเดียวที่ทำให้ client ใหม่ issue token ได้จริง
- `<br/>` ไม่ได้ใช้ในไฟล์นี้เพราะชื่อ participant และข้อความบนลูกศรสั้นพอในบรรทัดเดียว

**Render**: GitHub / Obsidian / VS Code Mermaid
