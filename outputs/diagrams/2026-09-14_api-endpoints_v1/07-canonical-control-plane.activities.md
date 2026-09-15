# pol-core API — Canonical control plane (Activity Diagrams)

> Source: `docs/reference/api-endpoints.md` section "Canonical control plane" บรรทัด L146-L173 และ source ที่อ้างต่อ § (`src/Api/Api/ControlPlane/CanonicalMerchantConfigurationEndpoints.cs`, `src/Api/Api/ControlPlane/CanonicalProviderConfigurationEndpoints.cs`, `src/Infrastructure/Persistence/Persistence.ControlPlane/Merchants/AdminMerchantControlStore.cs`, `Persistence.ControlPlane/Payments/AdminPaymentsControlStore.cs`, `Persistence.ControlPlane/Governance/GovernanceStore.cs`, `src/Api/Api/ConcurrencyEtags.cs`, `src/Api/BuildingBlocks.Web/ProblemDetailsExceptionHandler.cs`)
> Scope: 24 endpoint ใต้ `/api/v1/merchants/{merchantId}/*` และ `/api/v1/payment-providers/*` ครอบ Merchant/Branch/Sale master data, Provider Account (configuration, credential, test, disable), payment setting maker-checker และ payment provider catalog
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

ทุก GET ผ่าน gate เดียว (permission ตามตาราง ไม่มี CSRF) แล้วต่างกันที่มี pagination หรือไม่ และ scope check เป็น explicit throw หรือ merge เข้ากับ lookup เงียบ ๆ ซึ่งกำหนดว่า 403 เกิดได้จริงหรือถูกซ่อนเป็น 404 (source: `CanonicalMerchantConfigurationEndpoints.cs:28-49,84-105,164-186,278-282`, `CanonicalProviderConfigurationEndpoints.cs:32-55,59-74,100-119,149-162,246-267,272-308,450-454`, `AdminMerchantControlStore.cs:115-122,171-188,247-266,637-641`, `AdminPaymentsControlStore.cs:50-60,62-96,98-107,109-135,2129-2133`, `GovernanceStore.cs:23-70`)

```mermaid
flowchart TD
    START((●)) --> GATE["policy admin + permission ตามตาราง<br/>merchant.view / settings.manage<br/>ไม่มี CSRF (GET) ดู § 0.1"]
    GATE --> SHAPE{"รูปแบบ endpoint?"}

    SHAPE -->|"catalog ไม่มี merchantId"| CAT["อ่าน static provider catalog<br/>จาก IPspAdapterFactory ไม่แตะ DB"]
    CAT --> CATSINGLE{"lookup providerId เดียว?<br/>เฉพาะ /methods"}
    CATSINGLE -->|"list ทั้งหมด"| R200C["200"]
    CATSINGLE -->|"พบ"| R200C
    CATSINGLE -->|"ไม่พบ"| R404BARE["404<br/>Results.NotFound() เปล่า<br/>ไม่มี ProblemDetails code"]

    SHAPE -->|"list พร้อม page/limit"| PAGE{"page >= 1 และ limit 1..100?<br/>ValidatePage เอง ไม่ใช่ SfsQueryParser"}
    PAGE -->|"no"| R400["400 ProblemDetails<br/>code invalid_filter"]
    PAGE -->|"yes"| LSCOPE{"scope check แบบไหน?"}
    LSCOPE -->|"EnsureAccess throw ก่อน query<br/>branches, sales, provider-accounts"| LCHECK{"merchantId อยู่ใน Accessible?"}
    LCHECK -->|"no"| R403["403 ProblemDetails<br/>code merchant_scope_forbidden"]
    LCHECK -->|"yes"| LRUN["query กรองด้วย merchantId ตรง"]
    LSCOPE -->|"merge เข้า query filter ไม่ throw<br/>payment-setting-requests ผ่าน ApplyAccess"| LRUN2["query กรองเงียบ<br/>นอก scope = ผลว่าง ไม่ error"]
    LRUN --> R200L["200 PagedResult"]
    LRUN2 --> R200L

    SHAPE -->|"single lookup"| SSCOPE{"scope check แบบไหน?"}
    SSCOPE -->|"EnsureAccess throw ก่อน query<br/>merchant, credential-versions parent"| SCHECK{"merchantId อยู่ใน Accessible?"}
    SCHECK -->|"no"| R403
    SCHECK -->|"yes"| SLOOKUP["lookup by id"]
    SSCOPE -->|"merge เข้า lookup ไม่ throw<br/>provider-account, payment-settings, setting-request"| SLOOKUP
    SLOOKUP --> SFOUND{"พบ และ (ถ้า merge) access.Allows ผ่าน?"}
    SFOUND -->|"no"| S404{"endpoint คืน 404 รูปไหน?"}
    S404 -->|"Results.Problem + code not_found<br/>merchant, provider-account"| R404A["404 ProblemDetails<br/>code not_found"]
    S404 -->|"Results.NotFound() เปล่า<br/>setting-request, payment-settings, credential-versions"| R404BARE
    SFOUND -->|"yes"| R200S["200 (+ETag ถ้ามี EtagResponseMarker)"]

    R200C --> END_S((◉))
    R200L --> END_S
    R200S --> END_S
    R400 --> END_F((◉))
    R403 --> END_F
    R404A --> END_F
    R404BARE --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class CAT,LRUN,LRUN2,SLOOKUP,R200C,R200L,R200S,END_S ok
    class R400,R403,R404A,R404BARE,END_F fail
    class SHAPE,CATSINGLE,PAGE,LSCOPE,LCHECK,SSCOPE,SCHECK,SFOUND,S404 gate
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/merchants/{merchantId:guid}` | single, `EnsureAccess` throw ก่อน query -> 403, ไม่พบ = Problem 404 code `not_found`, สำเร็จ = 200 + ETag (`GetMerchantAsync` `AdminMerchantControlStore.cs:115-122`) |
| GET | `/api/v1/merchants/{merchantId:guid}/branches` | list, `EnsureAccess` throw -> 403, `ValidatePage` 400 `invalid_filter`, ไม่เช็คว่า merchant ยังอยู่จริง (merchant ถูกลบก็คืน 200 ว่าง) (`ListBranchesAsync` `AdminMerchantControlStore.cs:171-188`) |
| GET | `/api/v1/merchants/{merchantId:guid}/payment-setting-requests` | list, ไม่มี `EnsureAccess` (กรองเงียบผ่าน `ApplyAccess`), `ValidatePage` 400 `invalid_filter`, paging ทำ 2 ชั้น: ดึง governance 100 แถวแรกก่อนแล้ว filter ประเภทและ skip/take เอง — merchant ที่มี approval รวมเกิน 100 รายการจะเห็น page ถัดไปขาดหาย (`GovernanceStore.ListApprovalsAsync:23-62`, endpoint `CanonicalProviderConfigurationEndpoints.cs:272-290`) |
| GET | `/api/v1/merchants/{merchantId:guid}/payment-setting-requests/{requestId:guid}` | single, merge access ผ่าน `ApplyAccess` ใน `GetApprovalAsync`, merchantId ไม่ตรง route หรือไม่ใช่ payment-setting type = `Results.NotFound()` เปล่าเหมือนไม่พบ (`CanonicalProviderConfigurationEndpoints.cs:292-308`, `GovernanceStore.cs:64-70`) |
| GET | `/api/v1/merchants/{merchantId:guid}/payment-settings` | single ไม่มี id ปลาย, merge access+existence เป็น bool เดียวใน `MerchantExistsForAccessAsync` (403 ไม่เคยเกิดจริง เห็นแต่ 404 เปล่า), ประกอบ view จาก 3 store: environment, effective methods, simple routing (`AdminPaymentsControlStore.cs:50-60`, endpoint `CanonicalProviderConfigurationEndpoints.cs:246-267`) |
| GET | `/api/v1/merchants/{merchantId:guid}/provider-accounts` | list, `EnsureAccess` throw -> 403, `ValidatePage` 400 `invalid_filter` (`ListConnectionsAsync` `AdminPaymentsControlStore.cs:62-96`) |
| GET | `/api/v1/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}` | single, merge access ใน `GetConnectionAsync` (`access.Allows` คืน null ถ้าไม่ผ่าน ไม่ throw), ไม่พบ = Problem 404 code `not_found` (endpoint เขียนเองไม่ผ่าน exception), สำเร็จ = 200 + ETag ไม่คืน secret (`AdminPaymentsControlStore.cs:98-107`) |
| GET | `/api/v1/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}/credential-versions` | single (parent), `EnsureAccess` throw -> 403, connection ไม่พบ = `Results.NotFound()` เปล่า (ผสม throw + bare), คืนเฉพาะ version/state/masked hint ไม่คืน plaintext (`ListCredentialVersionsAsync` `AdminPaymentsControlStore.cs:109-135`) |
| GET | `/api/v1/merchants/{merchantId:guid}/sales` | list, `EnsureAccess` throw -> 403, `ValidatePage` 400 `invalid_filter`, filter เพิ่ม `branchId` ได้ (`ListSalesAsync` `AdminMerchantControlStore.cs:247-266`) |
| GET | `/api/v1/payment-providers` | catalog ไม่มี merchantId ไม่มี scope check เลย, คืนเสมอ 200 รายชื่อ provider คงที่ 2 ตัว (2c2p, Omise) จาก `IPspAdapterFactory` (`CanonicalProviderConfigurationEndpoints.cs:34-42,397-416`) |
| GET | `/api/v1/payment-providers/{providerId:guid}/methods` | catalog + lookup เดียว ไม่มี scope check, providerId ไม่อยู่ใน catalog คงที่ = `Results.NotFound()` เปล่า (`CanonicalProviderConfigurationEndpoints.cs:44-54`) |

---

## 7.2 เขียน Merchant / Branch / Sale (create + patch)

POST (สร้าง) ไม่ต้องมี `If-Match`, ตรวจ parent + duplicate code เป็น 409, ส่วน PATCH (แก้) ต้องมี `If-Match` และโหลด target ก่อนเช็ค version — ทั้งกลุ่มมี endpoint filter เฉพาะโมดูลที่ map `ConcurrencyConflictException` เป็น **412** ไม่ใช่ 409 ตามค่า default ทั่วระบบ (source: `CanonicalMerchantConfigurationEndpoints.cs:26-79,107-159,188-266`, `AdminMerchantControlStore.cs:124-169,190-245,268-344,437-467,630-647`, `ConcurrencyEtags.cs:14-42`)

```mermaid
flowchart TD
    START((●)) --> GATE["policy admin + permission merchant.manage<br/>ดู § 0.1"]
    GATE --> KIND{"POST (สร้าง) หรือ PATCH (แก้)?"}

    KIND -->|"PATCH"| HDRM{"If-Match รูป quoted vN?"}
    HDRM -->|"no"| R400E["400 ProblemDetails<br/>code invalid_etag"]
    HDRM -->|"yes"| HDRI{"Idempotency-Key ถูกรูป?<br/>ไม่ว่าง <= 200 ตัว ไม่มี control char"}
    KIND -->|"POST"| HDRI
    HDRI -->|"no"| R400I["400 ProblemDetails<br/>code invalid_idempotency_key"]
    HDRI -->|"yes"| ACCOK{"EnsureAccess:<br/>merchantId อยู่ใน Accessible?"}
    ACCOK -->|"no"| R403["403 ProblemDetails<br/>code merchant_scope_forbidden"]
    ACCOK -->|"yes"| PKIND{"POST หรือ PATCH?"}

    PKIND -->|"POST"| PARENT{"merchant แม่พบ<br/>(sale: branch เป็นของ merchant นี้ด้วย)?"}
    PARENT -->|"merchant ไม่พบ"| R404N["404<br/>NotFoundException ไม่มี code"]
    PARENT -->|"branch ผิด merchant"| R400X["400 ProblemDetails<br/>code cross_merchant_reference"]
    PARENT -->|"yes"| IDEMP["FindOperationAsync<br/>actor, operation, key, intentHash"]
    PKIND -->|"PATCH"| IDEMP

    IDEMP --> PRIOR{"มี OperationRecord เดิม?"}
    PRIOR -->|"hash ต่าง"| R409K["409 ProblemDetails<br/>code idempotency_key_reused"]
    PRIOR -->|"ยังไม่ Succeeded"| R409P["409 ProblemDetails<br/>code operation_in_progress"]
    PRIOR -->|"Succeeded"| REPLAY["คืน response เดิม (Replayed)"]
    PRIOR -->|"ไม่มี"| KIND2{"POST หรือ PATCH?"}

    KIND2 -->|"POST"| DUP{"code ซ้ำใน merchant นี้?"}
    DUP -->|"yes"| R409D["409 ProblemDetails<br/>code branch_code_exists / sale_code_exists"]
    DUP -->|"no"| CREATE["สร้าง entity ใหม่<br/>Branch.Create / Sale.Create"]

    KIND2 -->|"PATCH"| LOAD{"target (branch/sale/merchant) พบ?"}
    LOAD -->|"no"| R404N
    LOAD -->|"yes"| VER{"Version ตรง If-Match?"}
    VER -->|"no"| R412["412 ProblemDetails<br/>code precondition_failed<br/>HandleKnownErrors: ConcurrencyConflictException"]
    VER -->|"yes"| MOVE{"เฉพาะ sale: เปลี่ยน BranchId?"}
    MOVE -->|"yes ไม่มี reason"| R400R["400 ProblemDetails<br/>code reason_required"]
    MOVE -->|"yes branch ผิด merchant"| R400X
    MOVE -->|"yes ผ่าน"| SQLU["raw SQL UPDATE Sales<br/>SET BranchId WHERE Version = expected<br/>optimistic ไม่ผ่าน EF change tracking"]
    SQLU --> SQLOK{"เปลี่ยน 1 แถวพอดี?"}
    SQLOK -->|"no"| R412
    SQLOK -->|"yes"| UPDATE["mutate field อื่น<br/>name / status"]
    MOVE -->|"no"| UPDATE

    CREATE --> COMPLETE["operation.Complete + SaveChanges + commit"]
    UPDATE --> COMPLETE
    COMPLETE --> RESP["POST: 201 Location + ETag v1<br/>PATCH: 200 + ETag vN"]
    RESP --> END_S((◉))
    REPLAY --> END_S
    R400I --> END_F((◉))
    R400E --> END_F
    R403 --> END_F
    R404N --> END_F
    R400X --> END_F
    R409K --> END_F
    R409P --> END_F
    R409D --> END_F
    R412 --> END_F
    R400R --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    class CREATE,UPDATE,COMPLETE,RESP,END_S ok
    class R400I,R400E,R403,R404N,R400X,R409K,R409P,R409D,R412,R400R,END_F fail
    class HDRI,HDRM,ACCOK,PARENT,PRIOR,KIND,PKIND,KIND2,DUP,LOAD,VER,MOVE,SQLOK gate
    class REPLAY warn
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| PATCH | `/api/v1/merchants/{merchantId:guid}` | KIND=PATCH, ก่อนถึง GATE ต่อไปนี้มีเช็คพิเศษ: `body.MerchantId` (ถ้าส่งมา) ต้องตรง route มิฉะนั้น 400 `validation_failed` ก่อนแม้แต่ header, ไม่มี PARENT check, `LOAD` จาก `db.Merchants`, mutate = `Update()` + `Reactivate()`/`Suspend()` ตาม `body.Status` (`body.Status` ที่ไม่รู้จัก = 400 `invalid_filter` จาก `ParseMerchantStatus`), ไม่มีขั้น MOVE (`PatchMerchantAsync` `AdminMerchantControlStore.cs:124-169,602-615`) |
| POST | `/api/v1/merchants/{merchantId:guid}/branches` | KIND=POST, PARENT = `EnsureMerchantExistsAsync` เท่านั้น, DUP = code ซ้ำใน Branches, CREATE = `Branch.Create` (`CreateBranchAsync` `AdminMerchantControlStore.cs:190-213`) |
| PATCH | `/api/v1/merchants/{merchantId:guid}/branches/{branchId:guid}` | KIND=PATCH, `LOAD` จาก `db.Branches` ตรง branchId+merchantId, ไม่มีขั้น MOVE, mutate = `Rename`/`Enable`/`Disable` ตาม `body.Status` (ไม่รู้จัก = 400 `invalid_filter` จาก `ParseBranchStatus`) (`UpdateBranchAsync` `AdminMerchantControlStore.cs:215-245,616-622`) |
| POST | `/api/v1/merchants/{merchantId:guid}/sales` | KIND=POST, PARENT = `EnsureMerchantExistsAsync` + `EnsureBranchAsync` (branch ต้องเป็นของ merchant นี้), DUP = code ซ้ำใน Sales, CREATE = `Sale.Create` ผูก BranchId (`CreateSaleAsync` `AdminMerchantControlStore.cs:268-292`) |
| PATCH | `/api/v1/merchants/{merchantId:guid}/sales/{saleId:guid}` | KIND=PATCH, มีขั้น MOVE เต็ม: เปลี่ยน BranchId ต้องมี reason + `EnsureBranchAsync` + raw SQL optimistic update (ไม่ผ่าน EF), mutate อื่น = `Rename`/`Enable`/`Disable` ตาม `body.Status` (ไม่รู้จัก = 400 `invalid_filter` จาก `ParseSaleStatus`) (`UpdateSaleAsync` `AdminMerchantControlStore.cs:294-344,623-629`) |

---

## 7.3 เขียน Provider Account (create / patch / disable)

header/existence gate เหมือนกันทั้งสาม, แต่ validation อยู่ *ก่อน* idempotency lookup เสมอ (hash ต้องคำนวณจากค่าที่ validate แล้ว) — ดู Notes แถว "โอกาส 500" สำหรับ bug จริงที่พบใน patch/disable (source: `CanonicalProviderConfigurationEndpoints.cs:76-241`, `AdminPaymentsControlStore.cs:577-666,1606-1642,1678-1695,2129-2139`, `Connection.cs:97-102`)

```mermaid
flowchart TD
    START((●)) --> GATE["policy admin + permission settings.manage<br/>ดู § 0.1"]
    GATE --> KIND{"create, patch หรือ disable?"}

    KIND -->|"create"| HDRI{"Idempotency-Key ถูกรูป?"}
    KIND -->|"disable"| REASON{"reason ไม่ว่าง?<br/>เช็คก่อนแม้แต่ lookup"}
    REASON -->|"no"| R400R["400 code reason_required"]
    REASON -->|"yes"| PRE
    KIND -->|"patch"| PRE{"provider account พบ<br/>เช็คใน endpoint ก่อน header/store"}
    PRE -->|"no"| R404N["404<br/>NotFoundException ไม่มี code"]
    PRE -->|"yes"| HDRM{"If-Match รูป vN?"}
    HDRM -->|"no"| R400E["400 code invalid_etag"]
    HDRM -->|"yes"| HDRI
    HDRI -->|"no"| R400I["400 code invalid_idempotency_key"]
    HDRI -->|"yes"| ACCESS{"EnsureAccess:<br/>merchantId อยู่ใน Accessible?<br/>(patch/disable: unreachable, PRE merge-check ผ่านแล้ว)"}
    ACCESS -->|"no (เกิดได้จริงเฉพาะ create)"| R403["403 code merchant_scope_forbidden"]
    ACCESS -->|"yes"| KIND2{"create หรือ patch/disable?<br/>ใน store"}

    KIND2 -->|"create"| CLOAD{"merchant แม่พบ?<br/>LoadMerchantAsync"}
    CLOAD -->|"no"| R404N
    CLOAD -->|"yes"| CVAL{"displayName/config ถูกรูป และ<br/>ParseProviderId ถูกต้อง?"}
    CVAL -->|"no"| R400V["400 code validation_failed / invalid_psp_config"]
    CVAL -->|"yes"| PROV{"LoadProviderAsync: provider row พบ<br/>และ IsEnabled?"}
    PROV -->|"row ไม่พบใน catalog (SQL Server ล้วน)"| R500["500 default<br/>PaymentCapabilityUnavailableException ไม่ถูก catch"]
    PROV -->|"disabled"| R409PD["409 code provider_disabled"]
    PROV -->|"enabled"| CENV{"ParseEnvironment ถูกต้อง?"}
    CENV -->|"no"| R400V
    CENV -->|"yes"| IDEMP["FindOperationAsync<br/>actor, psp.account.create/psp.update, key"]

    KIND2 -->|"patch/disable"| MVAL{"ValidateMethods + ValidateConfig ผ่าน?"}
    MVAL -->|"no"| R400M["400 code validation_failed / invalid_psp_config"]
    MVAL -->|"yes"| IDEMP

    IDEMP --> PRIOR{"มี OperationRecord เดิม?"}
    PRIOR -->|"hash ต่าง"| R409K["409 code idempotency_key_reused"]
    PRIOR -->|"ยังไม่ Succeeded"| R409P["409 code operation_in_progress"]
    PRIOR -->|"Succeeded"| REPLAY["คืน response เดิม (Replayed)"]
    PRIOR -->|"ไม่มี"| KIND3{"create หรือ patch/disable?"}

    KIND3 -->|"create"| DUPP{"มี provider account<br/>ของ psp นี้อยู่แล้ว?"}
    DUPP -->|"yes"| R409D["409 code psp_connection_exists"]
    DUPP -->|"no"| CREATE["Connection.Create (disabled, ไม่มี secret)<br/>SyncAccountMethodsAsync([]) ไม่มี method ให้ enable"]

    KIND3 -->|"patch/disable"| LEASE{"authorizationLease.VerifyAsync ผ่าน?"}
    LEASE -->|"no"| R403L["403 code authorization_stale<br/>global handler ไม่ผ่าน HandleKnownErrors"]
    LEASE -->|"yes"| VER{"Version ตรง If-Match?"}
    VER -->|"no"| R412["412 code precondition_failed"]
    VER -->|"yes"| PLOAD{"LoadProviderAsync ของ connection.Psp<br/>พบใน catalog?"}
    PLOAD -->|"ไม่พบ (SQL Server ล้วน)"| R500
    PLOAD -->|"พบ"| SETIE["connection.Update(methods, config, isEnabled)<br/>set IsEnabled ทันที (Connection.cs:97-102)"]
    SETIE --> SYNC{"SyncAccountMethodsAsync: methods ไม่ว่าง<br/>และ connection.IsEnabled == false?<br/>(ทุกครั้งที่ patch/disable ตั้ง false ทั้งที่มี method เดิม)"}
    SYNC -->|"yes"| R500B["500 default<br/>PaymentCapabilityUnavailableException ไม่ถูก catch<br/>(ผลข้างเคียงของ disable แทบทุกครั้งที่มี method เปิดอยู่)"]
    SYNC -->|"no (methods ว่าง หรือ IsEnabled=true)"| UPDATE["sync MerchantProviderAccountMethods ปกติ"]

    CREATE --> COMPLETE["operation.Complete + SaveChanges + commit"]
    UPDATE --> COMPLETE
    COMPLETE --> RESP["create: 201 Location + ETag v1<br/>patch/disable: 200 + ETag vN"]
    RESP --> END_S((◉))
    REPLAY --> END_S
    R400I --> END_F((◉))
    R400E --> END_F
    R404N --> END_F
    R400R --> END_F
    R403 --> END_F
    R400V --> END_F
    R500 --> END_F
    R409PD --> END_F
    R400M --> END_F
    R409K --> END_F
    R409P --> END_F
    R409D --> END_F
    R403L --> END_F
    R412 --> END_F
    R500B --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    class CREATE,UPDATE,COMPLETE,RESP,END_S ok
    class R400I,R400E,R404N,R400R,R403,R400V,R500,R409PD,R400M,R409K,R409P,R409D,R403L,R412,R500B,END_F fail
    class HDRI,HDRM,PRE,REASON,ACCESS,PRIOR,KIND,KIND2,KIND3,CLOAD,CVAL,PROV,CENV,MVAL,DUPP,LEASE,VER,PLOAD,SYNC gate
    class REPLAY warn
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| POST | `/api/v1/merchants/{merchantId:guid}/provider-accounts` | KIND=create, ไม่มี `If-Match`, ไม่มี PRE-check (ไม่มี id ปลาย), เริ่ม `IsEnabled=false` และ `methods=[]` จึงไม่โดน SYNC bug (`CreateProviderAccountAsync` `AdminPaymentsControlStore.cs:577-627`) |
| PATCH | `/api/v1/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}` | KIND=patch, `body.IsEnabled` เป็น `bool?` — ไม่ส่งจะ fallback เป็น `current.IsEnabled` ที่อ่านจาก `GetConnectionAsync` ก่อน PATCH (`CanonicalProviderConfigurationEndpoints.cs:134`) ถ้าส่ง `IsEnabled=true` พร้อม methods valid จะผ่านปกติ, ถ้าส่ง `false` หรือไม่ส่งเลยขณะ `current.IsEnabled=false` (ค่าเริ่มต้นเสมอหลังสร้าง) พร้อม `EnabledMethods` ไม่ว่าง จะชน SYNC bug (500) เหมือนกันทั้งสองกรณี เพราะ `intent.IsEnabled` ตกเป็น false ทั้งคู่ (`UpdateConnectionAsync` `AdminPaymentsControlStore.cs:629-666`) |
| POST | `/api/v1/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}/disable` | KIND=disable, ต้องมี `Reason` (400 `reason_required`), ส่ง `EnabledMethods`/`Config` เดิมของ account กลับเข้าไปพร้อม `IsEnabled=false` เสมอ — ชน SYNC bug (500) ทุกครั้งที่ account มี method เปิดอยู่จริง ซึ่งเป็นกรณีใช้งานหลักของปุ่ม "หยุดฉุกเฉิน" (`CanonicalProviderConfigurationEndpoints.cs:215-241`) |

---

## 7.4 ทดสอบการเชื่อมต่อ Provider Account

probe adapter นอก transaction ด้วย secret จาก vault ก่อนเปิด transaction ที่สองมาบันทึกผล (health หรือ pending) พร้อม OperationRecord ที่เก็บ status 200/502 ไว้ replay — มีแค่ credential ที่ active เท่านั้น ไม่มี candidate-credential variant เหมือน legacy (source: `CanonicalProviderConfigurationEndpoints.cs:193-213`, `AdminPaymentsControlStore.cs:668-721`, `MerchantRuntimeAuthorizationLease.cs:38`)

```mermaid
flowchart TD
    START((●)) --> GATE["policy admin + permission settings.manage<br/>ดู § 0.1"]
    GATE --> PRE{"provider account พบ<br/>GetConnectionAsync เช็คก่อน header"}
    PRE -->|"no"| R404N["404<br/>NotFoundException ไม่มี code"]
    PRE -->|"yes"| HDR{"If-Match รูป vN<br/>และ Idempotency-Key ถูกรูป?<br/>ดู § 0.5"}
    HDR -->|"no"| R400H["400 code invalid_etag / invalid_idempotency_key"]
    HDR -->|"yes"| ACCESS{"EnsureAccess:<br/>merchantId อยู่ใน Accessible?<br/>(unreachable, PRE merge-check ผ่านแล้วเสมอ)"}
    ACCESS -->|"no (ไม่เกิดจริง)"| R403["403 code merchant_scope_forbidden"]
    ACCESS -->|"yes"| IDEM{"OperationRecord เดิม<br/>actor, psp.test, key?"}
    IDEM -->|"hash ต่าง"| R409K["409 code idempotency_key_reused"]
    IDEM -->|"ยังไม่ Succeeded"| R409P["409 code operation_in_progress"]
    IDEM -->|"Succeeded status 200"| REPLAY200["200 view เดิม (Replayed)"]
    IDEM -->|"Succeeded status 502"| REPLAY502["502 code psp_test_failed (replay)"]
    IDEM -->|"ไม่มี"| SNAP{"snapshot connection พบ<br/>นอก transaction?"}
    SNAP -->|"no"| R404N
    SNAP -->|"yes"| VER{"Version ตรง If-Match?"}
    VER -->|"no"| R412["412 code precondition_failed"]
    VER -->|"yes"| PROBE["นอก transaction:<br/>vault.ReadVersionForServer/Reveal<br/>adapter.TestConnectionAsync(secret, environment)<br/>exception ใด ๆ = succeeded false"]
    PROBE --> TXN["transaction ที่สอง:<br/>reload connection (404), EnsureVersion (412),<br/>authorizationLease.VerifyAsync (403 authorization_stale)"]
    TXN --> RECORD["RecordTest(succeeded, authenticated/probe_failed)<br/>operation.Complete(200 หรือ 502) + commit"]
    RECORD --> OUT{"probe สำเร็จ?"}
    OUT -->|"yes"| R200["200 PspConnectionView + ETag vN"]
    OUT -->|"no"| R502["502 code psp_test_failed<br/>state ล้มเหลวถูก commit แล้ว"]
    R200 --> END_S((◉))
    REPLAY200 --> END_S
    R400H --> END_F((◉))
    R404N --> END_F
    R403 --> END_F
    R409K --> END_F
    R409P --> END_F
    REPLAY502 --> END_F
    R412 --> END_F
    R502 --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    classDef ext fill:#3b2a5a,stroke:#a371f7,color:#fff
    class RECORD,R200,END_S ok
    class R400H,R404N,R403,R409K,R409P,REPLAY502,R412,R502,END_F fail
    class HDR,PRE,ACCESS,IDEM,SNAP,VER,OUT gate
    class REPLAY200,TXN warn
    class PROBE ext
```

---

## 7.5 สร้างคำขอเปลี่ยน payment setting (maker)

`/payment-setting-requests` (POST) dispatch เป็น 3 ชนิดตามรูป body (credential / routing / environment) ที่ล้วนจบด้วยเขียน `ApprovalRequested` ลง governance outbox ในธุรกรรมเดียวกับการ stage state เป็น pending — เหมือน § 0.7 OUT1 ทุกประการยกเว้น response code, ส่วน `/provider-accounts/{id}/credential-versions` (POST) เรียกฟังก์ชัน credential-kind เดียวกันตรง ๆ ผ่าน route คนละเส้น (source: `CanonicalProviderConfigurationEndpoints.cs:164-191,270-358`, `AdminPaymentsControlStore.cs:723-796,860-983,1064-1122,1503-1509`, `AdminControlEndpoints.cs:784-817`)

```mermaid
flowchart TD
    START((●)) --> GATE["policy admin + permission settings.manage<br/>ดู § 0.1"]
    GATE --> BODY{"ReadSecretBodyAsync:<br/>ไม่เกิน 16 KiB และ JSON parse ได้?<br/>Cache-Control no-store"}
    BODY -->|"เกิน 16 KiB"| R413["413 code request_too_large"]
    BODY -->|"JSON ผิดรูป/ว่าง"| R400J["400 code validation_failed"]
    BODY -->|"yes"| ROUTE{"endpoint ไหน?"}

    ROUTE -->|"POST payment-setting-requests"| V1{"baseVersion >= 1<br/>และ reason ไม่ว่าง?"}
    V1 -->|"no"| R400V["400 code validation_failed"]
    V1 -->|"yes"| ETAGP{"If-Match รูป vN?"}
    ETAGP -->|"no"| R400E["400 code invalid_etag"]
    ETAGP -->|"yes"| VERB{"body.baseVersion ==<br/>If-Match version?"}
    VERB -->|"no"| R412V["412 code precondition_failed<br/>HandleKnownErrors: ConcurrencyConflictException"]
    VERB -->|"yes"| KIND{"body มี ProviderAccountId+SecretFields<br/>หรือ RulesetId<br/>หรือ Environment+Connections?"}
    KIND -->|"ไม่ตรงทั้ง 3"| R400N["400 code validation_failed<br/>a provider account, ruleset or<br/>target environment is required"]

    ROUTE -->|"POST credential-versions"| CVBODY{"secretFields ไม่ว่าง<br/>และ keyId ไม่ว่าง?"}
    CVBODY -->|"no"| R400V
    CVBODY -->|"yes"| CVFOUND{"provider account พบ<br/>GetConnectionAsync?"}
    CVFOUND -->|"no"| R404N["404<br/>NotFoundException ไม่มี code"]
    CVFOUND -->|"yes"| ETAGP2{"If-Match รูป vN?"}
    ETAGP2 -->|"no"| R400E
    ETAGP2 -->|"yes"| CRED

    KIND -->|"credential"| CRED["EnsureAccess (403)<br/>load connection+merchant (404)<br/>ValidateSecretFields (400) + envelopeFactory.Build<br/>idempotency (409 reused/in-progress หรือ replay)<br/>PendingApprovalId/merchant pending (409 approval_pending)<br/>legacy snapshot (409 legacy_snapshot_blocked)<br/>authorizationLease (403 authorization_stale)<br/>EnsureVersion (412)"]
    KIND -->|"routing"| ROUTING["EnsureAccess (403)<br/>load ruleset (404), EnsureVersion (412)<br/>ValidateRulesAsync + EnsureRoutingCoverageAsync<br/>(400/409 ตาม domain rule)<br/>idempotency (409/replay), authorizationLease (403)"]
    KIND -->|"environment"| ENVK["EnsureAccess (403)<br/>parse target env (400)<br/>โหลด merchant (404), เท่าเดิม (400 validation_failed)<br/>validate connections + credential ต่อ connection (400)<br/>idempotency lookup (409 reused/in-progress หรือ replay)<br/>ทุก connection ต้องมี credential ครบ<br/>(409 environment_credentials_incomplete)<br/>Omise live ต้องยืนยัน webhook (409 webhook_not_ready)<br/>pending checks (409), legacy snapshot (409)<br/>authorizationLease (403), EnsureVersion (412)"]

    CRED --> STAGE["vault.StageVersionAsync<br/>StageSecretVersion / StagePaymentEnvironment / RequestActivation<br/>เขียน ApprovalRequested ลง governance outbox<br/>ในธุรกรรมเดียว, OperationRecord.Complete(202) ภายใน<br/>ไม่ใช่ response จริง"]
    ROUTING --> STAGE
    ENVK --> STAGE
    STAGE -.async.-> DISP["GovernanceOutboxDispatcher<br/>เหมือน § 0.7 OUT1/DISP1: เขียน<br/>ApprovalRequest Pending v1"]
    STAGE --> RESP{"endpoint ไหน?"}
    RESP -->|"payment-setting-requests"| R201A["201 Created<br/>Location .../payment-setting-requests/{approvalId}<br/>ต่างจาก § 0.7 ที่ตอบ 202"]
    RESP -->|"credential-versions"| R201B["201 Created<br/>Location .../credential-versions/{candidateVersionId}<br/>ETag = current.Version + 1<br/>คำนวณจาก snapshot ก่อนเขียน ไม่ใช่ผลจริงหลัง commit"]

    R201A --> END_S((◉))
    R201B --> END_S
    R413 --> END_F((◉))
    R400J --> END_F
    R400V --> END_F
    R400E --> END_F
    R412V --> END_F
    R400N --> END_F
    R404N --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef ext fill:#3b2a5a,stroke:#a371f7,color:#fff
    class R201A,R201B,END_S ok
    class R413,R400J,R400V,R400E,R412V,R400N,R404N,END_F fail
    class BODY,ROUTE,V1,ETAGP,VERB,KIND,CVBODY,CVFOUND,ETAGP2,RESP gate
    class STAGE ext
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| POST | `/api/v1/merchants/{merchantId:guid}/payment-setting-requests` | dispatch 3 kind ตาม body (credential / routing / environment), ต้องส่ง `baseVersion`+`reason` แยกจาก `If-Match`, mismatch ระหว่างสองค่านี้ = 412 ไม่ใช่ 400, เขียน operation record ภายในเป็น 202 แต่ response จริงคือ 201 Created เสมอ ต่างจากรูปแบบทั่วไปของ § 0.7 ที่ตอบ 202 (`CanonicalProviderConfigurationEndpoints.cs:310-358`) |
| POST | `/api/v1/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}/credential-versions` | เข้าถึง provider account ตรง (ไม่มี `baseVersion`/`reason` แยก), ExpectedVersion มาจาก `If-Match` อย่างเดียว, connection ไม่พบ = 404 เปล่า, ETag คำนวณ `current.Version + 1` จาก snapshot ก่อนเขียน (อาจไม่ตรง state จริงถ้ามีการเปลี่ยนแปลงคั่นกลาง), response 201 Created ไปที่ `credential-versions/{candidateVersionId}`, เรียก `RequestCredentialChangeAsync` ตัวเดียวกับ kind credential ของแถวบน (`CanonicalProviderConfigurationEndpoints.cs:164-191`) |

---

## 7.6 อนุมัติ / ปฏิเสธ payment setting request (checker)

ใช้ `GovernanceStore.DecideAsync` ตัวเดียวกับ § 0.7 (route คนละเส้นจาก `/approvals/{approvalId}`) เข้าถึงผ่าน merchant-scoped path ที่กรอง target type เฉพาะ payment setting — rule/version conflict คืน 409 ตาม § 0.7 เป๊ะ แต่ response สำเร็จเป็น **200** ไม่ใช่ 202 และ validation เป็น presence-only (source: `CanonicalProviderConfigurationEndpoints.cs:364-395`, `GovernanceStore.cs:64-146`)

```mermaid
flowchart TD
    START((●)) --> GATE["policy admin + permission settings.manage<br/>ดู § 0.1"]
    GATE --> VALID{"reason และ targetVersion ไม่ว่าง?<br/>presence เท่านั้น"}
    VALID -->|"no"| R400["400 code validation_failed"]
    VALID -->|"yes"| FETCH["GetApprovalAsync<br/>merge access ผ่าน ApplyAccess"]
    FETCH --> FOUND{"พบ และ MerchantId ตรง route<br/>และเป็น payment-setting type<br/>credential/environment/routing?"}
    FOUND -->|"no"| R404["404<br/>Results.NotFound() เปล่า<br/>ไม่มี ProblemDetails code"]
    FOUND -->|"yes"| HDR{"If-Match รูป vN<br/>และ Idempotency-Key ถูกรูป?"}
    HDR -->|"no"| R400H["400 code invalid_etag / invalid_idempotency_key"]
    HDR -->|"yes"| DECIDE["governance.DecideAsync (DecisionIntent)<br/>ฟังก์ชันเดียวกับ § 0.7 checker"]
    DECIDE --> IDEM{"OperationRecord เดิม<br/>actor, Approve/RejectRequest, key?"}
    IDEM -->|"hash ต่าง"| R409K["409 code idempotency_key_reused"]
    IDEM -->|"ยังไม่ Succeeded"| R409P["409 code operation_in_progress"]
    IDEM -->|"Succeeded"| REPLAY["200 response เดิม (Replayed)"]
    IDEM -->|"ไม่มี"| SCOPE{"admin.Allows(approval.MerchantId)?<br/>ปกติผ่านแล้วเพราะ FETCH กรองมาก่อน"}
    SCOPE -->|"no"| R403S["403 code merchant_scope_forbidden"]
    SCOPE -->|"yes"| PERM{"admin มี approval.RequiredPermission?"}
    PERM -->|"no"| R403P["403 code underlying_permission_forbidden"]
    PERM -->|"yes"| RULES{"ApprovalRequest.Decide: ไม่ใช่ maker เอง,<br/>status Pending, version + targetVersion ตรง?<br/>เหมือน § 0.7 RULES"}
    RULES -->|"maker เอง"| R403M["403 code maker_cannot_decide"]
    RULES -->|"ไม่ Pending / version stale"| R409A["409 code approval_not_pending"]
    RULES -->|"targetVersion เปลี่ยน"| R409T["409 code target_version_changed"]
    RULES -->|"yes"| DECIDED["status Approved/Rejected, Version++<br/>เขียน ApprovalDecided ลง governance outbox<br/>OperationRecord.Complete(202) ภายใน"]
    DECIDED --> R200["200 CanonicalPaymentSettingRequestView + ETag v2<br/>ต่างจาก § 0.7: 202 pending"]
    R200 --> END_S((◉))
    REPLAY --> END_S
    DECIDED -.async.-> ASYNC["GovernanceOutboxDispatcher + IApprovalDecisionExecutor<br/>เหมือน § 0.7 DISP2/HANDLER/APPLY/REPORT/DISP3/FINAL ทุกประการ"]
    R400 --> END_F((◉))
    R404 --> END_F
    R400H --> END_F
    R409K --> END_F
    R409P --> END_F
    R403S --> END_F
    R403P --> END_F
    R403M --> END_F
    R409A --> END_F
    R409T --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    class DECIDED,R200,END_S ok
    class R400,R404,R400H,R409K,R409P,R403S,R403P,R403M,R409A,R409T,END_F fail
    class VALID,FOUND,HDR,IDEM,SCOPE,PERM,RULES gate
    class REPLAY,ASYNC warn
```

| Method | fullPath | ต่างจาก § กลางตรงไหน |
| --- | --- | --- |
| POST | `/api/v1/merchants/{merchantId:guid}/payment-setting-requests/{requestId:guid}/approve` | decision = Approve, response 200 (ไม่ใช่ 202 เหมือน § 0.7 ทั่วไป), validation เป็น presence-only (code `validation_failed` ไม่ใช่ `invalid_request` ของ § 0.7) (`MapDecision` `CanonicalProviderConfigurationEndpoints.cs:364-395`) |
| POST | `/api/v1/merchants/{merchantId:guid}/payment-setting-requests/{requestId:guid}/reject` | decision = Reject, ที่เหลือเหมือนแถวบนทุกจุด |

---

## Deviations

ไม่พบ deviation ระหว่างเอกสารกับ source — คอลัมน์ caller/auth policy ของทุก 24 แถวใน `docs/reference/api-endpoints.md` (policy `admin`, permission `merchant.view`/`merchant.manage`/`settings.manage`) ตรงกับ `.RequireAuthorization("admin").RequirePermission(...)` ใน source ทุกตัว; admin เป็น Bearer JWT จึงไม่มี CSRF filter (admin double-submit ถูก retire)

## Notes

| เรื่อง | ข้อเท็จจริงจาก source | source |
| --- | --- | --- |
| 412 ไม่ใช่ 409 | ทั้งสองไฟล์มี `HandleKnownErrors` filter เฉพาะโมดูลที่ catch `ConcurrencyConflictException` แล้วคืน **412 Precondition Failed** code `precondition_failed` แทน 409 Conflict ที่เป็นค่า default ทั่วระบบ — มีผลกับทุก endpoint ใน § 7.2, 7.3, 7.4, 7.5 ที่ตรวจ version | `CanonicalMerchantConfigurationEndpoints.cs:247-266`, `CanonicalProviderConfigurationEndpoints.cs:456-487`, `ProblemDetailsExceptionHandler.cs:77-78` |
| 201 vs 202 (maker) | endpoint maker ทั้งสอง (§ 7.5) ตอบ **201 Created** เสมอ ทั้งที่ domain layer บันทึก `OperationRecord` ด้วย status 202 ภายใน ต่างจากรูปแบบทั่วไปของ § 0.7 (`/approvals`) ที่ endpoint ตอบ 202 ตรง ๆ | `CanonicalProviderConfigurationEndpoints.cs:310-358,164-191` |
| 200 vs 202 (checker) | endpoint approve/reject ของ theme นี้ (§ 7.6) ตอบ 200 ไม่ใช่ 202 ทั้งที่ domain logic (`GovernanceStore.DecideAsync`) เดียวกับ § 0.7 บันทึก 202 ภายในเช่นกัน | `CanonicalProviderConfigurationEndpoints.cs:364-395`, `GovernanceStore.cs:140` |
| 3 รูป 404 | § 7.1 มี 404 อย่างน้อย 3 รูปที่ไม่เหมือนกัน: (a) `Results.Problem(404, code: not_found)` (merchant, provider-account), (b) `NotFoundException` ไม่ใส่ code เลยทำให้ ProblemDetails ไม่มี field `code` (§ 7.2/7.3 เมื่อ load target ไม่เจอ), (c) `Results.NotFound()` เปล่า (setting-request, payment-settings, credential-versions list, provider methods) | `NotFoundException.cs:9-14`, endpoint files ตามตารางแต่ละ § |
| ACCESS หลัง PRE เป็น dead branch (§ 7.3 patch/disable, § 7.4) | endpoint เรียก `store.GetConnectionAsync(providerAccountId, merchantId, Access(scope), ct)` เป็น PRE ก่อนเสมอ ซึ่ง merge-check `access.Allows(row.MerchantId)` ด้วย `access`/`merchantId` ค่าเดียวกับที่ `EnsureAccess` ในสโตร์เมธอดถัดไปจะเช็คซ้ำ — เมื่อ PRE ผ่าน (ไม่ null) `EnsureAccess` ที่ตามมาจึงไม่มีทาง throw ได้อีก นอก scope จะโดน 404 จาก PRE เสมอ ไม่ใช่ 403 (ยกเว้น create ใน § 7.3 ที่ไม่มี PRE มาก่อน จึงยังเกิด 403 จริงได้) Class sweep: § 7.5 node CRED (`RequestCredentialChangeAsync` -> `EnsureAccess` line 727) เป็น dead branch เดียวกันเมื่อเข้าทาง route `POST credential-versions` (CVFOUND ทำ PRE มาก่อนแล้ว) แต่ยังเกิด 403 จริงเมื่อเข้าทาง route `POST payment-setting-requests` kind credential (ไม่มี PRE) — ไม่ปรับ diagram § 7.5 เพราะ fix ของ issue นี้ระบุ scope เฉพาะ § 7.3/7.4 | `AdminPaymentsControlStore.cs:98-107` (`GetConnectionAsync`), เทียบกับ `AdminPaymentsControlStore.cs:633` (`UpdateConnectionAsync`), `:671` (`TestConnectionAsync`), `:727` (`RequestCredentialChangeAsync`); เรียกจาก `CanonicalProviderConfigurationEndpoints.cs:130-131` (PATCH), `:226-227` (disable), `:201-202` (connection-tests), `:175-176` (credential-versions POST, PRE only) |
| โอกาส 500 โดยไม่ตั้งใจ (§ 7.3, code analysis ยังไม่ reproduce) | `SyncAccountMethodsAsync` เรียก `EnsureAccountMethodCanEnable(..., enabled: true)` แบบ literal ไม่ผูกกับ `intent.IsEnabled`, ขณะที่ `connection.Update(...)` set `IsEnabled` ไปก่อนแล้ว — PATCH ที่ตั้ง `IsEnabled=false` พร้อม method เดิม หรือ endpoint `/disable` (ส่ง `EnabledMethods` เดิมกลับเข้าไปเสมอ) โยน `PaymentCapabilityUnavailableException` ที่ไม่ถูก catch ทั้งใน `HandleKnownErrors` ของไฟล์นี้และใน `ProblemDetailsExceptionHandler` กลาง จึงตกเป็น 500 แทนที่จะเป็น 409 ที่ตั้งใจ (legacy `AdminControlEndpoints.cs:1038` catch exception เดียวกันแล้วคืน 409) — ยังไม่ได้ยืนยันด้วยการรันจริง | `AdminPaymentsControlStore.cs:1606-1642,1678-1688`, `Connection.cs:97-102` |
| LoadProviderAsync บน SQL Server | ถ้า `cfg.PaymentProviders` ไม่มีแถวของ psp ที่รองรับ (data ไม่ครบ) ทั้ง POST create (#13, line 588) และ PATCH/disable (#15, #19, line 654) ก็ชน `PaymentCapabilityUnavailableException` ที่ไม่ถูก catch เช่นกัน (500) — เป็น data-integrity edge case ไม่ใช่ path ปกติ | `AdminPaymentsControlStore.cs:588,654,1697-1722` |
| #18 ETag คำนวณจาก snapshot | `POST .../credential-versions` เซ็ต `ETag = current.Version + 1` จากค่าที่อ่านก่อนเขียน ไม่ใช่ version จริงหลัง commit — ถ้ามี mutation อื่นแทรกกลางทาง ETag ที่ส่งกลับอาจไม่ตรง resource จริง | `CanonicalProviderConfigurationEndpoints.cs:164-191` |
| #6 paging สองชั้น | `GET .../payment-setting-requests` ดึง governance approvals หน้าแรก 100 แถวก่อนแล้ว filter type + skip/take เอง — merchant ที่มี approval (ทุกประเภทรวมกัน) เกิน 100 รายการจะเห็นหน้าถัดไปของ payment-setting request ขาดหายแม้ query page/limit จะถูกต้อง | `GovernanceStore.cs:23-62`, `CanonicalProviderConfigurationEndpoints.cs:272-290` |
| catalog คงที่ในโค้ด | payment provider catalog (#23, #24) เป็นค่าคงที่ในโค้ด (2c2p, Omise) ไม่ได้มาจาก DB การเพิ่ม provider ใหม่ต้องแก้โค้ดนี้โดยตรง อยู่นอก frame ของ diagram | `CanonicalProviderConfigurationEndpoints.cs:397-416` |
| เลขบรรทัดใน theme file | ไฟล์ theme อ้าง L148-L171 แต่แถวจริงของ 24 endpoint ในเอกสารอยู่ L150-L173 (section heading L146, table header L148, เลื่อน 2 บรรทัด) เนื้อหาแถวตรงกันทุกคอลัมน์ | `docs/reference/api-endpoints.md:146-173` |

**Render**: GitHub / Obsidian / VS Code Mermaid
