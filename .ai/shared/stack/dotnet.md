# Stack profile — .NET (pol-core)

> Complements the neutral canon (`../CODING_STANDARDS.md`, `../ARCHITECTURE.md`, `../TESTING_PROTOCOL.md`),
> ไม่แทนที่. version pin: `../CODING_STANDARDS.md` (.NET 10 / C# 14 / EF Core 10 / SQL Server 2025 / Mediator 3.x).

## Commands

| งาน | คำสั่ง |
|---|---|
| restore | `dotnet restore` |
| build / typecheck | `dotnet build -warnaserror` |
| test (ทั้ง solution) | `dotnet test` |
| test 1 project | `dotnet test tests/<Project>.Tests` |
| test 1 case | `dotnet test --filter "FullyQualifiedName~<Namespace>.<Class>.<Method>"` |
| format | `dotnet format` (CI ใช้ `dotnet format --verify-no-changes`) |
| run API | `dotnet run --project src/Hosts/Api` (Backend API เดียว, dev http :5100) |
| run worker | `dotnet run --project src/Hosts/Worker` |

**Task gate** (`.ai/bin/gate-task.sh` อ่านจาก env) — ตั้งให้ flip task เป็น `[x]` พิสูจน์เขียวจริง:

```
export SDD_TYPECHECK_CMD="dotnet build -warnaserror"
export SDD_TEST_CMD="dotnet test"
```

## Project layout (Modular Monolith · Clean Architecture · CQRS)

แต่ละโมดูล (Products / Cart / Checkout / Orders / Payments) เป็นชุด project แยกตามชั้น Clean Architecture
dependency ชี้เข้า domain (Domain ไม่อ้าง Infrastructure):

```
src/
  SharedKernel/            # Money, base types, primitives — ทุกโมดูลใช้ร่วม
  Contracts/               # cross-module messages (INotification เช่น PaymentPaid) — ตัวเดียวที่โมดูลอื่น reference ได้
  BuildingBlocks/
    BuildingBlocks.Application/     # cross-cutting ports: ITenantContext, IClock, IUnitOfWork, IOutbox, IIdempotencyStore, IVaultSecretStore, ITenantScoped, pipeline behaviors
    BuildingBlocks.Infrastructure/  # cross-cutting impl: ProducerDbContext, SessionContextConnectionInterceptor (RLS floor), outbox+dispatcher, idempotency store, envelope vault, EfUnitOfWork
  Modules/
    Payments/
      Payments.Domain/         # entity, value object, domain rule (ไม่พึ่ง EF/ASP.NET)
      Payments.Application/     # CQRS: ICommand/IQuery + handler + IPipelineBehavior
      Payments.Infrastructure/ # EF Core config, PSP adapter (IPspAdapter), vault, migrations
    Orders/  Checkout/  Cart/  Products/    # โครงเดียวกัน
  Hosts/
    Api/                   # ASP.NET Core Backend API (เดียว, เสิร์ฟ 2 SPA: pol-tenant + pol-admin)
    Worker/                # background host (outbox dispatcher)
tests/
  <Module>.Tests/          # co-locate ตามโมดูล
```

- โมดูลคุยกันผ่าน **Contracts** + Mediator เท่านั้น — ห้าม reference `*.Domain`/`*.Infrastructure` ของโมดูลอื่นตรงๆ
- `Money` อยู่ **SharedKernel** (แก้ seam `PaymentPaid.Amount` long สตางค์ ↔ Orders decimal บาท — ดู ARCHITECTURE)

## EF Core

- 1 `DbContext`: `ProducerDbContext` (schema `producer`, RLS-enforced via pol_app)
- `IEntityTypeConfiguration<T>` ต่อ entity (`{Entity}Configuration`) — ไม่ config inline ใน `OnModelCreating`
- migration: `dotnet ef migrations add <PascalCaseName> --context <Ctx> --project src/Modules/<M>/<M>.Infrastructure`
- datetime เก็บ UTC, column ลงท้าย `Utc`
- **multi-tenant isolation floor = SQL Server native RLS + `SESSION_CONTEXT('TenantId')`** ต่อ request (ไม่พึ่ง app code).
  **`SESSION_CONTEXT` เป็น per-connection** → set ตอน connection-open ผ่าน **`DbConnectionInterceptor`** (ไม่ใช่ต่อ query — pooled connection คนละตัวจะไม่เห็นค่า; spike 2026-06-21 ยืนยัน).
  EF global query filter = ชั้นสะดวกเสริม **ไม่ใช่** floor. ban raw SQL / `IgnoreQueryFilters` ข้าม tenant + test พิสูจน์ leak ปิด (รวม pooled connection ไม่ retain tenant เดิม). admin cross-tenant = DB principal แยก
- `Money` value object ใน SharedKernel: `{ MinorUnits: long, Currency: ISO4217 }` — ไม่ map decimal/float ที่ cross-module seam
- **provisioning = saga ข้าม store** (DB + vault คนละที่ ไม่มี distributed tx): `PendingProvisioning` → write DB → write vault (idempotency key) → verify → activate ขั้นสุดท้าย → compensation/retry. idempotent ด้วย tenant key

## Mediator (martinothamar/Mediator) — source-generated

- `Mediator.SourceGenerator` ใส่ที่ **project ปลายสุด** (Hosts) `PrivateAssets=all` · `Mediator.Abstractions` ที่ project นิยาม message/handler
- CQRS: write = `ICommand<,>`, read = `IQuery<,>`, cross-module event = `INotification` · `Handle` คืน `ValueTask<T>`
- `AddMediator(...)` (generator สร้างให้, handler ลงทะเบียนอัตโนมัติ) · pipeline behaviors เพิ่มเอง (เช่น `IdempotencyBehavior`, validation)
- **lifetime:** `IMediator` Singleton (perf) ได้ แต่ **handler/pipeline ที่พึ่ง `DbContext` ต้อง Scoped** (หรือ inject `IDbContextFactory`) — กัน captive dependency. เปิด `ValidateScopes=true` + มี DI validation test
- `IdempotencyBehavior`: unique key `(psp,eventId)` + `(psp,externalChargeId,normalizedStatus)`, atomic upsert; publish `PaymentPaid` ผ่าน **outbox** (table + dispatcher poll lock/lease + poison/DLQ + idempotent consumer)
- ได้ diagnostic ตอน **build** ถ้า request ไม่มี handler — อย่าปิด warning นี้

## Testing

- runner: `dotnet test` (xUnit แนะนำ — pick ครั้งเดียวทั้ง solution)
- assert พฤติกรรมที่สังเกตได้ ไม่ใช่ internal detail · webhook/idempotency/money path เป็น critical → property-based test (ดู `/spec-pbt`)
- ห้าม commit `[Fact(Skip=...)]` / `.only` ค้าง
- **repo test บน SQLite ไม่ต้อง live SQL Server**: `new ProducerDbContext(new DbContextOptionsBuilder<ProducerDbContext>().UseSqlite(conn).Options, new ModuleAssemblies([typeof(<AnyConfigInModule>).Assembly]))` → `EnsureCreated()` map เฉพาะ config ของ assembly นั้น + base tables → เรียก repository จริง (filter/sort/paging/LIKE-escape/NULLS-last) ออฟไลน์. `EF.Functions.Like` แปลบน SQLite ได้ (EF Core **InMemory** provider แปลไม่ได้ — อย่าใช้กับ Like). `[` wildcard เป็น SQL-Server-only (SQLite ไม่ treat พิเศษ) → เทสค่า escape output แทน. CI แยก integration ด้วย trait `[Trait("Category","Integration")]` (unit job รัน `--filter "Category!=Integration"`).

## SFS / System.Text.Json / OpenAPI (patterns เจอจริง)

- **`SearchOption` ชนกับ `System.IO.SearchOption`** ใต้ `ImplicitUsings enable` (CS0104) → ทุกไฟล์ที่ใช้ `BuildingBlocks.Application.SearchOption` ต้องมี `using SearchOption = BuildingBlocks.Application.SearchOption;`.
- **enum string-token contract เข้ม (reject integer)**: `[JsonConverter(typeof(JsonStringEnumConverter<T>))]` default `allowIntegerValues: true` → `{"operator":0}` ผ่านเป็น ordinal เงียบๆ (bypass string-token). ต้องการ reject numeric → subclass: `internal sealed class TConverter() : JsonStringEnumConverter<T>(namingPolicy: null, allowIntegerValues: false);` แล้ว annotate ตัว subclass; `[JsonStringEnumMemberName]` ยัง honor ปกติ. numeric ที่โดน reject = `JsonException` → parser ห่อเป็น `ArgumentException` → 400.
- **OpenAPI param ที่อ่านจาก raw query string** (ไม่ใช่ typed minimal-API param, ASP.NET จึงไม่ emit): .NET 10 built-in OpenAPI ใช้ `builder.Services.AddOpenApi(o => o.AddOperationTransformer(...))` + endpoint metadata marker (`.WithMetadata(new Marker())`, transformer เช็ก `context.Description.ActionDescriptor.EndpointMetadata.OfType<Marker>()`) — **ไม่ใช่** `.WithOpenApi(...)` (Swashbuckle-era, ไม่มีผลกับ built-in generator). ทดสอบด้วย `WebApplicationFactory` fetch `/openapi/v1.json`. Microsoft.OpenApi 2.x: `OpenApiParameter`/`OpenApiSchema`/`JsonSchemaType`/`ParameterLocation` อยู่ใน namespace `Microsoft.OpenApi`.
- **default-sort fallback ต้องมี unique tie-breaker**: paging (`Skip`/`Take`) เสถียรเฉพาะเมื่อ ORDER BY ปิดท้ายด้วย column unique. `OrderByDescending(CreatedAt)` เดี่ยว = ไม่ deterministic (timestamp ชนกัน bulk/seed → SQL Server เรียงมั่ว → หน้าซ้ำ/ข้าม). ต่อ `.ThenByDescending(Id)` (PK). column ที่มี unique index (เช่น `AdminRole.Code`) ใช้เดี่ยวได้.
- **project scalar เท่านั้นใน server-side `Select`**: computed/unmapped member (`Product.Price => Money.Of(...)`, `AdminRole.PermissionKeys`) แปล LINQ→SQL ไม่ได้ (`could not be translated` ตอน execute) — project สอง scalar column แล้ว reconstitute client-side หลัง `ToListAsync`, หรือ materialize entity ก่อนแล้ว map.

## SQL Server RLS — direct query/DML ผ่าน sqlcmd (กับดักเจอจริง)

- **SESSION_CONTEXT ต้อง base type ตรง**: predicate ของ repo นี้ทำ `CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier)` — sql_variant ที่เก็บ nvarchar (จากการส่ง string ตรงๆ เข้า `sp_set_session_context`) CAST ไม่ผ่าน → มองเห็น 0 แถวเงียบๆ ทั้ง SELECT/DML. ต้อง `DECLARE @t uniqueidentifier = '<guid>'; EXEC sp_set_session_context 'TenantId', @t;` ใน batch เดียวกัน.
- **ตารางมี filtered index → DML ต้องเปิด QUOTED_IDENTIFIER**: sqlcmd default OFF → `DELETE/UPDATE` fail `Msg 1934` — ใส่ flag `-I`.
- **sa ไม่ bypass RLS**: bypass เดียวคือ membership role `pol_rls_bypass` — sa/dbo โดน filter เหมือนกัน ต้อง bind session context เสมอ.
- **Tenant guid ของ Integration suite อ่านจาก `tests/Integration.Tests/IntegrationDb.cs`** (TenantA ลงท้าย `a1`, TenantB ลงท้าย `b1` — ไม่ใช่ศูนย์ล้วน); integration flake `OrdersReconciliation` = marker `QQQ` สะสมบน DB reuse — ล้าง residue ด้วย DELETE scoped ใต้ TenantB binding แล้วรันใหม่.
