# Tasks — masterdata-module

> Status: approved 2026-07-13 (quick, no gates)

3 task ตามลำดับ dependency: T1 (code move) -> T2 (DB) -> T3 (guard + canon).
แต่ละ task จบด้วย build เขียว + test เขียว.

---

## - [x] T1 — สร้างโมดูล MasterData + ย้ายโค้ดออกจาก Admins + ต่อ seam ใหม่

Satisfies: 1.1, 1.2, 1.3, 1.4, 2.1, 2.2, 2.3, 2.4, 3.1, 3.2, 4.1, 4.2, 4.3, 4.4, 5.1, 5.2, 5.3, 5.4, 7.2

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

## - [x] T2 — ย้ายตารางไป schema `cfg` (migration + grant + seed)

Satisfies: 3.3, 3.4, 3.5, 3.6, 5.5, 6.1, 6.2, 6.3
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

### Evidence (2026-07-13)

Hand-edited the 3 existing migration files in place (no new migration) — chosen over
`dotnet ef migrations add` because the hand-written `migrationBuilder.Sql` blocks in
`SecurityObjects.cs` (RLS functions/procs/policy/grants) are NOT part of the EF model and would be
silently lost by a wholesale regenerate; every edit below is a scoped, mechanical substitution
(schema string / CLR type-name string), applied via a verified Python find-and-assert script for
the repetitive Designer/Snapshot files and via `Edit` for the hand-written SQL:

- `20260712185344_InitialSchema.cs`: added `EnsureSchema("cfg")`; `admin` -> `cfg` on the 4
  `CreateTable` calls (Divisions/Levels/Offices/Positions), the 4 FK `principalSchema` on the
  `Users` table, the 4 `CreateIndex` schema args, and the 4 `Down()` `DropTable` schema args.
- `20260712185344_InitialSchema.Designer.cs`, `20260712185646_SecurityObjects.Designer.cs` (this
  file DOES have a Designer.cs — the original T1 handoff note only checked 3 files, missed this
  one; caught by a full-repo grep before declaring done, fixed the same way),
  `20260712185912_SeedData.Designer.cs`, `PolDbContextModelSnapshot.cs`: renamed the CLR type
  strings `"Admins.Domain.MasterData.{MasterDataItem,Division,Level,Office,Position}"` ->
  `"MasterData.Domain.{MasterDataItem,Divisions.Division,Levels.Level,Offices.Office,Positions.Position}"`
  (13 occurrences each file, matching T1's actual namespaces) and `b.ToTable("<Table>", "admin")`
  -> `b.ToTable("<Table>", "cfg")` for the 4 tables, in all 4 files.
- `20260712185646_SecurityObjects.cs`: added `ALTER AUTHORIZATION ON SCHEMA::cfg TO dbo;` next to
  the other schema re-asserts (Up); moved the 4 GRANT (`SELECT, INSERT, UPDATE`) lines from
  `admin.{Positions,Offices,Levels,Divisions}` to `cfg.*`, same principal `pol_admin`, unchanged
  verb set; mirrored the 4 REVOKE lines in `Down()`. `pol_app` was never granted anything on these
  tables before or after — untouched.
- `20260712185912_SeedData.cs`: `INSERT INTO admin.{table}` -> `INSERT INTO cfg.{table}` (Up, 4x)
  and `DELETE FROM admin.{table}` -> `DELETE FROM cfg.{table}` (Down, 4x) — GUIDs untouched.

Verify, on a real fresh DB (`.env` `POL_DESIGN_SQL`/`MSSQL_SA_PASSWORD`, container `pol-db` on
`localhost:11433`):

```
$ docker compose down -v && docker compose up -d
 ... Container pol-db Healthy / pol-core-pol-db-init-1 Started
$ docker compose logs pol-db-init
pol-db-init-1  | Changed database context to 'VCentralPay'.

$ POL_DESIGN_SQL=... dotnet ef database update --context PolDbContext \
    --project src/BuildingBlocks/BuildingBlocks.Infrastructure --startup-project src/Hosts/Api
Applying migration '20260712185344_InitialSchema'.
Applying migration '20260712185646_SecurityObjects'.
Applying migration '20260712185912_SeedData'.
Done.

$ POL_DESIGN_SQL=... dotnet ef migrations has-pending-model-changes --context PolDbContext \
    --project src/BuildingBlocks/BuildingBlocks.Infrastructure --startup-project src/Hosts/Api
No changes have been made to the model since the last migration.
```

Real DB queries (sqlcmd against `VCentralPay` on `pol-db`):

```sql
-- (a) table locations
SELECT s.name, t.name FROM sys.tables t JOIN sys.schemas s ON t.schema_id=s.schema_id
WHERE t.name IN ('Positions','Offices','Levels','Divisions');
-- cfg.Divisions, cfg.Levels, cfg.Offices, cfg.Positions (4 rows) — zero rows left under admin

-- (b) seed counts
Positions=12, Offices=8, Levels=10, Divisions=10  -- matches T1's pre-move counts exactly

-- (c) grants on cfg.* (sys.database_permissions)
pol_admin: SELECT+INSERT+UPDATE on all 4 tables (12 rows total), nothing else
pol_app grants on cfg.* -> COUNT(*) = 0

-- (d) FKs
FK_Users_Divisions_DivisionId  admin.Users -> cfg.Divisions
FK_Users_Levels_LevelId        admin.Users -> cfg.Levels
FK_Users_Offices_OfficeId      admin.Users -> cfg.Offices
FK_Users_Positions_PositionId  admin.Users -> cfg.Positions

-- schema ownership
cfg owner = dbo
```

Build + tests:
- `dotnet build pol-core.slnx` -> **0 errors, 0 warnings** (48 projects).
- `dotnet test tests/Hosts.Tests` -> **228/228 passed** (the one red test from T1,
  `ModelConsistencyTests.Model_has_no_pending_changes_against_the_migration_snapshot`, is now
  green).
- `dotnet test tests/Integration.Tests` (with `.env.integration` sourced in the same shell call)
  -> **93/93 passed**.
- `dotnet test tests/Admins.Tests` -> **95/95 passed** (unaffected — `MasterDataLookup`'s
  `_db.Set<T>()` queries resolve the new schema transparently through the EF model, no code
  change needed here).

### Handoff (for T3)

- No new migration file — still exactly 3: `InitialSchema`/`SecurityObjects`/`SeedData`, same
  timestamps as before T1/T2.
- `cfg` schema is now live in the DB with the 4 master tables, owned by `dbo`, no RLS policy,
  grants exactly `SELECT, INSERT, UPDATE` to `pol_admin` only — this is the "cfg ใช้จริงแล้ว" fact
  T3's `.ai/shared/ARCHITECTURE.md` update (REQ-7.1) should record.
- `Admins.Domain.MasterData.*` no longer appears anywhere in the repo (migrations included) —
  confirmed via `grep -rn "Admins\.Domain\.MasterData" src/ tests/` returning nothing. T3's
  `MasterDataArchitectureTests.cs` can assert this as a fail-closed pin without any known
  exceptions to carve out.
- The DB container was reset (`docker compose down -v && up -d`) and re-migrated during this
  task's verification — it is currently on the new `cfg`-schema state. T3 does not need to touch
  the DB (its scope is Architecture.Tests + docs only) but should be aware the local dev DB is
  already on this schema if it runs anything DB-backed.
- One correction to T1's handoff note: `20260712185646_SecurityObjects.Designer.cs` DOES exist
  (T1's file listing missed it) and needed the same CLR-type/schema edit as the other two
  Designer files — worth double-checking file listings with `ls`, not a truncated `grep`, before
  trusting a "3 files" count on future migration surgery.

---

## - [x] T3 — Architecture guard + canon

Satisfies: 4.5, 7.1
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

### Evidence (2026-07-13)

- New file `tests/Architecture.Tests/MasterDataArchitectureTests.cs` — 5 tests: fail-closed assembly-name
  pin (`MasterData_layer_keys_match_their_real_assembly_names`), `MasterData.Domain` vs EF Core, `MasterData.Domain`
  vs any `*.Infrastructure`, `MasterData.Domain`+`.Application` vs `Admins.*` (REQ-4.2), and
  `Admins.Domain`+`.Application` vs `MasterData.Application`/`.Infrastructure` (REQ-4.1 — `MasterData.Domain` stays
  allowed, the published-language seam `Admins.Application.csproj` already declares).
- **Red/green guard proof** — temporarily added a violating `MasterData.Application` project reference to
  `Admins.Application.csproj` + a `using MasterData.Application;` and a dummy `IMasterDataStore?` member on
  `IMasterDataLookup` in `Admins.Application/Users/MasterDataLookup.cs`:
  - RED: `dotnet test tests/Architecture.Tests --filter FullyQualifiedName~MasterDataArchitectureTests` ->
    `Failed: 1, Passed: 4, Total: 5` — the one failure:
    `Admins_Domain_and_Application_reference_only_MasterData_Domain_not_Application_or_Infrastructure`,
    message `Admins.Application may reference only MasterData.Domain, not MasterData.Application/Infrastructure.
    Offenders: Admins.Application.Users.IMasterDataLookup`.
  - Reverted both files (`git diff --stat` on them came back empty, confirming exact revert to the T1 state).
  - GREEN: `dotnet build pol-core.slnx` -> 0 errors; `dotnet test tests/Architecture.Tests` ->
    `Passed: 68, Failed: 0, Total: 68` (63 pre-existing + these 5 new).
- `.ai/shared/ARCHITECTURE.md` — added a new bullet (next to the rf2 IAM-catalog bullet, same style) recording:
  MasterData is now its own module (3-project shape like `Iam`, `Admins.Application` may reference only
  `MasterData.Domain`), and schema `cfg` is live — first occupant = MasterData's 4 reference tables (outside RLS,
  `pol_admin`-only grants), rf3 will add payment config to the same schema. Checked first: `ARCHITECTURE.md` had
  ZERO prior mentions of MasterData/`cfg`/Position-Office-Level-Division (grep confirmed) — the "v5 line saying
  master data lives in schema `admin`" the team lead flagged lives in `.ai/specs/rf1-schema-reset/design.md`
  (a past spec's point-in-time data-model table, line 112), not in the canon file; left that historical spec
  artifact untouched (specs are not rewritten after the fact) and only added the new canon fact to
  `ARCHITECTURE.md`, which had nothing to contradict.
- `docs/reference/platform-modules.md` — added row `15 | MasterData | ...` to `## ตารางสรุป`, same 5-column
  shape as every other row (`#`/`โมดูล`/`บทบาทหนึ่งบรรทัด`/`สถานะ`/`อ้างอิงลึก`), linking to `ARCHITECTURE.md`
  since there's no module-specific deep doc for MasterData yet. Did not touch the rest of this doc (explicitly
  marked stale/pre-rf1 at the top, full rewrite is out-of-scope future work, not this task) — `Iam`/`Merchants`
  don't have rows here either; adding one more incremental row for MasterData is consistent with existing
  incremental notes (e.g. row 4.2 already carries an inline rf2 note without a full rewrite).
- `bash scripts/spec-trace.sh masterdata-module` -> exit 0, prints "requirements.md ... ไม่ใช่รูปแบบ REQ-based
  (ไม่มีหัวข้อ '## REQ-N:') — ข้ามการตรวจ traceability". This is the script's own documented skip path
  (`scripts/spec_trace.py` requires the exact `## REQ-N:` H2-with-colon heading; this spec's requirements.md
  uses `### REQ-N — Title`, H3 with em-dash, written that way by `/spec-quick` before T3 started) — not a T3
  regression, exit code is 0 either way.
- Full non-DB suite re-run, all green, no regressions vs T2's baseline: `Architecture.Tests` 68/68 (was 63),
  `BuildingBlocks.Tests` 65/65, `Admins.Tests` 95/95, `Iam.Tests` 66/66, `Merchants.Tests` 114/114,
  `Products.Tests` 25/25, `Carts.Tests` 15/15, `Checkouts.Tests` 2/2, `Orders.Tests` 25/25, `Payments.Tests` 59/59,
  `SharedKernel.Tests` 46/46, `Hosts.Tests` 228/228.
- `dotnet build pol-core.slnx` -> **0 errors, 0 warnings** (48 projects).
- `git diff --stat` (final, after revert) touches exactly the 3 files in scope:
  `.ai/shared/ARCHITECTURE.md` (+8), `docs/reference/platform-modules.md` (+1), plus the new
  `tests/Architecture.Tests/MasterDataArchitectureTests.cs`.
