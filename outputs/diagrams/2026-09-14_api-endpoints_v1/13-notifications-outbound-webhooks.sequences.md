# pol-core API — Notifications และ outbound webhooks (Sequence Diagrams)

> Source: `docs/reference/api-endpoints.md` section "Notification delivery" บรรทัด L321-L335 และ section "Canonical notifications" บรรทัด L341-L349 พร้อม source ที่อ้างต่อ § (`src/Api/Api/Notifications/DeliveryEndpoints.cs`, `src/Api/Api/Notifications/CanonicalNotificationEndpoints.cs`, `Persistence.ControlPlane/Notifications/DeliveryStore.cs`, `Persistence.MerchantRuntime/Notifications/NotificationOperations.cs`, `Persistence.MerchantRuntime/Idempotency/AdminOperationExecutor.cs`, `Persistence.ControlPlane/Governance/ControlPlaneOperationExecutor.cs`, `src/Application/Modules/Notifications.Application/DeliveryContracts.cs`, `src/Domain/Modules/Notifications.Domain/DeliveryModels.cs`)
> Scope: 9 § เดียวกับ `13-notifications-outbound-webhooks.activities.md` (หมายเลข § ตรงกัน) แสดงลำดับข้าม actor (Admin, Notification provider) / API / store / executor / DB / dispatcher
> Generated: 2026-09-14

| § | Diagram | Endpoints |
| --- | --- | --- |
| 13.1 | GET list / detail — notification rule, notification delivery, webhook delivery, webhook endpoint (admin config plane) | `GET /api/v1/notifications/deliveries`, `GET /api/v1/notifications/deliveries/{deliveryId:guid}`, `GET /api/v1/notifications/rules`, `GET /api/v1/notifications/rules/{ruleId:guid}`, `GET /api/v1/webhooks/deliveries`, `GET /api/v1/webhooks/deliveries/{deliveryId:guid}`, `GET /api/v1/webhooks/endpoints`, `GET /api/v1/webhooks/endpoints/{endpointId:guid}` |
| 13.2 | GET list / detail — canonical notification, notification-delivery, audit log | `GET /api/v1/audit-logs`, `GET /api/v1/notification-deliveries/{deliveryId:guid}`, `GET /api/v1/notification-deliveries/{deliveryId:guid}/attempts`, `GET /api/v1/notifications`, `GET /api/v1/notifications/{notificationId:guid}` |
| 13.3 | สร้าง / แก้ไข / ลบ notification rule | `POST /api/v1/notifications/rules`, `PUT /api/v1/notifications/rules/{ruleId:guid}`, `DELETE /api/v1/notifications/rules/{ruleId:guid}` |
| 13.4 | สร้าง webhook endpoint (SSRF-safe + secret ครั้งเดียว) | `POST /api/v1/webhooks/endpoints` |
| 13.5 | แก้ไข / ลบ webhook endpoint | `PUT /api/v1/webhooks/endpoints/{endpointId:guid}`, `DELETE /api/v1/webhooks/endpoints/{endpointId:guid}` |
| 13.6 | ส่ง outbound webhook ซ้ำ (replay) | `POST /api/v1/webhooks/deliveries/{deliveryId:guid}/replay` |
| 13.7 | Merchant canonical event-endpoint (อ่าน + กำหนด/ปิด) | `GET /api/v1/merchants/{merchantId:guid}/event-endpoint`, `PUT /api/v1/merchants/{merchantId:guid}/event-endpoint` |
| 13.8 | ลองส่ง canonical notification delivery ใหม่ (retry) | `POST /api/v1/notification-deliveries/{deliveryId:guid}/retries` |
| 13.9 | Receipt จาก notification provider (inbound, anonymous) | `POST /api/v1/webhooks/notifications/{providerCode}` |

---

## 13.1 GET list / detail — notification rule, notification delivery, webhook delivery, webhook endpoint (admin config plane)

ลำดับเดียวกันสำหรับทั้ง 8 endpoint: gate แล้ว list ผ่าน filter ธรรมดา + scope query หรือ detail ผ่าน scope + SingleOrDefault (source: `DeliveryEndpoints.cs:14-38,85-107,123-147,195-217`, `DeliveryStore.cs:106-129,237-262,283-305,360-391`)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin Console
    participant API as GET rules / deliveries / endpoints
    participant ST as DeliveryControlStore
    participant DB as DB (admin.NotificationRules / NotificationDeliveries / WebhookDeliveries / WebhookEndpoints)

    Note over A,API: Phase A — gate
    A->>SPA: เปิดรายการ หรือ รายละเอียด
    SPA->>API: GET + Authorization Bearer (ดู § 0.1, permission settings.manage, admin Bearer ไม่มี CSRF)
    Note over API,DB: Phase B — list
    API->>API: page >= 1, limit 1..100
    alt page/limit ผิด
        API-->>SPA: 400 ProblemDetails invalid_filter
    else ผ่าน
        API->>ST: ListAsync(access, filters)
        ST->>DB: DeliveryAccess scope + WHERE filter/search (LIKE) + ORDER BY + Skip/Take
        DB-->>ST: items, total
        ST-->>API: PagedResult
        API-->>SPA: 200 PagedResult
    end
    Note over API,DB: Phase C — detail
    API->>ST: GetAsync(id, access)
    ST->>DB: DeliveryAccess scope + SingleOrDefault(Id)
    DB-->>ST: row หรือ null
    alt ไม่พบหรือนอก scope
        ST-->>API: null
        API-->>SPA: 404 (bare NotFound)
    else พบ
        ST-->>API: detail
        alt rule หรือ webhook endpoint
            API-->>SPA: 200 + ETag vN (ดู § 0.5)
        else notification delivery หรือ webhook delivery
            API-->>SPA: 200 ไม่มี ETag
        end
    end
```

---

## 13.2 GET list / detail — canonical notification, notification-delivery, audit log

ต่างจาก § 13.1 ตรง 4 ใน 5 endpoint อ่านผ่าน `PlatformReadGuard` (DbException = 503) และ `IgnoreQueryFilters` + rescope เอง; audit-logs อ่านผ่าน `GovernanceStore` ที่ไม่ผ่าน PlatformReadGuard เลย ดังนั้น DbException ของ audit-logs จึงเป็น 500 ธรรมดา ไม่ใช่ 503 (source: `CanonicalNotificationEndpoints.cs:23-100,269-309`, `NotificationOperations.cs:15-74`, `GovernanceStore.cs:198-238,267-314`, `PlatformReadGuard.cs:15-30`, `ProblemDetailsExceptionHandler.cs:71-100`)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin Console
    participant API as GET notifications / notification-deliveries / audit-logs
    participant OPS as NotificationOperations / GovernanceStore
    participant DB as DB (txn.Notifications, txn.Deliveries, admin.AuditRecords)

    Note over A,API: Phase A — gate (ไม่มี CSRF filter ต่อ endpoint กลุ่มนี้)
    A->>SPA: เปิดรายการ หรือ รายละเอียด
    SPA->>API: GET + cookie session (permission settings.manage หรือ audit.view ดู § 0.1)
    Note over API,DB: Phase B — list (exact-match filter, SfsQueryParamsMarker เป็น doc marker ดู § 0.6 variant)
    API->>API: page >= 1, limit 1..100 (audit-logs เพิ่ม from <= to)
    alt filter ผิด
        API-->>SPA: 400 ProblemDetails invalid_filter
    else audit-logs
        API->>OPS: ListAuditsAsync(query, access)
        OPS->>DB: GovernanceStore: VerifyAccessibleAsync (hash chain) + GovernanceAccess scope + WHERE exact-match (ไม่ผ่าน PlatformReadGuard)
        alt DbException ระหว่างอ่าน
            DB-->>OPS: DbException
            OPS-->>API: DbException (ไม่ถูกจับ, ไม่ใช่ AuditIntegrityException)
            API-->>SPA: 500 ProblemDetails ทั่วไป (ไม่มี code) ดู § 0.9
        else hash chain integrity ล้ม
            OPS-->>API: AuditIntegrityException
            API-->>SPA: 503 code audit_integrity_unhealthy
        else ปกติ
            DB-->>OPS: items, total
            OPS-->>API: PagedResult
            API-->>SPA: 200 PagedResult
        end
    else notifications
        API->>OPS: SearchAsync(query, access)
        OPS->>DB: PlatformReadGuard.ReadAsync: IgnoreQueryFilters + rescope + WHERE exact-match
        alt DbException ระหว่างอ่าน
            DB-->>OPS: DbException
            OPS-->>API: DependencyUnavailableException
            API-->>SPA: 503 ProblemDetails ดู § 0.9
        else ปกติ
            DB-->>OPS: items, total
            OPS-->>API: PagedResult
            API-->>SPA: 200 PagedResult
        end
    end
    Note over API,DB: Phase C — detail / attempts
    API->>OPS: GetAsync / GetDeliveryAsync / ListAttemptsAsync(id, access)
    OPS->>DB: PlatformReadGuard.ReadAsync: IgnoreQueryFilters + rescope + SingleOrDefault หรือ Where
    alt detail ไม่พบหรือนอก scope
        DB-->>OPS: null
        OPS-->>API: null
        API-->>SPA: 404 (bare NotFound)
    else attempts และ delivery ไม่พบ
        OPS-->>API: empty array
        API-->>SPA: 200 empty array (ไม่มี 404 จริง)
    else พบ
        DB-->>OPS: row(s)
        OPS-->>API: view (recipient mask แล้ว)
        API-->>SPA: 200 (ไม่มี ETag)
    end
```

---

## 13.3 สร้าง / แก้ไข / ลบ notification rule

`ControlPlaneOperationExecutor` เดียวกัน แต่ลำดับ validation ต่างกันจริงต่อ mode (create: scope ก่อน body, update: body ก่อน scope), ไม่เขียน audit log ทั้ง 3 mode (source: `DeliveryEndpoints.cs:149-193`, `DeliveryStore.cs:307-358,429-436`, `ControlPlaneOperationExecutor.cs:19-82`)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin Console
    participant API as POST/PUT/DELETE /notifications/rules
    participant ST as DeliveryControlStore
    participant EXEC as ControlPlaneOperationExecutor
    participant DB as DB (admin.NotificationRules, OperationRecords)

    Note over A,API: Phase A — header (ลำดับ If-Match ก่อน Idempotency-Key เมื่อมีทั้งคู่)
    A->>SPA: สร้าง / แก้ไข / ลบ กฎการแจ้งเตือน
    SPA->>API: POST (Idempotency-Key) หรือ PUT/DELETE (If-Match + Idempotency-Key) + X-CSRF-Token (ดู § 0.1 / § 0.3)
    alt If-Match หรือ Idempotency-Key รูปผิด
        API-->>SPA: 400 invalid_etag / invalid_idempotency_key
    end
    Note over API,ST: Phase B — validate: create = EnsureAccess ก่อน ValidateRule, update = ValidateRule ก่อน scope, delete = ไม่มี body
    API->>ST: create/update/delete(...)
    alt create และ merchantId นอก scope
        ST-->>API: AccessDeniedException
        API-->>SPA: 403 code permission_denied
    else eventType/channel/destination ไม่ผ่าน ValidateRule
        ST-->>API: InvalidRequestException
        API-->>SPA: 400 channel_unavailable / validation_failed
    else update/delete และ rule ไม่พบหรือนอก scope
        ST-->>API: null
        API-->>SPA: 404 (bare NotFound)
    else ผ่านทุกเงื่อนไข
        ST->>EXEC: ExecuteAsync(operation, key, intent)
        Note over EXEC,DB: Phase C — idempotency ในทรานแซกชันเดียว
        EXEC->>DB: lookup OperationRecord (actor, operation, key)
        alt hash ต่าง
            EXEC-->>API: 409 idempotency_key_reused
        else InProgress
            EXEC-->>API: 409 operation_in_progress
        else Succeeded
            EXEC-->>API: response เดิม (Replayed)
        else ไม่มี record
            Note over EXEC,DB: Phase D — mutate ตาม mode ไม่เขียน audit log (ต่างจาก § 13.4/13.5)
            alt create
                EXEC->>DB: insert NotificationRule
                EXEC-->>API: 201 + ETag v1
            else update และ version ไม่ตรง If-Match
                EXEC-->>API: 409 ConcurrencyConflict code state_conflict
            else update และ version ตรง
                EXEC->>DB: row.Update(...)
                EXEC-->>API: 200 + ETag ใหม่
            else delete และ version ไม่ตรง
                EXEC-->>API: 409 ConcurrencyConflict code state_conflict
            else delete และมี NotificationDelivery อ้าง RuleId
                EXEC-->>API: 409 rule_referenced
            else delete ลบได้
                EXEC->>DB: ลบแถว
                EXEC-->>API: 204
            end
        end
        API-->>SPA: ผลตามด้านบน
    end
```

---

## 13.4 สร้าง webhook endpoint (SSRF-safe + secret ครั้งเดียว)

ต่างจาก § 13.3 ตรงต้องผ่าน `SafeDestinationValidator` (DNS resolve จริง) ก่อน executor และออก signing secret ครั้งเดียว (source: `DeliveryEndpoints.cs:40-53`, `DeliveryStore.cs:39-91,131-167`)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin Console
    participant API as POST /webhooks/endpoints
    participant ST as DeliveryControlStore
    participant SSRF as SafeDestinationValidator
    participant EXEC as ControlPlaneOperationExecutor
    participant DB as DB (admin.WebhookEndpoints, DeliverySecretVersions)

    A->>SPA: สร้าง webhook endpoint
    SPA->>API: POST + Idempotency-Key + X-CSRF-Token (ดู § 0.1 / § 0.3)
    alt Idempotency-Key รูปผิด
        API-->>SPA: 400 invalid_idempotency_key
    end
    API->>ST: CreateEndpointAsync(...)
    alt merchantId นอก scope
        ST-->>API: AccessDeniedException
        API-->>SPA: 403 code permission_denied
    else events ว่าง/ซ้ำ/ไม่ใช่ SupportedEvents
        ST-->>API: InvalidRequestException
        API-->>SPA: 400 validation_failed
    else ผ่าน
        ST->>SSRF: ResolveAsync(url)
        SSRF->>SSRF: absolute HTTPS 443, ไม่มี userinfo/fragment, host ไม่ใช่ IP/localhost, DNS resolve
        alt destination ไม่ปลอดภัย
            SSRF-->>ST: InvalidRequestException unsafe_destination
            API-->>SPA: 400 unsafe_destination
        else ปลอดภัย
            ST->>EXEC: ExecuteAsync(create, key)
            EXEC->>DB: lookup OperationRecord
            alt hash ต่าง
                EXEC-->>API: 409 idempotency_key_reused
            else InProgress
                EXEC-->>API: 409 operation_in_progress
            else Succeeded
                EXEC-->>API: response เดิม (Replayed, ไม่คืน secret ซ้ำ)
            else ไม่มี record
                EXEC->>DB: สร้าง WebhookEndpoint + DeliverySecretVersion Active + audit webhook-endpoint.create
                EXEC-->>API: endpoint + issuedSecret
                API-->>SPA: 201 Created + no-store + ETag v1 + issuedSecret (ครั้งเดียว)
            end
        end
    end
```

---

## 13.5 แก้ไข / ลบ webhook endpoint

ต่างจาก § 13.4 ตรง SSRF check เฉพาะ PUT เมื่อ enabled = true (source: `DeliveryEndpoints.cs:55-83`, `DeliveryStore.cs:169-235`)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin Console
    participant API as PUT/DELETE /webhooks/endpoints/{id}
    participant ST as DeliveryControlStore
    participant SSRF as SafeDestinationValidator
    participant EXEC as ControlPlaneOperationExecutor
    participant DB as DB (admin.WebhookEndpoints, WebhookDeliveries)

    A->>SPA: แก้ไข หรือ ลบ webhook endpoint
    SPA->>API: PUT/DELETE + If-Match + Idempotency-Key + X-CSRF-Token (ดู § 0.1 / § 0.3)
    alt If-Match หรือ Idempotency-Key รูปผิด
        API-->>SPA: 400 invalid_etag / invalid_idempotency_key
    end
    API->>ST: UpdateEndpointAsync / DeleteEndpointAsync(...)
    alt PUT และ events ว่าง/ซ้ำ/ไม่ใช่ SupportedEvents
        ST-->>API: InvalidRequestException
        API-->>SPA: 400 validation_failed
    else PUT และ enabled = true และ destination ไม่ปลอดภัย
        ST->>SSRF: ResolveAsync(url)
        SSRF-->>ST: InvalidRequestException unsafe_destination
        API-->>SPA: 400 unsafe_destination
    else endpoint ไม่พบหรือนอก scope
        ST-->>API: null
        API-->>SPA: 404 (bare NotFound)
    else ผ่าน
        ST->>EXEC: ExecuteAsync(update/delete, key)
        EXEC->>DB: lookup OperationRecord
        alt hash ต่าง
            EXEC-->>API: 409 idempotency_key_reused
        else InProgress
            EXEC-->>API: 409 operation_in_progress
        else Succeeded
            EXEC-->>API: response เดิม (Replayed)
        else ไม่มี record และ version ไม่ตรง If-Match
            EXEC-->>API: 409 ConcurrencyConflict code state_conflict
        else ไม่มี record, version ตรง, PUT
            EXEC->>DB: row.Update(...) + audit webhook-endpoint.update
            EXEC-->>API: 200 + ETag ใหม่
        else ไม่มี record, version ตรง, DELETE และมี WebhookDelivery อ้างอิง
            EXEC-->>API: 409 endpoint_referenced
        else ไม่มี record, version ตรง, DELETE ลบได้
            EXEC->>DB: retire secret แล้วลบแถว + audit webhook-endpoint.delete
            EXEC-->>API: 204
        end
        API-->>SPA: ผลตามด้านบน
    end
```

---

## 13.6 ส่ง outbound webhook ซ้ำ (replay)

idempotency ใช้ unique index `(OriginalDeliveryId, ReplayKey)` แทน operation executor (source: `DeliveryEndpoints.cs:109-121`, `DeliveryStore.cs:264-281`, `DeliveryModels.cs:104-121`)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin Console
    participant API as POST /webhooks/deliveries/{id}/replay
    participant ST as DeliveryControlStore
    participant DB as DB (admin.WebhookDeliveries)
    participant DISP as WebhookDeliveryDispatcher

    A->>SPA: ส่ง webhook ซ้ำ
    SPA->>API: POST + Idempotency-Key + X-CSRF-Token (ดู § 0.1 / § 0.3)
    alt Idempotency-Key รูปผิด
        API-->>SPA: 400 invalid_idempotency_key
    end
    API->>ST: ReplayAsync(id, key)
    ST->>DB: หา WebhookDelivery ที่ OriginalDeliveryId=id, ReplayKey=key เดิม
    alt มี replay เดิมอยู่แล้ว
        DB-->>ST: แถว replay เดิม
        ST-->>API: Replayed = true
        API-->>SPA: 200 WebhookReplayResult (Replayed)
    else ไม่มี
        ST->>DB: หา delivery ต้นทาง (scope)
        alt ไม่พบหรือนอก scope
            DB-->>ST: null
            API-->>SPA: 404 (bare NotFound)
        else พบและ Status ไม่ใช่ Failed
            ST-->>API: InvalidOperationException ผ่าน ConflictException
            API-->>SPA: 409 replay_ineligible
        else พบและ Status = Failed
            ST->>DB: เพิ่มแถว WebhookDelivery ใหม่ Status Pending
            ST-->>API: WebhookReplayResult (Replayed = false)
            API-->>SPA: 200 WebhookReplayResult
            DB--)DISP: WebhookDeliveryDispatcher lease แถว Pending ดู § 0.8
        end
    end
```

---

## 13.7 Merchant canonical event-endpoint (อ่าน + กำหนด/ปิด)

`PUT` เป็น upsert เรียก path เดียวกับ § 13.4 (สร้าง) หรือ § 13.5 (แก้ไข) หลังเช็ค merchant scope เอง (source: `CanonicalNotificationEndpoints.cs:180-267,324-342`)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin Console
    participant API as GET/PUT /merchants/{id}/event-endpoint
    participant ST as DeliveryControlStore
    participant DB as DB (admin.WebhookEndpoints)

    A->>SPA: อ่าน หรือ กำหนด/ปิด event endpoint ของ merchant
    SPA->>API: GET หรือ PUT + cookie session (PUT ต่อ CSRF + Idempotency-Key ดู § 0.1 / § 0.3)
    API->>API: EnsureMerchantAccess(merchantId)
    alt merchantId ว่างหรือนอก accessible
        API-->>SPA: 403 merchant_scope_forbidden
    else ผ่าน
        alt GET
            API->>ST: ListEndpointsAsync(merchantId) แล้วเลือกล่าสุด (UpdatedAt desc)
            ST->>DB: query
            alt ไม่มี endpoint
                DB-->>ST: ว่าง
                API-->>SPA: 404 (bare NotFound)
            else มี
                DB-->>ST: endpoint
                API-->>SPA: 200 + ETag vN
            end
        else PUT
            API->>API: ValidateSigningKeyReference (ตรวจแล้วไม่ใช้ต่อ), disabled ต้องไม่มี url
            alt validation ผิด
                API-->>SPA: 400 validation_failed
            else ผ่าน
                API->>ST: ListEndpointsAsync(merchantId) หา endpoint เดิม
                alt Idempotency-Key รูปผิด
                    API-->>SPA: 400 invalid_idempotency_key
                else ผ่าน
                    alt ไม่มี endpoint เดิม และ enabled = false
                        API-->>SPA: 404 (ไม่มีอะไรให้ปิด)
                    else ไม่มี endpoint เดิม, enabled = true, url ว่าง
                        API-->>SPA: 400 validation_failed (ต้องมี url)
                    else ไม่มี endpoint เดิม, สร้างใหม่
                        API->>ST: CreateEndpointAsync เหมือน § 13.4 (ไม่ต้อง If-Match)
                        ST-->>API: created (secret ถูกทิ้ง)
                        API-->>SPA: 200 WebhookEndpointView
                    else มี endpoint เดิม และ If-Match รูปผิด
                        API-->>SPA: 400 invalid_etag
                    else มี endpoint เดิม ผ่าน
                        API->>ST: UpdateEndpointAsync เหมือน § 13.5
                        ST-->>API: ผลตาม § 13.5 (200 / 404 / 409)
                        API-->>SPA: ตามผล ST
                    end
                end
            end
        end
    end
```

---

## 13.8 ลองส่ง canonical notification delivery ใหม่ (retry)

ใช้ `AdminOperationExecutor` (Commerce plane) แทน `ControlPlaneOperationExecutor`, ไม่มี If-Match (source: `CanonicalNotificationEndpoints.cs:102-134`, `NotificationOperations.cs:80-128`, `AdminOperationExecutor.cs:24-60`, `DeliveryModels.cs:521-532`)

```mermaid
sequenceDiagram
    autonumber
    actor A as Admin
    participant SPA as Admin Console
    participant API as POST /notification-deliveries/{id}/retries
    participant OPS as NotificationOperations
    participant EXEC as AdminOperationExecutor
    participant DB as DB (txn.Deliveries, AdminOperationRecords)
    participant DISP as NotificationDeliveryDispatcher

    A->>SPA: ลองส่ง notification ใหม่
    SPA->>API: POST + reason + Idempotency-Key + X-CSRF-Token (ดู § 0.1 / § 0.3)
    alt reason ว่างหรือเกิน 1000 ตัว
        API-->>SPA: 400 validation_failed
    else ผ่าน
        alt Idempotency-Key รูปผิด
            API-->>SPA: 400 invalid_idempotency_key
        end
        API->>OPS: RetryAsync(deliveryId, reason, key)
        OPS->>DB: หา delivery (IgnoreQueryFilters + scope)
        alt ไม่พบ
            DB-->>OPS: null
            API-->>SPA: 404 (bare NotFound)
        else พบ
            OPS->>EXEC: ExecuteAsync(request, action)
            EXEC->>DB: lookup AdminOperationRecord (merchant, actor, operation, key)
            alt intent hash ต่าง
                EXEC-->>API: 409 idempotency_key_reused
            else ยังไม่ Succeeded
                EXEC-->>API: 409 operation_in_progress
            else Succeeded
                EXEC-->>API: response เดิม (Replayed)
                API-->>SPA: 202 (Replayed)
            else ไม่มี record และ Status ไม่ retryable
                EXEC-->>API: ConflictException delivery_not_retryable
                API-->>SPA: 409 delivery_not_retryable
            else ไม่มี record และ retryable
                EXEC->>DB: row.Retry(now): Status = Pending
                EXEC-->>API: CommerceDeliveryView
                API-->>SPA: 202 CommerceDeliveryView
                DB--)DISP: NotificationDeliveryDispatcher lease แถว Pending ดู § 0.8
            end
        end
    end
```

---

## 13.9 Receipt จาก notification provider (inbound, anonymous)

`AllowAnonymous` ตรวจสิทธิ์ด้วย signature ผ่าน provider port แทน gate ปกติ (source: `CanonicalNotificationEndpoints.cs:136-178`, `DeliveryContracts.cs:60-65`, `NotificationOperations.cs:155-235`)

```mermaid
sequenceDiagram
    autonumber
    actor P as Notification provider
    participant API as POST /webhooks/notifications/{providerCode}
    participant VER as INotificationReceiptVerifier
    participant OPS as NotificationOperations
    participant DB as DB (txn.Deliveries, DeliveryAttempts)

    Note over P,API: AllowAnonymous ไม่มี § 0.1 / § 0.2 / § 0.3 / § 0.4
    P->>API: POST + body + header X-Signature
    API->>VER: VerifyAsync(providerCode, payload, signature)
    VER-->>API: verification (default = NoVendorNotificationReceiptVerifier, fail-closed)
    alt ไม่ valid
        API-->>P: 401 code = verification.Code (default notification_provider_not_configured)
    else valid แต่ Receipt null
        API-->>P: 400 validation_failed
    else valid
        API->>OPS: ResolveDeliveryMerchantAsync(receipt.DeliveryId)
        alt ไม่พบ merchant
            OPS-->>API: null
            API-->>P: 404 (bare NotFound)
        else พบ
            API->>API: IActorScope.Begin(merchantId)
            API->>OPS: ApplyReceiptAsync(receipt)
            alt DeliveryId ว่างหรือ ProviderMessageId ว่าง
                OPS-->>API: 400 validation_failed
            else delivery ไม่พบ
                OPS-->>API: 404 (bare NotFound)
            else มี DeliveryAttempt เดิม outcome ต่าง
                OPS-->>API: 409 idempotency_conflict
            else มีเดิม outcome เดียวกัน
                OPS-->>API: Replayed = true
                API-->>P: 200 NotificationReceiptView (Replayed)
            else ไม่มีเดิมและ row.Mark* ผิดกฎ state
                OPS-->>API: 409 receipt_state_conflict
            else ไม่มีเดิมและผ่าน
                OPS->>DB: row.Mark*(...) แล้วบันทึก DeliveryAttempt ใหม่
                OPS-->>API: NotificationReceiptView (Replayed = false)
                API-->>P: 200 NotificationReceiptView
            end
        end
    end
```

## Notes

- Deviations ของ theme นี้อยู่ที่ `13-notifications-outbound-webhooks.activities.md` ไฟล์นี้ไม่ทำซ้ำ
- สอง "delivery" คนละตาราง (§ 13.1 = `admin.NotificationDeliveries`, § 13.2 = `txn.Deliveries`) และการ async ต่อจาก § 13.6/§ 13.8 เป็นแค่การ lease แถว Pending ของ dispatcher ที่มีอยู่แล้ว ไม่ใช่ outbox message ใหม่ (ต่างจาก maker-checker ของ theme 12) รายละเอียดเต็มอยู่ใน Notes ของ activities.md
- § 13.9 เป็น sequence เดียวในไฟล์นี้ที่ actor เป็นระบบภายนอก (Notification provider) ไม่ใช่ Admin และไม่มี Admin Console เข้ามาเกี่ยวข้องเลย

**Render**: GitHub / Obsidian / VS Code Mermaid
