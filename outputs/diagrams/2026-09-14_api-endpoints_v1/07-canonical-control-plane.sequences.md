# pol-core API — Canonical control plane (Sequence Diagrams)

> Source: `docs/reference/api-endpoints.md` section "Canonical control plane" บรรทัด L146-L173 และ source ที่อ้างต่อ § (`src/Api/Api/ControlPlane/CanonicalMerchantConfigurationEndpoints.cs`, `src/Api/Api/ControlPlane/CanonicalProviderConfigurationEndpoints.cs`, `src/Infrastructure/Persistence/Persistence.ControlPlane/Merchants/AdminMerchantControlStore.cs`, `Persistence.ControlPlane/Payments/AdminPaymentsControlStore.cs`, `Persistence.ControlPlane/Governance/GovernanceStore.cs`)
> Scope: 6 § เดียวกับ `07-canonical-control-plane.activities.md` (หมายเลข § ตรงกัน) แสดงลำดับข้าม Console / API / Store / DB / Vault / PSP / Governance outbox
> Generated: 2026-09-14

| § | Diagram | Endpoints |
| --- | --- | --- |
| 7.1 | อ่าน Merchant / Branch / Sale / Provider Account / catalog (single + list) | `GET /api/v1/merchants/{merchantId:guid}`, `GET /api/v1/merchants/{merchantId:guid}/branches`, `GET /api/v1/merchants/{merchantId:guid}/payment-setting-requests`, `GET /api/v1/merchants/{merchantId:guid}/payment-setting-requests/{requestId:guid}`, `GET /api/v1/merchants/{merchantId:guid}/payment-settings`, `GET /api/v1/merchants/{merchantId:guid}/provider-accounts`, `GET /api/v1/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}`, `GET /api/v1/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}/credential-versions`, `GET /api/v1/merchants/{merchantId:guid}/sales`, `GET /api/v1/payment-providers`, `GET /api/v1/payment-providers/{providerId:guid}/methods` |
| 7.2 | เขียน Merchant / Branch / Sale (create + patch) | `PATCH /api/v1/merchants/{merchantId:guid}`, `POST /api/v1/merchants/{merchantId:guid}/branches`, `PATCH /api/v1/merchants/{merchantId:guid}/branches/{branchId:guid}`, `POST /api/v1/merchants/{merchantId:guid}/sales`, `PATCH /api/v1/merchants/{merchantId:guid}/sales/{saleId:guid}` |
| 7.3 | เขียน Provider Account (create / patch / disable) | `POST /api/v1/merchants/{merchantId:guid}/provider-accounts`, `PATCH /api/v1/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}`, `POST /api/v1/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}/disable` |
| 7.4 | ทดสอบการเชื่อมต่อ Provider Account | `POST /api/v1/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}/connection-tests` |
| 7.5 | สร้างคำขอเปลี่ยน payment setting (maker) | `POST /api/v1/merchants/{merchantId:guid}/payment-setting-requests`, `POST /api/v1/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}/credential-versions` |
| 7.6 | อนุมัติ / ปฏิเสธ payment setting request (checker) | `POST /api/v1/merchants/{merchantId:guid}/payment-setting-requests/{requestId:guid}/approve`, `POST /api/v1/merchants/{merchantId:guid}/payment-setting-requests/{requestId:guid}/reject` |

---

## 7.1 อ่าน Merchant / Branch / Sale / Provider Account / catalog

sequence เดียวครอบทุก GET ของ theme — ต่างกันที่มี paging หรือไม่ และ scope check เป็น throw หรือ merge เงียบ (ดูรายละเอียดต่อ endpoint ใน activities.md) (source เดียวกับ § 7.1 ของ activities.md)

```mermaid
sequenceDiagram
    autonumber
    actor Admin
    participant Console as Admin Console
    participant API
    participant Store as Merchant / Payments / Governance store
    participant DB

    Note over Admin,API: Phase A — gate ดู § 0.1, ไม่มี CSRF เพราะเป็น GET
    Admin->>Console: เปิดหน้า merchant / provider account / setting request / catalog
    Console->>API: GET .../{id} หรือ GET .../list?page&limit

    alt list พร้อม page/limit
        API->>API: ValidatePage(page, limit)
        API-->>Console: 400 ProblemDetails code invalid_filter (เมื่อผิดรูป)
    else single หรือ catalog ไม่มี paging
        Note over API: ไม่มีขั้น ValidatePage
    end

    Note over API,DB: Phase B — scope check + lookup
    alt scope check แบบ EnsureAccess throw (merchant, branches, sales, provider-accounts, credential-versions)
        API->>Store: EnsureAccess(merchantId)
        Store-->>API: throw AdminMerchantAccessDeniedException / AdminPaymentsAccessDeniedException
        API-->>Console: 403 ProblemDetails code merchant_scope_forbidden
    else scope check แบบ merge เข้า lookup (provider-account, payment-settings, setting-request)
        API->>Store: lookup(id, access)
        Store->>DB: query กรองด้วย access.Allows หรือ ApplyAccess
        DB-->>Store: row หรือ null
        Store-->>API: view หรือ null
    else catalog ไม่มี scope check (payment-providers, methods)
        API->>API: อ่าน static catalog จาก IPspAdapterFactory
    end

    alt ไม่พบ
        API-->>Console: 404 — Problem+code not_found (merchant, provider-account) หรือ Results.NotFound() เปล่า (setting-request, payment-settings, credential-versions, provider methods)
    else พบ
        API-->>Console: 200 (+ ETag ถ้ามี EtagResponseMarker)
    end
```

---

## 7.2 เขียน Merchant / Branch / Sale (create + patch)

POST ตรวจ parent + duplicate code เป็น 409, PATCH โหลด target ก่อนแล้วเทียบ version — version ไม่ตรงในกลุ่มนี้ตอบ **412** เสมอ (endpoint filter เฉพาะโมดูล ไม่ใช่ 409 ของ § 0.5 ทั่วไป) (source: `CanonicalMerchantConfigurationEndpoints.cs:26-266`, `AdminMerchantControlStore.cs:124-344`)

```mermaid
sequenceDiagram
    autonumber
    actor Admin
    participant Console as Admin Console
    participant API
    participant Store as AdminMerchantControlStore
    participant DB as DB merch.Merchants / Branches / Sales

    Note over Admin,API: Phase A — gate ดู § 0.1 / § 0.3, header
    Admin->>Console: แก้ไข หรือ สร้าง merchant / branch / sale
    Console->>API: POST หรือ PATCH .../{id} + Idempotency-Key (+ If-Match เฉพาะ PATCH) + X-CSRF-Token

    alt header ผิดรูป
        API-->>Console: 400 ProblemDetails code invalid_etag / invalid_idempotency_key
    else header ผ่าน
        Note over API,Store: Phase B — access + idempotency
        API->>Store: EnsureAccess(merchantId)
        alt นอก scope
            Store-->>API: throw AdminMerchantAccessDeniedException
            API-->>Console: 403 ProblemDetails code merchant_scope_forbidden
        else ผ่าน
            opt POST เท่านั้น
                API->>DB: EnsureMerchantExistsAsync (+ EnsureBranchAsync สำหรับ sale)
                DB-->>API: 404 (ไม่มี code) หรือ 400 cross_merchant_reference หรือ ผ่าน
            end
            API->>Store: FindOperationAsync(actor, operation, key, intentHash)
            alt มี OperationRecord เดิม
                Store-->>API: hash ต่าง (409 idempotency_key_reused) หรือ ยังไม่ Succeeded (409 operation_in_progress) หรือ Succeeded (replay)
                API-->>Console: 409 ProblemDetails หรือ response เดิม (Replayed)
            else ไม่มี record
                Note over API,DB: Phase C — POST เช็ค duplicate, PATCH โหลด+ตรวจ version
                alt POST สร้าง
                    API->>DB: เช็ค code ซ้ำใน merchant นี้
                    DB-->>API: 409 code_exists หรือ ผ่าน
                    API->>DB: สร้าง entity + OperationRecord.Complete(201) + commit
                    API-->>Console: 201 Created + ETag v1
                else PATCH แก้
                    API->>DB: load target ตาม id + merchantId
                    DB-->>API: 404 (NotFoundException ไม่มี code) หรือ entity
                    API->>API: EnsureVersion(actual, If-Match)
                    alt version ไม่ตรง
                        API-->>Console: 412 ProblemDetails code precondition_failed
                    else ตรง
                        opt sale เปลี่ยน BranchId
                            API->>API: ต้องมี reason (400 reason_required) + EnsureBranchAsync (400 cross_merchant_reference)
                            API->>DB: raw SQL UPDATE Sales SET BranchId WHERE Version = expected
                            DB-->>API: เปลี่ยนไม่ครบ 1 แถว = 412 code precondition_failed
                        end
                        API->>DB: mutate field อื่น (name/status) + OperationRecord.Complete(200) + commit
                        API-->>Console: 200 + ETag vN
                    end
                end
            end
        end
    end
```

---

## 7.3 เขียน Provider Account (create / patch / disable)

create เริ่ม disabled ไม่มี method จึงไม่ชน bug ด้านล่าง, validate เสมอก่อน idempotency lookup (hash ต้องใช้ค่าที่ validate แล้ว), patch/disable ผ่าน `authorizationLease` ก่อน sync methods — sync methods ที่ตั้ง `IsEnabled=false` พร้อม method เดิมโยน exception ที่ไม่ถูก catch (500) ดูรายละเอียดใน Notes ของ activities.md (source: `CanonicalProviderConfigurationEndpoints.cs:76-241`, `AdminPaymentsControlStore.cs:577-666,1606-1642`)

```mermaid
sequenceDiagram
    autonumber
    actor Admin
    participant Console as Admin Console
    participant API
    participant Store as AdminPaymentsControlStore
    participant DB as DB PspConnections / MerchantProviderAccountMethods

    Note over Admin,API: Phase A — gate ดู § 0.1 / § 0.3, existence ก่อน header
    Admin->>Console: สร้าง / แก้ / ปิด provider account
    Console->>API: POST create, PATCH หรือ POST disable + Idempotency-Key (+ If-Match เว้น create) + X-CSRF-Token
    opt disable เท่านั้น
        API->>API: reason ไม่ว่าง มิฉะนั้น 400 code reason_required
    end
    opt patch / disable
        API->>Store: GetConnectionAsync(id, merchantId)
        Store-->>API: 404 (NotFoundException ไม่มี code) เมื่อไม่พบ
    end

    alt header ผิดรูป
        API-->>Console: 400 ProblemDetails code invalid_etag / invalid_idempotency_key
    else header ผ่าน
        API->>Store: EnsureAccess(merchantId)
        Note over API,Store: patch/disable — unreachable จริง เพราะ GetConnectionAsync (PRE) merge-check access ไปแล้ว, นอก scope โดน 404 จาก PRE ก่อนเสมอ, เกิดได้จริงเฉพาะ create
        alt นอก scope (เกิดได้จริงเฉพาะ create)
            Store-->>API: throw AdminPaymentsAccessDeniedException
            API-->>Console: 403 ProblemDetails code merchant_scope_forbidden
        else ผ่าน
            Note over API,Store: Phase B — validate ตาม kind ก่อน idempotency
            alt create
                API->>Store: LoadMerchantAsync (404) + displayName/config + ParseProviderId
                Store-->>API: 400 validation_failed/invalid_psp_config หรือ ผ่าน
                API->>Store: LoadProviderAsync + ParseEnvironment
                Store-->>API: 500 (row ไม่พบ ไม่ถูก catch), 409 provider_disabled, 400 validation_failed หรือ ผ่าน
            else patch / disable
                API->>Store: ValidateMethods + ValidateConfig
                Store-->>API: 400 validation_failed/invalid_psp_config หรือ ผ่าน
            end
            API->>Store: FindOperationAsync(actor, psp.account.create / psp.update, key)
            alt มี OperationRecord เดิม
                Store-->>API: hash ต่าง (409 idempotency_key_reused) หรือ ยังไม่ Succeeded (409 operation_in_progress) หรือ Succeeded (replay)
                API-->>Console: 409 ProblemDetails หรือ response เดิม (Replayed)
            else ไม่มี record
                alt create
                    API->>DB: มี provider account ของ psp นี้แล้ว?
                    DB-->>API: 409 psp_connection_exists หรือ ผ่าน
                    Store->>DB: Connection.Create (disabled, ไม่มี secret) + SyncAccountMethodsAsync([])
                else patch / disable
                    API->>Store: authorizationLease.VerifyAsync
                    Store-->>API: 403 code authorization_stale (ไม่ผ่าน HandleKnownErrors) เมื่อล้มเหลว
                    API->>API: EnsureVersion (412)
                    API->>Store: LoadProviderAsync ของ connection.Psp
                    Store-->>API: 500 (row ไม่พบ ไม่ถูก catch) หรือ ผ่าน
                    Store->>Store: connection.Update(methods, config, isEnabled) — set IsEnabled ทันที
                    Store->>Store: SyncAccountMethodsAsync(methods)
                    alt methods ไม่ว่าง และ IsEnabled กลายเป็น false
                        Store-->>API: throw PaymentCapabilityUnavailableException (ไม่ถูก catch)
                        API-->>Console: 500 default (ไม่ตั้งใจ — ดู Notes)
                    else ผ่าน
                        Store->>DB: sync MerchantProviderAccountMethods ปกติ
                    end
                end
                Store->>DB: OperationRecord.Complete + commit
                API-->>Console: create 201 Created + ETag v1, patch/disable 200 + ETag vN
            end
        end
    end
```

---

## 7.4 ทดสอบการเชื่อมต่อ Provider Account

probe เกิดนอก transaction ก่อนเปิด transaction ที่สองมาบันทึกผล เพื่อไม่ถือ lock ระหว่างรอ PSP ตอบ (source: `CanonicalProviderConfigurationEndpoints.cs:193-213`, `AdminPaymentsControlStore.cs:668-721`)

```mermaid
sequenceDiagram
    autonumber
    actor Admin
    participant Console as Admin Console
    participant API
    participant Store as AdminPaymentsControlStore
    participant Vault
    participant PSP as PSP adapter (external)
    participant DB

    Note over Admin,API: Phase A — gate ดู § 0.1 / § 0.3, header, existence, access, idempotency
    Admin->>Console: กดทดสอบการเชื่อมต่อ
    Console->>API: POST .../connection-tests + If-Match + Idempotency-Key + X-CSRF-Token
    API->>Store: GetConnectionAsync + EnsureAccess + FindOperationAsync
    Store-->>API: 400/404/403/409 ตามรายละเอียดใน activities.md หรือ ผ่าน (ไม่มี record เดิม)
    Note over API,Store: EnsureAccess ในขั้นนี้ unreachable จริง เพราะ GetConnectionAsync ก่อนหน้า merge-check access ไปแล้ว, นอก scope โดน 404 ก่อนเสมอ ไม่ใช่ 403

    Note over API,DB: Phase B — snapshot + probe นอก transaction
    API->>DB: อ่าน snapshot connection (นอก transaction)
    DB-->>API: snapshot หรือ 404
    API->>API: EnsureVersion(snapshot.Version, If-Match) มิฉะนั้น 412
    API->>Vault: ReadVersionForServer หรือ Reveal(secret)
    Vault-->>API: secret
    API->>PSP: TestConnectionAsync(secret, environment)
    PSP-->>API: สำเร็จ หรือ exception ใด ๆ (ถือเป็น succeeded=false)

    Note over API,DB: Phase C — บันทึกผลใน transaction ที่สอง
    API->>DB: reload connection, EnsureVersion อีกครั้ง (412), authorizationLease.VerifyAsync (403 authorization_stale)
    API->>DB: RecordTest(succeeded, authenticated/probe_failed) + OperationRecord.Complete(200/502) + commit

    alt probe สำเร็จ
        API-->>Console: 200 PspConnectionView + ETag vN
    else probe ล้มเหลว
        API-->>Console: 502 ProblemDetails code psp_test_failed (state ล้มเหลวถูก commit แล้ว)
    end
```

---

## 7.5 สร้างคำขอเปลี่ยน payment setting (maker)

`/payment-setting-requests` dispatch 3 kind จาก body shape แล้วทุก kind จบด้วยเขียน `ApprovalRequested` ลง governance outbox ในธุรกรรมเดียวกับ stage state — `/credential-versions` เรียก kind credential ตัวเดียวกันตรง ๆ ผ่าน route คนละเส้น ทั้งคู่ตอบ **201** ไม่ใช่ 202 (source: `CanonicalProviderConfigurationEndpoints.cs:164-358`, `AdminPaymentsControlStore.cs:723-1122`)

```mermaid
sequenceDiagram
    autonumber
    actor Admin
    participant Console as Admin Console
    participant API
    participant Store as AdminPaymentsControlStore
    participant Vault
    participant DB as DB PspConnections / Merchants / GovernanceOutboxMessages
    participant Disp as GovernanceOutboxDispatcher

    Note over Admin,API: Phase A — gate ดู § 0.1 / § 0.3, body 16 KiB, validation, version
    Admin->>Console: ขอเปลี่ยน credential / routing / environment
    Console->>API: POST payment-setting-requests (baseVersion+reason+kind) หรือ POST credential-versions + If-Match + Idempotency-Key + X-CSRF-Token
    API->>API: ReadSecretBodyAsync (413/400), validate presence (400), If-Match parse (400), version เทียบ (412)

    Note over API,Store: Phase B — dispatch ตาม kind แล้วสร้าง pending
    alt kind credential
        API->>Store: RequestCredentialChangeAsync
        Store->>Store: EnsureAccess (403) + load connection+merchant (404) + ValidateSecretFields (400) + envelopeFactory.Build
        Store->>Store: idempotency lookup (409 reused/in-progress หรือ replay)
        Store->>Store: pending/legacy checks (409) + authorizationLease (403) + EnsureVersion (412)
        Store->>Vault: StageVersionAsync(secret envelope)
        Vault-->>Store: candidateVersionId
        Store->>Store: connection.StageSecretVersion(candidate, approvalId)
    else kind routing
        API->>Store: RequestActivationAsync
        Store->>Store: EnsureAccess (403) + load ruleset (404) + EnsureVersion (412) + ValidateRulesAsync/EnsureRoutingCoverageAsync (400/409)
        Store->>Store: idempotency lookup (409/replay) + authorizationLease (403)
        Store->>Store: entity.RequestActivation(approvalId)
    else kind environment
        API->>Store: RequestEnvironmentChangeAsync
        Store->>Store: EnsureAccess (403) + parse target (400) + โหลด merchant (404) + per-connection validate (400)
        Store->>Store: idempotency lookup (409 reused/in-progress หรือ replay)
        Store->>Store: completeness/webhook/pending/legacy checks (409) + authorizationLease (403) + EnsureVersion (412)
        Store->>Vault: StageVersionAsync ต่อ connection
        Vault-->>Store: candidateVersionId ต่อ connection
        Store->>Store: merchant.StagePaymentEnvironment(target, approvalId)
    end

    Store->>DB: transaction: OperationRecord.Complete(202 ภายใน) + outbox ApprovalRequested + commit
    DB-->>Store: ok
    Store-->>API: pending result (approvalId)
    API-->>Console: 201 Created (Location ตาม endpoint) — ไม่ใช่ 202

    Note over Disp,DB: Phase C — governance รับคำขอ (async)
    DB-->>Disp: lease ApprovalRequested
    Disp->>DB: ApprovalRequest Pending v1 (เหมือน § 0.7 Phase B ทุกประการ)
```

---

## 7.6 อนุมัติ / ปฏิเสธ payment setting request (checker)

route merchant-scoped คนละเส้นจาก `/approvals/{approvalId}` แต่เรียก `GovernanceStore.DecideAsync` ฟังก์ชันเดียวกับ § 0.7 — rule/version conflict คืน 409 ตาม § 0.7 เป๊ะ ต่างที่ response สำเร็จเป็น 200 ไม่ใช่ 202 (source: `CanonicalProviderConfigurationEndpoints.cs:364-395`, `GovernanceStore.cs:64-146`)

```mermaid
sequenceDiagram
    autonumber
    actor Admin as Checker (Admin)
    participant Console as Admin Console
    participant API
    participant Gov as GovernanceStore
    participant DB as DB admin.ApprovalRequests / GovernanceOutboxMessages
    participant Disp as GovernanceOutboxDispatcher
    participant Exe as IApprovalDecisionExecutor

    Note over Admin,API: Phase A — presence validation + fetch + header
    Admin->>Console: เปิดคำขอ payment setting แล้วตัดสินใจ
    Console->>API: POST .../approve หรือ .../reject + If-Match + Idempotency-Key + body reason, targetVersion + X-CSRF-Token
    API->>API: reason/targetVersion ไม่ว่าง มิฉะนั้น 400 code validation_failed
    API->>Gov: GetApprovalAsync(requestId) merge access ผ่าน ApplyAccess
    Gov-->>API: approval หรือ null
    alt ไม่พบ หรือ merchantId ไม่ตรง route หรือไม่ใช่ payment-setting type
        API-->>Console: 404 Results.NotFound() เปล่า
    else พบ
        API->>API: If-Match/Idempotency-Key parse มิฉะนั้น 400
        Note over API,Gov: Phase B — decide เหมือน § 0.7 checker
        API->>Gov: DecideAsync(DecisionIntent)
        Gov->>DB: lock + lookup OperationRecord
        alt idempotency reuse/in-progress
            Gov-->>API: ConflictException
            API-->>Console: 409 ProblemDetails idempotency_key_reused / operation_in_progress
        else checker เป็น maker เอง / นอก scope / ไม่มี permission
            Gov-->>API: GovernanceAccessDeniedException
            API-->>Console: 403 ProblemDetails maker_cannot_decide / merchant_scope_forbidden / underlying_permission_forbidden
        else ไม่ Pending / version stale / targetVersion เปลี่ยน
            Gov-->>API: ConflictException (จาก ApprovalRuleException)
            API-->>Console: 409 ProblemDetails approval_not_pending / target_version_changed
        else ผ่าน
            Gov->>DB: Approved/Rejected v2 + ApprovalEvent + outbox ApprovalDecided + audit + OperationRecord 202 ภายใน
            Gov-->>API: ApprovalDetail
            API-->>Console: 200 CanonicalPaymentSettingRequestView + ETag v2 — ไม่ใช่ 202
        end
    end

    Note over Disp,Exe: Phase C — execute (async) เหมือน § 0.7 Phase D ทุกประการ
    DB-->>Disp: lease ApprovalDecided
    Disp->>Exe: Publish (executor ตรง TargetType)
    Exe->>DB: apply เมื่อ approved / คืน pending เมื่อ rejected + outbox ApprovalExecutionReported
    DB-->>Disp: lease ApprovalExecutionReported
    Disp->>Gov: Publish
    Gov->>DB: status Succeeded / Failed / Unknown + audit approval.executed
```

## Notes

- Deviations และข้อสังเกตเรื่อง status code (412, 201/202, 200/202, 3 รูป 404, bug 500 ของ SyncAccountMethodsAsync, ETag ของ #18, paging 2 ชั้นของ #6, ACCESS หลัง PRE เป็น dead branch ใน § 7.3 patch/disable และ § 7.4) อยู่ใน `07-canonical-control-plane.activities.md` — ไฟล์นี้อ้างกลับแทนการเขียนซ้ำ
- § 7.1 วาดเป็น sequence กลางเดียวครอบ 11 endpoint ตามตาราง flow ประกอบใน activities.md แทนการแยก lifeline ต่อ endpoint

**Render**: GitHub / Obsidian / VS Code Mermaid
