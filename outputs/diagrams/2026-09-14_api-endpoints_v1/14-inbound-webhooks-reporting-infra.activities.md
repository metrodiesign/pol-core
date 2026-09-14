# pol-core API — Inbound PSP webhook log, reporting, health และ development surfaces (Activity Diagrams)

> Source: `docs/reference/api-endpoints.md` ส่วน "Inbound PSP webhooks" (L351-L356), "Reporting" (L358-L368), "Infrastructure และ health" (L370-L375), "OpenAPI และ Scalar (development only)" (L377-L384) และ source ที่อ้างต่อ § (`src/Api/Api/Webhooks/InboundWebhookEndpoints.cs`, `src/Api/Api/Reporting/AdminReportingEndpoints.cs`, `src/Api/Api/Program.cs`, `src/Api/BuildingBlocks.Web/HealthChecks.cs`, `src/Api/Api/OpenApiDocuments.cs`, `src/Infrastructure/Persistence/Persistence.MerchantRuntime/Payments/InboundWebhookStore.cs`, `src/Infrastructure/Persistence/Persistence.MerchantRuntime/Reporting/AdminReportingReader.cs`)
> Scope: 13 endpoints — อ่าน inbound PSP webhook log แบบลดข้อมูลอ่อนไหว (2), รายงานธุรกรรม/dashboard/reconciliation ฝั่ง Admin (7), health probe (2), OpenAPI/Scalar เฉพาะ development (2)
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

list กรอง merchant scope + query แล้วคืนเฉพาะ payload fingerprint และผล signature ไม่คืน raw payload หรือลายเซ็นจริง (source: `InboundWebhookEndpoints.cs:13-43`, `InboundWebhookStore.cs:126-178,215-223`, `Payments.Domain/Psp/Code.cs:27-36`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy admin (Bearer) + permission audit.view ดู § 0.1<br/>GET เป็น safe method, admin เป็น Bearer จึงไม่มี CSRF"]
    AUTHZ --> BIND["bind query page=1 limit=25 merchantId psp status search from to"]
    BIND --> VALID{"Validate: page>=1, limit 1..100,<br/>search <=128 ตัวอักษร, from<=to?"}
    VALID -->|no| R400_V["400 ProblemDetails code invalid_filter<br/>(ArgumentException: page/limit ผิด, search ยาวเกิน, from หลัง to)"]
    VALID -->|yes| SCOPE{"Admin ไม่ unrestricted และ<br/>Accessible.Merchants ว่างเปล่า?"}
    SCOPE -->|yes| EMPTY["คืน PagedResult ว่างทันที ไม่ query DB<br/>(short-circuit ก่อน parse psp/status)"]
    SCOPE -->|no| CODE{"psp ตรง code ที่รู้จัก (2c2p/omise)<br/>และ status ตรง group/enum ที่รู้จัก?"}
    CODE -->|no| R400_C["400 ProblemDetails code invalid_filter<br/>(Unknown PSP code / Unknown inbound webhook status)"]
    CODE -->|yes| QUERY["query txn.InboundWebhookEvents<br/>กรอง merchantId/psp/status/search/from/to + merchant scope<br/>ORDER BY ReceivedAt DESC, Id DESC"]
    EMPTY --> R200["200 PagedResult InboundWebhookEventView<br/>PayloadFingerprint + SignatureValid(bool?), ไม่มี raw payload/signature"]
    QUERY --> R200
    R200 --> END_S((◉))
    R400_V --> END_F((◉))
    R400_C --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class R200,END_S ok
    class R400_V,R400_C,END_F fail
    class AUTHZ,VALID,SCOPE,CODE gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/webhooks/inbound-events/{eventId:guid}` | permission เดียวกัน (audit.view), ไม่มี query filter/validation, lookup ตรง `eventId` ในสโคปเดียวกัน, หา `eventId` ไม่พบหรือนอก scope -> 404 bare (ไม่แยกเหตุ), พบ -> 200 `InboundWebhookEventView` ตัวเดียวกัน (ไม่มี ETag) |

---

## 14.2 รายการและรายละเอียดธุรกรรม (Transactions)

list ใช้ SFS parser แล้วเติม createdAt ย้อนหลัง 7 วันเมื่อไม่ได้ระบุ, join session กับ order ภายใน merchant scope (source: `AdminReportingEndpoints.cs:51-88,165-188,258-270`, `AdminReportingReader.cs:97-132,134-169,171-209`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy admin + permission txn.view ดู § 0.1"]
    AUTHZ --> SFS["SfsQueryParser.Parse(query, maxLimit:100) ดู § 0.6"]
    SFS --> DEFAULT{"filters มี field createdAt แล้ว?"}
    DEFAULT -->|no| ADDPERIOD["เติม filter createdAt Between ย้อนหลัง 7 วันจาก now"]
    DEFAULT -->|yes| KEEP["ใช้ createdAt filter ที่ผู้เรียกส่งมา"]
    ADDPERIOD --> QUERY{"ListTransactionsAsync: ArgumentException<br/>จาก filter/sort ที่ SfsQueryParser ไม่ครอบ?"}
    KEEP --> QUERY
    QUERY -->|yes| R400["400 ProblemDetails code invalid_filter"]
    QUERY -->|no| JOIN["JOIN txn.PaymentSessions + shop.Orders ตาม Admin merchant scope<br/>(ReportingAccess.IsUnrestricted / MerchantIds)"]
    JOIN --> R200["200 PagedResult TransactionListResponse<br/>mask CustomerName/CustomerReference, amount เป็น decimal string 4 ตำแหน่ง"]
    R200 --> END_S((◉))
    R400 --> END_F((◉))

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class R200,END_S ok
    class R400,END_F fail
    class AUTHZ,DEFAULT,QUERY gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/payments/transactions/{paymentSessionId:guid}` | permission เดียวกัน (txn.view), ไม่มี SFS, lookup ตรง `paymentSessionId` ในสโคปเดียวกัน, ไม่พบหรือนอก scope -> 404, พบ -> 200 `TransactionDetailResponse` (transaction + Order lines + lifecycle events จาก outbox PaymentPaid/Failed/Expired + capability flags) พร้อม header ETag = vN จาก `Session.Version` ดู § 0.5, `ProducesProblem(409)` ประกาศไว้แต่ handler ไม่มี path คืน 409 จริง (ดู Notes) |

---

## 14.3 สรุปยอด dashboard และรายงานปฏิบัติการ

period ไม่ระบุ default ย้อนหลัง 7 วัน สูงสุด 31 วัน, ส่ง merchantId ที่นอก Admin scope ไม่ error แต่คืนค่าว่างเงียบ ๆ (source: `AdminReportingEndpoints.cs:20-49,190-217,275-296`, `AdminReportingReader.cs:19-95`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy admin + permission txn.view ดู § 0.1"]
    AUTHZ --> PERIOD{"from/to (optional): parse ได้ทั้งคู่ (ถ้าส่ง),<br/>to>=from?"}
    PERIOD -->|"parse ไม่ได้ หรือ to<from"| R400["400 ProblemDetails code invalid_period"]
    PERIOD -->|"ผ่าน แต่ (to-from) > 31 วัน"| R422["422 ProblemDetails code query_too_broad"]
    PERIOD -->|"ผ่าน และ <=31 วัน"| MERCH{"query merchantId: ถ้าส่งมาต้อง<br/>parse เป็น UUID ไม่ว่าง?"}
    MERCH -->|"ส่งมาแต่ parse ไม่ได้"| R400_M["400 ProblemDetails code invalid_filter<br/>(InvalidRequestException ดู § 0.9)"]
    MERCH -->|"ไม่ได้ส่ง หรือ parse ได้"| ALLOW{"ส่ง merchantId มาและ<br/>ReportingAccess.Allows(merchantId) เป็น false?"}
    ALLOW -->|yes| EMPTY["DashboardAsync คืนค่าว่างทันที<br/>(total/count/breakdown ทั้งหมด = 0) ไม่ query DB"]
    ALLOW -->|no| QUERY["DashboardAsync: JOIN txn.PaymentSessions+shop.Orders ตาม scope,<br/>filter CreatedAt ในช่วง period, group by currency/PSP/method/originator"]
    EMPTY --> R200["200 DashboardResponse"]
    QUERY --> R200
    R200 --> END_S((◉))
    R400 --> END_F((◉))
    R422 --> END_F
    R400_M --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class R200,END_S ok
    class R400,R422,R400_M,END_F fail
    class AUTHZ,PERIOD,MERCH,ALLOW gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/reports/operations` | permission/period/merchantId/empty-scope logic เหมือนกันทุกจุด (เรียก `DashboardAsync` ตัวเดียวกัน), ต่างที่ response ห่อเป็น `OperationsReportResponse(Summary, UnavailableSections: [])` — `UnavailableSections` เป็น array ว่างตายตัว ไม่เคยเติมค่า |

---

## 14.4 ส่งออกรายงานเป็น CSV

period บังคับทั้งคู่ ไม่เกิน 31 วัน, ตรวจ cap ก่อนสร้างไฟล์และหลังสร้างไฟล์ พร้อม escape spreadsheet formula injection (source: `AdminReportingEndpoints.cs:15-16,90-163,219-252,307-378`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy admin + permission txn.export ดู § 0.1"]
    AUTHZ --> PERIOD{"from/to required: ส่งมาทั้งคู่, parse ได้,<br/>to>=from, (to-from)<=31 วัน?"}
    PERIOD -->|"ไม่ส่ง หรือ parse ไม่ได้ หรือ to<from"| R400["400 ProblemDetails code invalid_period"]
    PERIOD -->|"เกิน 31 วัน"| R422_P["422 ProblemDetails code query_too_broad"]
    PERIOD -->|ok| SFS["SfsQueryParser.Parse(query, maxLimit:100) ดู § 0.6<br/>ตัด filter createdAt เดิมออก แทนด้วยช่วง from..to"]
    SFS --> QUERY{"ListTransactionsAsync (Limit=100,000+1)<br/>ArgumentException?"}
    QUERY -->|yes| R400_F["400 ProblemDetails code invalid_filter"]
    QUERY -->|no| ROWCAP{"result.Total > 100,000 แถว?"}
    ROWCAP -->|yes| R422_ROWS["422 ProblemDetails code export_too_large"]
    ROWCAP -->|no| BUILD["สร้าง CSV: header คงที่ + แถวต่อ transaction<br/>escape spreadsheet formula (=+-@ นำหน้า -> เติม ')"]
    BUILD --> SIZE{"bytes.Length > 100 MiB?"}
    SIZE -->|yes| R422_BYTES["422 ProblemDetails code export_too_large"]
    SIZE -->|no| R200["200 text/csv attachment<br/>transactions-{from:yyyyMMdd}-{to:yyyyMMdd}.csv"]
    R200 --> END_S((◉))
    R400 --> END_F((◉))
    R422_P --> END_F
    R400_F --> END_F
    R422_ROWS --> END_F
    R422_BYTES --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class R200,END_S ok
    class R400,R422_P,R400_F,R422_ROWS,R422_BYTES,END_F fail
    class AUTHZ,PERIOD,QUERY,ROWCAP,SIZE gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/reports/operations/export` | permission `txn.export` เดียวกัน, period required เหมือนกัน (400 `invalid_period` / 422 `query_too_broad`), ไม่มี SFS filter, ไม่มี row-count cap, `merchantId` optional เหมือน § 14.3 — นอก scope คืน summary ว่างเงียบ ๆ ก่อนสร้าง CSV, เรียก `DashboardAsync` แทน `ListTransactionsAsync`, สร้าง CSV totals+breakdown ต่อ PSP/method/originator, ยังตรวจ cap 100 MiB เดียวกัน -> 422 `export_too_large`, ไฟล์ `operations-{from}-{to}.csv` |

---

## 14.5 รายงาน reconciliation (dual-console)

audience Merchant เห็นเฉพาะร้านค้าที่ผูก session ผ่าน mediator query, audience Admin เห็นร้านค้าใน scope และกรอง merchantId ได้ผ่าน reader ตรง ทั้งสองคืนรูปแบบเดียวกัน (source: `Program.cs:2376-2411,3719-3720,3846-3847`, `Orders.Application/GetReconciliationSummary.cs:9,17-31`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy dual-console + permission payment.view ดู § 0.1<br/>audience (Admin/Merchant) ถูกเลือกไว้แล้วตอน auth"]
    AUTHZ --> AUD{"SelectedConsoleAudience ที่ § 0.1 เลือก?"}
    AUD -->|Merchant| MEDQ["mediator.Send(GetReconciliationSummaryQuery(actor.MerchantId))<br/>IMerchantScoped ผ่าน mediator pipeline"]
    MEDQ --> R200_M["200 ReconciliationView (Lines: Status+Currency+Count+Total)<br/>เฉพาะ merchant ที่ผูกกับ session"]
    AUD -->|Admin| Q{"query merchantId ส่งมา?"}
    Q -->|yes| PARSE{"Guid.TryParse ผ่านและไม่ใช่ Guid.Empty?"}
    PARSE -->|no| R400["400 ProblemDetails code invalid_filter"]
    PARSE -->|yes| READ
    Q -->|no| READ["adminOrders.ReconciliationAsync(CommerceOrderAccess(scope), merchantId?)<br/>กรองตาม Admin merchant scope"]
    READ --> R200_A["200 ReconciliationView (Lines เดียวกัน)<br/>ภายใน Admin merchant scope"]
    R200_M --> END_S((◉))
    R200_A --> END_S
    R400 --> END_F((◉))

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class R200_M,R200_A,END_S ok
    class R400,END_F fail
    class AUTHZ,AUD,Q,PARSE gate
```

---

## 14.6 Health check (liveness/readiness)

ready ตรวจ DB connectivity และ vault key แล้วคืนเฉพาะ status token, live ไม่ตรวจ dependency ใด ๆ (source: `HealthChecks.cs:69-117`, `Program.cs:727`)

```mermaid
flowchart TD
    START((●)) --> ANON["AllowAnonymous ไม่มี policy/CSRF/rate limit"]
    ANON --> CHECKS["HealthCheckService.CheckHealthAsync(tag=ready):<br/>app-db (SqlConnection.OpenAsync ต่อ connection string เดียวของ physical DB)<br/>+ vault (VaultKeyring.Active key ต้องยาว 32 byte)"]
    CHECKS --> AGG{"ทุก check ที่ tag ready เป็น Healthy?"}
    AGG -->|yes| R200["200 {status: healthy}"]
    AGG -->|no| R503["503 {status: not_ready}<br/>ไม่คืนชื่อ check หรือ exception ที่ล้มเหลว"]
    R200 --> END_S((◉))
    R503 --> END_F((◉))

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class R200,END_S ok
    class R503,END_F fail
    class ANON,AGG gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/health/live` | `AllowAnonymous` เหมือนกัน, ไม่ตรวจ dependency ใด ๆ (ไม่มี tag `ready`), ไม่มี branch, เสมอ 200 `{status: healthy}` — orchestrator ใช้เป็นสัญญาณ restart process เท่านั้น |

---

## 14.7 OpenAPI document และ Scalar UI (development only)

ทั้งสอง route ถูก map เฉพาะตอน startup เมื่อ `Environment.IsDevelopment()` เป็นจริง ไม่ใช่ per-request check (source: `Program.cs:729-740`, `OpenApiDocuments.cs:8-98`)

```mermaid
flowchart TD
    START((●)) --> BOOT{"Startup: Environment.IsDevelopment()?"}
    BOOT -->|no| SKIP["MapOpenApi / MapScalarApiReference ไม่ถูกเรียกเลย<br/>route ไม่มีอยู่จริงในแอป"]
    SKIP --> R404_BOOT["request ใด ๆ ไปยัง path นี้ = 404 route ไม่ match<br/>(ไม่ถึง handler เลย)"]
    BOOT -->|yes| MAP["MapOpenApi() + MapScalarApiReference(...) map ครั้งเดียวตอน startup<br/>เอกสาร v1(combined)/merchant/admin/integration ลงทะเบียนผ่าน AddOpenApi ต่อ documentName"]
    MAP --> DOC{"GET /openapi/{documentName}.json:<br/>documentName ตรงกับเอกสารที่ลงทะเบียน (v1/merchant/admin/integration)?"}
    DOC -->|no| R404_DOC["404 (พฤติกรรม default ของ<br/>Microsoft.AspNetCore.OpenApi นอก source repo นี้)"]
    DOC -->|yes| FILTER["OpenApiDocuments.ShouldInclude กรอง operation ต่อ document<br/>(v1=ทุก operation, อื่น ๆ กรองตาม security scheme + path)"]
    FILTER --> R200_DOC["200 OpenAPI JSON document"]
    R200_DOC --> END_S((◉))
    R404_DOC --> END_F((◉))
    R404_BOOT --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class R200_DOC,END_S ok
    class R404_DOC,R404_BOOT,END_F fail
    class BOOT,DOC gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/scalar/{documentName?}` | boot-gate เดียวกัน (dev only), เมื่อ map แล้วเสิร์ฟ Scalar reference UI (HTML) ไม่ใช่ JSON, document ตัวเลือก merchant/admin/integration (default = merchant, `isDefault:true`), title คงที่ "pol-core API", ไม่มี 404 ต่อ documentName ที่ไม่รู้จัก (Scalar UI จัดการ selector เอง) เสมอ 200 |

---

## Deviations

| fullPath | เอกสารบอก | source บอก | อ้างอิง |
| --- | --- | --- | --- |
| ไม่พบ deviation ระหว่างเอกสารกับ source | - | - | - |

## Notes

| เรื่อง | ข้อเท็จจริงจาก source | source |
| --- | --- | --- |
| CSRF บน GET ของ webhook log | endpoint เป็น policy `admin` (Bearer JWT) จึงไม่มี CSRF filter — admin double-submit ถูก retire และเป็น GET (safe method) อยู่แล้ว | `Iam/CsrfParity.cs:19,94-125` |
| scope ว่างของ list vs detail | list คืน `PagedResult` ว่าง (200) เมื่อ Admin ไม่มี merchant ใน scope เลย ส่วน detail คืน null -> 404 แบบเดียวกับหาไม่พบ (ไม่แยกเหตุ) | `InboundWebhookStore.cs:131-132,185-186` |
| 409 ที่ประกาศไว้แต่ไม่มี path จริง | `GetAdminTransaction` (`GET /payments/transactions/{id}`) ประกาศ `ProducesProblem(409)` แต่ `GetTransactionAsync` อ่านแล้วคืน null หรือ object เท่านั้น ไม่มี branch ที่ throw exception ที่ map เป็น 409 — เผื่อ concurrency ในอนาคตหรือ metadata ตกค้าง | `AdminReportingEndpoints.cs:165-188`, `AdminReportingReader.cs:134-169` |
| merchantId นอก scope ของ dashboard/operations/operations-export | ไม่ตอบ 403/404 แต่คืนค่าว่าง/summary ศูนย์อย่างเงียบ ๆ (`DashboardAsync` เช็ค `access.Allows(selected)` ก่อน query) | `AdminReportingReader.cs:25-26` |
| reconciliation สองเส้นทาง | audience Merchant ส่งผ่าน `IMediator` (`GetReconciliationSummaryQuery`, `IMerchantScoped`) ส่วน audience Admin เรียก `IAdminOrderReader.ReconciliationAsync` ตรง — คนละ code path แต่คืน `ReconciliationView` ทรงเดียวกัน | `Program.cs:2384-2401`, `GetReconciliationSummary.cs:9,17-31` |
| openapi/scalar ไม่อยู่ในยอด explicit operations | เอกสารระบุว่า 2 route นี้ map เฉพาะ development และไม่นับรวมยอด explicit 274 operations ของ API ทั้งระบบ | `docs/reference/api-endpoints.md:379` |
| ขนาด export cap | `MaxExportRows = 100_000` ใช้เฉพาะ transactions/export (นับต่อแถว), ทั้งสอง export ใช้ `MaxExportBytes = 100 MiB` ร่วมกัน (นับ byte ของ CSV ที่ build เสร็จ) | `AdminReportingEndpoints.cs:15-16` |
| formula injection escape | ค่าที่ขึ้นต้นด้วย `= + - @` ถูกเติม `'` นำหน้าก่อนใส่ quote ใน CSV ทุกคอลัมน์ ป้องกัน spreadsheet formula injection | `AdminReportingEndpoints.cs:372-377` |
| health ไม่ตรวจ topology | ทั้ง live/ready คืนเฉพาะ `{status}` ไม่มี exception message, connection string หรือชื่อ check หลุดออกไป | `HealthChecks.cs:33-37,60-64,101-107` |

**Render**: GitHub / Obsidian / VS Code Mermaid
