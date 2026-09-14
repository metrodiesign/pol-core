# pol-core API — Admin merchant console, merchant roles และ originators (Activity Diagrams)

> Source: `docs/reference/api-endpoints.md` section "Admin control และ merchant console" บรรทัด L233-L253 และ source ที่อ้างต่อ § (`src/Api/Api/ControlPlane/AdminControlEndpoints.cs`, `AdminMerchantIdentityEndpoints.cs`, `src/Application/Modules/Merchants.Application/AdminControlPlane/AdminMerchantControl.cs`, `Merchants.Application/Users/ManageMerchantUsers.cs`, `SetUserRoles.cs`, `src/Infrastructure/Persistence/Persistence.ControlPlane/Merchants/AdminMerchantControlStore.cs`, `Payments/PaymentAuthorizationSqlLockManager.cs`, `src/Application/Modules/Iam.Application/Roles/*`, `src/Domain/Modules/Iam.Domain/Roles/Role.cs`, `src/Domain/Modules/Merchants.Domain/Merchant.cs`, `Originator.cs`, `Users/User.cs`)
> Scope: 21 endpoints — Admin console อ่าน/แก้ร้านค้า, บทบาทของร้านค้า, ผู้ใช้ร้านค้า และ Originator (branch/agent/broker/staff/app) ทั้งหมดอยู่ใต้ policy `admin`
> Generated: 2026-09-14

| § | Diagram | Endpoints |
| --- | --- | --- |
| 10.1 | Merchant / Originator read model (list ×2 + originator detail) | `GET /api/v1/merchants`, `GET /api/v1/originators`, `GET /api/v1/originators/{originatorId:guid}` |
| 10.2 | แก้ไข / ระงับ / เปิดใช้งานร้านค้าอีกครั้ง (idempotent executor + SQL lock) | `PUT /api/v1/merchants/{merchantId:guid}`, `POST /api/v1/merchants/{merchantId:guid}/suspend`, `POST /api/v1/merchants/{merchantId:guid}/reactivate` |
| 10.3 | Merchant role และ permission read model | `GET /api/v1/merchants/{merchantId:guid}/permissions`, `GET /api/v1/merchants/{merchantId:guid}/roles`, `GET /api/v1/merchants/{merchantId:guid}/roles/{code}` |
| 10.4 | Merchant role CRUD | `POST /api/v1/merchants/{merchantId:guid}/roles`, `PUT /api/v1/merchants/{merchantId:guid}/roles/{code}`, `DELETE /api/v1/merchants/{merchantId:guid}/roles/{code}` |
| 10.5 | ผู้ใช้ร้านค้า: edit view (reveal audit) + update โดย Admin | `GET /api/v1/merchants/{merchantId:guid}/users/{merchantUserId:guid}/edit`, `PUT /api/v1/merchants/{merchantId:guid}/users/{merchantUserId:guid}` |
| 10.6 | เชิญผู้ใช้เข้าร้านค้าโดย Admin (idempotent, async email outbox) | `POST /api/v1/merchants/{merchantId:guid}/user-invitations` |
| 10.7 | กำหนดบทบาทให้ผู้ใช้ร้านค้าโดย Admin | `PUT /api/v1/merchants/{merchantId:guid}/users/{merchantUserId:guid}/roles` |
| 10.8 | สร้าง Originator | `POST /api/v1/originators` |
| 10.9 | แก้ไข / เปิดใช้งาน / ปิดใช้งาน Originator (If-Match เท่านั้น ไม่มี Idempotency-Key) | `PUT /api/v1/originators/{originatorId:guid}`, `POST /api/v1/originators/{originatorId:guid}/enable`, `POST /api/v1/originators/{originatorId:guid}/disable` |
| 10.10 | ลบ Originator (soft-disable เมื่อยังถูก routing rule อ้างอิง) | `DELETE /api/v1/originators/{originatorId:guid}` |

หมายเหตุ gate ท้องถิ่นของ theme นี้: endpoint ที่อ่านผ่าน `AdminMerchantIdentityEndpoints.cs` (§ 10.3-10.7) เช็ค `RequireReadAccess`/`RequireMutationAccess` (merchant นอก Admin scope) แยกจาก `RequirePermission` — read คืน **404** (กัน leak ว่ามี merchant จริงไหม), mutation คืน **403** `merchant_scope_forbidden`; ทุก endpoint ในกลุ่มนี้เช็คเพิ่มว่า merchant ต้อง Active (`RequireActiveMerchantAsync`, 404) ส่วน endpoint ที่แก้ตรงผ่าน `AdminControlEndpoints.cs` (§ 10.2, 10.8-10.10) ใช้ `EnsureAccess` เดียวกัน (403 mutation, 404 สำหรับ read ที่คืน null) แต่**ไม่เช็ค merchant Active เลย** (source: `AdminMerchantIdentityEndpoints.cs:284-301`, `AdminMerchantControlStore.cs:637-641`)

---

## 10.1 Merchant / Originator read model (list ×2 + originator detail)

ทั้งสาม endpoint ผ่าน policy `admin` + permission `merchant.view` แล้ว query ตรงแบบไม่มี transaction ไม่มี SFS filter เต็มรูป (typed filter ธรรมดา คล้าย variant "approvals/audits" ใน § 0.6) — list กรอง access แบบเงียบ (WHERE MerchantIds), ส่วน list originator ที่ระบุ `merchantId` เจาะจงเช็ค `EnsureAccess` แบบ throw (source: `AdminControlEndpoints.cs:316-367,397-435`, `AdminMerchantControlStore.cs:33-54,346-385`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy admin + permission merchant.view ดู § 0.1<br/>safe method ข้าม CSRF ดู § 0.3"]
    AUTHZ --> KIND{"endpoint?"}

    KIND -->|"GET /merchants"| PG1{"page >= 1, limit 1..100,<br/>status ∈ active/suspended ถ้ามี?"}
    PG1 -->|no| R400M["400 ProblemDetails<br/>code invalid_filter"]
    PG1 -->|yes| Q1["query: access filter เงียบ (WHERE MerchantIds ถ้าไม่ unrestricted)<br/>+ search code/name + status, OrderBy Code, Id"]
    Q1 --> R200M["200 PagedResult AdminMerchantListItem"]

    KIND -->|"GET /originators"| PG2{"page >= 1, limit 1..100?"}
    PG2 -->|no| R400O["400 ProblemDetails<br/>code invalid_filter"]
    PG2 -->|yes| MID{"query merchantId ให้มาด้วย?"}
    MID -->|yes| ACC2{"EnsureAccess: merchantId<br/>อยู่ใน Admin scope?"}
    ACC2 -->|no| R403["403 ProblemDetails<br/>code merchant_scope_forbidden"]
    ACC2 -->|yes| TYP
    MID -->|no| TYP{"type ∈ branch/agent/broker/staff/app ถ้ามี,<br/>status ∈ active/inactive ถ้ามี?"}
    TYP -->|no| R400T["400 ProblemDetails<br/>code invalid_type หรือ invalid_filter"]
    TYP -->|yes| Q2["query: access filter เงียบ (ไม่มี explicit merchantId)<br/>+ merchantId/type/status/search, OrderBy MerchantId, Code, Id"]
    Q2 --> R200O["200 PagedResult OriginatorView"]

    KIND -->|"GET /originators/{originatorId:guid}"| LOOKUP["SingleOrDefault: Id = originatorId<br/>(+ MerchantId = query merchantId ถ้าส่งมา)"]
    LOOKUP --> FOUND{"พบแถว และ access.Allows(row.MerchantId)?"}
    FOUND -->|no| R404["404 ProblemDetails<br/>(ไม่แยกว่าไม่พบ หรือ นอก scope)"]
    FOUND -->|yes| ETAG["VersionEtags.Set(vN)"]
    ETAG --> R200D["200 OriginatorView + ETag"]

    R200M --> END_S((◉))
    R200O --> END_S
    R200D --> END_S
    R400M --> END_F((◉))
    R403 --> END_F
    R400O --> END_F
    R400T --> END_F
    R404 --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class Q1,Q2,ETAG,R200M,R200O,R200D,END_S ok
    class R400M,R403,R400O,R400T,R404,END_F fail
    class KIND,PG1,MID,ACC2,PG2,TYP,LOOKUP,FOUND gate
```

| Method | fullPath | รายละเอียดเพิ่มเติม |
| --- | --- | --- |
| GET | `/api/v1/merchants` | ไม่มี `merchantId` filter จึงไม่มี branch 403, มีเฉพาะ 400 invalid_filter |
| GET | `/api/v1/originators` | `merchantId` เป็น query filter เจาะจง — นอก scope คืน **403** (ต่างจาก detail ที่คืน 404) |
| GET | `/api/v1/originators/{originatorId:guid}` | ไม่มี paging, คืน ETag, นอก scope หรือไม่พบยุบรวมเป็น 404 เดียว |

---

## 10.2 แก้ไข / ระงับ / เปิดใช้งานร้านค้าอีกครั้ง (idempotent executor + SQL lock)

ทั้งสาม endpoint ใช้ permission `merchant.manage`, ตรวจ `body.MerchantId` ตรง route ก่อน แล้วเข้า keyed "admin" transaction เดียวกัน: ล็อก SQL แบบ exclusive ต่อ merchant, หา `OperationRecord` เดิมด้วย (merchantId, adminId, operation, Idempotency-Key) เพื่อ replay, เช็ค version แล้ว mutate — เฉพาะ PUT มีขั้น sync payment-method policy เพิ่ม (source: `AdminControlEndpoints.cs:316-395,1064-1089`, `AdminMerchantControlStore.cs:56-113,437-473,637-647`, `PaymentAuthorizationSqlLockManager.cs:32-73`, `Merchant.cs:115-123,180-194`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy admin + permission merchant.manage ดู § 0.1"]
    AUTHZ --> BODY{"EnsureMerchant: body.MerchantId<br/>== route merchantId (ไม่ Guid.Empty)?"}
    BODY -->|no| R400V["400 ProblemDetails<br/>code validation_failed"]
    BODY -->|yes| HDR{"VersionEtags.Require(If-Match) รูป vN<br/>+ IdempotencyKeys.Require(Idempotency-Key) ดู § 0.5"}
    HDR -->|no| R400H["400 invalid_etag / invalid_idempotency_key"]
    HDR -->|yes| ACC{"keyed admin txn: EnsureAccess<br/>merchantId อยู่ใน Admin scope?"}
    ACC -->|no| R403["403 code merchant_scope_forbidden"]
    ACC -->|yes| LOCK["AcquireMerchantExclusiveAsync:<br/>sp_getapplock global (Shared) แล้ว per-merchant (Exclusive), timeout 15s<br/>(SQL Server เท่านั้น, no-op บน SQLite)"]
    LOCK --> LOCKOK{"ได้ lock ก่อน timeout?"}
    LOCKOK -->|no| R409L["409 code payment_authorization_busy"]
    LOCKOK -->|yes| PRIOR{"OperationRecord เดิม<br/>(merchantId, adminId, operation, key)?"}
    PRIOR -->|"hash ต่าง"| R409K["409 code idempotency_key_reused"]
    PRIOR -->|"status ยังไม่ Succeeded"| R409P["409 code operation_in_progress"]
    PRIOR -->|"succeeded"| REPLAY["คืน response เดิม + ETag เดิม"]
    PRIOR -->|"ไม่มี"| LOAD["LoadMerchantAsync(merchantId)"]
    LOAD --> FOUND{"พบ merchant?"}
    FOUND -->|no| R404["404 ProblemDetails"]
    FOUND -->|yes| VER{"merchant.Version = If-Match?"}
    VER -->|no| R409V["409 code state_conflict"]
    VER -->|yes| WHICH{"endpoint?"}
    WHICH -->|"PUT (update)"| SYNC["SyncMerchantPoliciesAsync:<br/>sync MerchantPaymentMethods ตาม enabledChannels<br/>ตรวจ qualifying provider account ต่อ channel (SQL Server เท่านั้น)"]
    SYNC --> SYNCOK{"ทุก channel มี qualifying<br/>provider account?"}
    SYNCOK -->|no| R409C["409 code payment_capability_unavailable"]
    SYNCOK -->|yes| VALIDU{"name ไม่ว่าง และ metadata ตรง allowlist schema<br/>(JsonUnmappedMemberHandling.Disallow)?"}
    VALIDU -->|no| R400U["400 (ArgumentException ถ้า name ว่าง,<br/>JsonException ถ้า metadata ผิด schema)"]
    VALIDU -->|yes| MUT["merchant.Update(name, note, channels, metadata)"]
    WHICH -->|"POST suspend / reactivate"| MUT2["merchant.Suspend() / Reactivate()<br/>no-op ถ้า status ตรงอยู่แล้ว (ไม่ bump version, ไม่มี audit)"]
    MUT --> COMPLETE
    MUT2 --> COMPLETE["OperationRecord.Complete(200) + SaveChanges"]
    COMPLETE --> R200["200 AdminMerchantListItem + ETag vN ใหม่"]

    R200 --> END_S((◉))
    REPLAY --> END_S
    R400V --> END_F((◉))
    R400H --> END_F
    R403 --> END_F
    R409L --> END_F
    R409K --> END_F
    R409P --> END_F
    R404 --> END_F
    R409V --> END_F
    R409C --> END_F
    R400U --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    class MUT,MUT2,COMPLETE,R200,END_S ok
    class R400V,R400H,R403,R409L,R409K,R409P,R404,R409V,R409C,R400U,END_F fail
    class BODY,HDR,ACC,LOCKOK,PRIOR,FOUND,VER,WHICH,SYNCOK,VALIDU gate
    class REPLAY warn
```

| Method | fullPath | ต่างจากไดอะแกรมตรงไหน |
| --- | --- | --- |
| PUT | `/api/v1/merchants/{merchantId:guid}` | diagram — body name/note/enabledChannels/metadata, มี SyncMerchantPoliciesAsync, operation `merchant.update`, 200 |
| POST | `/api/v1/merchants/{merchantId:guid}/suspend` | diagram — body มีแค่ MerchantId, activate=false, ข้าม SyncMerchantPoliciesAsync, operation `merchant.suspend`, 200 |
| POST | `/api/v1/merchants/{merchantId:guid}/reactivate` | diagram — body มีแค่ MerchantId, activate=true, ข้าม SyncMerchantPoliciesAsync, operation `merchant.reactivate`, 200 |

---

## 10.3 Merchant role และ permission read model

สาม endpoint อ่านของร้านค้าเดียวกัน ผ่าน gate ท้องถิ่น `RequireReadAccess` (404 นอก scope) + `RequireActiveMerchantAsync` (404 ไม่ Active) ก่อนเสมอ — เฉพาะ list role ใช้ `SfsQueryParser` เต็มรูป (§ 0.6), permission catalog และ role detail ไม่มี filter (source: `AdminMerchantIdentityEndpoints.cs:105-175,284-301`, `Iam.Application/Roles/RoleQueries.cs:11-51`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy admin + permission merchants.roles.view ดู § 0.1<br/>safe method ข้าม CSRF ดู § 0.3"]
    AUTHZ --> R1{"RequireReadAccess:<br/>merchantId อยู่ใน Admin scope?"}
    R1 -->|no| R404A["404 (ไม่ leak ว่ามี merchant จริงไหม)"]
    R1 -->|yes| A1{"RequireActiveMerchantAsync:<br/>merchant.Status = Active?"}
    A1 -->|no| R404B["404"]
    A1 -->|yes| KIND{"endpoint?"}

    KIND -->|"GET .../permissions"| CAT["GetPermissionCatalogQuery(Scope.Merchant)<br/>คืน groups + keys ของ Scope.Merchant รวม Scope.Shared"]
    CAT --> R200C["200 AdminMerchantPermissionCatalogResponse"]

    KIND -->|"GET .../roles"| SFS["SfsQueryParser.Parse(query, maxLimit 100) ดู § 0.6"]
    SFS --> SFSOK{"parse ผ่าน?"}
    SFSOK -->|no| R400["400 ดู § 0.6"]
    SFSOK -->|yes| LIST["ListRolesQuery(Merchant context)<br/>เติม UserCount ต่อ role"]
    LIST --> R200L["200 PagedResult AdminMerchantRoleResponse"]

    KIND -->|"GET .../roles/{code}"| GET["GetRoleQuery(Merchant context, code)"]
    GET --> FOUND{"พบและมองเห็นได้ใน context นี้?"}
    FOUND -->|no| R404C["404"]
    FOUND -->|yes| ETAGR["เติม UserCount + VersionEtags.Set(vN)"]
    ETAGR --> R200D["200 AdminMerchantRoleResponse + ETag"]

    R200C --> END_S((◉))
    R200L --> END_S
    R200D --> END_S
    R404A --> END_F((◉))
    R404B --> END_F
    R400 --> END_F
    R404C --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class CAT,LIST,ETAGR,R200C,R200L,R200D,END_S ok
    class R404A,R404B,R400,R404C,END_F fail
    class R1,A1,KIND,SFSOK,FOUND gate
```

| Method | fullPath | รายละเอียดเพิ่มเติม |
| --- | --- | --- |
| GET | `/api/v1/merchants/{merchantId:guid}/permissions` | ไม่มี paging, catalog นิ่ง (ไม่ต่อ DB ต่อ role) |
| GET | `/api/v1/merchants/{merchantId:guid}/roles` | `SfsQueryParamsMarker(100)` เต็มรูป ดู § 0.6, filters/sort/search จริง |
| GET | `/api/v1/merchants/{merchantId:guid}/roles/{code}` | GET เดี่ยว ไม่มี paging, มี ETag |

---

## 10.4 Merchant role CRUD

สาม endpoint แก้ role ของร้านค้าเดียวกัน ผ่าน gate ท้องถิ่น `RequireMutationAccess` (403) + `RequireActiveMerchantAsync` (404) — create เช็คโค้ดซ้ำก่อนสร้าง, update/delete โหลด role ด้วย `GetByCodeAsync` แล้วเช็ค ownership (role ที่มองเห็นได้แต่ merchant ไม่ได้เป็นเจ้าของ เช่น shared/seed role ที่ `MerchantId` เป็น null เสมอ) ก่อนเช็ค version (source: `AdminMerchantIdentityEndpoints.cs:177-244,284-301`, `Iam.Application/Roles/CreateRole.cs:17-51`, `UpdateRole.cs:16-77`, `DeleteRole.cs:13-60`, `Iam.Domain/Roles/Role.cs:57-149`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy admin + permission merchants.roles.manage ดู § 0.1"]
    AUTHZ --> M1{"RequireMutationAccess:<br/>merchantId อยู่ใน Admin scope?"}
    M1 -->|no| R403["403 code merchant_scope_forbidden"]
    M1 -->|yes| A1{"RequireActiveMerchantAsync:<br/>merchant Active?"}
    A1 -->|no| R404A["404"]
    A1 -->|yes| KIND{"endpoint?"}

    KIND -->|"POST (create)"| DUP{"CodeExistsAsync ใน context นี้<br/>(รวม NULL bucket ที่มองเห็นได้)?"}
    DUP -->|yes| R409D["409 (role ซ้ำ code)"]
    DUP -->|no| PERM1{"permission keys ทุกตัวอยู่ใน catalog<br/>และ side = Merchant หรือ Shared?"}
    PERM1 -->|no| R400P["400 (unknown key / permission นอก scope)"]
    PERM1 -->|yes| CREATE["Role.Create + audit RoleCreated"]
    CREATE --> R201["201 Created .../roles/{code} + ETag v1"]

    KIND -->|"PUT / DELETE (code)"| LOAD["GetByCodeAsync(context, code)"]
    LOAD --> FOUND{"พบและมองเห็นได้ใน context?"}
    FOUND -->|no| R404B["404"]
    FOUND -->|yes| OWN{"role.MerchantId = merchantId นี้?<br/>(shared/seed role MerchantId เป็น null เสมอ -> ไม่ own)"}
    OWN -->|no| R409O["409 (แก้/ลบ role ที่ merchant นี้ไม่ได้เป็นเจ้าของ)"]
    OWN -->|yes| VER{"role.Version = If-Match?"}
    VER -->|no| R409V["409 code state_conflict"]
    VER -->|yes| WHICH2{"PUT หรือ DELETE?"}

    WHICH2 -->|PUT| INVAL["InvalidateAssignedAccountsAsync:<br/>bump AuthorizationVersion ทุก account ที่ถือ role นี้ ดู § 0.2"]
    INVAL --> NAMEV{"name (Rename) ไม่ว่าง/ไม่ whitespace?"}
    NAMEV -->|no| R400N["400 (ArgumentException)"]
    NAMEV -->|yes| PERM2{"permission keys (SetPermissions) ผ่าน catalog check เดิม?"}
    PERM2 -->|no| R400P
    PERM2 -->|yes| UPD["description/color/status<br/>BumpVersion, audit RoleUpdated"]
    UPD --> R200U["200 + ETag ใหม่"]

    WHICH2 -->|DELETE| BOUND{"มี user ผูก role นี้อยู่<br/>(นับเฉพาะ assignment ของ merchant นี้เอง)?"}
    BOUND -->|yes| R409B["409 (role มีผู้ใช้ผูกอยู่)"]
    BOUND -->|no| DEL["Remove + audit RoleDeleted"]
    DEL --> R204["204 (ไม่มี ETag, marker EmitsEtag: false)"]

    R201 --> END_S((◉))
    R200U --> END_S
    R204 --> END_S
    R403 --> END_F((◉))
    R404A --> END_F
    R409D --> END_F
    R400P --> END_F
    R404B --> END_F
    R409O --> END_F
    R409V --> END_F
    R409B --> END_F
    R400N --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class CREATE,INVAL,UPD,DEL,R201,R200U,R204,END_S ok
    class R403,R404A,R409D,R400P,R404B,R409O,R409V,R409B,R400N,END_F fail
    class M1,A1,KIND,DUP,PERM1,FOUND,OWN,VER,WHICH2,PERM2,NAMEV,BOUND gate
```

| Method | fullPath | ต่างจากไดอะแกรมตรงไหน |
| --- | --- | --- |
| POST | `/api/v1/merchants/{merchantId:guid}/roles` | diagram — 409 ซ้ำ code, 400 permission key, ไม่มี If-Match, 201 |
| PUT | `/api/v1/merchants/{merchantId:guid}/roles/{code}` | diagram — code เปลี่ยนไม่ได้, InvalidateAssignedAccountsAsync ก่อนเขียน, 200 |
| DELETE | `/api/v1/merchants/{merchantId:guid}/roles/{code}` | diagram — ต้องไม่มี user ผูกอยู่, ไม่มี ETag response, 204 |

---

## 10.5 ผู้ใช้ร้านค้า: edit view (reveal audit) + update โดย Admin

GET edit ผ่าน gate read (404 นอก scope) แล้วเขียน audit `Reveal` ทุกครั้งที่เปิดดู (นอก transaction) ก่อนคืนฟิลด์ที่ไม่ mask, PUT ผ่าน gate mutation (403 นอก scope) แล้วต้องมี If-Match — ทั้งคู่ไม่เช็ค `user.MerchantId == merchantId` ตรง ๆ ใน handler แต่พึ่ง query filter ของ `actorScope.Begin(merchantId, adminId)`; PUT request record ไม่มี `[Required]` บน `firstName`/`lastName` จึงผ่าน model binding ด้วยค่าว่างได้ แล้วไปชน `400` ที่ `User.UpdateProfile` แทน (source: `AdminMerchantIdentityEndpoints.cs:28-52,78-102,284-301,326-327`, `Merchants.Application/Users/ManageMerchantUsers.cs:288-333`, `Merchants.Domain/Users/User.cs:132-135,246-250`)

```mermaid
flowchart TD
    START((●)) --> KIND{"endpoint?"}

    KIND -->|"GET .../edit"| AUTHZ1["policy admin + permission merchants.users.manage ดู § 0.1"]
    AUTHZ1 --> A1{"RequireReadAccess (404 นอก scope) +<br/>RequireActiveMerchantAsync (404 ไม่ Active)?"}
    A1 -->|no| R404A["404"]
    A1 -->|yes| BIND1["actorScope.Begin(merchantId, adminId)"]
    BIND1 --> Q["GetMerchantUserEditQuery: FindByIdAsync<br/>กรองด้วย query filter ของ actor scope"]
    Q --> FOUND1{"พบ user ใน merchant นี้?"}
    FOUND1 -->|no| R404B["404"]
    FOUND1 -->|yes| REVEAL["audit Reveal + SaveChanges (นอก transaction ของ mutation)"]
    REVEAL --> ETAG["VersionEtags.Set(vN)"]
    ETAG --> R200["200 MerchantUserEditView (ฟิลด์ไม่ mask)"]

    KIND -->|"PUT (update)"| AUTHZ2["policy admin + permission merchants.users.manage ดู § 0.1"]
    AUTHZ2 --> M2{"RequireMutationAccess:<br/>merchantId อยู่ใน Admin scope?"}
    M2 -->|no| R403["403 code merchant_scope_forbidden"]
    M2 -->|yes| ACT2{"RequireActiveMerchantAsync:<br/>merchant Active?"}
    ACT2 -->|no| R404C["404"]
    ACT2 -->|yes| BIND2["actorScope.Begin(merchantId, adminId)"]
    BIND2 --> LOAD["UpdateMerchantUserCommand: FindByIdAsync<br/>กรองด้วย query filter ของ actor scope"]
    LOAD --> FOUND2{"พบ user ใน merchant นี้?"}
    FOUND2 -->|no| R404D["404"]
    FOUND2 -->|yes| VER{"user.Version = If-Match?"}
    VER -->|no| R409["409 (InvalidOperationException, ไม่มี code)"]
    VER -->|yes| VALID{"firstName/lastName ไม่ว่าง/ไม่ whitespace?<br/>(ไม่มี [Required] กัน model binding)"}
    VALID -->|no| R400["400 (ArgumentException)"]
    VALID -->|yes| MUT["UpdateProfile(firstName, lastName, saleCode, licenseNumber, phone)<br/>audit Update"]
    MUT --> R204["204 + ETag vN ใหม่"]

    R200 --> END_S((◉))
    R204 --> END_S
    R404A --> END_F((◉))
    R404B --> END_F
    R403 --> END_F
    R404C --> END_F
    R404D --> END_F
    R409 --> END_F
    R400 --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    class ETAG,MUT,R200,R204,END_S ok
    class R404A,R404B,R403,R404C,R404D,R409,R400,END_F fail
    class KIND,A1,FOUND1,M2,ACT2,FOUND2,VER,VALID gate
    class REVEAL warn
```

| Method | fullPath | ต่างจากไดอะแกรมตรงไหน |
| --- | --- | --- |
| GET | `/api/v1/merchants/{merchantId:guid}/users/{merchantUserId:guid}/edit` | diagram — gate read (404), เขียน audit Reveal ทุกครั้งที่เปิด, มี ETag |
| PUT | `/api/v1/merchants/{merchantId:guid}/users/{merchantUserId:guid}` | diagram — gate mutation (403), ต้อง If-Match, 409 ไม่มี machine code, 400 ถ้า firstName/lastName ว่าง (ไม่มี `[Required]` กัน model binding), 204 |

---

## 10.6 เชิญผู้ใช้เข้าร้านค้าโดย Admin (idempotent, async email outbox)

Endpoint เดียวแต่ branch ซับซ้อน: TTL validate, resolve roleCodes เป็น role ที่ Active (Admin audience เท่านั้น), replay ผ่าน `IAdminUserOperationStore` (เฉพาะ hash ต่าง หรือ hash ตรงแล้ว replay — ไม่มี state "in progress" เพราะรันจบใน transaction เดียว), revoke invitation เดิมที่ยัง pending อีเมลเดียวกัน แล้ว enqueue อีเมลผ่าน outbox จริง (source: `Merchants.Application/Users/ManageMerchantUsers.cs:21-130`, `AdminMerchantIdentityEndpoints.cs:54-76,284-301`, `Persistence.MerchantUsers/Outbox/MerchantRegistrationOutboxWriter.cs:46-64`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy admin + permission merchants.users.manage ดู § 0.1"]
    AUTHZ --> M1{"RequireMutationAccess:<br/>merchantId อยู่ใน Admin scope?"}
    M1 -->|no| R403["403 code merchant_scope_forbidden"]
    M1 -->|yes| ACT{"RequireActiveMerchantAsync:<br/>merchant Active?"}
    ACT -->|no| R404A["404"]
    ACT -->|yes| BIND["actorScope.Begin(merchantId, adminId)"]
    BIND --> IDEM["IdempotencyKeys.Require(Idempotency-Key) รูปแบบ ดู § 0.5"]
    IDEM --> IDEMOK{"รูปแบบผ่าน?"}
    IDEMOK -->|no| R400I["400 code invalid_idempotency_key"]
    IDEMOK -->|yes| TTL{"TtlHours ใน 1..168?"}
    TTL -->|no| R400T["400 (ArgumentException)"]
    TTL -->|yes| ROLES{"roleCodes ที่ส่งมา (ถ้ามี)<br/>resolve เป็น active role ทั้งหมด?"}
    ROLES -->|no| R409R["409 (Role unknown or inactive)"]
    ROLES -->|yes| TXN["ผูก txn: replay lookup<br/>(merchantId, adminId, operation=merchant-user.invite, key)"]
    TXN --> PRIOR{"มี record เดิม?"}
    PRIOR -->|"hash ต่าง"| R409K["409 code idempotency_key_reused"]
    PRIOR -->|"hash ตรง"| REPLAY["คืน CreateInvitationResult เดิม (201)"]
    PRIOR -->|"ไม่มี"| OLD{"มี pending invitation เดิม<br/>อีเมลเดียวกัน (normalized)?"}
    OLD -->|yes| REVOKE["revoke ของเดิม + audit InviteRevoke"]
    OLD -->|no| CREATE
    REVOKE --> CREATE["สร้าง invitation ใหม่ (token hash, expiresAt)<br/>audit InviteCreate"]
    CREATE --> ENQ["Enqueue MerchantUserInvitationDeliveryRequested<br/>ลง merch.UserOutbox (protected token, ไม่ใช่ raw)"]
    ENQ --> REC["AdminUserOperationRecord.Succeeded + SaveChanges"]
    REC --> R201["201 Created Location .../merchants/users/invitations/{id}<br/>body maskedEmail, expiresAt, status pending"]

    R201 --> END_S((◉))
    REPLAY --> END_S
    R404A --> END_F((◉))
    R400I --> END_F
    R400T --> END_F
    R409R --> END_F
    R409K --> END_F

    REC -.async.-> DISP["MerchantUserOutboxDispatcher ดู § 0.8"]
    DISP --> MAIL["MerchantUserInvitationDeliveryHandler:<br/>unprotect token แล้ว IInvitationEmailSender.SendAsync"]
    MAIL --> EMAIL["ผู้สมัครได้รับอีเมลเชิญ"]

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef ext fill:#4a3a1f,stroke:#d29922,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    class REVOKE,CREATE,ENQ,REC,R201,END_S ok
    class R403,R404A,R400I,R400T,R409R,R409K,END_F fail
    class M1,ACT,IDEMOK,TTL,ROLES,PRIOR,OLD gate
    class MAIL,EMAIL ext
    class REPLAY warn
```

---

## 10.7 กำหนดบทบาทให้ผู้ใช้ร้านค้าโดย Admin

ใช้ `SetRolesCommand` ตัวเดียวกับ endpoint self-service ฝั่ง merchant-user (`PUT /api/v1/merchants/users/{merchantUserId:guid}/roles`) — ฝั่ง Admin ส่ง `ActingMerchantUserId = scope.Current.AdminId` (Guid ของ admin เอง ไม่ใช่ merchant user จริง) เพื่อ stamp เป็นผู้ assign เท่านั้น target ต้อง Active และอยู่ merchant เดียวกับ route ไม่งั้น 404 (source: `AdminMerchantIdentityEndpoints.cs:246-269,284-301`, `Merchants.Application/Users/SetUserRoles.cs:17-103`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy admin + permission merchants.roles.manage ดู § 0.1"]
    AUTHZ --> M1{"RequireMutationAccess:<br/>merchantId อยู่ใน Admin scope?"}
    M1 -->|no| R403["403 code merchant_scope_forbidden"]
    M1 -->|yes| ACT{"RequireActiveMerchantAsync:<br/>merchant Active?"}
    ACT -->|no| R404A["404"]
    ACT -->|yes| BIND["actorScope.Begin(merchantId, adminId)"]
    BIND --> TARGET["SetRolesCommand: FindByIdAsync(targetUserId)"]
    TARGET --> T1{"target Active และ<br/>target.MerchantId = merchantId (route)?"}
    T1 -->|no| R404B["404 (merchant user ไม่พบในร้านค้านี้)"]
    T1 -->|yes| VER{"target.Version = If-Match?"}
    VER -->|no| R409V["409 (InvalidOperationException, ไม่มี code)"]
    VER -->|yes| RES{"roleCodes ทุกตัว resolve<br/>เป็น role id ใน merchant นี้?"}
    RES -->|no| R400["400 (ArgumentException Unknown role codes)"]
    RES -->|yes| MGR{"ถอด merchant_manager ออก และ<br/>เหลือผู้ใช้ Active ที่ถือ role นี้ <= 1?"}
    MGR -->|yes| R409M["409 (last active merchant manager cannot be downgraded)"]
    MGR -->|no| DIFF["add assignment ที่ขาด + remove ที่เกิน<br/>BumpVersion, audit SetRoles"]
    DIFF --> R204["204 + ETag vN ใหม่"]

    R204 --> END_S((◉))
    R403 --> END_F((◉))
    R404A --> END_F
    R404B --> END_F
    R409V --> END_F
    R400 --> END_F
    R409M --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class DIFF,R204,END_S ok
    class R403,R404A,R404B,R409V,R400,R409M,END_F fail
    class M1,ACT,T1,VER,RES,MGR gate
```

---

## 10.8 สร้าง Originator

สร้าง Originator ใน merchant ที่ Admin เข้าถึงได้ — เช็คแค่ว่า merchant "มีอยู่จริง" (ไม่เช็ค Active เหมือน § 10.3-10.7) แล้วไม่มี pre-check โค้ดซ้ำที่ handler เลย รหัส (MerchantId, Code) ซ้ำจะชนที่ DB ตอน SaveChanges แล้วถูกแปลเป็น 409 โดย `ControlPlaneUnitOfWork` เป็น backstop — ตาราง Originators ไม่มี FK บน `ApiClientId` เลย (มีแค่ unique index (MerchantId, Code) และ FK ไปยัง Merchants) จึง apiClientId ที่ไม่มีอยู่จริงจะถูกบันทึกเงียบ ๆ โดยไม่มีการตรวจสอบใด ๆ (source: `AdminControlEndpoints.cs:397-467`, `AdminMerchantControlStore.cs:387-397,541-546,637-641`, `Originator.cs:36-61,85-110`, `Admins/ControlPlaneUnitOfWork.cs:34-54`, `OriginatorConfiguration.cs:20-22`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy admin + permission merchant.manage ดู § 0.1"]
    AUTHZ --> ACC{"EnsureAccess: body.MerchantId<br/>อยู่ใน Admin scope?"}
    ACC -->|no| R403["403 code merchant_scope_forbidden"]
    ACC -->|yes| EXIST{"EnsureMerchantExistsAsync:<br/>merchant มีอยู่จริง? (ไม่เช็ค Active)"}
    EXIST -->|no| R404["404"]
    EXIST -->|yes| TYPE{"ParseType(type): type ∈<br/>branch/agent/broker/staff/app?"}
    TYPE -->|no| R400TY["400 code invalid_type<br/>(InvalidRequestException)"]
    TYPE -->|yes| VALID{"code/name ไม่ว่างและไม่เกินความยาว,<br/>code เป็น a-z0-9-_. เท่านั้น, linkedApiClientId ไม่ Guid.Empty ถ้าส่งมา?"}
    VALID -->|no| R400["400 (ArgumentException, ไม่มี code)"]
    VALID -->|yes| CREATE["Originator.Create + Add"]
    CREATE --> SAVE["SaveChangesAsync"]
    SAVE --> DUP{"unique (MerchantId, Code) ชนที่ DB<br/>(SQL 2627/2601)?"}
    DUP -->|yes| R409["409 (ไม่มี pre-check ที่ handler,<br/>backstop ที่ ControlPlaneUnitOfWork)"]
    DUP -->|no| R201["201 Created Location /api/v1/originators/{id}<br/>+ ETag v1"]

    R201 --> END_S((◉))
    R403 --> END_F((◉))
    R404 --> END_F
    R400 --> END_F
    R400TY --> END_F
    R409 --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class CREATE,SAVE,R201,END_S ok
    class R403,R404,R400,R400TY,R409,END_F fail
    class ACC,EXIST,TYPE,VALID,DUP gate
```

---

## 10.9 แก้ไข / เปิดใช้งาน / ปิดใช้งาน Originator (If-Match เท่านั้น)

สามตัวนี้ใช้ `LoadOriginatorAsync(originatorId, body.MerchantId)` เดียวกัน (Id และ MerchantId ต้องตรงทั้งคู่ ไม่งั้น 404) แล้วเช็ค If-Match — **ไม่มี** Idempotency-Key และไม่มี operation executor เหมือน § 10.2 (ไม่มี replay), enable/disable เป็น no-op ถ้า status ตรงอยู่แล้ว และไม่มี audit log ใด ๆ ต่อการแก้ (source: `AdminControlEndpoints.cs:397-495,520-545`, `AdminMerchantControlStore.cs:399-419,548-551`, `Originator.cs:63-83`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy admin + permission merchant.manage ดู § 0.1"]
    AUTHZ --> ACC{"EnsureAccess: body.MerchantId<br/>อยู่ใน Admin scope?"}
    ACC -->|no| R403["403 code merchant_scope_forbidden"]
    ACC -->|yes| LOAD["LoadOriginatorAsync(originatorId, body.MerchantId)"]
    LOAD --> FOUND{"พบแถวที่ Id และ MerchantId ตรงกันทั้งคู่?"}
    FOUND -->|no| R404["404"]
    FOUND -->|yes| VER{"entity.Version = If-Match?"}
    VER -->|no| R409V["409 code state_conflict"]
    VER -->|yes| KIND{"endpoint?"}
    KIND -->|"PUT (update)"| TYPE2{"ParseType(type): type ∈<br/>branch/agent/broker/staff/app?"}
    TYPE2 -->|no| R400TY["400 code invalid_type<br/>(InvalidRequestException)"]
    TYPE2 -->|yes| VALID{"name/saleCode/linkedApiClientId ผ่าน validate?<br/>(code ไม่เปลี่ยน)"}
    VALID -->|no| R400["400 (ArgumentException, ไม่มี code)"]
    VALID -->|yes| MUT["entity.Update(name, type, saleCode, linkedApiClientId)"]
    KIND -->|"POST enable/disable"| MUT2["entity.Enable() / Disable()<br/>no-op ถ้า status ตรงอยู่แล้ว (ไม่ bump version)"]
    MUT --> SAVE
    MUT2 --> SAVE["SaveChangesAsync (ไม่มี audit)"]
    SAVE --> R200["200 OriginatorView + ETag vN ใหม่"]

    R200 --> END_S((◉))
    R403 --> END_F((◉))
    R404 --> END_F
    R409V --> END_F
    R400 --> END_F
    R400TY --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    class MUT,MUT2,SAVE,R200,END_S ok
    class R403,R404,R409V,R400,R400TY,END_F fail
    class ACC,FOUND,VER,KIND,TYPE2,VALID gate
```

| Method | fullPath | ต่างจากไดอะแกรมตรงไหน |
| --- | --- | --- |
| PUT | `/api/v1/originators/{originatorId:guid}` | diagram — แก้ name/saleCode/linkedApiClientId, 400 code invalid_type ถ้า type ผิด, 400 (ArgumentException) ถ้า field อื่น validate ไม่ผ่าน |
| POST | `/api/v1/originators/{originatorId:guid}/enable` | diagram — SetStatus(Active), ไม่มี body validate เพิ่ม |
| POST | `/api/v1/originators/{originatorId:guid}/disable` | diagram — SetStatus(Inactive), ไม่มี body validate เพิ่ม |

---

## 10.10 ลบ Originator (soft-disable เมื่อยังถูก routing rule อ้างอิง)

`merchantId` เป็น query parameter (non-nullable — ไม่ส่งมาโดน model binding ปฏิเสธก่อนถึง handler) ใช้แทน body ของสาม § ก่อนหน้า ลบจริงเฉพาะเมื่อไม่มี `RoutingRule` อ้างอิง originator นี้อยู่ ไม่งั้น soft-disable แทนแล้วยังตอบ 204 เหมือนเดิม (source: `AdminControlEndpoints.cs:397-399,497-517`, `AdminMerchantControlStore.cs:421-435`)

```mermaid
flowchart TD
    START((●)) --> AUTHZ["policy admin + permission merchant.manage ดู § 0.1"]
    AUTHZ --> BINDQ{"query merchantId มาด้วย?<br/>(non-nullable, model binding)"}
    BINDQ -->|no| R400B["400 (BadHttpRequestException, model binding)"]
    BINDQ -->|yes| ACC{"EnsureAccess: merchantId<br/>อยู่ใน Admin scope?"}
    ACC -->|no| R403["403 code merchant_scope_forbidden"]
    ACC -->|yes| LOAD["LoadOriginatorAsync(originatorId, merchantId)"]
    LOAD --> FOUND{"พบแถว?"}
    FOUND -->|no| R404["404"]
    FOUND -->|yes| VER{"entity.Version = If-Match?"}
    VER -->|no| R409V["409 code state_conflict"]
    VER -->|yes| REF{"มี RoutingRule อ้างอิง<br/>(merchantId, originatorId) อยู่?"}
    REF -->|yes| SOFT["entity.Disable(now) (soft, ไม่ลบแถว)"]
    REF -->|no| HARD["Remove(entity) (hard delete)"]
    SOFT --> SAVE
    HARD --> SAVE["SaveChangesAsync (ไม่มี audit)"]
    SAVE --> R204["204 No Content (ไม่มี ETag, marker EmitsEtag: false)"]

    R204 --> END_S((◉))
    R400B --> END_F((◉))
    R403 --> END_F
    R404 --> END_F
    R409V --> END_F

    classDef ok fill:#1f6f3a,stroke:#3fb950,color:#fff
    classDef fail fill:#6b1f1f,stroke:#f85149,color:#fff
    classDef gate fill:#1f3f6b,stroke:#58a6ff,color:#fff
    classDef warn fill:#5a3d0a,stroke:#d29922,color:#fff
    class HARD,SAVE,R204,END_S ok
    class R400B,R403,R404,R409V,END_F fail
    class BINDQ,ACC,FOUND,VER,REF gate
    class SOFT warn
```

---

## Deviations

ไม่พบ deviation ระหว่างตารางเอกสาร (`docs/reference/api-endpoints.md` L233-L253) กับ source — permission key, policy และ CSRF ตรงกันทุกแถว

## Notes

- theme file `theme-T10.md` อ้าง doc line L231-L251 (จุดเริ่มค้น) แต่บรรทัดจริงในไฟล์ปัจจุบันคือ L233-L253 (เอกสารถูกแก้เลื่อน 2 บรรทัดหลังสร้าง theme file) — 21 แถวยังต่อเนื่องและครบ
- seed-anchor guard ใน `UpdateRoleHandler`/`DeleteRoleHandler` (`Role.IsSeedAnchor` ต้อง `MerchantId is null`) เป็น dead code ในบริบท Merchant ของ theme นี้ เพราะ ownership guard (`role.MerchantId != merchantId`) ดักไว้ก่อนเสมอ — role ที่ merchant เป็นเจ้าของมี `MerchantId` ไม่ใช่ null (source: `UpdateRole.cs:48-55`, `DeleteRole.cs:43-48`)
- Originator mutations (สร้าง/แก้/enable/disable/ลบ) ไม่มี `IManagementAuditWriter`/`IRoleAuditSink` เขียน audit log ใด ๆ ต่างจาก Role CRUD (§ 10.4) และ merchant-user mutations (§ 10.5-10.7) ที่เขียน audit ทุกครั้ง (source: `AdminMerchantControlStore.cs:387-435`)
- ตาราง `Originators` ไม่มี foreign key บนคอลัมน์ `ApiClientId` (มีแค่ `HasIndex(MerchantId, Code).IsUnique()` และ FK ไปยัง `Merchants`) — apiClientId ที่ไม่มีอยู่จริง (แต่ไม่ใช่ `Guid.Empty`) ผ่าน validate ที่ handler แล้วถูกบันทึกเงียบ ๆ ไม่มี SQL error ใด ๆ (§ 10.8, source: `OriginatorConfiguration.cs:20-22`, `PolDbContextModelSnapshot.cs:2433-2481`, migration `20260810112718_AdminTenantPspRoutingControlPlane.cs:125`)
- `DeleteOriginator` endpoint's `WithDescription` (`AdminControlEndpoints.cs:512`) เขียนว่า "รายการที่ยังถูกอ้างอิงลบไม่ได้ -> 409" แต่ source จริงทำ soft-disable แล้วตอบ 204 เสมอ (409 มีทางเดียวคือ version stale) — ตาราง inventory ไม่ได้อ้างพฤติกรรมนี้จึงไม่นับเป็น Deviation แต่บันทึกไว้เพื่อความถูกต้อง
- `SetRolesCommand` (§ 10.7) ใช้ร่วมกับ endpoint self-service ของ merchant-user เอง (`PUT /api/v1/merchants/users/{merchantUserId:guid}/roles`, อยู่นอก theme นี้) — ฝั่ง Admin stamp `ActingMerchantUserId` ด้วย Guid ของ admin ไม่ใช่ merchant user จริง เป็นแค่ label ผู้ assign ใน audit
- 409 shape ในธีมนี้มี 3 แบบต่างกัน: `ConcurrencyConflictException` (merchant/role version) เป็น code `state_conflict` เสมอ, plain `InvalidOperationException` (merchant-user version, § 10.5/10.7) ไม่มี code field, ส่วน `ConflictException` (role ownership/bound/originator DB constraint) มี message คงที่และมี code เฉพาะบางจุด — เทียบ node ในแต่ละ § ก่อนอ้างอิง code

**Render**: GitHub / Obsidian / VS Code Mermaid
