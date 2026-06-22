# Implementation Tasks: Tenant Provisioning (control-plane)
> Status: approved 2026-06-22

> Each task is a cohesive, independently verifiable slice. Implement a whole task
> in one pass (it may touch many files). Sub-step sequencing handled at execution time.
> โหมด AFK autonomous: implement ต่อเนื่องบน branch feat/tenant-provisioning, TDD, commit ต่อ task, PR ไม่ merge เอง.

- [x] 1. Tenant domain + ADR — `Tenant` aggregate (`AggregateRoot<Guid>`, `Create()` validate invariant + Status=Active), `TenantStatus { Active }`, `TenantCode` (allowlist vcentral/vcommerce/vsouvenir + `Normalize()` lowercase + `IsAllowed()`); ADR `docs/adr/0001-tenant-provisioning-single-transaction.md` (single-tx valid เฉพาะ vault DB-backed; vault->KMS = trigger saga). Done = pure-domain unit tests เขียว.
     Satisfies: REQ-1.1, 1.2, 1.3, 1.5, 1.6, 1.7. Verify: `dotnet test tests/Tenant.Tests` (domain).
     Evidence: `dotnet test tests/Tenant.Tests` -> Passed 20, Failed 0. Tenant.Domain (Tenant/TenantStatus/TenantCode) + ADR 0001 + 4 csproj + slnx wired. build เขียว.

- [x] 2. Payments secret-envelope port + cross-module write seam — `IPspSecretEnvelopeFactory` (Payments.Application/Ports: validate `secretKey` required ต่อ psp -> ArgumentException, serialize provided secrets เป็น envelope JSON, คืน hint last-4 ต่อ field) + impl ใน Payments.Infrastructure/Psp; ขยาย `OmiseSecret` ถือ optional `PublicKey`/`WebhookSecret` (store-as-provided); `IPspConnectionRepository.Add(PspConnection)` + impl; `PspConnectionConfiguration.Metadata` -> `nvarchar(max)`. Done = envelope factory unit tests (required/optional/hint) เขียว + Payments build.
     Satisfies: REQ-3.7. Depends on: none (parallel กับ 1). Verify: `dotnet test tests/Payments.Tests`.
     Evidence: `dotnet test tests/Payments.Tests` -> Passed 55 (48 เดิม + 7 factory), Failed 0. OmiseSecret extension ไม่ break adapter. Metadata model -> nvarchar(max) (migration ใน task 5).

- [x] 3. Tenant.Application provisioning + audit (orchestrator) — `ITenantRepository` (Add/GetByCodeAsync/ExistsByCodeAsync), `IProvisioningAuditWriter` + `ProvisioningAudit`, `ProvisionTenant` command/result/handler (normalize+validate allowlist/psp/currency/non-empty/dup-psp -> envelope factory -> dup-check นอก tx ใต้ admin ctx -> `ExecuteInTransactionAsync`[idempotent-under-retry: entity/DEK ใน delegate, result var re-init ต้น delegate] -> Tenant+PspConnection+vault.StoreAsync+audit -> build masked result post-commit จาก input), `GetTenant` query/handler/`TenantView` (masked จาก PspConnection). Done = handler unit tests (fakes; รวม run-lambda-ซ้ำ พิสูจน์ idempotent) เขียว.
     Satisfies: REQ-2.1, 2.2, 2.3, 2.4, 3.1, 3.2, 3.3, 3.4, 3.5, 3.6, 4.1, 4.2, 4.3, 5.1, 6.1, 6.2, 6.3, 6.4, 6.5, 9.1, 9.3, 11.1, 11.3. Depends on: 1, 2. Verify: `dotnet test tests/Tenant.Tests` (handler).
     Evidence: `dotnet test tests/Tenant.Tests` -> Passed 30 (20 domain + 10 handler/query), Failed 0. ใช้ marker interfaces `IAdminUnitOfWork`/`IAdminVaultSecretStore`/`IAdminPspConnectionRepository` แทน keyed-string (compile-time กัน non-keyed leak; แก้ S2 ดีกว่าเดิม). สร้างล่วงหน้า: `BuildingBlocks.Application/ConflictException` (handler throw -> task 6 map 409) + `IPspConnectionRepository.ListByTenantAsync` (read-back). connMetadata = `{config, secretHints}` บน PspConnection.Metadata; read-back/masked อ่านจากที่นี่ ไม่แตะ vault.

- [ ] 4. Tenant.Infrastructure persistence — `TenantConfiguration` (ToTable Tenants, unique index Code, Status int, Metadata json), `TenantRepository`, `ProvisioningAuditConfiguration` + `ProvisioningAuditWriter`, `TenantModuleRegistration` (`AddTenantModule`). Done = builds + EF model สร้างได้ (no model error).
     Satisfies: REQ-1.4, 11.2 (persistence). Depends on: 3. Verify: `dotnet build` + EF model snapshot.

- [ ] 5. Central migration AddTenantTable + RLS + grants — เพิ่ม Tenant assembly เข้า `HostModuleAssemblies.All` + `RawConnectionTests.ProductionInfrastructure[]`; migration (timestamp ใหม่กว่า 20260622022145): CreateTable `Tenants` + `ProvisioningAudits` + unique index `Code` + `Sql()` ALTER SECURITY POLICY (FILTER+BLOCK `fn_tenant_predicate(Id)` บน Tenants) + grants (pol_app SELECT Tenants; pol_admin SELECT/INSERT/UPDATE Tenants, INSERT PspConnections, INSERT VaultSecrets, SELECT/INSERT ProvisioningAudits) + `Down` ครบ (revoke + DROP 1 FILTER+2 BLOCK + drop tables + revert Metadata len). Done = `dotnet ef migrations add AddTenantTable` สำเร็จ + migration apply ได้ (ถ้ามี DB).
     Satisfies: REQ-8.1, 8.4, 1.4 (DB-level). Depends on: 4. Verify: `dotnet ef migrations script` + Integration RLS (task 7).

- [ ] 6. Api host wiring + admin endpoints + ConflictException — connection string `Admin` (pol_admin) + keyed `ProducerDbContext "admin"` (ไม่ผูก interceptor) + keyed admin `IUnitOfWork`/repos/`IVaultSecretStore`/`IProvisioningAuditWriter` + boot fail-fast เมื่อขาด `Admin` conn; `AddTenantModule()`; endpoints `POST /admin/tenants` (ProvisionTenantCommand, not ITenantScoped) + `GET /admin/tenants/{code}` masked, ทั้งคู่ `RequireAuthorization("admin")` + request/response records; `ConflictException : Exception` + map 409 ใน `ProblemDetailsExceptionHandler` (arm ก่อน InvalidOperationException). Done = build + endpoints ตอบถูก status.
     Satisfies: REQ-2.5, 2.6, 4.4, 5.2, 7.1, 7.2, 7.3, 7.4, 9.1, 9.2. Depends on: 3, 4, 5. Verify: `dotnet test tests/Hosts.Tests`.

- [ ] 7. Verification suites — Integration.Tests (real SQL, มิเรอร์ RlsIsolationTests): pol_admin insert Tenant+children+audit 1 tx; pol_app เห็นเฉพาะแถวตัวเอง + insert id อื่นไม่ได้; vault-fail-mid-loop -> rollback 0 แถว; masked read = ****hint ไม่ plaintext + pol_admin ไม่ SELECT VaultSecrets; dup code -> 409 + ไม่มีแถวที่สอง. Hosts.Tests: 401/403/201 + 404 + masked body. Architecture.Tests: Tenant.Domain no EF, Tenant.* no Host, keyed-deps fact. Done = `dotnet test pol-core.slnx` เขียว (integration skip ได้ถ้าไม่มี DB แต่ unit/arch/hosts ต้องเขียว).
     Satisfies: REQ-4.2, 5.3, 8.2, 8.3, 8.5, 9.2, 10.1, 10.2. Depends on: 6. Verify: `dotnet test pol-core.slnx`.

- [ ] 8. Config + prerequisite (REQ-7.5) — appsettings.json/Development.json + .env.example: เพิ่ม `ConnectionStrings:Admin` (placeholder ค่าปลอม, ไม่ commit secret จริง) + `Google:Audiences` admin client (placeholder) + เอกสารสั้นใน design/README ว่าต้อง provision admin OAuth client + pol_admin login ก่อนใช้งานจริง. Done = build + config validation pass + ไม่มี secret จริงใน repo.
     Satisfies: REQ-7.5. Depends on: 6. Verify: `dotnet build` + `git diff` ยืนยันไม่มี secret.

## Suggested execution batches

> COUPLED feature (share Tenant/PspConnection/vault primitives) -> DEFAULT = all-in-one session (`/spec-implement all`).
> Task 1 และ 2 อิสระต่อกัน (domain vs Payments port) -> รันก่อน/ขนานได้. 3 รวมผลของ 1+2. 4->5->6->7 เป็นสายพึ่งกัน. 8 หลัง 6.
> AFK autonomous: implement เรียง 1,2 -> 3 -> 4 -> 5 -> 6 -> 7 -> 8, commit ต่อ task, รัน gate (build/test) ก่อน flip [x].
