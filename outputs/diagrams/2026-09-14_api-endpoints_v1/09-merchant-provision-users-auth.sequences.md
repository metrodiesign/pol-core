# pol-core API — Merchant provisioning, merchant users และ merchant OIDC (Sequence Diagrams)

> Source: `docs/reference/api-endpoints.md` section "Admin และ merchant identity" บรรทัด L204-L227 และ section "OIDC callback ที่ middleware จัดการ" บรรทัด L393, source ที่อ้างต่อ § เหมือน `09-merchant-provision-users-auth.activities.md`
> Scope: 8 § เดียวกับ `09-merchant-provision-users-auth.activities.md` (หมายเลข § ตรงกัน) แสดงลำดับข้าม actor / middleware / handler / DB / worker
> Generated: 2026-09-14

| § | Diagram | Endpoints |
| --- | --- | --- |
| 9.1 | Provision ร้านค้าใหม่ (Super-only) | `POST /api/v1/merchants` |
| 9.2 | Merchant-user OIDC login + callback | `GET /api/v1/merchants/auth/{provider}/login`, `GET /api/v1/merchants/auth/microsoft/callback` |
| 9.3 | Logout เครื่องนี้ / ทุกเครื่อง | `POST /api/v1/merchants/auth/logout`, `POST /api/v1/merchants/auth/logout-all` |
| 9.4 | ส่งคำขอลงทะเบียนผู้ใช้ร้านค้า (self-service) | `POST /api/v1/merchants/users/register` |
| 9.5 | Read model ผู้ใช้ร้านค้า + ร้านค้า (me / list / detail / catalog) | `GET .../users/me`, `GET .../users`, `GET .../users/{id}` + 5 composed |
| 9.6 | เชิญ / เพิกถอนคำเชิญผู้ใช้ร้านค้า | `POST .../users/invitations`, `DELETE .../invitations/{invitationId:guid}` |
| 9.7 | แก้ไขโปรไฟล์ + lifecycle ผู้ใช้ร้านค้า | `PUT .../users/{id}` + 4 composed (approve/reject/suspend/reactivate) |
| 9.8 | Role RBAC ฝั่งผู้ใช้ร้านค้า | `POST/PUT/DELETE .../users/roles(/{code})`, `PUT .../users/{id}/roles` |

---

## 9.1 Provision ร้านค้าใหม่ (Super-only)

```mermaid
sequenceDiagram
    autonumber
    actor U as Super admin
    participant SPA as Admin console SPA
    participant API as API POST /merchants
    participant H as ProvisionMerchantHandler
    participant PC as ProvisioningCoordinator
    participant DB as ControlPlaneDB + CommerceDB (txn เดียว)

    Note over U,API: Phase A — validate ก่อนเข้า transaction (pure)
    U->>SPA: กรอกฟอร์ม provision ร้านค้า + PSP connection
    SPA->>API: POST /api/v1/merchants (Super, Authorization Bearer)
    Note over API: policy admin (Bearer) + RequirePlatformUserTier(Super) ดู § 0.1 (ไม่มี CSRF)
    API->>H: ProvisionMerchantCommand
    H->>H: validate code / PSP / methods / channels
    alt validate ไม่ผ่าน
        H-->>API: ArgumentException
        API-->>SPA: 400 ProblemDetails
    else ผ่าน
        H->>DB: ExistsByCodeAsync(code)
        alt code ซ้ำ
            DB-->>H: true
            H-->>API: ConflictException
            API-->>SPA: 409 already provisioned
        else ไม่ซ้ำ
            Note over H,PC: Phase B — เขียนใน txn เดียว (Provisioning UoW)
            H->>PC: ProvisionAsync(spec, callerAdminId, expectedAuthorizationVersion, operationKey)
            PC->>DB: BEGIN TX
            PC->>DB: SELECT acct.Accounts Status=Active, AuthorizationVersion=expected + EXISTS access.PlatformAccess active (UPDLOCK)
            alt caller ไม่ผ่าน recheck
                DB-->>PC: 0 rows
                PC-->>H: WriteGuardException
                H-->>API: unhandled exception
                API-->>SPA: 500 (ไม่มี case เฉพาะใน ProblemDetailsExceptionHandler)
            else ผ่าน
                PC->>DB: INSERT ledger row (operationKey unique)
                alt ledger key ชนกับของเดิม
                    DB-->>PC: duplicate key
                    alt caller/hash ไม่ตรง
                        PC-->>H: ConflictException
                        H-->>API: 409
                        API-->>SPA: 409 operation key ถูกใช้โดยอื่น
                    else ตรง และมี Result
                        PC-->>H: ผลลัพธ์เดิม (replay)
                        H-->>API: ProvisionMerchantResult
                        API-->>SPA: 201 (ผลเดิม)
                    end
                else insert ใหม่
                    PC->>DB: Add Merchant + PspConnection(s) + AccountMethod(s) + VaultSecretBlob(s) + ProvisioningAudit
                    PC->>DB: SaveChanges (ทั้งสอง context) -> COMMIT -> AcceptAllChanges
                    DB-->>PC: OK
                    PC-->>H: ProvisioningWriteResult
                    H-->>API: ProvisionMerchantResult
                    API-->>SPA: 201 Created Location /api/v1/merchants/{code}
                end
            end
        end
    end
```

---

## 9.2 Merchant-user OIDC login + callback

```mermaid
sequenceDiagram
    autonumber
    actor App as ผู้ใช้ร้านค้า / ผู้สมัคร
    participant SPA as Merchant console SPA
    participant API as API
    participant IDP as Microsoft Entra ID (external)
    participant MW as OIDC middleware<br/>scheme MerchantUserMicrosoft
    participant LS as UserLoginService
    participant DB as DB (Sessions, Accounts)

    Note over App,API: Phase A — Challenge ไป IdP
    App->>SPA: กด เข้าสู่ระบบ
    SPA->>API: GET /merchants/auth/{provider}/login?returnTo=
    API->>API: provider รู้จัก + ReturnUrlPolicy.Resolve
    API-->>SPA: 302 Challenge ไป Microsoft (PKCE, state, nonce)
    SPA->>IDP: ไปยืนยันตัวที่ Entra
    IDP-->>MW: 302 callback ?code and state

    Note over MW,LS: Phase B — callback validate + 4-way branch
    MW->>MW: protocol validate (issuer/aud/nonce/sig), OnTokenValidated tid gate
    alt protocol หรือ tenant gate ไม่ผ่าน
        MW->>LS: DenyAsync(reason)
        LS->>DB: audit AuthDenied (scope ใหม่)
        LS-->>SPA: 302 ErrorPath?reason=...
    else ผ่าน
        MW->>LS: OnTicketReceived HandleCallbackAsync(subject, email)
        alt subject/email ไม่มี
            LS->>DB: audit AuthDenied
            LS-->>SPA: 302 reason=missing-identity
        else มีครบ
            LS->>DB: ResolveLoginQuery(provider, subject)
            alt resolve โยน exception
                LS->>DB: audit AuthDenied
                LS-->>SPA: 302 reason=resolve-failed
            else สำเร็จ
                alt Active
                    LS->>DB: Session.Start + AuthAudit LoginSuccess (SaveChanges ร่วม)
                    LS-->>SPA: Set-Cookie __Host-mch_session + mch_csrf, 302 returnTo
                else NotFound หรือ Rejected
                    LS->>LS: mint ticket Registration/Correction (ไม่มี DB row)
                    LS-->>SPA: 302 RegisterUrl?ticket=...
                else PendingApproval
                    LS-->>SPA: 302 ErrorPath?reason=awaiting-approval (ไม่ audit)
                else Suspended หรือ อื่น
                    LS->>DB: audit AuthDenied reason=suspended
                    LS-->>SPA: 302 ErrorPath?reason=suspended
                end
            end
        end
    end
    Note over MW,LS: Properties.Items[merchant_invitation_id] มี branch invitation ticket ในโค้ด แต่ไม่มี endpoint ใน T09 ตั้งค่านี้ตอน Challenge (ดู activities.md Notes)
```

---

## 9.3 Logout เครื่องนี้ / ทุกเครื่อง

```mermaid
sequenceDiagram
    autonumber
    actor U as Merchant user
    participant SPA as Merchant console SPA
    participant API as API
    participant BF as BoundFilter (group /merchants/auth)
    participant DB as DB (Sessions)

    Note over U,API: Phase A — logout (AllowAnonymous แต่กลุ่มมี BoundFilter)
    U->>SPA: กด ออกจากระบบเครื่องนี้
    SPA->>API: POST /merchants/auth/logout (+cookie ถ้ามี, X-CSRF mch_csrf)
    API->>API: rate limit merchant-user-auth + CSRF ดู § 0.3 และ § 0.4
    API->>BF: default ConsoleSession scheme เลือก MerchantUserSession (ไม่มี policy บน endpoint)
    alt ไม่มี cookie หรือ cookie invalid/expired
        BF-->>SPA: 403 The selected console account is not active
    else มี session ที่ bound (Active)
        BF->>API: ผ่าน เข้า handler
        API->>DB: FindByTokenHashAsync + RevokeFamilyAsync(FamilyId)
        API->>DB: MerchantAuthAudit Logout, SaveChanges
        API-->>SPA: 204 + clear cookies
    end

    Note over U,API: Phase B — logout-all (policy merchant-user)
    U->>SPA: กด ออกจากระบบทุกเครื่อง
    SPA->>API: POST /merchants/auth/logout-all (cookie + CSRF)
    API->>API: policy merchant-user + BoundFilter ดู § 0.1, CSRF ดู § 0.3
    alt auth หรือ BoundFilter ไม่ผ่าน
        API-->>SPA: 401 หรือ 403 (lifecycle code หรือ csrf_failed)
    else ผ่าน
        API->>DB: RevokeAllForUserAsync(userId)
        API->>DB: MerchantAuthAudit LogoutAll, SaveChanges
        API-->>SPA: 204 + clear cookies
    end
```

---

## 9.4 ส่งคำขอลงทะเบียนผู้ใช้ร้านค้า (self-service)

```mermaid
sequenceDiagram
    autonumber
    actor App as ผู้สมัคร
    participant SPA as Register page
    participant API as API
    participant H as SubmitRegistrationHandler
    participant PS as PhotoStore
    participant DB as pol_admin DB

    Note over App,API: Phase A — อ่านและ validate multipart form
    App->>SPA: กรอกฟอร์ม + แนบรูป (ticket จาก callback)
    SPA->>API: POST /merchants/users/register (multipart, ticket, photo)
    API->>API: bound body size, HasFormContentType, ReadFormAsync
    alt เกินขนาด หรือ ไม่ใช่ multipart
        API-->>SPA: 413 หรือ 400
    else อ่านฟอร์มได้
        API->>API: tickets.TryUnprotect(ticket)
        alt ticket ไม่ถูกต้อง
            API-->>SPA: 400 registration-link-invalid
        else ผ่าน
            API->>API: PhotoValidation (photo บังคับ, kycPhoto ไม่บังคับ)
            alt validate รูปไม่ผ่าน
                API-->>SPA: 400 หรือ 413
            else ผ่าน
                opt ticket.InvitationId มีค่า
                    API->>DB: ResolveInvitationByIdQuery + เทียบ email
                    alt ไม่ตรงหรือไม่พบ
                        API-->>SPA: 400 invitation-invalid
                    end
                end
                Note over API,H: Phase B — pol_admin transaction เดียว
                API->>H: SubmitRegistrationCommand
                alt Purpose เป็น Registration
                    H->>DB: FindByIdentityAsync(provider, subject)
                    alt มีอยู่แล้ว
                        H-->>API: 409 already-registered
                    else ไม่มี
                        opt invitationId ผูกมา
                            H->>DB: FindByIdUnfilteredAsync(invitationId)
                            alt used หรือ invalid หรือ expired
                                H-->>API: 400 หรือ 409 (invitation-invalid หรือ invitation-used)
                            end
                        end
                        H->>DB: User.Register หรือ RegisterInvited + ExternalLogin.Create
                    end
                else Purpose เป็น Correction
                    H->>DB: FindByIdentityAsync
                    alt ไม่พบ
                        H-->>API: 409 (ไม่มี record ให้แก้)
                    else พบ
                        H->>H: account.Resubmit (ต้อง Rejected เดิม)
                    end
                end
                H->>PS: PutAsync(photo) และผูก staged KYC กับ account
                H->>DB: RegistrationAttempt snapshot + audit + outbox MerchantUserRegistrationSubmitted
                H->>DB: SaveChanges
                alt unique index ชน (race)
                    DB-->>H: ConcurrencyConflict หรือ Conflict
                    H-->>API: 409 already-registered หรือ invitation-used
                else สำเร็จ
                    H-->>API: SubmitRegistrationResult(userId, PendingApproval)
                    API-->>SPA: 201 Location .../users/{userId}
                    Note over H,DB: (async หลัง commit) outbox MerchantUserRegistrationSubmitted -> MerchantUserOutboxDispatcher -> RegistrationConsumer เขียน RegistrationNotice ฝั่ง control-plane ดู § 0.8
                    Note over H,DB: เมื่อมี KYC photo แนบมา (async หลัง commit) outbox KycPhotoLifecycleRequested -> KycPhotoLifecycleConsumer commit object ใหม่ + ลบของเก่าถ้าเปลี่ยน
                end
            end
        end
    end
```

---

## 9.5 Read model ผู้ใช้ร้านค้า + ร้านค้า (me / list / detail / catalog)

```mermaid
sequenceDiagram
    autonumber
    actor U as Admin หรือ Merchant user
    participant SPA as Console SPA
    participant API as API
    participant H as Query handler
    participant DB

    Note over U,API: Phase A — me (อ่านจาก scope ไม่ query ใหม่)
    U->>SPA: เปิดหน้าโปรไฟล์ตัวเอง
    SPA->>API: GET /merchants/users/me (policy merchant-user ดู § 0.1)
    API->>API: อ่าน IUserScope.Current
    API-->>SPA: 200 userId/email/merchantId/roles/permissions

    Note over SPA,API: Phase B — list (dual-console + SFS)
    SPA->>API: GET /merchants/users?page and limit and filters (policy dual-console ดู § 0.1, § 0.6)
    API->>API: SfsQueryParser.Parse
    alt parse ผิด หรือ merchantId ไม่ใช่ UUID
        API-->>SPA: 400
    else admin เลือก merchantId นอก accessible scope
        API-->>SPA: 404
    else ผ่าน
        API->>H: ListMerchantUsersQuery
        H->>DB: query แบ่งหน้า + role codes ต่อรายการ
        DB-->>H: page
        H-->>API: PagedResult
        API-->>SPA: 200
    end

    Note over SPA,DB: Phase C — detail + ETag (composed: permissions/roles/edit/merchant code คล้ายกัน)
    SPA->>API: GET /merchants/users/{id} (policy dual-console)
    API->>H: GetMerchantUserQuery
    H->>DB: FindByIdAsync หรือ FindByIdForAdminAsync
    alt ไม่พบ
        DB-->>H: null
        H-->>API: null
        API-->>SPA: 404
    else พบ
        DB-->>H: user
        H-->>API: MerchantUserDetail
        API->>API: VersionEtags.Set ดู § 0.5
        API-->>SPA: 200 + ETag vN
    end
    Note over API,DB: composed: GET /permissions (catalog คงที่), GET /roles และ /roles/{code} (RoleSideContext.Merchant ไม่มี ETag), GET /{id}/edit (เหมือน detail แต่เขียน audit Reveal ระหว่างอ่าน), GET /merchants/{code} (entity Merchant policy admin + permission merchant.view)
```

---

## 9.6 เชิญ / เพิกถอนคำเชิญผู้ใช้ร้านค้า

```mermaid
sequenceDiagram
    autonumber
    actor U as Merchant user (users.manage)
    participant SPA
    participant API
    participant H as CreateInvitationHandler / RevokeInvitationHandler
    participant DB
    participant OB as Outbox / worker ดู § 0.8

    Note over U,API: Phase A — create
    U->>SPA: กรอกอีเมลที่จะเชิญ
    SPA->>API: POST /merchants/users/invitations {email} (policy merchant-user + permission users.manage)
    API->>H: CreateInvitationCommand
    H->>H: TtlHours (config MerchantUser:Invitation:TtlHours default 24, ไม่ใช่จาก request body) อยู่ในช่วง 1-168?
    alt นอกช่วง
        H-->>API: 400
    else ผ่าน
        H->>DB: txn FindPendingByNormalizedEmailAsync(email)
        opt มี invitation pending เดิม
            H->>DB: old.Revoke + audit InviteRevoke
        end
        H->>DB: Invitation.Create + audit InviteCreate
        H->>OB: Enqueue MerchantUserInvitationDeliveryRequested (protected token)
        H->>DB: SaveChanges
        H-->>API: CreateInvitationResult (maskedEmail, ไม่คืน raw token)
        API-->>SPA: 201 Location .../invitations/{id}
    end
    Note over H: .WithDescription ของ endpoint บอกว่าอีเมลซ้ำ -> 409 แต่ handler จริง revoke ของเดิมแล้วสร้างใหม่เงียบ ๆ (ดู activities.md Notes)

    Note over U,API: Phase B — revoke
    SPA->>API: DELETE /merchants/users/invitations/{id}
    API->>H: RevokeInvitationCommand
    H->>DB: FindByIdAsync(id)
    alt ไม่พบ
        H-->>API: 404
    else พบ และ AcceptedAt ไม่ null
        H-->>API: 409 An accepted invitation cannot be revoked
    else พบ และยัง pending หรือ revoked ไปแล้ว
        H->>DB: invitation.Revoke + audit InviteRevoke, SaveChanges
        H-->>API: 204
    end

    Note over OB: Phase C (async) — ส่งอีเมลจริงนอก request
    OB->>OB: MerchantUserInvitationDeliveryHandler unprotect token + ส่งอีเมล
```

---

## 9.7 แก้ไขโปรไฟล์ + lifecycle ผู้ใช้ร้านค้า

```mermaid
sequenceDiagram
    autonumber
    actor U as Merchant user manager (users.manage)
    participant SPA
    participant API
    participant H as UpdateMerchantUserHandler / ChangeMerchantUserLifecycleHandler
    participant DB
    participant SS as SessionStore

    Note over U,API: Phase A — แก้ไขโปรไฟล์
    SPA->>API: PUT /merchants/users/{id} {firstName และ field อื่น}
    API->>H: UpdateMerchantUserCommand
    H->>DB: FindByIdAsync(id)
    alt ไม่พบ
        H-->>API: 404
    else พบ
        H->>DB: UpdateProfile + audit Update, SaveChanges
        H-->>API: 204
    end

    Note over U,API: Phase B — lifecycle (approve / reject / suspend / reactivate)
    SPA->>API: POST /merchants/users/{id}/{action}
    opt action เป็น approve หรือ suspend หรือ reactivate
        API->>DB: AcquirePaymentAuthorizationExclusiveAsync(merchantId)
    end
    API->>H: ChangeMerchantUserLifecycleCommand
    H->>DB: FindByIdAsync(id)
    alt ไม่พบ
        H-->>API: 404
    else พบ
        alt id เป็น actor เอง และ action เป็น reject หรือ suspend
            H-->>API: 409 cannot reject/suspend yourself
        else ผ่าน self-check
            alt approve
                H->>H: Status ต้อง PendingApproval มิฉะนั้น 409
                H->>DB: Approve + assign role merchant_staff (409 ถ้า role หาย)
            else reject
                H->>SS: RevokeAllForUserAsync
                H->>DB: Reject
            else suspend
                H->>H: EnsureNotLastManagerAsync (409 ถ้าเป็น manager คนสุดท้าย)
                H->>SS: RevokeAllForUserAsync
                H->>DB: Suspend
            else reactivate
                H->>DB: Reactivate
            end
            H->>DB: audit ต่อ action, SaveChanges
            H-->>API: 204
        end
    end
```

---

## 9.8 Role RBAC ฝั่งผู้ใช้ร้านค้า

```mermaid
sequenceDiagram
    autonumber
    actor U as Merchant user (roles.manage / users.roles)
    participant SPA
    participant API
    participant H as Iam.Application.Roles handlers / SetRolesHandler
    participant DB

    Note over U,API: Phase A — create / update / delete role (RoleSideContext.Merchant)
    SPA->>API: POST หรือ PUT หรือ DELETE /merchants/users/roles(/{code})
    API->>H: CreateRoleCommand หรือ UpdateRoleCommand หรือ DeleteRoleCommand
    alt POST และ code ซ้ำ (รวม shared bucket)
        H-->>API: 409
    else PUT หรือ DELETE และไม่พบใน visible set
        H-->>API: 404
    else PUT หรือ DELETE และ role ไม่ใช่ของร้านนี้ (shared seed)
        H-->>API: 409
    else PUT deactivate seed anchor merchant_manager หรือ DELETE seed anchor
        H-->>API: 409
    else DELETE และยังมีผู้ใช้ผูกอยู่
        H-->>API: 409
    else permission key นอก catalog ฝั่ง Merchant
        H-->>API: 400
    else ผ่าน
        H->>DB: mutate + audit RoleCreated/Updated/Deleted, SaveChanges
        H-->>API: 201 หรือ 200 หรือ 204 (ไม่มี If-Match/ETag ต่างจาก § 2.4 Platform role)
    end

    Note over U,API: Phase B — set roles ให้ target user
    SPA->>API: PUT /merchants/users/{id}/roles {roleCodes}
    API->>H: SetRolesCommand
    H->>DB: FindByIdAsync(target)
    alt target ไม่ Active หรือคนละร้าน
        H-->>API: 404 (ไม่ leak)
    else พบ
        H->>DB: GetRoleIdsByCodesAsync(roleCodes)
        alt มี code ไม่รู้จัก
            H-->>API: 400
        else รู้จักหมด
            alt ถอด merchant_manager จาก manager Active คนสุดท้าย
                H-->>API: 409
            else ผ่าน
                H->>DB: add/remove RoleAssignment + BumpVersion + audit SetRoles, SaveChanges
                H-->>API: 204
            end
        end
    end
```

---

## Notes

- ดู `09-merchant-provision-users-auth.activities.md` สำหรับ Deviations ทั้งหมด, สาเหตุที่ `PUT .../{id}` ไม่มี ETag/If-Match, และ evidence ของ `BoundFilter` ต่อ logout
- callback (§ 9.2) ทุกทางออกเป็น 302 redirect ตาม § 0.9 ไม่มีทางออกเป็น JSON เลย เพราะเป็น browser navigation flow
- § 9.1 ไม่มี external call จริง (PSP adapter แค่ตรวจ SupportedMethods ในหน่วยความจำ ไม่ยิง HTTP ไปที่ PSP ตอน provision)

**Render**: GitHub / Obsidian / VS Code Mermaid
