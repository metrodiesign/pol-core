# pol-core API — Governance (maker-checker), audit และ API clients (Activity Diagrams)

> Source: `docs/reference/api-endpoints.md` section "Governance และ audit" บรรทัด L298-L303 และ section "API clients" บรรทัด L309-L315 พร้อม source ที่อ้างต่อ § (`src/Api/Api/Governance/GovernanceEndpoints.cs`, `src/Api/Api/Iam/ApiClientEndpoints.cs`, `src/Api/Api/ConcurrencyEtags.cs`, `src/Infrastructure/Persistence/Persistence.ControlPlane/Governance/GovernanceStore.cs`, `ControlPlaneOperationExecutor.cs`, `AuditAnchorRegistration.cs`, `Persistence.ControlPlane/Iam/ApiClientStore.cs`, `ApiClientApprovalExecutor.cs`, `src/Domain/Modules/Governance.Domain/ApprovalRequest.cs`, `src/Domain/Modules/Iam.Domain/ApiClients/ApiClient.cs`)
> Scope: 13 endpoints — อ่าน / ตัดสินคำขอ maker-checker (4), อ่าน audit hash chain (2), จัดการ API client และ one-time secret ticket (7) ทุกตัวอยู่ใต้ policy `admin` ผ่าน IAdminScope
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

GET ทั้ง 4 ตัวใช้โครงเดียวกัน: gate policy admin + permission, typed filter (ไม่ใช่ SFS parser) แล้วกรองด้วย accessible merchants ของ admin ก่อน query, detail ที่ไม่พบหรือนอก scope ตอบ 404 และ detail คืน ETag (source: `src/Api/Api/Governance/GovernanceEndpoints.cs:72-93,184-216,286-335`, `src/Api/Api/Iam/ApiClientEndpoints.cs:13-37,130-136`, `Persistence.ControlPlane/Governance/GovernanceStore.cs:23-70,332-335`, `Persistence.ControlPlane/Iam/ApiClientStore.cs:29-61,246-247`)

```mermaid
flowchart TD
    START((●)) --> GATE["policy admin + permission<br/>settings.manage (approvals) / apikey.manage (api-clients)<br/>ดู § 0.1 (GET ข้าม CSRF filter ดู § 0.3)"]
    GATE --> KIND{"list หรือ detail?"}
    KIND -->|list| FILTER{"typed filter ผ่าน?<br/>page ≥ 1, limit 1..100, guid / instant / status / ความยาว"}
    FILTER -->|no| R400["400 ProblemDetails<br/>code invalid_filter"]
    FILTER -->|yes| SCOPE_L["ApplyAccess / Scope: unrestricted = ทั้งหมด<br/>ไม่งั้น MerchantId อยู่ใน accessible<br/>merchantId นอก scope = ชุดว่าง ไม่ใช่ 403"]
    SCOPE_L --> QUERY["WHERE filters + search<br/>ORDER BY (CreatedAt desc หรือ Name) ThenBy Id<br/>OFFSET / FETCH + LongCount"]
    QUERY --> R200_L["200 PagedResult (items, page, limit, total)<br/>api-clients ไม่คืน client secret"]
    KIND -->|detail| SCOPE_D["ApplyAccess / Scope แล้ว SingleOrDefault(Id)"]
    SCOPE_D --> FOUND{"พบและอยู่ใน scope?"}
    FOUND -->|no| R404["404: approvals = ProblemDetails code not_found,<br/>api-clients = bare NotFound ผ่าน UseStatusCodePages ดู § 0.9"]
    FOUND -->|yes| ETAG["header ETag = vN ดู § 0.5"]
    ETAG --> R200_D["200 ApprovalDetail / ApiClientView"]
    R200_L --> END_S((◉))
    R200_D --> END_S
    R400 --> END_F((◉))
    R404 --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class QUERY,ETAG,R200_L,R200_D,END_S ok
    class R400,R404,END_F fail
    class KIND,FILTER,FOUND gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/approvals` | permission `settings.manage`, filter page / limit / search ≤ 200 / action ≤ 120 / status (pending, approved, rejected, succeeded, failed, unknown) / merchantId guid D / from, to ต้องมี offset และ from ≤ to, 400 คืน `Problem` ตรงจาก endpoint (`GovernanceEndpoints.cs:198-202`), search เป็น guid = Id หรือ TargetId, ORDER BY CreatedAt desc, Id desc |
| GET | `/api/v1/approvals/{approvalId:guid}` | permission `settings.manage`, 404 ProblemDetails `not_found` ชัดเจน, body ApprovalDetail (maker, requiredPermission, targetVersion, decision / execution outcome) + ETag จาก `Version` (`GovernanceEndpoints.cs:208-216`) |
| GET | `/api/v1/api-clients` | permission `apikey.manage`, `RequireCsrf` ต่อไว้แต่ไม่ตรวจ GET, filter page / limit (400 จาก `InvalidRequestException invalid_filter` ผ่าน § 0.9), status ต้องเป็น active / revoked ไม่งั้น 400 `invalid_filter`, search LIKE บน Name หรือ PublicClientId (SfsLike.Escape), ORDER BY Name, Id (`ApiClientStore.cs:29-53`) |
| GET | `/api/v1/api-clients/{clientId:guid}` | permission `apikey.manage`, 404 เป็น `Results.NotFound()` ไม่มี code, body ApiClientView (clientId, scopes, ipPolicy, secretHint, status, rotationPending, version) + ETag (`ApiClientEndpoints.cs:25-37`) |

---

## 12.2 ตัดสินคำขอ maker-checker (approve / reject)

approve และ reject map จาก handler เดียว (`MapDecision`) ต่างกันแค่ `ApprovalDecision` และชื่อ operation, endpoint ตรวจ header + body เองเป็น 400 เดียว แล้ว `GovernanceStore.DecideAsync` ทำ idempotency, scope, permission และกฎ maker-checker ใน transaction เดียว (source: `src/Api/Api/Governance/GovernanceEndpoints.cs:123-182,290-295`, `Persistence.ControlPlane/Governance/GovernanceStore.cs:72-146,351-355`, `src/Domain/Modules/Governance.Domain/ApprovalRequest.cs:74-98`, `src/Application/Modules/Governance.Application/GovernanceHandlers.cs:26-30,64-75`)

```mermaid
flowchart TD
    START((●)) --> GATE["RequireCsrf + policy admin + permission settings.manage<br/>ดู § 0.1 / § 0.3"]
    GATE --> WHICH{"segment?"}
    WHICH -->|approve| INTENT_A["ApprovalDecision.Approve<br/>operation ApproveRequest"]
    WHICH -->|reject| INTENT_R["ApprovalDecision.Reject<br/>operation RejectRequest"]
    INTENT_A --> VALID
    INTENT_R --> VALID{"If-Match รูป quoted vN, Idempotency-Key 1..200 ไม่มี control char,<br/>body reason 1..1000, targetVersion 1..200?"}
    VALID -->|no| R400["400 ProblemDetails Invalid decision request<br/>code invalid_request"]
    VALID -->|yes| TXN["DecideAsync ใน transaction เดียว<br/>app lock governance-operation (actor, operation, key)"]
    TXN --> PRIOR{"OperationRecords มี (actor, operation, key)?"}
    PRIOR -->|"hash ของ intent ต่าง"| R409_K["409 Conflict code idempotency_key_reused<br/>(ConflictException ผ่าน § 0.9)"]
    PRIOR -->|"InProgress หรือไม่มี body"| R409_P["409 code operation_in_progress"]
    PRIOR -->|Succeeded| REPLAY["202 ApprovalDetail เดิม (Replayed = true)<br/>+ ETag จาก detail ที่เก็บ"]
    PRIOR -->|"ไม่มี"| FOUND{"ApprovalRequests มี approvalId?"}
    FOUND -->|no| R404["404 ProblemDetails Approval not found<br/>code not_found (NotFoundException จับที่ endpoint)"]
    FOUND -->|yes| SCOPE{"approval.MerchantId อยู่ใน accessible ของ checker?"}
    SCOPE -->|no| R403_S["403 Decision forbidden<br/>code merchant_scope_forbidden"]
    SCOPE -->|yes| PERM{"Permissions ของ checker มี approval.RequiredPermission?"}
    PERM -->|no| R403_P["403 code underlying_permission_forbidden"]
    PERM -->|yes| RULES{"ApprovalRequest.Decide ดู § 0.7"}
    RULES -->|"checker = maker"| R403_M["403 code maker_cannot_decide"]
    RULES -->|"ไม่ Pending หรือ version stale"| R409_A["409 code approval_not_pending"]
    RULES -->|"targetVersion ต่าง"| R409_T["409 code target_version_changed"]
    RULES -->|ok| WRITE["Status Approved / Rejected, Version++<br/>ApprovalEvent decided + outbox ApprovalDecided<br/>audit approval.decided + OperationRecord 202, commit"]
    WRITE --> R202["202 ApprovalDetail + ETag v2<br/>(Results.Accepted ไม่มี Location)"]
    WRITE -.async.-> DISP["GovernanceOutboxDispatcher → ApprovalDecidedHandler<br/>→ IApprovalDecisionExecutor ตาม TargetType ดู § 0.7 / § 0.8"]
    R202 --> END_S((◉))
    REPLAY --> END_S
    R400 --> END_F((◉))
    R409_K --> END_F
    R409_P --> END_F
    R404 --> END_F
    R403_S --> END_F
    R403_P --> END_F
    R403_M --> END_F
    R409_A --> END_F
    R409_T --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    class WRITE,R202,END_S ok
    class R400,R409_K,R409_P,R404,R403_S,R403_P,R403_M,R409_A,R409_T,END_F fail
    class WHICH,VALID,PRIOR,FOUND,SCOPE,PERM,RULES gate
    class REPLAY,DISP warn
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| POST | `/api/v1/approvals/{approvalId:guid}/approve` | `ApprovalDecision.Approve`, operation `ApproveRequest`, ApprovalDecided.Decision = `approved`, executor ของ target apply การเปลี่ยนแปลง (`GovernanceEndpoints.cs:95`, `GovernanceStore.cs:76,117`) |
| POST | `/api/v1/approvals/{approvalId:guid}/reject` | `ApprovalDecision.Reject`, operation `RejectRequest`, Decision = `rejected`, executor คืน target จาก pending (ดู Notes เรื่อง ApprovalExecutionReported ของ rejected) (`GovernanceEndpoints.cs:96`) |

---

## 12.3 อ่าน audit แบบ append-only พร้อมตรวจ hash chain

audit ต่างจาก § 12.1 ตรงที่ store ตรวจความสมบูรณ์ของ hash chain ก่อนคืนข้อมูล: list ตรวจทุก scope ที่ admin เข้าถึงได้ก่อน query ส่วน detail ตรวจเฉพาะ scope ของ record หลังพบ, ผิดปกติตอบ 503 (source: `src/Api/Api/Governance/GovernanceEndpoints.cs:98-120,218-265`, `Persistence.ControlPlane/Governance/GovernanceStore.cs:198-330,337-340`, `AuditAnchorRegistration.cs:22-46`, `AuditAnchorStore.cs:12-59`, `src/Domain/Modules/Governance.Domain/AuditRecord.cs:109-134`)

```mermaid
flowchart TD
    START((●)) --> GATE["policy admin + permission audit.view ดู § 0.1"]
    GATE --> KIND{"list หรือ detail?"}
    KIND -->|list| FILTER{"page ≥ 1, limit 1..100, actor / merchantId guid,<br/>from / to มี offset และ from ≤ to,<br/>action ≤ 120, resource ≤ 200, result ≤ 80?"}
    FILTER -->|no| R400["400 ProblemDetails Invalid audit filter<br/>code invalid_filter"]
    FILTER -->|yes| HEADS["VerifyAccessibleAsync: AuditHeads ทุก scope ที่ accessible<br/>(unrestricted = ทุก scope รวม platform)"]
    HEADS --> ANCH{"IAuditAnchorStore.IsEnabled?"}
    ANCH -->|yes| READA["ReadAllLatestAsync จากไฟล์ anchor<br/>(HMAC signature chain ต่อบรรทัด)"]
    ANCH -->|no| VERIFY
    READA --> VERIFY["VerifyScopeAsync ต่อ scope:<br/>Sequence ต่อเนื่องจาก 1, PreviousHash = hash ก่อนหน้า, HasValidHash,<br/>head.LastSequence / LastHash ตรง, anchor hash ตรง record ที่ anchored"]
    VERIFY --> OK1{"ทุก scope ผ่าน?"}
    OK1 -->|no| R503["503 ProblemDetails Audit integrity is unhealthy<br/>code audit_integrity_unhealthy"]
    OK1 -->|yes| QUERY["ApplyAccess + filters (resource = ResourceId หรือ ResourceType contains)<br/>ORDER BY OccurredAt desc, Id desc, OFFSET / FETCH"]
    QUERY --> R200_L["200 PagedResult AuditListItem"]
    KIND -->|detail| LOAD["ApplyAccess แล้ว SingleOrDefault(auditId)"]
    LOAD --> FOUND{"พบและอยู่ใน scope?"}
    FOUND -->|no| R404["404 ProblemDetails Audit record not found<br/>code not_found"]
    FOUND -->|yes| VERIFY1["VerifyScopeAsync เฉพาะ record.ScopeKey<br/>(อ่าน anchor เองเมื่อเปิด)"]
    VERIFY1 --> OK2{"chain ของ scope นั้นผ่าน?"}
    OK2 -->|no| R503
    OK2 -->|yes| R200_D["200 AuditDetail: changes (redacted), approvalId,<br/>resourceVersion, previousHash, hash (hex)"]
    R200_L --> END_S((◉))
    R200_D --> END_S
    R400 --> END_F((◉))
    R404 --> END_F
    R503 --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef ext fill:#4a3b0f,stroke:#e3b341,color:#fff
    class QUERY,R200_L,R200_D,END_S ok
    class R400,R404,R503,END_F fail
    class KIND,FILTER,ANCH,OK1,FOUND,OK2 gate
    class READA ext
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/audits` | ตรวจ chain ของทุก scope ที่ accessible ก่อน query (`GovernanceStore.cs:200,267-279`), anchors อ่านครั้งเดียวแล้วส่งต่อทุก scope |
| GET | `/api/v1/audits/{auditId:guid}` | 404 ก่อน แล้วค่อยตรวจ chain เฉพาะ scope ของ record (`GovernanceStore.cs:243-247`), 503 เกิดหลัง 404 |

---

## 12.4 สร้าง API client + Ready secret ticket

สร้าง client ใน merchant ที่ admin เข้าถึงได้ พร้อม one-time ticket สถานะ Ready ที่ถือ secret แบบ protected, plaintext secret ไม่อยู่ใน response ต้องไปเปิดที่ § 12.7 (source: `src/Api/Api/Iam/ApiClientEndpoints.cs:39-52`, `src/Api/Api/ConcurrencyEtags.cs:31-42`, `Persistence.ControlPlane/Iam/ApiClientStore.cs:63-94,225-236,253-278`, `ControlPlaneOperationExecutor.cs:54-82`, `src/Domain/Modules/Iam.Domain/ApiClients/ApiClient.cs:27-49,138-149`)

```mermaid
flowchart TD
    START((●)) --> GATE["RequireCsrf + policy admin + permission apikey.manage<br/>ดู § 0.1 / § 0.3"]
    GATE --> KEY{"Idempotency-Key ไม่ว่าง, ≤ 200, ไม่มี control char?"}
    KEY -->|no| R400_K["400 ProblemDetails<br/>code invalid_idempotency_key"]
    KEY -->|yes| ACC{"body.merchantId อยู่ใน scope ของ admin?"}
    ACC -->|no| R403["403 ProblemDetails Forbidden<br/>(AccessDeniedException ผ่าน § 0.9)"]
    ACC -->|yes| SCOPES{"scopes ไม่ว่าง, ไม่ซ้ำ, อยู่ใน allowlist<br/>payments:create, payments:read, refunds:create, refunds:read,<br/>webhooks:read, settlements:read?"}
    SCOPES -->|no| R400_S["400 code invalid_scope"]
    SCOPES -->|yes| IP{"ipPolicy ว่าง หรือ CIDR list ≤ 64 รายการ parse ได้?"}
    IP -->|no| R400_I["400 code validation_failed"]
    IP -->|yes| EXEC["ControlPlaneOperationExecutor ดู § 0.5<br/>operation api-client.create, scope merchant<br/>app lock admin-operation (actor, operation, key)"]
    EXEC --> PRIOR{"OperationRecords มี (actor, operation, key)?"}
    PRIOR -->|"hash ต่าง"| R409_K["409 code idempotency_key_reused"]
    PRIOR -->|"ยังไม่ Succeeded"| R409_P["409 code operation_in_progress"]
    PRIOR -->|Succeeded| REPLAY["201 body เดิมจาก ResponseBody (Replayed = true)<br/>รวม ticketId เดิม"]
    PRIOR -->|"ไม่มี"| GEN["clientId = cli_live_ + 12 bytes, secret = pol_ + 32 bytes<br/>SecretHash = HMAC-SHA256(VaultKeyring.Active), hint = 4 ตัวท้าย"]
    GEN --> SAVE["ApiClient Active v1 + OneTimeSecretTicket Ready<br/>(ProtectedSecret ผ่าน DataProtection, หมดอายุ 10 นาที)<br/>OperationRecord 201, commit"]
    SAVE --> R201["201 Location /api/v1/api-clients/{id}<br/>Cache-Control no-store, ETag v1<br/>body client + secretTicket (ticketId, expiresAt) ไม่มี plaintext"]
    R201 --> END_S((◉))
    REPLAY --> END_S
    R400_K --> END_F((◉))
    R403 --> END_F
    R400_S --> END_F
    R400_I --> END_F
    R409_K --> END_F
    R409_P --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    classDef ext fill:#4a3b0f,stroke:#e3b341,color:#fff
    class SAVE,R201,END_S ok
    class R400_K,R403,R400_S,R400_I,R409_K,R409_P,END_F fail
    class KEY,ACC,SCOPES,IP,PRIOR gate
    class REPLAY warn
    class GEN ext
```

---

## 12.5 แก้ไข / เพิกถอน API client

mutation ตรงสองตัวใช้ If-Match + Idempotency-Key ผ่าน executor เดียวกัน: update ตรวจ scopes / ipPolicy ก่อนหา client และปฏิเสธ client ที่ revoked, revoke ไม่มี body และ idempotent เมื่อ revoked อยู่แล้ว (source: `src/Api/Api/Iam/ApiClientEndpoints.cs:54-82`, `src/Api/Api/ConcurrencyEtags.cs:18-42`, `Persistence.ControlPlane/Iam/ApiClientStore.cs:96-133,238-241,258-278`, `src/Domain/Modules/Iam.Domain/ApiClients/ApiClient.cs:51-60,92-100`)

```mermaid
flowchart TD
    START((●)) --> GATE["RequireCsrf + policy admin + permission apikey.manage<br/>ดู § 0.1 / § 0.3"]
    GATE --> IFM{"If-Match รูป quoted vN?"}
    IFM -->|no| R400_E["400 code invalid_etag"]
    IFM -->|yes| KEY{"Idempotency-Key ถูกรูป?"}
    KEY -->|no| R400_K["400 code invalid_idempotency_key"]
    KEY -->|yes| WHICH{"endpoint?"}
    WHICH -->|"PUT /{clientId}"| VAL{"scopes อยู่ใน allowlist และ ipPolicy parse ได้?"}
    VAL -->|no| R400_V["400 code invalid_scope / validation_failed"]
    VAL -->|yes| LOOK
    WHICH -->|"POST /{clientId}/revoke"| LOOK["MerchantIdAsync: client อยู่ใน scope?"]
    LOOK --> FOUND{"พบ?"}
    FOUND -->|no| R404["404 bare NotFound<br/>(UseStatusCodePages ดู § 0.9)"]
    FOUND -->|yes| EXEC["ControlPlaneOperationExecutor ดู § 0.5<br/>operation api-client.update / api-client.revoke"]
    EXEC --> PRIOR{"OperationRecords มี (actor, operation, key)?"}
    PRIOR -->|"hash ต่าง"| R409_K["409 code idempotency_key_reused"]
    PRIOR -->|"ยังไม่ Succeeded"| R409_P["409 code operation_in_progress"]
    PRIOR -->|Succeeded| REPLAY["200 body เดิม (Replayed = true) + ETag"]
    PRIOR -->|"ไม่มี"| VER{"row.Version = If-Match?"}
    VER -->|no| R409_C["409 Conflict<br/>ConcurrencyConflictException API client changed"]
    VER -->|yes| OP{"operation?"}
    OP -->|update| REV{"Status Revoked?"}
    REV -->|yes| R409_S["409 code state_conflict<br/>Revoked API client is immutable"]
    REV -->|no| UPD["Name, ScopesCsv (sorted), IpPolicy, Version++"]
    OP -->|revoke| RVK["Status Revoked, ล้าง pending rotation, Version++<br/>(revoked อยู่แล้ว = no-op ไม่ bump)"]
    UPD --> SAVE["OperationRecord 200, commit"]
    RVK --> SAVE
    SAVE --> R200["200 ApiClientMutation + ETag vN ใหม่"]
    R200 --> END_S((◉))
    REPLAY --> END_S
    R400_E --> END_F((◉))
    R400_K --> END_F
    R400_V --> END_F
    R404 --> END_F
    R409_K --> END_F
    R409_P --> END_F
    R409_C --> END_F
    R409_S --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    class UPD,RVK,SAVE,R200,END_S ok
    class R400_E,R400_K,R400_V,R404,R409_K,R409_P,R409_C,R409_S,END_F fail
    class IFM,KEY,WHICH,VAL,FOUND,PRIOR,VER,OP,REV gate
    class REPLAY warn
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| PUT | `/api/v1/api-clients/{clientId:guid}` | body name / scopes / ipPolicy ตรวจก่อนหา client (400 มาก่อน 404), operation `api-client.update`, revoked = 409 `state_conflict`, ไม่เปลี่ยน clientId หรือ secret (`ApiClientStore.cs:96-115`) |
| POST | `/api/v1/api-clients/{clientId:guid}/revoke` | ไม่มี body, operation `api-client.revoke`, ล้าง PendingRotationApprovalId / TicketId, revoked อยู่แล้ว = 200 โดยไม่ bump version, หลังสำเร็จ `VerifyAsync` ไม่รับ client นี้อีก (`ApiClientStore.cs:117-133,208-213`) |

---

## 12.6 ขอหมุน client secret (maker) + executor async

maker ยื่นคำขอแบบ maker-checker: stage ticket Pending อายุ 24 ชั่วโมงและ mark client ว่ามี rotation ค้าง แล้วเขียน ApprovalRequested ลง governance outbox ใน transaction เดียว, secret ใหม่ถูกสร้างเฉพาะตอน executor apply หลัง checker approve (source: `src/Api/Api/Iam/ApiClientEndpoints.cs:84-100`, `Persistence.ControlPlane/Iam/ApiClientStore.cs:135-170`, `ApiClientApprovalExecutor.cs:25-80`, `Persistence.ControlPlane/Governance/GovernanceStore.cs:148-196`, `GovernanceOutboxDispatcher.cs:111-138`, `src/Domain/Modules/Iam.Domain/ApiClients/ApiClient.cs:62-90,151-181`, `src/Domain/Modules/Governance.Domain/ApprovalRequest.cs:100-113`)

```mermaid
flowchart TD
    START((●)) --> GATE["RequireCsrf + policy admin + permission apikey.manage<br/>ดู § 0.1 / § 0.3"]
    GATE --> IFM{"If-Match รูป quoted vN?"}
    IFM -->|no| R400_E["400 code invalid_etag"]
    IFM -->|yes| KEY{"Idempotency-Key ถูกรูป?"}
    KEY -->|no| R400_K["400 code invalid_idempotency_key"]
    KEY -->|yes| LOOK["MerchantIdAsync: client อยู่ใน scope?"]
    LOOK --> FOUND{"พบ?"}
    FOUND -->|no| R404["404 bare NotFound (UseStatusCodePages ดู § 0.9)"]
    FOUND -->|yes| EXEC["ControlPlaneOperationExecutor ดู § 0.5<br/>operation api-client.secret.rotate"]
    EXEC --> PRIOR{"OperationRecords มี (actor, operation, key)?"}
    PRIOR -->|"hash ต่าง"| R409_K["409 code idempotency_key_reused"]
    PRIOR -->|"ยังไม่ Succeeded"| R409_P["409 code operation_in_progress"]
    PRIOR -->|Succeeded| REPLAY["202 body เดิม (Replayed = true) + ETag"]
    PRIOR -->|"ไม่มี"| VER{"row.Version = If-Match?"}
    VER -->|no| R409_C["409 Conflict ConcurrencyConflictException"]
    VER -->|yes| STATE{"Status Active และไม่มี PendingRotationApprovalId?"}
    STATE -->|no| R409_S["409 code state_conflict<br/>Only active API clients can rotate secrets /<br/>A secret rotation is already pending"]
    STATE -->|yes| STAGE["approvalId (Guid v7) + ticket token 32 bytes<br/>OneTimeSecretTicket Pending (ApprovalId, หมดอายุ 24 ชม.)<br/>row.RequestRotation: PendingRotationApprovalId + TicketId, Version++"]
    STAGE --> OUT["outbox ApprovalRequested: scope merchant, action api-client.secret.rotate,<br/>requiredPermission apikey.manage, targetType api-client-secret,<br/>targetVersion = v(Version หลัง bump), OperationRecord 202, commit"]
    OUT --> R202["202 Location /api/v1/approvals/{approvalId}<br/>Cache-Control no-store, ETag = clientVersion<br/>body approvalId, secretTicket (ticketId, expiresAt), status pending"]
    OUT -.async.-> GOV["GovernanceOutboxDispatcher → GovernanceStore.ReceiveAsync<br/>ApprovalRequest Pending v1 + audit approval.created ดู § 0.7"]
    GOV --> DECIDE["checker (คนละคนกับ maker) approve / reject ดู § 12.2"]
    DECIDE -.async.-> EXE["ApiClientApprovalExecutor (TargetType api-client-secret) ใน transaction:<br/>client.PendingRotationApprovalId = approvalId, TargetVersion = v(client.Version),<br/>มี PendingRotationTicketId?"]
    EXE --> MATCH{"ตรง?"}
    MATCH -->|no| CC["ConcurrencyConflictException API-client rotation target changed<br/>→ message MarkFailed, retry จนถึง 8 ครั้ง ดู § 0.8"]
    MATCH -->|yes| DEC{"decision?"}
    DEC -->|approved| APPLY["secret ใหม่ pol_ + 32 bytes, SecretHash = HMAC(VaultKeyring.Active), hint ใหม่<br/>client.CompleteRotation (Version++), ticket.Activate = Ready หมดอายุ 10 นาทีนับจาก activate<br/>outbox ApprovalExecutionReported succeeded, outcome api_client_secret_rotated"]
    DEC -->|rejected| REVERT["ticket.Reject, client.RejectRotation (Version++)<br/>outbox ApprovalExecutionReported succeeded = false,<br/>outcome api_client_secret_rotation_rejected"]
    APPLY -.async.-> FINAL["GovernanceStore.ReceiveAsync → ApprovalRequest Succeeded v3<br/>audit approval.executed"]
    REVERT -.async.-> POISON["GovernanceStore.RecordExecution โยน approval_rejected (Status Rejected)<br/>message ล้มซ้ำจนครบ 8 ครั้ง, ApprovalRequest ค้าง rejected v2 (ดู Notes)"]
    R202 --> END_S((◉))
    REPLAY --> END_S
    FINAL --> END_S
    R400_E --> END_F((◉))
    R400_K --> END_F
    R404 --> END_F
    R409_K --> END_F
    R409_P --> END_F
    R409_C --> END_F
    R409_S --> END_F
    CC --> END_F
    POISON --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    classDef ext fill:#4a3b0f,stroke:#e3b341,color:#fff
    class STAGE,OUT,GOV,APPLY,FINAL,END_S ok
    class R400_E,R400_K,R404,R409_K,R409_P,R409_C,R409_S,CC,POISON,END_F fail
    class IFM,KEY,FOUND,PRIOR,VER,STATE,MATCH,DEC gate
    class R202,REPLAY,DECIDE,REVERT warn
    class EXE ext
```

| สถานะ OneTimeSecretTicket | เปลี่ยนโดย | ผลต่อ § 12.7 | source |
| --- | --- | --- | --- |
| Ready (create) | `CreateReady` ตอน § 12.4, หมดอายุ 10 นาที | reveal ได้ทันที | `ApiClient.cs:138-149` |
| Pending (rotation) | `CreatePending` ตอนยื่นคำขอ, หมดอายุ 24 ชม. | 409 `secret_ticket_pending` (หมดอายุก่อนตัดสิน = 410) | `ApiClient.cs:151-162` |
| Ready (rotation) | `Activate` โดย executor เมื่อ approved, หมดอายุ 10 นาทีนับใหม่ | reveal ได้ | `ApiClient.cs:164-172` |
| Rejected | `Reject` โดย executor เมื่อ rejected | 410 `secret_ticket_rejected` | `ApiClient.cs:174-181` |
| Consumed | `Consume` ตอน reveal สำเร็จ | 410 `secret_ticket_consumed` | `ApiClient.cs:183-190` |

---

## 12.7 เปิดดู client secret หนึ่งครั้ง

consume one-time ticket ด้วย token ที่ได้จาก § 12.4 หรือ § 12.6: หา ticket ด้วย SHA-256 ของ token, ตัดสินจากสถานะ + วันหมดอายุ แล้ว unprotect secret และ mark Consumed ใน transaction เดียว, ไม่มี merchant scope check เพราะ token คือสิทธิ์ (source: `src/Api/Api/Iam/ApiClientEndpoints.cs:102-127`, `Persistence.ControlPlane/Iam/ApiClientStore.cs:172-206`, `src/Domain/Modules/Iam.Domain/ApiClients/ApiClient.cs:183-190`)

```mermaid
flowchart TD
    START((●)) --> GATE["RequireCsrf + policy admin + permission apikey.manage ดู § 0.1 / § 0.3<br/>ไม่มี merchant scope check (ticket token คือสิทธิ์)"]
    GATE --> KEY{"Idempotency-Key ถูกรูป?<br/>(ตรวจอย่างเดียว ไม่เก็บ OperationRecord)"}
    KEY -->|no| R400["400 code invalid_idempotency_key"]
    KEY -->|yes| TKT{"ticketId ไม่ว่างและ ≤ 200?"}
    TKT -->|no| UNKNOWN
    TKT -->|yes| LOOK["transaction: SingleOrDefault(TicketHash = SHA-256(ticketId))"]
    LOOK --> ST{"สถานะ ticket?"}
    ST -->|"ไม่พบ"| UNKNOWN["Unknown"]
    ST -->|"Pending และ ExpiresAt ≤ now"| EXPIRED["Expired"]
    ST -->|Pending| R409["409 ProblemDetails code secret_ticket_pending<br/>(รอ checker ดู § 12.6)"]
    ST -->|Consumed| R410_C["410 code secret_ticket_consumed"]
    ST -->|Rejected| R410_R["410 code secret_ticket_rejected"]
    ST -->|"Ready แต่ ExpiresAt ≤ now"| EXPIRED
    ST -->|Ready| CONSUME["Unprotect ProtectedSecret (DataProtection)<br/>ticket.Consume (Version++), SaveChanges"]
    UNKNOWN --> R410_E["410 code secret_ticket_expired"]
    EXPIRED --> R410_E
    CONSUME --> RACE{"DbUpdateConcurrencyException<br/>(consume ชนกันบน Version)?"}
    RACE -->|yes| R410_C
    RACE -->|no| R200["200 clientId + clientSecret<br/>Cache-Control no-store, Pragma no-cache"]
    R200 --> END_S((◉))
    R400 --> END_F((◉))
    R409 --> END_F
    R410_C --> END_F
    R410_R --> END_F
    R410_E --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    class CONSUME,R200,END_S ok
    class R400,R409,R410_C,R410_R,R410_E,END_F fail
    class KEY,TKT,ST,RACE gate
    class UNKNOWN,EXPIRED warn
```

---

## Deviations

| fullPath | เอกสารบอก | source บอก | อ้างอิง |
| --- | --- | --- | --- |
| ไม่พบ deviation ระหว่างเอกสารกับ source | - | policy `admin`, permission (`settings.manage` / `audit.view` / `apikey.manage`) และ CSRF filter ทั้ง 13 แถวตรงกับ chain ใน endpoint, `RequireCsrf` บน GET api-clients ต่อไว้จริงแต่ไม่ตรวจ safe method | `GovernanceEndpoints.cs:72-121,168`, `ApiClientEndpoints.cs:19,32,47,62,77,94,122`, `Admins/CsrfFilter.cs` (ดู § 0.3) |

## Notes

| เรื่อง | ข้อเท็จจริงจาก source | source |
| --- | --- | --- |
| rejected decision ทำให้ report message ค้าง | executor commit การ revert แล้ว enqueue `ApprovalExecutionReported(succeeded false)` แต่ `GovernanceStore.ReceiveAsync` เรียก `RecordExecution` ซึ่งโยน `approval_rejected` เมื่อ Status เป็น Rejected, dispatcher `MarkFailed` แล้ว retry จนครบ 8 ครั้งแล้วหยุด lease, `ApprovalRequest` ค้าง `rejected` v2 โดย ExecutionOutcome เป็น null, ไม่มี test ครอบ path นี้ (test ตรวจเฉพาะ outcome ที่ executor เขียน) และ § 0.7 วาด REVERT → REPORT → FINAL ซึ่งไม่ตรงกับ source | `ApprovalRequest.cs:100-103`, `ApiClientApprovalExecutor.cs:41-48`, `GovernanceStore.cs:175-196`, `GovernanceOutboxDispatcher.cs:128-131`, `tests/ArchitectureTests/Architecture.Tests/AdminPaymentsApprovalExecutorTests.cs:143-157` |
| version drift ระหว่างรอ approve | `Update` / `Revoke` ไม่ block เมื่อมี rotation ค้าง, ทุก mutation bump `Version` ทำให้ `decision.TargetVersion != v{client.Version}` ตอน executor และโยน ConcurrencyConflict, message ApprovalDecided retry จนครบ 8 ครั้ง, ApprovalRequest ค้าง `approved` v2 | `ApiClient.cs:51-60,92-100`, `ApiClientApprovalExecutor.cs:35-38` |
| `ValidateKey` ใน executor ไม่ถึง | `ControlPlaneOperationExecutor.ValidateKey` (code `validation_failed`) อยู่หลัง `IdempotencyKeys.Require` (code `invalid_idempotency_key`) ที่ endpoint จึงไม่มีทางถูกเรียกจาก endpoint ในไฟล์นี้ | `ControlPlaneOperationExecutor.cs:93-97`, `ConcurrencyEtags.cs:33-41` |
| OperationRecord TTL | record หมดอายุ 24 ชม. และถูก prune ทุก 1 ชม. (`OperationRecordPruneService`), replay ของ create คืน ticketId เดิมจาก ResponseBody ซึ่ง ticket อาจหมดอายุหรือ consumed ไปแล้ว | `ControlPlaneOperationExecutor.cs:75-76`, `GovernanceOutboxDispatcher.cs:152-194`, `ApiClientStore.cs:82-86` |
| audit anchor | Development / Testing ใช้ `DisabledAuditAnchorStore` (ข้ามการเทียบ anchor), environment อื่นต้องตั้ง `AuditAnchor:Path` + `AuditAnchor:SigningKeyFile` ไม่งั้น boot ล้ม, `AuditAnchorService` เขียน checkpoint ทุก 5 วินาทีและ readiness check `audit-anchor` | `AuditAnchorRegistration.cs:22-46`, `AuditAnchorService.cs:43-125` |
| ต้นทุนการอ่าน audit | list re-hash ทุก record ของทุก scope ที่ accessible ต่อ request (unrestricted admin = ทุก scope), detail re-hash เฉพาะ scope ของ record | `GovernanceStore.cs:267-314` |
| body model validation | `[Required]` บน `DecisionRequest`, `CreateApiClientRequest`, `UpdateApiClientRequest` เป็น attribute ของ record, ผลของ minimal API model validation อยู่นอก frame (ไม่พบ pipeline ที่อ่านใน source ชุดนี้) | `GovernanceEndpoints.cs:344-346`, `ApiClientEndpoints.cs:139-142` |
| reveal ไม่มี scope | `RevealAsync` รับเฉพาะ ticket string ไม่รับ `Access(scope)`, admin ที่มี `apikey.manage` และถือ token เปิดได้ทุก merchant, Idempotency-Key ตรวจแล้วทิ้ง เรียกซ้ำ = 410 `secret_ticket_consumed` | `ApiClientEndpoints.cs:102-106`, `ApiClientStore.cs:172-206` |
| ETag ของ 202 rotation | ETag ตอบ `ClientVersion` (version ของ client หลัง RequestRotation) ไม่ใช่ version ของ ApprovalRequest, checker ต้องอ่าน `GET /api/v1/approvals/{approvalId}` เพื่อรับ ETag v1 ของ approval ก่อนตัดสิน | `ApiClientEndpoints.cs:92`, `ApiClientStore.cs:165-167` |

**Render**: GitHub / Obsidian / VS Code Mermaid
