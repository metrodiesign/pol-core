# Connection Strings + RLS — คู่มือ reference (pol-core)

คู่มือสรุปสถานะ **ปัจจุบัน** ของการเข้าถึง database ใน pol-core: connection string, SQL principal,
Row-Level Security (RLS) และ flow การทำงานจริงแต่ละเส้นทาง. เป็น current-state reference (ไม่ใช่ prescriptive) —
อ้าง file:line ตามโค้ดจริง เพื่อให้ตามอ่านต่อได้.

> Stack: C# 14 / .NET 10 / EF Core 10 / SQL Server 2025 / martinothamar Mediator. การกรอง row ต่อ tenant
> ทำที่ **SQL Server native RLS** เป็นพื้น (floor) ไม่ใช่ EF query filter — โปรเจกต์ **ไม่มี** `HasQueryFilter`
> เลย (`grep HasQueryFilter` = ว่าง). App-layer guard เป็นชั้นสะดวกบน RLS ไม่ใช่ตัวแทน.

> ข้อควรรู้: "account" ในระบบมี 2 ความหมายที่ห้ามสับสน — (1) **SQL principal / login** (`pol_app`,
> `pol_admin`, ...) คือตัวที่ runtime ใช้ connect DB = "database access account" จริง; (2) **application
> identity row** (`ProducerAccount`, `AdminAccount`) คือแถวในตาราง identity keyed ด้วย Google `sub` = "คนที่
> operate" ไม่ใช่ DB login. คู่มือนี้ว่าด้วยความหมาย (1).

- RLS floor + tenant isolation (แนวคิดระดับสถาปัตย์): `../../.ai/shared/ARCHITECTURE.md`, `../../.ai/shared/SECURITY_RULES.md`
- module map: `docs/reference/platform-modules.md`
- entity fields: `docs/reference/entity-fields.md`

---

## สารบัญ

0. [อธิบายแบบเข้าใจง่าย (ตึกให้เช่า)](#อธิบายแบบเข้าใจง่าย-ตึกให้เช่า) — อ่านก่อนถ้าไม่ใช่สาย technical
1. [Mental model — defense in depth](#1-mental-model--defense-in-depth)
2. [Connection strings -> principal](#2-connection-strings---principal)
3. [SQL principals ทั้งหมด](#3-sql-principals-ทั้งหมด)
4. [DbContext wiring](#4-dbcontext-wiring)
5. [RLS layer (isolation floor)](#5-rls-layer-isolation-floor)
6. [Session-context stamping](#6-session-context-stamping)
7. [App-layer guards](#7-app-layer-guards)
8. [EXECUTE AS procs](#8-execute-as-procs)
9. [Per-principal GRANT (least privilege)](#9-per-principal-grant-least-privilege)
10. [Flow การทำงาน (A-E)](#10-flow-การทำงาน-a-e)
11. [File map](#11-file-map)

---

## อธิบายแบบเข้าใจง่าย (ตึกให้เช่า)

ส่วนนี้อธิบายแนวคิดด้วยการเปรียบเทียบ สำหรับคนที่ไม่ใช่สาย technical. เดินเรื่องด้วยภาพเดียว: **ตึกออฟฟิศให้เช่า
ที่มีหลายบริษัทมาเช่าห้อง**.

> "บริษัท A / B / C" ในตัวอย่าง = **tenant จริงของแพลตฟอร์ม 3 เจ้า: vCentral / vCommerce / vSouvenir** (บริษัทในเครือ,
> allowlist). ทั้ง 3 ใช้ Tenant Console + backend + database **ชุดเดียวกัน** แต่ข้อมูลแยกเด็ดขาดด้วย RLS — ดู
> [ตัวอย่างสถานการณ์จริง](#ตัวอย่างสถานการณ์จริง-vcentral--vcommerce--vsouvenir) ท้ายหัวข้อนี้.

| ในเรื่องเปรียบเทียบ | ของจริงในระบบ | คืออะไร |
|---|---|---|
| ตัวตึก | Database | ที่เก็บข้อมูลของทุกคนรวมกัน |
| บริษัทที่เช่าห้อง (A, B, C) | Tenant | ลูกค้าแต่ละเจ้าที่ใช้ระบบเรา |
| ของในห้องบริษัท A | ข้อมูล (row) ของ tenant A | order, product, การจ่ายเงิน ของ A |
| คีย์การ์ดเข้าตึก | Connection string / DB account | บัตรที่ "โปรแกรม" ใช้เข้าไปในฐานข้อมูล |
| ล็อกอัจฉริยะหน้าห้อง | RLS (Row-Level Security) | ระบบที่กันไม่ให้ A เห็นของ B โดยอัตโนมัติ |
| ป้ายชื่อที่แตะตอนเข้า | `SESSION_CONTEXT('TenantId')` | บอกล็อกว่า "ฉันคือบริษัท A" |
| คู่มือพนักงาน (ใครทำอะไรได้) | RBAC | กฎว่า role ไหนกดปุ่มอะไรได้ |

### Connection string / "DB account" คืออะไร

Database เหมือน **ตึกที่เก็บของทุกบริษัทไว้รวมกัน**. โปรแกรมจะเข้าไปหยิบ/วางข้อมูลได้ ต้องมี **คีย์การ์ด** ก่อน —
คีย์การ์ดนั้นคือ **connection string** ซึ่งบอกว่า ตึกไหน (server), ใช้บัญชีอะไร (`User Id`), รหัสอะไร (password).
ระบบนี้มี **คีย์การ์ด 3 ใบ** (3 account): `pol_app`, `pol_admin`, `pol_worker` — สิทธิ์ต่างกัน.

### ทำไมมีหลายคีย์การ์ด

เพราะงาน 3 แบบต้องการสิทธิ์ต่างกัน — และการแยกใบคือ **กำแพงความปลอดภัยที่จงใจ** ไม่ใช่ความรก:

- **`pol_app`** = คีย์ของ "พนักงานหน้าร้าน" ที่คุยกับลูกค้า. เข้าตึกได้ แต่ล็อกอัจฉริยะเปิดให้เฉพาะห้องของบริษัทที่กำลังให้บริการอยู่
- **`pol_admin`** = **คีย์มาสเตอร์ของผู้จัดการตึก**. เข้าได้ทุกห้อง. ใช้เฉพาะตอน "เปิดห้องใหม่ให้บริษัทที่เพิ่งมาเช่า" (สร้าง tenant ใหม่)
- **`pol_worker`** = คีย์ของ "แม่บ้าน/ภารโรง". เดินเก็บจดหมาย (งานเบื้องหลัง) จากทุกห้อง แต่จะทำงานในห้องไหน ต้องแตะป้ายบอกเข้าห้องนั้นก่อน

### RLS คือหัวใจ — "ล็อกอัจฉริยะหน้าห้อง"

**RLS** คือระบบที่ **ฐานข้อมูลเองเป็นคนกัน** ว่าใครเห็น row ของใคร (ไม่ใช่โปรแกรมกัน — ตัว database กันเอง).

ต่อให้พนักงานถือคีย์ `pol_app` เดินไปหน้าห้องบริษัท B แล้วสั่ง "ขอดูของทั้งหมด" — ล็อกจะโชว์ให้แค่ห้องของบริษัทที่แตะป้ายไว้
(เช่น A) เท่านั้น ของ B ไม่โผล่เลย แม้โปรแกรมจะเขียนพลาดขอไปทั้งหมด. ทำงาน 2 ขั้น:

1. ตอนเข้า แตะป้าย `SESSION_CONTEXT('TenantId') = A` (บอกว่า "ฉันทำงานให้บริษัท A")
2. ทุกครั้งที่ขอข้อมูล ล็อกเช็ค: row นี้เป็นของ A ไหม? ใช่ = เห็น, ไม่ใช่ = ซ่อน

นี่คือเหตุผลที่ระบบ **ไม่ต้องเขียนโค้ดกรอง "เอาเฉพาะของ A" ในทุกจุด** — ฐานข้อมูลกันให้ที่พื้น. มีแค่คีย์มาสเตอร์ `pol_admin`
ที่ข้ามล็อกนี้ได้ (เพราะผู้จัดการต้องเปิดห้องใหม่).

### RBAC ต่างจาก RLS ยังไง (จุดที่มักสับสน)

สองอันนี้ **คนละแกน** ต้องแยก:

| | ตอบว่า | ตัวอย่าง |
|---|---|---|
| **RBAC** | "ใคร/ตำแหน่งไหน **กดปุ่มอะไร** ได้" | ผู้จัดการ **สร้าง** ห้องใหม่ได้, พนักงานทั่วไปสร้างไม่ได้ |
| **RLS** | "คนนั้นเห็น **ของห้องไหน**" | พนักงานที่ดูแลบริษัท A เห็นแค่ของ A |

- **RBAC = คู่มือพนักงาน** บอก "action" ที่แต่ละตำแหน่งทำได้ (สร้าง/ลบ/แก้)
- **RLS = ล็อกหน้าห้อง** บอก "ห้อง/ข้อมูล" ที่มองเห็น

**RBAC แทน RLS ไม่ได้**: คู่มือพนักงานไม่ได้ล็อกประตูห้อง. ต่อให้คู่มือเขียนว่า "พนักงานคนนี้ดูออเดอร์ได้" มันไม่ได้บอกว่า
**ออเดอร์ของบริษัทไหน** — ตัวที่บอกว่าเห็นของบริษัทไหนคือล็อก RLS เท่านั้น.

### ตัวอย่างสถานการณ์จริง (vCentral / vCommerce / vSouvenir)

3 บริษัทในเครือ = 3 tenant จริง (allowlist; `code` normalize เป็น lowercase: `vcentral`, `vcommerce`, `vsouvenir`).
อยู่ในตึกเดียวกัน (database + backend ชุดเดียว) แต่คนละห้อง. 4 สถานการณ์ผูกกับ flow ในหัวข้อ 10:

**S1 — ตัวแทนของ vCommerce เปิดดูออเดอร์ตัวเอง** (= Flow A)
- ตัวแทนล็อกอิน Google SSO -> token มี claim tenant = `vcommerce`
- `pol_app` แตะป้าย `SESSION_CONTEXT('TenantId') = <vcommerce id>`
- `GET /api/v1/orders` -> RLS โชว์เฉพาะออเดอร์ของ vcommerce; ของ `vcentral`/`vsouvenir` **ไม่โผล่** แม้อยู่ในตาราง `Orders` เดียวกัน
- ต่อให้ query เขียนพลาดขอทั้งตาราง ก็ยังเห็นแค่ vcommerce — ล็อกกันที่ DB ไม่ใช่ที่โปรแกรม

**S2 — ทีมกลางเปิด tenant ใหม่ให้ vSouvenir** (= Flow B)
- Admin Console (session cookie) -> RBAC เช็คสิทธิ์ provision (operation authz)
- ใช้คีย์มาสเตอร์ `pol_admin` (bypass) -> `POST /api/v1/admins/tenants` payload `code = vsouvenir`, PSP credential
- เก็บ PSP secret ลง vault (encrypt, key แยกต่อ tenant); สร้าง "ห้อง" ของ vsouvenir ในตึกเดียวกัน

**S3 — ลูกค้าของ vCommerce จ่ายเงิน แล้ว PSP (2C2P/Omise) ยิง webhook กลับ** (= Flow D)
- callback มาแค่ connection id (ยังไม่รู้ว่า tenant ไหน)
- `usp_resolve_webhook_tenant` (EXECUTE AS bypass) map connection id -> `vcommerce`
- bind `SESSION_CONTEXT = vcommerce` -> ยืนยัน/อัปเดตออเดอร์ของ vcommerce เท่านั้น

**S4 — งานเบื้องหลังส่งลิงก์สรุปออเดอร์ของ vCentral** (= Flow C)
- worker ดึง message จาก outbox (เห็นทุก tenant — ตารางนี้ไม่มีล็อกกรอง)
- ต่อ message: bind `SESSION_CONTEXT = vcentral` -> อ่าน/เขียนออเดอร์ของ vcentral แบบ scoped

**บทสรุปที่เห็นจาก 4 สถานการณ์**: การแยก tenant (vcommerce เห็นแค่ vcommerce) เกิดจาก **RLS ที่ DB floor** —
ไม่ใช่ RBAC. RBAC ตัดสินแค่ "ใครกดปุ่ม provision/ดูออเดอร์ได้"; ตัวที่กันไม่ให้ vcommerce เห็นออเดอร์ vsouvenir คือ RLS.

---

## 1. Mental model — defense in depth

การเข้าถึงข้อมูลถูกกั้นเป็นชั้น โดยมี **SQL RLS เป็นพื้นแข็ง (hard floor)** และ app-layer guard อยู่บน:

```
                request (HTTP)
                     |
   [app-layer]  TenantGuardBehavior + ITenantContext + RBAC/RequirePermission
                     |   ตั้ง SESSION_CONTEXT('TenantId') ผ่าน interceptor
                     v
   [SQL floor]  RLS security policy (FILTER/BLOCK predicate) + per-principal GRANT
                     |
                     v
                SQL Server 2025
```

- **RBAC != RLS**: RBAC = "ใคร/role ไหน ทำ operation อะไรได้" (app layer). RLS = "เห็น row ของ tenant ไหน"
  (DB floor). สองอันคนละแกน — RBAC แทน RLS ไม่ได้.
- ชั้น isolation แต่ละชั้นอิสระกัน: RLS bypass ข้าม *predicate* แต่ไม่ข้าม *GRANT*; GRANT ผ่านแต่ก็ยังโดน
  RLS predicate กรอง.

---

## 2. Connection strings -> principal

| Config key | login (`User Id=`) | RLS posture | ใช้โดย | นิยามที่ |
|---|---|---|---|---|
| `ConnectionStrings:Producer` | `pol_app` | **RLS-enforced** | API default `ProducerDbContext` (tenant-facing) | `src/Hosts/Api/appsettings.json:11` |
| `ConnectionStrings:Admin` | `pol_admin` | **RLS-bypass** | API keyed `"admin"` context (provisioning/control-plane) | `src/Hosts/Api/appsettings.json:12` |
| `ConnectionStrings:Worker` | `pol_worker` | RLS-enforced | Worker `ProducerDbContext` (outbox) | `src/Hosts/Worker/appsettings.json:11` |
| `ConnectionStrings:Migrator` | *(privileged, ไม่ commit)* | — | dev boot auto-migrate | `src/Hosts/Api/Program.cs:347` |
| `POL_DESIGN_SQL` (env) | `sa` | — | `dotnet ef database update` (design-time DDL) | `.env:18`, `docker/migrate-entrypoint.sh` |

- Password ใน committed config = **ว่าง**; ฉีดตอน runtime ผ่าน env `ConnectionStrings__Producer/__Admin/__Worker`
  (ASP.NET map `__` -> `:`). ทุกเส้นมี `Database=PaymentOrchestration;Encrypt=True`.
- นอก Development ถ้า password ว่าง -> fail-fast (`ProvisioningGuards.RequireInjectedCredential`,
  `src/Hosts/Api/Program.cs:1763-1769`).
- Prod: `docker/entrypoint.sh` สร้าง connection string ตอน container start จาก `DB_PRINCIPAL` + password
  file secret (`entrypoint.sh:18` = Producer/Worker; `:29` = Admin เมื่อ mount admin password). `docker-compose.prod.yml`:
  `DB_PRINCIPAL: pol_app` (api) / `pol_worker` (worker), `DB_ADMIN_PRINCIPAL: pol_admin`.

---

## 3. SQL principals ทั้งหมด

นิยามที่ `docker/bootstrap/01-principals.sql` (รันเป็น `sa`, ก่อน EF migration, idempotent).

**Server logins (connect ได้):**

| login | posture | หน้าที่ |
|---|---|---|
| `pol_app` | **RLS-enforced** (ไม่อยู่ใน bypass role) | API tenant-facing; CRUD เฉพาะ tenant ตัวเอง |
| `pol_admin` | **RLS-bypass** (สมาชิก `pol_rls_bypass`) | provisioning/control-plane ข้าม tenant; ใช้เฉพาะ endpoint admin |
| `pol_worker` | RLS-enforced | Worker outbox dispatcher; อ่าน OutboxMessages ข้าม tenant ได้ (ตารางไม่มี FILTER) แต่เขียน Orders scoped |
| `sa` | — | bootstrap + DDL migration เท่านั้น; runtime login ไม่มีสิทธิ DDL; app ไม่เคยใช้ |

**Login-less users** (`CREATE USER ... WITHOUT LOGIN` — login ไม่ได้, เป็นแค่ `EXECUTE AS` proc identity; ทั้งคู่เป็นสมาชิก `pol_rls_bypass`):

- `pol_webhook_resolver` — target ของ `usp_resolve_webhook_tenant` / `usp_resolve_order_summary`
- `pol_vault_auditor` — target ของ `usp_vault_audit_head`

**Database role:**

- `pol_rls_bypass` — **ทางข้าม RLS ทางเดียว**. สมาชิก: `pol_admin`, `pol_webhook_resolver`, `pol_vault_auditor`.
  `pol_app` / `pol_worker` จงใจ **ไม่** เป็นสมาชิก. (พิสูจน์บน SQL Server 2025: ownership chaining และ
  `EXECUTE AS OWNER` ไม่ข้าม RLS — role membership เท่านั้นที่ข้าม.)

---

## 4. DbContext wiring

มี `ProducerDbContext` type เดียว register หลายแบบ แต่ละแบบต่อคนละ login:

| Registration | login | interceptor? | ที่ |
|---|---|---|---|
| API default | `pol_app` | **มี** `SessionContextConnectionInterceptor` | `src/Hosts/Api/Program.cs:81-84` |
| API keyed `"admin"` | `pol_admin` | **ไม่มี** (bypass เห็นทุก tenant) | `src/Hosts/Api/AdminScopedServices.cs:71-94` |
| Worker | `pol_worker` | **มี** | `src/Hosts/Worker/Program.cs:37-40` |
| Design-time | `sa`/fallback | — | `src/Hosts/Api/DesignTimeDbContextFactories.cs` |

Handler ที่ต้องการ cross-tenant/control-plane inject keyed ผ่าน `[FromKeyedServices("admin")]` — Admin RBAC
handlers, tenant provisioning, producer identity wiring, admin OIDC data-protection key ring.

---

## 5. RLS layer (isolation floor)

นิยามที่ `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/20260621133209_AddRlsSecurityPolicy.cs`.

**Predicate function** (`fn_tenant_predicate`, :27-33):

```sql
CREATE FUNCTION producer.fn_tenant_predicate(@TenantId uniqueidentifier)
RETURNS TABLE WITH SCHEMABINDING AS
RETURN SELECT 1 AS allowed
WHERE @TenantId = CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier)   -- row ตรง tenant ที่ผูกไว้
   OR IS_ROLEMEMBER(N'pol_rls_bypass') = 1;                                 -- หรือ principal อยู่ใน bypass role
```

- แถวมองเห็น/เขียนได้ก็ต่อเมื่อ `TenantId` = `SESSION_CONTEXT('TenantId')` ของ connection **หรือ** principal
  เป็นสมาชิก bypass.
- `CartItems` ไม่มี `TenantId` -> `fn_cartitem_predicate` (:36-44) scope ผ่าน parent `Carts.TenantId`.

**Security policy `producer.TenantIsolationPolicy`** (:58-75) — coverage ต่อกลุ่มตาราง:

| กลุ่ม | predicate | ตาราง |
|---|---|---|
| tenant data | **FILTER + BLOCK** (insert/update) | `PaymentSessions, PspConnections, Products, CheckoutSessions, Carts, Orders, VaultSecrets, IdempotencyRecords` + `CartItems` (ผ่าน parent) |
| outbox | **BLOCK-on-insert** เท่านั้น (อ่านข้าม tenant ได้; ปลอม tenant id ตอนเขียนไม่ได้) | `OutboxMessages` (dispatcher ต้อง drain ทุก tenant) |
| audit | **BLOCK-on-insert** (append-only) | `VaultRevealAudits` (เพิ่มโดย `20260622022145_AddVaultRevealAudit.cs`) |
| control-plane | **ไม่อยู่ใน policy** — กั้นด้วย GRANT อย่างเดียว | `Tenants, AdminAccounts, ProducerAccounts, *Roles, *Sessions, *Assignments, *Audits, DataProtectionKeys` |

`20260629085733_AddProducerAccountAdminParity.cs:39-42` ถอด predicate ออกจาก `TenantUsers` ตอน graduate เป็น
control-plane `ProducerAccounts`. RLS มีผลกับทุก principal แม้ sysadmin — ทางข้ามเดียว = membership ใน `pol_rls_bypass`.

---

## 6. Session-context stamping

`src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/SessionContextConnectionInterceptor.cs` —
`DbConnectionInterceptor` ที่ทุกครั้ง connection เปิด (physical open) รัน:

```sql
EXEC sys.sp_set_session_context @key = N'TenantId', @value = @tenant, @read_only = 1;
```

- ค่ามาจาก `ITenantContext.TenantId`; รันเฉพาะเมื่อ `_tenant.HasTenant` = true (:37-38). `@read_only = 1` ->
  request code แก้ทับไม่ได้.
- ต้องรันตอน connection-open (ไม่ใช่ per-query) เพราะ SESSION_CONTEXT เป็น per-connection — pooled connection
  ที่ reuse จะค้างค่าของ tenant ก่อนหน้า (comment ยืนยัน spike 2026-06-21).
- ผูกกับ default ctx (pol_app) + worker ctx (pol_worker) เท่านั้น. keyed `"admin"` **ไม่**ผูก -> SESSION_CONTEXT
  ไม่ถูกตั้ง -> bypass role governs.

---

## 7. App-layer guards

- **`TenantGuardBehavior`** (`BuildingBlocks.Application/TenantGuardBehavior.cs`, register `Program.cs:76`) —
  MediatR pipeline behavior: ปฏิเสธ message ที่ implement `ITenantScoped` เมื่อ `!HasTenant` ->
  `TenantBindingException` -> 500 opaque. กัน "tenant-scoped แต่ไม่มี tenant = RLS scoping หาย".
- **`ITenantContext`** (scoped) — `HttpTenantContext` precedence: explicit `AmbientTenant` binding ->
  `tenant_id` **claim** จาก authenticated principal -> dev fallback. **ไม่เคย** เอา tenant จาก URL path.
- **`ITenantScope` / `AmbientTenant`** — `Begin(tenantId)` ตั้ง scoped binding (throw ถ้า bind ซ้ำ =
  confused-deputy guard), dispose แล้วล้าง. ใช้ที่ entry point ที่ไม่มี auth claim: webhook, outbox
  dispatcher, vault audit writer.

---

## 8. EXECUTE AS procs

proc ที่ `WITH EXECUTE AS '<bypass member>'` — ตัว proc query แบบ bypass ได้ ขณะ caller (`pol_app`) ยังถูกกั้น:

| proc | EXECUTE AS | หน้าที่ |
|---|---|---|
| `usp_resolve_webhook_tenant` | `pol_webhook_resolver` | map PSP connection id -> tenant id (`AddRlsSecurityPolicy.cs:49-56`) |
| `usp_resolve_order_summary` | `pol_webhook_resolver` | resolve anonymous order-summary token (`AddOrderSummaryToken.cs`) |
| `usp_vault_audit_head` | `pol_vault_auditor` | อ่าน audit-chain head ต่อ tenant (`AddVaultRevealAudit.cs`); pol_app INSERT-only, SELECT ไม่ได้ |

login-less user เหล่านี้ **ไม่ขึ้นกับจำนวน connecting login** — เป็น proc-execution identity.

---

## 9. Per-principal GRANT (least privilege)

RLS bypass ข้าม *predicate* ไม่ข้าม *GRANT* -> grant เป็นด่านที่สอง อิสระ. grant matrix หลักที่
`AddRlsSecurityPolicy.cs:77-108`:

- `pol_app`: CRUD 8 ตาราง tenant + `CartItems`; SELECT/INSERT `IdempotencyRecords`; **INSERT-only**
  `OutboxMessages` (อ่าน payload tenant อื่นไม่ได้); EXECUTE webhook resolve proc. (INSERT-only `VaultRevealAudits`
  เพิ่มที่ vault migration.)
- `pol_worker`: SELECT/UPDATE `OutboxMessages`, `Orders` เท่านั้น.
- `pol_admin`: cross-tenant **SELECT** ตาราง data ทั้งหมด (`:99-107`) — **ไม่มี** grant อ่าน vault plaintext.
  ส่วน grant ตาราง control-plane (`AdminAccounts`, roles, sessions, `ProducerAccounts`, `DataProtectionKeys`, ...)
  อยู่ใน identity migration (`AddAdminIdentityTables`, `AddProducerIdentityTables`, `AddDataProtectionKeys`, ...).
- `pol_webhook_resolver`: SELECT `PspConnections` (+`Orders` สำหรับ summary proc). `pol_vault_auditor`: SELECT
  `VaultRevealAudits`.
- ไม่มี `db_owner`/`db_datareader`/blanket role กับ runtime login เลย — grant ต่อตารางล้วน; runtime login ไม่มีสิทธิ DDL.

---

## 10. Flow การทำงาน (A-E)

### Flow A — Tenant-facing request (เช่น `GET /api/v1/products`)

```
HTTP + Bearer(id-token, tenant audience)
  -> auth -> tenant_id claim
  -> HttpTenantContext.TenantId (จาก claim)
  -> [default ProducerDbContext = pol_app]
  -> connection open -> SessionContextConnectionInterceptor
        EXEC sp_set_session_context 'TenantId' = <tenant>  (read_only)
  -> query Products
  -> RLS fn_tenant_predicate: TenantId = SESSION_CONTEXT  -> เห็นเฉพาะ row ของ tenant นี้
[ถ้า message เป็น ITenantScoped แต่ HasTenant=false -> TenantGuardBehavior โยน 500 ก่อนแตะ DB]
```

Isolation มาจาก: RLS (pol_app non-bypass) + SESSION_CONTEXT จาก claim.

### Flow B — Admin provisioning (`POST /api/v1/admins/tenants`) — cross-tenant

```
HTTP + Admin session cookie (BFF)
  -> RBAC: RequirePermission (operation authz ที่ app layer)
  -> ProvisionTenantHandler [keyed "admin" = pol_admin, ไม่มี interceptor]
  -> connection open: SESSION_CONTEXT('TenantId') = ไม่ถูกตั้ง
  -> INSERT Tenant (control-plane, ไม่อยู่ใน RLS)
     INSERT PspConnection, VaultSecret ของ tenant ใหม่ (FILTER+BLOCK tables)
  -> RLS BLOCK predicate ผ่านเพราะ IS_ROLEMEMBER('pol_rls_bypass')=1  (pol_admin)
```

ข้อสังเกต: provisioning **พึ่ง bypass ล้วน ไม่ bind SESSION_CONTEXT** แม้จะรู้ tenant id ใหม่อยู่แล้ว.

### Flow C — Worker outbox drain

```
Worker loop [pol_worker + interceptor], lease pass: HasTenant=false
  -> SELECT OutboxMessages (BLOCK-only, ไม่มี FILTER) -> อ่านได้ทุก tenant
  -> ต่อ message: ITenantScope.Begin(msg.TenantId)   (OutboxDispatcher.cs:106-109)
  -> fresh scope -> connection open -> interceptor stamps SESSION_CONTEXT = msg tenant
  -> consumer เขียน Orders -> RLS scoped ตาม tenant นั้น
```

pol_worker non-bypass เขียน scoped ได้เพราะ bind tenant ต่อ message.

### Flow D — Webhook (PSP callback, ไม่มี auth claim)

```
HTTP callback (มี connection id, ยังไม่รู้ tenant)
  -> WebhookTenantResolver (fresh DI scope — กัน request ctx เปิด connection ก่อนรู้ tenant)
  -> usp_resolve_webhook_tenant  [EXECUTE AS pol_webhook_resolver, bypass]
        map connection id -> tenant id
  -> ITenantScope.Begin(tenantId)   (Program.cs:479-483)
  -> dispatch -> default ctx pol_app -> interceptor stamps tenant -> RLS scoped
```

bypass ใช้เฉพาะ "หา tenant จาก connection id" (ต้องอ่านก่อนรู้ tenant) แล้ว flow หลักกลับมา scoped.

### Flow E — Vault reveal + audit

```
reveal secret ของ tenant X:
  -> อ่าน VaultSecrets ของ X  [pol_app CRUD, RLS scoped ด้วย SESSION_CONTEXT=X]
  -> usp_vault_audit_head  [EXECUTE AS pol_vault_auditor, bypass] อ่าน audit-chain head (pol_app SELECT ไม่ได้)
  -> VaultRevealAuditWriter (fresh tenant-bound scope, .cs:30-33)
        INSERT VaultRevealAudits -> BLOCK predicate ผ่านเพราะ scope bind tenant X
```

---

## 11. File map

| ชิ้นส่วน | ไฟล์ |
|---|---|
| connection strings (Api) | `src/Hosts/Api/appsettings.json:10-13` |
| connection string (Worker) | `src/Hosts/Worker/appsettings.json:10-12` |
| principals + role + members | `docker/bootstrap/01-principals.sql` |
| conn build ตอน container start | `docker/entrypoint.sh:18,29`; `docker-compose.prod.yml` |
| credential guard | `src/Hosts/Api/Program.cs:1763-1769` |
| RLS predicate + policy + grant matrix | `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/20260621133209_AddRlsSecurityPolicy.cs` |
| session-context interceptor | `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/SessionContextConnectionInterceptor.cs` |
| tenant guard behavior | `src/BuildingBlocks/BuildingBlocks.Application/TenantGuardBehavior.cs` |
| ambient tenant / scope | `ITenantContext.cs`, `ITenantScope.cs`, `AmbientTenant.cs`, `src/Hosts/Api/HttpTenantContext.cs` |
| keyed "admin" registration | `src/Hosts/Api/AdminScopedServices.cs:71-94` |
| EXECUTE AS procs | `AddRlsSecurityPolicy.cs`, `AddVaultRevealAudit.cs`, `AddOrderSummaryToken.cs` |
