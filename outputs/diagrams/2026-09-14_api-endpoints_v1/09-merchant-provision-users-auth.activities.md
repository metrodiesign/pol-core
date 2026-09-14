# pol-core API — Merchant provisioning, merchant users และ merchant OIDC (Activity Diagrams)

> Source: `docs/reference/api-endpoints.md` section "Admin และ merchant identity" บรรทัด L204-L227 และ section "OIDC callback ที่ middleware จัดการ" บรรทัด L393, source ที่อ้างต่อ § (`src/Api/Api/Program.cs:2506-3148`, `src/Api/Api/Merchants/UserOidcAuthentication.cs`, `UserLoginService.cs`, `UserSessionAuthenticationHandler.cs`, `UserPermissionAuthorization.cs`, `src/Application/Modules/Merchants.Application/ProvisionMerchant/ProvisionMerchantHandler.cs`, `src/Infrastructure/Persistence/Persistence.Provisioning/ProvisioningCoordinator.cs`, `src/Application/Modules/Merchants.Application/Users/SubmitRegistration.cs`, `ManageMerchantUsers.cs`, `SetUserRoles.cs`, `src/Application/Modules/Iam.Application/Roles/*.cs`)
> Scope: 25 endpoints — provision ร้านค้า (Super-only), merchant-user OIDC login/logout/callback, self-service registration, read model ของผู้ใช้ร้านค้า/ร้านค้า, invitation, profile + lifecycle mutation, role RBAC ฝั่งร้านค้า
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

Super validate ทุกอย่างก่อนเข้า transaction (pure, ไม่มี side effect) แล้วเปิด txn เดียวคุมทั้ง control plane และ commerce runtime พร้อม idempotency ledger ที่ key จาก merchant code (source: `src/Api/Api/Program.cs:2370-2411`, `src/Application/Modules/Merchants.Application/ProvisionMerchant/ProvisionMerchantHandler.cs:49-118`, `src/Infrastructure/Persistence/Persistence.Provisioning/ProvisioningCoordinator.cs:72-221,223-249`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy admin (Bearer PlatformToken) + RequirePlatformUserTier(Tier.Super) ดู § 0.1<br/>admin เป็น Bearer ไม่มี CSRF"]
    AUTHZ --> BODY["body merchant + pspConnections list<br/>CorrelationId = TraceIdentifier, CallerAdminId จาก scope"]
    BODY --> SECCHK{"ProvisioningGuards.RejectSecretsInConfig:<br/>config มี secretKey/publicKey/webhookSecret?"}
    SECCHK -->|yes| R400SEC["400 secret field ต้องอยู่ใน secrets ไม่ใช่ config"]
    SECCHK -->|no| CODE{"MerchantCode.Normalize + IsAllowed<br/>(captive allowlist)?"}
    CODE -->|no| R400C["400 Merchant code ไม่อยู่ใน allowlist"]
    CODE -->|yes| PSP{"PSP connections มี >=1, code รู้จัก, ไม่ซ้ำ,<br/>methods อยู่ใน adapter.SupportedMethods?"}
    PSP -->|no| R400P["400 ArgumentException<br/>PSP ไม่รู้จัก / ซ้ำ / method ไม่รองรับ"]
    PSP -->|yes| CHAN{"ทุก enabledChannel ของร้านค้า<br/>มี PSP connection ที่รองรับ method นั้น?"}
    CHAN -->|no| R400M["400 payment method ไม่มี PSP connection รองรับ"]
    CHAN -->|yes| ENV["PspSecretEnvelopeFactory.Build ต่อ connection<br/>(secrets เข้ารหัสใน envelope, masked hints สำหรับอ่านคืน)"]
    ENV --> EXIST{"ExistsByCodeAsync(code) (pre-check นอก txn)?"}
    EXIST -->|yes| R409E["409 Merchant is already provisioned"]
    EXIST -->|no| TXN["ProvisioningCoordinator: เปิด txn เดียวคุม<br/>ControlPlaneDbContext + CommerceDbContext"]
    TXN --> RECHECK{"VerifyCallerIsActiveSuperAsync (WITH UPDLOCK, HOLDLOCK):<br/>acct.Accounts Status=Active, AuthorizationVersion ตรงที่ pin ไว้,<br/>และมี access.PlatformAccess active (Super)?"}
    RECHECK -->|no| R500["500 An unexpected error occurred<br/>(WriteGuardException ไม่มี case เฉพาะใน ProblemDetailsExceptionHandler)"]
    RECHECK -->|yes| LEDGER["INSERT ledger row operationKey=provision-merchant:{code}<br/>(parameterized, unique index)"]
    LEDGER --> DUPKEY{"insert ชนกับ key เดิม (duplicate)?"}
    DUPKEY -->|"caller/hash ไม่ตรง"| R409D["409 operation key ถูกใช้โดย caller/payload อื่น"]
    DUPKEY -->|"caller/hash ตรง + มี Result"| REPLAY["คืนผลลัพธ์เดิมจาก ledger (deserialize)"]
    DUPKEY -->|no| WRITE["สร้าง Merchant + PspConnection(s) +<br/>MerchantProviderAccountMethod(s) + VaultSecretBlob(s)<br/>+ ProvisioningAudit ทั้งหมดใน txn เดียว"]
    WRITE --> SAVE["SaveChanges(false) ทั้งสอง context แล้ว Commit แล้ว AcceptAllChanges<br/>(retry ได้ถึง 3 ครั้งเมื่อ transient error, verify-before-retry กัน double-write)"]
    SAVE --> R201["201 Created Location /api/v1/merchants/{code}<br/>body merchantId + connections (masked secrets)"]
    REPLAY --> R201
    R201 --> END_S((◉))
    R400SEC --> END_F((◉))
    R400C --> END_F
    R400P --> END_F
    R400M --> END_F
    R409E --> END_F
    R500 --> END_F
    R409D --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class ENV,TXN,LEDGER,WRITE,SAVE,REPLAY,R201,END_S ok
    class R400SEC,R400C,R400P,R400M,R409E,R500,R409D,END_F fail
    class SECCHK,CODE,PSP,CHAN,EXIST,RECHECK,DUPKEY gate
```

---

## 9.2 Merchant-user OIDC login + callback

login ตรวจ provider + returnTo แล้ว Challenge ไป Microsoft, callback ไม่มี endpoint map แต่ OIDC middleware ตรวจ protocol และ tenant gate แล้วให้ `UserLoginService` เดินสาขาตามสถานะบัญชี (Active/NotFound/Rejected/PendingApproval/Suspended) ไม่มี JIT provision และไม่มี Graph employeeId gate เหมือนฝั่ง admin (source: `src/Api/Api/Program.cs:2600-2619`, `Merchants/UserOidcAuthentication.cs:73-157`, `Merchants/UserLoginService.cs:89-245`, `src/Api/appsettings.json:40-52`)

```mermaid
flowchart TD
    START((●)) --> LOGIN["GET /api/v1/merchants/auth/{provider}/login?returnTo=<br/>AllowAnonymous, rate limit merchant-user-auth ดู § 0.4"]
    LOGIN --> PROV{"provider slug อยู่ใน UserOidcProviders<br/>(microsoft ที่ตั้ง ClientId แล้ว)?"}
    PROV -->|no| R404["404 (Results.NotFound ไม่มี body)"]
    PROV -->|yes| RET["ReturnUrlPolicy.Resolve: returnTo ต้องอยู่ใน<br/>ReturnUrlAllowlist ไม่ผ่านใช้ DefaultReturnPath"]
    RET --> CHAL["302 Challenge ไป Microsoft authorize<br/>AuthenticationProperties.RedirectUri = returnTo (ไม่ตั้ง merchant_invitation_id)<br/>response_type=code, PKCE S256, state, nonce<br/>scope openid email profile"]
    CHAL --> IDP["ผู้ใช้ยืนยันตัวที่ Entra ID (นอก frame)"]
    IDP --> CB["GET /api/v1/merchants/auth/microsoft/callback?code&state<br/>OIDC middleware scheme MerchantUserMicrosoft (ไม่มี endpoint map)"]
    CB --> PROTO{"state/correlation cookie ตรง, code exchange สำเร็จ,<br/>id_token ผ่าน issuer/aud/nonce/signature/lifetime?"}
    PROTO -->|"error=access_denied"| DENY_AD["OnAccessDenied: reason access-denied"]
    PROTO -->|no| DENY_RF["OnRemoteFailure: reason auth-failed (ทั่วไป)"]
    PROTO -->|yes| GATE{"OnTokenValidated: tid อยู่ใน AllowedTenants<br/>(เมื่อตั้งค่าไว้)?"}
    GATE -->|no| DENY_TG["context.Fail แล้วไป OnRemoteFailure<br/>reason tenant-missing (tid-required) / tenant-not-allowed"]
    GATE -->|yes| TICKET["OnTicketReceived: UserLoginService.HandleCallbackAsync<br/>subject/email จาก id_token เท่านั้น"]
    TICKET --> IDENT{"subject และ email มีค่า?"}
    IDENT -->|no| DENY_MI["reason missing-identity"]
    IDENT -->|yes| INVCHK{"Properties.Items[merchant_invitation_id] มีค่า?<br/>(ไม่มี endpoint ใน T09 ตั้งค่านี้ตอน Challenge - ดู Notes)"}
    INVCHK -->|"yes (unreachable จาก #4 ปัจจุบัน)"| INVFLOW["resolve invitation: ไม่ตรง/หมดอายุ ได้ reason invitation-invalid<br/>ตรง mint ticket Registration ผูก invitationId"]
    INVFLOW --> END_S
    INVCHK -->|no| RESOLVE{"ResolveLoginQuery(provider, subject)<br/>โยน exception?"}
    RESOLVE -->|yes| DENY_RS["reason resolve-failed"]
    RESOLVE -->|no| OUTCOME{"LoginOutcome?"}
    OUTCOME -->|Active| SESS["Session.Start (token hash SHA-256, ip, user-agent 256)<br/>+ AuthAudit LoginSuccess SaveChanges ร่วมกัน"]
    SESS --> SESSOK{"บันทึก session สำเร็จ?"}
    SESSOK -->|no| DENY_SW["reason session-write-failed"]
    SESSOK -->|yes| COOKIE["Set-Cookie __Host-mch_session (HttpOnly) + mch_csrf<br/>302 WebAppBaseUrl + returnTo (SafeReturn)"]
    COOKIE --> END_S((◉))
    OUTCOME -->|NotFound| TICK_REG["mint ticket Registration (signed+time-limited, ไม่มี server row)<br/>302 SPA RegisterUrl?ticket=..."]
    TICK_REG --> END_S
    OUTCOME -->|Rejected| TICK_COR["mint ticket Correction<br/>302 SPA RegisterUrl?ticket=..."]
    TICK_COR --> END_S
    OUTCOME -->|PendingApproval| PEND["302 SPA ErrorPath?reason=awaiting-approval<br/>ไม่มี session, ไม่เขียน audit (lifecycle ปกติ ไม่ใช่ security failure)"]
    PEND --> END_S
    OUTCOME -->|"Suspended / อื่น"| DENY_SUSP["reason suspended"]
    DENY_AD --> DENY
    DENY_RF --> DENY
    DENY_TG --> DENY
    DENY_MI --> DENY
    DENY_RS --> DENY
    DENY_SW --> DENY
    DENY_SUSP --> DENY
    DENY["UserLoginService.DenyAsync: audit AuthDenied บน scope ใหม่<br/>302 WebAppBaseUrl + ErrorPath?reason=... (ค่าเริ่มต้น /login-error)"]
    DENY --> END_F((◉))
    R404 --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef ext fill:#4a3a1f,stroke:#d29922,color:#fff
    classDef warn fill:#5c4813,stroke:#e3b341,color:#fff
    class CHAL,SESS,COOKIE,END_S ok
    class R404,DENY_AD,DENY_RF,DENY_TG,DENY_MI,DENY_RS,DENY_SW,DENY_SUSP,DENY,END_F fail
    class PROV,PROTO,GATE,IDENT,INVCHK,RESOLVE,OUTCOME,SESSOK gate
    class IDP,CB ext
    class TICK_REG,TICK_COR,PEND,INVFLOW warn
```

---

## 9.3 Logout เครื่องนี้ / ทุกเครื่อง

ทั้งสอง endpoint อยู่ในกลุ่ม `/merchants/auth` ที่มี `BoundFilter` ผูกไว้ระดับกลุ่ม (ต่างจาก admin logout ที่กลุ่มไม่มี filter นี้เลย) จึงต้องมี merchant-user session ที่ bound จริงก่อนถึงจะ revoke ได้ แม้ endpoint แรกจะ `AllowAnonymous` (source: `src/Api/Api/Program.cs:2765-2819`, `Merchants/UserPermissionAuthorization.cs:16-29`, `Iam/ConsoleSessionAuthentication.cs:33-49`)

```mermaid
flowchart TD
    START((●)) --> WHICH{"endpoint?"}
    WHICH -->|"POST /api/v1/merchants/auth/logout"| RL["AllowAnonymous, rate limit merchant-user-auth ดู § 0.4<br/>CSRF mch_csrf จาก group ดู § 0.3"]
    RL --> RLOK{"ผ่าน rate limit + CSRF?"}
    RLOK -->|no| R4XX["429 Retry-After หรือ 403 csrf_failed"]
    RLOK -->|yes| BF{"BoundFilter: default ConsoleSession scheme เลือก<br/>MerchantUserSession เสมอ (ไม่มี policy บน endpoint นี้)<br/>session cookie ผูกกับ merchant user ที่ Active?"}
    BF -->|"ไม่มี cookie / cookie invalid / expired"| R403BF["403 The selected console account is not active<br/>(BoundFilter, ดู § 0.1) - ไม่ใช่ 204 เงียบ ๆ แม้ endpoint AllowAnonymous"]
    BF -->|yes| REVF["FindByTokenHashAsync + RevokeFamilyAsync(FamilyId) เฉพาะเครื่องนี้<br/>MerchantAuthAudit Logout, SaveChanges<br/>(cookie/session ตามจริงมีเสมอเพราะผ่าน BF มาแล้ว - ดู Notes)"]
    REVF --> CLEAR
    WHICH -->|"POST /api/v1/merchants/auth/logout-all"| AUTHZ["policy merchant-user + BoundFilter ดู § 0.1<br/>CSRF mch_csrf ดู § 0.3"]
    AUTHZ --> AZOK{"ผ่าน auth + BoundFilter + CSRF?"}
    AZOK -->|no| R401["401 default หรือ 403 lifecycle code / csrf_failed ดู § 0.1"]
    AZOK -->|yes| REVA["RevokeAllForUserAsync(scope.Current.UserId) ทุกเครื่อง<br/>MerchantAuthAudit LogoutAll, SaveChanges"]
    REVA --> CLEAR["cookies.Clear: ลบ __Host-mch_session + mch_csrf"]
    CLEAR --> R204["204 No Content"]
    R204 --> END_S((◉))
    R4XX --> END_F((◉))
    R403BF --> END_F
    R401 --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class REVF,REVA,CLEAR,R204,END_S ok
    class R4XX,R403BF,R401,END_F fail
    class WHICH,RLOK,BF,AZOK gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| POST | `/api/v1/merchants/auth/logout` | diagram — AllowAnonymous แต่กลุ่มมี `BoundFilter` จึงต้องมี session ที่ bound จริงก่อน revoke ได้ (ต่างจาก employee logout § 1.7 ที่ไม่มี filter นี้) ไม่มี session -> 403 ไม่ใช่ 204 |
| POST | `/api/v1/merchants/auth/logout-all` | diagram — policy `merchant-user` (401/403 มาตรฐาน § 0.1), revoke ทุก session ของ user, audit `LogoutAll` |

---

## 9.4 ส่งคำขอลงทะเบียนผู้ใช้ร้านค้า (self-service)

Anonymous + ticket-gated multipart: จำกัดขนาด body ก่อนอ่าน form, validate ticket/รูป/invitation ก่อนเข้า `pol_admin` transaction เดียวที่ create หรือ resubmit บัญชี พร้อม audit + outbox ในทรานแซกชันเดียวกัน (source: `src/Api/Api/Program.cs:2633-2756`, `src/Application/Modules/Merchants.Application/Users/SubmitRegistration.cs:123-262`)

```mermaid
flowchart TD
    START((●)) --> REG["POST /api/v1/merchants/users/register<br/>AllowAnonymous, DisableAntiforgery, rate limit merchant-user-auth ดู § 0.4<br/>multipart/form-data"]
    REG --> SIZE["จำกัด MaxRequestBodySize = 2xPhotoMaxBytes + 64KB ก่อนอ่าน form"]
    SIZE --> FORMCT{"HasFormContentType?"}
    FORMCT -->|no| R400FC["400 multipart/form-data is required"]
    FORMCT -->|yes| READFORM{"ReadFormAsync สำเร็จ (ไม่เกิน MaxRequestBodySize)?"}
    READFORM -->|"BadHttpRequestException"| R413A["413 The upload exceeds the size limit"]
    READFORM -->|yes| TICKET{"tickets.TryUnprotect(form.ticket)?"}
    TICKET -->|no| R400T["400 code registration-link-invalid<br/>ticket ไม่มี/ไม่ถูกต้อง/หมดอายุ"]
    TICKET -->|yes| PHOTO{"form.photo: ไฟล์มี, ขนาด<=PhotoMaxBytes,<br/>content-type+magic bytes ผ่าน PhotoValidation?"}
    PHOTO -->|"ไม่มีไฟล์"| R400PH["400 photo is required"]
    PHOTO -->|"เกินขนาด"| R413P["413 The photo exceeds the size limit"]
    PHOTO -->|"validate ไม่ผ่าน"| R400PV["400 (validation.Error)"]
    PHOTO -->|ok| KYC{"form.kycPhoto มีไฟล์?"}
    KYC -->|"มี, เกินขนาด"| R413K["413 The KYC photo exceeds the size limit"]
    KYC -->|"มี, validate ไม่ผ่าน"| R400PV
    KYC -->|"มี ผ่าน / ไม่มี"| FIELDS{"firstName, lastName, idNumber,<br/>producerCode, phone ครบ?"}
    FIELDS -->|no| R400F["400 required fields ขาด"]
    FIELDS -->|yes| INV{"ticket.InvitationId มีค่า?"}
    INV -->|yes| BIND["host: ResolveInvitationByIdQuery + เทียบ email normalized<br/>ไม่พบ/ไม่ตรง ได้ 400, พบแล้ว actorScope.Begin(merchantId) ชั่วคราว"]
    BIND --> BINDOK{"resolve สำเร็จ?"}
    BINDOK -->|no| R400INV["400 code invitation-invalid"]
    BINDOK -->|yes| TX
    INV -->|no| TX["pol_admin transaction: SubmitRegistrationHandler"]
    TX --> PURPOSE{"ticket.Purpose?"}
    PURPOSE -->|Registration| DUPID{"มีบัญชี (Provider,Subject) นี้แล้ว?"}
    DUPID -->|yes| R409DUP["409 code already-registered"]
    DUPID -->|no| INV2{"invitationId ผูกมา: accepted แล้ว<br/>หรือไม่ pending หรือ email ไม่ตรง?"}
    INV2 -->|"used"| R409IU["409 code invitation-used"]
    INV2 -->|"invalid/expired"| R400INV2["400 code invitation-invalid"]
    INV2 -->|"ok / ไม่มี invitation"| CREATE["User.Register หรือ RegisterInvited (PendingApproval)<br/>+ ExternalLogin, invitation.Accept + audit InviteAccept"]
    PURPOSE -->|Correction| FINDACC{"พบบัญชี (Provider,Subject) เดิม?"}
    FINDACC -->|no| R409NA["409 InvalidOperationException (ไม่มี record ให้แก้)"]
    FINDACC -->|yes| RESUB{"account.Resubmit: สถานะเดิมเป็น Rejected?"}
    RESUB -->|no| R409RS["409 domain guard (Rejected only)"]
    RESUB -->|yes| CORRECT["Resubmit (Rejected เปลี่ยนเป็น PendingApproval)"]
    CREATE --> APPLY["ApplyForm + ApplyPhoto (เก็บ object store)<br/>KYC: staged ก่อน txn -> ผูกกับ account ใน txn"]
    CORRECT --> APPLY
    APPLY --> ATTEMPT["RegistrationAttempt snapshot (AttemptNo ต่อบัญชี)<br/>audit Registered/Resubmitted + outbox MerchantUserRegistrationSubmitted"]
    ATTEMPT --> SAVE{"SaveChanges สำเร็จ (unique index ไม่ชน)?"}
    SAVE -->|"ConcurrencyConflict / unique ชน"| R409RACE["409 already-registered หรือ invitation-used (replay/concurrent submit)"]
    SAVE -->|yes| R201["201 Location /api/v1/merchants/users/{userId}<br/>body userId, status PendingApproval"]
    R201 --> END_S((◉))
    R201 -.async.-> BG_REG["outbox MerchantUserRegistrationSubmitted (merch.UserOutbox)<br/>MerchantUserOutboxDispatcher ดู § 0.8 -> RegistrationConsumer<br/>เขียน RegistrationNotice ฝั่ง control-plane ให้ Admin เห็น pending"]
    R201 -.async.-> BG_KYC["เมื่อมี KYC photo แนบมา: outbox KycPhotoLifecycleRequested<br/>MerchantUserOutboxDispatcher -> KycPhotoLifecycleConsumer<br/>commit object ใหม่ในสโตร์ + ลบ object เก่าถ้าเปลี่ยน"]
    R400FC --> END_F((◉))
    R413A --> END_F
    R400T --> END_F
    R400PH --> END_F
    R413P --> END_F
    R400PV --> END_F
    R413K --> END_F
    R400F --> END_F
    R400INV --> END_F
    R409DUP --> END_F
    R409IU --> END_F
    R400INV2 --> END_F
    R409NA --> END_F
    R409RS --> END_F
    R409RACE --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class CREATE,CORRECT,APPLY,ATTEMPT,R201,END_S ok
    class R400FC,R413A,R400T,R400PH,R413P,R400PV,R413K,R400F,R400INV,R409DUP,R409IU,R400INV2,R409NA,R409RS,R409RACE,END_F fail
    class FORMCT,READFORM,TICKET,PHOTO,KYC,FIELDS,INV,BINDOK,PURPOSE,DUPID,INV2,FINDACC,RESUB,SAVE gate
```

---

## 9.5 Read model ผู้ใช้ร้านค้า + ร้านค้า (me / list / detail / catalog)

reads ทั้งหมดผ่าน gate § 0.1 แล้วส่ง query แบบไม่มี transaction: `/me` อ่านจาก `IUserScope` ที่ middleware bind ไว้, list ผ่าน `SfsQueryParser`, detail ตรวจ existence ก่อนแล้วคืน 404 หรือ 200 + ETag (source: `src/Api/Api/Program.cs:2823-2916,3024-3064,2563-2584`, `src/Application/Modules/Merchants.Application/Users/ManageMerchantUsers.cs:218-304`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy merchant-user เดี่ยว หรือ dual-console + permission ตามแถว<br/>+ BoundFilter (กลุ่ม merchantUsers) ดู § 0.1<br/>(safe method ไม่มี CSRF)"]
    AUTHZ --> KIND{"endpoint?"}
    KIND -->|"GET /api/v1/merchants/users/me"| ME["อ่าน IUserScope.Current (ไม่ query ใหม่)<br/>roleCodes = ListActiveRoleCodesForUserAsync<br/>permissions = scope.Current.Permissions"]
    ME --> R200
    KIND -->|"GET /api/v1/merchants/users (list)"| SFS["SfsQueryParser.Parse(maxLimit:100) ดู § 0.6<br/>roleCode filter ต้องมี merchantId เมื่อ admin read"]
    SFS --> SFSOK{"parse ผ่าน + (admin read: merchantId เป็น UUID<br/>และ Accessible.Allows(merchantId))?"}
    SFSOK -->|"parse ผิด/เกิน cap"| R400["400 Invalid request ดู § 0.6"]
    SFSOK -->|"merchantId ไม่ใช่ UUID"| R400MID["400 code invalid_filter"]
    SFSOK -->|"merchantId นอก accessible scope"| R404MID["404 (ไม่ leak การมีอยู่)"]
    SFSOK -->|yes| LIST["ListMerchantUsersHandler: merchant console เห็นเฉพาะร้านตัวเอง<br/>admin console เห็นตาม accessible merchants (unrestricted = All)"]
    LIST --> R200
    KIND -->|"GET /api/v1/merchants/users/{merchantUserId:guid} (detail)"| Q["GetMerchantUserHandler: FindByIdAsync (merchant console)<br/>หรือ FindByIdForAdminAsync (admin console, accessible floor)"]
    Q --> NULLQ{"ผลเป็น null?"}
    NULLQ -->|yes| R404["404 ProblemDetails"]
    NULLQ -->|no| ETAG["VersionEtags.Set (EtagResponseMarker) ดู § 0.5"]
    ETAG --> R200["200 JSON (maskedEmail/maskedPhone/maskedLicenseNumber ผ่าน PiiMask)"]
    R200 --> END_S((◉))
    R400 --> END_F((◉))
    R400MID --> END_F
    R404MID --> END_F
    R404 --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class ME,LIST,ETAG,R200,END_S ok
    class R400,R400MID,R404MID,R404,END_F fail
    class KIND,SFSOK,NULLQ gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/merchants/users/me` | diagram — ไม่มี permission เพิ่มจาก policy `merchant-user`, ไม่ query DB ใหม่ (อ่านจาก scope), ไม่มี ETag |
| GET | `/api/v1/merchants/users` | diagram — policy `dual-console`, audience permission `merchants.users.view`(admin)/`users.view`(merchant), SFS filter `roleCode` ต้องมี `merchantId` เมื่อ admin, admin เลือก `merchantId` นอก scope -> 404, ไม่มี ETag |
| GET | `/api/v1/merchants/users/{merchantUserId:guid}` | diagram — policy `dual-console`, permission audience เดียวกับ list, ETag `vN`, 404 เมื่อไม่พบหรือ admin นอก scope |
| GET | `/api/v1/merchants/users/permissions` | composed — policy `merchant-user` + permission `roles.view`, catalog `Scope.Merchant` คงที่ ไม่มี query/404/ETag |
| GET | `/api/v1/merchants/users/roles` | composed — policy `merchant-user` + permission `roles.view`, `RoleSideContext.Merchant` (ของร้าน + shared seed), `Limit=int.MaxValue` ไม่ผ่าน SFS, ไม่มี ETag |
| GET | `/api/v1/merchants/users/roles/{code}` | composed — policy `merchant-user` + permission `roles.view`, 404 เมื่อไม่อยู่ visible set, ไม่มี ETag (ต่างจาก admin `roles/{code}` ที่มี ETag) |
| GET | `/api/v1/merchants/users/{merchantUserId:guid}/edit` | composed — policy `merchant-user` + permission `users.manage`, 404 เมื่อไม่พบ, **เขียน audit `Reveal` + SaveChanges ระหว่างอ่าน (ไม่ read-only จริง)**, คืนเฉพาะ field แก้ไขได้แบบไม่ mask |
| GET | `/api/v1/merchants/{code}` | composed — policy `admin` + permission `merchant.view` (ไม่ใช่ `merchant-user`), accessible-merchant floor (Scoped เห็นเฉพาะที่ assign), 404 เมื่อไม่พบ/นอก scope, ETag `vN`, entity เป็น Merchant ไม่ใช่ MerchantUser |

---

## 9.6 เชิญ / เพิกถอนคำเชิญผู้ใช้ร้านค้า

สร้าง invitation แบบ tenant-bound: TTL 1-168 ชั่วโมง, invitation pending เดิมของอีเมลเดียวกันถูก revoke อัตโนมัติแล้วสร้างใหม่ (ไม่ 409), enqueue outbox ส่งลิงก์แบบเข้ารหัส ไม่คืน raw token (source: `src/Api/Api/Program.cs:2918-2953`, `src/Application/Modules/Merchants.Application/Users/ManageMerchantUsers.cs:21-163`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy merchant-user + permission users.manage ดู § 0.1<br/>CSRF mch_csrf ดู § 0.3"]
    AUTHZ --> WHICH{"endpoint?"}
    WHICH -->|"POST /invitations"| TTL{"UserInvitationOptions.TtlHours (config MerchantUser:Invitation:TtlHours,<br/>default 24, ไม่ใช่จาก request body) อยู่ในช่วง 1-168?"}
    TTL -->|no| R400TTL["400 Invitation TTL must be between 1 and 168 hours"]
    TTL -->|yes| TX1["pol_admin txn: FindPendingByNormalizedEmailAsync(email)"]
    TX1 --> OLD{"มี invitation pending เดิมของ email นี้?"}
    OLD -->|yes| REVOKE_OLD["old.Revoke(now), audit InviteRevoke<br/>(ไม่ 409 - ดู Notes)"]
    OLD -->|no| CREATE_INV
    REVOKE_OLD --> CREATE_INV["MerchantUserInvitation.Create (token hash SHA-256, expiresAt)<br/>audit InviteCreate + outbox MerchantUserInvitationDeliveryRequested"]
    CREATE_INV --> R201["201 Location .../invitations/{id}<br/>body invitationId, maskedEmail, expiresAt, status pending (ไม่คืน raw token)"]
    WHICH -->|"DELETE /invitations/{id}"| FIND2{"FindByIdAsync(invitationId) พบ?"}
    FIND2 -->|no| R404INV["404 Invitation not found"]
    FIND2 -->|yes| ACCEPTED{"invitation.AcceptedAt is not null?"}
    ACCEPTED -->|yes| R409ACC["409 An accepted invitation cannot be revoked"]
    ACCEPTED -->|no| REVOKE2["invitation.Revoke(now), audit InviteRevoke, SaveChanges"]
    REVOKE2 --> R204["204 No Content"]
    R201 --> END_S((◉))
    R204 --> END_S
    R400TTL --> END_F((◉))
    R404INV --> END_F
    R409ACC --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class REVOKE_OLD,CREATE_INV,R201,REVOKE2,R204,END_S ok
    class R400TTL,R404INV,R409ACC,END_F fail
    class WHICH,TTL,OLD,FIND2,ACCEPTED gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| POST | `/api/v1/merchants/users/invitations` | diagram — invitation pending เดิม (ถ้ามี) ถูก revoke เงียบ ๆ แล้วสร้างใหม่แทน 409, audit `InviteRevoke`+`InviteCreate` |
| DELETE | `/api/v1/merchants/users/invitations/{invitationId:guid}` | diagram — 404 เมื่อไม่พบ, 409 เมื่อ invitation ถูก accept ไปแล้ว (`FindByIdAsync` ไม่กรอง `AcceptedAt` จึงเจอ invitation ที่ accept แล้วได้จริง) |

---

## 9.7 แก้ไขโปรไฟล์ + lifecycle ผู้ใช้ร้านค้า

profile update และ lifecycle action (approve/reject/suspend/reactivate) ใช้ permission `users.manage` เดียวกันแต่ domain rule ต่างกันจริงต่อ action: self-check บน reject/suspend, auto-assign role `merchant_staff` บน approve, last-manager guard บน suspend, revoke session บน reject/suspend เท่านั้น (source: `src/Api/Api/Program.cs:2955-3004`, `src/Application/Modules/Merchants.Application/Users/ManageMerchantUsers.cs:306-422`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy merchant-user + permission users.manage ดู § 0.1<br/>CSRF mch_csrf ดู § 0.3"]
    AUTHZ --> METHOD{"endpoint?"}
    METHOD -->|"PUT /api/v1/merchants/users/{merchantUserId:guid}"| TXP["txn: FindByIdAsync(id)"]
    TXP --> FOUNDP{"พบ user?"}
    FOUNDP -->|no| R404P["404 Merchant user not found"]
    FOUNDP -->|yes| VERP{"ExpectedVersion ส่งมา<br/>(ปัจจุบัน wire request ไม่มี field นี้ - ดู Notes)?"}
    VERP -->|"มี และไม่ตรง"| R409P["409 (EnsureVersion, ไม่ reachable จาก wire ปัจจุบัน)"]
    VERP -->|"ไม่มี / ตรง"| UPDATE["UpdateProfile(firstName,lastName,producerCode,licenseNumber,phone)<br/>audit Update, SaveChanges"]
    UPDATE --> R204U["204 No Content"]
    METHOD -->|"POST /api/v1/merchants/users/{merchantUserId:guid}/approve, /reject, /suspend, /reactivate"| LOCK{"action เป็น Approve/Suspend/Reactivate?<br/>AcquirePaymentAuthorizationExclusiveAsync(merchantId)"}
    LOCK --> TXA["txn: FindByIdAsync(id)"]
    TXA --> FOUNDA{"พบ user?"}
    FOUNDA -->|no| R404A["404 Merchant user not found"]
    FOUNDA -->|yes| SELF{"id = actor เอง และ action เป็น Reject/Suspend?"}
    SELF -->|yes| R409SELF["409 You cannot reject or suspend yourself"]
    SELF -->|no| DOMAIN{"กติกาต่อ action (ตารางด้านล่าง)?"}
    DOMAIN -->|"409"| R409D["409 ตามตาราง (state ผิด / last manager / role หาย)"]
    DOMAIN -->|ok| MUT["mutate lifecycle + audit ต่อ action, SaveChanges"]
    MUT --> R204A2["204 No Content"]
    R204U --> END_S((◉))
    R204A2 --> END_S
    R404P --> END_F((◉))
    R409P --> END_F
    R404A --> END_F
    R409SELF --> END_F
    R409D --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class UPDATE,R204U,MUT,R204A2,END_S ok
    class R404P,R409P,R404A,R409SELF,R409D,END_F fail
    class FOUNDP,VERP,LOCK,FOUNDA,SELF,DOMAIN gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| PUT | `/api/v1/merchants/users/{merchantUserId:guid}` | diagram — ไม่มี lock, ไม่มี self-check, 409 ประกาศไว้แต่ไม่ reachable จริง (wire request ไม่มี version field), audit `Update` |
| POST | `/api/v1/merchants/users/{merchantUserId:guid}/approve` | diagram — ต้อง `Status=PendingApproval` (ไม่งั้น 409 domain exception), assign role `merchant_staff` อัตโนมัติ (409 ถ้า role หายจากระบบ), ไม่ self-check, ไม่ revoke session |
| POST | `/api/v1/merchants/users/{merchantUserId:guid}/reject` | diagram — self-check (409), `Reject()` ต้องมีสถานะเดิมเป็น `PendingApproval` มิฉะนั้น 409, `RevokeAllForUserAsync` ทุกเครื่อง, ไม่ lock |
| POST | `/api/v1/merchants/users/{merchantUserId:guid}/suspend` | diagram — lock, self-check (409), `EnsureNotLastManagerAsync` (409 เมื่อเป็น manager Active คนสุดท้าย), `RevokeAllForUserAsync` |
| POST | `/api/v1/merchants/users/{merchantUserId:guid}/reactivate` | diagram — lock, ไม่ self-check, `Reactivate()` ต้องมีสถานะเดิมเป็น `Suspended` มิฉะนั้น 409, ไม่ revoke session |

---

## 9.8 Role RBAC ฝั่งผู้ใช้ร้านค้า

role CRUD ใช้ handler ร่วมกับ admin console ผ่าน `RoleSideContext.Merchant` (seed anchor คือ `merchant_manager` แทน `platform_admin`) set-user-roles เป็นคนละ handler ที่ผูก role ให้ target user ภายในร้านเดียวกัน (source: `src/Api/Api/Program.cs:3066-3139`, `src/Application/Modules/Iam.Application/Roles/CreateRole.cs`, `UpdateRole.cs`, `DeleteRole.cs`, `src/Application/Modules/Merchants.Application/Users/SetUserRoles.cs:24-103`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy merchant-user + permission roles.manage (role CRUD)<br/>หรือ users.roles (set-roles) ดู § 0.1<br/>CSRF mch_csrf ดู § 0.3"]
    AUTHZ --> METHOD{"endpoint?"}
    METHOD -->|"POST /roles"| STATUS{"ParseMerchantUserRoleStatus(body.status):<br/>active หรือ inactive (ไม่ null/blank)?"}
    METHOD -->|"PUT /roles/{code}"| STATUS
    METHOD -->|"DELETE /roles/{code}"| FIND3
    METHOD -->|"PUT /api/v1/merchants/users/{merchantUserId:guid}/roles"| SETR["txn: FindByIdAsync(target)"]
    STATUS -->|no| R400S["400 Invalid role status (ArgumentException)"]
    STATUS -->|"yes, POST"| DUP{"CodeExistsAsync(RoleSideContext.Merchant, code)<br/>(รวม shared bucket)?"}
    DUP -->|yes| R409C["409 A role with code already exists"]
    DUP -->|no| CREATE{"ทุก permission key อยู่ใน catalog ฝั่ง Merchant?"}
    CREATE -->|no| R400PK["400 (ArgumentException)"]
    CREATE -->|yes| SAVE_C["Role.Create + audit RoleCreated, SaveChanges"]
    SAVE_C --> R201["201 Location .../roles/{code}<br/>RoleResponse (ไม่มี ETag)"]
    STATUS -->|"yes, PUT"| FIND3["GetByCodeAsync(RoleSideContext.Merchant, code)"]
    FIND3 --> FOUND3{"พบใน visible set (ของร้าน + shared seed)?"}
    FOUND3 -->|no| R404R["404 Role was not found"]
    FOUND3 -->|yes| OWN{"role.MerchantId = ร้านตัวเอง?<br/>(shared seed มองเห็นได้แต่ไม่ใช่เจ้าของ)"}
    OWN -->|no| R409OWN["409 Role cannot be modified/deleted by this merchant"]
    OWN -->|yes| METHOD2{"method?"}
    METHOD2 -->|PUT| ANCHOR{"role.IsSeedAnchor (merchant_manager)<br/>และ status=inactive?"}
    ANCHOR -->|yes| R409A["409 role cannot be deactivated"]
    ANCHOR -->|no| PERMOK{"permission keys อยู่ใน catalog ฝั่ง Merchant?"}
    PERMOK -->|no| R400PK
    PERMOK -->|yes| SAVE_U["InvalidateAssignedAccountsAsync (lock ก่อน)<br/>Rename/SetDescription/SetColor/SetPermissions/Activate-Deactivate<br/>audit RoleUpdated, SaveChanges"]
    SAVE_U --> R200["200 RoleResponse (ไม่มี ETag)"]
    METHOD2 -->|DELETE| BOUND{"role.IsSeedAnchor หรือ CountAsync(role)>0?"}
    BOUND -->|"seed anchor"| R409SEED["409 role cannot be deleted"]
    BOUND -->|"มีผู้ใช้ผูก"| R409B["409 A role with bound users cannot be deleted"]
    BOUND -->|no| SAVE_D["Remove role (cascade) + audit RoleDeleted, SaveChanges"]
    SAVE_D --> R204R["204 No Content"]
    SETR --> FOUNDT{"พบ target Active ในร้านเดียวกับ actor?"}
    FOUNDT -->|no| R404T["404 The merchant user was not found in your merchant"]
    FOUNDT -->|yes| CODES{"role codes ทั้งหมดรู้จัก (GetRoleIdsByCodesAsync)?"}
    CODES -->|no| R400UK["400 Unknown role codes"]
    CODES -->|yes| MGRCHK{"ถอด merchant_manager ออกจาก manager Active คนสุดท้าย?"}
    MGRCHK -->|yes| R409MGR["409 last active merchant manager cannot be downgraded"]
    MGRCHK -->|no| SETSAVE["add/remove RoleAssignment ตามชุดใหม่<br/>BumpVersion, audit SetRoles, SaveChanges"]
    SETSAVE --> R204T2["204 No Content"]
    R201 --> END_S((◉))
    R200 --> END_S
    R204R --> END_S
    R204T2 --> END_S
    R400S --> END_F((◉))
    R409C --> END_F
    R400PK --> END_F
    R404R --> END_F
    R409OWN --> END_F
    R409A --> END_F
    R409SEED --> END_F
    R409B --> END_F
    R404T --> END_F
    R400UK --> END_F
    R409MGR --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class SAVE_C,R201,SAVE_U,R200,SAVE_D,R204R,SETSAVE,R204T2,END_S ok
    class R400S,R409C,R400PK,R404R,R409OWN,R409A,R409SEED,R409B,R404T,R400UK,R409MGR,END_F fail
    class STATUS,DUP,CREATE,FOUND3,OWN,METHOD2,ANCHOR,PERMOK,BOUND,FOUNDT,CODES,MGRCHK gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| POST | `/api/v1/merchants/users/roles` | diagram — 409 code ซ้ำ (รวม shared bucket), 400 permission key นอก catalog, ไม่มี ETag |
| PUT | `/api/v1/merchants/users/roles/{code}` | diagram — 404 นอก visible set, 409 ไม่ใช่เจ้าของ (shared seed) / seed anchor deactivate, lock Account ก่อนแก้, ไม่มี If-Match/ETag (ต่างจาก § 2.4 Platform role ที่มี If-Match/ETag) |
| DELETE | `/api/v1/merchants/users/roles/{code}` | diagram — 409 seed anchor (`merchant_manager`) หรือมีผู้ใช้ผูก, ไม่มี If-Match |
| PUT | `/api/v1/merchants/users/{merchantUserId:guid}/roles` | diagram — target ต้อง Active + merchant เดียวกัน (404 no leak), 400 unknown code, 409 ลด role ของ manager คนสุดท้าย |

---

## Deviations

| fullPath | เอกสารบอก | source บอก | อ้างอิง |
| --- | --- | --- | --- |
| `/api/v1/merchants` (POST) | policy `admin` (Bearer, ไม่มี CSRF) | `RequirePlatformUserTier(Tier.Super)` ที่ boundary แล้ว re-verify ใน txn ผ่าน `ProvisioningCoordinator.VerifyCallerIsActiveSuper` (`acct.Accounts` Status/AuthorizationVersion + `access.PlatformAccess` active) | `src/Api/Api/Program.cs:2370-2411`, `ProvisioningCoordinator.cs:223-249` |

## Notes

- `POST /api/v1/merchants/auth/logout` มี comment ในโค้ดว่า "idempotent -> 204 เสมอแม้ไม่มี cookie" แต่กลุ่ม `/merchants/auth` ผูก `BoundFilter` ไว้ (`Program.cs:2765-2767`) ซึ่งอาศัย default `ConsoleSession` scheme (`builder.Services.AddAuthentication(ConsoleSessionAuthentication.SchemeName)`, `Program.cs:351`) เลือก `MerchantUserSession` ให้เสมอเมื่อ endpoint ไม่มี policy — caller ที่ไม่มี cookie หรือ cookie invalid/expired จะได้ 403 จาก `BoundFilter` ก่อนถึง handler เสมอ ทำให้ path "ไม่มี cookie -> clear เงียบ ๆ" ในตัว handler เป็น dead code จริงในทางปฏิบัติ
- endpoint policy `merchant-user` เดี่ยว (`me`/`permissions`/`roles`/`edit`) ผ่าน `RequireAuthorization("merchant-user")` ที่ authenticate สำเร็จเฉพาะ user Active+bound อยู่แล้ว (`UserSessionAuthenticationHandler` เซ็ต `IUserScope` เฉพาะ path สำเร็จ) เช็ค `IsBound` ซ้ำใน handler/`BoundFilter` จึงเป็น defense-in-depth ไม่ใช่ branch ที่ caller ปกติไปถึงได้จริง — ต่างจาก logout ข้างต้นที่ `BoundFilter` ทำงานจริงเพราะ endpoint ไม่มี policy เลย
- callback `/api/v1/merchants/auth/microsoft/callback` เป็น path จาก `MerchantAuth:Providers:Microsoft:CallbackPath` (`appsettings.json:48`, ค่าเริ่มต้นตรงกับ inventory) — ไม่มี `MapGet`, handler จริงคือ event ของ OpenIdConnect scheme `MerchantUserMicrosoft`
- callback มี branch `Properties.Items["merchant_invitation_id"]` (`UserOidcAuthentication.cs:128`) แต่ไม่มี endpoint ใดใน source (รวม `GET /{provider}/login` ที่ตั้งแค่ `RedirectUri`) เซ็ตค่านี้ตอน Challenge จึง unreachable จาก endpoint จริงในปัจจุบัน — เก็บไว้เป็นโค้ดสำรองสำหรับ invitation-accept flow ที่ยังไม่ได้ wire
- `PUT /api/v1/merchants/users/{merchantUserId:guid}` ประกาศ `ProducesProblem(409)` แต่ `UpdateMerchantUserRequest` (wire DTO) ไม่มี version field เลย ทำให้ `ExpectedVersion` เป็น null เสมอและ branch 409 (`EnsureVersion`) ไม่ reachable จาก request จริง
- `POST /api/v1/merchants/users/invitations` เขียนใน `.WithDescription` ของตัวเองว่าอีเมลที่มี invitation ใช้ได้อยู่แล้ว -> 409 แต่ `CreateInvitationHandler` (merchant audience) revoke ใบเดิมเงียบ ๆ แล้วสร้างใบใหม่แทนเสมอ ไม่เคยโยน 409 กรณีนี้ (409 เกิดเฉพาะฝั่ง Admin audience ที่มี Idempotency-Key ซ้ำกับ intent ต่างกัน ซึ่งไม่ใช่ path นี้)
- `POST /api/v1/merchants` ที่ txn ตรวจพบว่า caller ไม่ใช่ Super/Active/AuthorizationVersion ตรงแล้วอีกต่อไป โยน `WriteGuardException` ซึ่งไม่มี case เฉพาะใน `ProblemDetailsExceptionHandler.Map` จึงตกไปที่ default 500 ไม่ใช่ 403 ตามที่อาจคาดไว้
- คอลัมน์ caller/policy ของเอกสารไม่ระบุ header marker (`EtagResponseMarker`, `SfsQueryParamsMarker`) หรือ endpoint filter (`BoundFilter`) ทุก § วาดตาม source และอ้าง § 0.1/0.5/0.6 แทน
- error body ทุก 4xx/5xx เป็น ProblemDetails ตาม § 0.9: `ArgumentException` 400, `NotFoundException` 404, `ConflictException`/`InvalidOperationException` 409, `WriteGuardException`/unhandled 500 — callback ทุกทางออกเป็น 302 redirect ไม่ใช่ JSON

**Render**: GitHub / Obsidian / VS Code Mermaid
