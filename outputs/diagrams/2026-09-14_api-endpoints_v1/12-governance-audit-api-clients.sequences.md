# pol-core API — Governance (maker-checker), audit และ API clients (Sequence Diagrams)

> Source: `docs/reference/api-endpoints.md` section "Governance และ audit" บรรทัด L298-L303 และ section "API clients" บรรทัด L309-L315 พร้อม source ที่อ้างต่อ § (`src/Api/Api/Governance/GovernanceEndpoints.cs`, `src/Api/Api/Iam/ApiClientEndpoints.cs`, `src/Api/Api/ConcurrencyEtags.cs`, `Persistence.ControlPlane/Governance/GovernanceStore.cs`, `ControlPlaneOperationExecutor.cs`, `GovernanceOutboxDispatcher.cs`, `Persistence.ControlPlane/Iam/ApiClientStore.cs`, `ApiClientApprovalExecutor.cs`, `src/Domain/Modules/Governance.Domain/ApprovalRequest.cs`, `src/Domain/Modules/Iam.Domain/ApiClients/ApiClient.cs`, `src/Application/Modules/Governance.Application/GovernanceHandlers.cs`)
> Scope: 7 § เดียวกับ `12-governance-audit-api-clients.activities.md` (หมายเลข § ตรงกัน) แสดงลำดับข้าม actor (maker, checker) / API / store / DB / outbox worker / executor
> Generated: 2026-09-14

| § | Diagram | Endpoints |
| --- | --- | --- |
| 12.1 | GET list / detail ภายใน Admin merchant scope | `GET /api/v1/approvals`, `GET /api/v1/approvals/{approvalId:guid}`, `GET /api/v1/api-clients`, `GET /api/v1/api-clients/{clientId:guid}` |
| 12.2 | ตัดสินคำขอ maker-checker (approve / reject) | `POST /api/v1/approvals/{approvalId:guid}/approve`, `POST /api/v1/approvals/{approvalId:guid}/reject` |
| 12.3 | อ่าน audit แบบ append-only พร้อมตรวจ hash chain | `GET /api/v1/audits`, `GET /api/v1/audits/{auditId:guid}` |
| 12.4 | สร้าง API client + Ready secret ticket | `POST /api/v1/api-clients` |
| 12.5 | แก้ไข / เพิกถอน API client | `PUT /api/v1/api-clients/{clientId:guid}`, `POST /api/v1/api-clients/{clientId:guid}/revoke` |
| 12.6 | ขอหมุน client secret (maker) + executor async | `POST /api/v1/api-clients/{clientId:guid}/secret-rotation-requests` |
| 12.7 | เปิดดู client secret หนึ่งครั้ง | `POST /api/v1/api-clients/secrets/{ticketId}/reveal` |

---

## 12.1 GET list / detail ภายใน Admin merchant scope

ลำดับเดียวกันสำหรับทั้ง 4 endpoint: gate แล้ว list ผ่าน typed filter + scope query หรือ detail ผ่าน scope + SingleOrDefault (source: `GovernanceEndpoints.cs:72-93,184-216`, `ApiClientEndpoints.cs:13-37`, `GovernanceStore.cs:23-70`, `ApiClientStore.cs:29-61`)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin Console
    participant API as GET /approvals หรือ /api-clients
    participant ST as GovernanceStore / ApiClientStore
    participant DB as DB (admin.ApprovalRequests / iam.ApiClients)

    Note over A,API: Phase A — gate
    A->>SPA: เปิดรายการ หรือ รายละเอียด
    SPA->>API: GET + cookie session (ดู § 0.1, permission settings.manage หรือ apikey.manage, api-clients ต่อ RequireCsrf แต่ GET ข้าม ดู § 0.3)
    Note over API,DB: Phase B — list
    API->>API: typed filter, page >= 1, limit 1..100, guid / instant / status / ความยาว
    alt filter ผิด
        API-->>SPA: 400 ProblemDetails invalid_filter
    else ผ่าน
        API->>ST: ListAsync(access, filters)
        ST->>DB: ApplyAccess (unrestricted หรือ MerchantId ใน accessible) + WHERE filters + ORDER BY + OFFSET / FETCH
        DB-->>ST: items, total
        ST-->>API: PagedResult
        API-->>SPA: 200 PagedResult
    end
    Note over API,DB: Phase C — detail
    API->>ST: GetAsync(id, access)
    ST->>DB: ApplyAccess + SingleOrDefault(Id)
    DB-->>ST: row หรือ null
    alt ไม่พบหรือนอก scope
        ST-->>API: null
        API-->>SPA: 404 (approvals = ProblemDetails not_found, api-clients = bare NotFound ดู § 0.9)
    else พบ
        ST-->>API: detail
        API-->>SPA: 200 + ETag vN (ดู § 0.5)
    end
```

---

## 12.2 ตัดสินคำขอ maker-checker (approve / reject)

`DecideAsync` ทำ idempotency, scope, permission และกฎ maker-checker ทั้งหมดในทรานแซกชันเดียว ก่อนเขียนผลตัดสินและ enqueue ให้ executor ทำงานแบบ async (source: `GovernanceEndpoints.cs:123-182`, `GovernanceStore.cs:72-146`, `ApprovalRequest.cs:74-98`)

```mermaid
sequenceDiagram
    autonumber
    actor K as Checker (Admin)
    participant SPA as Admin Console
    participant API as POST /approvals/{id}/approve หรือ /reject
    participant GOV as GovernanceStore
    participant DB as DB (admin.ApprovalRequests, OperationRecords, GovernanceOutboxMessages)
    participant DISP as GovernanceOutboxDispatcher

    Note over K,API: Phase A — รับคำขอตัดสิน
    K->>SPA: ตัดสินคำขอ (approve หรือ reject)
    SPA->>API: POST .../approve หรือ /reject + If-Match vN + Idempotency-Key + reason, targetVersion + X-CSRF-Token (ดู § 0.1, § 0.3)
    alt If-Match, Idempotency-Key, reason หรือ targetVersion ผิดรูป
        API-->>SPA: 400 ProblemDetails invalid_request
    end
    API->>GOV: DecideAsync(intent)
    Note over GOV,DB: Phase B — idempotency ในทรานแซกชันเดียว
    GOV->>DB: app lock governance-operation(actor, operation, key) + lookup OperationRecord
    alt record เดิม hash ต่างกัน
        GOV-->>API: ConflictException idempotency_key_reused
        API-->>SPA: 409 ProblemDetails
    else record เดิม InProgress หรือไม่มี body
        GOV-->>API: ConflictException operation_in_progress
        API-->>SPA: 409 ProblemDetails
    else record เดิม Succeeded
        GOV-->>API: ApprovalDetail เดิม (Replayed)
        API-->>SPA: 202 + ETag เดิม
    else ไม่มี record
        GOV->>DB: SingleOrDefault(approvalId)
        alt ไม่พบ
            GOV-->>API: NotFoundException
            API-->>SPA: 404 ProblemDetails not_found
        else พบ
            Note over GOV,DB: Phase C — ตรวจ scope, permission และกฎ maker-checker
            alt merchantId นอก accessible ของ checker
                GOV-->>API: GovernanceAccessDeniedException merchant_scope_forbidden
                API-->>SPA: 403 ProblemDetails
            else checker ไม่มี RequiredPermission
                GOV-->>API: GovernanceAccessDeniedException underlying_permission_forbidden
                API-->>SPA: 403 ProblemDetails
            else checker = maker
                GOV-->>API: GovernanceAccessDeniedException maker_cannot_decide
                API-->>SPA: 403 ProblemDetails
            else ไม่ Pending หรือ version stale
                GOV-->>API: ConflictException approval_not_pending
                API-->>SPA: 409 ProblemDetails
            else targetVersion เปลี่ยน
                GOV-->>API: ConflictException target_version_changed
                API-->>SPA: 409 ProblemDetails
            else ผ่านทุกกฎ
                Note over GOV,DB: Phase D — เขียนผลตัดสินและ enqueue
                GOV->>DB: Status Approved / Rejected v+1, ApprovalEvent decided, outbox ApprovalDecided, audit approval.decided, OperationRecord 202, commit
                GOV-->>API: ApprovalDetail
                API-->>SPA: 202 + ETag v(ใหม่)
            end
        end
    end
    Note over DISP,DB: Phase E — execute แบบ async (รายละเอียดดู § 12.6 สำหรับ api-client-secret, § 0.7 / § 0.8 ทั่วไป)
    DISP->>DB: lease ApprovalDecided (READPAST, UPDLOCK)
    DISP->>DISP: publish ให้ executor ของ TargetType นั้น
```

---

## 12.3 อ่าน audit แบบ append-only พร้อมตรวจ hash chain

list ตรวจ chain ของทุก scope ที่ accessible ก่อน query ส่วน detail ตรวจเฉพาะ scope ของ record ที่พบ (source: `GovernanceEndpoints.cs:98-120,218-265`, `GovernanceStore.cs:198-330`, `AuditAnchorRegistration.cs:22-46`)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin Console
    participant API as GET /audits หรือ /audits/{id}
    participant GOV as GovernanceStore
    participant ANCH as AuditAnchorStore (ไฟล์ anchor)
    participant DB as DB (admin.AuditRecords, AuditHeads)

    Note over A,API: Phase A — gate policy admin + permission audit.view (ดู § 0.1)
    A->>SPA: เปิดรายการ หรือ รายละเอียด audit
    SPA->>API: GET + cookie session
    API->>API: typed filter, page, limit, actor / merchantId guid, from <= to, ความยาว action / resource / result
    alt filter ผิด
        API-->>SPA: 400 ProblemDetails invalid_filter
    end
    Note over API,DB: Phase B — list ตรวจ chain ทุก scope ก่อน query
    API->>GOV: ListAuditsAsync(query)
    GOV->>DB: AuditHeads ทุก scope ที่ accessible
    DB-->>GOV: scopeKeys
    opt anchor เปิดใช้ (ดู Notes)
        GOV->>ANCH: ReadAllLatestAsync
        ANCH-->>GOV: anchors ต่อ scope
    end
    loop ทุก scope
        GOV->>DB: อ่าน records เรียง Sequence, ตรวจ PreviousHash ต่อเนื่อง, HasValidHash, head ตรง, anchor ตรง (ถ้าเปิด)
    end
    alt scope ใดไม่ผ่าน
        GOV-->>API: AuditIntegrityException
        API-->>SPA: 503 ProblemDetails audit_integrity_unhealthy
    else ทุก scope ผ่าน
        GOV->>DB: ApplyAccess + filters + ORDER BY OccurredAt desc, Id desc + OFFSET / FETCH
        DB-->>GOV: rows, total
        GOV-->>API: PagedResult AuditListItem
        API-->>SPA: 200 PagedResult
    end
    Note over API,DB: Phase C — detail ตรวจ chain เฉพาะ scope ของ record
    API->>GOV: GetAuditAsync(auditId)
    GOV->>DB: ApplyAccess + SingleOrDefault(auditId)
    alt ไม่พบหรือนอก scope
        GOV-->>API: null
        API-->>SPA: 404 ProblemDetails not_found
    else พบ
        GOV->>DB: VerifyScopeAsync เฉพาะ record.ScopeKey (อ่าน anchor เองถ้าเปิด)
        alt chain ของ scope นั้นไม่ผ่าน
            GOV-->>API: AuditIntegrityException
            API-->>SPA: 503 ProblemDetails audit_integrity_unhealthy
        else ผ่าน
            GOV-->>API: AuditDetail (changes redacted, approvalId, previousHash, hash)
            API-->>SPA: 200 AuditDetail
        end
    end
```

---

## 12.4 สร้าง API client + Ready secret ticket

`ControlPlaneOperationExecutor` ผูก idempotency กับการสร้าง client และ Ready secret ticket ในทรานแซกชันเดียว plaintext secret ไม่อยู่ใน response (source: `ApiClientEndpoints.cs:39-52`, `ApiClientStore.cs:63-94`, `ControlPlaneOperationExecutor.cs:43-82`, `ApiClient.cs:27-49`)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin Console
    participant API as POST /api-clients
    participant ST as ApiClientStore
    participant EX as ControlPlaneOperationExecutor
    participant DB as DB (iam.ApiClients, OneTimeSecretTickets, OperationRecords)

    Note over A,API: Phase A — gate + validate body
    A->>SPA: สร้าง API client
    SPA->>API: POST /api-clients + Idempotency-Key + body name, merchantId, scopes, ipPolicy + X-CSRF-Token (ดู § 0.1, § 0.3)
    alt Idempotency-Key ผิดรูป
        API-->>SPA: 400 ProblemDetails invalid_idempotency_key
    end
    API->>ST: CreateAsync(input)
    alt merchantId นอก scope ของ admin
        ST-->>API: AccessDeniedException
        API-->>SPA: 403 ProblemDetails
    else scopes ว่าง, ซ้ำ หรือนอก allowlist
        ST-->>API: InvalidRequestException invalid_scope
        API-->>SPA: 400 ProblemDetails
    else ipPolicy parse ไม่ได้
        ST-->>API: InvalidRequestException validation_failed
        API-->>SPA: 400 ProblemDetails
    else ผ่าน
        Note over ST,DB: Phase B — idempotent execute ในทรานแซกชันเดียว
        ST->>EX: ExecuteAsync(actor, merchant, api-client.create, key, intent, action)
        EX->>DB: app lock admin-operation + lookup OperationRecord
        alt record เดิม hash ต่างกัน
            EX-->>ST: ConflictException idempotency_key_reused
            ST-->>API: 409
            API-->>SPA: 409 ProblemDetails
        else record เดิมยังไม่ Succeeded
            EX-->>ST: ConflictException operation_in_progress
            API-->>SPA: 409 ProblemDetails
        else record เดิม Succeeded
            EX-->>ST: body เดิม (Replayed)
            ST-->>API: ApiClientCreated (Replayed)
            API-->>SPA: 201 body เดิม
        else ไม่มี record
            ST->>ST: clientId = cli_live_ + 12 bytes, secret = pol_ + 32 bytes, SecretHash = HMAC(VaultKeyring.Active)
            ST->>DB: ApiClient Active v1 + OneTimeSecretTicket Ready (ProtectedSecret, หมดอายุ 10 นาที) + OperationRecord 201, commit
            DB-->>ST: commit
            ST-->>API: ApiClientCreated (client, secretTicket ticketId / expiresAt, ไม่มี plaintext)
            API-->>SPA: 201 Location /api-clients/{id}, Cache-Control no-store, ETag v1
        end
    end
```

---

## 12.5 แก้ไข / เพิกถอน API client

update และ revoke ผ่าน executor เดียวกันด้วย If-Match + Idempotency-Key, update ปฏิเสธ client ที่ revoked แล้ว, revoke เป็น no-op เมื่อ revoked อยู่แล้ว (source: `ApiClientEndpoints.cs:54-82`, `ApiClientStore.cs:96-133`, `ApiClient.cs:51-60,92-100`)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin Console
    participant API as PUT /api-clients/{id} หรือ POST .../revoke
    participant ST as ApiClientStore
    participant EX as ControlPlaneOperationExecutor
    participant DB as DB (iam.ApiClients, OperationRecords)

    Note over A,API: Phase A — gate + header + body
    A->>SPA: แก้ไข หรือ เพิกถอน API client
    SPA->>API: PUT / POST + If-Match vN + Idempotency-Key (+ body scopes, ipPolicy สำหรับ PUT) + X-CSRF-Token (ดู § 0.1, § 0.3)
    alt If-Match หรือ Idempotency-Key ผิดรูป
        API-->>SPA: 400 ProblemDetails invalid_etag / invalid_idempotency_key
    else PUT และ scopes หรือ ipPolicy ไม่ถูกต้อง
        API-->>SPA: 400 ProblemDetails invalid_scope / validation_failed
    end
    API->>ST: MerchantIdAsync(id)
    ST->>DB: Scope + Where(Id)
    alt ไม่พบ
        API-->>SPA: 404 bare NotFound (ดู § 0.9)
    end
    Note over ST,DB: Phase B — idempotent execute ในทรานแซกชันเดียว
    ST->>EX: ExecuteAsync(actor, merchant, api-client.update / .revoke, key, intent, action)
    EX->>DB: app lock + lookup OperationRecord
    alt record เดิม hash ต่างกัน
        EX-->>API: ConflictException idempotency_key_reused
        API-->>SPA: 409 ProblemDetails
    else record เดิมยังไม่ Succeeded
        EX-->>API: ConflictException operation_in_progress
        API-->>SPA: 409 ProblemDetails
    else record เดิม Succeeded
        EX-->>API: body เดิม (Replayed)
        API-->>SPA: 200 + ETag เดิม
    else ไม่มี record
        EX->>DB: SingleAsync(id) แล้วเทียบ row.Version = If-Match
        alt version ไม่ตรง
            EX-->>API: ConcurrencyConflictException
            API-->>SPA: 409 ProblemDetails Conflict
        else ตรง และ PUT บน client ที่ Revoked
            EX-->>API: ConflictException state_conflict
            API-->>SPA: 409 ProblemDetails
        else ตรง และผ่าน
            EX->>DB: PUT - Update Name / ScopesCsv / IpPolicy, revoke - Status Revoked + ล้าง pending rotation (no-op ถ้า revoked อยู่แล้ว), Version++, OperationRecord 200, commit
            EX-->>API: ApiClientMutation
            API-->>SPA: 200 + ETag vN ใหม่
        end
    end
```

---

## 12.6 ขอหมุน client secret (maker) + executor async

maker stage ticket Pending และ enqueue `ApprovalRequested` ในทรานแซกชันเดียว secret ใหม่ถูกสร้างเฉพาะตอน executor apply หลัง checker approve (source: `ApiClientEndpoints.cs:84-100`, `ApiClientStore.cs:135-170`, `ApiClientApprovalExecutor.cs:25-80`, `GovernanceStore.cs:148-196`, `GovernanceOutboxDispatcher.cs:81-138`, `ApiClient.cs:62-90`, `ApprovalRequest.cs:100-113`)

```mermaid
sequenceDiagram
    autonumber
    actor M as Maker (Admin)
    actor K as Checker (Admin)
    participant SPA as Admin Console
    participant API as API
    participant ST as ApiClientStore
    participant EX as ControlPlaneOperationExecutor
    participant DB as DB (iam.ApiClients, OneTimeSecretTickets, admin.ApprovalRequests, GovernanceOutboxMessages)
    participant DISP as GovernanceOutboxDispatcher
    participant GOV as GovernanceStore
    participant EXE as ApiClientApprovalExecutor

    Note over M,API: Phase A — maker ยื่นคำขอหมุน secret
    M->>SPA: ขอหมุน client secret
    SPA->>API: POST /api-clients/{id}/secret-rotation-requests + If-Match vN + Idempotency-Key + X-CSRF-Token (ดู § 0.1, § 0.3)
    alt If-Match หรือ Idempotency-Key ผิดรูป
        API-->>SPA: 400 ProblemDetails invalid_etag / invalid_idempotency_key
    end
    API->>ST: MerchantIdAsync(id)
    alt ไม่พบ
        ST-->>API: null
        API-->>SPA: 404 bare NotFound (ดู § 0.9)
    end
    ST->>EX: ExecuteAsync(actor, merchant, api-client.secret.rotate, key, intent, action)
    EX->>DB: app lock + lookup OperationRecord
    alt record เดิม hash ต่างกัน
        EX-->>API: ConflictException idempotency_key_reused
        API-->>SPA: 409 ProblemDetails
    else record เดิมยังไม่ Succeeded
        EX-->>API: ConflictException operation_in_progress
        API-->>SPA: 409 ProblemDetails
    else record เดิม Succeeded
        EX-->>API: body เดิม (Replayed)
        API-->>SPA: 202 + ETag เดิม
    else ไม่มี record
        EX->>DB: SingleAsync(id) แล้วเทียบ row.Version = If-Match
        alt version ไม่ตรง
            EX-->>API: ConcurrencyConflictException
            API-->>SPA: 409 ProblemDetails Conflict
        else client ไม่ Active หรือมี PendingRotationApprovalId อยู่แล้ว
            EX-->>API: ConflictException state_conflict
            API-->>SPA: 409 ProblemDetails
        else ผ่าน
            EX->>EX: approvalId (Guid v7) + ticket token 32 bytes
            EX->>DB: OneTimeSecretTicket Pending (หมดอายุ 24 ชม.) + client.RequestRotation (PendingRotationApprovalId / TicketId, Version++) + outbox ApprovalRequested + OperationRecord 202, commit
            EX-->>API: ApiClientRotationRequested (approvalId, ticket, status pending)
            API-->>SPA: 202 Location /approvals/{approvalId}, Cache-Control no-store, ETag = ClientVersion
        end
    end
    Note over DISP,DB: Phase B — governance รับคำขอ (async)
    DISP->>DB: lease ApprovalRequested (READPAST, UPDLOCK)
    DISP->>GOV: Publish(ApprovalRequested)
    GOV->>DB: ApprovalRequest Pending v1 + ApprovalEvent requested + audit approval.created
    Note over K,API: Phase C — checker ตัดสิน (รายละเอียดเต็ม ดู § 12.2)
    K->>SPA: เปิดคำขอ แล้วอ่าน ETag v1 จาก GET /approvals/{approvalId} (ไม่ใช่ ETag ของ client ใน Phase A)
    SPA->>API: POST /approvals/{approvalId}/approve หรือ /reject + If-Match v1 + Idempotency-Key + reason, targetVersion
    API-->>SPA: 202 ApprovalDetail + ETag vN (400 / 403 / 409 ดู § 12.2)
    Note over DISP,EXE: Phase D — execute แบบ async
    DISP->>DB: lease ApprovalDecided
    DISP->>EXE: Publish(ApprovalDecided), CanHandle "api-client-secret" ตรง 1 executor
    EXE->>DB: SingleOrDefault(clientId, merchantId)
    alt ไม่พบ client
        EXE-->>DISP: NotFoundException
        DISP->>DB: MarkFailed, retry จนครบ 8 ครั้ง (ดู § 0.8)
    else PendingRotationApprovalId, TargetVersion หรือ PendingRotationTicketId ไม่ตรง
        EXE-->>DISP: ConcurrencyConflictException API-client rotation target changed
        DISP->>DB: MarkFailed, retry จนครบ 8 ครั้ง (version drift ค้าง approved v2 ดู Notes)
    else ตรง และ decision rejected
        EXE->>DB: ticket.Reject, client.RejectRotation (Version++), outbox ApprovalExecutionReported succeeded=false, commit
    else ตรง และ decision approved
        EXE->>EXE: secret ใหม่ pol_ + 32 bytes, SecretHash = HMAC(VaultKeyring.Active)
        EXE->>DB: client.CompleteRotation (Version++), ticket.Activate (Ready หมดอายุ 10 นาทีนับใหม่), outbox ApprovalExecutionReported succeeded=true, commit
    end
    Note over DISP,GOV: Phase E — บันทึกผล execution (async)
    DISP->>DB: lease ApprovalExecutionReported
    DISP->>GOV: Publish(ApprovalExecutionReported)
    alt decision เดิมคือ approved
        GOV->>DB: RecordExecution สำเร็จ, Status Succeeded v3, audit approval.executed
    else decision เดิมคือ rejected
        GOV->>GOV: RecordExecution โยน ApprovalRuleException approval_rejected (Status เป็น Rejected อยู่ก่อนแล้ว)
        GOV-->>DISP: exception
        DISP->>DB: MarkFailed, retry จนครบ 8 ครั้งแล้วหยุด (ค้าง rejected v2, ExecutionOutcome null ดู Notes)
    end
```

---

## 12.7 เปิดดู client secret หนึ่งครั้ง

`RevealAsync` หา ticket ด้วย SHA-256 ของ token แล้ว consume ในทรานแซกชันเดียว ไม่มี merchant scope check เพราะ token คือสิทธิ์ (source: `ApiClientEndpoints.cs:102-127`, `ApiClientStore.cs:172-206`, `ApiClient.cs:183-190`)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin Console
    participant API as POST /api-clients/secrets/{ticketId}/reveal
    participant ST as ApiClientStore
    participant DB as DB (iam.OneTimeSecretTickets, ApiClients)

    Note over A,API: Phase A — gate (ไม่มี merchant scope check เพราะ token คือสิทธิ์)
    A->>SPA: เปิดดู client secret ด้วย ticket จาก § 12.4 หรือ § 12.6
    SPA->>API: POST .../reveal + Idempotency-Key + X-CSRF-Token (ดู § 0.1, § 0.3)
    alt Idempotency-Key ผิดรูป
        API-->>SPA: 400 ProblemDetails invalid_idempotency_key
    end
    Note over API,DB: Phase B — consume ticket ในทรานแซกชันเดียว
    API->>ST: RevealAsync(ticketId)
    ST->>DB: transaction, SingleOrDefault(TicketHash = SHA-256(ticketId))
    alt ไม่พบ หรือ ticketId ว่าง / ยาวเกิน 200
        ST-->>API: state Unknown
        API-->>SPA: 410 ProblemDetails secret_ticket_expired
    else Pending และหมดอายุ, หรือ Ready แต่หมดอายุ
        ST-->>API: state Expired
        API-->>SPA: 410 ProblemDetails secret_ticket_expired
    else Pending และยังไม่หมดอายุ
        ST-->>API: state Pending
        API-->>SPA: 409 ProblemDetails secret_ticket_pending
    else Consumed
        ST-->>API: state Consumed
        API-->>SPA: 410 ProblemDetails secret_ticket_consumed
    else Rejected
        ST-->>API: state Rejected
        API-->>SPA: 410 ProblemDetails secret_ticket_rejected
    else Ready และยังไม่หมดอายุ
        ST->>DB: Unprotect ProtectedSecret (DataProtection), ticket.Consume (Version++), SaveChanges
        alt DbUpdateConcurrencyException, consume ชนกัน
            ST-->>API: state Consumed
            API-->>SPA: 410 ProblemDetails secret_ticket_consumed
        else สำเร็จ
            ST-->>API: clientId, clientSecret
            API-->>SPA: 200 clientId + clientSecret, Cache-Control no-store, Pragma no-cache
        end
    end
```

---

## Notes

- Deviations ของ theme นี้อยู่ที่ `12-governance-audit-api-clients.activities.md` ไฟล์นี้ไม่ทำซ้ำ
- Phase ที่มาจาก outbox dispatcher (§ 12.2 Phase E, § 12.6 Phase B/D/E) เกิดหลัง HTTP response แล้ว ไม่มี caller รอ ผลสุดท้ายอ่านได้จาก `GET /api/v1/approvals/{approvalId}`
- § 12.6: ETag ที่ response 202 ตอบกลับ maker เป็น `ClientVersion` ไม่ใช่ version ของ approval checker ต้องอ่าน `GET /api/v1/approvals/{approvalId}` เพื่อรับ ETag v1 ของ approval เองก่อนตัดสิน
- § 12.6 Phase D/E มี 2 เส้นทางที่ dispatcher retry จนครบ 8 ครั้งแล้วต้องหยุดค้าง (ไม่มี resolution อัตโนมัติ) — version drift ระหว่าง Update/Revoke กับ rotation ที่รออนุมัติ และ execution report ของ decision ที่ rejected ที่ `RecordExecution` ปฏิเสธเพราะ Status เป็น Rejected อยู่แล้ว รายละเอียดเต็มอยู่ใน Notes ของ activities.md
- `<br/>` ไม่ได้ใช้ในไฟล์นี้เพราะชื่อ participant สั้นพอในบรรทัดเดียว

**Render**: GitHub / Obsidian / VS Code Mermaid
