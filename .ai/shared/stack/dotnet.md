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
- datetime เก็บ UTC (datetime2); field/column **ไม่ใส่** suffix `Utc` — ตั้งชื่อ `CreatedAt`/`UpdatedAt`/`OccurredAt` (ตาม CODING_STANDARDS; suffix `Utc` ถูกถอดทั้งโค้ด+DB ใน PR #18)
- **multi-tenant isolation floor = SQL Server native RLS + `SESSION_CONTEXT('TenantId')`** ต่อ request (ไม่พึ่ง app code).
  **`SESSION_CONTEXT` เป็น per-connection** → set ตอน connection-open ผ่าน **`DbConnectionInterceptor`** (ไม่ใช่ต่อ query — pooled connection คนละตัวจะไม่เห็นค่า; spike 2026-06-21 ยืนยัน).
  EF global query filter = ชั้นสะดวกเสริม **ไม่ใช่** floor. ban raw SQL / `IgnoreQueryFilters` ข้าม tenant + test พิสูจน์ leak ปิด (รวม pooled connection ไม่ retain tenant เดิม). admin cross-tenant = DB principal แยก
- `Money` value object ใน SharedKernel — as-built: `{ MinorUnits: long, Currency: ISO4217 }` (bigint); **มาตรฐานใหม่ (ตัดสิน 2026-07-05): `{ Amount: DECIMAL(19,4), Currency }` ทุกชั้น, ห้าม float/double** — migration รอ ADR (ดู CODING_STANDARDS + `docs/reference/platform-modules.md` ข้อ 22)
- **provisioning = saga ข้าม store** (DB + vault คนละที่ ไม่มี distributed tx): `PendingProvisioning` → write DB → write vault (idempotency key) → verify → activate ขั้นสุดท้าย → compensation/retry. idempotent ด้วย tenant key
- **seed GUID = deterministic AND RFC-4122 well-formed**: fixed (ห้าม `NEWID()` ใน migration) พอสำหรับ `uniqueidentifier` แต่ nil version/variant (`…-0000-0000-…`) = malformed UUID (reviewer/tool ตีเป็น placeholder). ตั้ง version nibble=`4` + variant=`8` → `a1000000-0000-4000-8000-0000000000NN` (namespace ต่อตาราง `a1/b2/c3/…` + row counter). `Down` = `DELETE … WHERE Id LIKE 'a1000000-%'` (implicit convert uniqueidentifier→string ทำงานได้บน CI collation). seed ผ่าน `migrationBuilder.Sql("INSERT … N'ไทย' …")` (data-only migration = empty model diff, snapshot ไม่เปลี่ยน).
- **หลาย master/lookup table รูปเดียวกัน → TPC** (`builder.UseTpcMappingStrategy()` บน abstract base + `ToTable("X")` ต่อ concrete): ได้ FK type-safe N ตารางโดย **ไม่มี discriminator/base table** — ยืนยันจาก migration `Up()` = N CreateTable ไม่มี column `Discriminator`. abstract base ที่ไม่ตั้ง strategy = default TPH (discriminator, ตารางเดียว) — ตรงข้ามที่ต้องการ.

## Mediator (martinothamar/Mediator) — source-generated

- `Mediator.SourceGenerator` ใส่ที่ **project ปลายสุด** (Hosts) `PrivateAssets=all` · `Mediator.Abstractions` ที่ project นิยาม message/handler
- CQRS: write = `ICommand<,>`, read = `IQuery<,>`, cross-module event = `INotification` · `Handle` คืน `ValueTask<T>`
- `AddMediator(...)` (generator สร้างให้, handler ลงทะเบียนอัตโนมัติ) · pipeline behaviors เพิ่มเอง (เช่น `IdempotencyBehavior`, validation)
- **lifetime:** `IMediator` Singleton (perf) ได้ แต่ **handler/pipeline ที่พึ่ง `DbContext` ต้อง Scoped** (หรือ inject `IDbContextFactory`) — กัน captive dependency. เปิด `ValidateScopes=true` + มี DI validation test
- **ไม่มี open-generic handler**: source generator ไม่สร้าง `IRequestHandler<Cmd<T>>` แบบ open-generic → CRUD ที่ symmetric N ตาราง (master/lookup) อย่าเขียน N×concrete handler (boilerplate ล้วน). ใช้ generic service (เช่น `IMasterDataStore` เรียก `_db.Set<T>()`) bypass Mediator แต่ **คง commit ผ่าน keyed UoW เดิม** (S2, ห้าม `DbContext.SaveChanges` ตรง) — code-shape ตัดสินจากข้อจำกัด generator จริง ไม่ใช่รสนิยม.
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

## Minimal-API routing / MapGroup (route-scheme migration — เจอจริง api-route-scheme)

- **area-root ห้าม empty pattern บน `MapGroup`**: `group.MapGet("")` / `MapPost("")` บน `MapGroup("/products")` render RawText เป็น **trailing-slash** `/api/v1/products/` (ยืนยันด้วย `EndpointDataSource.Endpoints.OfType<RouteEndpoint>().RoutePattern.RawText`) — ผิด clean-path, OpenAPI path ติด `/` ท้าย, และ assertion `RawText == "/api/v1/products"` fail. map area-root ด้วย **explicit full path บน parent group** (`api.MapPost("/products")`) แทน; ใช้ `MapGroup` เมื่อต้องผูก endpoint filter ครั้งเดียวทั้ง surface (admin/producer CSRF) เท่านั้น — root ของ filtered group ที่ต้องการ path สะอาด map บน parent + `.AddEndpointFilter<T>()` per-endpoint.
- **routing ยอมรับ trailing slash** → build + smoke ปิดบัง defect: `GET /api/v1/products` (no-slash) ยัง match route `/api/v1/products/` (ไม่ 404, เข้า handler) → `dotnet build -warnaserror` เขียว + smoke request ผ่าน = มองไม่เห็น path เพี้ยน. verify route-scheme/big-bang migration ด้วย **arch guard** (boot `WebApplicationFactory` → enumerate `EndpointDataSource` เทียบ literal regex `^/api/v1/(areas)(/.*)?$` OR infra allowlist `/health/*`,`/openapi/`,`/scalar`) + **complete legacy-404** (assert ทุก old method+path generate จาก mapping table ทั้งชุด ไม่ sample per-area) — arch test คือที่เดียวที่จับ empty-pattern trailing-slash + route ตกสำรวจ/วางผิด area.
- **OIDC `CallbackPath` = middleware ไม่ใช่ mapped endpoint**: ย้าย callback ด้วย config (`options.CallbackPath`) ไม่ใช่ routing; ไม่โผล่ใน `EndpointDataSource` (arch test มองไม่เห็น) → ต้อง integration test แยก (assert OIDC challenge emit `redirect_uri` ลงท้าย callback ใหม่). callback path ที่ย้ายแล้วชนกับ mapped route ไม่ได้ และ group filter (CSRF) มองไม่เห็นมัน (intercept ก่อน routing).

## SQL Server RLS — direct query/DML ผ่าน sqlcmd (กับดักเจอจริง)

- **SESSION_CONTEXT ต้อง base type ตรง**: predicate ของ repo นี้ทำ `CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier)` — sql_variant ที่เก็บ nvarchar (จากการส่ง string ตรงๆ เข้า `sp_set_session_context`) CAST ไม่ผ่าน → มองเห็น 0 แถวเงียบๆ ทั้ง SELECT/DML. ต้อง `DECLARE @t uniqueidentifier = '<guid>'; EXEC sp_set_session_context 'TenantId', @t;` ใน batch เดียวกัน.
- **DML บนตารางใต้ security policy ต้องเปิด QUOTED_IDENTIFIER**: predicate ของ policy เป็น schema-bound inline function (ตระกูลเดียวกับ indexed view / filtered index) → sqlcmd default OFF → `DELETE/UPDATE/INSERT` fail `Msg 1934`. ใส่ flag `-I` หรือ `SET QUOTED_IDENTIFIER ON;` ครั้งเดียวหัวสคริปต์ (session-level คลุมทุก batch ใน connection เดียว).
- **sa ไม่ bypass RLS**: bypass เดียวคือ membership role `pol_rls_bypass` — sa/dbo โดน filter เหมือนกัน ต้อง bind session context เสมอ.
- **FILTER predicate มีผลกับ `DELETE`/`UPDATE`/`SELECT` ไม่ใช่แค่ `INSERT`** — ไม่ stamp context แล้ว `DELETE` จะ "สำเร็จ 0 แถว" เงียบ ๆ (ไม่ error) เพราะมันมองไม่เห็นแถวที่จะลบ. กับดัก 2 ชั้นที่ตามมา:
  - **maintenance/seed script ที่เป็น delete-then-insert ต้อง stamp ก่อน DELETE เสมอ** ไม่งั้นรอบสองชน PK (`Msg 2627`) — ทางเข้าที่สะอาดคือ insert แถว `admin.Users` ที่ `Tier = 1` (Super, ตารางนี้ไม่มี predicate) ก่อน แล้ว `sp_set_session_context` ด้วย `UserId` = แถวนั้น + `MerchantId` = `Guid.Empty` → เข้าสาขา Super ของ `sec.fn_merchant_predicate` เห็นทุก merchant โดยไม่แตะ policy/bypass role เลย.
  - **`SELECT COUNT(*)` โดยไม่ stamp = 0 ซึ่งอ่านผิดเป็น "ตารางว่าง" ได้ง่ายมาก** — เคสจริง: สำรวจ DB ตอนต้นงานแล้วสรุปว่า `shop.Orders` ว่างเปล่า ที่จริงมี 3 แถว RLS ซ่อนไว้. ก่อนสรุปว่าตารางใดว่าง ต้อง stamp context แล้วนับซ้ำ.
- **`LINENO` เป็น reserved keyword ของ T-SQL** — ตั้งชื่อคอลัมน์ table variable ว่า `LineNo` ได้ `Msg 156 Incorrect syntax near the keyword 'LineNo'` (ใช้ `LineIdx`).
- **`merch.Merchants.Code` เก็บ lowercase เสมอ** — `Merchant.Create` เรียก `MerchantCode.Normalize` (`ToLowerInvariant`) ก่อน persist และ allowlist เป็น ordinal set ของ `vprivilege`/`vcommerce`/`vsouvenir`. seed/fixture ที่ใส่ mixed-case จะไม่ตรงกับสิ่งที่แอปเขียนจริง.

## Configuration / local-dev env (กับดักเจอจริง)

- **ค่าใน `.env` ที่มี `;` หรือช่องว่างต้องอยู่ใน single quote** — Docker Compose มี parser ของตัวเอง (ไม่แคร์) แต่ README สั่ง `set -a && source .env` ด้วย และ shell ตีความ `;` ที่ไม่ได้ quote เป็นตัวจบคำสั่ง → connection string ทุกตัวถูกตัดเงียบที่ `;` ตัวแรก (`POL_DESIGN_SQL` เหลือ `Server=localhost,11433`) แล้ว `User Id=sa` ถูกรันเป็นคำสั่ง (`command not found: User`). อาการปลายทาง = `pre-login handshake` error จาก `dotnet ef`. ใช้ single ไม่ใช่ double quote เพื่อไม่ให้ `$` ใน password ถูก interpolate.
- **key ของ connection string ฝั่ง app คือ `App` ไม่ใช่ `Producer`** (rf1 rename; `Program.cs` เรียก `GetConnectionString("App")`). key เก่าที่ค้างใน `.env` / `appsettings.Development.json` จะ **ไม่ถูกอ่านเลย** และ `App` ตกไปหยิบค่าจาก `appsettings.json` ที่ commit ไว้ซึ่ง **password ว่างโดยตั้งใจ** (secret ฉีดตอน runtime) → `pol_app` ต่อ DB ไม่ได้ แต่ host ยัง boot ผ่าน. ทั้งสองไฟล์ gitignored → ไม่มี CI จับ. ตรวจเร็ว: boot API แล้วดู health check `app-db` รัน `SELECT 1` ผ่านไหม / `/health/ready` = 200 ไหม.
- **`docker compose up -d` คืน prompt ก่อน SQL Server รับ connection ~30-60 วิ** (นานกว่านั้นถ้าเพิ่ง `down -v`) — `dotnet ef` ที่ยิงเร็วไปได้ `error 10061 Connection refused` ซึ่งไม่ใช่ config พัง. รอ `docker compose ps pol-db` = `healthy` ก่อน.
- **Tenant guid ของ Integration suite อ่านจาก `tests/Integration.Tests/IntegrationDb.cs`** (TenantA ลงท้าย `a1`, TenantB ลงท้าย `b1` — ไม่ใช่ศูนย์ล้วน); integration flake `OrdersReconciliation` = marker `QQQ` สะสมบน DB reuse — ล้าง residue ด้วย DELETE scoped ใต้ TenantB binding แล้วรันใหม่.
