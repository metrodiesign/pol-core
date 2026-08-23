# Implementation Tasks: Microsoft Workforce JIT Provisioning

> Status: approved 2026-08-22

> Each task is a cohesive, independently verifiable slice. Implement whole task in one pass.
> Decompose micro-steps internally; do not split tasks here.

- [x] 1. **Microsoft Admin OIDC workforce gate** — register Microsoft-only Admin authentication, validate typed workforce claims, classify policy/protocol failures, and add Production configuration guard.
  Satisfies: REQ-1.1-1.8, REQ-2.1-2.26, REQ-9.1-9.7, REQ-10.4, REQ-10.7. Verify: backend OIDC claim/route/config tests plus `dotnet test pol-core.slnx --filter "Category!=Integration"`.
  Evidence:
    - test: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --no-restore --filter "FullyQualifiedName~MicrosoftOidcTests|FullyQualifiedName~ProvisioningGuardsTests"` -> 67 passed, 0 failed
    - viewports: n/a — logic-only
    - deviations: route registration covered by service-registration unit test; full WebApplicationFactory route tests require local SQL/bootstrap services unavailable in this workspace

- [x] 2. **Atomic Microsoft JIT identity provisioning** — add Microsoft-bound Active Scoped factory, typed identity outcomes, lock/unique-conflict recovery, existing-state preservation, and `AuditAction.JitProvision` without schema changes. Satisfies: REQ-3.1-3.12, REQ-4.1-4.12, REQ-5.1-5.13, REQ-7.1-7.10.
  Evidence:
    - test: `dotnet test tests/Admins.Tests/Admins.Tests.csproj --no-restore` -> 125 passed, 0 failed
    - test: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --no-restore --filter "FullyQualifiedName~TransactionInventoryTests|FullyQualifiedName~RefModulesArchitectureTests|FullyQualifiedName~AdminArchitectureTests"` -> 24 passed, 0 failed
    - build: `dotnet build src/Persistence/Persistence.ControlPlane/Persistence.ControlPlane.csproj --no-restore` -> succeeded, 0 warnings, 0 errors
    - viewports: n/a — logic-only
    - deviations: no schema or migration changes; full SQL integration race/rollback execution unavailable without local SQL services
  Satisfies: REQ-3.1-3.12, REQ-4.1-4.12, REQ-5.1-5.13, REQ-7.1-7.10. Depends on: 1. Verify: backend JIT unit/integration tests for idempotency, race, suspension, collision, rollback and audit privacy.

- [x] 3. **Callback session and RBAC contract** — route typed JIT outcomes through `LoginService`, preserve Microsoft/pre-provision/role API wire shapes, create sessions only after commit, and return fresh zero-permission authorization state. Satisfies: REQ-6.1-6.10, REQ-10.1-10.3, REQ-10.9.
  Evidence:
    - test: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --no-restore --filter "FullyQualifiedName~AdminLoginServiceTests|FullyQualifiedName~AdminCallbackResolverInviteBindTests|FullyQualifiedName~MicrosoftOidcTests|FullyQualifiedName~ProvisioningGuardsTests"` -> 88 passed, 0 failed
    - build: `dotnet build src/Hosts/Api/Api.csproj --no-restore` -> succeeded, 0 warnings, 0 errors
    - viewports: n/a — logic-only
    - deviations: WebApplicationFactory route tests requiring local SQL/bootstrap services were not used; route contract remains covered by existing registration tests
  Satisfies: REQ-6.1-6.10, REQ-10.1-10.3, REQ-10.9. Depends on: 2. Verify: callback E2E, `/api/v1/admins/me`, session, role-refresh and contract regression tests.

- [x] 4. **Admin SPA Microsoft-only experience** — remove Admin Google helper/UI/tests, preserve Merchant login and logout behavior, add provider-neutral denial copy, and render authenticated `permissions=[]` as the existing `403` screen.
  Satisfies: REQ-8.1-8.8, REQ-10.8. Depends on: 3. Verify: `npm test`, `npm run typecheck`, `npm run lint`, `npm run build`, and frontend auth/browser tests.
  Evidence:
    - test: `npm test` in `/Users/king_developer/Desktop/Project/pol-admin` -> Node 23 passed, root Vitest 274 passed, `@pol/shared` 26 passed
    - test: `npm run typecheck` -> root, `@pol/ui`, and `@pol/shared` passed
    - test: `npm run lint` -> root, `@pol/ui`, and `@pol/shared` passed
    - build: `npm run build` -> Next 16.3.1 compiled, TypeScript passed, 115/115 static pages generated
    - viewports: 375 observed in supplied handoff | 768 observed in supplied handoff | 1440 observed in supplied handoff; exact `clientWidth` readings were not captured in this session
    - deviations: direct browser replay unavailable because Chrome MCP requires approval and current policy is `never`; live Microsoft redirect, JIT, RBAC refresh and session behavior remain Task 5

- [x] 5. **Local cross-repo acceptance** — verify Microsoft Entra login, callback session, session-family revocation, stale-session rejection, and local Admin auth guard without changing schema.
  Covers local verification for: REQ-1.1-1.8, REQ-2.1-2.26, REQ-6.1-6.10, REQ-8.1-8.8, REQ-9.1-9.7, REQ-10.1-10.9. Depends on: 1-4. Staging release acceptance moves to Task 6.
  Evidence:
    - test: `dotnet test pol-core.slnx --filter "Category=Integration"` with `/private/tmp/pol-core.integration.env` loaded without logging values -> Integration.Tests 168/168 passed and Architecture integration 4/4 passed
    - test: targeted Admin/JIT suites passed: Admins 125/125 and Hosts 88/88; `scripts/spec-trace.sh admin-workforce-jit` passed 115/115
    - regression: Admin logout preserves its authenticated-session contract; missing/stale session returns `401`, and the existing CSRF filter remains mandatory for authenticated mutations. Targeted backend checks passed 18/18; frontend `npm test` passed 23 + 274 + 26
    - test: `npm test && npm run typecheck && npm run lint && npm run build` -> passed; Node 23/23, root Vitest 274/274, shared 26/26, typecheck/lint green, Next build 115/115 pages
    - runbook: `docs/runbooks/admin-workforce-jit-rollout.md` records Entra prerequisites, Super bootstrap, session revoke and rollback steps
    - browser: production Admin UI employee Microsoft-only and Merchant Google/Microsoft controls passed at exact `clientWidth` 375/768/1440 with no horizontal overflow; workforce/identity-conflict/provider-neutral error copy and return-to-login passed
    - browser: local corporate Microsoft login reached `/dashboard`; `POST /admin/auth/logout-all` returned `204`; a second browser session received `401 admin_session_required` from `GET /admin/me`
    - frontend: removed `NEXT_PUBLIC_SKIP_AUTH`/mock-user bypass; `npm run typecheck`, `npx vitest run src/lib/api/admin/auth.test.ts` (29/29), and `npm run lint` passed
    - deviations: full non-integration suite initially stalled because ambient Admin Microsoft configuration leaked into `Hosts.Tests`; `bugfix-host-test-tenant-pin` isolates the testhost, and the current full run passes 1,936/1,936. No production action performed

- [ ] 6. **Staging Entra release acceptance** — verify staging Entra/SQL/release-image prerequisites, JIT-to-403-to-role-refresh, negative controls, Super/session-revocation rehearsal, and rollback evidence without changing schema.
  Satisfies: REQ-1.1-1.8, REQ-2.1-2.26, REQ-6.1-6.10, REQ-8.1-8.8, REQ-9.1-9.7, REQ-10.1-10.9. Depends on: 5. Verify: staging browser verification of eligible, collision, suspended, Hotmail/onmicrosoft, zero-permission, role-refresh, Admin Google `404`, Merchant Google regression, session revoke, and rollback.
  Release gate: do not deploy to Production until this task has live Staging evidence with release/image digest, timestamp, redacted status/Location/correlation ID, and rollback result.

## Suggested execution batches

งานนี้ coupled ระหว่าง `pol-core` และ `pol-admin`; รัน tasks 1-5 ใน session เดียวเมื่อพร้อม. Task 6 ต้องรอ Staging:

```bash
scripts/pane-loop.sh admin-workforce-jit all-in-one
```

หยุด review ที่ task boundary ตาม `TASK_PROTOCOL.md`. Task 4 ต้องใช้ clean `pol-admin` worktree และห้ามทับ dirty logout/PSP changes เดิม
