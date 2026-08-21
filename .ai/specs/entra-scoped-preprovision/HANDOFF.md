# Handoff: Entra Scoped Pre-provision

> From: Codex root session   To: ship/review   Date: 2026-08-20

Implementation ตาม spec เสร็จและผ่าน build, unit, live SQL integration, trace, migration, audit,
security review, Browser Lab happy path และ unbound-account negative control. Task 1–6 ปิดพร้อม Evidence แล้ว

## Task Summary

เพิ่ม Tier 0 Entra Workforce pre-provision สำหรับ Scoped admin ตั้งแต่ immutable tenant pin, atomic identity
binding, permanent idempotency replay, tamper-evident audit, Super-only HTTP contract จนถึง strict OIDC login

- **Spec**: `requirements.md`, `design.md`, `tasks.md`
- **Scope**: Task 1–6 และ REQ 93 criteria ตาม spec trace
- **Audit**: `.pipeline/entra-scoped-preprovision/audit.md` verdict PASS รอบสอง

## Current Status

| ส่วน | สถานะ | หมายเหตุ |
|---|---|---|
| Task 1–5 source + tests | Complete | Build ผ่าน; non-integration 1,911 tests ผ่าน |
| Task 6 automated artifacts | Complete | Live SQL: Architecture 4 + Integration 168 tests ผ่าน |
| Security/audit | PASS | ไม่พบ finding คงเหลือ |
| Unit test execution | PASS | 1,911 passed, 0 failed, 0 skipped |
| Live SQL execution | PASS | SQL Server 2025 ephemeral 3 instances; 172 passed |
| Browser Lab happy path | PASS | Super pre-provision, replay, employee login, Admin session และ merchant scope ผ่าน |
| Browser Lab negative control | PASS | unbound Microsoft account ได้ `not-provisioned`; foreign/missing claims ผ่าน automated coverage |

Task 1–6 ปิดครบและพร้อม ship หลัง final scoped gate

## Files Changed

| Area | Files | Change |
|---|---|---|
| Spec | `.ai/specs/entra-scoped-preprovision/*` | requirements, design, tasks และ handoff ใหม่ |
| Migration | `20260819145219_WorkforceTenantBinding*`, `PolDbContextModelSnapshot.cs` | canonical subject guard, singleton tenant pin, grants และ rollback |
| Fresh DB | `docker/bootstrap/assert-fresh-db.*` | pin migration 20, schema/check/grant/empty-row assertions |
| Runbooks | `docs/runbooks/local-dev-run.md`, `docs/runbooks/deploy-self-host.md` | latest migration และ count 20 |
| Admin domain/application | `WorkforceTenantBinding.cs`, `WorkforceTenantBindingPorts.cs`, `PreProvisionMicrosoftIdentity.cs` | tenant pin model, command, ports, atomic handler |
| Admin identity flows | `BindInvitedAdmin.cs`, `CreateScopedAdmin.cs`, `SelfProvisionSuperAdmin.cs`, `ResolveAdmin*.cs` | identity mutation lock, safe re-read และ authorization state |
| Persistence | `UserRepository.cs`, `UserConfiguration.cs`, `AuthorizationLease.cs`, `AdminOperationStore.cs`, `WorkforceTenantBindingStore.cs` | transaction locks, tenant store, permanent replay และ race handling |
| Audit | `GovernanceAuditAppender.cs`, `AdminIdentityAuditWriter.cs`, `GovernanceStore.cs`, `AuditRecord.cs` | shared hash-chain appender, fingerprint-only payload, timezone-stable hash |
| Host auth | `AdminMicrosoftTenantSnapshot.cs`, `OidcAuthentication.cs`, `LoginService.cs`, `SessionAuthenticationHandler.cs` | strict Authority/tid/oid, canonical subject, no external subject leakage |
| HTTP contract | `Program.cs`, `AuthRateLimiting.cs`, `CsrfFilter.cs`, `HostWiring.cs` | PUT endpoint, stable errors, two-layer limiter, internal actor identity |
| Shared errors | `NotFoundException.cs`, `ProblemDetailsExceptionHandler.cs` | optional stable code และ trace ID |
| Wiring/floors | `ControlPlaneDbContext.cs`, `ControlPlanePersistenceRegistration.cs`, `WriteAuthorizers.cs`, configuration files | mappings, DI และ append-only/write floors |
| Unit/host tests | `tests/Admins.Tests/*`, `tests/Governance.Tests/*`, selected `tests/Hosts.Tests/*` | handler, audit, OIDC, session, contract และ rate-limit coverage |
| SQL/architecture tests | `EntraPreProvisionSqlIntegrationTests.cs`, `WorkforceTenantBindingStoreTests.cs`, `WorkforceTenantBindingIntegrationTests.cs`, `FreshBaselineMigrationIntegrationTests.cs` | production adapters, races, rollback, grants, migration up/down/fresh |

User-owned untracked files `docs/prompts/` และ `tests/Hosts.Tests/AdminPspCredentialChangeTests.cs` อยู่นอก scope.
Runtime artifact `src/Hosts/Api/merchant-user-photos/*.png` อยู่นอก scope เช่นกัน; ห้ามลบหรือรวมใน PR นี้

## Important Decisions

- Workforce Authority รับเฉพาะ `https://login.microsoftonline.com/{tenant-guid}/v2.0`; snapshot เดียวใช้ทั้ง boot pin และ Admin OIDC
- Tenant pin เป็น singleton immutable; host ตรวจ pin หลัง Development migration และก่อน listen
- Identity/email mutations ใช้ transaction-owned PII-free global applock แล้ว re-read ก่อน Save เพื่อปิด expected unique races และ log leakage
- Pre-provision operation lock serialize ต่อ actor + operation เพื่อครอบ key ที่ SQL collation มองว่าเท่ากัน
- Identity binding, hash-chain audit และ permanent `200` replay record commit ใน transaction เดียว
- Microsoft audit/session ไม่เก็บ external subject; downstream actor ใช้ `admin:{internal-id}`
- Rate limiting มี source-IP layer ก่อน authentication และ internal Admin ID layer หลัง authorization
- Merchant Microsoft handler ไม่เปลี่ยน semantic; strict tenant/OID enforcement จำกัดเฉพาะ Admin

## Constraints

- ห้าม hardcode Lab tenant, Object ID, email หรือ credential ใน source/test/spec
- ห้ามอ่านหรือ commit `.env`; ใช้ exported environment variables เท่านั้น
- ห้าม push หรือ commit ตรง `main`/`develop`; งานนี้ยังไม่มี commit/push
- Browser submit real tenant/Object ID ต้องยืนยันกับผู้ใช้ ณ เวลากด action

## Tests Run

### Build

```bash
dotnet build pol-core.slnx --no-restore -warnaserror -m:1 /nodeReuse:false -p:UseSharedCompilation=false
```

ผล: PASS, 0 warnings, 0 errors

### Unit and in-memory tests

```bash
dotnet test pol-core.slnx --no-build --filter "Category!=Integration"
```

ผล: PASS, 1,911 passed, 0 failed, 0 skipped

### Live SQL integration

ใช้ SQL Server 2025 ephemeral 3 instances พร้อม generated process-only credentials แล้วรัน bootstrap,
migration, fresh database assertion และ solution integration filter

```bash
dotnet test pol-core.slnx --no-build --filter "Category=Integration"
```

ผล: PASS, Architecture 4 + Integration 168 passed, 0 failed, 0 skipped; ephemeral containers removed

### Spec trace

```bash
scripts/spec-trace.sh entra-scoped-preprovision
```

ผล: PASS, 93 criteria referenced, EARS lint PASS

### EF migration model

```bash
dotnet ef migrations list --context PolDbContext \
  --project src/BuildingBlocks/BuildingBlocks.Infrastructure \
  --startup-project src/Hosts/Api --no-build --no-connect

dotnet ef migrations has-pending-model-changes --context PolDbContext \
  --project src/BuildingBlocks/BuildingBlocks.Infrastructure \
  --startup-project src/Hosts/Api --no-build
```

ผล: PASS, 20 migrations, latest `20260819145219_WorkforceTenantBinding`, no pending model changes

### Migration render

รัน `dotnet ef migrations script 0 --no-build` แล้วตรวจ preflight มาก่อน application DDL และพบ
`WorkforceTenantBindings`; ผล `migration-script-render: OK`

### Review

- Auditor: PASS รอบสอง, 0 findings
- Security reviewer: PASS, 0 findings
- Lab identifier scan: PASS, ไม่พบ real Lab IDs ใน source/test/docker/spec
- Scoped `git diff --check`: PASS
- OpenAPI CSRF contract follow-up: focused Hosts.Tests build PASS, 0 warnings/errors; unsafe AdminSession
  operations ประกาศ required `X-CSRF-Token`

### Browser Lab happy path

รันกับ API current source และ Entra tenant จริงโดยไม่บันทึก tenant, Object ID, email หรือ token ลง repo:

- API health/readiness และ Scalar endpoint ใหม่พร้อมใช้งาน
- Super session อ่าน target Scoped admin ได้ `subjectBound: false`, version `3`, ETag `"v3"`
- ตรวจพบข้อมูล Lab เดิมคลาดเคลื่อน: Object ID แรกเป็นของ Super account; employee account เป็น tenant-local
  Guest ที่มี Object ID คนละค่า จึงหยุด request ที่ conflict และตรวจ target ว่ายังไม่เปลี่ยน
- Corrected pre-provision `PUT` ผ่าน `200`; response มีเฉพาะ `adminId`, `provider`, `subjectBound`, `version`;
  ได้ `subjectBound: true`, version `4`, ETag `"v4"`
- Replay request เดิมด้วย idempotency key เดิมผ่าน `200` พร้อม body และ ETag เดิม
- Employee Microsoft login redirect เข้า dashboard สำเร็จ
- `GET /api/v1/admins/me` ผ่าน `200` และ resolve เป็น Scoped target เดิม พร้อม expected role permissions และ merchant scope
- `GET /api/v1/merchants` ผ่าน `200` และคืนเฉพาะ merchant ที่ assign ไว้หนึ่งรายการ
- CSRF mismatch attempts คืน `403 csrf_failed` ก่อน mutation; target/ETag คงเดิม

ข้อสังเกต Lab: employee account ที่ใช้เป็น Entra B2B `Guest`; production-like workforce employee ควรใช้ tenant
`Member`. Happy path พิสูจน์ tenant-local OID binding และ authorization flow แต่ไม่เปลี่ยน user type ใน Entra

### Browser Lab negative control

สร้าง tenant-local Entra Member ที่ไม่มี pol-core binding แล้ว login ผ่าน Microsoft OIDC. Callback ผ่าน
provider/tenant/claim validation และ redirect ไป `/login-error?reason=not-provisioned`; ไม่ provision account
หรือสร้าง session ใหม่ตาม production flow. Foreign/missing `tid`/`oid` ผ่าน automated Hosts.Tests

## Known Issues

- OpenAPI AdminSession cookie name ยังอธิบาย `adm_session` ขณะที่ Development over HTTPS ใช้
  `__Host-adm_session`; ไม่กระทบ browser cookie auth แต่ documentation ยังคลาดเคลื่อน
- Admin SPA error copy ระบุ “บัญชี Google” แม้ denial มาจาก Microsoft; SPA source อยู่นอก repo นี้

## Next Recommended Agent

Git/PR ship หลัง final scoped gate

## Next Steps

1. แยก user-owned untracked files ออกจาก feature scope
2. รัน final scoped gate และ pre-commit hooks
3. Commit บน feature branch และเปิด PR เข้า `develop`
