# pol-core API — Agent registration (ผู้สมัครและผู้ตรวจ) (Activity Diagrams)

> Source: `docs/reference/api-endpoints.md` section "Identity และ OAuth" บรรทัด L43–L51 และ source ที่อ้างต่อ § (`src/Api/Api/Accounts/AgentRegistrationEndpoints.cs`, `src/Application/Modules/Accounts.Application/Registration.cs`, `src/Infrastructure/Persistence/Persistence.ControlPlane/IdentityAccess/AgentRegistrationStore.cs`, `src/Domain/Modules/Accounts.Domain/RegistrationModels.cs`)
> Scope: 9 endpoints — ฝั่งผู้สมัคร 4 ตัวใต้ `/api/v1/agent-registration` (cookie `pol_registration_session`, AllowAnonymous) และฝั่งผู้ตรวจ 5 ตัวใต้ `/api/v1/agent-registrations` (policy `admin` + RequireCsrf ระดับ group)
> Generated: 2026-09-14

| § | Diagram | Endpoints |
| --- | --- | --- |
| 3.1 | ผู้สมัครอ่าน case และประวัติ attempts | `GET /api/v1/agent-registration`, `GET /api/v1/agent-registration/history` |
| 3.2 | ผู้สมัครบันทึก draft (upsert, If-Match optional) | `PUT /api/v1/agent-registration` |
| 3.3 | ผู้สมัครส่ง draft เป็น attempt (idempotent, 201) | `POST /api/v1/agent-registration/submissions` |
| 3.4 | ผู้ตรวจดูรายการ case ใน merchant scope | `GET /api/v1/agent-registrations` |
| 3.5 | ผู้ตรวจอ่าน case และ attempts ของ case | `GET /api/v1/agent-registrations/{registrationId:guid}`, `GET /api/v1/agent-registrations/{registrationId:guid}/attempts` |
| 3.6 | ผู้ตรวจตัดสิน attempt (approve / reject) | `POST /api/v1/agent-registrations/{registrationId:guid}/attempts/{attemptId:guid}/approve`, `POST /api/v1/agent-registrations/{registrationId:guid}/attempts/{attemptId:guid}/reject` |

---

## 3.1 ผู้สมัครอ่าน case และประวัติ attempts

endpoint ฝั่งผู้สมัครไม่มี policy แต่ handler resolve registration session จาก cookie เอง แล้วอ่าน case ที่ผูก identity + merchant ของ session และตั้ง ETag จาก registration.Version (source: `src/Api/Api/Accounts/AgentRegistrationEndpoints.cs:22-23,30-31,56-69,99-116,179-197`, `src/Application/Modules/Accounts.Application/Registration.cs:82-96`, `AgentRegistrationStore.cs:26-38,205-208`)

```mermaid
flowchart TD
    START((●)) --> ANON["metadata AllowAnonymous<br/>ไม่มี policy, CSRF, rate limit"]
    ANON --> SESSION{"cookie pol_registration_session:<br/>SHA-256 พบใน acct.RegistrationSessions<br/>Status Active และ now ก่อน ExpiresAt?"}
    SESSION -->|no| R401["401 bare Results.Unauthorized<br/>UseStatusCodePages แปลงเป็น ProblemDetails ไม่มี code ดู § 0.9"]
    SESSION -->|yes| FIND["FindCaseAsync: acct.AgentRegistrations<br/>ตรง Provider + TenantId + ExternalUserId + MerchantId ของ session"]
    FIND --> FOUND{"พบ registration?"}
    FOUND -->|no| R404["404 bare Results.NotFound<br/>ProblemDetails ไม่มี code"]
    FOUND -->|yes| ATT["ListAttemptsAsync: acct.AgentRegistrationAttempts<br/>ORDER BY AttemptNo"]
    ATT --> ETAG["header ETag = vN (registration.Version)"]
    ETAG --> SHAPE{"route?"}
    SHAPE -->|"GET /agent-registration"| R200_C["200 registration (+ rejectionReason ของ current attempt),<br/>nextAction submit / wait / login, currentAttempt"]
    SHAPE -->|"GET /agent-registration/history"| R200_H["200 registration + attempts ทั้งหมด<br/>(applicant view ไม่มี PII และ InternalReviewNote)"]
    R200_C --> END_S((◉))
    R200_H --> END_S
    R401 --> END_F((◉))
    R404 --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class ETAG,R200_C,R200_H,END_S ok
    class R401,R404,END_F fail
    class ANON,SESSION,FOUND,SHAPE gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/agent-registration` | subject: payload `registration` (rejectionReason จาก attempt ปัจจุบัน) + `nextAction` (Draft / Rejected = submit, Pending = wait, Approved = login) + `currentAttempt` (null เมื่อยังไม่เคย submit) |
| GET | `/api/v1/agent-registration/history` | payload `registration` (ไม่มี rejectionReason) + `attempts[]` ทุก attempt เรียงตาม AttemptNo ในรูป applicant view, ไม่มี nextAction, gate / 401 / 404 / ETag เหมือน subject |

---

## 3.2 ผู้สมัครบันทึก draft (upsert, If-Match optional)

PUT สร้างหรือแก้ draft ใน transaction ที่ล็อกต่อ identity ด้วย sp_getapplock, If-Match ส่งหรือไม่ก็ได้ (parse เฉพาะเมื่อมี header) และแก้ไม่ได้เมื่อ case Pending / Approved (source: `AgentRegistrationEndpoints.cs:24-26,71-81,183-184`, `Registration.cs:98-103,158-174`, `AgentRegistrationStore.cs:40-69,256-266`, `RegistrationModels.cs:59-76`, `Persistence.ControlPlane/Governance/GovernanceSqlLockManager.cs:11-24`)

```mermaid
flowchart TD
    START((●)) --> ANON["metadata AllowAnonymous + EtagResponseMarker 200<br/>ไม่มี policy, CSRF, rate limit"]
    ANON --> SESSION{"registration session live? ดู § 3.1"}
    SESSION -->|no| R401["401 ProblemDetails ไม่มี code ดู § 0.9"]
    SESSION -->|yes| IFM{"มี header If-Match?"}
    IFM -->|no| VALID{"ValidateDraft: SaleCode ไม่ว่าง ไม่เกิน 64,<br/>Email รูป MailAddress ไม่เกิน 320, PhoneNumber ไม่ว่าง ไม่เกิน 64,<br/>Profile เป็น JSON object ไม่เกิน 32768 ตัวอักษร?"}
    IFM -->|yes| IFM_OK{"รูป quoted vN (VersionEtags.Require)?"}
    IFM_OK -->|no| R400_E["400 ProblemDetails code invalid_etag ดู § 0.5"]
    IFM_OK -->|yes| VALID
    VALID -->|no| R400_V["400 ProblemDetails code validation_failed"]
    VALID -->|yes| TXN["transaction (admin unit of work)<br/>sp_getapplock agent-registration:hash(identity) timeout 15s"]
    TXN --> LOOKUP["SELECT acct.AgentRegistrations<br/>ตรง Provider + TenantId + ExternalUserId (ไม่กรอง MerchantId)"]
    LOOKUP --> EXISTS{"มี registration เดิม?"}
    EXISTS -->|no| CREATE["AgentRegistration.Create<br/>MerchantId ของ session, Status Draft, Version 1"]
    EXISTS -->|yes| MCH{"registration.MerchantId ตรง session.MerchantId?"}
    MCH -->|no| R409_M["409 ProblemDetails code registration_merchant_mismatch"]
    MCH -->|yes| VER{"ส่ง If-Match มาและ Version ไม่ตรง?"}
    VER -->|yes| R409_V["409 ProblemDetails code state_conflict<br/>(ConcurrencyConflictException)"]
    VER -->|no| STATE{"Status เป็น Pending หรือ Approved?"}
    STATE -->|yes| R409_S["409 ProblemDetails InvalidOperationException<br/>ไม่มี code (แก้ draft ไม่ได้)"]
    STATE -->|no| UPDATE["UpdateDraft: แทนค่าทั้ง 4 field<br/>Status Draft (Rejected กลับเป็น Draft), Version +1"]
    CREATE --> SAVE["SaveChanges + commit (ไม่มี outbox)"]
    UPDATE --> SAVE
    SAVE --> R200["200 registration + nextAction submit, currentAttempt null<br/>header ETag = vN ใหม่"]
    R200 --> END_S((◉))
    R401 --> END_F((◉))
    R400_E --> END_F
    R400_V --> END_F
    R409_M --> END_F
    R409_V --> END_F
    R409_S --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class CREATE,UPDATE,SAVE,R200,END_S ok
    class R401,R400_E,R400_V,R409_M,R409_V,R409_S,END_F fail
    class ANON,SESSION,IFM,IFM_OK,VALID,EXISTS,MCH,VER,STATE gate
```

---

## 3.3 ผู้สมัครส่ง draft เป็น attempt (idempotent, 201)

POST บังคับทั้ง Idempotency-Key และ If-Match (ตรวจ key ก่อน), สร้าง attempt ที่ snapshot sale / branch version ปัจจุบันจาก merch.* แล้วเปลี่ยน case เป็น Pending ใน transaction เดียว โดย key ซ้ำที่ intent เดิมตอบ 201 เดิมแบบ replay (source: `AgentRegistrationEndpoints.cs:27-29,83-97`, `Registration.cs:105-111`, `AgentRegistrationStore.cs:71-114,215-235,276-279`, `RegistrationModels.cs:78-91,178-187`, `src/Api/Api/ConcurrencyEtags.cs:18-41`)

```mermaid
flowchart TD
    START((●)) --> ANON["metadata AllowAnonymous + IfMatchMutationMarker 201 + IdempotencyMutationMarker<br/>ไม่มี policy, CSRF, rate limit"]
    ANON --> SESSION{"registration session live? ดู § 3.1"}
    SESSION -->|no| R401["401 ProblemDetails ไม่มี code ดู § 0.9"]
    SESSION -->|yes| IDEM{"Idempotency-Key ไม่ว่าง ไม่เกิน 200<br/>ไม่มี control char?"}
    IDEM -->|no| R400_I["400 ProblemDetails code invalid_idempotency_key ดู § 0.5"]
    IDEM -->|yes| IFM{"If-Match รูป quoted vN?"}
    IFM -->|no| R400_E["400 ProblemDetails code invalid_etag"]
    IFM -->|yes| TXN["transaction + sp_getapplock agent-registration:hash(identity)"]
    TXN --> LOOKUP{"acct.AgentRegistrations ตรง identity<br/>(ไม่กรอง MerchantId) พบ?"}
    LOOKUP -->|no| R404["404 ProblemDetails Registration draft was not found"]
    LOOKUP -->|yes| MCH{"registration.MerchantId ตรง session?"}
    MCH -->|no| R409_M["409 ProblemDetails code registration_merchant_mismatch"]
    MCH -->|yes| VER{"registration.Version ตรง If-Match?"}
    VER -->|no| R409_V["409 ProblemDetails code state_conflict<br/>(ConcurrencyConflictException)"]
    VER -->|yes| PRIOR{"มี attempt ของ registration นี้<br/>ที่ IdempotencyKey เดียวกัน?"}
    PRIOR -->|yes| HASH{"IntentHash = SHA-256(SaleCode, Email,<br/>PhoneNumber, ProfileJson) ตรง?"}
    HASH -->|no| R409_K["409 ProblemDetails code idempotency_conflict"]
    HASH -->|yes| REPLAY["201 เดิม replayed = true<br/>ไม่เขียน DB, ETag = version ปัจจุบัน"]
    PRIOR -->|no| STATUS{"registration.Status?"}
    STATUS -->|Pending| R409_P["409 ProblemDetails code registration_pending"]
    STATUS -->|Approved| R409_A["409 ProblemDetails code account_already_approved"]
    STATUS -->|"Draft / Rejected"| SALE{"merch.Sales JOIN merch.Branches<br/>Code = SaleCode, MerchantId เดียวกัน, Status = 1 ทั้งคู่ พบ?"}
    SALE -->|no| R409_S["409 ProblemDetails code registration_sale_invalid"]
    SALE -->|yes| ATTEMPT["AgentRegistrationAttempt.Create: AttemptNo +1, Status Pending,<br/>snapshot SaleId / BranchId / SaleVersion / BranchVersion<br/>+ form + IdempotencyKey + IntentHash"]
    ATTEMPT --> START_ATT["registration.StartAttempt: CurrentAttemptId / No,<br/>Status Pending, Version +1"]
    START_ATT --> SAVE["SaveChanges + commit (ไม่มี outbox)"]
    SAVE --> R201["201 Created Location /api/v1/agent-registration/history/{attemptId}<br/>body registration + attempt + replayed = false<br/>header ETag = vN ใหม่"]
    R201 --> END_S((◉))
    REPLAY --> END_S
    R401 --> END_F((◉))
    R400_I --> END_F
    R400_E --> END_F
    R404 --> END_F
    R409_M --> END_F
    R409_V --> END_F
    R409_K --> END_F
    R409_P --> END_F
    R409_A --> END_F
    R409_S --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    class ATTEMPT,START_ATT,SAVE,R201,END_S ok
    class R401,R400_I,R400_E,R404,R409_M,R409_V,R409_K,R409_P,R409_A,R409_S,END_F fail
    class ANON,SESSION,IDEM,IFM,LOOKUP,MCH,VER,PRIOR,HASH,STATUS,SALE gate
    class REPLAY warn
```

---

## 3.4 ผู้ตรวจดูรายการ case ใน merchant scope

GET list ไม่ใช้ SFS: เลือก merchant จาก query `merchantId` หรือจาก Accessible ของ admin (auto-select เฉพาะเมื่อมี merchant เดียว) แล้วอ่านทุก case ของ merchant นั้นเรียงตาม UpdatedAt (source: `AgentRegistrationEndpoints.cs:33-39,118-131`, `AgentRegistrationStore.cs:210-213`, `src/Application/Modules/Admins.Application/Users/AccessibleMerchants.cs:8-18`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy admin + permission merchants.users.view ดู § 0.1<br/>RequireCsrf ระดับ group ข้าม GET ดู § 0.3"]
    AUTHZ --> Q{"query merchantId ส่งมา?"}
    Q -->|yes| SEL["selected = merchantId"]
    Q -->|no| UNR{"scope.Accessible.IsUnrestricted?"}
    UNR -->|yes| R400["400 ValidationProblem errors.merchantId<br/>merchantId is required when more than one Merchant is accessible"]
    UNR -->|no| CNT{"จำนวน Accessible.Merchants?"}
    CNT -->|"1"| SEL1["selected = merchant เดียวนั้น (SingleOrDefault)"]
    CNT -->|"0"| R400
    CNT -->|"2 ขึ้นไป"| R409["409 ProblemDetails InvalidOperationException<br/>(SingleOrDefault บน set หลายตัว) ดู § 0.9"]
    SEL --> ALLOW{"scope.Accessible.Allows(selected)?"}
    SEL1 --> ALLOW
    ALLOW -->|no| R404["404 bare Results.NotFound"]
    ALLOW -->|yes| LIST["ListCasesAsync: acct.AgentRegistrations WHERE MerchantId<br/>ORDER BY UpdatedAt DESC (ไม่มี paging / SFS)"]
    LIST --> R200["200 array RegistrationCaseView (ไม่มี ETag)"]
    R200 --> END_S((◉))
    R400 --> END_F((◉))
    R409 --> END_F
    R404 --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class LIST,R200,END_S ok
    class R400,R409,R404,END_F fail
    class AUTHZ,Q,UNR,CNT,ALLOW gate
```

---

## 3.5 ผู้ตรวจอ่าน case และ attempts ของ case

GET detail ตรวจ scope ด้วย MerchantId ของ case ที่พบ (ไม่พบหรือนอก scope ตอบ 404 เหมือนกัน) และไม่ตั้ง header ETag แม้ approve / reject จะต้องใช้ version (source: `AgentRegistrationEndpoints.cs:40-45,133-150`, `Registration.cs:145-156`, `AgentRegistrationStore.cs:37-38,205-208`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy admin + permission merchants.users.view ดู § 0.1<br/>RequireCsrf ระดับ group ข้าม GET ดู § 0.3"]
    AUTHZ --> FIND["FindCaseByIdAsync: acct.AgentRegistrations WHERE Id = registrationId"]
    FIND --> OK{"พบ และ scope.Accessible.Allows(registration.MerchantId)?"}
    OK -->|no| R404["404 bare Results.NotFound<br/>(ไม่แยกเหตุ ไม่พบ กับ นอก scope)"]
    OK -->|yes| SHAPE{"route?"}
    SHAPE -->|"GET /{registrationId:guid}"| R200_C["200 RegistrationCaseView<br/>(version อยู่ใน body, ไม่ตั้ง header ETag)"]
    SHAPE -->|"GET /{registrationId:guid}/attempts"| ATT["ListAttemptsAsync ORDER BY AttemptNo"]
    ATT --> R200_A["200 array reviewer attempt view<br/>มี SaleCode, Email, PhoneNumber, ProfileJson, InternalReviewNote"]
    R200_C --> END_S((◉))
    R200_A --> END_S
    R404 --> END_F((◉))

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class ATT,R200_C,R200_A,END_S ok
    class R404,END_F fail
    class AUTHZ,OK,SHAPE gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/agent-registrations/{registrationId:guid}` | subject: คืน `RegistrationCaseView` ตัวเดียว (`version` ใน body ใช้สร้าง If-Match ของ § 3.6) |
| GET | `/api/v1/agent-registrations/{registrationId:guid}/attempts` | อ่าน attempts เพิ่มหลัง scope check, คืน array reviewer view ที่มี PII และ InternalReviewNote, gate / 404 เหมือน subject |

---

## 3.6 ผู้ตรวจตัดสิน attempt (approve / reject)

approve และ reject เป็น mutate ตรง (ไม่ใช่ maker-checker): endpoint ตรวจ scope ก่อน parse header, store ล็อกต่อ registration, replay จาก DecisionIdempotencyKey ก่อนตรวจ version, approve เพิ่มการตรวจ sale / identity / role แล้วสร้าง account ชุดเต็ม และทั้งคู่เขียน GovernanceOutbox ให้ notification (source: `AgentRegistrationEndpoints.cs:46-53,152-177`, `Registration.cs:113-137,176-180`, `AgentRegistrationStore.cs:116-203,245-254,268-283`, `RegistrationModels.cs:93-102,189-237`, `src/Application/Contracts/AgentRegistrationDecidedV1.cs:23-37`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy admin + RequireCsrf (X-CSRF-Token = adm_csrf / pol_csrf) ดู § 0.1 / § 0.3<br/>permission merchants.users.approve (approve) หรือ merchants.users.reject (reject)<br/>IfMatchMutationMarker 200 + IdempotencyMutationMarker ดู § 0.5"]
    AUTHZ --> FIND{"FindCaseByIdAsync พบ และ<br/>scope.Accessible.Allows(registration.MerchantId)?"}
    FIND -->|no| R404["404 bare Results.NotFound"]
    FIND -->|yes| IDEM{"Idempotency-Key ไม่ว่าง ไม่เกิน 200<br/>ไม่มี control char?"}
    IDEM -->|no| R400_I["400 ProblemDetails code invalid_idempotency_key"]
    IDEM -->|yes| IFM{"If-Match รูป quoted vN?"}
    IFM -->|no| R400_E["400 ProblemDetails code invalid_etag"]
    IFM -->|yes| SVC{"service: scope.Current.AdminId ไม่ว่าง<br/>และ body field บังคับไม่ว่าง?"}
    SVC -->|"AdminId ว่าง"| R403["403 ProblemDetails Reviewer identity is required<br/>code permission_denied"]
    SVC -->|"field ว่าง"| R400_B["400 ProblemDetails code contact_evidence_required (approve)<br/>หรือ rejection_reason_required (reject)"]
    SVC -->|ok| TXN["transaction + sp_getapplock agent-registration:{registrationId} timeout 15s"]
    TXN --> LOAD{"acct.AgentRegistrations Id และ<br/>acct.AgentRegistrationAttempts Id + RegistrationId พบทั้งคู่?"}
    LOAD -->|no| R404_T["404 ProblemDetails Registration / attempt was not found"]
    LOAD -->|yes| DECIDED{"attempt.DecisionIdempotencyKey มีแล้ว?"}
    DECIDED -->|yes| DHASH{"key และ DecisionIntentHash =<br/>SHA-256(decision, reason หรือ evidence, note) ตรง?"}
    DHASH -->|no| R409_D["409 ProblemDetails code decision_already_recorded"]
    DHASH -->|yes| REPLAY["200 เดิม replayed = true<br/>(ไม่ตรวจ If-Match, ไม่เขียน DB)"]
    DECIDED -->|no| VER{"registration.Version ตรง If-Match?"}
    VER -->|no| R409_V["409 ProblemDetails code state_conflict<br/>(ConcurrencyConflictException)"]
    VER -->|yes| PEND{"registration Pending, CurrentAttemptId = attemptId,<br/>attempt.Status Pending?"}
    PEND -->|no| R409_P["409 ProblemDetails code registration_not_pending"]
    PEND -->|yes| KIND{"approve หรือ reject?"}
    KIND -->|approve| CHK{"merch.Sales / Branches Status 1 และ snapshot Sale / Branch Id + Version ตรง,<br/>ไม่มี acct.Agents ที่ SaleId เดียวกัน, ไม่มี acct.LoginAccounts ที่ identity เดียวกัน,<br/>มี iam.Roles merchant_staff Scope Shared Active?"}
    CHK -->|no| R409_C["409 ProblemDetails code registration_context_changed /<br/>sale_already_bound / identity_already_bound /<br/>registration_role_unavailable"]
    CHK -->|yes| APPROVE["attempt.SetDecisionIdempotency + Approve<br/>(ContactEvidenceReference, DecidedBy, DecidedAt)<br/>registration.ApplyDecision Approved, Version +1"]
    APPROVE --> ACCOUNT["สร้าง acct.Accounts (Agent) + acct.LoginAccounts (identity, email)<br/>+ acct.Agents (SaleId, ProfileJson) + MerchantAccess DataScope.Self<br/>+ access.AccessRoles merchant_staff"]
    KIND -->|reject| REJECT["attempt.SetDecisionIdempotency + Reject<br/>(RejectionReason ไม่เกิน 1000, InternalReviewNote ไม่เกิน 4000)<br/>registration.ApplyDecision Rejected, Version +1"]
    ACCOUNT --> OUTBOX["admin.GovernanceOutboxMessages: AgentRegistrationDecidedV1<br/>(Merchant scope, decision, email, phone, rejectionReason)"]
    REJECT --> OUTBOX
    OUTBOX --> SAVE["SaveChanges + commit"]
    SAVE --> R200["200 registration + attempt (reviewer view) + replayed = false<br/>header ETag = vN ใหม่"]
    SAVE -.async.-> BG["GovernanceOutboxDispatcher ดู § 0.8<br/>AgentRegistrationDecidedV1Handler, NotificationMaterializer<br/>Notifications + Deliveries email / sms template agent-registration-result.v1"]
    R200 --> END_S((◉))
    REPLAY --> END_S
    R404 --> END_F((◉))
    R400_I --> END_F
    R400_E --> END_F
    R403 --> END_F
    R400_B --> END_F
    R404_T --> END_F
    R409_D --> END_F
    R409_V --> END_F
    R409_P --> END_F
    R409_C --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    class APPROVE,ACCOUNT,REJECT,OUTBOX,SAVE,R200,END_S ok
    class R404,R400_I,R400_E,R403,R400_B,R404_T,R409_D,R409_V,R409_P,R409_C,END_F fail
    class AUTHZ,FIND,IDEM,IFM,SVC,LOAD,DECIDED,DHASH,VER,PEND,KIND,CHK gate
    class REPLAY,BG warn
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| POST | `/api/v1/agent-registrations/{registrationId:guid}/attempts/{attemptId:guid}/approve` | permission `merchants.users.approve`, body `contactEvidenceReference` (ว่าง = 400 `contact_evidence_required`, เกิน 256 = 400 ArgumentException ไม่มี code), เดิน branch approve: 4 การตรวจ 409 เพิ่ม + สร้าง Account / LoginAccount / Agent / MerchantAccess / AccessRole |
| POST | `/api/v1/agent-registrations/{registrationId:guid}/attempts/{attemptId:guid}/reject` | permission `merchants.users.reject`, body `rejectionReason` (ว่าง = 400 `rejection_reason_required`, เกิน 1000 = 400) + `internalReviewNote` optional (เกิน 4000 = 400), เดิน branch reject: ไม่มีการตรวจ sale / identity / role, เขียนเฉพาะ attempt + registration + outbox |

---

## Deviations

| fullPath | เอกสารบอก | source บอก | อ้างอิง |
| --- | --- | --- | --- |
| ไม่พบ deviation ระหว่างเอกสารกับ source | - | - | - |

## Notes

| เรื่อง | ข้อเท็จจริงจาก source | source |
| --- | --- | --- |
| ที่มาของ registration session | cookie `pol_registration_session` ออกโดย agent OIDC callback (theme อื่น) HttpOnly, Path `/`, Secure เมื่อ HTTPS, อายุ `IdentityAccess:RegistrationSessionMinutes`, ออกไม่ได้ถ้า identity เป็น workforce หรือมี approved account แล้ว (`account_already_approved`) — อยู่นอก frame ของ theme นี้ | `src/Api/Api/IdentityAccess/IdentityAccessWiring.cs:258-271`, `Accounts.Application/IdentityAccessContracts.cs:97-118` |
| session live | หา session ด้วย SHA-256 ของค่า cookie, ต้อง Status Active และ now ก่อน ExpiresAt, ไม่มี slide / rotate | `Registration.cs:82-89`, `Accounts.Domain/AccountModels.cs:486` |
| CSRF ฝั่งผู้สมัคร | PUT / POST ใต้ `/agent-registration` เป็น AllowAnonymous ไม่มี policy จึงไม่มี CSRF filter และ CsrfParity ไม่ตรวจ (ตรวจเฉพาะ endpoint ใต้ policy ที่รู้จัก) | `AgentRegistrationEndpoints.cs:22-31`, § 0.3 |
| CSRF ฝั่งผู้ตรวจ | `RequireCsrf()` ติดระดับ group `/agent-registrations` จึงมีทุก child รวม GET แต่ filter ข้าม safe method, unsafe (approve / reject) ต้องส่ง `X-CSRF-Token` | `AgentRegistrationEndpoints.cs:33-34`, § 0.3 |
| Location ของ 201 | header ชี้ `/api/v1/agent-registration/history/{attemptId}` ซึ่งไม่มี route map ไว้ (มีเฉพาะ `GET /agent-registration/history`) | `AgentRegistrationEndpoints.cs:30-31,91` |
| ETag ฝั่งผู้ตรวจ | `GET /agent-registrations/{id}` และ `/attempts` ไม่ตั้ง header ETag, client ต้องสร้าง If-Match `"vN"` จาก `version` ใน body ของ RegistrationCaseView | `AgentRegistrationEndpoints.cs:133-150`, `ConcurrencyEtags.cs:16-26` |
| merchantId ใน § 3.4 | ข้อความ validation บอก "more than one Merchant is accessible" แต่ path ที่ Accessible มี 2 merchant ขึ้นไปโดยไม่ส่ง merchantId ได้ 409 จาก `SingleOrDefault` (InvalidOperationException) ไม่ใช่ 400, 400 เกิดเมื่อ unrestricted (Super) หรือ Accessible ว่าง | `AgentRegistrationEndpoints.cs:121-126` |
| การหา case ต่างกันระหว่างอ่านกับเขียน | GET ฝั่งผู้สมัครกรอง identity + MerchantId ของ session (คนละ merchant = 404) ส่วน PUT / POST ค้น identity อย่างเดียวแล้วตอบ 409 `registration_merchant_mismatch` เมื่อ merchant ไม่ตรง | `AgentRegistrationStore.cs:31-35,48-60,79-84` |
| ลำดับ replay กับ version | approve / reject ตรวจ DecisionIdempotencyKey ก่อน EnsureVersion จึง replay 200 ได้แม้ If-Match เก่า, submit ตรวจ EnsureVersion ก่อน replay จึงต้องส่ง version ปัจจุบันเสมอ | `AgentRegistrationStore.cs:85-95,129-135,187-193` |
| applock | `sp_getapplock` Exclusive, owner Transaction, timeout 15s, ผลลบ = InvalidOperationException (409 ผ่าน § 0.9), provider ที่ไม่ใช่ SQL Server (SQLite ใน test) ข้าม lock | `GovernanceSqlLockManager.cs:11-24` |
| ขนาด field ใน domain | evidence เกิน 256, reason เกิน 1000, note เกิน 4000 โยน ArgumentException = 400 Invalid request ไม่มี code (service ตรวจเฉพาะค่าว่าง) | `RegistrationModels.cs:202-237`, § 0.9 |
| state machine ของ case | Draft / Rejected แก้ draft และ submit ได้ (Rejected กลับเป็น Draft เมื่อแก้), Pending รอตัดสิน, Approved จบ, AttemptNo ต้องต่อเนื่อง, nextAction ของ applicant = submit / wait / login | `RegistrationModels.cs:65-102`, `AgentRegistrationEndpoints.cs:186-197` |
| ไม่มี paging | ทุก list ของ theme นี้คืน array เต็ม ไม่ผ่าน SfsQueryParser (§ 0.6 ไม่เกี่ยว) | `AgentRegistrationStore.cs:205-213` |
| ปลายทางของ outbox | GovernanceOutboxDispatcher publish `AgentRegistrationDecidedV1` ให้ `AgentRegistrationDecidedV1Handler` เรียก NotificationMaterializer เขียน Notifications + NotificationInboxMessages (dedupe ด้วย SourceEventId) + Deliveries เฉพาะ channel email / sms (template `agent-registration-result.v1`) แล้ว NotificationDeliveryDispatcher ส่งต่อ (§ 0.8); channel `business_webhook` ใช้กับ event นี้ไม่ได้ เพราะ `AgentRegistrationDecidedV1` ไม่อยู่ใน webhook `SupportedEvents` whitelist ที่ create/update endpoint บังคับ (merchant ตั้ง WebhookEndpoint ให้ event นี้ไม่ได้ตั้งแต่ต้น) และไม่มี template รองรับ combination นี้เลย | `GovernanceOutboxDispatcher.cs:22-30,111-138`, `AgentRegistrationDecidedV1.cs:23-37`, `Persistence.MerchantRuntime/Notifications/NotificationMaterializer.cs:15-79`, `Persistence.ControlPlane/Notifications/DeliveryStore.cs:16-26,102-103,131-134,169-173,438-443`, `Notifications.Application/NotificationTemplates.cs:14-50` |

**Render**: GitHub / Obsidian / VS Code Mermaid
