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
| run console | `dotnet run --project src/Hosts/<TenantConsole\|AdminConsole>` |

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
  Modules/
    Payments/
      Payments.Domain/         # entity, value object, domain rule (ไม่พึ่ง EF/ASP.NET)
      Payments.Application/     # CQRS: ICommand/IQuery + handler + IPipelineBehavior
      Payments.Infrastructure/ # EF Core config, PSP adapter (IPspAdapter), vault, migrations
    Orders/  Checkout/  Cart/  Products/    # โครงเดียวกัน
  Hosts/
    TenantConsole/         # ASP.NET Core app#1 (public-facing)
    AdminConsole/          # ASP.NET Core app#2 (internal-only)
tests/
  <Module>.Tests/          # co-locate ตามโมดูล
```

- โมดูลคุยกันผ่าน **Contracts** + Mediator เท่านั้น — ห้าม reference `*.Domain`/`*.Infrastructure` ของโมดูลอื่นตรงๆ
- `Money` อยู่ **SharedKernel** (แก้ seam `PaymentPaid.Amount` long สตางค์ ↔ Orders decimal บาท — ดู ARCHITECTURE)

## EF Core

- 2 `DbContext` แยก: `AdminDbContext` (schema `admin`) · `ProducerDbContext` (schema `producer`)
- `IEntityTypeConfiguration<T>` ต่อ entity (`{Entity}Configuration`) — ไม่ config inline ใน `OnModelCreating`
- migration: `dotnet ef migrations add <PascalCaseName> --context <Ctx> --project src/Modules/<M>/<M>.Infrastructure`
- datetime เก็บ UTC, column ลงท้าย `Utc` · multi-tenant: global query filter กรอง `TenantId` (RLS) ที่ context — ไม่พึ่ง app code
- **provisioning** (`Tenant`/`PspConnection`/`VaultSecret`/active) ต้องอยู่ใน transaction เดียว + idempotent ด้วย tenant key

## Mediator (martinothamar/Mediator) — source-generated

- `Mediator.SourceGenerator` ใส่ที่ **project ปลายสุด** (Hosts) `PrivateAssets=all` · `Mediator.Abstractions` ที่ project นิยาม message/handler
- CQRS: write = `ICommand<,>`, read = `IQuery<,>`, cross-module event = `INotification` · `Handle` คืน `ValueTask<T>`
- `AddMediator(...)` (generator สร้างให้, handler ลงทะเบียนอัตโนมัติ) · pipeline behaviors เพิ่มเอง (เช่น `IdempotencyBehavior`, validation) · lifetime แนะนำ Singleton
- ได้ diagnostic ตอน **build** ถ้า request ไม่มี handler — อย่าปิด warning นี้

## Testing

- runner: `dotnet test` (xUnit แนะนำ — pick ครั้งเดียวทั้ง solution)
- assert พฤติกรรมที่สังเกตได้ ไม่ใช่ internal detail · webhook/idempotency/money path เป็น critical → property-based test (ดู `/spec-pbt`)
- ห้าม commit `[Fact(Skip=...)]` / `.only` ค้าง
