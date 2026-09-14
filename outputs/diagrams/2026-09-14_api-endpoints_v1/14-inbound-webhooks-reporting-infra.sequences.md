# pol-core API — Inbound PSP webhook log, reporting, health และ development surfaces (Sequence Diagrams)

> Source: `docs/reference/api-endpoints.md` ส่วน "Inbound PSP webhooks" (L351-L356), "Reporting" (L358-L368), "Infrastructure และ health" (L370-L375), "OpenAPI และ Scalar (development only)" (L377-L384) และ source ที่อ้างต่อ § (`src/Api/Api/Webhooks/InboundWebhookEndpoints.cs`, `src/Api/Api/Reporting/AdminReportingEndpoints.cs`, `src/Api/Api/Program.cs`, `src/Api/BuildingBlocks.Web/HealthChecks.cs`, `src/Api/Api/OpenApiDocuments.cs`, `src/Infrastructure/Persistence/Persistence.MerchantRuntime/Payments/InboundWebhookStore.cs`, `src/Infrastructure/Persistence/Persistence.MerchantRuntime/Reporting/AdminReportingReader.cs`, `src/Application/Modules/Orders.Application/GetReconciliationSummary.cs`)
> Scope: 13 endpoints เดียวกับ `14-inbound-webhooks-reporting-infra.activities.md` (หมายเลข § ตรงกัน) แสดงลำดับข้าม actor / API / reader / DB
> Generated: 2026-09-14

| § | Diagram | Endpoints |
| --- | --- | --- |
| 14.1 | ค้นและอ่านรายละเอียด Inbound PSP webhook event | `GET /api/v1/webhooks/inbound-events`, `GET /api/v1/webhooks/inbound-events/{eventId:guid}` |
| 14.2 | รายการและรายละเอียดธุรกรรม (Transactions) | `GET /api/v1/payments/transactions`, `GET /api/v1/payments/transactions/{paymentSessionId:guid}` |
| 14.3 | สรุปยอด dashboard และรายงานปฏิบัติการ | `GET /api/v1/reports/dashboard`, `GET /api/v1/reports/operations` |
| 14.4 | ส่งออกรายงานเป็น CSV | `GET /api/v1/payments/transactions/export`, `GET /api/v1/reports/operations/export` |
| 14.5 | รายงาน reconciliation (dual-console) | `GET /api/v1/reports/reconciliation` |
| 14.6 | Health check (liveness/readiness) | `GET /health/live`, `GET /health/ready` |
| 14.7 | OpenAPI document และ Scalar UI (development only) | `GET /openapi/{documentName}.json`, `GET /scalar/{documentName?}` |

---

## 14.1 ค้นและอ่านรายละเอียด Inbound PSP webhook event

validate query ก่อนแล้วจึง query ตาม merchant scope, scope ว่างคืนผลว่างโดยไม่แตะ DB (source: `InboundWebhookEndpoints.cs:13-43`, `InboundWebhookStore.cs:126-178,215-223`)

```mermaid
sequenceDiagram
    autonumber
    actor AD as Admin
    participant CON as Admin Console
    participant API as API<br/>InboundWebhookEndpoints
    participant STORE as InboundWebhookStore
    participant DB as SQL Server<br/>txn.InboundWebhookEvents

    Note over CON,DB: Phase A — auth + validate query
    AD->>CON: เปิดหน้า inbound webhook log
    CON->>API: GET /api/v1/webhooks/inbound-events?page&limit&merchantId&psp&status&search&from&to + Authorization Bearer
    API->>API: policy admin + permission audit.view ดู § 0.1 (CSRF filter ติดแต่ GET ข้าม ดู § 0.3)
    API->>STORE: ListAsync(query, Access(scope))
    STORE->>STORE: Validate: page>=1, limit 1..100, search<=128, from<=to
    alt validation ล้มเหลว
        API-->>CON: 400 ProblemDetails code invalid_filter
    else ผ่าน
        Note over STORE,DB: Phase B — query ตาม merchant scope
        alt Admin ไม่ unrestricted และ Accessible.Merchants ว่างเปล่า
            STORE-->>API: PagedResult ว่าง (ไม่แตะ DB, short-circuit ก่อน parse psp/status)
            API-->>CON: 200 PagedResult InboundWebhookEventView ว่าง (ไม่มี raw payload/signature)
        else มี merchant ใน scope
            STORE->>STORE: Codes.FromCode(psp) / map status group — ไม่รู้จัก = ArgumentException
            alt psp/status ไม่รู้จัก
                API-->>CON: 400 ProblemDetails code invalid_filter
            else ผ่าน
                STORE->>DB: SELECT txn.InboundWebhookEvents WHERE filter + scope ORDER BY ReceivedAt DESC, Id DESC
                DB-->>STORE: rows + total
                API-->>CON: 200 PagedResult InboundWebhookEventView (PayloadFingerprint + SignatureValid, ไม่มี raw payload/signature)
            end
        end
    end
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/webhooks/inbound-events/{eventId:guid}` | permission เดียวกัน (audit.view), ไม่มี query filter/validation, lookup ตรง `eventId` ในสโคปเดียวกัน, หา `eventId` ไม่พบหรือนอก scope -> 404 bare (ไม่แยกเหตุ), พบ -> 200 `InboundWebhookEventView` ตัวเดียวกัน (ไม่มี ETag) |

---

## 14.2 รายการและรายละเอียดธุรกรรม (Transactions)

SFS parse แล้วเติม createdAt default ก่อน query join session+order (source: `AdminReportingEndpoints.cs:51-88,258-270`, `AdminReportingReader.cs:97-132,171-209`)

```mermaid
sequenceDiagram
    autonumber
    actor AD as Admin
    participant CON as Admin Console
    participant API as API<br/>AdminReportingEndpoints
    participant RDR as AdminReportingReader
    participant DB as SQL Server<br/>txn.PaymentSessions JOIN shop.Orders

    Note over CON,DB: Phase A — auth + SFS parse
    AD->>CON: เปิดหน้ารายการธุรกรรม
    CON->>API: GET /api/v1/payments/transactions?page&limit&filters&sort&search + Authorization Bearer
    API->>API: policy admin + permission txn.view ดู § 0.1
    API->>API: SfsQueryParser.Parse(query, maxLimit:100) ดู § 0.6
    alt ไม่มี filter createdAt
        API->>API: เติม createdAt Between ย้อนหลัง 7 วันจาก now
    else มี createdAt filter อยู่แล้ว
        API->>API: ใช้ filter เดิม
    end
    Note over API,DB: Phase B — query ตาม scope
    API->>RDR: ListTransactionsAsync(query)
    alt filter/sort ไม่ถูกต้อง (ArgumentException)
        API-->>CON: 400 ProblemDetails code invalid_filter
    else ผ่าน
        RDR->>DB: JOIN PaymentSessions+Orders WHERE scope + filters ORDER BY sort, Skip/Take
        DB-->>RDR: rows + total
        API-->>CON: 200 PagedResult TransactionListResponse (mask CustomerName/CustomerReference, amount 4 ตำแหน่ง)
    end
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/payments/transactions/{paymentSessionId:guid}` | permission เดียวกัน (txn.view), ไม่มี SFS, lookup ตรง `paymentSessionId` ในสโคปเดียวกัน, ไม่พบหรือนอก scope -> 404, พบ -> 200 `TransactionDetailResponse` (transaction + Order lines + lifecycle events จาก outbox PaymentPaid/Failed/Expired + capability flags) พร้อม header ETag = vN จาก `Session.Version` ดู § 0.5, `ProducesProblem(409)` ประกาศไว้แต่ handler ไม่มี path คืน 409 จริง (ดู Notes ของ activities.md) |

---

## 14.3 สรุปยอด dashboard และรายงานปฏิบัติการ

period default 7 วัน สูงสุด 31 วัน, merchantId นอก scope คืนค่าว่างเงียบ ๆ ไม่ error (source: `AdminReportingEndpoints.cs:20-49,275-296`, `AdminReportingReader.cs:19-95`)

```mermaid
sequenceDiagram
    autonumber
    actor AD as Admin
    participant CON as Admin Console
    participant API as API<br/>AdminReportingEndpoints
    participant RDR as AdminReportingReader
    participant DB as SQL Server<br/>txn.PaymentSessions JOIN shop.Orders

    Note over CON,DB: Phase A — auth + parse period
    AD->>CON: เปิดหน้า dashboard
    CON->>API: GET /api/v1/reports/dashboard?from&to&merchantId + Authorization Bearer
    API->>API: policy admin + permission txn.view ดู § 0.1
    API->>API: ParsePeriod: from/to optional, default 7 วัน, ต้อง to>=from
    alt parse ไม่ได้ หรือ to<from
        API-->>CON: 400 ProblemDetails code invalid_period
    else ผ่าน แต่ (to-from) > 31 วัน
        API-->>CON: 422 ProblemDetails code query_too_broad
    else period ถูกต้อง
        API->>API: OptionalGuid(merchantId): ถ้าส่งมาต้อง parse UUID ไม่ว่าง
        alt merchantId ส่งมาแต่ parse ไม่ได้
            API-->>CON: 400 ProblemDetails code invalid_filter
        else ไม่ได้ส่ง หรือ parse ได้
            API->>RDR: DashboardAsync(period, access, merchantId)
            alt ส่ง merchantId มาและนอก scope (access.Allows = false)
                RDR-->>API: ค่าว่างทันที (total/count/breakdown = 0, ไม่แตะ DB)
            else ไม่ส่ง หรืออยู่ใน scope
                RDR->>DB: JOIN PaymentSessions+Orders WHERE scope + CreatedAt in period, GROUP BY currency/PSP/method/originator
                DB-->>RDR: totals + counts + breakdown
            end
            API-->>CON: 200 DashboardResponse
        end
    end
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/reports/operations` | permission/period/merchantId/empty-scope logic เหมือนกันทุกจุด (เรียก `DashboardAsync` ตัวเดียวกัน), ต่างที่ response ห่อเป็น `OperationsReportResponse(Summary, UnavailableSections: [])` — `UnavailableSections` เป็น array ว่างตายตัว ไม่เคยเติมค่า |

---

## 14.4 ส่งออกรายงานเป็น CSV

period บังคับ, ตรวจ row cap ก่อนสร้างไฟล์แล้วตรวจ byte cap หลังสร้างไฟล์ (source: `AdminReportingEndpoints.cs:15-16,90-163,307-378`)

```mermaid
sequenceDiagram
    autonumber
    actor AD as Admin
    participant CON as Admin Console
    participant API as API<br/>AdminReportingEndpoints
    participant RDR as AdminReportingReader
    participant DB as SQL Server<br/>txn.PaymentSessions JOIN shop.Orders

    Note over CON,DB: Phase A — auth + required period
    AD->>CON: กด Export CSV
    CON->>API: GET /api/v1/payments/transactions/export?from&to&filters&sort&search + Authorization Bearer
    API->>API: policy admin + permission txn.export ดู § 0.1
    API->>API: ParsePeriod(required:true): ต้องส่งทั้ง from/to, to>=from, <=31 วัน
    alt ไม่ส่ง หรือ parse ไม่ได้ หรือ to<from
        API-->>CON: 400 ProblemDetails code invalid_period
    else เกิน 31 วัน
        API-->>CON: 422 ProblemDetails code query_too_broad
    else period ถูกต้อง
        API->>API: SfsQueryParser.Parse ดู § 0.6, แทน createdAt filter ด้วยช่วง from..to
        API->>RDR: ListTransactionsAsync(Limit=100,000+1)
        alt filter ไม่ถูกต้อง (ArgumentException)
            API-->>CON: 400 ProblemDetails code invalid_filter
        else ผ่าน
            RDR->>DB: JOIN PaymentSessions+Orders WHERE scope + filters
            DB-->>RDR: rows + total
            alt total > 100,000 แถว
                API-->>CON: 422 ProblemDetails code export_too_large
            else ไม่เกิน
                API->>API: สร้าง CSV (header + แถวต่อ transaction, escape formula injection)
                alt bytes.Length > 100 MiB
                    API-->>CON: 422 ProblemDetails code export_too_large
                else ไม่เกิน
                    API-->>CON: 200 text/csv attachment transactions-{from}-{to}.csv
                end
            end
        end
    end
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/reports/operations/export` | permission `txn.export` เดียวกัน, period required เหมือนกัน (400 `invalid_period` / 422 `query_too_broad`), ไม่มี SFS filter, ไม่มี row-count cap, `merchantId` optional เหมือน § 14.3 — นอก scope คืน summary ว่างเงียบ ๆ ก่อนสร้าง CSV, เรียก `DashboardAsync` แทน `ListTransactionsAsync`, สร้าง CSV totals+breakdown ต่อ PSP/method/originator, ยังตรวจ cap 100 MiB เดียวกัน -> 422 `export_too_large`, ไฟล์ `operations-{from}-{to}.csv` |

---

## 14.5 รายงาน reconciliation (dual-console)

audience ถูกเลือกไว้แล้วตอน auth (§ 0.1) แล้ว handler แยกสอง code path ที่คืน `ReconciliationView` ทรงเดียวกัน (source: `Program.cs:2376-2411`, `GetReconciliationSummary.cs:9,17-31`)

```mermaid
sequenceDiagram
    autonumber
    actor U as Admin หรือ Merchant user
    participant CON as Admin/Merchant Console
    participant API as API<br/>Program.cs reconciliation handler
    participant MED as IMediator
    participant RDR as IAdminOrderReader
    participant DB as SQL Server<br/>shop.Orders

    Note over CON,DB: Phase A — auth เลือก audience (ดู § 0.1)
    U->>CON: เปิดหน้า reconciliation
    CON->>API: GET /api/v1/reports/reconciliation?merchantId + Authorization Bearer หรือ cookie __Host-mch_session
    API->>API: policy dual-console + permission payment.view ดู § 0.1
    alt audience = Merchant (SelectedConsoleAudience)
        API->>MED: Send(GetReconciliationSummaryQuery(actor.MerchantId))
        MED->>DB: SELECT shop.Orders WHERE MerchantId = session merchant, GROUP BY Status/Currency
        DB-->>MED: totals
        API-->>CON: 200 ReconciliationView (Lines ต่อ Status+Currency) เฉพาะ merchant ที่ผูกกับ session
    else audience = Admin
        alt query merchantId ส่งมาแต่ parse ไม่ได้
            API-->>CON: 400 ProblemDetails code invalid_filter
        else ไม่ได้ส่ง หรือ parse ได้
            API->>RDR: ReconciliationAsync(CommerceOrderAccess(scope), merchantId?)
            RDR->>DB: SELECT shop.Orders WHERE scope + merchantId? GROUP BY Status/Currency
            DB-->>RDR: totals
            API-->>CON: 200 ReconciliationView (Lines เดียวกัน) ภายใน Admin merchant scope
        end
    end
```

---

## 14.6 Health check (liveness/readiness)

ready รวมผล 2 check แล้วคืนเฉพาะ status token, live ตอบทันทีไม่มี dependency (source: `HealthChecks.cs:69-117`)

```mermaid
sequenceDiagram
    autonumber
    actor LB as Load balancer/orchestrator
    participant API as API<br/>HealthCheckExtensions
    participant SQL as SQL Server<br/>connection probe
    participant VAULT as VaultKeyring

    Note over LB,VAULT: Phase A — readiness probe
    LB->>API: GET /health/ready (ไม่มี cookie, AllowAnonymous)
    API->>SQL: SqlConnection.OpenAsync() (app-db check, tag ready)
    SQL-->>API: เปิดได้ / exception
    API->>VAULT: keyring.Active (vault check, tag ready)
    VAULT-->>API: key 32 byte / exception
    alt ทุก check Healthy
        API-->>LB: 200 {status: healthy}
    else มี check ล้มเหลว
        API-->>LB: 503 {status: not_ready} (ไม่คืนชื่อ check หรือ exception)
    end
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/health/live` | `AllowAnonymous` เหมือนกัน, ไม่ตรวจ dependency ใด ๆ (ไม่มี tag `ready`), ไม่มี branch, เสมอ 200 `{status: healthy}` — orchestrator ใช้เป็นสัญญาณ restart process เท่านั้น |

---

## 14.7 OpenAPI document และ Scalar UI (development only)

boot-time gate ครั้งเดียว ไม่ใช่ per-request check (source: `Program.cs:729-740`, `OpenApiDocuments.cs:8-98`)

```mermaid
sequenceDiagram
    autonumber
    actor DEV as Developer/SPA build
    participant API as API<br/>Program.cs (Development only)
    participant OAS as Microsoft.AspNetCore.OpenApi

    Note over DEV,OAS: Phase A — boot-time gate
    Note over API: Environment.IsDevelopment() ตรวจตอน startup ครั้งเดียว, ถ้า false MapOpenApi/MapScalarApiReference ไม่ถูกเรียกเลย
    DEV->>API: GET /openapi/{documentName}.json
    alt ไม่ใช่ Development
        API-->>DEV: 404 route ไม่ match (route ไม่มีอยู่จริง)
    else Development
        API->>OAS: resolve document ชื่อ documentName
        alt documentName ไม่ตรง v1/merchant/admin/integration ที่ AddOpenApi ลงทะเบียนไว้
            OAS-->>DEV: 404 (พฤติกรรม default ของ framework)
        else ตรง
            OAS->>OAS: OpenApiDocuments.ShouldInclude กรอง operation ต่อ document
            OAS-->>DEV: 200 OpenAPI JSON document
        end
    end
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/scalar/{documentName?}` | boot-gate เดียวกัน (dev only), เมื่อ map แล้วเสิร์ฟ Scalar reference UI (HTML) ไม่ใช่ JSON, document ตัวเลือก merchant/admin/integration (default = merchant, `isDefault:true`), title คงที่ "pol-core API", ไม่มี 404 ต่อ documentName ที่ไม่รู้จัก (Scalar UI จัดการ selector เอง) เสมอ 200 |

---

## Notes

- Deviations ของ theme นี้อยู่ที่ `14-inbound-webhooks-reporting-infra.activities.md` (ไม่พบ deviation ระหว่างเอกสารกับ source) — sequences.md ไม่ทำซ้ำตาราง
- § 14.1-14.4 ใช้ participant `RDR`/`STORE` แทนชั้น reader/store จริง (`AdminReportingReader`, `InboundWebhookStore`) เพื่อความกระชับ รายละเอียด query ต่อบรรทัดดู source citation ใต้แต่ละ diagram
- § 14.5 แสดงสอง code path (mediator กับ reader ตรง) เป็น `alt` เดียวเพราะ auth (§ 0.1) เลือก audience ไว้ก่อนถึง handler แล้ว ไม่ใช่ decision ใหม่ในชั้น handler
- § 14.6 ใช้ actor "Load balancer/orchestrator" แทน Admin เพราะ endpoint นี้ anonymous และถูกเรียกโดย infrastructure จริง ไม่ใช่ผู้ใช้ผ่าน console
- § 14.7 ใช้ actor "Developer/SPA build" แทน Admin เพราะ route นี้เปิดเฉพาะ development สำหรับทีมพัฒนา ไม่ใช่ flow ของผู้ใช้ปลายทาง

**Render**: GitHub / Obsidian / VS Code Mermaid
