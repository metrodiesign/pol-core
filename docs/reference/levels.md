# โมดูล Levels — Reference Master Data (ระดับ)

> As-built 2026-08-07. แหล่งความจริง: `src/Modules/Levels/**`,
> `src/Persistence/Persistence.ControlPlane/Levels/**`, `src/Hosts/Api/Program.cs` (route mapping)

## บริบท

Levels เป็น 1 ใน 4 โมดูล reference master data ที่โครงสร้างเหมือนกันเป๊ะ (Divisions/Levels/Offices/Positions)
ประวัติ: เดิมเป็น field บนโมดูล Admin → แยกเป็นโมดูล `MasterData` เดียว (PR #109, 2026-07-13) → แตกเป็น 4
โมดูลอิสระแล้วลบ `MasterData` ทิ้ง (`masterdata-split`, PR #117 MERGED 2026-07-19) → เพิ่ม full CRUD (PR
เดียวกับ split, commit `46bbecd`, 2026-07-20). DDL ไม่เปลี่ยนตลอดทาง

โมดูลพี่น้องที่มีไฟล์ reference แยกแบบนี้เหมือนกัน: [`divisions.md`](divisions.md),
[`offices.md`](offices.md), [`positions.md`](positions.md) ภาพรวมรวม 4 โมดูลร่วมกันยังอยู่ใน
[`platform-modules.md`](platform-modules.md#reference-master-data),
[`layers-guide.md`](layers-guide.md), [`src-structure.md`](src-structure.md),
[`entity-fields.md`](entity-fields.md)

## Domain model (`Levels.Domain`)

`Level` (`Level.cs`) — `sealed class Level : AggregateRoot<Guid>`

| Property | Type | แก้ได้ยังไง |
|---|---|---|
| `Code` | string | ตั้งได้ครั้งเดียวตอน `Create()` — regex `^[a-z0-9_]+$`, immutable ตลอดไป (identity) |
| `Name` | string | `Rename(name)` |
| `Status` | `LevelStatus` (`Active=0`, `Inactive=1`) | `Activate()` / `Deactivate()` |

Invariant: ไม่มี state machine, ไม่มี concurrency token. `Deactivate()` **ไม่ใช่การลบ** — FK จาก `admin.Users`
เป็น `Restrict` (comment ในโค้ดยืนยันตรงๆ ว่า "never a hard delete"). Comment บน aggregate เอง: "standalone
aggregate since masterdata-split — the retired shared base logic lives inline, verbatim" (เคยมี base class
กลางที่ retire ไปแล้ว)

## Application layer (`Levels.Application`)

ไม่มี Command/Query/Handler ผ่าน Mediator — **ตั้งใจ bypass Mediator** (reference data control-plane แบบง่าย)
มีแค่ interface เดียว `ILevelStore` (`LevelStore.cs`) + DTO `LevelItem(Guid Id, string Code, string Name,
LevelStatus Status)`:

| Method | คืนอะไร | error |
|---|---|---|
| `ListAsync(page, limit, search, ct)` | `PagedResult<LevelItem>` | — |
| `CreateAsync(code, name, ct)` | `LevelItem` | code ซ้ำ → `ConflictException` 409 |
| `UpdateAsync(id, name, status, ct)` | `LevelItem` | status ต้อง `Active` หรือ `Inactive`; id ไม่พบ → `NotFoundException` 404 |
| `GetByIdAsync(id, ct)` | `LevelItem` | id ไม่พบ → 404 |
| `DeactivateAsync(id, ct)` | `LevelItem` (soft) | id ไม่พบ → 404 |

`Levels.Application.csproj` อ้างอิงแค่ `Levels.Domain` + `BuildingBlocks.Application` — ไม่มี cross-module
`.Domain` reference ใดๆ. Lookup ของ FK บน admin profile **ไม่ได้อยู่ในโมดูลนี้** — เป็นของ
`Admins.Application.Users.IProfileLookup` (caller need ไม่ใช่ use case ของ Levels)

## Infrastructure

**Schema `cfg.Levels`** (control-plane, `ControlPlaneDbContext`, ไม่มี query filter/RLS) — คอลัมน์: `Id`
(PK), `Code` (nvarchar(64), unique index), `Name` (nvarchar(200)), `Status` (int) รายละเอียด field เต็ม:
[`entity-fields.md`](entity-fields.md)

- `Levels.Infrastructure/LevelsModuleRegistration.AddLevelsModule()` คืน `services` เปล่า — เป็นแค่ wiring hook
  ให้ assembly ถูกโหลด (`HostModuleAssemblies.All`) เพื่อ EF model discovery **ไม่ได้ register store ที่นี่**
- **Store จริงอยู่คนละที่**: `Persistence.ControlPlane.Levels.LevelStore` (implement `ILevelStore`) ผูกกับ
  `ControlPlaneDbContext` + keyed `"admin"` `IUnitOfWork` (`ControlPlaneUnitOfWork`) — bind ผ่าน
  `ControlPlanePersistenceRegistration.AddControlPlanePersistence`. `CreateAsync` pre-check duplicate `Code`
  ก่อน 409 (unique index เป็น race-safe backstop)
- **`LevelConfiguration` มี 2 คลาสคนละ namespace ต้อง sync มือ**: `Levels.Infrastructure.Persistence.
  LevelConfiguration` (apply เข้า `PolDbContext` ผ่านสแกน assembly) กับ `Persistence.ControlPlane.Levels.
  LevelConfiguration` (apply ตรงใน `ControlPlaneDbContext.OnModelCreating`) — mapping ต้องเหมือนกันทุก field
  เป๊ะ (comment ในโค้ด: "must stay in lockstep") แก้ไฟล์เดียวแล้วลืมอีกไฟล์ = schema drift เงียบข้าม migration
- Migration owner จริงคือ `PolDbContext` (ไม่ใช่ `ControlPlaneDbContext` — context นี้ไม่มี migration ของตัวเอง)

## API endpoints (`src/Hosts/Api/Program.cs`, generic `MapMasterCrud<ILevelStore, LevelItem>`)

Top-level API area (ไม่อยู่ใต้ `/admins`, ย้ายออกมาตั้งแต่ 2026-07-20) ทุก verb gate `RequireAuthorization
("admin")` + `RequirePermission(Keys.UserManage)` (`"user.manage"`) + `AddEndpointFilter<CsrfFilter>()` เอง —
Scalar tag = คำไทย `"ระดับ"` (`var tag = thaiLabel`, ไม่ใช่ "Levels")

| Method | Path | Success | Error |
|---|---|---|---|
| GET | `/api/v1/levels` | 200, paged + `q` search (SFS) | — |
| GET | `/api/v1/levels/{id:guid}` | 200 | 404 ไม่พบ id |
| POST | `/api/v1/levels` | 201 | 400 code ผิด `^[a-z0-9_]+$`; 409 code ซ้ำ |
| PUT | `/api/v1/levels/{id:guid}` | 200 (rename + set `Status`, code แก้ไม่ได้) | 400 status ไม่ใช่ 0/1; 404 ไม่พบ id |
| DELETE | `/api/v1/levels/{id:guid}` | 204 (soft-deactivate เท่านั้น — **ไม่ hard-delete**) | 404 ไม่พบ id |

Wire DTO ร่วมกับอีก 3 โมดูล: `MasterWriteRequest(Code, Name)`, `MasterUpdateRequest(Name, Status)`,
`MasterResponse(Id, Code, Name, Status)`

`LevelId` ยังโผล่เป็น FK บน endpoint ของ `Admins` module (ไม่ใช่ endpoint ของ Levels เอง): `PUT
/api/v1/admins/{id}/profile` รับ `levelId` (nullable Guid, full-replace); response DTO ของ admin detail มี
`level: { id, code, name } | null`

## Migration history

| Migration | ผล |
|---|---|
| `20260807042818_InitialSchema` | สร้าง `cfg.Levels` (PK `Id`, unique index `Code`) + FK `admin.Users.LevelId` → Restrict |
| `20260807042833_SeedData` | seed 10 แถวคงที่ id prefix `c3000000-…` (`level_1`..`level_10`) |

ไม่มี migration อื่นแก้ schema ของ Levels ต่อจากนั้น

## Cross-reference

- ภาพรวม 4 โมดูลร่วมกัน (Divisions/Offices/Positions รายละเอียดเดียวกัน):
  [`platform-modules.md`](platform-modules.md#reference-master-data)
  §15, [`layers-guide.md`](layers-guide.md) §10, [`src-structure.md`](src-structure.md) §4.9-4.12
- Field-level schema เต็ม (คอลัมน์/type/FK/seed row ทั้ง 4 ตาราง): [`entity-fields.md`](entity-fields.md)
- FK consumer (`admin.Users.LevelId`, profile update endpoint): [`admins.md`](admins.md)

## Source of truth

`src/Modules/Levels/Levels.Domain/Level.cs`, `src/Modules/Levels/Levels.Application/LevelStore.cs`,
`src/Modules/Levels/Levels.Infrastructure/{LevelsModuleRegistration.cs,Persistence/LevelConfigurations.cs}`,
`src/Persistence/Persistence.ControlPlane/Levels/{LevelStore.cs,LevelConfiguration.cs}`,
`src/Hosts/Api/Program.cs` (route mapping) — ตัวเลข/พฤติกรรมในไฟล์นี้ต้อง sync กับโค้ด 3 จุดนี้เสมอ
