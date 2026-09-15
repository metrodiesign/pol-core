# pol-core API — Notifications และ outbound webhooks (Activity Diagrams)

> Source: `docs/reference/api-endpoints.md` section "Notification delivery" บรรทัด L321-L335 และ section "Canonical notifications" บรรทัด L341-L349 พร้อม source ที่อ้างต่อ § (`src/Api/Api/Notifications/DeliveryEndpoints.cs`, `src/Api/Api/Notifications/CanonicalNotificationEndpoints.cs`, `Persistence.ControlPlane/Notifications/DeliveryStore.cs`, `Persistence.MerchantRuntime/Notifications/NotificationOperations.cs`, `Persistence.MerchantRuntime/Idempotency/AdminOperationExecutor.cs`, `Persistence.ControlPlane/Governance/ControlPlaneOperationExecutor.cs`, `src/Application/Modules/Notifications.Application/DeliveryContracts.cs`, `src/Domain/Modules/Notifications.Domain/DeliveryModels.cs`, `src/Api/Api/ConcurrencyEtags.cs`, `src/Infrastructure/BuildingBlocks.Infrastructure/Persistence/PlatformReadGuard.cs`)
> Scope: 24 endpoints — admin console config ของ webhook endpoint / notification rule / delivery log (15), canonical audit-logs / notification / notification-delivery (8) และ inbound provider receipt (1) ทุกตัวใต้ policy `admin` ยกเว้น receipt ที่ `AllowAnonymous`
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

ทั้ง 8 endpoint ใช้โครงเดียวกัน: gate policy admin (Bearer) + permission settings.manage (ไม่มี CSRF), scope ด้วย DeliveryAccess (unrestricted หรือ MerchantId ใน accessible) แล้ว list ผ่าน ValidatePage + filter ธรรมดา (ไม่ใช่ SFS parser) หรือ detail ผ่าน SingleOrDefault (source: `src/Api/Api/Notifications/DeliveryEndpoints.cs:14-38,85-107,123-147,195-217`, `Persistence.ControlPlane/Notifications/DeliveryStore.cs:106-129,237-262,283-305,360-391`)

```mermaid
flowchart TD
    START((●)) --> GATE["policy admin (Bearer) + permission settings.manage<br/>ดู § 0.1"]
    GATE --> KIND{"list หรือ detail?"}
    KIND -->|list| FILTER{"page >= 1 และ limit 1..100?"}
    FILTER -->|no| R400["400 ProblemDetails<br/>code invalid_filter"]
    FILTER -->|yes| SCOPE_L["DeliveryAccess: unrestricted = ทั้งหมด<br/>ไม่งั้น WHERE MerchantId IN accessible"]
    SCOPE_L --> OPT["filter เพิ่มเติมถ้ามี<br/>merchantId / enabled / status / channel / search (LIKE, SfsLike.Escape)"]
    OPT --> QUERY["ORDER BY คอลัมน์ของ entity นั้น ThenBy Id<br/>Skip / Take + LongCount"]
    QUERY --> R200_L["200 PagedResult (items, page, limit, total)<br/>webhook endpoint ไม่คืน signing secret"]
    KIND -->|detail| SCOPE_D["DeliveryAccess scope แล้ว SingleOrDefault(Id)"]
    SCOPE_D --> FOUND{"พบและอยู่ใน scope?"}
    FOUND -->|no| R404["404 (bare NotFound, ไม่มี code)"]
    FOUND -->|yes| ETAGQ{"entity เป็น notification rule<br/>หรือ webhook endpoint?"}
    ETAGQ -->|yes| ETAG["header ETag = vN ดู § 0.5"]
    ETAG --> R200_D["200 detail view"]
    ETAGQ -->|no| R200_D2["200 detail view ไม่มี ETag<br/>(notification / webhook delivery เป็น log ที่ไม่แก้ตรง)"]
    R200_L --> END_S((◉))
    R200_D --> END_S
    R200_D2 --> END_S
    R400 --> END_F((◉))
    R404 --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class QUERY,R200_L,ETAG,R200_D,R200_D2,END_S ok
    class R400,R404,END_F fail
    class KIND,FILTER,FOUND,ETAGQ gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/notifications/deliveries` | filter merchantId / channel / status / search (LIKE eventType หรือ channel), ORDER BY SentAt desc, Id desc (`DeliveryStore.cs:360-384`) |
| GET | `/api/v1/notifications/deliveries/{deliveryId:guid}` | ไม่มี ETag (log ที่ไม่แก้), body NotificationDeliveryView (`DeliveryStore.cs:386-391`) |
| GET | `/api/v1/notifications/rules` | filter merchantId / enabled / search (LIKE eventType หรือ channel), ORDER BY EventType, Id (`DeliveryStore.cs:283-299`) |
| GET | `/api/v1/notifications/rules/{ruleId:guid}` | มี ETag vN, body NotificationRuleView (destination มาสก์แล้ว) (`DeliveryStore.cs:301-305`) |
| GET | `/api/v1/webhooks/deliveries` | filter merchantId / status / search (LIKE eventType หรือ transactionId), ORDER BY CreatedAt desc, Id desc (`DeliveryStore.cs:237-256`) |
| GET | `/api/v1/webhooks/deliveries/{deliveryId:guid}` | ไม่มี ETag, body มี attemptCount, latencyMs, failureCode, canReplay (`DeliveryStore.cs:258-262`) |
| GET | `/api/v1/webhooks/endpoints` | filter merchantId / enabled / search (LIKE Name / Url / EventsCsv), ORDER BY Name, Id, ไม่คืน signing secret (`DeliveryStore.cs:106-123`) |
| GET | `/api/v1/webhooks/endpoints/{endpointId:guid}` | มี ETag vN, body มี secretHint เท่านั้น ไม่คืน signing secret (`DeliveryStore.cs:125-129`) |

---

## 13.2 GET list / detail — canonical notification, notification-delivery, audit log

4 ใน 5 endpoint (ไม่รวม audit-logs) อยู่คนละชั้นกับ § 13.1: อ่านผ่าน `NotificationOperations` ที่ห่อด้วย `PlatformReadGuard.ReadAsync` (DbException กลายเป็น 503) และ `IgnoreQueryFilters()` แล้ว re-scope เอง; audit-logs อ่านผ่าน `IGovernanceStore.ListAuditsAsync` ซึ่งไม่ผ่าน PlatformReadGuard เลย (`PlatformReadGuard` เป็น `internal` แต่ `GovernanceStore` อยู่ assembly เดียวกัน คือ `Infrastructure.csproj` จึงเรียกได้ถ้าอยากเรียก เป็นแค่โค้ดปัจจุบันไม่ได้ wrap ไว้ ไม่ใช่ข้อจำกัดด้าน visibility/assembly) DbException จึงหลุดเป็น 500 ธรรมดา ไม่ใช่ 503, filter เป็น exact-match ไม่ใช่ SFS parser ทั้งที่ list 2 ตัวมี `SfsQueryParamsMarker` (variant ดู § 0.6) (source: `src/Api/Api/Notifications/CanonicalNotificationEndpoints.cs:23-100,269-309`, `Persistence.MerchantRuntime/Notifications/NotificationOperations.cs:15-74`, `Persistence.ControlPlane/Governance/GovernanceStore.cs:198-238,267-314`, `src/Infrastructure/BuildingBlocks.Infrastructure/Persistence/PlatformReadGuard.cs:15-30`, `src/Api/BuildingBlocks.Web/ProblemDetailsExceptionHandler.cs:71-100`)

```mermaid
flowchart TD
    START((●)) --> GATE["policy admin + permission<br/>settings.manage (notifications, notification-deliveries) / audit.view (audit-logs)<br/>ดู § 0.1 (ไม่มี CSRF filter ต่อ endpoint กลุ่มนี้)"]
    GATE --> KIND{"list, detail หรือ attempts?"}
    KIND -->|list| LSRC{"audit-logs หรือ notifications?"}
    LSRC -->|audit-logs| FILTER_A{"page >= 1, limit 1..100,<br/>from <= to?"}
    FILTER_A -->|no| R400["400 ProblemDetails<br/>code invalid_filter"]
    FILTER_A -->|yes| READA["GovernanceStore.ListAuditsAsync:<br/>VerifyAccessibleAsync (hash chain) + GovernanceAccess scope<br/>WHERE exact-match (ไม่ผ่าน PlatformReadGuard)"]
    READA --> DBFAILA{"DbException ระหว่างอ่าน?"}
    DBFAILA -->|yes| R500["500 ProblemDetails ทั่วไป (ไม่มี code)<br/>GovernanceStore ไม่ผ่าน PlatformReadGuard ดู § 0.9"]
    DBFAILA -->|no| INTEG{"audit chain hash integrity ผ่าน?"}
    INTEG -->|no| R503B["503 code audit_integrity_unhealthy<br/>(AuditIntegrityException)"]
    INTEG -->|yes| R200_L["200 PagedResult"]
    LSRC -->|notifications| FILTER_N{"page >= 1, limit 1..100?"}
    FILTER_N -->|no| R400
    FILTER_N -->|yes| READL["PlatformReadGuard.ReadAsync:<br/>IgnoreQueryFilters + DeliveryAccess<br/>WHERE exact-match filters (ไม่มี LIKE)"]
    READL --> DBFAILL{"DbException ระหว่างอ่าน?"}
    DBFAILL -->|yes| R503["503 ProblemDetails<br/>DependencyUnavailable ดู § 0.9"]
    DBFAILL -->|no| R200_L
    KIND -->|detail| READD["PlatformReadGuard.ReadAsync:<br/>IgnoreQueryFilters + DeliveryAccess rescope + SingleOrDefault(Id)"]
    READD --> DBFAILD{"DbException?"}
    DBFAILD -->|yes| R503
    DBFAILD -->|no| FOUND{"พบและอยู่ใน scope?"}
    FOUND -->|no| R404["404 (bare NotFound)"]
    FOUND -->|yes| R200_D["200 detail view ไม่มี ETag<br/>recipient / attempt ถูก mask"]
    KIND -->|attempts| EXISTS{"delivery พบและอยู่ใน scope?"}
    EXISTS -->|no| R200_EMPTY["200 empty array<br/>(ไม่มี 404 แม้ประกาศ ProducesProblem 404)"]
    EXISTS -->|yes| R200_ATT["200 attempt history (redacted)"]
    R200_L --> END_S((◉))
    R200_D --> END_S
    R200_EMPTY --> END_S
    R200_ATT --> END_S
    R400 --> END_F((◉))
    R404 --> END_F
    R503 --> END_F
    R503B --> END_F
    R500 --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class READL,READA,R200_L,R200_D,R200_EMPTY,R200_ATT,END_S ok
    class R400,R404,R503,R503B,R500,END_F fail
    class KIND,LSRC,FILTER_A,FILTER_N,DBFAILA,INTEG,DBFAILL,DBFAILD,FOUND,EXISTS gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/audit-logs` | permission `audit.view`, filter actor / action / resource / result / merchantId / from / to, from <= to ไม่งั้น 400 invalid_filter, catch เฉพาะ `AuditIntegrityException` (503 audit_integrity_unhealthy); อ่านผ่าน `GovernanceStore` ที่ไม่ผ่าน `PlatformReadGuard` (ต่างจาก 4 endpoint ที่เหลือใน § นี้) DbException จริงจึงหลุดเป็น 500 ธรรมดา ไม่ใช่ 503 (`CanonicalNotificationEndpoints.cs:271-308`, `GovernanceStore.cs:198-238,267-314`); ตรรกะเดียวกับ `GET /api/v1/audits` (theme 12 § 12.3) ต่าง path และไม่ผ่าน mediator |
| GET | `/api/v1/notification-deliveries/{deliveryId:guid}` | อ่านจาก `txn.Deliveries` (CommerceDbContext) คนละตารางกับ `/api/v1/notifications/deliveries/{id}` ใน § 13.1 (`admin.NotificationDeliveries`, ControlPlaneDbContext) (`NotificationOperations.cs:49-56`) |
| GET | `/api/v1/notification-deliveries/{deliveryId:guid}/attempts` | ไม่มี 404 จริงแม้ประกาศ ProducesProblem(404): delivery ไม่พบหรือนอก scope คืน 200 empty array (`NotificationOperations.cs:58-74`) |
| GET | `/api/v1/notifications` | exact-match filter merchantId / orderNo / transactionNo / correlationId เท่านั้น ไม่มี LIKE / JSON filter แม้มี `SfsQueryParamsMarker` (variant ดู § 0.6), merchantId นอก scope คืนชุดว่างไม่ใช่ 403 (`NotificationOperations.cs:15-38`) |
| GET | `/api/v1/notifications/{notificationId:guid}` | ไม่มี ETag, ไม่มี attempts, body เดียว (`NotificationOperations.cs:40-47`) |

---

## 13.3 สร้าง / แก้ไข / ลบ notification rule

3 endpoint ใช้ ControlPlaneOperationExecutor เดียวกัน แต่ลำดับ validation ต่างกันจริงต่อ mode: create ตรวจ scope (403) ก่อน body (400) ส่วน update ตรวจ body (400) ก่อน scope (404); rule mutation ทั้ง 3 ไม่เขียน audit log (source: `src/Api/Api/Notifications/DeliveryEndpoints.cs:149-193`, `Persistence.ControlPlane/Notifications/DeliveryStore.cs:307-358,429-436`, `Persistence.ControlPlane/Governance/ControlPlaneOperationExecutor.cs:19-82`)

```mermaid
flowchart TD
    START((●)) --> GATE["policy admin (Bearer) + permission settings.manage<br/>ดู § 0.1"]
    GATE --> MODE{"create, update หรือ delete?"}
    MODE -->|create| IDEM_C{"Idempotency-Key ไม่ว่าง, <=200, ไม่มี control char?"}
    IDEM_C -->|no| R400_I["400 code invalid_idempotency_key"]
    IDEM_C -->|yes| SCOPE_C{"merchantId ใน accessible ของ admin?"}
    SCOPE_C -->|no| R403["403 AccessDeniedException<br/>code permission_denied"]
    SCOPE_C -->|yes| BODY_C{"eventType ใน 4 SupportedEvents,<br/>channel = inapp, destination = admin-console?"}
    BODY_C -->|no| R400_V["400 code channel_unavailable<br/>หรือ validation_failed"]
    BODY_C -->|yes| EXEC_C["ControlPlaneOperationExecutor ใน transaction เดียว"]
    MODE -->|update| IFM_U{"If-Match รูป quoted vN?"}
    IFM_U -->|no| R400_E["400 code invalid_etag"]
    IFM_U -->|yes| IDEM_U{"Idempotency-Key ผ่าน?"}
    IDEM_U -->|no| R400_I
    IDEM_U -->|yes| BODY_U{"eventType / channel / destination ผ่าน?"}
    BODY_U -->|no| R400_V
    BODY_U -->|yes| SCOPE_U{"rule พบและอยู่ใน scope?"}
    SCOPE_U -->|no| R404["404 (bare NotFound)"]
    SCOPE_U -->|yes| EXEC_U["ControlPlaneOperationExecutor"]
    MODE -->|delete| IFM_D{"If-Match รูป quoted vN?"}
    IFM_D -->|no| R400_E
    IFM_D -->|yes| IDEM_D{"Idempotency-Key ผ่าน?"}
    IDEM_D -->|no| R400_I
    IDEM_D -->|yes| SCOPE_D{"rule พบและอยู่ใน scope?"}
    SCOPE_D -->|no| R404
    SCOPE_D -->|yes| EXEC_D["ControlPlaneOperationExecutor"]
    EXEC_C --> PRIOR
    EXEC_U --> PRIOR
    EXEC_D --> PRIOR{"OperationRecord (actor, operation, key) เดิม?"}
    PRIOR -->|"hash ต่าง"| R409_K["409 code idempotency_key_reused"]
    PRIOR -->|"InProgress"| R409_P["409 code operation_in_progress"]
    PRIOR -->|"Succeeded"| REPLAY["response เดิม (Replayed)"]
    PRIOR -->|"ไม่มี"| RUN{"mode?"}
    RUN -->|create| INSERT["NotificationRule.Create แล้วเพิ่มแถว<br/>ไม่เขียน audit log"]
    INSERT --> R201["201 Created + ETag v1"]
    RUN -->|update| VER{"row.Version ตรงกับ If-Match?"}
    VER -->|no| R409_C["409 ProblemDetails Conflict<br/>ConcurrencyConflictException code state_conflict"]
    VER -->|yes| UPDATE["row.Update(...) ไม่เขียน audit log"]
    UPDATE --> R200["200 + ETag v ใหม่"]
    RUN -->|delete| VERD{"row.Version ตรง?"}
    VERD -->|no| R409_C
    VERD -->|yes| REF{"มี NotificationDelivery อ้าง RuleId นี้?"}
    REF -->|yes| R409_R["409 code rule_referenced"]
    REF -->|no| DEL["ลบแถว ไม่เขียน audit log"]
    DEL --> R204["204 ไม่มี body"]
    R201 --> END_S((◉))
    R200 --> END_S
    R204 --> END_S
    REPLAY --> END_S
    R400_E --> END_F((◉))
    R400_I --> END_F
    R400_V --> END_F
    R403 --> END_F
    R404 --> END_F
    R409_K --> END_F
    R409_P --> END_F
    R409_C --> END_F
    R409_R --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    class EXEC_C,EXEC_U,EXEC_D,INSERT,UPDATE,DEL,R201,R200,R204,END_S ok
    class R400_E,R400_I,R400_V,R403,R404,R409_K,R409_P,R409_C,R409_R,END_F fail
    class MODE,IDEM_C,SCOPE_C,BODY_C,IFM_U,IDEM_U,BODY_U,SCOPE_U,IFM_D,IDEM_D,SCOPE_D,PRIOR,RUN,VER,VERD,REF gate
    class REPLAY warn
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| POST | `/api/v1/notifications/rules` | ไม่มี If-Match, ลำดับ EnsureAccess (403) ก่อน ValidateRule (400) ต่างจาก update, 201 + ETag v1, ไม่เขียน audit (`DeliveryStore.cs:307-322`) |
| PUT | `/api/v1/notifications/rules/{ruleId:guid}` | ValidateRule (400) ก่อนหา scope (404) ตรงข้ามกับ create, ไม่เขียน audit (`DeliveryStore.cs:324-340`) |
| DELETE | `/api/v1/notifications/rules/{ruleId:guid}` | ไม่มี body validation, เช็ค NotificationDelivery อ้างอิงก่อนลบ (409 rule_referenced), ไม่เขียน audit (`DeliveryStore.cs:342-358`) |

---

## 13.4 สร้าง webhook endpoint (SSRF-safe + secret ครั้งเดียว)

ต่างจาก § 13.3 ตรงต้องผ่าน `SafeDestinationValidator` (DNS resolve จริง) ก่อน executor และออก signing secret ที่คืนให้ client ครั้งเดียวเท่านั้น (source: `src/Api/Api/Notifications/DeliveryEndpoints.cs:40-53`, `Persistence.ControlPlane/Notifications/DeliveryStore.cs:39-91,131-167,438-443`)

```mermaid
flowchart TD
    START((●)) --> GATE["policy admin (Bearer) + permission settings.manage<br/>Idempotency-Key ดู § 0.5<br/>ดู § 0.1"]
    GATE --> IDEM{"Idempotency-Key ไม่ว่าง, <=200, ไม่มี control char?"}
    IDEM -->|no| R400_I["400 code invalid_idempotency_key"]
    IDEM -->|yes| SCOPE{"merchantId ใน accessible ของ admin?"}
    SCOPE -->|no| R403["403 AccessDeniedException<br/>code permission_denied"]
    SCOPE -->|yes| EVQ{"events ไม่ว่าง, ไม่ซ้ำ,<br/>เป็น subset ของ 4 SupportedEvents?"}
    EVQ -->|no| R400_V["400 code validation_failed"]
    EVQ -->|yes| SSRF["SafeDestinationValidator.ResolveAsync(url)"]
    SSRF --> SSRFQ{"absolute HTTPS port 443,<br/>ไม่มี userinfo/fragment, host ไม่ใช่ IP/localhost,<br/>DNS resolve ได้ผล public ทั้งหมด?"}
    SSRFQ -->|no| R400_U["400 code unsafe_destination"]
    SSRFQ -->|yes| EXEC["ControlPlaneOperationExecutor ใน transaction เดียว"]
    EXEC --> PRIOR{"OperationRecord (actor, operation, key) เดิม?"}
    PRIOR -->|"hash ต่าง"| R409_K["409 code idempotency_key_reused"]
    PRIOR -->|"InProgress"| R409_P["409 code operation_in_progress"]
    PRIOR -->|"Succeeded"| REPLAY["response เดิม (Replayed)<br/>ไม่คืน signing secret ซ้ำ"]
    PRIOR -->|"ไม่มี"| CREATE["สร้าง WebhookEndpoint + DeliverySecretVersion Active<br/>เขียน audit webhook-endpoint.create"]
    CREATE --> R201["201 Created + no-store<br/>+ ETag v1 + issuedSecret (ครั้งเดียว)"]
    R201 --> END_S((◉))
    REPLAY --> END_S
    R403 --> END_F((◉))
    R400_I --> END_F
    R400_V --> END_F
    R400_U --> END_F
    R409_K --> END_F
    R409_P --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    class SSRF,EXEC,CREATE,R201,END_S ok
    class R403,R400_I,R400_V,R400_U,R409_K,R409_P,END_F fail
    class IDEM,SCOPE,EVQ,SSRFQ,PRIOR gate
    class REPLAY warn
```

---

## 13.5 แก้ไข / ลบ webhook endpoint

ต่างจาก § 13.3 ตรง SSRF check เฉพาะ PUT เมื่อ enabled = true และมี audit log ทุก mutation (source: `src/Api/Api/Notifications/DeliveryEndpoints.cs:55-83`, `Persistence.ControlPlane/Notifications/DeliveryStore.cs:169-235`)

```mermaid
flowchart TD
    START((●)) --> GATE["policy admin (Bearer) + permission settings.manage<br/>ดู § 0.1"]
    GATE --> IFM{"If-Match รูป quoted vN?"}
    IFM -->|no| R400_E["400 code invalid_etag"]
    IFM -->|yes| IDEM{"Idempotency-Key ไม่ว่าง, <=200, ไม่มี control char?"}
    IDEM -->|no| R400_I["400 code invalid_idempotency_key"]
    IDEM -->|yes| MODE{"PUT หรือ DELETE?"}
    MODE -->|PUT| EVQ{"events ไม่ว่าง, ไม่ซ้ำ,<br/>subset ของ 4 SupportedEvents?"}
    EVQ -->|no| R400_V["400 code validation_failed"]
    EVQ -->|yes| ENQ{"enabled = true?"}
    ENQ -->|yes| SSRF{"SafeDestinationValidator.ResolveAsync(url) ผ่าน?"}
    SSRF -->|no| R400_U["400 code unsafe_destination"]
    SSRF -->|yes| SCOPE_U
    ENQ -->|no| SCOPE_U{"endpoint พบและอยู่ใน scope?"}
    SCOPE_U -->|no| R404["404 (bare NotFound)"]
    SCOPE_U -->|yes| EXEC_U["ControlPlaneOperationExecutor"]
    MODE -->|DELETE| SCOPE_D{"endpoint พบและอยู่ใน scope?"}
    SCOPE_D -->|no| R404
    SCOPE_D -->|yes| EXEC_D["ControlPlaneOperationExecutor"]
    EXEC_U --> PRIOR
    EXEC_D --> PRIOR{"OperationRecord เดิม?"}
    PRIOR -->|"hash ต่าง"| R409_K["409 code idempotency_key_reused"]
    PRIOR -->|"InProgress"| R409_P["409 code operation_in_progress"]
    PRIOR -->|"Succeeded"| REPLAY["response เดิม (Replayed)"]
    PRIOR -->|"ไม่มี"| VERQ{"row.Version ตรงกับ If-Match?"}
    VERQ -->|no| R409_C["409 ProblemDetails Conflict<br/>ConcurrencyConflictException code state_conflict"]
    VERQ -->|yes| MODE2{"PUT หรือ DELETE?"}
    MODE2 -->|PUT| UPDATE["row.Update(...) เขียน audit webhook-endpoint.update"]
    UPDATE --> R200["200 + ETag v ใหม่"]
    MODE2 -->|DELETE| REF{"มี WebhookDelivery อ้าง EndpointId นี้?"}
    REF -->|yes| R409_R["409 code endpoint_referenced"]
    REF -->|no| DEL["retire secret แล้วลบแถว<br/>เขียน audit webhook-endpoint.delete"]
    DEL --> R204["204 ไม่มี body"]
    R200 --> END_S((◉))
    R204 --> END_S
    REPLAY --> END_S
    R400_E --> END_F((◉))
    R400_I --> END_F
    R400_V --> END_F
    R400_U --> END_F
    R404 --> END_F
    R409_K --> END_F
    R409_P --> END_F
    R409_C --> END_F
    R409_R --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    class EXEC_U,EXEC_D,UPDATE,DEL,R200,R204,END_S ok
    class R400_E,R400_I,R400_V,R400_U,R404,R409_K,R409_P,R409_C,R409_R,END_F fail
    class IFM,IDEM,MODE,EVQ,ENQ,SSRF,SCOPE_U,SCOPE_D,PRIOR,VERQ,MODE2,REF gate
    class REPLAY warn
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| PUT | `/api/v1/webhooks/endpoints/{endpointId:guid}` | SSRF check เฉพาะเมื่อ enabled = true, ไม่หมุน signing secret, เขียน audit webhook-endpoint.update (`DeliveryStore.cs:169-201`) |
| DELETE | `/api/v1/webhooks/endpoints/{endpointId:guid}` | เช็ค WebhookDelivery อ้างอิงก่อนลบ (409 endpoint_referenced), retire secret version, เขียน audit webhook-endpoint.delete (`DeliveryStore.cs:203-235`) |

---

## 13.6 ส่ง outbound webhook ซ้ำ (replay)

ต่างจาก § 13.3-13.5 ตรง idempotency ใช้ unique index `(OriginalDeliveryId, ReplayKey)` แทน operation executor และ eligibility ตรวจจาก domain method โดยตรง (source: `src/Api/Api/Notifications/DeliveryEndpoints.cs:109-121`, `Persistence.ControlPlane/Notifications/DeliveryStore.cs:264-281`, `src/Domain/Modules/Notifications.Domain/DeliveryModels.cs:104-121`)

```mermaid
flowchart TD
    START((●)) --> GATE["policy admin (Bearer) + permission settings.manage<br/>Idempotency-Key ดู § 0.5, ดู § 0.1"]
    GATE --> IDEM{"Idempotency-Key ไม่ว่าง, <=200, ไม่มี control char?"}
    IDEM -->|no| R400_I["400 code invalid_idempotency_key"]
    IDEM -->|yes| PRIOR{"มี WebhookDelivery ที่<br/>OriginalDeliveryId = id และ ReplayKey = key เดิมอยู่แล้ว?"}
    PRIOR -->|yes| REPLAY["คืนแถว replay เดิม (Replayed = true)<br/>idempotent ผ่าน unique index ไม่ใช่ operation executor"]
    PRIOR -->|no| FOUND{"delivery ต้นทางพบและอยู่ใน scope?"}
    FOUND -->|no| R404["404 (bare NotFound)"]
    FOUND -->|yes| STATQ{"delivery.Status = Failed?"}
    STATQ -->|no| R409["409 code replay_ineligible<br/>(WebhookDelivery.Replay throw InvalidOperationException)"]
    STATQ -->|yes| CREATE["สร้างแถว WebhookDelivery ใหม่ Status Pending<br/>OriginalDeliveryId = id, ReplayKey = key<br/>ไม่แก้ประวัติเดิม"]
    CREATE --> R200["200 WebhookReplayResult (Replayed = false)"]
    R200 --> END_S((◉))
    REPLAY --> END_S
    R404 --> END_F((◉))
    R409 --> END_F
    R400_I --> END_F
    CREATE -.async.-> DISP["WebhookDeliveryDispatcher lease แถว Pending ดู § 0.8"]

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    class CREATE,R200,END_S ok
    class R404,R409,R400_I,END_F fail
    class IDEM,PRIOR,FOUND,STATQ gate
    class REPLAY warn
```

---

## 13.7 Merchant canonical event-endpoint (อ่าน + กำหนด/ปิด)

`PUT` เป็น upsert: ไม่มี endpoint เดิม = สร้าง (เรียก path เดียวกับ § 13.4), มีเดิม = แก้ (เรียก path เดียวกับ § 13.5) ต้องเช็ค merchant scope เองก่อนเข้าทั้งสองสาขา (source: `src/Api/Api/Notifications/CanonicalNotificationEndpoints.cs:180-267,324-342`)

```mermaid
flowchart TD
    START((●)) --> GATE["policy admin (Bearer) + permission settings.manage<br/>ดู § 0.1 (Bearer ไม่มี CSRF)"]
    GATE --> ACCESS{"merchantId != Guid.Empty และ<br/>อยู่ใน accessible ของ admin?"}
    ACCESS -->|no| R403["403 code merchant_scope_forbidden"]
    ACCESS -->|yes| METHOD{"GET หรือ PUT?"}
    METHOD -->|GET| LOOKUP_G["ListEndpointsAsync(merchantId, page1, limit100)<br/>OrderByDescending(UpdatedAt).FirstOrDefault"]
    LOOKUP_G --> FOUND_G{"มี endpoint?"}
    FOUND_G -->|no| R404_G["404 (bare NotFound)"]
    FOUND_G -->|yes| R200_G["200 + ETag vN ดู § 0.5"]
    METHOD -->|PUT| KEYQ{"SigningKeyReference <=200 ตัว<br/>ไม่มี control char (ถ้ามีค่า)?"}
    KEYQ -->|no| R400_K["400 code validation_failed"]
    KEYQ -->|yes| DISQ{"enabled = false และ url != null?"}
    DISQ -->|yes| R400_D["400 code validation_failed<br/>Disabled event endpoint must omit URL"]
    DISQ -->|no| LOOKUP_P["ListEndpointsAsync(merchantId) เดิม<br/>OrderByDescending(UpdatedAt).FirstOrDefault"]
    LOOKUP_P --> IDEM{"Idempotency-Key ไม่ว่าง, <=200?"}
    IDEM -->|no| R400_I["400 code invalid_idempotency_key"]
    IDEM -->|yes| EXIST{"มี endpoint เดิมของ merchant นี้?"}
    EXIST -->|no| ENQ{"enabled = true?"}
    ENQ -->|no| R404_P["404 (ไม่มีอะไรให้ปิด)"]
    ENQ -->|yes| URLQ{"url ไม่ว่าง?"}
    URLQ -->|no| R400_U["400 code validation_failed<br/>Enabled event endpoint requires URL"]
    URLQ -->|yes| CREATE["CreateEndpointAsync เหมือน § 13.4<br/>ไม่ต้องมี If-Match ทั้งที่ endpoint ประกาศ IfMatchMutationMarker<br/>SSRF check + ออก secret แต่ response ทิ้ง secret"]
    CREATE --> R200_C["200 WebhookEndpointView<br/>(secret ที่ออกไม่ถูกคืน)"]
    EXIST -->|yes| IFM{"If-Match รูป quoted vN?<br/>(บังคับเฉพาะ path นี้)"}
    IFM -->|no| R400_E["400 code invalid_etag"]
    IFM -->|yes| UPDATE["UpdateEndpointAsync เหมือน § 13.5<br/>url = body.Url ?? current.Url ทั้งสองสาขา enabled (ternary ซ้ำ)<br/>SSRF check ถ้า enabled, EnsureVersion, audit webhook-endpoint.update"]
    UPDATE --> UOK{"พบและ version ตรง?"}
    UOK -->|no| R404_U2["404 หรือ 409 ตาม § 13.5"]
    UOK -->|yes| R200_U["200 WebhookEndpointView"]
    R200_G --> END_S((◉))
    R200_C --> END_S
    R200_U --> END_S
    R403 --> END_F((◉))
    R404_G --> END_F
    R400_K --> END_F
    R400_D --> END_F
    R400_I --> END_F
    R404_P --> END_F
    R400_U --> END_F
    R400_E --> END_F
    R404_U2 --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class LOOKUP_G,R200_G,CREATE,R200_C,UPDATE,R200_U,END_S ok
    class R403,R404_G,R400_K,R400_D,R400_I,R404_P,R400_U,R400_E,R404_U2,END_F fail
    class ACCESS,METHOD,FOUND_G,KEYQ,DISQ,IDEM,EXIST,ENQ,URLQ,IFM,UOK gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/merchants/{merchantId:guid}/event-endpoint` | เช็ค `EnsureMerchantAccess` ก่อน (403 merchant_scope_forbidden ต่างจาก § 13.1 ที่ scope กรองเงียบ), คืน endpoint ล่าสุดของ merchant เท่านั้น (`CanonicalNotificationEndpoints.cs:182-205`) |
| PUT | `/api/v1/merchants/{merchantId:guid}/event-endpoint` | upsert: ไม่มี endpoint เดิม = create (ไม่ต้อง If-Match ทั้งที่ marker ประกาศไว้, ทิ้ง secret ที่ออก), มีเดิม = update (ต้อง If-Match), `url` ทั้งสอง branch ของ enabled คำนวณค่าเดียวกัน (`CanonicalNotificationEndpoints.cs:207-266`) |

---

## 13.8 ลองส่ง canonical notification delivery ใหม่ (retry)

ใช้ `AdminOperationExecutor` (Commerce plane, ลงทะเบียนจริงใน DI) แทน `ControlPlaneOperationExecutor`, ไม่มี If-Match เพราะไม่ใช่การแก้ field ของ resource (source: `src/Api/Api/Notifications/CanonicalNotificationEndpoints.cs:102-134`, `Persistence.MerchantRuntime/Notifications/NotificationOperations.cs:80-128`, `Persistence.MerchantRuntime/Idempotency/AdminOperationExecutor.cs:24-60`, `src/Domain/Modules/Notifications.Domain/DeliveryModels.cs:521-532`)

```mermaid
flowchart TD
    START((●)) --> GATE["policy admin (Bearer) + permission settings.manage<br/>Idempotency-Key ดู § 0.5, ดู § 0.1"]
    GATE --> REASONQ{"body.Reason ไม่ว่าง, <=1000 ตัว?"}
    REASONQ -->|no| R400["400 code validation_failed"]
    REASONQ -->|yes| IDEM{"Idempotency-Key ไม่ว่าง, <=200, ไม่มี control char?"}
    IDEM -->|no| R400_I["400 code invalid_idempotency_key"]
    IDEM -->|yes| FOUND{"delivery พบ (IgnoreQueryFilters + scope)?"}
    FOUND -->|no| R404["404 (bare NotFound)"]
    FOUND -->|yes| EXEC["AdminOperationExecutor (CommerceDbContext)<br/>request = merchantId, actorId, notification-delivery.retry, key, 202"]
    EXEC --> PRIOR{"AdminOperationRecord (merchant, actor, operation, key) เดิม?"}
    PRIOR -->|"intent hash ต่าง"| R409_K["409 code idempotency_key_reused"]
    PRIOR -->|"ยังไม่ Succeeded"| R409_P["409 code operation_in_progress"]
    PRIOR -->|"Succeeded"| REPLAY["response เดิม (Replayed)"]
    PRIOR -->|"ไม่มี"| RETRYQ{"delivery.Status ∈<br/>Failed / ManualQueue / Unknown / BlockedNotConfigured?"}
    RETRYQ -->|no| R409_R["409 code delivery_not_retryable"]
    RETRYQ -->|yes| RETRY["row.Retry(now): Status = Pending<br/>NextAttemptAt = now, ล้าง FailureCode / lease"]
    RETRY --> R202["202 CommerceDeliveryView"]
    R202 --> END_S((◉))
    REPLAY --> END_S
    R400 --> END_F((◉))
    R400_I --> END_F
    R404 --> END_F
    R409_K --> END_F
    R409_P --> END_F
    R409_R --> END_F
    R202 -.async.-> DISP["NotificationDeliveryDispatcher lease แถว Pending ดู § 0.8"]

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    class EXEC,RETRY,R202,END_S ok
    class R400,R400_I,R404,R409_K,R409_P,R409_R,END_F fail
    class REASONQ,IDEM,FOUND,PRIOR,RETRYQ gate
    class REPLAY warn
```

---

## 13.9 Receipt จาก notification provider (inbound, anonymous)

`AllowAnonymous` ไม่ผ่านกลุ่ม § 0.1-0.4 เลย, ตรวจสิทธิ์ด้วย signature ผ่าน provider port แทน คนละกลไกกับ webhook inbound ของ PSP (source: `src/Api/Api/Notifications/CanonicalNotificationEndpoints.cs:136-178`, `src/Application/Modules/Notifications.Application/DeliveryContracts.cs:60-65`, `Persistence.MerchantRuntime/Notifications/NotificationOperations.cs:155-235`, `Program.cs:300-301`)

```mermaid
flowchart TD
    START((●)) --> READ["อ่าน raw body + header X-Signature<br/>AllowAnonymous ไม่มี § 0.1 / § 0.2 / § 0.3 / § 0.4"]
    READ --> VERIFY["INotificationReceiptVerifier.VerifyAsync(providerCode, payload, signature)<br/>default = NoVendorNotificationReceiptVerifier"]
    VERIFY --> VALIDQ{"verification.IsValid?<br/>(default fail-closed: ไม่มี vendor ตั้งค่า)"}
    VALIDQ -->|no| R401["401 ProblemDetails<br/>code = verification.Code<br/>default notification_provider_not_configured"]
    VALIDQ -->|yes| RECQ{"verification.Receipt ไม่ null?"}
    RECQ -->|no| R400_R["400 code validation_failed"]
    RECQ -->|yes| RESOLVE["ResolveDeliveryMerchantAsync(receipt.DeliveryId)"]
    RESOLVE --> MFOUND{"พบ merchant ของ delivery?"}
    MFOUND -->|no| R404_M["404 (bare NotFound)"]
    MFOUND -->|yes| BIND["IActorScope.Begin(merchantId)<br/>bind actor สำหรับ background write authorizer"]
    BIND --> IDQ{"DeliveryId != Empty และ<br/>ProviderMessageId ไม่ว่าง?"}
    IDQ -->|no| R400_I["400 code validation_failed"]
    IDQ -->|yes| DFOUND{"delivery พบ?"}
    DFOUND -->|no| R404_D["404 (bare NotFound)"]
    DFOUND -->|yes| DUPQ{"มี DeliveryAttempt<br/>(DeliveryId, ProviderMessageId) เดิม?"}
    DUPQ -->|"มี, outcome ต่าง"| R409_C["409 code idempotency_conflict"]
    DUPQ -->|"มี, outcome เดิม"| REPLAY["คืนผลเดิม (Replayed = true)"]
    DUPQ -->|ไม่มี| MARKQ{"row.Mark* ตาม outcome<br/>(Accepted / Delivered / Failed / Unknown) ผ่านกฎ state?"}
    MARKQ -->|no| R409_S["409 code receipt_state_conflict<br/>(InvalidOperationException)"]
    MARKQ -->|yes| RECORD["บันทึก DeliveryAttempt ใหม่ (marker RECEIPT_*)"]
    RECORD --> R200["200 NotificationReceiptView (Replayed = false)"]
    R200 --> END_S((◉))
    REPLAY --> END_S
    R401 --> END_F((◉))
    R400_R --> END_F
    R404_M --> END_F
    R400_I --> END_F
    R404_D --> END_F
    R409_C --> END_F
    R409_S --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef ext fill:#4a2f6b,stroke:#bc8cff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    class BIND,RECORD,R200,END_S ok
    class R401,R400_R,R404_M,R400_I,R404_D,R409_C,R409_S,END_F fail
    class VALIDQ,RECQ,MFOUND,IDQ,DFOUND,DUPQ,MARKQ gate
    class VERIFY ext
    class REPLAY warn
```

---

## Deviations

ไม่พบ deviation ระหว่างเอกสารกับ source — ทุก policy / permission / caller ในตาราง inventory ตรงกับ `RequireAuthorization` / `RequirePermission` / `AllowAnonymous` จริงในทั้ง 2 ไฟล์ source

## Notes

| เรื่อง | ข้อเท็จจริงจาก source | source |
| --- | --- | --- |
| สอง "delivery" คนละตาราง | `/api/v1/notifications/deliveries/{id}` (§ 13.1) อ่าน `admin.NotificationDeliveries` (ControlPlaneDbContext, append-only) ส่วน `/api/v1/notification-deliveries/{id}` (§ 13.2) อ่าน `txn.Deliveries` (CommerceDbContext, จัดการโดย NotificationDeliveryDispatcher) เป็นคนละตาราง คนละ Id space | `DeliveryStore.cs:393-420`, `NotificationOperations.cs:49-56` |
| แถวต้นทางของ delivery มาจากนอก 24 endpoint นี้ | `IDeliveryEventSink.EnqueueAsync` (สร้าง WebhookDeliveries + NotificationDeliveries ของ § 13.1) ถูกเรียกจาก order/payment event consumer ของโมดูลอื่น ไม่ใช่จาก endpoint ใน theme นี้ ถือว่าอยู่นอก frame | `DeliveryStore.cs:393-420` |
| § 13.2 ผ่าน PlatformReadGuard เฉพาะ 4 ใน 5 endpoint, § 13.1 ไม่ผ่านเลย | ทุก read ใน `NotificationOperations` (notifications, notifications/{id}, notification-deliveries/{id}, attempts) ห่อด้วย `PlatformReadGuard.ReadAsync` (DbException กลายเป็น 503 DependencyUnavailable); `DeliveryControlStore` (§ 13.1) และ `GovernanceStore` (audit-logs) เรียก EF ตรงไม่มี guard นี้เลย (`PlatformReadGuard` เป็น `internal` แต่ `GovernanceStore` อยู่ assembly เดียวกัน คือ `Infrastructure.csproj` จึงเรียกได้ถ้าอยากเรียก เป็นความไม่สม่ำเสมอของ implementation ไม่ใช่ข้อจำกัดด้าน visibility/assembly) — DbException ของ audit-logs จึงหลุดเป็น 500 ธรรมดา ไม่ใช่ 503 | `PlatformReadGuard.cs:15,17-30`, `NotificationOperations.cs` ทุก method, `GovernanceStore.cs:198-238,267-314` |
| § 13.2 ข้าม global query filter เอง | ทุก query ใน `NotificationOperations` เรียก `IgnoreQueryFilters()` แล้ว re-scope ด้วย `DeliveryAccess` เอง ไม่พึ่ง EF global filter ปกติ | `NotificationOperations.cs:19,43,52,61,66,91,111,137,162` |
| attempts ไม่เคย 404 จริง | `ListAttemptsAsync` คืน `[]` เมื่อ delivery ไม่พบหรือนอก scope แทนที่จะ throw แม้ endpoint ประกาศ `ProducesProblem(404)` | `NotificationOperations.cs:58-74` |
| rule mutation ไม่มี audit log | `CreateRuleAsync` / `UpdateRuleAsync` / `DeleteRuleAsync` ไม่เรียก `audits.AppendAsync` เลย ต่างจาก webhook endpoint 3 ตัวที่เขียน audit ทุกครั้ง | `DeliveryStore.cs:307-358` เทียบ `131-235` |
| § 13.7 PUT ทิ้ง secret ที่ออกใหม่ | สาขา create เรียก `CreateEndpointAsync` แล้ว `Results.Ok(created.Endpoint)` เท่านั้น `created.Secret` (signing secret ที่ออกครั้งแรก) ไม่ถูกส่งกลับ client เลย | `CanonicalNotificationEndpoints.cs:230-239` |
| § 13.7 SigningKeyReference ตรวจแล้วไม่ใช้ | `ValidateSigningKeyReference(body.SigningKeyReference)` ตรวจความยาว/control char แต่ค่าที่ผ่านไม่ถูกเก็บหรือใช้ต่อในเมธอดนี้เลย | `CanonicalNotificationEndpoints.cs:207-266` |
| § 13.7 ประกาศ 412 แต่ไม่เคยส่งจริง | `ProducesProblem(Status412PreconditionFailed)` ประกาศไว้ แต่ If-Match รูปผิดออก 400 invalid_etag (ConcurrencyEtags) และ version ไม่ตรงออก 409 (ConcurrencyConflictException) ไม่มี path ไหนโยน 412 | `ConcurrencyEtags.cs:18-26`, `DeliveryStore.cs:169-201` |
| § 13.6 replay ไม่ผ่าน operation executor | idempotency ของ replay ใช้ unique index `(OriginalDeliveryId, ReplayKey)` เช็คเองใน transaction ไม่ใช้ ControlPlaneOperationExecutor, `ValidateKey` ภายในไม่มีทางถูกเรียกเพราะ `IdempotencyKeys.Require` ที่ endpoint กรองก่อนแล้ว | `DeliveryStore.cs:264-281` |
| unique index ที่ endpoint อาจไม่ได้กันชน (ยังไม่ยืนยัน) | ตาราง `WebhookEndpoints` มี unique filtered index `MerchantId WHERE Enabled=1` แต่ไม่พบจุดใน `CreateEndpointAsync` / `UpdateEndpointAsync` ที่ catch การชนแล้วแปลงเป็น 409 คาดว่าจะหลุดเป็น 500 ผ่าน § 0.9 "อื่น 500" ไม่พบ handler ที่ map เป็นค่าที่ต่างออกไป | `DeliveryStore.cs:517`, `131-167`, `169-201` |
| § 13.9 ไม่มี rate limit | endpoint ไม่มี `.RequireRateLimiting` ต่อไว้เลย (ไม่อยู่ใน policy `psp-webhook` ของ § 0.4 ซึ่งครอบเฉพาะ PSP inbound คนละกลุ่ม) | `CanonicalNotificationEndpoints.cs:138-178` |

**Render**: GitHub / Obsidian / VS Code Mermaid
