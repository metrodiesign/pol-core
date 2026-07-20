# Requirements: masterdata-split

> Status: approved 2026-07-19

## Overview

MasterData module (PR #109) รวม reference data ของโปรไฟล์พนักงาน 4 ชุด (`Position` /
`Office` / `Level` / `Division`) ไว้โมดูลเดียว ใต้ abstract TPC base `MasterDataItem` +
generic `IMasterDataStore`. งานนี้แยกออกเป็น 4 โมดูลอิสระ (`Divisions`, `Levels`,
`Offices`, `Positions`) แล้วลบ MasterData ทิ้งทั้งโมดูล — user ปฏิเสธการเก็บ generic
core ไว้เป็น shared building block อย่างชัดเจน ดังนั้นห้ามมี shared base/interface ของ
master data หลงเหลือที่ใดในระบบ. **Behaviour-preserving ทั้งหมด** — endpoint,
permission key, wire contract, schema `cfg`, ตาราง และ seed data ไม่เปลี่ยน.

Locked decisions (user ตัดสิน 2026-07-19 — ห้าม re-litigate):
- แยกเป็น **4 โมดูล** โมดูลละ entity; **ลบ MasterData ทั้งโมดูล**
- **ไม่มี shared base** — ไม่ hoist `MasterDataItem`/`IMasterDataStore` ไป
  SharedKernel/BuildingBlocks (คือ option ที่ user ปฏิเสธ)
- route คงเดิม `/api/v1/admins/{positions|offices|levels|divisions}`
- permission key คงเดิม `user.manage` — ไม่แตะ iam catalog
- ตารางคงอยู่ schema `cfg` ชื่อเดิม, seed rows/GUID เดิม (12/8/10/10)
- workflow เต็มแบบมี gate

## REQ-1: Module split

**User Story:** As a platform developer, I want each reference list to live in its own
module, so that each can evolve independently (Level rank, Division hierarchy) without
touching the others.

**Acceptance Criteria (EARS):**
- 1.1 THE SYSTEM SHALL มีโมดูลใหม่ 4 โมดูลที่ `src/Modules/{Divisions,Levels,Offices,Positions}/`
  โมดูลละ 3 project ตาม template repo (`X.Domain`, `X.Application`, `X.Infrastructure`).
- 1.2 THE SYSTEM SHALL ประกาศ entity ของแต่ละโมดูลเป็น `sealed class` ที่สืบทอด
  `AggregateRoot<Guid>` ตรง ๆ อยู่ที่ module-root namespace (`Divisions.Domain.Division` ฯลฯ — L2).
- 1.3 THE SYSTEM SHALL NOT มี abstract base class หรือ shared interface ของ master data
  (เช่น `MasterDataItem`, `IMasterDataStore`) เหลืออยู่ที่ใดใน solution.
- 1.4 THE SYSTEM SHALL ลบ `src/Modules/MasterData/` ทั้ง directory และไม่เหลือ reference
  ถึง assembly `MasterData.*` ใน csproj/slnx ใดเลย.
- 1.5 THE SYSTEM SHALL ลงทะเบียน Infrastructure assembly ของทั้ง 4 โมดูลแทนที่
  `MasterData.Infrastructure` ใน `HostModuleAssemblies.All` เพื่อให้ `PolDbContext`
  discover entity configuration ได้.
- 1.6 THE SYSTEM SHALL เพิ่ม project ใหม่ทั้งหมดเข้า `pol-core.slnx` และ compile ผ่านเป็นส่วนหนึ่งของ solution build.

## REQ-2: Naming (hierarchical-naming L1-L8)

**User Story:** As a maintainer, I want the new modules to follow the naming law, so that
the codebase stays navigable by one consistent rule.

**Acceptance Criteria (EARS):**
- 2.1 THE SYSTEM SHALL ตั้งชื่อ project/namespace เป็นพหูพจน์ (`Divisions.Domain` ฯลฯ)
  และชื่อ type เป็นเอกพจน์ (`Division` ฯลฯ) (L3).
- 2.2 THE SYSTEM SHALL คงชื่อตาราง `Positions`/`Offices`/`Levels`/`Divisions` ใน schema
  `cfg` โดยไม่เปลี่ยน (L7).
- 2.3 WHEN การแยกเสร็จสมบูรณ์, THE SYSTEM SHALL เพิ่ม retired tokens ใน
  `scripts/check_rename_identifiers.py`: `MasterData`, `MasterDataItem`,
  `IMasterDataStore`, `IMasterDataLookup`, `MasterItem`, `MasterRef`.
- 2.4 THE SYSTEM SHALL ผ่าน `scripts/check-rename-identifiers.sh` หลังเพิ่ม tokens ตาม 2.3
  (ไม่มี identifier ต้องห้ามเหลือใน `src/**.cs` + `tests/**.cs`).

## REQ-3: Per-module store ports

**User Story:** As a module owner, I want each module to publish its own typed store
port, so that no generic machinery couples the four modules together.

**Acceptance Criteria (EARS):**
- 3.1 THE SYSTEM SHALL ประกาศใน `X.Application` ของแต่ละโมดูล: `record XItem(Guid Id,
  string Code, string Name, bool IsActive)` และ `interface IXStore` ที่มี
  `ListAsync(page, limit, search, ct)`, `CreateAsync(code, name, ct)`,
  `UpdateAsync(id, name, isActive, ct)`.
- 3.2 THE SYSTEM SHALL ให้ `CreateAsync(code, name)` เป็นผู้สร้าง entity ภายใน store
  (host ไม่ต้องอ้าง Domain type ในการ create).
- 3.3 THE SYSTEM SHALL implement store ทั้ง 4 ใน `Persistence.ControlPlane` (โฟลเดอร์ต่อ
  โมดูล) โดย commit ผ่าน keyed `"admin"` `IUnitOfWork` เหมือนเดิม.
- 3.4 THE SYSTEM SHALL คง semantics เดิมของ store: duplicate `Code` -> `ConflictException`
  (409), ไม่พบ id -> `NotFoundException` (404), search escape ด้วย `SfsLike.Escape`.
- 3.5 IF create/update ได้รับ `code`/`name` ที่ผิด invariant THEN THE SYSTEM SHALL โยน
  `ArgumentException` ให้ host แปลงเป็น 400 เหมือนเดิม.
- 3.6 THE SYSTEM SHALL แทนที่ DI registration ของ `IMasterDataStore` ด้วย
  `AddScoped<IXStore>` 4 ตัวใน `ControlPlanePersistenceRegistration`.

## REQ-4: Admins profile lookup redesign

**User Story:** As the Admins module, I want to validate profile FKs without referencing
any reference-data module, so that Admins.Application is fully decoupled.

**Acceptance Criteria (EARS):**
- 4.1 THE SYSTEM SHALL แทน generic `IMasterDataLookup` ด้วย port enum-based ใน
  `Admins.Application`: `enum ProfileField { Position, Office, Level, Division }`,
  `record ProfileRef(Guid Id, string Code, string Name)`, `interface IProfileLookup`
  (`ExistsActiveAsync(field, id, ct)`, `GetRefAsync(field, id, ct)`).
- 4.2 THE SYSTEM SHALL คง validation เดิมของ profile FK ทั้ง 4 (ตรวจ exists + active)
  โดย error message เดิมทุกตัวอักษร.
- 4.3 THE SYSTEM SHALL NOT ให้ `Admins.Domain` และ `Admins.Application` reference project
  ของโมดูลใดเลย (รวมทั้ง 4 โมดูลใหม่).
- 4.4 THE SYSTEM SHALL ให้ `Admins.Infrastructure` reference เฉพาะ `X.Domain` ของ 4 โมดูล
  (เพื่อ FK config `HasOne<X>()` ใน `UserConfigurations.cs`) และ SHALL NOT reference
  `X.Application`/`X.Infrastructure`.
- 4.5 THE SYSTEM SHALL implement `IProfileLookup` ใน
  `Persistence.ControlPlane/Admins/ProfileLookup.cs` query จาก DbSet ทั้ง 4 ตาม enum.
- 4.6 WHEN a create/update-profile request อ้าง FK ที่ไม่มีอยู่จริงหรือ inactive, THE
  SYSTEM SHALL ตอบ 400 เหมือนเดิม.

## REQ-5: Module boundaries

**User Story:** As an architect, I want the boundaries machine-enforced, so that the
split cannot silently erode.

**Acceptance Criteria (EARS):**
- 5.1 THE SYSTEM SHALL NOT ให้ `X.Domain` ของโมดูลใหม่อ้าง EF Core หรือ Infrastructure
  assembly ใดเลย.
- 5.2 THE SYSTEM SHALL NOT ให้โมดูลใหม่ทั้ง 4 reference กันเองในทุก layer.
- 5.3 THE SYSTEM SHALL NOT ให้โมดูลใหม่ทั้ง 4 reference module `Admins` ในทุก layer.
- 5.4 THE SYSTEM SHALL บังคับ 4.3, 4.4, 5.1, 5.2, 5.3 ด้วย Architecture.Tests แบบ
  fail-closed (ผูก assembly name จริงผ่าน `AssertAllResolveToARealAssembly` — ห้าม
  vacuous pass) ในไฟล์ Theory เดียวที่ครอบทั้ง 4 โมดูล แทน `MasterDataArchitectureTests.cs`.

## REQ-6: Behaviour preservation

**User Story:** As an API consumer, I want the split to be invisible on the wire, so that
no client changes.

**Acceptance Criteria (EARS):**
- 6.1 THE SYSTEM SHALL คง endpoint เดิมทั้ง 12: path
  (`/api/v1/admins/{positions|offices|levels|divisions}` + `/{id:guid}` สำหรับ PUT),
  verb (GET list / POST create / PUT update) และ request/response shape เดิม. —
  amended 2026-07-20: ขยายเป็น 20 endpoint (เพิ่ม `GET /{id:guid}` + `DELETE /{id:guid}`
  ต่อ dimension, `DELETE` = soft-deactivate ผ่าน `IsActive`, ไม่ใช่ hard-delete) และย้าย
  path ออกจาก `/api/v1/admins/` ทั้งหมด — แต่ละ dimension กลายเป็น standalone area ของ
  ตัวเอง: `/api/v1/{positions|offices|levels|divisions}` (+ `/{id:guid}`).
- 6.2 THE SYSTEM SHALL คง gate เดิมของทุก endpoint: `.RequireAuthorization("admin")` +
  `.RequirePermission("user.manage")` และ SHALL NOT แตะ iam catalog.
- 6.3 THE SYSTEM SHALL คง operation name เดิม (interpolation string เดิม). OpenAPI tag
  SHALL แยกต่อโมดูล (`"Positions"`/`"Offices"`/`"Levels"`/`"Divisions"`, ไม่มี prefix
  `"Admin "` — เป็น reference list ไม่ใช่ admin-account operation) แทนการรวมเป็น
  `"Admin Master Data"` เดียว — amended 2026-07-20: Scalar UI ต้องสะท้อนการแตกโมดูลจริง
  ไม่ใช่ซ่อนไว้หลัง tag รวม.
- 6.4 THE SYSTEM SHALL คง domain invariant เดิมในทุก entity: `Code` ตรง `^[a-z0-9_]+$`,
  trim, immutable หลังสร้าง; `Rename` แก้ได้แค่ `Name`; `Activate`/`Deactivate` toggle
  `IsActive`.
- 6.5 THE SYSTEM SHALL คง endpoint helper เป็น generic เดียวใน host (delegate-parameterized)
  เพื่อให้ representative-segment pinning ของ `PermissionGateSitesTests` ยังถูกต้อง.
- 6.6 THE SYSTEM SHALL คง write floor: `ControlPlaneAdminWriteAuthorizer.BoundOnlyTypes`
  ยังเป็น entity type ทั้ง 4 (CLR type ใหม่) ไม่ขาดไม่เกิน.

## REQ-7: EF model & migrations

**User Story:** As an operator, I want the DB untouched, so that no data or DDL changes
ship with a code-only restructure.

**Acceptance Criteria (EARS):**
- 7.1 THE SYSTEM SHALL map entity ทั้ง 4 แบบ standalone (ไม่มี TPC base) ลงตารางเดิมใน
  schema `cfg` ผ่าน configuration ใน `X.Infrastructure` ที่อ้าง `SchemaNames.Cfg`.
- 7.2 THE SYSTEM SHALL NOT แก้ไข body `Up()`/`Down()` ของ migration ทั้ง 4 ไฟล์ และ
  SHALL NOT เพิ่ม migration ใหม่.
- 7.3 THE SYSTEM SHALL อัปเดต `.Designer.cs` ทั้ง 4 + `PolDbContextModelSnapshot.cs` ให้
  ตรง model ใหม่ จน `dotnet ef` ไม่รายงาน pending model changes
  (`Hosts.Tests.ModelConsistencyTests` เขียว).
- 7.4 THE SYSTEM SHALL พิสูจน์ว่า DDL ไม่เปลี่ยน ด้วย temp migration ที่ `Up()`/`Down()`
  ว่างเปล่า (สร้างเพื่อตรวจแล้วลบทิ้ง — machine proof บันทึกเป็น evidence ใน tasks.md).
- 7.5 WHEN `dotnet ef database update` รันบน DB เปล่า, THE SYSTEM SHALL ผ่าน
  `docker/bootstrap/assert-fresh-db.sql` โดยไม่แก้ไฟล์ assert (ตาราง + seed counts
  12/8/10/10 + GUID เดิม).
- 7.6 THE SYSTEM SHALL ผ่าน `scripts/check-migration-lineage.sh` (migration ID เดิมครบ 4).
- 7.7 THE SYSTEM SHALL map entity ใหม่ทั้ง 4 ใน `ControlPlaneDbContext` ให้
  `ModelDisjointnessTests` ผ่าน (ทุก entity ของ `PolDbContext` อยู่ใน runtime context
  เดียวพอดี ไม่มี unassigned/phantom).

## REQ-8: Tests

**User Story:** As a reviewer, I want the split fully covered by the existing test
tiers, so that regressions surface at the cheapest gate.

**Acceptance Criteria (EARS):**
- 8.1 THE SYSTEM SHALL มี test project ต่อโมดูลใหม่ทั้ง 4 (`Divisions.Tests` ฯลฯ)
  ครอบ domain invariant ตาม 6.4 (slug reject / trim / rename / toggle).
- 8.2 THE SYSTEM SHALL แทน `FakeMasterDataStore` ใน `Admins.Tests` ด้วย fake ของ
  `IProfileLookup` และ suite `Admins.Tests` ทั้งหมดยังเขียว.
- 8.3 THE SYSTEM SHALL อัปเดต hardcoded module-assembly list ใน
  `EntitySchemaMappingTests`, `ModelDisjointnessTests`, `RawConnectionTests`
  (MasterData.Infrastructure ออก, 4 assembly ใหม่เข้า).
- 8.4 THE SYSTEM SHALL อัปเดต `TransactionInventoryTests` จาก 1 path (2 sites) เป็น
  4 path (path ละ 2 sites) ตาม store ใหม่.
- 8.5 THE SYSTEM SHALL ผ่าน `dotnet build pol-core.slnx -warnaserror` และ
  `dotnet test --filter "Category!=Integration"` ทั้ง solution.
- 8.6 THE SYSTEM SHALL ผ่าน integration suite (`--filter "Category=Integration"`) บน DB
  ที่ migrate จริง.

## REQ-9: Canon & docs

**User Story:** As a future contributor, I want canon to reflect the new structure, so
that docs never contradict the code.

**Acceptance Criteria (EARS):**
- 9.1 THE SYSTEM SHALL เพิ่ม dated bullet ใน `.ai/shared/ARCHITECTURE.md` ว่า MasterData
  ถูกแยกเป็น 4 โมดูลและลบทิ้ง (supersede ข้อความเดิม — ไม่ลบประวัติ).
- 9.2 THE SYSTEM SHALL อัปเดต `docs/reference/platform-modules.md` row ของ MasterData
  เป็น 4 โมดูลใหม่.
- 9.3 THE SYSTEM SHALL แก้ bullet ใน `.ai/shared/stack/dotnet.md` ที่ endorse generic
  `IMasterDataStore`/`Set<T>()` shape ให้ตรงสภาพใหม่ (per-module typed store).
- 9.4 THE SYSTEM SHALL อัปเดต XML doc ของ `SchemaNames.Cfg` ให้ระบุผู้อยู่อาศัยเป็น 4
  โมดูลใหม่.
- 9.5 THE SYSTEM SHALL อัปเดต transaction inventory ใน
  `.ai/specs/rls-to-query-filter/design.md` (แถว store path เดิม -> 4 path ใหม่) ให้ตรง
  `TransactionInventoryTests`.

## Edge Cases & Open Questions

- **Temp migration ไม่ว่าง (7.4)**: แปลว่า config ใหม่ตกหล่น facet (เช่น
  `HasMaxLength(64)`, unique index) — hard stop, แก้ config, ห้าม ship diff.
- **Atomic cutover**: `ModelDisjointnessTests` เทียบด้วย CLR type — runtime cutover กับ
  design-time cutover ต้องจบใน task เดียว (state กลางทาง red ทั้ง unassigned + phantom).
- **XML doc `<see cref=` ค้าง**: type เก่าหายแล้ว cref ค้าง = CS1574 = build error
  (`-warnaserror`) — ต้องกวาดพร้อม cutover.
- **Designer surgery**: designer ตัวที่ 4 (RlsTeardown) ต่างจาก 1-3 นอก MasterData block —
  ห้าม bulk-copy model เดียวทับทุกไฟล์; ใช้ per-file block replacement + assert
  occurrence เก่า -> 0.
- ไม่มี open question ค้าง — ทุก decision ถูก user เคาะแล้ว (2026-07-19) ตาม approved plan.
