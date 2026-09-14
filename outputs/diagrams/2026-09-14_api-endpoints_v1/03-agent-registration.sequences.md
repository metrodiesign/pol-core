# pol-core API — Agent registration (ผู้สมัครและผู้ตรวจ) (Sequence Diagrams)

> Source: `docs/reference/api-endpoints.md` section "Identity และ OAuth" บรรทัด L43–L51 และ source ที่อ้างต่อ § (`src/Api/Api/Accounts/AgentRegistrationEndpoints.cs`, `src/Application/Modules/Accounts.Application/Registration.cs`, `src/Infrastructure/Persistence/Persistence.ControlPlane/IdentityAccess/AgentRegistrationStore.cs`, `src/Domain/Modules/Accounts.Domain/RegistrationModels.cs`, `src/Api/Api/ConcurrencyEtags.cs`, `Persistence.ControlPlane/Governance/GovernanceSqlLockManager.cs`, `Persistence.ControlPlane/Governance/GovernanceOutboxDispatcher.cs`, `src/Application/Contracts/AgentRegistrationDecidedV1.cs`)
> Scope: 9 endpoints เดียวกับ `03-agent-registration.activities.md` (หมายเลข § ตรงกัน) แสดงลำดับข้าม actor / handler / DB / async dispatcher
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

session resolve เป็น self-step ของ API (ไม่ผ่าน policy ของแพลตฟอร์ม) แล้ว shape ผลต่างกันตาม route ปลายทาง (source: `AgentRegistrationEndpoints.cs:22-23,30-31,56-69,99-116,179-197`, `Registration.cs:82-96`, `AgentRegistrationStore.cs:26-38,205-208`)

```mermaid
sequenceDiagram
    autonumber
    actor AP as Applicant
    participant SPA as Registration SPA
    participant API as API<br/>AgentRegistrationEndpoints
    participant SVC as AgentRegistrationService<br/>+ AgentRegistrationStore
    participant DB as SQL Server<br/>acct.*

    Note over SPA,DB: Phase A — resolve registration session
    AP->>SPA: เปิดหน้าสถานะการสมัคร
    SPA->>API: GET /api/v1/agent-registration หรือ /agent-registration/history + cookie pol_registration_session (ไม่มี policy, CSRF, rate limit)
    API->>SVC: ResolveSessionAsync(cookie)
    SVC->>DB: SHA-256(cookie) หา acct.RegistrationSessions
    DB-->>SVC: session หรือ null
    alt ไม่พบ หรือหมดอายุ
        API-->>SPA: 401 (bare Results.Unauthorized, ไม่มี code)
    else live
        Note over API,DB: Phase B — อ่าน case + attempts
        API->>SVC: GetCaseAsync(session)
        SVC->>DB: FindCaseAsync ตรง Provider + TenantId + ExternalUserId + MerchantId ของ session
        DB-->>SVC: registration หรือ null
        alt ไม่พบ registration
            API-->>SPA: 404 (bare Results.NotFound)
        else พบ
            API->>SVC: ListAttemptsAsync(registration.Id)
            SVC->>DB: SELECT ORDER BY AttemptNo
            DB-->>SVC: attempts[]
            API->>API: header ETag = quoted vN (registration.Version)
            alt route GET /agent-registration
                API-->>SPA: 200 registration (+ rejectionReason ของ current attempt) + nextAction (submit / wait / login) + currentAttempt
            else route GET /agent-registration/history
                API-->>SPA: 200 registration (ไม่มี rejectionReason) + attempts[] ทั้งหมด (applicant view, ไม่มี PII / InternalReviewNote)
            end
        end
    end
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/agent-registration` | subject: payload `registration` (rejectionReason จาก attempt ปัจจุบัน) + `nextAction` (Draft / Rejected = submit, Pending = wait, Approved = login) + `currentAttempt` (null เมื่อยังไม่เคย submit) |
| GET | `/api/v1/agent-registration/history` | payload `registration` (ไม่มี rejectionReason) + `attempts[]` ทุก attempt เรียงตาม AttemptNo ในรูป applicant view, ไม่มี nextAction, gate / 401 / 404 / ETag เหมือน subject |

---

## 3.2 ผู้สมัครบันทึก draft (upsert, If-Match optional)

parse If-Match เฉพาะเมื่อมี header แล้ว validate field ก่อนเข้า transaction ที่ล็อกต่อ identity ด้วย sp_getapplock (source: `AgentRegistrationEndpoints.cs:24-26,71-81,183-184`, `Registration.cs:98-103,158-174`, `AgentRegistrationStore.cs:40-69,256-266`, `RegistrationModels.cs:59-76`, `GovernanceSqlLockManager.cs:11-24`, `ConcurrencyEtags.cs:18-26`)

```mermaid
sequenceDiagram
    autonumber
    actor AP as Applicant
    participant SPA as Registration SPA
    participant API as API<br/>AgentRegistrationEndpoints
    participant SVC as AgentRegistrationService<br/>+ AgentRegistrationStore
    participant DB as SQL Server<br/>acct.*

    Note over SPA,DB: Phase A — resolve session (เหมือน § 3.1)
    AP->>SPA: แก้ไขข้อมูลการสมัคร
    SPA->>API: PUT /api/v1/agent-registration + cookie pol_registration_session (+ If-Match เมื่อมี, ไม่มี policy, CSRF, rate limit)
    API->>SVC: ResolveSessionAsync(cookie)
    SVC->>DB: SHA-256(cookie) หา acct.RegistrationSessions
    DB-->>SVC: session หรือ null
    alt ไม่พบ หรือหมดอายุ
        API-->>SPA: 401 (bare)
    else live
        Note over API: Phase B — parse If-Match (optional) + validate draft
        alt มี header If-Match และรูปไม่ใช่ quoted vN (VersionEtags.Require)
            API-->>SPA: 400 ProblemDetails invalid_etag ดู § 0.5
        else รูปถูก หรือไม่ได้ส่ง
            API->>SVC: SaveDraftAsync(session, body, version?)
            SVC->>SVC: ValidateDraft — SaleCode ไม่ว่าง ไม่เกิน 64, Email รูป MailAddress ไม่เกิน 320, PhoneNumber ไม่ว่าง ไม่เกิน 64, Profile เป็น JSON object ไม่เกิน 32768 ตัวอักษร
            alt validation ล้มเหลว
                API-->>SPA: 400 ProblemDetails validation_failed
            else ผ่าน
                Note over SVC,DB: Phase C — upsert ใน transaction, sp_getapplock agent-registration:hash(identity) timeout 15s
                SVC->>DB: SELECT acct.AgentRegistrations ตรง Provider + TenantId + ExternalUserId (ไม่กรอง MerchantId)
                DB-->>SVC: registration หรือ null
                alt ไม่พบ (สร้างใหม่)
                    SVC->>DB: INSERT AgentRegistration MerchantId ของ session, Status Draft, Version 1
                    SVC->>DB: commit
                    API-->>SPA: 200 registration + nextAction submit, currentAttempt null, header ETag v1
                else registration.MerchantId ไม่ตรง session.MerchantId
                    SVC-->>API: ConflictException registration_merchant_mismatch
                    API-->>SPA: 409 ProblemDetails registration_merchant_mismatch
                else ส่ง If-Match มาและ Version ไม่ตรง
                    SVC-->>API: ConcurrencyConflictException
                    API-->>SPA: 409 ProblemDetails Conflict code state_conflict
                else Status เป็น Pending หรือ Approved
                    SVC-->>API: InvalidOperationException (แก้ draft ไม่ได้)
                    API-->>SPA: 409 ProblemDetails ไม่มี code
                else Status Draft หรือ Rejected (แก้ไข)
                    SVC->>DB: UPDATE ทั้ง 4 field, Status กลับเป็น Draft, Version +1
                    SVC->>DB: commit
                    API-->>SPA: 200 registration + nextAction submit, currentAttempt null, header ETag vN ใหม่
                end
            end
        end
    end
```

---

## 3.3 ผู้สมัครส่ง draft เป็น attempt (idempotent, 201)

บังคับ Idempotency-Key และ If-Match (ตรวจ key ก่อน), key ซ้ำที่ intent เดิม replay 201 เดิม, มิฉะนั้น snapshot sale/branch แล้วเปลี่ยน case เป็น Pending ใน transaction เดียว (source: `AgentRegistrationEndpoints.cs:27-29,83-97`, `Registration.cs:105-111`, `AgentRegistrationStore.cs:71-114,215-235,276-279`, `RegistrationModels.cs:78-91,178-187`, `ConcurrencyEtags.cs:18-41`)

```mermaid
sequenceDiagram
    autonumber
    actor AP as Applicant
    participant SPA as Registration SPA
    participant API as API<br/>AgentRegistrationEndpoints
    participant SVC as AgentRegistrationService<br/>+ AgentRegistrationStore
    participant DB as SQL Server<br/>acct.* / merch.*

    Note over SPA,DB: Phase A — resolve session (เหมือน § 3.1)
    AP->>SPA: กดส่งใบสมัคร
    SPA->>API: POST /api/v1/agent-registration/submissions + cookie pol_registration_session + Idempotency-Key + If-Match (ไม่มี policy, CSRF, rate limit)
    API->>SVC: ResolveSessionAsync(cookie)
    SVC->>DB: SHA-256(cookie) หา acct.RegistrationSessions
    DB-->>SVC: session หรือ null
    alt ไม่พบ หรือหมดอายุ
        API-->>SPA: 401 (bare)
    else live
        Note over API: Phase B — parse header (Idempotency-Key ก่อน If-Match)
        alt Idempotency-Key ว่าง หรือเกิน 200 หรือมี control char
            API-->>SPA: 400 ProblemDetails invalid_idempotency_key ดู § 0.5
        else If-Match ไม่ใช่รูป quoted vN
            API-->>SPA: 400 ProblemDetails invalid_etag
        else รูปถูกทั้งคู่
            Note over SVC,DB: Phase C — submit ใน transaction, sp_getapplock agent-registration:hash(identity)
            SVC->>DB: SELECT registration ตรง identity (ไม่กรอง MerchantId)
            DB-->>SVC: registration หรือ null
            alt ไม่พบ
                SVC-->>API: NotFoundException Registration draft was not found
                API-->>SPA: 404 ProblemDetails
            else MerchantId ไม่ตรง session
                SVC-->>API: ConflictException registration_merchant_mismatch
                API-->>SPA: 409 ProblemDetails registration_merchant_mismatch
            else registration.Version ไม่ตรง If-Match
                SVC-->>API: ConcurrencyConflictException
                API-->>SPA: 409 ProblemDetails Conflict code state_conflict
            else ตรงทุกอย่าง, มี attempt เดิมที่ IdempotencyKey เดียวกัน
                SVC->>SVC: IntentHash = SHA-256(SaleCode, Email, PhoneNumber, ProfileJson)
                alt IntentHash ตรงกับ attempt เดิม
                    API-->>SPA: 201 เดิม replayed = true, ไม่เขียน DB, ETag = version ปัจจุบัน
                else IntentHash ไม่ตรง
                    SVC-->>API: ConflictException idempotency_conflict
                    API-->>SPA: 409 ProblemDetails idempotency_conflict
                end
            else ตรงทุกอย่าง, ไม่มี attempt เดิมที่ key นี้, registration.Status Pending
                SVC-->>API: ConflictException registration_pending
                API-->>SPA: 409 ProblemDetails registration_pending
            else ไม่มี attempt เดิม, registration.Status Approved
                SVC-->>API: ConflictException account_already_approved
                API-->>SPA: 409 ProblemDetails account_already_approved
            else ไม่มี attempt เดิม, Status Draft หรือ Rejected
                SVC->>DB: SELECT TOP 1 merch.Sales JOIN merch.Branches Code = SaleCode, MerchantId เดียวกัน, Status = 1 ทั้งคู่
                DB-->>SVC: sale snapshot หรือ null
                alt ไม่พบ sale ที่ active
                    SVC-->>API: ConflictException registration_sale_invalid
                    API-->>SPA: 409 ProblemDetails registration_sale_invalid
                else พบ
                    SVC->>DB: INSERT AgentRegistrationAttempt — AttemptNo +1, Status Pending, snapshot SaleId/BranchId/SaleVersion/BranchVersion, form, IdempotencyKey, IntentHash
                    SVC->>DB: UPDATE registration.StartAttempt — CurrentAttemptId/No, Status Pending, Version +1
                    SVC->>DB: commit (ไม่มี outbox)
                    API-->>SPA: 201 Created Location /api/v1/agent-registration/history/{attemptId}, body registration + attempt + replayed = false, header ETag vN ใหม่
                end
            end
        end
    end
```

---

## 3.4 ผู้ตรวจดูรายการ case ใน merchant scope

ไม่ใช้ SFS: เลือก merchant จาก query หรือจาก scope ของ admin ก่อนอ่านทุก case (source: `AgentRegistrationEndpoints.cs:33-39,118-131`, `AgentRegistrationStore.cs:210-213`)

```mermaid
sequenceDiagram
    autonumber
    actor AD as Admin (reviewer)
    participant CON as Admin console
    participant API as API<br/>AgentRegistrationEndpoints
    participant SVC as AgentRegistrationService<br/>+ AgentRegistrationStore
    participant DB as SQL Server<br/>acct.*

    Note over CON,API: Phase A — authz (ดู § 0.1), CSRF ข้าม GET (ดู § 0.3)
    AD->>CON: เปิดหน้ารายการ registration
    CON->>API: GET /api/v1/agent-registrations?merchantId=... + Authorization Bearer (policy admin + permission merchants.users.view)
    Note over API: Phase B — เลือก merchant scope
    alt query merchantId ส่งมา
        API->>API: selected = merchantId
        alt scope.Accessible.Allows(selected)
            API->>SVC: ListCasesAsync(selected)
            SVC->>DB: SELECT WHERE MerchantId = selected ORDER BY UpdatedAt DESC
            DB-->>SVC: registrations[]
            API-->>CON: 200 array RegistrationCaseView
        else ไม่ allow
            API-->>CON: 404 (bare)
        end
    else ไม่ส่ง, scope.Accessible.IsUnrestricted หรือ Accessible ว่าง
        API-->>CON: 400 ValidationProblem merchantId is required when more than one Merchant is accessible
    else ไม่ส่ง, Accessible มี merchant เดียว
        API->>API: selected = merchant เดียวนั้น (SingleOrDefault, allow เสมอ)
        API->>SVC: ListCasesAsync(selected)
        SVC->>DB: SELECT WHERE MerchantId = selected ORDER BY UpdatedAt DESC
        DB-->>SVC: registrations[]
        API-->>CON: 200 array RegistrationCaseView
    else ไม่ส่ง, Accessible มี 2 merchant ขึ้นไป
        API-->>CON: 409 ProblemDetails InvalidOperationException (SingleOrDefault บนหลายค่า) ดู § 0.9
    end
```

---

## 3.5 ผู้ตรวจอ่าน case และ attempts ของ case

scope check ด้วย MerchantId ของ case ที่พบ, ไม่พบหรือนอก scope ตอบ 404 เหมือนกัน, ไม่ตั้ง header ETag (source: `AgentRegistrationEndpoints.cs:40-45,133-150`, `Registration.cs:145-156`, `AgentRegistrationStore.cs:37-38,205-208`)

```mermaid
sequenceDiagram
    autonumber
    actor AD as Admin (reviewer)
    participant CON as Admin console
    participant API as API<br/>AgentRegistrationEndpoints
    participant SVC as AgentRegistrationService<br/>+ AgentRegistrationStore
    participant DB as SQL Server<br/>acct.*

    Note over CON,API: Phase A — authz (ดู § 0.1), CSRF ข้าม GET (ดู § 0.3)
    AD->>CON: เปิด case เพื่อพิจารณา
    CON->>API: GET /api/v1/agent-registrations/{registrationId} หรือ /attempts + Authorization Bearer (policy admin + permission merchants.users.view)
    API->>SVC: GetCaseByIdAsync(registrationId)
    SVC->>DB: SELECT acct.AgentRegistrations WHERE Id = registrationId
    DB-->>SVC: registration หรือ null
    alt ไม่พบ หรือ ไม่ scope.Accessible.Allows(registration.MerchantId)
        API-->>CON: 404 (bare, ไม่แยกเหตุ ไม่พบ กับ นอก scope)
    else พบและอยู่ใน scope
        alt route GET /{registrationId}
            API-->>CON: 200 RegistrationCaseView (version อยู่ใน body, ไม่ตั้ง header ETag)
        else route GET /{registrationId}/attempts
            API->>SVC: ListAttemptsAsync(registrationId)
            SVC->>DB: SELECT ORDER BY AttemptNo
            DB-->>SVC: attempts[]
            API-->>CON: 200 array reviewer attempt view (มี SaleCode, Email, PhoneNumber, ProfileJson, InternalReviewNote)
        end
    end
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/agent-registrations/{registrationId:guid}` | subject: คืน `RegistrationCaseView` ตัวเดียว (`version` ใน body ใช้สร้าง If-Match ของ § 3.6) |
| GET | `/api/v1/agent-registrations/{registrationId:guid}/attempts` | อ่าน attempts เพิ่มหลัง scope check, คืน array reviewer view ที่มี PII และ InternalReviewNote, gate / 404 เหมือน subject |

---

## 3.6 ผู้ตรวจตัดสิน attempt (approve / reject)

mutate ตรง (ไม่ใช่ maker-checker): replay จาก DecisionIdempotencyKey ก่อนตรวจ version, approve เพิ่มการตรวจ sale / identity / role แล้วสร้าง account ชุดเต็ม, ทั้งคู่เขียน GovernanceOutbox ให้ notification (source: `AgentRegistrationEndpoints.cs:46-53,152-177`, `Registration.cs:113-137,176-180`, `AgentRegistrationStore.cs:116-203,245-254,268-283`, `RegistrationModels.cs:93-102,189-237`, `AgentRegistrationDecidedV1.cs:23-37`, `GovernanceOutboxDispatcher.cs:22-30,111-138`)

```mermaid
sequenceDiagram
    autonumber
    actor AD as Admin (reviewer)
    participant CON as Admin console
    participant API as API<br/>AgentRegistrationEndpoints
    participant SVC as AgentRegistrationService<br/>+ AgentRegistrationStore
    participant DB as SQL Server<br/>acct.* / admin.*
    participant OB as GovernanceOutboxDispatcher<br/>ดู § 0.8

    Note over CON,API: Phase A — authz (ดู § 0.1) + CSRF (ดู § 0.3) + ETag/Idempotency (ดู § 0.5)
    AD->>CON: ตัดสินใจ approve หรือ reject attempt
    CON->>API: POST .../attempts/{attemptId}/approve หรือ /reject + Authorization Bearer + If-Match + Idempotency-Key (policy admin + permission merchants.users.approve หรือ merchants.users.reject)
    API->>SVC: GetCaseByIdAsync(registrationId)
    SVC->>DB: SELECT WHERE Id = registrationId
    DB-->>SVC: registration หรือ null
    alt ไม่พบ หรือนอก scope
        API-->>CON: 404 (bare)
    else พบและอยู่ใน scope
        Note over API: Phase B — parse header + service guard
        alt Idempotency-Key ผิดรูป
            API-->>CON: 400 ProblemDetails invalid_idempotency_key
        else If-Match ผิดรูป
            API-->>CON: 400 ProblemDetails invalid_etag
        else scope.Current.AdminId ว่าง
            API-->>CON: 403 ProblemDetails permission_denied (Reviewer identity is required)
        else body field บังคับว่าง (contactEvidenceReference สำหรับ approve, rejectionReason สำหรับ reject)
            API-->>CON: 400 ProblemDetails contact_evidence_required หรือ rejection_reason_required
        else รูปถูกและ field ครบ
            Note over SVC,DB: Phase C — ตัดสินใจใน transaction, sp_getapplock agent-registration:{registrationId} timeout 15s
            SVC->>DB: SELECT registration Id + attempt Id ตรง RegistrationId
            alt ไม่พบทั้งคู่
                SVC-->>API: NotFoundException
                API-->>CON: 404 ProblemDetails
            else attempt.DecisionIdempotencyKey มีแล้วและ key + DecisionIntentHash ตรง
                API-->>CON: 200 ผลเดิม replayed = true (ไม่ตรวจ If-Match, ไม่เขียน DB)
            else attempt.DecisionIdempotencyKey มีแล้วแต่ไม่ตรง
                SVC-->>API: ConflictException decision_already_recorded
                API-->>CON: 409 ProblemDetails decision_already_recorded
            else ยังไม่เคยตัดสิน, registration.Version ไม่ตรง If-Match
                SVC-->>API: ConcurrencyConflictException
                API-->>CON: 409 ProblemDetails Conflict code state_conflict
            else ยังไม่เคยตัดสิน, registration/attempt ไม่ Pending ตรงกัน
                SVC-->>API: ConflictException registration_not_pending
                API-->>CON: 409 ProblemDetails registration_not_pending
            else ยังไม่เคยตัดสิน, version ตรง, Pending ตรงกัน, เป็น approve
                SVC->>DB: ตรวจ sale/branch snapshot ตรง, ไม่มี Agents.SaleId ซ้ำ, ไม่มี LoginAccounts identity ซ้ำ, มี Roles merchant_staff Scope Shared Active
                alt การตรวจข้อใดข้อหนึ่งล้มเหลว
                    SVC-->>API: ConflictException registration_context_changed / sale_already_bound / identity_already_bound / registration_role_unavailable
                    API-->>CON: 409 ProblemDetails
                else ผ่านทุกการตรวจ
                    SVC->>DB: attempt.SetDecisionIdempotency + Approve, registration.ApplyDecision Approved, Version +1
                    SVC->>DB: INSERT Account(Agent) + LoginAccount + Agent + MerchantAccess DataScope.Self + AccessRole merchant_staff
                    SVC->>DB: INSERT admin.GovernanceOutboxMessages AgentRegistrationDecidedV1 (decision approved)
                    SVC->>DB: commit
                    API-->>CON: 200 registration + attempt (reviewer view) + replayed = false, header ETag vN ใหม่
                end
            else ยังไม่เคยตัดสิน, version ตรง, Pending ตรงกัน, เป็น reject
                SVC->>DB: attempt.SetDecisionIdempotency + Reject (rejectionReason ไม่เกิน 1000, internalReviewNote optional ไม่เกิน 4000), registration.ApplyDecision Rejected, Version +1
                SVC->>DB: INSERT admin.GovernanceOutboxMessages AgentRegistrationDecidedV1 (decision rejected)
                SVC->>DB: commit
                API-->>CON: 200 registration + attempt (reviewer view) + replayed = false, header ETag vN ใหม่
            end
        end
    end
    Note over OB,DB: Phase D — async dispatch (ดู § 0.8)
    OB->>DB: lease admin.GovernanceOutboxMessages (READPAST, UPDLOCK)
    OB->>OB: publish AgentRegistrationDecidedV1 ให้ AgentRegistrationDecidedV1Handler
    OB->>DB: NotificationMaterializer เขียน Notifications + Deliveries ต่อ channel email / sms (template agent-registration-result.v1)
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| POST | `/api/v1/agent-registrations/{registrationId:guid}/attempts/{attemptId:guid}/approve` | permission `merchants.users.approve`, body `contactEvidenceReference` (ว่าง = 400 `contact_evidence_required`, เกิน 256 = 400 ArgumentException ไม่มี code), เดิน branch approve: 4 การตรวจ 409 เพิ่ม + สร้าง Account / LoginAccount / Agent / MerchantAccess / AccessRole |
| POST | `/api/v1/agent-registrations/{registrationId:guid}/attempts/{attemptId:guid}/reject` | permission `merchants.users.reject`, body `rejectionReason` (ว่าง = 400 `rejection_reason_required`, เกิน 1000 = 400) + `internalReviewNote` optional (เกิน 4000 = 400), เดิน branch reject: ไม่มีการตรวจ sale / identity / role, เขียนเฉพาะ attempt + registration + outbox |

---

## Notes

- Deviations ของ theme นี้อยู่ที่ `03-agent-registration.activities.md` (ไม่พบ deviation ระหว่างเอกสารกับ source) — sequences.md ไม่ทำซ้ำตาราง
- SVC ในทุก diagram รวม `AgentRegistrationService` (validation, orchestration) กับ `AgentRegistrationStore` (transaction, lock, DB) ไว้ participant เดียวเพื่อความกระชับ, รายละเอียดแยกชั้นดู source ต่อ § ใน activities.md
- § 3.6 แสดง Phase D (async) เป็น note เดียวท้าย diagram ครอบทั้ง branch approve และ reject เพราะทั้งคู่ enqueue `AgentRegistrationDecidedV1` แบบเดียวกัน, ปลายทาง Notification เต็มอยู่นอก frame ของ theme นี้ (ดู Notes ของ activities.md)
- endpoint ฝั่งผู้สมัคร (§ 3.1–3.3) ไม่มี policy หรือ CSRF filter ของแพลตฟอร์ม (AllowAnonymous, ตรวจเองด้วย registration session) จึงไม่มี arrow อ้าง § 0.1 / § 0.3 ในสามไดอะแกรมนี้

**Render**: GitHub / Obsidian / VS Code Mermaid
