# Design — masterdata-module

> Status: approved 2026-07-13 (quick, no gates)

## 1. Shape

โมดูลใหม่ลอกทรงจาก `Iam` (rf2) ตรง ๆ — 3 project, ไม่มี Mediator handler (reference data ธรรมดา
เหมือนเดิม), entity configuration ถูก discover ผ่าน `ModuleAssemblies` โดย `PolDbContext`.

```
src/Modules/MasterData/
  MasterData.Domain/            -> SharedKernel เท่านั้น
    MasterDataItem.cs                    abstract base (module root — L2/L4 stop)
    Positions/Position.cs                sealed class Position
    Offices/Office.cs                    sealed class Office
    Levels/Level.cs                      sealed class Level
    Divisions/Division.cs                sealed class Division
  MasterData.Application/       -> MasterData.Domain + BuildingBlocks.Application
    MasterDataStore.cs                   record MasterItem + interface IMasterDataStore (List/Create/Update)
  MasterData.Infrastructure/    -> MasterData.Application + BuildingBlocks.Infrastructure
    Persistence/MasterDataConfigurations.cs   ToTable(..., SchemaNames.Cfg), TPC
    Persistence/MasterDataStore.cs            EF impl (keyed "admin" IUnitOfWork — ไม่เปลี่ยน)
    MasterDataModuleRegistration.cs           AddMasterDataModule(services, Func<IServiceProvider, PolDbContext>)
```

**การตัดสินที่สำคัญ:** `ExistsActiveAsync` / `GetRefAsync` / `MasterRef` / `ValidateProfileFksAsync`
**ไม่ตามไปอยู่ MasterData** — มันคือความต้องการของ `Admins` (validate FK บนโปรไฟล์ `User`) ไม่ใช่
use case ของ MasterData. ตาม hexagonal + precedent rf2 (`Admins.Infrastructure` query `iam.Roles`
ตรงด้วย type ของ `Iam.Domain`) จึงย้ายไปเป็น port ของผู้เรียก:

| ของเดิม | ที่อยู่ใหม่ |
|---------|------------|
| `IMasterDataStore.ListAsync/CreateAsync/UpdateAsync` + `MasterItem` | `MasterData.Application` (module CRUD — ใช้โดย endpoint) |
| `IMasterDataStore.ExistsActiveAsync/GetRefAsync` + `MasterRef` + `MasterProfileValidation` | `Admins.Application/Users/MasterDataLookup.cs` — interface `IMasterDataLookup` (port ของ Admins) |
| impl ของสองเมธอดนั้น | `Admins.Infrastructure/Persistence/Users/MasterDataLookup.cs` — query `_db.Set<T>()` ตรง (T จาก `MasterData.Domain`) |

ผลลัพธ์: `Admins.Application` -> `MasterData.Domain` เท่านั้น (published language) ✔ REQ-4.1;
`MasterData.*` ไม่รู้จัก `Admins` เลย ✔ REQ-4.2.

## 2. Naming (L1-L8)

- L3: folder/namespace พหูพจน์ (`Positions/`), type เอกพจน์ (`Position`) — REQ-2.1/2.2
- L2: `MasterDataItem` เป็น base ของ 4 aggregate root -> อยู่ module-root namespace `MasterData.Domain`
- L4 stop: ไม่ตัดเหลือ `Item` (กำกวมกับ `CartItem`/`OrderItem`) — REQ-2.3
- L7: ชื่อตารางคงเดิม (พหูพจน์แล้ว) เปลี่ยนแค่ schema — REQ-2.4
- L8: ไม่แตะ config key / security-scheme id / permission key / route ใด ๆ — REQ-5.1/5.2

## 3. Schema `cfg`

`SchemaNames.Cfg = "cfg"` (BuildingBlocks.Infrastructure/Persistence/SchemaNames.cs) — ผู้ใช้แรกคือ
MasterData; rf3 จะเติม Provider/RoutingRule/GatewayConfig/FeeStructure ใน schema เดียวกัน.
`cfg` อยู่นอก RLS (control-plane reference data เหมือน `iam`) — ไม่มี security policy, ไม่มี
`ITenantScoped`.

Grant (คัดลอกสิทธิ์เดิมของ `admin.*` แบบตรงตัว — REQ-3.4):

```sql
GRANT SELECT, INSERT, UPDATE ON cfg.Positions TO pol_admin;   -- + Offices/Levels/Divisions
-- pol_app: ไม่มีสิทธิ์ใดบน cfg.* (funnel ไม่เคยอ่าน master data)
```

FK ข้าม schema: `admin.PlatformUsers.PositionId -> cfg.Positions.Id` (Restrict) — ทำได้ปกติ,
precedent = `admin.RoleAssignments -> iam.Roles`.

## 4. Migrations (แก้ 3 ไฟล์เดิมในที่ — big-bang, REQ-6.1)

| ไฟล์ | สิ่งที่แก้ |
|------|-----------|
| `20260712185344_InitialSchema.cs` (+ Designer) | `EnsureSchema("cfg")`; 4 `CreateTable(..., schema: "cfg")`; FK ของ PlatformUsers ชี้ `principalSchema: "cfg"` |
| `20260712185646_SecurityObjects.cs` | เพิ่ม `ALTER AUTHORIZATION ON SCHEMA::cfg TO dbo;`; ย้าย 4 GRANT (และ 4 REVOKE ใน `Down`) จาก `admin.*` เป็น `cfg.*` |
| `20260712185912_SeedData.cs` (+ Designer) | `InsertData(schema: "cfg", ...)` สำหรับ HR seed 4 ชุด (GUID เดิม — REQ-5.5) |
| `PolDbContextModelSnapshot.cs` | regen ให้ตรง model (REQ-6.3) |

Verify = fresh DB (`docker compose down -v` + bootstrap + `dotnet ef database update`) ไม่ใช่แค่ build
(บทเรียนจาก PR #68: rename schema ใน snapshot/designer หลุดได้ถ้าไม่ลองบน DB เปล่าจริง).

## 5. Host wiring

- `src/Hosts/Api/DesignTimeDbContextFactories.cs` -> `HostModuleAssemblies.All` เพิ่ม
  `typeof(MasterDataStore).Assembly` (และ Worker ถ้ามี list ของตัวเอง) — REQ-1.4
- `src/Hosts/Api/Admins/HostWiring.cs` -> เดิม register `IMasterDataStore` ตรง; เปลี่ยนเป็นเรียก
  `AddMasterDataModule(...)` + register `IMasterDataLookup` (impl ใหม่ใน Admins.Infrastructure)
- `src/Hosts/Api/Program.cs` -> เปลี่ยนแค่ `using` (MasterData.Domain.Positions ฯลฯ) — `MapMasterCrud`,
  segment, `RequirePermission(Keys.UserManage)`, response shape คงเดิมทุกบรรทัด (REQ-5.1/5.2)
- `Api.csproj` / `Worker.csproj` / `pol-core.slnx` -> เพิ่ม 3 project

## 6. Requirement Traceability

| REQ | ไฟล์ / จุดที่รับผิดชอบ |
|-----|------------------------|
| REQ-1.1 | `src/Modules/MasterData/{MasterData.Domain,MasterData.Application,MasterData.Infrastructure}/*.csproj` |
| REQ-1.2 | ลบ `src/Modules/Admins/*/MasterData/` ทั้ง 3 ชั้น |
| REQ-1.3 | `pol-core.slnx` |
| REQ-1.4 | `src/Hosts/Api/DesignTimeDbContextFactories.cs` (`HostModuleAssemblies.All`) |
| REQ-2.1 | `MasterData.Domain/{Positions,Offices,Levels,Divisions}/` |
| REQ-2.2 | `Position` / `Office` / `Level` / `Division` (sealed class, เอกพจน์) |
| REQ-2.3 | `MasterData.Domain/MasterDataItem.cs` |
| REQ-2.4 | `MasterData.Infrastructure/Persistence/MasterDataConfigurations.cs` (`ToTable("Positions", SchemaNames.Cfg)`) |
| REQ-3.1 | `MasterDataConfigurations.cs` |
| REQ-3.2 | `BuildingBlocks.Infrastructure/Persistence/SchemaNames.cs` (`Cfg`) |
| REQ-3.3 | `20260712185646_SecurityObjects.cs` (`ALTER AUTHORIZATION ON SCHEMA::cfg`) |
| REQ-3.4 | `20260712185646_SecurityObjects.cs` (GRANT/REVOKE block) |
| REQ-3.5 | `20260712185646_SecurityObjects.cs` (ไม่มี policy บน `cfg.*`) |
| REQ-3.6 | `Admins.Infrastructure/Persistence/Users/UserConfigurations.cs` + `20260712185344_InitialSchema.cs` |
| REQ-4.1 | `Admins.Application.csproj` (อ้าง `MasterData.Domain` เท่านั้น) |
| REQ-4.2 | `MasterData.Application.csproj` / `MasterData.Domain.csproj` |
| REQ-4.3 | `MasterData.Domain.csproj` (SharedKernel เท่านั้น) |
| REQ-4.4 | `Admins.Application/Users/MasterDataLookup.cs` (port) + `Admins.Infrastructure/Persistence/Users/MasterDataLookup.cs` (impl) |
| REQ-4.5 | `tests/Architecture.Tests/MasterDataArchitectureTests.cs` |
| REQ-5.1 | `src/Hosts/Api/Program.cs` (`MapMasterCrud`) |
| REQ-5.2 | `src/Hosts/Api/Program.cs` (`RequirePermission(Keys.UserManage)`) |
| REQ-5.3 | `MasterData.Domain/MasterDataItem.cs` + `MasterData.Infrastructure/Persistence/MasterDataStore.cs` |
| REQ-5.4 | `Admins.Application/Users/MasterDataLookup.cs` (`ValidateProfileFksAsync`) |
| REQ-5.5 | `20260712185912_SeedData.cs` |
| REQ-6.1 | migration 3 ไฟล์เดิม (ไม่มีไฟล์ใหม่) |
| REQ-6.2 | fresh-DB verify (Verify ของ T2) |
| REQ-6.3 | `PolDbContextModelSnapshot.cs` |
| REQ-7.1 | `.ai/shared/ARCHITECTURE.md` |
| REQ-7.2 | `BuildingBlocks.Infrastructure/Persistence/SchemaNames.cs` |
