# Inventory การย้าย packaging

เอกสารนี้บันทึกจุดที่ต้องรักษาก่อนลบ legacy project wrappers ของ Task 1. ไม่มี production/cutover migration, backfill หรือ external business side effect; baseline schema และ fixture writes ใช้เฉพาะ database `PolPackagingTask1Test`.

## Baseline และเป้าหมาย

| รายการ | ก่อน | หลัง |
|---|---:|---:|
| Source projects | 39 | 4: `Pol.Domain`, `Pol.Application`, `Pol.Infrastructure`, `Pol.Api` |
| Test projects | 13 delivery + 1 fixture | 3 delivery + 4 negative fixtures |
| Source files | 602 | 602 |
| Test files | 293 | 293 |
| Baseline tests | 2,345 passed | target full suite 2,351 passed |

## Callers และ runtime wiring

| จุด | ตำแหน่งเป้าหมาย | สิ่งที่ต้องคง |
|---|---|---|
| HTTP host และ DI | `src/Pol.Api/Api/Program.cs` | HTTP route, service registration และ host เดียว |
| Generated mediator wiring | `src/Pol.Api/Pol.Api.csproj` | `Mediator.SourceGenerator` ใน compilation ของ host |
| EF design/runtime model | `src/Pol.Api/Api/DesignTimeDbContextFactories.cs`, `src/Pol.Infrastructure/BuildingBlocks.Infrastructure/Persistence/PolDbContext.cs` | selection ของ module mapping ตรงกับ baseline |
| Reflection | `src/Pol.Api/Api/Persistence/WriteAuthorizers.cs` | assembly-qualified type ของ authorization lease resolve ได้ |
| Workforce migration CLI | `src/Pol.Infrastructure/Tools/WorkforceIdentityMigrator/Program.cs` | privileged migration flow, manifest evidence และ `IsolationLevel.Serializable` |

## DI, jobs และ configuration

| กลุ่ม | จุดที่ตรวจ |
|---|---|
| Host jobs | `Pol.Api/Api/BackgroundDispatch`, `Admins/SessionPruneService`, `Merchants/PhotoStagingPruneService`, `ConsoleConfiguration` |
| Persistence jobs | `Pol.Infrastructure/Persistence/Persistence.ControlPlane`, `Persistence.MerchantRuntime`, `Persistence.MerchantUsers` |
| Telemetry | `Pol.Infrastructure/BuildingBlocks.Infrastructure/Observability/SecurityTelemetryDispatcher.cs` |
| Container | `Dockerfile`, `docker-compose.prod.yml`, `docker/migrate-entrypoint.sh` |
| CI/local commands | `.github/workflows/ci.yml`, `.gitlab-ci.yml`, `scripts/dev-db-migrate.sh`, `scripts/check-migration-script.sh`, `scripts/check-migration-lineage.sh` |

## Data ที่ต้องรักษา

- EF migrations และ `PolDbContextModelSnapshot` ย้ายไป `src/Pol.Infrastructure/BuildingBlocks.Infrastructure/Persistence/Migrations`.
- `PolDbContext` เป็น migration-only composition; runtime เหลือ `ControlPlaneDbContext` และ `CommerceDbContext`. ชื่อ `MerchantUserDbContext`/`MerchantRuntimeDbContext` เหลือ compatibility fixtures ที่ไม่ register.
- `WorkforceIdentityMigration` คง target validation, manifest digest, atomic transaction และ redacted output.
- ไม่มีการเปลี่ยน business/prod data หรือ existing database schema; integration fixture สามารถสร้าง schema และข้อมูลทดสอบใน `PolPackagingTask1Test` เท่านั้น.
- `PhotoStoreRootPath` ที่เป็น relative path เปลี่ยน content root จาก `src/Pol.Api/Api` เป็น `src/Pol.Api`; ตรวจพบทั้งสอง directory ว่างก่อนย้าย. Container volume `/app/merchant-user-photos` ไม่เปลี่ยน.

| ข้อมูลค้างที่ต้อง inventory ก่อน cutover | Source/table owner pointer | เงื่อนไขเก็บรักษา |
|---|---|---|
| Admin/Merchant identities และ sessions | `Pol.Domain/Modules/Admins.Domain/Users/User.cs`, `Pol.Domain/Modules/Merchants.Domain/Users/User.cs`, `Pol.Infrastructure/Persistence/Persistence.ControlPlane/Admins`, `Persistence.MerchantUsers` | map จาก identity evidence, ไม่เดาจาก email/display name |
| Order, PaymentSession IDs และ PSP references | `Pol.Domain/Modules/Orders.Domain/Order.cs`, `Pol.Domain/Modules/Payments.Domain/Session.cs`, `Persistence.MerchantRuntime` | คง ID/reference และไม่สร้าง charge ใหม่ |
| Customer link/token aliases | `Pol.Domain/Modules/Orders.Domain`, `Pol.Api/Api/Payments/LegacyPaymentCompatibility.cs` | resolve alias จน inventory ยืนยันว่า reference หมดอายุ |
| Inbound webhook และ recovery events | `Pol.Application/Modules/Payments.Application/HandlePspWebhook`, `Persistence.MerchantRuntime/Payments/InboundWebhookStore.cs` | callback ที่ค้างต้อง durable/replay ได้ |
| Outbox, inbox และ notification delivery | `Pol.Infrastructure/BuildingBlocks.Infrastructure/Outbox`, `Persistence.ControlPlane/Notifications`, `Persistence.MerchantRuntime/Outbox` | รักษา event/delivery history และ dedupe keys |
| Provider credential versions | `Pol.Domain/Modules/Payments.Domain/Psp`, `Persistence.MerchantRuntime/Payments` | เก็บ version/provenance, secret ไม่อ่านหรือ log |

## เงื่อนไขก่อนถอด compatibility path

- source/test path เก่าต้องไม่มี caller จาก static guard, runbook, Docker, CI หรือ script.
- build, unit, architecture, integration, host, migration parity และ entrypoint ต้องผ่านจาก path ใหม่.
- inventory นี้และ `api-scope.json` ต้องยังอ้าง command/runtime ที่ตรวจซ้ำได้.
