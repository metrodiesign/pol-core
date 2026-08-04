# Connection Strings + App-Layer Isolation Floor — คู่มือ reference (pol-core)

> **[rewrite เต็มฉบับ, 2026-07-19 — spec `rls-to-query-filter`]** เอกสารนี้เคยอธิบาย SQL Server native RLS
> เป็น isolation floor (pre-rf1 vocabulary) — RLS **ถูกถอดทิ้งทั้งหมดแล้ว** ใน 1 forward migration (task 8) และ
> ไม่มีอยู่จริงในระบบอีกต่อไป. เนื้อหาด้านล่างคือ current-state ของสถาปัตยกรรมใหม่: **app-layer isolation floor**
> (EF global query filter + sealed write guard), **1 DB principal เดียว** (`pol_app`), **3 runtime `DbContext`**
> แยกตาม cluster, และ **observability taxonomy** (REQ-13) ที่ชดเชย DB-level attribution ที่หายไปตอนยุบเหลือ
> 1 principal. Supersede: rf1 REQ-3.2/3.3/3.7/3.8, admin-actor-rename REQ-7.4.

คู่มือสรุปสถานะ **ปัจจุบัน** ของการเข้าถึง database ใน pol-core: connection string, SQL principal, EF query
filter, sealed write guard และ flow การทำงานจริงแต่ละเส้นทาง. เป็น current-state reference (ไม่ใช่
prescriptive) — อ้าง file:line ตามโค้ดจริง เพื่อให้ตามอ่านต่อได้.

> Stack: C# 14 / .NET 10 / EF Core 10 / SQL Server 2025 / martinothamar Mediator. การกรอง row ต่อ merchant
> ทำที่ **EF global query filter (deny-default)** เป็นพื้น (floor) — ไม่มี SQL RLS/security policy/
> `SESSION_CONTEXT`/`EXECUTE AS` bypass proc หลงเหลืออยู่เลย. สองชั้น app-layer (query filter อ่าน + sealed
> write guard เขียน) ประกบกันเป็น floor เดียวกัน ไม่ใช่ชั้นสะดวกเสริมบน floor อื่น.

> ข้อควรรู้: "account" ในระบบมี 2 ความหมายที่ห้ามสับสน — (1) **SQL principal / login** (`pol_app` — ตัวเดียว
> ตอนนี้) คือตัวที่ runtime ใช้ connect DB = "database access account" จริง; (2) **application identity row**
> (`Merchants.Domain.Users.User`, `Admins.Domain.Users.User`) คือแถวในตาราง identity keyed ด้วย Google `sub` =
> "คนที่ operate" ไม่ใช่ DB login. คู่มือนี้ว่าด้วยความหมาย (1).

- isolation floor (แนวคิดระดับสถาปัตย์): `../../.ai/shared/ARCHITECTURE.md`, `../../.ai/shared/SECURITY_RULES.md`
- design เต็มของ rewrite นี้: `../../.ai/specs/rls-to-query-filter/design.md`
- module map: `docs/reference/platform-modules.md`
- entity fields: `docs/reference/entity-fields.md`

---

## สารบัญ

0. [อธิบายแบบเข้าใจง่าย (ตึกให้เช่า)](#อธิบายแบบเข้าใจง่าย-ตึกให้เช่า) — อ่านก่อนถ้าไม่ใช่สาย technical
1. [Mental model — สองชั้น app-layer](#1-mental-model--สองชั้น-app-layer)
2. [Connection string -> principal](#2-connection-string---principal)
3. [SQL principal (เหลือใบเดียว)](#3-sql-principal-เหลือใบเดียว)
4. [3 runtime DbContext + migration-owner](#4-3-runtime-dbcontext--migration-owner)
5. [Read floor — EF query filter](#5-read-floor--ef-query-filter)
6. [Write floor — sealed guard](#6-write-floor--sealed-guard)
7. [Escape-hatch allowlist (แทน EXECUTE AS procs เดิม)](#7-escape-hatch-allowlist-แทน-execute-as-procs-เดิม)
8. [Observability — denial/rollback/authz taxonomy](#8-observability--denialrollbackauthz-taxonomy)
9. [Flow การทำงาน (A-E)](#9-flow-การทำงาน-a-e)
10. [File map](#10-file-map)

---

## อธิบายแบบเข้าใจง่าย (ตึกให้เช่า)

ส่วนนี้อธิบายแนวคิดด้วยการเปรียบเทียบ สำหรับคนที่ไม่ใช่สาย technical. เดินเรื่องด้วยภาพเดียว: **ตึกออฟฟิศให้เช่า
ที่มีหลายบริษัทมาเช่าห้อง** — เหมือนเอกสารเดิม แต่ **ไม่มีล็อกอัจฉริยะที่ตัวประตูอีกแล้ว**: ตอนนี้ **พนักงานเอง
(โปรแกรม) เป็นคนตรวจบัตรก่อนหยิบของทุกครั้ง** แทน.

> "บริษัท A / B / C" ในตัวอย่าง = **merchant จริงของแพลตฟอร์ม 3 เจ้า: vPrivilege / vCommerce / vSouvenir**
> (บริษัทในเครือ, allowlist). ทั้ง 3 ใช้ Merchant Console + backend + database **ชุดเดียวกัน** แต่ข้อมูลแยกเด็ดขาด
> ด้วยการเช็คของพนักงานทุกครั้ง — ดู [ตัวอย่างสถานการณ์จริง](#ตัวอย่างสถานการณ์จริง-vprivilege--vcommerce--vsouvenir) ท้ายหัวข้อนี้.

| ในเรื่องเปรียบเทียบ | ของจริงในระบบ | คืออะไร |
|---|---|---|
| ตัวตึก | Database | ที่เก็บข้อมูลของทุกคนรวมกัน |
| บริษัทที่เช่าห้อง (vPrivilege, vCommerce, vSouvenir) | Merchant | ลูกค้าแต่ละเจ้าที่ใช้ระบบเรา |
| ของในห้องบริษัท vPrivilege | ข้อมูล (row) ของ merchant vPrivilege | order, product, การจ่ายเงิน ของ vPrivilege |
| คีย์การ์ดเข้าตึก (ใบเดียวตอนนี้) | Connection string / `pol_app` | บัตรที่ "โปรแกรม" ใช้เข้าไปในฐานข้อมูล — เข้าได้ทุกห้องทางกายภาพ |
| **พนักงานที่ยืนตรวจของทุกครั้งก่อนหยิบ/วาง** (แทนล็อกอัจฉริยะเดิม) | EF query filter + sealed write guard | โค้ดแอปเช็คทุกครั้งว่า "ของชิ้นนี้เป็นของบริษัทที่กำลังให้บริการอยู่จริงไหม" ก่อนอ่าน/เขียน |
| ป้ายชื่อที่พนักงานถืออยู่ | `IActorContext.CurrentMerchant` | บอกพนักงานว่า "ตอนนี้ฉันกำลังบริการบริษัทไหน" |
| คู่มือพนักงาน (ใครทำอะไรได้) | RBAC / `IWriteAuthorizer` | กฎว่า role ไหนกดปุ่มอะไรได้ + เขียนอะไรได้ |
| สมุดจดเหตุการณ์ผิดปกติ (ใหม่) | `ISecurityTelemetry` -> Seq | ทุกครั้งที่พนักงานปฏิเสธไม่ให้หยิบของผิดห้อง ถูกจดไว้ส่งไปที่ศูนย์กลางแยกต่างหาก |

### Connection string / "DB account" คืออะไร (ตอนนี้เหลือใบเดียว)

Database เหมือน **ตึกที่เก็บของทุกบริษัทไว้รวมกัน**. โปรแกรมจะเข้าไปหยิบ/วางข้อมูลได้ ต้องมี **คีย์การ์ด** ก่อน —
คีย์การ์ดนั้นคือ **connection string**. **เดิมมี 3 ใบ** (`pol_app`/`pol_admin`/`pol_worker`, สิทธิ์ต่างกันที่ DB)
**ตอนนี้เหลือใบเดียว: `pol_app`** — Api host เดียว (Worker's background dispatch merge เข้ามาแล้ว,
`multi-tier-deployment`) เข้าตึกด้วยบัตรใบเดียวกันทุก flow, เข้าได้ทุกห้องทางกายภาพ
เหมือนกันหมด. สิ่งที่กันไม่ให้พนักงานหยิบของผิดห้องไม่ใช่บัตรอีกต่อไป — เป็น **พนักงานเองที่เช็คก่อนหยิบทุกครั้ง**
(โค้ดแอป).

### ทำไมยุบเหลือใบเดียว

เพราะ RLS (ล็อกอัจฉริยะที่ตัวประตู) ถูกถอดทิ้งทั้งระบบ — ไม่มีเหตุผลให้ต้องมีบัตรสิทธิ์ต่างกันอีกต่อไป (บัตรที่
"ข้ามล็อกได้" ไม่มีความหมายเมื่อไม่มีล็อกให้ข้าม). ทีมตัดสินใจแลก **least-privilege ที่ DB** (บัตรแยกสิทธิ์)
เพื่อความง่ายในการ operate — ความปลอดภัยทั้งหมดย้ายไปอยู่ที่ **แอปพลิเคชันชั้นเดียว** แทน (บันทึกไว้ใน
[design.md ของ `rls-to-query-filter`](../../.ai/specs/rls-to-query-filter/design.md) หัวข้อ "Human sign-off
item" — เจ้าของระบบ confirm รับความเสี่ยงนี้แล้ว).

### พนักงานเช็คของยังไง — สองขั้นตอน

1. **ตอนหยิบของ (อ่าน)** — พนักงานดูป้ายชื่อของตัวเอง (`CurrentMerchant`) แล้วเทียบกับป้ายบนกล่อง (`MerchantId`
   ของแถว) ก่อนหยิบให้ลูกค้าดูเสมอ — ไม่ตรง = ไม่ให้ดู (deny-default, ไม่ใช่ allow-list). ไม่มีป้ายชื่อผูกตัวเอง
   เลย (ไม่มี actor bound) = **เห็นศูนย์ชิ้น** ไม่ใช่เห็นหมด.
2. **ตอนวางของ (เขียน)** — พนักงานเช็คคู่มือ (`IWriteAuthorizer`) ว่า "ของประเภทนี้ ด้วยสิทธิ์ปัจจุบัน วางที่ห้องนี้
   ได้ไหม" ก่อนวางทุกครั้ง — ป้ายบนกล่องเปลี่ยนทีหลังไม่ได้ (immutable-after-insert), กล่องเก่าแก้ไม่ได้ถ้าเป็น
   audit log (append-only).

นี่คือเหตุผลที่ระบบ **ต้องพึ่งโค้ดแอปเขียนถูกทุกจุด** (ต่างจากเดิมที่ฐานข้อมูลกันให้ที่พื้นแม้โปรแกรมเขียนพลาด) —
เพื่อชดเชยจุดอ่อนนี้ ระบบมี **สมุดจดเหตุการณ์ผิดปกติ** (observability, REQ-13) ที่จดทุกครั้งที่พนักงานปฏิเสธ/เจอ
สถานการณ์แปลก ส่งไปศูนย์กลางแยกต่างหาก (Seq) ที่แก้ไขย้อนหลังไม่ได้.

### RBAC ต่างจาก isolation floor ยังไง (จุดที่มักสับสน — เหมือนเดิม)

สองอันนี้ **คนละแกน** ต้องแยก:

| | ตอบว่า | ตัวอย่าง |
|---|---|---|
| **RBAC** | "ใคร/ตำแหน่งไหน **กดปุ่มอะไร** ได้" | ผู้จัดการ **สร้าง** ห้องใหม่ได้, พนักงานทั่วไปสร้างไม่ได้ |
| **Isolation floor** | "คนนั้นเห็น/แก้ **ของห้องไหน**" | พนักงานที่ดูแลบริษัท A เห็น/แก้แค่ของ A |

**RBAC แทน isolation floor ไม่ได้**: คู่มือพนักงานไม่ได้เช็คว่าของชิ้นนี้เป็นของใคร. ต่อให้คู่มือเขียนว่า
"พนักงานคนนี้ดูออเดอร์ได้" มันไม่ได้บอกว่า **ออเดอร์ของบริษัทไหน** — ตัวที่บอกว่าเห็นของบริษัทไหนคือ query
filter + write guard เท่านั้น.

### ตัวอย่างสถานการณ์จริง (vPrivilege / vCommerce / vSouvenir)

3 บริษัทในเครือ = 3 merchant จริง (allowlist; `code` normalize เป็น lowercase: `vprivilege`, `vcommerce`,
`vsouvenir`). อยู่ในตึกเดียวกัน (database + backend ชุดเดียว) แต่คนละห้อง. 4 สถานการณ์ผูกกับ flow ในหัวข้อ 9:

**S1 — ตัวแทนของ vCommerce เปิดดูออเดอร์ตัวเอง** (= Flow A)
- ตัวแทนล็อกอิน Google SSO -> session cookie ผูก merchant = `vcommerce`
- `MerchantRuntimeDbContext.CurrentMerchant = <vcommerce id>` (จาก `IActorContext`)
- `GET /api/v1/orders` -> EF query filter โชว์เฉพาะออเดอร์ของ vcommerce; ของ `vprivilege`/`vsouvenir`
  **ไม่โผล่** แม้อยู่ในตาราง `Orders` เดียวกัน — filter ต่อไว้ที่ `DbSet` ระดับ `OnModelCreating`, handler เขียน
  query ปกติไม่ต้องกรองเองซ้ำ

**S2 — ทีมกลางเปิด merchant ใหม่ให้ vSouvenir** (= Flow B)
- Admin Console (session cookie) -> RBAC เช็คสิทธิ์ provision (operation authz)
- `ProvisioningCoordinator` (Super-only, task 7) — **บัตร `pol_app` ใบเดียวกัน**, ไม่มีบัตรมาสเตอร์แยกแล้ว —
  แทนที่ด้วย `WITH (UPDLOCK, HOLDLOCK)` recheck ว่า caller เป็น active Super ที่ `AuthorizationVersion` ที่
  คาดไว้ IN-TRANSACTION ก่อนเขียน `merch.Merchants`/`PspConnections`/`VaultSecrets` แบบ atomic (2 `DbContext`
  ใน tx เดียวกัน — the ONE cross-context write ในระบบ)

**S3 — ลูกค้าของ vCommerce จ่ายเงิน แล้ว PSP (2C2P/Omise) ยิง webhook กลับ** (= Flow D)
- callback มาแค่ connection id (ยังไม่รู้ว่า merchant ไหน)
- `WebhookMerchantResolver` (escape-hatch port, allowlisted) map connection id -> `vcommerce`
- `IActorScope.Begin(vcommerceId)` -> ยืนยัน/อัปเดตออเดอร์ของ vcommerce เท่านั้น

**S4 — งานเบื้องหลังส่งลิงก์สรุปออเดอร์ของ vPrivilege** (= Flow C)
- worker ดึง message จาก outbox (escape-hatch lease query, allowlisted — ตารางนี้ต้องเห็นทุก merchant เพื่อ
  drain)
- ต่อ message: `IActorScope.Begin(msg.MerchantId)` -> อ่าน/เขียนออเดอร์ของ vprivilege แบบ scoped

**บทสรุปที่เห็นจาก 4 สถานการณ์**: การแยก merchant (vcommerce เห็นแค่ vcommerce) เกิดจาก **EF query filter +
sealed write guard ที่ app layer** — ไม่มี DB-level floor เหลืออยู่แล้ว. RBAC ตัดสินแค่ "ใครกดปุ่ม
provision/ดูออเดอร์ได้"; ตัวที่กันไม่ให้ vcommerce เห็นออเดอร์ vsouvenir คือ query filter + guard.

---

## 1. Mental model — สองชั้น app-layer

การเข้าถึงข้อมูลถูกกั้นเป็นสองชั้น **ที่ app layer ทั้งคู่** (ไม่มี SQL floor แยกอีกต่อไป):

```
                request (HTTP)
                     |
   [app-layer]  MerchantGuardBehavior + IActorContext + RBAC/RequirePermission
                     |
                     v
   [read floor]  EF global query filter (deny-default, per-DbContext, OnModelCreating)
                     |
                     v
   [write floor] GuardedRuntimeDbContext.GuardPendingChanges (sealed SaveChanges override)
                     |   IWriteAuthorizer.CanWrite + concurrency token + immutable-after-insert + CHECK/FK
                     v
                SQL Server 2025  (1 principal: pol_app, no RLS, no bypass role)
```

- **RBAC != isolation floor**: RBAC = "ใคร/role ไหน ทำ operation อะไรได้" (app layer, operation authz). Read
  floor = "เห็น row ของ merchant ไหน" (app layer, query filter). Write floor = "เขียน row นี้ ด้วยสิทธิ์นี้ ได้
  ไหม" (app layer, sealed guard). สามอันคนละแกน.
- Query filter กัน **read** เท่านั้น — ทุก `SaveChanges` ต้องผ่าน write guard แยกต่างหาก (เขียนผ่าน tracked
  entity **หรือ** raw `ExecuteUpdate`/`ExecuteDelete` ก็โดนกันคนละแบบ: tracked ผ่าน guard, raw ผ่าน escape-hatch
  allowlist เท่านั้น — ดูหัวข้อ 7).
- ทุก denial จากทั้งสองชั้นยิง `ISecurityTelemetry.Emit` ไปสมุดจดกลาง (หัวข้อ 8) — ช่องทางเดียวที่เหลือให้
  detect attack/bug หลังยุบเหลือ 1 principal (ไม่มี DB-level audit ตาม principal แยกให้เทียบอีกแล้ว).

---

## 2. Connection string -> principal

| Config key | login (`User Id=`) | isolation posture | ใช้โดย | นิยามที่ |
|---|---|---|---|---|
| `ConnectionStrings:App` | `pol_app` | app-layer floor เท่านั้น (query filter + write guard) | Api — ทุก `DbContext` ทุก flow (HTTP request + background dispatcher ที่ merge เข้ามาแล้ว, `multi-tier-deployment`) | `src/Hosts/Api/appsettings.json:11` |
| `ConnectionStrings:Migrator` | *(privileged, ไม่ commit)* | — | dev boot auto-migrate | `src/Hosts/Api/Program.cs:390` |
| `POL_DESIGN_SQL` (env) | `sa` | — | `dotnet ef database update` (design-time DDL) | `.env:*`, `docker/migrate-entrypoint.sh` |

- Password ใน committed config = **ว่าง**; ฉีดตอน runtime ผ่าน env `ConnectionStrings__App`
  (ASP.NET map `__` -> `:`). มี `Database=VCentralPay;Encrypt=True` (ต่อ DB tier ระยะไกล — ดู
  `multi-tier-deployment` spec — เป็น `Encrypt=Strict` เมื่อ pin CA cert ผ่าน `DB_CA_CERTIFICATE_FILE`,
  ไม่งั้น `Encrypt=True;TrustServerCertificate=False` ต่อ OS trust store, ไม่มีทาง `True` ได้).
- นอก Development ถ้า password ว่าง -> fail-fast (`ProvisioningGuards.RequireInjectedCredential`,
  `src/Hosts/Api/Program.cs:~1944`).
- Prod: `docker-compose.prod.yml` กับ `docker/entrypoint.sh` สร้าง connection string จาก `DB_PRINCIPAL=pol_app`
  (host เดียว `api` — Worker merge เข้ามาแล้ว, ไม่มี service แยกอีกต่อไป) + password file secret.
- Api's `Program.cs` ห่อ connection string ด้วย `SqlConnectionStringBuilder { ApplicationName = "Api" }`
  ก่อนใช้ (REQ-13.3 — partial DB attribution แม้ใช้ 1 principal, ดูหัวข้อ 8).

---

## 3. SQL principal (เหลือใบเดียว)

นิยามที่ `docker/bootstrap/01-principals.sql` (รันเป็น `sa`, ก่อน EF migration, idempotent).

| login | posture | หน้าที่ |
|---|---|---|
| `pol_app` | ไม่มี RLS ให้ bypass, ไม่มี query filter ที่ DB — grant ครอบคลุมทุก runtime table (UNION ของสิทธิ์เดิมทุก principal) | ใช้โดย Api ทุก flow (HTTP request + background dispatcher scope ที่ merge เข้ามาแล้ว, ไม่มี Worker host แยกอีกต่อไป — `multi-tier-deployment`) |
| `sa` | — | bootstrap + DDL migration เท่านั้น; runtime login ไม่มีสิทธิ DDL; app ไม่เคยใช้ |

**ถูกถอดทิ้งทั้งหมดในการ migration เดียว (task 8):** `pol_admin`, `pol_worker`, `pol_rls_bypass` role,
login-less `pol_webhook_resolver`/`pol_vault_auditor` (`EXECUTE AS` proc identity เดิม). ไม่มี principal แยกตาม
capability เหลืออยู่แล้ว — capability แยกที่ app layer ผ่าน `IWriteAuthorizer` implementation แทน (หัวข้อ 6).

---

## 4. 3 runtime DbContext + migration-owner

`PolDbContext` เดิม (single DbContext ที่ RLS ผูกไว้) **ไม่ถูกลบ** แต่เหลือบทบาทเดียว: **migration-owner**
(`dotnet ef migrations add` ยังชี้มาที่นี่, CLR name kept) — **ไม่ registered ที่ runtime เลย**, ไม่มี host ไหน
resolve มันได้จริง.

Runtime ใช้ **3 context แยกตาม cluster** แทน — แต่ละอันมี query filter ของตัวเอง (หัวข้อ 5) และสืบทอด sealed
write guard เดียวกัน (`GuardedRuntimeDbContext`, หัวข้อ 6):

| DbContext | schema ที่คุม | query filter | registration |
|---|---|---|---|
| `ControlPlaneDbContext` | `admin`, `iam`, `cfg` | **ไม่มี** (control-plane ไม่มี merchant dimension) | `Persistence.ControlPlane/ControlPlanePersistenceRegistration.cs` |
| `MerchantUserDbContext` | `merch` (identity/session ส่วนเดียว) | เฉพาะ `Users`/`RoleAssignments` | `Persistence.MerchantUsers/MerchantUserPersistenceRegistration.cs` |
| `MerchantRuntimeDbContext` | `shop`, `txn`, `merch` (data ส่วนเดียว) | ทุก entity ที่ implement `IMerchantFiltered` | `Persistence.MerchantRuntime/MerchantRuntimePersistenceRegistration.cs` |

แต่ละ context อยู่คนละ assembly (`Persistence.ControlPlane`/`Persistence.MerchantUsers`/
`Persistence.MerchantRuntime`, `internal sealed class`) — ไม่มี `InternalsVisibleTo(Api)` เลย (ยกเว้น
`Persistence.Provisioning` ที่ต้องแตะสองอัน, หัวข้อ 9 Flow B) กันไม่ให้ host เห็น context ตรง ๆ; adapter สำหรับ
port ของ Application layer ต้องอยู่ใน assembly เดียวกับ context ที่มันแตะเสมอ.

Ctor ทั้ง 3 context รับ `ISecurityTelemetry` เป็น param สุดท้าย (task 9) — ใช้ยิง denial event จาก guard ภายใน
(หัวข้อ 8).

---

## 5. Read floor — EF query filter

นิยามใน `OnModelCreating` ของแต่ละ `EntityTypeConfiguration` (เช่น
`Persistence.MerchantRuntime/.../OrderConfiguration.cs`):

```csharp
builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);
```

- **Deny-default**: `context.CurrentMerchant` มาจาก `IActorContext.CurrentMerchant` — ถ้า `HasActor == false`
  (ไม่มี actor ผูก) มันคือ throw ไม่ใช่ wildcard match, ดังนั้น query ที่ไม่มี actor ผูกจะเห็น **ศูนย์แถว** ไม่ใช่
  เห็นหมด (fail-closed).
- `TenantKeyDescriptor` (`BuildingBlocks.Infrastructure/Persistence/TenantKeyDescriptor.cs`) mark entity ว่ามี
  tenant key คอลัมน์ไหน — arch test (`Architecture.Tests.ReadFloorTests`) เช็คว่าทุก entity ที่ควรมี filter มี
  จริง (deny-by-omission = red).
- **IDOR closure**: `CartItems` (denormalize `MerchantId` ของตัวเองแทนพึ่ง parent join, REQ-6) ปิดช่องที่เดิม
  ต้อง join ผ่าน `Carts` ก่อนถึงจะกรองได้ — ตอนนี้กรองตรงที่ตัวมันเอง.
- `IgnoreQueryFilters()` ข้าม read floor ได้ — **ห้าม** เว้นแต่อยู่ใน escape-hatch allowlist (หัวข้อ 7).

---

## 6. Write floor — sealed guard

`GuardedRuntimeDbContext` (`BuildingBlocks.Infrastructure/Persistence/GuardedRuntimeDbContext.cs`) เป็น base
class ของทั้ง 3 runtime context — override `SaveChanges`/`SaveChangesAsync` แบบ **sealed** (derived class
เขียนทับไม่ได้) เรียก `GuardPendingChanges()` ก่อนทุกครั้งเสมอ, เช็คต่อ tracked entry:

1. **`IWriteAuthorizer.CanWrite(entityType, operation, targetMerchant)`** — default-deny, ต้องมี capability
   ที่ host ผูกไว้ชัดเจนถึงจะเขียนผ่าน (4 implementation ต่อ flow, ดูตารางล่าง)
2. **Concurrency token** — `RowVersion`/`AuthorizationVersion`-style column บังคับเข้า `WHERE` clause ของ
   `UPDATE` ที่ EF emit; ชนกัน = `DbUpdateConcurrencyException`
3. **Tenant-key immutable-after-insert** — เปลี่ยน `MerchantId` ของแถวที่มีอยู่แล้วไม่ได้ (ทางเดียวที่ยอมให้
   ผ่าน = pre-bind approve/reject write ที่มี WHERE predicate ของตัวเองเป็น immutability enforcement แทน,
   หัวข้อ 7)
4. **`MerchantId == Guid.Empty` reject** — sentinel/unbound hit ที่ guard ระดับนี้ (แยกจาก deny-default ของ
   read floor)
5. Set-based DML (`ExecuteUpdate`/`ExecuteDelete`) **ข้าม guard นี้ไปเลย** โดยธรรมชาติ (ไม่ผ่าน change
   tracker) — ต้องอยู่ใน escape-hatch allowlist เท่านั้น (หัวข้อ 7)

**4 production `IWriteAuthorizer` implementation** (host-owned, `internal sealed`):

| Class | ที่ | capability |
|---|---|---|
| `MerchantRequestWriteAuthorizer` | `src/Hosts/Api/Persistence/WriteAuthorizers.cs` | merchant request เขียนได้เฉพาะ `targetMerchant` ของ actor ตัวเอง |
| `ControlPlaneAdminWriteAuthorizer` | เดียวกัน | admin เขียน control-plane entity ผ่าน `IAdminScope`/RBAC |
| `ProvisioningSuperWriteAuthorizer` | เดียวกัน | ครอบคลุมเฉพาะ entity set ของ provisioning ภายใต้ Super lock |
| `WorkerWriteAuthorizer` | `src/Hosts/Api/BackgroundDispatch/WorkerWriteAuthorizer.cs` (moved in from the retired standalone Worker host, class name kept — `multi-tier-deployment`) | outbox dispatch capability (cross-merchant drain ที่ระบุ merchant ต่อ message) |

ล้มเหลวจุดไหนของ guard = `WriteGuardException`/`ConcurrencyConflictException`/`ConflictException` (แปลจาก SQL
2627/2601/547) + `ISecurityTelemetry.Emit` (หัวข้อ 8).

---

## 7. Escape-hatch allowlist (แทน EXECUTE AS procs เดิม)

เดิมมี proc `WITH EXECUTE AS '<bypass member>'` (3 ตัว: webhook resolver, order-summary resolver, vault-audit
head reader) ให้ query bypass RLS แบบจำกัดเฉพาะ proc นั้น — **ไม่มีอยู่แล้ว**. แทนที่ด้วย **named escape-hatch
port** ที่ตั้งชื่อไว้ชัดเจน ทำ `IgnoreQueryFilters()`/`ExecuteUpdate`/`ExecuteDelete`/raw SQL ได้เฉพาะไฟล์ที่
อยู่ใน allowlist (`Architecture.Tests.BypassPrimitiveTests.AllowedPorts`, regex-scan gate — call site ใหม่นอก
allowlist = red CI ทันที ไม่ต้องรอ code review จับ):

| Port | หน้าที่ (เทียบ proc เดิม) |
|---|---|
| `Persistence.MerchantRuntime/Webhooks/WebhookMerchantResolver.cs` | map PSP connection id -> merchant id (เดิม `usp_resolve_webhook_tenant`) |
| `Persistence.MerchantRuntime/Orders/OrderSummaryReader.cs` | resolve anonymous order-summary token (เดิม `usp_resolve_order_summary`) |
| `Persistence.MerchantRuntime/Vault/VaultAuditAppender.cs` | `sp_getapplock`-based audit-chain append (เดิม `usp_vault_audit_head`, ตอน task 6 เปลี่ยนกลไก) |
| `Persistence.ControlPlane/Admins/SessionStore.cs`, `Persistence.MerchantUsers/Users/MerchantUserSessionStore.cs` | session rotate/revoke/prune (conditional `ExecuteUpdate`/`ExecuteDelete`) |
| `Persistence.MerchantRuntime/Outbox/OutboxDispatcher.cs`, `Persistence.MerchantUsers/Outbox/MerchantUserOutboxDrain.cs` | outbox lease query (ต้องเห็นทุก merchant เพื่อ drain) |
| `Persistence.MerchantUsers/Users/MerchantResolveLoginBySubject.cs`, `MerchantRegistrationWriter.cs`, `MerchantRegistrationSubmitWriter.cs` | pre-bind registration read/write (Subject lookup ก่อนมี `MerchantId`) |
| `Persistence.MerchantUsers/MerchantRoleAssignmentCountReader.cs`, `MerchantRoleAssignmentReader.cs` | cross-merchant role-assignment count/read (explicit param เสมอ ไม่ใช่ ambient state) |
| `Persistence.Provisioning/ProvisioningCoordinator.cs` | Super-recheck `UPDLOCK`/`HOLDLOCK` + idempotency-ledger raw INSERT |
| `Persistence.MerchantRuntime/Payments/Psp/ConnectionRepository.cs` | admin cross-merchant read-back (`ListByTenantAsync`, `GetMerchantHandler`'s ONE caller) |
| `Persistence.MerchantRuntime/Orders/Items/AdminItemPolicyWriter.cs` | admin cross-merchant `ItemPolicy` write (`policy-reference-record`) — `LoadAsync` ต้องอ่าน `OrderItem`/`ItemPolicy` ก่อนรู้ merchant ของ item; ตัวจำกัดขอบเขตจริงคือ `AdminItemPolicyWriteAuthorizer.CanWrite` ตอน `SaveChanges` (ชั้นนี้ข้าม **read** floor เท่านั้น). context เป็นของตัวเอง (`AddAdminItemPolicyWriter`) ไม่ใช่ ambient — ambient ผูก `MerchantRequestWriteAuthorizer` ซึ่ง deny ทุก admin write |
| `Persistence.MerchantRuntime/Orders/Items/PolicyReportSfs.cs` | admin cross-merchant policy report (`policy-reference-record`) — `BuildQuery(ignoreFilters: true)` ต่อ query root ทั้ง 3 (`OrderItem`/`Order`/`ItemPolicy`); confine ด้วย `IsUnrestrictedAdmin`/`AccessibleMerchantIds` + `?merchantId=` ที่ **caller** (`AdminItemPolicyReader`) — ตัว reader เองไม่มี bypass primitive จึงไม่อยู่ใน allowlist |

ทุกอันมีเหตุผลเดียวกัน: จุดที่ ambient `CurrentMerchant`/query filter **ใช้ไม่ได้โดยธรรมชาติ** (ยังไม่รู้
merchant, หรือต้อง cross-merchant โดยตั้งใจภายใต้ capability ที่ตรวจแล้ว) — ไม่ใช่ "ขี้เกียจเขียน filter"

---

## 8. Observability — denial/rollback/authz taxonomy

**ใหม่ทั้งหมด (REQ-13, task 9)** — ชดเชย DB-level attribution (แยกตาม principal) ที่หายไปตอนยุบเหลือ 1
principal. ทุก denial/anomaly path ยิง `ISecurityTelemetry.Emit(DenialEvent)`
(`BuildingBlocks.Application/ISecurityTelemetry.cs`) — bounded channel (10k, non-blocking) drain โดย
`BackgroundService` POST เป็น CLEF JSON ไปยัง **Seq** (`docker-compose.yml`, local dev service), retry 3x แล้ว
fallback log แทนดรอปเงียบ.

`DenialCategory` 11 ค่า (REQ-13.1's ลำดับเดิม) + call site หลัก:

| Category | Site |
|---|---|
| `GuardDenial` | `GuardedRuntimeDbContext` — append-only reject, tenant-key immutable |
| `CanWriteDenial` | `GuardedRuntimeDbContext` — `IWriteAuthorizer` deny |
| `ConcurrencyConflict` | ทั้ง 3 `IUnitOfWork` — `DbUpdateConcurrencyException` |
| `CheckOrForeignKeyViolation` | ทั้ง 3 `IUnitOfWork` — SQL 2627/2601/547 |
| `UnboundActor` | `MerchantGuardBehavior<,>` — `IMerchantScoped` ไม่มี actor ผูก |
| `EmptyOrSentinelHit` | `GuardedRuntimeDbContext` — `MerchantId == Guid.Empty` |
| `PortCardinalityAnomaly` | session `TrySupersedeAsync` (×2), `MerchantRegistrationWriter` approve/reject — affected-row 0 |
| `ApplockTimeout` | `VaultAuditAppender` — `sp_getapplock` timeout |
| `AdminCrossMerchantAction` | `ConnectionRepository.ListByTenantAsync`, `AdminItemPolicyReader.ListAsync`, `AdminItemPolicyWriter.LoadAsync` — escape-hatch use (emit **ครั้งเดียวต่อ call** ไม่ใช่ต่อแถว) |
| `AdminRevalidationDenial` | `AuthorizationLease.VerifyAsync`, `ProvisioningCoordinator.VerifyCallerIsActiveSuperAsync` |
| `RegistrationSentinelMisuse` | ไม่มี site แยก — degenerate เข้า `CanWriteDenial` |

`DenialEvent` มี ActorKind/ActorId/TargetMerchant/Entity/Operation/**Reason**/CorrelationId/OccurredAt
(REQ-13.2) — `Reason` เป็น **string literal ตายตัวเสมอ** (ห้าม `exception.Message`, ห้าม interpolate ค่าที่มี
PII/secret) บังคับด้วย `Architecture.Tests.SecurityTelemetryRedactionTests` (regex-scan ทุก `Emit(...)` call
site). `CorrelationId` มาจาก `System.Diagnostics.Activity.Current` (host-agnostic, ใช้ได้ทั้ง Api's HTTP
request และ Worker's background dispatch).

Alert + retention เป็นการตั้งค่าฝั่ง Seq เอง (Signals feature, stream retention policy) — operator config
post-deploy ไม่ใช่โค้ด.

---

## 9. Flow การทำงาน (A-E)

### Flow A — Product catalogue read (`GET /api/v1/products`) — ข้อยกเว้นของ isolation floor

> รายละเอียดเต็ม (ทำไมไม่มี query filter, ผลกระทบต่อ merchant-user) → [`products.md`](products.md)
> section "ข้อยกเว้นจาก convention ทั่วไปของ repo" — ที่นี่เก็บไว้แค่ flow diagram ให้ตรง pattern
> ของหัวข้อ 9 ในไฟล์นี้

```
HTTP + merchant-user session cookie
  -> auth: .RequireAuthorization("merchant-user") -> ไม่มี session = 401 ก่อนแตะ DB
  -> [SpDocumentGateway] connection แยกไปคนละ SQL Server (hippodb/mammothdb อยู่คนละ instance กันและ
     แยกจาก DB หลัก — external-sim-separate-containers) ผ่าน config section SpDocument ล้วน ๆ ไม่มี
     derive fallback (prod: docker/entrypoint.sh ประกอบ SpDocument__* จาก HIPPO_DB_SERVER/
     MAMMOTH_DB_SERVER แล้ว export ก่อน host boot) (ADO.NET, login pol_app มีแค่ EXECUTE)
  -> EXEC usp_{Motor|NonMotor}_SearchDocument @SaleCode=... @BranchCode=<จาก options ฝั่ง server>
  -> [MerchantRuntimeDbContext] upsert ผลลัพธ์เข้า shop.Products ตาม DocumentNo แล้วคืน Guid ของแถว local
  -> ไม่มี query filter ที่ DB/EF ระดับ entity -> "เห็นแถวไหน" ตัดสินที่ query criteria (SaleCode) ที่ caller
     ส่งมา ไม่ใช่ isolation floor (ต่างจากทุก entity อื่นในหัวข้อ 9)
```

Entity อื่นทั้งหมดในหัวข้อ 9 (Orders/Carts/PaymentSessions/…) ยังคง isolation มาจาก: EF query filter
(ทุก request ผ่าน context เดียวกัน, capability แยกที่ actor ไม่ใช่ principal) — `Product` เป็นข้อยกเว้น
เดียวที่ตั้งใจให้อยู่นอกโมเดลนี้.

### Flow B — Admin provisioning (`POST /api/v1/merchants`) — cross-context (the ONE)

```
HTTP + Admin session cookie (BFF)
  -> RBAC: RequirePermission (operation authz ที่ app layer)
  -> ProvisioningCoordinator.ProvisionAsync [Persistence.Provisioning, task 7]
  -> เปิด connection เดียว (pol_app) -> BeginTransaction
  -> ControlPlaneDbContext + MerchantRuntimeDbContext บน connection/tx เดียวกัน
  -> VerifyCallerIsActiveSuperAsync: SELECT ... WITH (UPDLOCK, HOLDLOCK) WHERE Tier=Super AND
     AuthorizationVersion=<expected>  [ล้มเหลว -> AdminRevalidationDenial telemetry + WriteGuardException]
  -> idempotency-ledger raw INSERT (ProvisioningOperations)
  -> INSERT Merchant, PspConnection(s), VaultSecret(s), ProvisioningAudit (ทั้งหมดผ่าน ProvisioningSuperWriteAuthorizer)
  -> SaveChanges(false) x2 -> Commit -> AcceptAllChanges x2
```

ข้อสังเกต: นี่คือ **จุดเดียวในระบบ** ที่ 2 runtime context แชร์ transaction เดียวกัน — ทุกที่อื่นแยกขาดกันเสมอ.

### Flow C — Background outbox drain (in-process, merged into Api)

> **[`multi-tier-deployment`, 2026-07-22]** เดิม flow นี้รันใน host `Worker` แยกต่างหาก — Worker ถูกลบทิ้ง
> ทั้งโปรเจกต์แล้ว, dispatcher ตัวเดิมรันเป็น `IHostedService` ใน Api process เดียวกันแทน (ไม่ใช่ network
> hop ข้าม host อีกต่อไป). ตัว discriminator ที่เลือก `WorkerActorContext`/`WorkerWriteAuthorizer` (แทน
> `HttpActorContext`/request authorizer) คือ `IHttpContextAccessor.HttpContext` เป็น `null` หรือไม่ — scope
> ที่ dispatcher สร้างเอง (`CreateScope()`, ไม่มี HTTP request ห่อ) ไม่มี `HttpContext` เสมอ.

```
Api's background dispatcher scope [MerchantRuntimeDbContext, WorkerWriteAuthorizer], lease pass: HasActor=false
  -> OutboxDispatcher.LeaseNextBatchAsync (escape-hatch allowlisted, ExecuteUpdate ข้าม query filter โดยธรรมชาติ)
     -> อ่านได้ทุก merchant (ตารางนี้ไม่มี IMerchantFiltered)
  -> ต่อ message: IActorScope.Begin(msg.MerchantId)
  -> fresh scope -> query filter ผูก merchant นั้นแล้ว
  -> consumer เขียน Orders -> ผ่าน write guard, CanWrite เช็ค WorkerWriteAuthorizer capability
```

เขียน scoped ได้เพราะ bind merchant ต่อ message ผ่าน `IActorScope`, ไม่ใช่เพราะ principal ต่างกัน (principal
เดียวกันทั้งหมด, `pol_app`) และไม่ใช่เพราะรันคนละ host อีกแล้ว — ตัวชี้คือ scope นั้นมี `HttpContext` หรือไม่.

### Flow D — Webhook (PSP callback, ไม่มี auth claim)

```
HTTP callback (มี connection id, ยังไม่รู้ merchant)
  -> fresh DI scope (กัน request ctx เปิด connection ก่อนรู้ merchant)
  -> WebhookMerchantResolver.ResolveAsync  [escape-hatch allowlisted, IgnoreQueryFilters()]
        map connection id -> merchant id
  -> IActorScope.Begin(merchantId)
  -> dispatch -> query filter ผูก merchant นั้นแล้ว
```

escape-hatch ใช้เฉพาะ "หา merchant จาก connection id" (ต้องอ่านก่อนรู้ merchant) แล้ว flow หลักกลับมา scoped
ปกติทันที.

### Flow E — Vault reveal + audit

```
reveal secret ของ merchant X:
  -> อ่าน VaultSecrets ของ X  [MerchantRuntimeDbContext, query filter scoped ด้วย CurrentMerchant=X]
  -> VaultAuditAppender.AcquireChainLockAsync  [escape-hatch allowlisted, sp_getapplock ต่อ merchant —
     กัน race บน hash-chain append, timeout -> ApplockTimeout telemetry]
  -> INSERT VaultRevealAudit  -> ผ่าน write guard ปกติ (append-only descriptor บังคับ, guard reject ทุก
     Delete/Modified บน entity นี้)
```

---

## 10. File map

| ชิ้นส่วน | ไฟล์ |
|---|---|
| connection strings (Api — host เดียว, Worker merge เข้ามาแล้ว) | `src/Hosts/Api/appsettings.json:9-12` |
| principal (1 ใบ) | `docker/bootstrap/01-principals.sql` |
| conn build ตอน container start | `docker/entrypoint.sh`; `docker-compose.prod.yml` |
| credential guard | `src/Hosts/Api/Program.cs` (`ProvisioningGuards.RequireInjectedCredential`) |
| migration-owner (ไม่ runtime) | `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/PolDbContext.cs` |
| sealed write guard base class | `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/GuardedRuntimeDbContext.cs` |
| `IWriteAuthorizer` + `WriteOperation` | `src/BuildingBlocks/BuildingBlocks.Application/IWriteAuthorizer.cs` |
| production `IWriteAuthorizer` impls | `src/Hosts/Api/Persistence/WriteAuthorizers.cs`, `src/Hosts/Api/BackgroundDispatch/WorkerWriteAuthorizer.cs` |
| 3 runtime `DbContext` + registration | `src/Persistence/Persistence.{ControlPlane,MerchantUsers,MerchantRuntime}/*PersistenceRegistration.cs` (assembly `MerchantUsers` **พหูพจน์**; ไฟล์ข้างในเป็น `MerchantUserPersistenceRegistration.cs` เอกพจน์) |
| cross-context provisioning UoW | `src/Persistence/Persistence.Provisioning/ProvisioningCoordinator.cs` |
| escape-hatch allowlist (enforced) | `tests/Architecture.Tests/BypassPrimitiveTests.cs` |
| observability core (channel/dispatcher/registration) | `src/BuildingBlocks/BuildingBlocks.Infrastructure/Observability/*.cs` |
| `ISecurityTelemetry`/`DenialEvent`/`DenialCategory` | `src/BuildingBlocks/BuildingBlocks.Application/ISecurityTelemetry.cs` |
| redaction test | `tests/Architecture.Tests/SecurityTelemetryRedactionTests.cs` |
| `MerchantGuardBehavior` (unbound-actor guard) | `src/BuildingBlocks/BuildingBlocks.Application/MerchantGuardBehavior.cs` |
| `IActorContext`/`IActorScope` | `src/BuildingBlocks/BuildingBlocks.Application/` |
| forward migration (RLS teardown) | `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/20260719081817_RlsTeardownAndOnePrincipal.cs` |
| spec เต็ม | `.ai/specs/rls-to-query-filter/{requirements,design,tasks}.md` |
