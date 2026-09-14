# pol-core API — Admin merchant console, merchant roles และ originators (Sequence Diagrams)

> Source: เดียวกับ `10-admin-merchant-console-originators.activities.md` (Deviations และ path:line เต็มอยู่ที่ไฟล์นั้น)
> Scope: 10 § เดียวกับไฟล์ activities (หมายเลข § ตรงกัน) แสดงลำดับข้าม actor / API / handler / DB / worker
> Generated: 2026-09-14

| § | Diagram | Endpoints |
| --- | --- | --- |
| 10.1 | Merchant / Originator read model (list ×2 + originator detail) | `GET /api/v1/merchants`, `GET /api/v1/originators`, `GET /api/v1/originators/{originatorId:guid}` |
| 10.2 | แก้ไข / ระงับ / เปิดใช้งานร้านค้าอีกครั้ง (idempotent executor + SQL lock) | `PUT /api/v1/merchants/{merchantId:guid}`, `POST /api/v1/merchants/{merchantId:guid}/suspend`, `POST /api/v1/merchants/{merchantId:guid}/reactivate` |
| 10.3 | Merchant role และ permission read model | `GET /api/v1/merchants/{merchantId:guid}/permissions`, `GET /api/v1/merchants/{merchantId:guid}/roles`, `GET /api/v1/merchants/{merchantId:guid}/roles/{code}` |
| 10.4 | Merchant role CRUD | `POST /api/v1/merchants/{merchantId:guid}/roles`, `PUT /api/v1/merchants/{merchantId:guid}/roles/{code}`, `DELETE /api/v1/merchants/{merchantId:guid}/roles/{code}` |
| 10.5 | ผู้ใช้ร้านค้า: edit view (reveal audit) + update โดย Admin | `GET /api/v1/merchants/{merchantId:guid}/users/{merchantUserId:guid}/edit`, `PUT /api/v1/merchants/{merchantId:guid}/users/{merchantUserId:guid}` |
| 10.6 | เชิญผู้ใช้เข้าร้านค้าโดย Admin (idempotent, async email outbox) | `POST /api/v1/merchants/{merchantId:guid}/user-invitations` |
| 10.7 | กำหนดบทบาทให้ผู้ใช้ร้านค้าโดย Admin | `PUT /api/v1/merchants/{merchantId:guid}/users/{merchantUserId:guid}/roles` |
| 10.8 | สร้าง Originator | `POST /api/v1/originators` |
| 10.9 | แก้ไข / เปิดใช้งาน / ปิดใช้งาน Originator (If-Match เท่านั้น) | `PUT /api/v1/originators/{originatorId:guid}`, `POST /api/v1/originators/{originatorId:guid}/enable`, `POST /api/v1/originators/{originatorId:guid}/disable` |
| 10.10 | ลบ Originator (soft-disable เมื่อยังถูก routing rule อ้างอิง) | `DELETE /api/v1/originators/{originatorId:guid}` |

---

## 10.1 Merchant / Originator read model (list ×2 + originator detail)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin Console
    participant API as API endpoint
    participant STORE as AdminMerchantControlStore
    participant DB as DB (Merchants / Originators)

    Note over SPA,API: Phase A — gate (ดู § 0.1, safe method ข้าม § 0.3)
    SPA->>API: GET /merchants หรือ /originators[?merchantId] หรือ /originators/{id}
    API->>API: RequirePermission(merchant.view)

    alt GET /merchants
        Note over API,DB: Phase B1 — list ร้านค้า
        API->>API: ValidatePage(page, limit)
        alt page/limit ผิด
            API-->>SPA: 400 invalid_filter
        else ถูกต้อง
            API->>STORE: ListMerchantsAsync(query, access)
            STORE->>DB: WHERE access filter + search/status, ORDER BY Code, Id
            DB-->>STORE: rows + total
            STORE-->>API: PagedResult
            API-->>SPA: 200 PagedResult
        end
    else GET /originators
        Note over API,DB: Phase B2 — list Originator
        API->>API: ValidatePage(page, limit)
        alt page/limit ผิด
            API-->>SPA: 400 invalid_filter
        else ถูกต้อง
            API->>STORE: ListOriginatorsAsync(query, access)
            opt query merchantId ให้มา
                STORE->>STORE: EnsureAccess(merchantId)
                alt นอก Admin scope
                    STORE-->>API: AdminMerchantAccessDeniedException
                    API-->>SPA: 403 merchant_scope_forbidden
                end
            end
            STORE->>STORE: ParseType(type) / ParseOriginatorStatus(status)
            alt type หรือ status ผิด
                STORE-->>API: InvalidRequestException
                API-->>SPA: 400 invalid_type / invalid_filter
            else ถูกต้อง
                STORE->>DB: WHERE access filter + merchantId/type/status/search
                DB-->>STORE: rows + total
                STORE-->>API: PagedResult
                API-->>SPA: 200 PagedResult
            end
        end
    else GET /originators/{id}
        Note over API,DB: Phase B3 — อ่าน originator เดี่ยว
        API->>STORE: GetOriginatorAsync(id, merchantId?, access)
        STORE->>DB: SingleOrDefault(Id = id [+ MerchantId])
        DB-->>STORE: row หรือ null
        alt ไม่พบ หรือ access.Allows ไม่ผ่าน
            STORE-->>API: null
            API-->>SPA: 404
        else พบ
            STORE-->>API: OriginatorView
            API-->>SPA: 200 + ETag vN
        end
    end
```

---

## 10.2 แก้ไข / ระงับ / เปิดใช้งานร้านค้าอีกครั้ง (idempotent executor + SQL lock)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin Console
    participant API as API endpoint
    participant STORE as AdminMerchantControlStore
    participant LOCK as SQL applock
    participant DB as DB (Merchants / OperationRecords)

    Note over SPA,API: Phase A — header + body guard
    SPA->>API: PUT /merchants/{id} หรือ POST .../suspend หรือ reactivate<br/>+ If-Match vN + Idempotency-Key k1 (+ X-CSRF-Token)
    API->>API: EnsureMerchant(body.MerchantId == route)
    alt ไม่ตรง
        API-->>SPA: 400 validation_failed
    end
    API->>API: VersionEtags.Require + IdempotencyKeys.Require ดู § 0.5
    alt header ผิดรูป
        API-->>SPA: 400 invalid_etag / invalid_idempotency_key
    end
    Note over API,DB: Phase B — keyed admin transaction
    API->>STORE: UpdateMerchantAsync / ChangeMerchantStatusAsync
    STORE->>STORE: EnsureAccess(merchantId)
    alt นอก Admin scope
        STORE-->>API: AdminMerchantAccessDeniedException
        API-->>SPA: 403 merchant_scope_forbidden
    else อยู่ใน scope
        STORE->>LOCK: sp_getapplock global (Shared) แล้ว per-merchant (Exclusive), timeout 15s
        alt lock timeout
            LOCK-->>STORE: negative result
            STORE-->>API: PaymentAuthorizationBusyException
            API-->>SPA: 409 payment_authorization_busy
        else ได้ lock
            STORE->>DB: FindOperationAsync(merchantId, adminId, operation, k1)
            alt hash ต่างกับ record เดิม
                DB-->>STORE: ConflictException idempotency_key_reused
                API-->>SPA: 409 code idempotency_key_reused
            else record เดิมยังไม่ Succeeded
                DB-->>STORE: ConflictException operation_in_progress
                API-->>SPA: 409 code operation_in_progress
            else record เดิม Succeeded
                DB-->>STORE: stored response
                STORE-->>API: Replayed = true
                API-->>SPA: 200 response เดิม + ETag เดิม
            else ไม่มี record
                STORE->>DB: LoadMerchantAsync(merchantId)
                alt ไม่พบ
                    DB-->>STORE: null
                    API-->>SPA: 404
                else พบ
                    DB-->>STORE: merchant vN
                    STORE->>STORE: EnsureVersion(vN, If-Match)
                    alt version ไม่ตรง
                        STORE-->>API: ConcurrencyConflictException
                        API-->>SPA: 409 code state_conflict
                    else ตรง
                        opt PUT (update)
                            STORE->>STORE: SyncMerchantPoliciesAsync(enabledChannels)
                            alt channel ไม่มี qualifying provider account
                                STORE-->>API: PaymentCapabilityUnavailableException
                                API-->>SPA: 409 payment_capability_unavailable
                            end
                        end
                        STORE->>STORE: merchant.Update(name, note, channels, metadata) / Suspend / Reactivate
                        alt PUT และ name ว่าง หรือ metadata ผิด allowlist schema (JsonUnmappedMemberHandling.Disallow)
                            STORE-->>API: ArgumentException / JsonException
                            API-->>SPA: 400
                        else สำเร็จ
                            STORE->>DB: OperationRecord.Complete(200) + SaveChanges
                            DB-->>STORE: committed
                            STORE-->>API: AdminMerchantListItem vN+1
                            API-->>SPA: 200 + ETag vN+1
                        end
                    end
                end
            end
        end
    end
```

---

## 10.3 Merchant role และ permission read model

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin Console
    participant API as API endpoint
    participant DIR as IAdminMerchantDirectory
    participant MED as Mediator (Iam.Application.Roles)
    participant DB as DB (Roles / RoleAssignments)

    Note over SPA,DIR: Phase A — gate ท้องถิ่นของ theme (ดู § 0.1)
    SPA->>API: GET .../permissions, .../roles หรือ .../roles/{code}
    API->>API: RequireReadAccess(merchantId)
    alt นอก Admin scope
        API-->>SPA: 404
    else อยู่ใน scope
        API->>DIR: IsActiveMerchantAsync(merchantId)
        alt merchant ไม่ Active
            DIR-->>API: false
            API-->>SPA: 404
        else Active
            Note over API,DB: Phase B — ตาม endpoint
            alt GET .../permissions
                API->>MED: GetPermissionCatalogQuery(Scope.Merchant)
                MED-->>API: groups + permission keys (รวม Shared)
                API-->>SPA: 200 catalog
            else GET .../roles
                API->>API: SfsQueryParser.Parse(maxLimit 100) ดู § 0.6
                alt parse ไม่ผ่าน
                    API-->>SPA: 400 ดู § 0.6
                else ผ่าน
                    API->>MED: ListRolesQuery(Merchant context)
                    MED->>DB: query + count assignment ต่อ role
                    DB-->>MED: rows + UserCount
                    MED-->>API: PagedResult
                    API-->>SPA: 200 PagedResult
                end
            else GET .../roles/{code}
                API->>MED: GetRoleQuery(Merchant context, code)
                MED->>DB: GetListItemByCodeAsync
                DB-->>MED: role หรือ null
                alt ไม่พบหรือมองไม่เห็น
                    MED-->>API: null
                    API-->>SPA: 404
                else พบ
                    MED-->>API: RoleListItem + UserCount
                    API-->>SPA: 200 + ETag vN
                end
            end
        end
    end
```

---

## 10.4 Merchant role CRUD

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin Console
    participant API as API endpoint
    participant MED as Mediator (CreateRole/UpdateRole/DeleteRole)
    participant DB as DB (Roles / RolePermissions / RoleAssignments)

    Note over SPA,API: Phase A — gate ท้องถิ่น (RequireMutationAccess 403, RequireActiveMerchantAsync 404, ดู § 0.1/0.3)
    SPA->>API: POST /roles, PUT /roles/{code} หรือ DELETE /roles/{code}<br/>(PUT/DELETE + If-Match)

    alt POST (create)
        Note over API,DB: Phase B1 — สร้าง role
        API->>MED: CreateRoleCommand(Merchant context, code, ...)
        MED->>DB: CodeExistsAsync(context, code)
        alt code ซ้ำในบริบทนี้
            DB-->>MED: true
            MED-->>API: ConflictException
            API-->>SPA: 409
        else ไม่ซ้ำ
            MED->>MED: Role.Create: ตรวจ permission key ทุกตัวอยู่ใน catalog<br/>และ side = Merchant/Shared
            alt key ผิด
                MED-->>API: ArgumentException
                API-->>SPA: 400
            else ผ่าน
                MED->>DB: Add + audit RoleCreated + SaveChanges
                DB-->>MED: role v1
                MED-->>API: RoleListItem
                API-->>SPA: 201 + ETag v1
            end
        end
    else PUT / DELETE
        Note over API,DB: Phase B2 — โหลด + ownership + version
        API->>MED: UpdateRoleCommand / DeleteRoleCommand(context, code, If-Match)
        MED->>DB: GetByCodeAsync(context, code)
        alt ไม่พบ/มองไม่เห็น
            DB-->>MED: null
            MED-->>API: NotFoundException
            API-->>SPA: 404
        else พบ
            DB-->>MED: role
            MED->>MED: role.MerchantId = merchantId นี้?
            alt ไม่ own (รวม shared/seed role)
                MED-->>API: ConflictException
                API-->>SPA: 409
            else own
                MED->>MED: role.Version = If-Match?
                alt ไม่ตรง
                    MED-->>API: ConflictException state_conflict
                    API-->>SPA: 409 code state_conflict
                else ตรง
                    alt PUT
                        MED->>DB: InvalidateAssignedAccountsAsync (bump AuthorizationVersion ดู § 0.2)
                        MED->>MED: role.Rename(name)
                        alt name ว่าง/whitespace
                            MED-->>API: ArgumentException
                            API-->>SPA: 400
                        else ไม่ว่าง
                            MED->>MED: SetPermissions ตรวจ catalog ซ้ำ
                            alt key ผิด
                                MED-->>API: ArgumentException
                                API-->>SPA: 400
                            else ผ่าน
                                MED->>DB: description/color/status + audit RoleUpdated + SaveChanges
                                DB-->>MED: role vN+1
                                MED-->>API: RoleListItem
                                API-->>SPA: 200 + ETag vN+1
                            end
                        end
                    else DELETE
                        MED->>DB: CountAsync(context, role.Id)
                        alt มี assignment ผูกอยู่
                            DB-->>MED: > 0
                            MED-->>API: ConflictException
                            API-->>SPA: 409
                        else ไม่มี
                            MED->>DB: Remove + audit RoleDeleted + SaveChanges
                            DB-->>MED: committed
                            MED-->>API: DeleteRoleResult
                            API-->>SPA: 204 (ไม่มี ETag)
                        end
                    end
                end
            end
        end
    end
```

---

## 10.5 ผู้ใช้ร้านค้า: edit view (reveal audit) + update โดย Admin

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin Console
    participant API as API endpoint
    participant SCOPE as IActorScope
    participant MED as Mediator (Merchants.Application.Users)
    participant DB as DB (Users / ManagementAudit)

    Note over SPA,API: Phase A — GET edit view
    SPA->>API: GET .../users/{userId}/edit
    API->>API: RequireReadAccess(merchantId) + RequireActiveMerchantAsync
    alt นอก scope หรือไม่ Active
        API-->>SPA: 404
    else ผ่าน
        API->>SCOPE: Begin(merchantId, adminId)
        API->>MED: GetMerchantUserEditQuery(userId)
        MED->>DB: FindByIdAsync (กรองด้วย query filter ของ actor scope)
        alt ไม่พบ user ใน merchant นี้
            DB-->>MED: null
            API-->>SPA: 404
        else พบ
            DB-->>MED: user
            MED->>DB: audit Reveal + SaveChanges (นอก transaction)
            MED-->>API: MerchantUserEditView (ฟิลด์ไม่ mask)
            API-->>SPA: 200 + ETag vN
        end
    end

    Note over SPA,API: Phase B — PUT update
    SPA->>API: PUT .../users/{userId} + If-Match vN (+ X-CSRF-Token)
    API->>API: RequireMutationAccess(merchantId) + RequireActiveMerchantAsync
    alt นอก scope
        API-->>SPA: 403 merchant_scope_forbidden
    else ไม่ Active
        API-->>SPA: 404
    else ผ่าน
        API->>SCOPE: Begin(merchantId, adminId)
        API->>MED: UpdateMerchantUserCommand(userId, ..., ExpectedVersion=vN)
        MED->>DB: FindByIdAsync (กรองด้วย query filter ของ actor scope)
        alt ไม่พบ
            DB-->>MED: null (NotFoundException)
            API-->>SPA: 404
        else พบ
            DB-->>MED: user
            MED->>MED: EnsureVersion(vN)
            alt ไม่ตรง
                MED-->>API: InvalidOperationException (ไม่มี code)
                API-->>SPA: 409
            else ตรง
                alt firstName/lastName ว่างหรือ whitespace
                    MED-->>API: ArgumentException
                    API-->>SPA: 400
                else ไม่ว่าง
                    MED->>DB: UpdateProfile(...) + audit Update + SaveChanges
                    DB-->>MED: user vN+1
                    MED-->>API: UpdateMerchantUserResult
                    API-->>SPA: 204 + ETag vN+1
                end
            end
        end
    end
```

---

## 10.6 เชิญผู้ใช้เข้าร้านค้าโดย Admin (idempotent, async email outbox)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin Console
    participant API as API endpoint
    participant MED as CreateInvitationHandler
    participant DB as DB (Invitations / OperationRecord / UserOutbox)
    participant WORK as MerchantUserOutboxDispatcher
    participant MAIL as InvitationEmailSender<br/>(external)

    Note over SPA,API: Phase A — gate + header validate
    SPA->>API: POST .../user-invitations + Idempotency-Key k1 (+ X-CSRF-Token)
    API->>API: RequireMutationAccess + RequireActiveMerchantAsync + IdempotencyKeys.Require ดู § 0.5
    alt นอก scope / ไม่ Active / key ผิดรูป
        API-->>SPA: 403 / 404 / 400 invalid_idempotency_key
    else ผ่าน
        Note over API,DB: Phase B — validate + replay lookup
        API->>MED: CreateInvitationCommand(email, roleCodes, k1)
        MED->>MED: TtlHours ∈ 1..168?
        alt ผิด
            MED-->>API: ArgumentException
            API-->>SPA: 400
        else ผ่าน
            MED->>DB: resolve roleCodes เป็น active role (Admin audience)
            alt code unknown/inactive
                DB-->>MED: unmatched codes
                MED-->>API: ConflictException
                API-->>SPA: 409
            else resolve ผ่าน
                MED->>DB: FindAsync(merchantId, adminId, "merchant-user.invite", k1)
                alt hash ต่าง
                    DB-->>MED: mismatch
                    MED-->>API: ConflictException idempotency_key_reused
                    API-->>SPA: 409 code idempotency_key_reused
                else hash ตรง (record เดิม)
                    DB-->>MED: stored result
                    MED-->>API: CreateInvitationResult (replay)
                    API-->>SPA: 201 (response เดิม)
                else ไม่มี record
                    Note over MED,DB: Phase C — สร้าง invitation ใหม่
                    opt มี pending invitation อีเมลเดียวกัน
                        MED->>DB: revoke ของเดิม + audit InviteRevoke
                    end
                    MED->>DB: สร้าง invitation (token hash, expiresAt) + audit InviteCreate
                    MED->>DB: Enqueue MerchantUserInvitationDeliveryRequested ลง merch.UserOutbox
                    MED->>DB: AdminUserOperationRecord.Succeeded + SaveChanges
                    DB-->>MED: committed
                    MED-->>API: CreateInvitationResult
                    API-->>SPA: 201 Created + maskedEmail, expiresAt, status pending
                end
            end
        end
    end

    Note over DB,MAIL: Phase D — ส่งอีเมลแบบ async ดู § 0.8
    DB-->>WORK: MerchantUserOutboxDispatcher poll เจอแถวใหม่
    WORK->>WORK: unprotect token
    WORK->>MAIL: SendAsync(email, rawToken)
    MAIL-->>WORK: ส่งสำเร็จ
```

---

## 10.7 กำหนดบทบาทให้ผู้ใช้ร้านค้าโดย Admin

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin Console
    participant API as API endpoint
    participant MED as SetRolesHandler
    participant DB as DB (Users / RoleAssignments)

    Note over SPA,API: Phase A — gate + target lookup
    SPA->>API: PUT .../users/{userId}/roles + If-Match vN (+ X-CSRF-Token)
    API->>API: RequireMutationAccess + RequireActiveMerchantAsync
    alt นอก scope / ไม่ Active
        API-->>SPA: 403 / 404
    else ผ่าน
        API->>MED: SetRolesCommand(targetUserId, roleCodes,<br/>ActingMerchantId=merchantId, ActingMerchantUserId=adminId, vN)
        MED->>DB: FindByIdAsync(targetUserId)
        alt target ไม่ Active หรือ target.MerchantId != merchantId
            DB-->>MED: ไม่ผ่านเงื่อนไข
            MED-->>API: NotFoundException
            API-->>SPA: 404
        else ผ่าน
            DB-->>MED: target user
            MED->>MED: EnsureVersion(vN)
            alt ไม่ตรง
                MED-->>API: InvalidOperationException (ไม่มี code)
                API-->>SPA: 409
            else ตรง
                MED->>DB: resolve roleCodes เป็น role id ใน merchant นี้
                alt code unknown
                    DB-->>MED: unmatched
                    MED-->>API: ArgumentException
                    API-->>SPA: 400
                else resolve ผ่าน
                    MED->>DB: นับ active user ที่ถือ merchant_manager
                    alt ถอด manager ออกจนเหลือ <= 1 คน
                        DB-->>MED: count <= 1
                        MED-->>API: ConflictException
                        API-->>SPA: 409
                    else ปลอดภัย
                        MED->>DB: add/remove assignment + BumpVersion + audit SetRoles
                        DB-->>MED: target vN+1
                        MED-->>API: SetRolesResult
                        API-->>SPA: 204 + ETag vN+1
                    end
                end
            end
        end
    end
```

---

## 10.8 สร้าง Originator

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin Console
    participant API as API endpoint
    participant STORE as AdminMerchantControlStore
    participant DB as DB (Merchants / Originators)

    Note over SPA,API: Phase A — access + existence
    SPA->>API: POST /originators + body MerchantId/Code/Name/Type/...
    API->>STORE: CreateOriginatorAsync(intent)
    STORE->>STORE: EnsureAccess(body.MerchantId)
    alt นอก Admin scope
        STORE-->>API: AdminMerchantAccessDeniedException
        API-->>SPA: 403 merchant_scope_forbidden
    else อยู่ใน scope
        STORE->>DB: EnsureMerchantExistsAsync(body.MerchantId) (ไม่เช็ค Active)
        alt merchant ไม่มีอยู่จริง
            DB-->>STORE: false
            STORE-->>API: NotFoundException
            API-->>SPA: 404
        else มีอยู่จริง
            Note over STORE,DB: Phase B — validate + สร้าง
            STORE->>STORE: ParseType(type)
            alt type ไม่ใช่ branch/agent/broker/staff/app
                STORE-->>API: InvalidRequestException invalid_type
                API-->>SPA: 400 code invalid_type
            else type ถูกต้อง
                STORE->>STORE: Originator.Create: validate code/name/linkedApiClientId
                alt validate ไม่ผ่าน
                    STORE-->>API: ArgumentException
                    API-->>SPA: 400
                else ผ่าน
                    STORE->>DB: Add + SaveChangesAsync
                    alt unique (MerchantId, Code) ชนที่ DB (SQL 2627/2601)
                        DB-->>STORE: DbUpdateException
                        STORE-->>API: ConflictException (ControlPlaneUnitOfWork backstop)
                        API-->>SPA: 409
                    else สำเร็จ
                        DB-->>STORE: originator v1
                        STORE-->>API: OriginatorView
                        API-->>SPA: 201 Created Location /originators/{id} + ETag v1
                    end
                end
            end
        end
    end
```

---

## 10.9 แก้ไข / เปิดใช้งาน / ปิดใช้งาน Originator (If-Match เท่านั้น)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin Console
    participant API as API endpoint
    participant STORE as AdminMerchantControlStore
    participant DB as DB (Originators)

    Note over SPA,API: Phase A — access + load + version
    SPA->>API: PUT /originators/{id} หรือ POST enable/disable + If-Match vN
    API->>STORE: UpdateOriginatorAsync / SetOriginatorStateAsync(body.MerchantId)
    STORE->>STORE: EnsureAccess(body.MerchantId)
    alt นอก Admin scope
        STORE-->>API: AdminMerchantAccessDeniedException
        API-->>SPA: 403 merchant_scope_forbidden
    else อยู่ใน scope
        STORE->>DB: LoadOriginatorAsync(originatorId, body.MerchantId)
        alt Id หรือ MerchantId ไม่ตรงกัน
            DB-->>STORE: null
            STORE-->>API: NotFoundException
            API-->>SPA: 404
        else พบ
            DB-->>STORE: entity vN
            STORE->>STORE: EnsureVersion(vN, If-Match)
            alt ไม่ตรง
                STORE-->>API: ConcurrencyConflictException
                API-->>SPA: 409 code state_conflict
            else ตรง
                Note over STORE,DB: Phase B — mutate (ไม่มี Idempotency-Key, ไม่มี audit)
                alt PUT (update)
                    STORE->>STORE: ParseType(type)
                    alt type ไม่ใช่ branch/agent/broker/staff/app
                        STORE-->>API: InvalidRequestException invalid_type
                        API-->>SPA: 400 code invalid_type
                    else type ถูกต้อง
                        STORE->>STORE: entity.Update: validate name/saleCode/linkedApiClientId
                        alt validate ไม่ผ่าน
                            STORE-->>API: ArgumentException
                            API-->>SPA: 400
                        else ผ่าน
                            STORE->>DB: SaveChangesAsync
                            DB-->>STORE: entity vN+1
                            STORE-->>API: OriginatorView
                            API-->>SPA: 200 + ETag vN+1
                        end
                    end
                else enable/disable
                    STORE->>STORE: entity.Enable() / Disable() (no-op ถ้า status ตรงอยู่แล้ว)
                    STORE->>DB: SaveChangesAsync
                    DB-->>STORE: entity vN(+1)
                    STORE-->>API: OriginatorView
                    API-->>SPA: 200 + ETag ล่าสุด
                end
            end
        end
    end
```

---

## 10.10 ลบ Originator (soft-disable เมื่อยังถูก routing rule อ้างอิง)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin Console
    participant API as API endpoint
    participant STORE as AdminMerchantControlStore
    participant DB as DB (Originators / RoutingRules)

    Note over SPA,API: Phase A — model binding + access + load
    SPA->>API: DELETE /originators/{id}?merchantId=... + If-Match vN
    alt query merchantId ขาด
        API-->>SPA: 400 (model binding ปฏิเสธก่อนถึง handler)
    else มี merchantId
        API->>STORE: DeleteOriginatorAsync(id, merchantId, vN)
        STORE->>STORE: EnsureAccess(merchantId)
        alt นอก Admin scope
            STORE-->>API: AdminMerchantAccessDeniedException
            API-->>SPA: 403 merchant_scope_forbidden
        else อยู่ใน scope
            STORE->>DB: LoadOriginatorAsync(id, merchantId)
            alt ไม่พบ
                DB-->>STORE: null (NotFoundException)
                API-->>SPA: 404
            else พบ
                DB-->>STORE: entity vN
                STORE->>STORE: EnsureVersion(vN, If-Match)
                alt ไม่ตรง
                    STORE-->>API: ConcurrencyConflictException
                    API-->>SPA: 409 code state_conflict
                else ตรง
                    STORE->>DB: มี RoutingRule อ้างอิง (merchantId, originatorId)?
                    alt มีอ้างอิงอยู่
                        DB-->>STORE: true
                        STORE->>STORE: entity.Disable(now) (soft, ไม่ลบแถว)
                    else ไม่มี
                        DB-->>STORE: false
                        STORE->>DB: Remove(entity) (hard delete)
                    end
                    STORE->>DB: SaveChangesAsync
                    DB-->>STORE: committed
                    STORE-->>API: ok
                    API-->>SPA: 204 No Content (ไม่มี ETag)
                end
            end
        end
    end
```

---

## Notes

- Deviations, สถิติ line-offset ของ inventory doc และรายละเอียดต่อ endpoint ทั้งหมดอยู่ที่ `10-admin-merchant-console-originators.activities.md` — ไฟล์นี้แสดงเฉพาะลำดับข้าม actor/system ของ flow เดียวกัน
- § 10.1/10.3/10.4/10.5/10.7 ใช้ participant `MED`/`STORE` แทน mediator handler หรือ store ตามที่ endpoint นั้นเรียกจริง (`IMediator.Send` ของ `Iam.Application.Roles`/`Merchants.Application.Users` หรือเรียก `AdminMerchantControlStore` ตรง) — ดู source citation ที่ไฟล์ activities ต่อ §

**Render**: GitHub / Obsidian / VS Code Mermaid
