# Tasks — masterdata-module

> Status: approved 2026-07-13 (quick, no gates)

3 task ตามลำดับ dependency: T1 (code move) -> T2 (DB) -> T3 (guard + canon).
แต่ละ task จบด้วย build เขียว + test เขียว.

---

## - [ ] T1 — สร้างโมดูล MasterData + ย้ายโค้ดออกจาก Admins + ต่อ seam ใหม่

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
