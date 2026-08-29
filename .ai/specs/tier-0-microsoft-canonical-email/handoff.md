> Status: unknown
# Handoff: Tier 0 Microsoft canonical email

> From: Codex `/root` | To: human review | Date: 2026-08-23

Tasks 1–6 ของ spec `tier-0-microsoft-canonical-email` implement และ verify ครบแล้ว. งานอยู่บน branch `codex/tier-0-microsoft-canonical-email` โดยยังไม่ commit หรือ push.

## Task Summary

เปลี่ยน Admin Microsoft workforce identity จาก Entra `oid`/App Role เป็น canonical `@viriyah.co.th` email, เพิ่ม atomic resolve/bind/JIT, fail-closed migration และ retire pre-provision surface. ครอบ REQ-1 ถึง REQ-9 ตาม traceability 183 criteria.

## Current Status

- Tasks 1–6 checked พร้อม Evidence ใน `tasks.md`.
- requirements, design และ tasks มีสถานะ approved.
- Build, offline tests, real-SQL integration, architecture, shell guards, secret scan และ REQ trace ผ่าน.
- Working tree ยัง dirty; ไม่มี commit, push, PR หรือ production deployment.

## Files Changed

| Path | Status | Summary |
|---|---|---|
| `.ai/specs/tier-0-microsoft-canonical-email/requirements.md` | new | Approved EARS requirements |
| `.ai/specs/tier-0-microsoft-canonical-email/design.md` | new | Approved design และ testing strategy |
| `.ai/specs/tier-0-microsoft-canonical-email/tasks.md` | new | Tasks 1–6 พร้อม Evidence |
| `.ai/specs/tier-0-microsoft-canonical-email/handoff.md` | new | Durable handoff นี้ |
| `.env.example` | edited | Admin Microsoft-only local config contract |
| `.env.prod.example` | edited | Required Admin Entra production config |
| `.github/workflows/ci.yml` | edited | Production Compose fixture ใช้ Admin Entra |
| `.gitlab-ci.yml` | edited | Production Compose fixture ใช้ Admin Entra |
| `Dockerfile` | edited | Build/copy workforce identity migrator |
| `docker-compose.prod.yml` | edited | Required Admin Microsoft config; retire Admin Google secrets |
| `docker/entrypoint.sh` | edited | Retire Admin Google secret export |
| `docker/migrate-entrypoint.sh` | edited | Run EF migration แล้ว workforce identity migrator |
| `docker/migrate-entrypoint.test.sh` | edited | Assert migrator sequencing/fail-closed exit |
| `docs/reference/admins.md` | edited | Canonical email Admin auth contract |
| `docs/runbooks/admin-workforce-jit-rollout.md` | edited | Backup, cutover, validation และ rollback cutoff |
| `docs/runbooks/deploy-self-host.md` | edited | Current Microsoft-only deployment steps |
| `docs/runbooks/local-dev-run.md` | edited | Current local Microsoft Admin setup |
| `pol-core.slnx` | edited | Add `WorkforceIdentityMigrator` project |
| `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/20260823132337_Tier0WorkforceEmailIdentity.cs` | new | Email key, snapshot/state และ guarded Down migration |
| `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/20260823132337_Tier0WorkforceEmailIdentity.Designer.cs` | new | EF migration metadata |
| `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/PolDbContextModelSnapshot.cs` | edited | Current workforce email model |
| `src/Hosts/Api/Admins/LoginService.cs` | edited | Canonical resolver dispatch และ privacy-safe logging |
| `src/Hosts/Api/Admins/MicrosoftWorkforceClaims.cs` | edited | Tenant/email precedence validation; ignore roles/oid |
| `src/Hosts/Api/Admins/OidcAuthentication.cs` | edited | Microsoft callback uses canonical subject |
| `src/Hosts/Api/Program.cs` | edited | New resolver wiring; retire pre-provision endpoint |
| `src/Hosts/Api/appsettings.json` | edited | Remove Admin Google provider |
| `src/Hosts/Api/appsettings.Development.json` | edited, ignored | Remove local Admin Google block; preserve all other local values |
| `src/Modules/Admins/Admins.Application/Users/JitProvisionMicrosoftAdmin.cs` | deleted | Replaced oid JIT path |
| `src/Modules/Admins/Admins.Application/Users/PreProvisionMicrosoftIdentity.cs` | deleted | Retired pre-provision handler |
| `src/Modules/Admins/Admins.Application/Users/ResolveMicrosoftAdmin.cs` | new | Atomic bind/JIT/conflict resolver |
| `src/Modules/Admins/Admins.Application/Users/UserPorts.cs` | edited | Candidate and recovery contracts |
| `src/Modules/Admins/Admins.Domain/Users/Audit.cs` | edited | Microsoft email bind audit action |
| `src/Modules/Admins/Admins.Domain/Users/User.cs` | edited | Workforce key invariant และ canonical JIT factory |
| `src/Modules/Admins/Admins.Domain/Users/WorkforceEmail.cs` | new | BCL canonicalizer/validator |
| `src/Modules/Admins/Admins.Infrastructure/Persistence/Users/UserConfigurations.cs` | edited | Nullable unique workforce key mapping |
| `src/Persistence/Persistence.ControlPlane/Admins/AdminIdentityAuditWriter.cs` | deleted | Retired pre-provision-only writer |
| `src/Persistence/Persistence.ControlPlane/Admins/ControlPlaneIdentityRecoveryReader.cs` | edited | Canonical email recovery lookup |
| `src/Persistence/Persistence.ControlPlane/Admins/UserConfiguration.cs` | edited | Control-plane workforce key mapping |
| `src/Persistence/Persistence.ControlPlane/Admins/UserRepository.cs` | edited | Tier 0 candidate query |
| `src/Persistence/Persistence.ControlPlane/Admins/WorkforceTenantBindingStore.cs` | edited | Completed-state startup invariant gate |
| `src/Persistence/Persistence.ControlPlane/ControlPlanePersistenceRegistration.cs` | edited | New resolver/recovery wiring |
| `src/Tools/WorkforceIdentityMigrator/Program.cs` | new | Transactional privileged subject migration tool |
| `src/Tools/WorkforceIdentityMigrator/WorkforceIdentityMigrator.csproj` | new | Tool project using existing dependencies |
| `tests/Admins.Tests/AdminAccountTests.cs` | edited | Canonical user invariant coverage |
| `tests/Admins.Tests/AdminFakes.cs` | edited | Candidate/recovery fakes |
| `tests/Admins.Tests/JitProvisionMicrosoftAdminTests.cs` | deleted | Replaced old oid JIT tests |
| `tests/Admins.Tests/PreProvisionMicrosoftIdentityTests.cs` | deleted | Retired surface tests |
| `tests/Admins.Tests/ResolveMicrosoftAdminTests.cs` | new | Bind/JIT/conflict/suspend/rollback coverage |
| `tests/Admins.Tests/WorkforceEmailTests.cs` | new | Canonicalizer edge coverage |
| `tests/Architecture.Tests/Architecture.Tests.csproj` | edited | Tool/test dependencies |
| `tests/Architecture.Tests/BypassPrimitiveTests.cs` | edited | Allowlist read-only startup invariant query |
| `tests/Architecture.Tests/EntraPreProvisionSqlIntegrationTests.cs` | deleted | Replaced old pre-provision SQL suite |
| `tests/Architecture.Tests/Tier0WorkforceArchitectureTests.cs` | new | Retired surface, privacy, tool และ Compose guards |
| `tests/Architecture.Tests/Tier0WorkforceIdentityMigrationSqlTests.cs` | new | Real-SQL migration/invariant tests |
| `tests/Architecture.Tests/TransactionInventoryTests.cs` | edited | New resolver transaction inventory |
| `tests/Architecture.Tests/WriteFloorTests.cs` | edited | New mutation seam allowlist |
| `tests/Governance.Tests/GovernanceStoreTests.cs` | edited | Updated user identity fixture |
| `tests/Hosts.Tests/AdminCallbackResolverInviteBindTests.cs` | edited | Canonical callback resolver coverage |
| `tests/Hosts.Tests/AdminLoginServiceTests.cs` | edited | Privacy/failure mapping coverage |
| `tests/Hosts.Tests/AdminMicrosoftIdentityEndpointTests.cs` | deleted | Retired endpoint tests |
| `tests/Hosts.Tests/MicrosoftAuthLoginRedirectTests.cs` | edited | Microsoft-only redirect contract |
| `tests/Hosts.Tests/MicrosoftOidcTests.cs` | edited | Tenant/email precedence and roles/oid-ignore coverage |
| `tests/Hosts.Tests/OidcCallbackE2ETests.cs` | edited | Canonical callback/session behavior |
| `tests/Hosts.Tests/RetiredCommerceRoutesTests.cs` | edited | Pre-provision route remains normal 404 |
| `tests/Integration.Tests/FreshBaselineMigrationIntegrationTests.cs` | edited | Current migration count/state expectations |

## Important Decisions

- Canonical identity ใช้ `MailAddress.TryCreate`, trim, ASCII/no-whitespace, exact `viriyah.co.th`, invariant lowercase และ max 254; ไม่เพิ่ม dependency.
- Microsoft `Subject` เท่ากับ canonical email. `oid` และ App Role ไม่ใช้ตัดสิน identity/eligibility.
- Resolve/bind/JIT ใช้ candidate set เดียวใต้ identity mutation lock; mutation และ audit commit transaction เดียว.
- Existing Active unbound owner bind ได้; suspended/divergent/duplicate owner fail closed; new JIT เป็น Active, Scoped, roleless.
- Migration tool ทำ full preflight ก่อน write, มี durable completed-state manifest และ startup revalidation.
- `Provider=microsoft, Subject=NULL` ยังเป็น valid unbound placeholder; Microsoft subject ต้อง canonical เมื่อ non-null.
- Admin Google และ oid pre-provision surface ถูก retire; unknown/retired route คง normal 404.
- Logs, browser errors และ tool console output ไม่เปิดเผย email, subject, token หรือ credential.

## Constraints

- ห้าม commit/push จน human review; ห้าม push ตรง `develop`.
- ห้าม reintroduce Admin Google, `vcp.employee`, oid identity หรือ retired pre-provision endpoint.
- ห้าม log/commit `.env.integration`, local `appsettings.Development.json` values หรือ secret files.
- Production migration ต้องผ่าน backup, maintenance window และ rollback cutoff ตาม runbook; ยังไม่ได้ authorize deploy.

## Tests Run

- `dotnet build pol-core.slnx --no-restore -warnaserror` -> passed, 0 warnings / 0 errors.
- `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"` -> 1,929 passed / 0 failed.
- `set -a; source .env.integration; set +a; dotnet test pol-core.slnx --filter "Category=Integration"` -> 176 passed / 0 failed: Integration 168, Architecture SQL 8.
- `bash docker/migrate-entrypoint.test.sh` -> 56 passed / 0 failed.
- Bash CI guard loop over `.claude/hooks/tests/*.test.sh`, Docker entrypoint tests and release-evidence test -> 10 scripts passed / 0 failed.
- `env -u POL_SA_PASSWORD bash docker/bootstrap/assert-fresh-db.test.sh` -> passed.
- `SECRET_GUARD_SKIP='' .ai/bin/check-secrets.sh --all` -> passed.
- `scripts/check-rename-identifiers.sh` -> passed.
- `scripts/spec-trace.sh tier-0-microsoft-canonical-email` -> 183/183 criteria covered; EARS lint passed.
- `dotnet ef migrations has-pending-model-changes --context PolDbContext --project src/BuildingBlocks/BuildingBlocks.Infrastructure --startup-project src/Hosts/Api --no-build` -> no pending model changes.
- `set -a; source .env.prod.example; set +a; docker compose -f docker-compose.prod.yml config -q` -> passed.
- `GATE_FILE=.ai/specs/tier-0-microsoft-canonical-email/tasks.md GATE_NEW="$(<.ai/specs/tier-0-microsoft-canonical-email/tasks.md)" .ai/bin/gate-task.sh` -> passed.
- `git diff --check` -> passed.

## Known Issues

- none ใน implementation/test scope.
- Production/staging deploy และ live Entra sign-in ไม่ได้รัน เพราะไม่มี authorization สำหรับ external deployment; runbook ระบุ acceptance/rollback steps แล้ว.

## Next Recommended Agent

Human/code reviewer ตรวจ diff และ migration safety. เมื่อ approve แล้วใช้ `ship-pr`; ห้าม merge โดยไม่มี CI.

## Next Steps

1. อ่าน spec ทั้งสามไฟล์และรัน `scripts/spec-state.sh tier-0-microsoft-canonical-email`.
2. Review diff บน branch `codex/tier-0-microsoft-canonical-email`, โดยเน้น canonical invariant, transaction lock และ migration Down/cutover.
3. เมื่อ review ผ่าน ให้ commit/push/open PR ผ่าน workflow ของ repo; รอ CI green ก่อน merge.
