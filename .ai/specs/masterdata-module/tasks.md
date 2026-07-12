# Tasks — masterdata-module

> Status: approved 2026-07-13 (quick, no gates)

3 task ตามลำดับ dependency: T1 (code move) -> T2 (DB) -> T3 (guard + canon).
แต่ละ task จบด้วย build เขียว + test เขียว.

---

## - [x] T1 — สร้างโมดูล MasterData + ย้ายโค้ดออกจาก Admins + ต่อ seam ใหม่

REQ: 1.1, 1.2, 1.3, 1.4, 2.1, 2.2, 2.3, 2.4, 3.1, 3.2, 4.1, 4.2, 4.3, 4.4, 5.1, 5.2, 5.3, 5.4, 7.2

Scope (design §1, §2, §3, §5):
1. สร้าง 3 project ใต้ `src/Modules/MasterData/` (ลอก csproj จาก `Iam.*`) + เพิ่มเข้า `pol-core.slnx`
   + `Api.csproj` + `Worker.csproj`
2. ย้าย domain: `MasterDataItem.cs` (module root) + `Positions/Position.cs`, `Offices/Office.cs`,
   `Levels/Level.cs`, `Divisions/Division.cs` — namespace `MasterData.Domain[.<Plural>]`, type เอกพจน์เดิม
3. ย้าย `MasterItem` + `IMasterDataStore` (เหลือ **List/Create/Update**) ไป `MasterData.Application`;
   ย้าย EF configuration + store impl ไป `MasterData.Infrastructure` (`ToTable(..., SchemaNames.Cfg)`);
   เพิ่ม `SchemaNames.Cfg = "cfg"` + XML doc (REQ-7.2); เพิ่ม `MasterDataModuleRegistration`
4. seam ใหม่ใน Admins: port `IMasterDataLookup` (`ExistsActiveAsync<T>`/`GetRefAsync<T>`) + `MasterRef`
   + `MasterProfileValidation` ไปอยู่ `Admins.Application/Users/`; impl ใน
   `Admins.Infrastructure/Persistence/Users/` (query `_db.Set<T>()` ตรง). `Admins.Application.csproj`
   อ้าง `MasterData.Domain` เท่านั้น
5. ลบ `MasterData/` folder ทั้ง 3 ชั้นใน Admins ให้หมด
6. host wiring: `HostModuleAssemblies.All` เพิ่ม MasterData.Infrastructure; `HostWiring.cs` register
   store ผ่าน module registration + register lookup; `Program.cs` แก้แค่ `using` — route/perm/response
   ห้ามเปลี่ยนแม้แต่บรรทัดเดียว
7. อัปเดต `using` ใน `tests/Admins.Tests/MasterDataAndProfileTests.cs`

Verify:
- `dotnet build` เขียว 0 error
- `dotnet test tests/Admins.Tests` เขียว
- `grep -rn "Admins.*MasterData" src/` เหลือศูนย์ (ไม่นับ comment อ้างประวัติ)

### Evidence (2026-07-13)

- `dotnet build pol-core.slnx` → **Build succeeded. 0 Warning(s), 0 Error(s)** (47 projects incl. the 3 new
  MasterData.* + all touched Admins.*/Api/test projects).
- `dotnet test tests/Admins.Tests/Admins.Tests.csproj` → **Passed! Failed: 0, Passed: 95, Skipped: 0, Total: 95**.
- Full non-DB suite also run (not required by scope, run anyway to catch collateral breakage):
  `Architecture.Tests` 63/63 green (had to add `MasterData.Infrastructure` to the pre-existing
  `EntitySchemaMappingTests` fixed `ModuleAssemblies` list + `SchemaNames.Cfg` to its allow-set — that test's own
  hardcoded module list is independent of `HostModuleAssemblies.All`, so it silently drifted null-schema on
  Position/Office/Level/Division the moment their EF config moved out of `Admins.Infrastructure`; this is a
  1-file, 3-line fix to an EXISTING test, not the new `MasterDataArchitectureTests.cs` T3 owns).
  `BuildingBlocks.Tests` 65/65, `Iam.Tests` 66/66, `Merchants.Tests` 114/114, `Products.Tests` 25/25,
  `Carts.Tests` 15/15, `Checkouts.Tests` 2/2, `Orders.Tests` 25/25, `Payments.Tests` 59/59,
  `SharedKernel.Tests` 46/46 — all green.
  `Hosts.Tests` **227/228** — the ONE failure is
  `Hosts.Tests.ModelConsistencyTests.Model_has_no_pending_changes_against_the_migration_snapshot`, and it is
  the EXACT, expected T1→T2 seam described above ("EF model ต้องชี้ cfg ก่อน ไม่งั้น snapshot ไม่ match"):
  `MasterDataConfigurations.cs` now calls `ToTable(..., SchemaNames.Cfg)` but the 3 migration files + snapshot
  still say `admin` — T2 regenerates the migration chain to close this. Did NOT touch any migration file
  (out of T1 scope) to make this pass.
- `grep -rn "Admins\.\(Domain\|Application\|Infrastructure\)\.MasterData" src/ tests/` → **NOT literally zero**:
  46 hits, ALL inside `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/*.cs`
  (`PolDbContextModelSnapshot.cs` + the 3 `*.Designer.cs` files) — every hit is an EF-generated CLR-type-name
  STRING LITERAL (`modelBuilder.Entity("Admins.Domain.MasterData.Position", b => ...)`), not a C# `using`/type
  reference. Zero hits in any non-migration `src/`/`tests/` file (confirmed separately). This is the same T1→T2
  seam as above — T2's migration-file edit is exactly what rewrites these strings to `MasterData.Domain.*`. Not
  a defect; flagging because the literal grep the team lead specified does not return 0 today by design.

### Handoff (for T2)

Files created (`src/Modules/MasterData/`): `MasterData.Domain/{MasterDataItem.cs, Positions/Position.cs,
Offices/Office.cs, Levels/Level.cs, Divisions/Division.cs, MasterData.Domain.csproj}`,
`MasterData.Application/{MasterDataStore.cs (MasterItem + IMasterDataStore, CRUD only),
MasterData.Application.csproj}`, `MasterData.Infrastructure/{Persistence/MasterDataConfigurations.cs (ToTable
targets `SchemaNames.Cfg` — model now says `cfg`, DB/migrations still say `admin`), Persistence/MasterDataStore.cs,
MasterDataModuleRegistration.cs (`AddMasterDataModule(services, Func<IServiceProvider,PolDbContext>)`, hardcodes
the keyed `"admin"` IUnitOfWork), MasterData.Infrastructure.csproj}`.

New in Admins (the split-off lookup port, design.md §1): `Admins.Application/Users/MasterDataLookup.cs`
(`IMasterDataLookup` + `MasterRef` + `MasterProfileValidation.ValidateProfileFksAsync`, ExistsActiveAsync/GetRefAsync
only) and `Admins.Infrastructure/Persistence/Users/MasterDataLookup.cs` (impl, queries `_db.Set<T>()` directly
with `MasterData.Domain` types — same pattern as `RoleRepository` on `iam.Roles`).

Deleted (git-mv'd, not raw `rm`): `Admins.Domain/MasterData/`, `Admins.Application/MasterData/`,
`Admins.Infrastructure/Persistence/MasterData/` — all 3 gone, verified by the grep above (non-migration files
only).

Exact `ToTable` call sites now pointing at `cfg` (T2's migration edit must match these table names verbatim):
`MasterData.Infrastructure/Persistence/MasterDataConfigurations.cs` → `Positions`, `Offices`, `Levels`,
`Divisions`, all via `builder.ToTable("<Name>", SchemaNames.Cfg)`. `SchemaNames.Cfg = "cfg"` lives in
`BuildingBlocks.Infrastructure/Persistence/SchemaNames.cs` (added, with XML doc per REQ-7.2).

Wiring: `src/Hosts/Api/Admins/HostWiring.cs` now calls `services.AddMasterDataModule(Admin)` (replaces the old
inline `IMasterDataStore` registration) + `services.AddScoped<IMasterDataLookup>(sp => new
MasterDataLookup(Admin(sp)))`. `src/Hosts/Api/DesignTimeDbContextFactories.cs`'s `HostModuleAssemblies.All` gained
`MasterData.Infrastructure` — this is what `dotnet ef` will build its model against for T2. Worker was
DELIBERATELY left untouched: `WorkerModuleAssemblies.All` never included `Admins.Infrastructure` either (Worker
never maps `admin.*`/now-`cfg.*` tables at all), so adding MasterData there would be scope creep, not parity.

EF snapshot is now out of sync (expected, see Evidence) — confirmed via both
`Hosts.Tests.ModelConsistencyTests` (red) and the migration-file grep (46 string-literal hits). T2 starts from:
regenerate the 3 migration files + snapshot so `EnsureSchema("cfg")`, the 4 `CreateTable(..., schema: "cfg")`,
the FK `principalSchema: "cfg"`, the GRANT/REVOKE block, and the seed `InsertData(schema: "cfg", ...)` all land —
then both the ModelConsistency test and the migration grep go green together.

---

## - [ ] T2 — ย้ายตารางไป schema `cfg` (migration + grant + seed)

REQ: 3.3, 3.4, 3.5, 3.6, 5.5, 6.1, 6.2, 6.3
ต้องรอ T1 (EF model ต้องชี้ `cfg` ก่อน ไม่งั้น snapshot ไม่ match)

Scope (design §4): แก้ migration 3 ไฟล์เดิมในที่ (+ Designer + snapshot) — `EnsureSchema("cfg")`,
`CreateTable(schema:"cfg")` x4, FK `principalSchema:"cfg"`, `ALTER AUTHORIZATION ON SCHEMA::cfg TO dbo`,
ย้าย GRANT/REVOKE 4 บรรทัดจาก `admin.*` -> `cfg.*`, `InsertData(schema:"cfg")` (GUID เดิม).
ห้ามเพิ่ม migration ใหม่. `cfg` ไม่มี RLS policy.

Verify (บน DB เปล่าจริง — build อย่างเดียวไม่พอ):
- `docker compose down -v && docker compose up -d` + bootstrap + `dotnet ef database update` ผ่าน
- `dotnet ef migrations has-pending-model-changes` -> ไม่มี
- query จริง: 4 ตารางอยู่ `cfg`, ไม่มีตาราง master เหลือใน `admin`, seed ครบ, `pol_admin` มี
  SELECT/INSERT/UPDATE บน `cfg.*` และ `pol_app` ไม่มีสิทธิ์ใด
- integration test suite เขียว

---

## - [ ] T3 — Architecture guard + canon

REQ: 4.5, 7.1
ต้องรอ T1 (assembly ต้องมีจริงก่อน)

Scope:
1. `tests/Architecture.Tests/MasterDataArchitectureTests.cs` — assert: `MasterData.Domain`/`.Application`
   ไม่ขึ้นกับ `Admins.*`; `MasterData.Domain` ไม่ขึ้นกับ EF Core หรือ `*.Infrastructure`;
   `Admins.Domain`/`.Application` ไม่ขึ้นกับ `MasterData.Application`/`MasterData.Infrastructure`;
   + fail-closed pin (assembly name จริง เหมือน `Module_key_matches_its_real_assembly_names`)
2. `.ai/shared/ARCHITECTURE.md` — บันทึก schema `cfg` ใช้จริงแล้ว (ผู้ใช้แรก = MasterData, rf3 มาต่อ)
   + MasterData เป็นโมดูลแยก (ไม่อยู่ใต้ Admins แล้ว)
3. `docs/reference/platform-modules.md` — เพิ่ม MasterData ในแผนที่โมดูล

Verify:
- `dotnet test tests/Architecture.Tests` เขียว (test ใหม่ต้องเห็น fail ถ้าลองเพิ่ม ref ผิดทิศจริง)
- `scripts/spec-trace.sh masterdata-module` ผ่าน
