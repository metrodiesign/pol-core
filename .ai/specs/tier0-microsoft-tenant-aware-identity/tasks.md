# Implementation Tasks: Tier 0 Microsoft Tenant-Aware Immutable Identity

> Status: approved 2026-09-02
> Status-Note: amended and approved 2026-09-02 — no-email/offline-manifest tasks

> Each task is a cohesive, independently verifiable slice. Implement a whole task
> in one pass (it may touch many files). Decompose into sub-steps yourself at
> execution time — do NOT pre-split tasks here.

- [x] 1. **Persisted no-email cutover and mandatory offline mapper** — เพิ่ม final User/EF identity shape,
     nullable non-unique Email, final-state policy, tenant FK/triple indexและ manifest state/snapshot tables แล้วสร้าง
     forward migrationต่อจาก `20260830172117_Tier0EmployeeProfile` ที่ prevalidate old key state, drop
     WorkforceEmailKey/Email ownership indexesและมี guarded Down พร้อม implement mandatory migratorแบบ strict
     AdminId manifest, SHA-256/exact-target/evidence, ordered tenant initialization, system-actor audits,
     first-run snapshotและ no-manifest rerun รวม startup final-state verifier Done = old pending/completed DB mapได้
     atomicallyโดยไม่ fabricate oid, invalid input/rollback failก่อน partial write, existing Admin/profile/RBAC/session/
     audit preserved และ schema/snapshot/bootstrapตรงกันโดยไม่แก้ migrationเดิมหรือ migrate entrypoint
     Satisfies: REQ-2.5-REQ-2.18, REQ-2.24-REQ-2.25, REQ-2.27-REQ-2.40, REQ-4, REQ-7
     Verify: `dotnet build pol-core.slnx --no-restore -warnaserror`; `dotnet test tests/Admins.Tests/Admins.Tests.csproj --filter "FullyQualifiedName~MicrosoftWorkforceIdentityPolicyTests|FullyQualifiedName~AdminContactEmailTests|FullyQualifiedName~PlatformUserTests|FullyQualifiedName~WorkforceTenantBindingTests"`; `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --filter "FullyQualifiedName~WorkforceTenantBindingStoreTests|FullyQualifiedName~Tier0WorkforceIdentityMigrationSqlTests"`; `dotnet test tests/Integration.Tests/Integration.Tests.csproj --filter "FullyQualifiedName~Tier0MicrosoftTenantAwareIdentityMigrationTests|FullyQualifiedName~FreshBaselineMigrationIntegrationTests"`; `scripts/check-migration-script.sh`.
     Evidence:
       - requirements: `REQ-2.5-REQ-2.18`, `REQ-2.24-REQ-2.25`, `REQ-2.27-REQ-2.40`, `REQ-4`, `REQ-7` implemented and traced
       - build: `dotnet build pol-core.slnx --no-restore -warnaserror` -> succeeded, 0 warnings / 0 errors
       - test: `dotnet test tests/Admins.Tests/Admins.Tests.csproj --no-build --filter 'FullyQualifiedName~MicrosoftWorkforceIdentityPolicyTests|FullyQualifiedName~AdminContactEmailTests|FullyQualifiedName~PlatformUserTests|FullyQualifiedName~WorkforceTenantBindingTests'` -> 31 passed / 0 failed
       - test: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --no-build --filter 'FullyQualifiedName~Tier0WorkforceIdentityMigrationSqlTests|FullyQualifiedName~WorkforceTenantBindingStoreTests'` -> 12 passed / 0 failed; real SQL Server mapper first-run/rerun/concurrency/atomic-failure included
       - test: `dotnet test tests/Integration.Tests/Integration.Tests.csproj --no-build --filter 'FullyQualifiedName~Tier0MicrosoftTenantAwareIdentityMigrationTests|FullyQualifiedName~FreshBaselineMigrationIntegrationTests.Fresh_baseline_applies_and_rolls_back_in_dependency_safe_order'` -> 8 passed / 0 failed; empty idempotent script, upgrade, metadata and guarded Down included
       - test: `dotnet test pol-core.slnx --no-build --filter 'Category!=Integration'` -> 2033 passed / 0 failed
       - migration: `scripts/check-migration-script.sh` -> schema script drift gate OK; `env -u POL_SA_PASSWORD bash docker/bootstrap/assert-fresh-db.test.sh` -> OK
       - security: `.ai/bin/check-secrets.sh --all` -> exit 0
       - viewports: n/a — backend/database task
       - deviations: none from approved design; compile-forward adaptations needed by the final nullable tuple landed in approved Tasks 2-3 files, but Tasks 2-3 remain unchecked pending their dedicated verification

- [x] 2. **Validated email-optional callback and exact-only JIT/session** — เปลี่ยน Admin Microsoft claims,
     OIDC/LoginService/application/repository/recoveryเป็น typed exact-one validated `tid+oid`, optional Email
     trim/length-onlyและ resolution exact tuple→existing/Suspendedหรือ roleless JITเท่านั้น พร้อม generic
     non-Microsoft lookupที่เห็นเฉพาะ NULL tenantและไม่มี candidate/email/EmployeeId fallback Done = missing/
     duplicate/malformed claimsและ wrong tenant denyก่อน Graph/DB/session, email-less/renamed login resolveหรือ JIT,
     same Email different tuplesไม่ bind/conflict, same oid foreign tenantไม่ข้ามและ session/RBAC behaviorเดิม
     Satisfies: REQ-1, REQ-2.1-REQ-2.4, REQ-2.6-REQ-2.7, REQ-2.16-REQ-2.23, REQ-3
     Depends on: 1
     Verify: `dotnet build pol-core.slnx --no-restore -warnaserror`; `dotnet test tests/Admins.Tests/Admins.Tests.csproj --filter "FullyQualifiedName~ResolveMicrosoftAdminTests|FullyQualifiedName~AdminContactEmailTests|FullyQualifiedName~PlatformUserTests|FullyQualifiedName~AdminHandlerTests"`; `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --filter "FullyQualifiedName~MicrosoftOidcTests|FullyQualifiedName~OidcCallbackE2ETests|FullyQualifiedName~AdminLoginServiceTests|FullyQualifiedName~AdminCallbackResolverInviteBindTests|FullyQualifiedName~AdminGraphEmployeeProfileE2ETests|FullyQualifiedName~MicrosoftAuthLoginRedirectTests"`; `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --filter "FullyQualifiedName~Tier0WorkforceArchitectureTests|FullyQualifiedName~WorkforceTenantBindingStoreTests|FullyQualifiedName~PreBindWritePortTests"`; `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"`.
     Evidence:
       - requirements: `REQ-1`, `REQ-2.1-REQ-2.4`, `REQ-2.6-REQ-2.7`, `REQ-2.16-REQ-2.23`, `REQ-3` implemented; startup/final-state clauses rely on completed Task 1
       - build: `dotnet build pol-core.slnx --no-restore -warnaserror` -> succeeded, 0 warnings / 0 errors
       - test: `dotnet test tests/Admins.Tests/Admins.Tests.csproj --no-build --filter 'FullyQualifiedName~ResolveMicrosoftAdminTests|FullyQualifiedName~AdminContactEmailTests|FullyQualifiedName~PlatformUserTests|FullyQualifiedName~AdminHandlerTests'` -> 72 passed / 0 failed
       - test: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --no-build --filter 'FullyQualifiedName~MicrosoftOidcTests|FullyQualifiedName~OidcCallbackE2ETests|FullyQualifiedName~AdminLoginServiceTests|FullyQualifiedName~AdminCallbackResolverInviteBindTests|FullyQualifiedName~AdminGraphEmployeeProfileE2ETests|FullyQualifiedName~MicrosoftAuthLoginRedirectTests'` -> 110 passed / 0 failed; real OIDC middleware signature/issuer/audience/nonce/lifetime/state/code-exchange and claim-order paths included
       - test: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --no-build --filter 'FullyQualifiedName~Tier0WorkforceArchitectureTests|FullyQualifiedName~WorkforceTenantBindingStoreTests|FullyQualifiedName~PreBindWritePortTests'` -> 23 passed / 0 failed; real EF exact-tuple/generic-isolation query included
       - test: `dotnet test pol-core.slnx --no-build --filter 'Category!=Integration'` -> 2060 passed / 0 failed
       - security: `.ai/bin/check-secrets.sh --all` -> exit 0; Admin fallback static scan -> no runtime `WorkforceEmailKey`, `preferred_username`, UPN or email-identity fallback reference
       - viewports: n/a — backend authentication task
       - deviations: none

- [x] 3. **Verified pre-bound invite and nullable Admin wire** — เปลี่ยน `CreateScopedAdmin` กับ narrow
     `src/Hosts/Api/Program.cs` blocksให้รับ required canonical ObjectIdจาก verified Entra export, bounded approval
     referenceและ optional Email, derive singleton tenant, reject exact tuple duplicateแล้ว create Active/Scoped
     pre-bound User + existing auditใน transactionเดิม พร้อม propagate nullable Emailผ่าน create/me/list/detail/OpenAPI
     โดยไม่เปลี่ยน route, CSRF, tier authorizationหรือ profile FK behavior Done = first login exact tupleได้ AdminIdเดิม,
     duplicate Emailอยู่ร่วมกันได้, malformed/evidence-less inviteไม่มี write และ Program diffไม่แตะ surfaceอื่น
     Satisfies: REQ-2.8-REQ-2.9, REQ-2.26, REQ-2.41-REQ-2.47
     Depends on: 1, 2
     Verify: `dotnet build pol-core.slnx --no-restore -warnaserror`; `dotnet test tests/Admins.Tests/Admins.Tests.csproj --filter "FullyQualifiedName~CreateScopedMicrosoftAdminTests|FullyQualifiedName~AdminHandlerTests|FullyQualifiedName~ProfileValidationTests|FullyQualifiedName~PlatformUserTests"`; `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --filter "FullyQualifiedName~AdminMicrosoftInviteEndpointTests|FullyQualifiedName~AdminAccountManagementEndpointTests|FullyQualifiedName~AdminCallbackResolverInviteBindTests|FullyQualifiedName~AudienceOpenApiDocumentTests.Admin_prebound_invite"`; `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --filter "FullyQualifiedName~Tier0WorkforceArchitectureTests|FullyQualifiedName~ModelDisjointnessTests"`; `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"`.
     Evidence:
       - requirements: `REQ-2.8-REQ-2.9`, `REQ-2.26`, `REQ-2.41-REQ-2.47` implemented; singleton FK enforcement relies on completed Task 1
       - build: `dotnet build pol-core.slnx --no-restore -warnaserror` -> succeeded, 0 warnings / 0 errors
       - test: `dotnet test tests/Admins.Tests/Admins.Tests.csproj --no-build --filter 'FullyQualifiedName~CreateScopedMicrosoftAdminTests|FullyQualifiedName~AdminHandlerTests|FullyQualifiedName~ProfileValidationTests|FullyQualifiedName~PlatformUserTests'` -> 77 passed / 0 failed; approval bounds, no-write failures, duplicate Email, exact conflict and first exact login included
       - test: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --no-build --filter 'FullyQualifiedName~AdminMicrosoftInviteEndpointTests|FullyQualifiedName~AdminAccountManagementEndpointTests|FullyQualifiedName~AdminCallbackResolverInviteBindTests|FullyQualifiedName~AudienceOpenApiDocumentTests.Admin_prebound_invite'` -> 15 passed / 0 failed; auth/CSRF/Super gates, malformed Guid, nullable JSON and required/optional OpenAPI shape included
       - test: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --no-build --filter 'FullyQualifiedName~Tier0WorkforceArchitectureTests|FullyQualifiedName~ModelDisjointnessTests'` -> 12 passed / 0 failed
       - test: `dotnet test pol-core.slnx --no-build --filter 'Category!=Integration'` -> 2082 passed / 0 failed
       - scope: `Program.cs` zero-context diff gate -> 8 hunks, all inside approved Admin create/request/nullable-response blocks
       - security: `.ai/bin/check-secrets.sh --all` -> exit 0
       - viewports: n/a — backend/API contract task
       - deviations: no behavioral deviation; `CreateAdminRequest` declares required approval reference before `Email = null` so OpenAPI correctly publishes Email as optional while JSON property names and approved wire semantics remain unchanged

- [x] 4. **Employee-profile atomicity and real tuple races** — rebase employee-profile compositionบน exact
     tenant-aware commandโดยไม่เปลี่ยน Graph/EmployeeId/HR/Office/Division semantics แล้วเพิ่ม real SQL Server
     handler/repository/UoW/applock testsสำหรับ same-tuple JIT, different tuplesที่ใช้ Emailเดียวกัน, direct unique
     winner/fresh recovery, concurrent mapper, profile switch on/offและ denial rollback Done = JIT/profile/UserAudits
     commitชุดเดียว, failureไม่เหลือ mutation/session, EmployeeIdยัง global HR conflict, Version bumpตาม identity/
     profile contractและ AuthorizationVersionคงเดิม
     Satisfies: REQ-5, REQ-6
     Depends on: 2, 3
     Verify: `dotnet build pol-core.slnx --no-restore -warnaserror`; `dotnet test tests/Admins.Tests/Admins.Tests.csproj --filter "FullyQualifiedName~ResolveMicrosoftAdminEmployeeProfileTests|FullyQualifiedName~UserEmployeeProfileTests|FullyQualifiedName~ResolveMicrosoftAdminTests"`; `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --filter "FullyQualifiedName~Tier0EmployeeProfileTransactionTests|FullyQualifiedName~Tier0WorkforceIdentityMigrationSqlTests"`; `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --filter "FullyQualifiedName~AdminGraphEmployeeProfileE2ETests|FullyQualifiedName~AdminLoginServiceTests"`; `dotnet test tests/Integration.Tests/Integration.Tests.csproj --filter "FullyQualifiedName~Tier0MicrosoftTenantAwareIdentityMigrationTests"`; `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"`.
     Evidence:
       - requirements: `REQ-5` and `REQ-6` implemented and covered across exact resolver, profile composition, session denial and SQL concurrency paths
       - build: `dotnet build pol-core.slnx --no-restore -warnaserror` -> succeeded, 0 warnings / 0 errors
       - test: `dotnet test tests/Admins.Tests/Admins.Tests.csproj --no-build --filter 'FullyQualifiedName~ResolveMicrosoftAdminEmployeeProfileTests|FullyQualifiedName~UserEmployeeProfileTests|FullyQualifiedName~ResolveMicrosoftAdminTests'` -> 37 passed / 0 failed
       - test: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --no-build --filter 'FullyQualifiedName~AdminGraphEmployeeProfileE2ETests|FullyQualifiedName~AdminLoginServiceTests'` -> 56 passed / 0 failed; profile switch on/off, denial-without-session and fresh denied-audit behavior included
       - test: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --no-build --filter 'FullyQualifiedName~Tier0EmployeeProfileTransactionTests|FullyQualifiedName~Tier0WorkforceIdentityMigrationSqlTests'` -> 12 passed / 0 failed against scratch SQL Server; same-tuple profile JIT, shared-Email distinct tuples, direct unique winner/fresh recovery, global EmployeeId race, rollback and concurrent mapper included
       - test: `dotnet test tests/Integration.Tests/Integration.Tests.csproj --no-build --filter 'FullyQualifiedName~Tier0MicrosoftTenantAwareIdentityMigrationTests'` -> 7 passed / 0 failed against scratch SQL Server
       - test: `dotnet test pol-core.slnx --no-build --filter 'Category!=Integration'` -> 2082 passed / 0 failed
       - fixtures: generated scratch database names, synthetic identities/profile data, `Pooling=false` and guarded per-test cleanup; no production query
       - security: `.ai/bin/check-secrets.sh --all` -> exit 0; test skip/duplicate-attribute scan -> OK
       - viewports: n/a — backend/concurrency task
       - deviations: none; the pre-existing employee-profile SQL harness was rebased from removed `WorkforceEmailKey`/runtime-bind assumptions to the approved final tuple and mandatory-state initialization

- [x] 5. **Operational cutover, privacy and regression boundary** — update Admin Microsoft runbooksด้วย
     authoritative export/manifest format, digest/exact target/evidence, no-mixed-version maintenance, aggregate
     completion, ephemeral cleanup, guarded rollback/forward recoveryและ multi-tenant blockers พร้อม static/
     architecture/privacy/bootstrap gatesที่บังคับ no WorkforceEmailKey/current candidate symbols, exact tuple writers,
     aggregate-only tool output, no PII/exception loggingและ unchanged Merchant/Admin-Google/session behavior Done =
     docsไม่มี stale canonical-email/fallback guidance, only migration-file token allowlist remains, bootstrap mutation
     testsจับ metadata drift, Merchant suitesไม่ regressและไม่มี real PII/secret/`.Skip`
     Satisfies: REQ-8, REQ-9
     Depends on: 1, 2, 3, 4
     Verify: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --filter "Category!=Integration"`; `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --filter "Category!=Integration"`; `docker/bootstrap/assert-fresh-db.test.sh`; `.ai/bin/check-secrets.sh --all`.
     Evidence:
       - requirements: `REQ-8` and `REQ-9` documented and enforced across tenant pinning, privacy, authorization/session preservation and regression gates
       - docs: authoritative export, strict three-field manifest, digest/exact target/evidence, maintenance window, aggregate completion, ephemeral deletion, pre-bound invite, forward recovery and tenant-registry/EmployeeId blockers recorded in `docs/runbooks/admin-workforce-jit-rollout.md`, `docs/runbooks/admin-microsoft-oidc.md`, deployment/local runbooks and Admin reference
       - build: `dotnet build pol-core.slnx --no-restore -warnaserror` -> succeeded, 0 warnings / 0 errors
       - test: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --no-build --filter 'Category!=Integration'` -> 312 passed / 0 failed; current-token allowlist, candidate/writer/privacy/output/dependency/docs/no-skip gates included
       - test: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --no-build --filter 'Category!=Integration'` -> 683 passed / 0 failed; Admin Google retirement, Merchant OIDC and Admin session/CSRF/audit regressions included
       - test: `dotnet test tests/Admins.Tests/Admins.Tests.csproj --no-build` -> 222 passed / 0 failed
       - test: `dotnet test tests/Merchants.Tests/Merchants.Tests.csproj --no-build` -> 181 passed / 0 failed; `dotnet test tests/Iam.Tests/Iam.Tests.csproj --no-build` -> 63 passed / 0 failed
       - test: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --no-build --filter 'FullyQualifiedName~Tier0WorkforceIdentityMigrationSqlTests'` -> 6 passed / 0 failed against generated scratch SQL Server databases; aggregate-only output and value/path canaries included
       - test: `bash docker/migrate-entrypoint.test.sh` -> 59 passed / 0 failed; schema-before-tool order, hard failure and redacted DB-target output included
       - test: `env -u POL_SA_PASSWORD bash docker/bootstrap/assert-fresh-db.test.sh` -> `assert-fresh-db.test: OK`; mutation copies proved removal of Email/key absence, state-table and tuple-index assertions turns the gate red
       - test: `scripts/check-migration-script.sh` -> schema script drift gate OK; production compose render with synthetic placeholders -> exit 0
       - security: `.ai/bin/check-secrets.sh --all` -> exit 0; stale identity guidance scan -> OK; dependency/project diff -> none
       - operations: migrator now builds its privileged connection in-process from existing deployment inputs when `POL_DESIGN_SQL` is absent, without changing `docker/migrate-entrypoint.sh` schema/tool invocation order or emitting the composed value
       - fixtures: reserved domains, synthetic GUID/profile values and guarded scratch databases only; no production query
       - viewports: n/a — operations/backend task
       - deviations: none

- [x] 6. **Assembly and final verification** — reconcileทุก requirementกับ implementation/tests, inspect dirty-tree
     ownershipเพื่อยืนยันว่า employee-profile workไม่ถูก revert/overwrite, regenerate/check schemaและ manifest
     migration metadata one final time แล้วรัน exact build/unit/integration/security/spec commandsพร้อมบันทึก observed
     resultทุกคำสั่ง Done = 452 criteriaมี design/task/code/test traceครบ, no skipped testหรือ Merchant regression,
     WorkforceEmailKeyไม่มีใน final current source/schemaนอก migration allowlist และสิ่งที่รันไม่ได้ถูก report
     blocked/unverifiedแทน claim pass
     Satisfies: REQ-10
     Depends on: 1, 2, 3, 4, 5
     Verify: `dotnet build pol-core.slnx --no-restore -warnaserror`; `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"`; `dotnet test pol-core.slnx --filter "Category=Integration"`; `scripts/check-migration-script.sh`; `.ai/bin/check-secrets.sh --all`; `scripts/spec-trace.sh tier0-microsoft-tenant-aware-identity`.
     Evidence:
       - requirements: all `REQ-10.1` through `REQ-10.107` reconciled with automated claims/callback, exact resolver/JIT/invite, profile/race, mapper, SQL migration, regression, static/privacy and documentation coverage; all feature criteria remain traced `452/452`
       - coverage: final assembly added explicit missing/incomplete/duplicate/foreign manifest rollback, mismatched tenant singleton, exact state-table columns, completed historical-key drift, alternate-key duplication, unknown-tenant FK, non-Microsoft/null-subject constraints and migration preservation of AdminId/Email/status/tier/roles/access/profile/session/audits/config rows
       - regression: rebased stale live-SQL assertions from global Email uniqueness and `IX_Users_Provider_Subject` to nullable duplicate contact and final `(Provider,TenantId,Subject)` metadata
       - build: `dotnet build pol-core.slnx --no-restore -warnaserror` -> succeeded, 0 warnings / 0 errors
       - test: `dotnet test pol-core.slnx --no-build --filter 'Category!=Integration'` -> 2090 passed / 0 failed / 0 skipped, including Admin, Merchant, Iam, Host/session/CSRF and architecture/privacy regressions
       - test: `dotnet test pol-core.slnx --filter 'Category=Integration'` -> 196 passed / 0 failed / 0 skipped (`Architecture.Tests` 17 + `Integration.Tests` 179) against a generated scratch core database and local synthetic upstream simulations; scratch database cleanup verified
       - test: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --no-build --filter 'FullyQualifiedName~Tier0WorkforceIdentityMigrationSqlTests'` -> 8 passed / 0 failed
       - test: `dotnet test tests/Integration.Tests/Integration.Tests.csproj --no-build --filter 'FullyQualifiedName~Tier0MicrosoftTenantAwareIdentityMigrationTests'` -> 9 passed / 0 failed
       - schema: `scripts/check-migration-script.sh --write` -> regenerated byte-identical script; `scripts/check-migration-script.sh` -> drift gate OK
       - schema: `dotnet ef migrations has-pending-model-changes --context PolDbContext --project src/BuildingBlocks/BuildingBlocks.Infrastructure --startup-project src/Hosts/Api` -> no model changes since latest migration
       - schema: `env -u POL_SA_PASSWORD bash docker/bootstrap/assert-fresh-db.test.sh` -> `assert-fresh-db.test: OK`; full-suite scratch bootstrap also passed live `assert-fresh-db.sql`
       - operations: `bash docker/migrate-entrypoint.test.sh` -> 59 passed / 0 failed; `./scripts/check-migration-lineage.sh` -> OK
       - security: `.ai/bin/check-secrets.sh --all` -> exit 0; test skip scan -> OK; dependency/project diff -> none; ephemeral manifest residue -> none
       - scope: `Program.cs` zero-context approved-scope gate -> 8 hunks; all remain inside Admin create/request/nullable-response blocks
       - ownership: no deleted employee-profile artifact; predecessor migration/designer remain present and current designer retains EmployeeId/name/profile FKs/indexes; no reset, restore or cleanup of dirty employee-profile work
       - static: retired `WorkforceEmailKey` token remains only in immutable historical migration artifacts and the approved drop/rollback migration source; live HEAD metadata tests prove column/index absence
       - trace: `scripts/spec-trace.sh tier0-microsoft-tenant-aware-identity` and strict `spec_contract.py` -> 452/452, EARS lint passed
       - diagnostics: first full-suite setup stopped before tests because the local self-signed SQL certificate rejected production-grade trust; rerun used the local design connection. The next run exposed two stale pre-cutover integration assertions; both were corrected and the final exact run above passed
       - fixtures: generated guarded scratch databases and synthetic GUID, Email and employee/profile data only; no production query, deploy, commit, push or PR
       - viewports: n/a — backend/operations assembly
       - deviations: none

## Suggested execution batches

> ฟีเจอร์นี้ coupledสูง: nullable Email/domain tuple, EF migration, offline mapper, startup, resolver, inviteและ
> employee profileแชร์ identity invariantเดียวกัน Defaultคือ implement task 1-6แบบ all-in-oneตามลำดับ
> Separate sessionเพิ่ม cold-context costและความเสี่ยง overwrite dirty employee-profile work

```bash
scripts/pane-loop.sh tier0-microsoft-tenant-aware-identity all-in-one
```

ไม่มี `Batch:` group: task 1-4เป็น foundational/critical pathและ task 5-6ต้องเห็น integrated treeหลังทุกส่วน land

## Implementation constraints

- เริ่มทุก taskด้วย `scripts/spec-state.sh tier0-microsoft-tenant-aware-identity` และ reconcile filesystem/
  untracked filesก่อนเชื่อ checkbox
- ห้ามแก้หรือลบ migration `20260830172117_Tier0EmployeeProfile` และห้าม cleanup dirty employee-profile work
- scope expansionอนุญาต `src/Tools/WorkforceIdentityMigrator/Program.cs` และเฉพาะ Admin create/request/nullable
  response blocksใน `src/Hosts/Api/Program.cs`; ห้ามแตะ Program surfaceอื่น
- คง `docker/migrate-entrypoint.sh` และ invocation orderเดิม; first-run manifest inputsส่งแบบ ephemeralจาก operator
- ห้ามใช้ email, preferred_username, UPN, WorkforceEmailKeyหรือ EmployeeIdใน Microsoft auth decisionทุกชนิด
- ห้าม log token, tid, oid, Email, EmployeeId, manifest path/content/digest/evidence/targetหรือ response body
- integrationใช้ scratch SQL Server databaseและ synthetic dataเท่านั้น; ห้าม query productionหรือ dump HR rows
- taskที่แตะ codeต้องรัน build + relevant non-integration tests; taskที่แตะ DBต้องรัน relevant real SQL Server tests
- checkbox `[x]` และ `Evidence:` ต้องแก้ใน editเดียวกันหลัง observed verificationผ่าน
- ห้ามเพิ่ม dependency, commit, push, PR หรือ deploy
