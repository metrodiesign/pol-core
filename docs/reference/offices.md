# โมดูล Offices — Reference Master Data (สำนักงาน)

> As-built 2026-08-07. แหล่งความจริง: `src/Modules/Offices/**`,
> `src/Persistence/Persistence.ControlPlane/Offices/**`, `src/Hosts/Api/Program.cs` (route mapping)

## บริบท

Offices เป็น 1 ใน 4 โมดูล reference master data ที่โครงสร้างเหมือนกันเป๊ะ (Divisions/Levels/Offices/Positions)
ประวัติ: เดิมเป็น field บนโมดูล Admin → แยกเป็นโมดูล `MasterData` เดียว (PR #109, 2026-07-13) → แตกเป็น 4
โมดูลอิสระแล้วลบ `MasterData` ทิ้ง (`masterdata-split`, PR #117 MERGED 2026-07-19) → เพิ่ม full CRUD (PR
เดียวกับ split, commit `46bbecd`, 2026-07-20). DDL ไม่เปลี่ยนตลอดทาง

โมดูลพี่น้องที่มีไฟล์ reference แยกแบบนี้เหมือนกัน: [`divisions.md`](divisions.md), [`levels.md`](levels.md),
[`positions.md`](positions.md) ภาพรวมรวม 4 โมดูลร่วมกันยังอยู่ใน
[`platform-modules.md`](platform-modules.md#reference-master-data),
[`layers-guide.md`](layers-guide.md), [`src-structure.md`](src-structure.md),
[`entity-fields.md`](entity-fields.md)

## Domain model (`Offices.Domain`)

`Office` (`Office.cs`) — `sealed class Office : AggregateRoot<Guid>`

| Property | Type | แก้ได้ยังไง |
|---|---|---|
| `Code` | string | ตั้งได้ครั้งเดียวตอน `Create()` — regex `^[a-z0-9_]+$`, immutable ตลอดไป (identity) |
| `Name` | string | `Rename(name)` |
| `Status` | `OfficeStatus` (`Active=0`, `Inactive=1`) | `Activate()` / `Deactivate()` |

Invariant: ไม่มี state machine, ไม่มี concurrency token. `Deactivate()` **ไม่ใช่การลบ** — FK จาก `admin.Users`
เป็น `Restrict` (comment ในโค้ดยืนยันตรงๆ ว่า "never a hard delete"). Comment บน aggregate เอง: "standalone
aggregate since masterdata-split — the retired shared base logic lives inline, verbatim" (เคยมี base class
กลางที่ retire ไปแล้ว)

## Application layer (`Offices.Application`)

ไม่มี Command/Query/Handler ผ่าน Mediator — **ตั้งใจ bypass Mediator** (reference data control-plane แบบง่าย)
มีแค่ interface เดียว `IOfficeStore` (`OfficeStore.cs`) + DTO `OfficeItem(Guid Id, string Code, string Name,
OfficeStatus Status)`:

| Method | คืนอะไร | error |
|---|---|---|
| `ListAsync(page, limit, search, ct)` | `PagedResult<OfficeItem>` | — |
| `CreateAsync(code, name, ct)` | `OfficeItem` | code ซ้ำ → `ConflictException` 409 |
| `UpdateAsync(id, name, status, ct)` | `OfficeItem` | status ต้อง `Active` หรือ `Inactive`; id ไม่พบ → `NotFoundException` 404 |
| `GetByIdAsync(id, ct)` | `OfficeItem` | id ไม่พบ → 404 |
| `DeactivateAsync(id, ct)` | `OfficeItem` (soft) | id ไม่พบ → 404 |

`Offices.Application.csproj` อ้างอิงแค่ `Offices.Domain` + `BuildingBlocks.Application` — ไม่มี cross-module
`.Domain` reference ใดๆ. Lookup ของ FK บน admin profile **ไม่ได้อยู่ในโมดูลนี้** — เป็นของ
`Admins.Application.Users.IProfileLookup` (caller need ไม่ใช่ use case ของ Offices)

## Infrastructure

**Schema `cfg.Offices`** (control-plane, `ControlPlaneDbContext`, ไม่มี query filter/RLS) — คอลัมน์: `Id`
(PK), `Code` (nvarchar(64), unique index), `Name` (nvarchar(200)), `Status` (int) รายละเอียด field เต็ม:
[`entity-fields.md`](entity-fields.md)

- `Offices.Infrastructure/OfficesModuleRegistration.AddOfficesModule()` คืน `services` เปล่า — เป็นแค่ wiring
  hook ให้ assembly ถูกโหลด (`HostModuleAssemblies.All`) เพื่อ EF model discovery **ไม่ได้ register store
  ที่นี่**
- **Store จริงอยู่คนละที่**: `Persistence.ControlPlane.Offices.OfficeStore` (implement `IOfficeStore`) ผูกกับ
  `ControlPlaneDbContext` + keyed `"admin"` `IUnitOfWork` (`ControlPlaneUnitOfWork`) — bind ผ่าน
  `ControlPlanePersistenceRegistration.AddControlPlanePersistence`. `CreateAsync` pre-check duplicate `Code`
  ก่อน 409 (unique index เป็น race-safe backstop) เขียนผ่าน `ExecuteInTransactionAsync` เสมอ ไม่เคยเรียก
  `SaveChanges` ตรงๆ
- **`OfficeConfiguration` มี 2 คลาสคนละ namespace ต้อง sync มือ**: `Offices.Infrastructure.Persistence.
  OfficeConfiguration` (apply เข้า `PolDbContext` ผ่านสแกน assembly) กับ `Persistence.ControlPlane.Offices.
  OfficeConfiguration` (apply ตรงใน `ControlPlaneDbContext.OnModelCreating`) — mapping ต้องเหมือนกันทุก field
  เป๊ะ (comment ในโค้ด: "must stay in lockstep") แก้ไฟล์เดียวแล้วลืมอีกไฟล์ = schema drift เงียบข้าม migration
- Migration owner จริงคือ `PolDbContext` (ไม่ใช่ `ControlPlaneDbContext` — context นี้ไม่มี migration ของ
  ตัวเอง)

## API endpoints (`src/Hosts/Api/Program.cs`, generic `MapMasterCrud<IOfficeStore, OfficeItem>`)

Top-level API area (ไม่อยู่ใต้ `/admins`, ย้ายออกมาตั้งแต่ 2026-07-20) ทุก verb gate `RequireAuthorization
("admin")` + `RequirePermission(Keys.UserManage)` (`"user.manage"`) + `AddEndpointFilter<CsrfFilter>()` เอง —
Scalar tag = คำไทย `"สำนักงาน"` (`var tag = thaiLabel`, ไม่ใช่ "Offices")

| Method | Path | Success | Error |
|---|---|---|---|
| GET | `/api/v1/offices` | 200, paged + `q` search (SFS) | — |
| GET | `/api/v1/offices/{id:guid}` | 200 | 404 ไม่พบ id |
| POST | `/api/v1/offices` | 201 | 400 code ผิด `^[a-z0-9_]+$`; 409 code ซ้ำ |
| PUT | `/api/v1/offices/{id:guid}` | 200 (rename + set `Status`, code แก้ไม่ได้) | 400 status ไม่ใช่ 0/1; 404 ไม่พบ id |
| DELETE | `/api/v1/offices/{id:guid}` | 204 (soft-deactivate เท่านั้น — **ไม่ hard-delete**) | 404 ไม่พบ id |

Wire DTO ร่วมกับอีก 3 โมดูล: `MasterWriteRequest(Code, Name)`, `MasterUpdateRequest(Name, Status)`,
`MasterResponse(Id, Code, Name, Status)`

`OfficeId` ยังโผล่เป็น FK บน endpoint ของ `Admins` module (ไม่ใช่ endpoint ของ Offices เอง): `PUT
/api/v1/admins/{id}/profile` รับ `officeId` (nullable Guid, full-replace); response DTO ของ admin detail มี
`office: { id, code, name } | null`

## Migration history

| Migration | ผล |
|---|---|
| `20260807042818_InitialSchema` | สร้าง `cfg.Offices` (PK `Id`, unique index `Code`) + FK `admin.Users.OfficeId` → Restrict |
| `20260807042833_SeedData` | seed 8 แถวคงที่ id prefix `b2000000-…` (`hq`, `north`, `northeast`, `central`, `east`, `west`, `south`, `remote`) |

ไม่มี migration อื่นแก้ schema ของ Offices ต่อจากนั้น

## Cross-reference

- ภาพรวม 4 โมดูลร่วมกัน (Divisions/Levels/Positions รายละเอียดเดียวกัน):
  [`platform-modules.md`](platform-modules.md#reference-master-data)
  §15, [`layers-guide.md`](layers-guide.md) §10, [`src-structure.md`](src-structure.md) §4.9-4.12
- โมดูลพี่น้องที่มีไฟล์ reference แยกแล้วเหมือนกัน: [`divisions.md`](divisions.md), [`levels.md`](levels.md),
  [`positions.md`](positions.md)
- Field-level schema เต็ม (คอลัมน์/type/FK/seed row ทั้ง 4 ตาราง): [`entity-fields.md`](entity-fields.md)
- FK consumer (`admin.Users.OfficeId`, profile update endpoint): [`admins.md`](admins.md)

## Source of truth

`src/Modules/Offices/Offices.Domain/Office.cs`, `src/Modules/Offices/Offices.Application/OfficeStore.cs`,
`src/Modules/Offices/Offices.Infrastructure/{OfficesModuleRegistration.cs,Persistence/OfficeConfigurations.cs}`,
`src/Persistence/Persistence.ControlPlane/Offices/{OfficeStore.cs,OfficeConfiguration.cs}`,
`src/Hosts/Api/Program.cs` (route mapping) — ตัวเลข/พฤติกรรมในไฟล์นี้ต้อง sync กับโค้ด 3 จุดนี้เสมอ
