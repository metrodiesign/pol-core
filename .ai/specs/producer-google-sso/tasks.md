# Implementation Tasks: Producer Google SSO + Role RBAC

> Status: approved 2026-06-25 (AFK-delegated per /goal directive; spec-trace 110/110 criteria covered, EARS lint clean)

> Each task is a cohesive, independently verifiable slice. Implement a whole task in one pass (it may touch many
> files). Decompose into sub-steps yourself at execution time — do NOT pre-split tasks here. Logic-first: pure-function
> unit tests green BEFORE wiring. Each `[DUP]` file copied from Admin carries a `// ponytail: DUPLICATE of Admin.<X>`
> comment. This feature is COUPLED (every task shares the Producer module) → default to ONE all-in-one session.

- [x] 1. **Producer module + identity domain + tables + RLS + boundary tests** — scaffold
     `src/Modules/Producer/{Producer.Domain,Producer.Application,Producer.Infrastructure}` + register in
     `ModuleAssemblies`; domain aggregates `TenantUser` (state machine Pending/Active/Rejected/Suspended, idempotent
     `Approve`, illegal-transition guard, **no Role column** F1), `ExternalLogin` `(Provider,Subject)` unique,
     `RegistrationTicket` (`Purpose`, single-use `Consume`), `TenantUserProfile` (+`PhotoObjectKey`/`PhotoContentType`
     + detail fields), `RegistrationAudit`; migration `AddProducerIdentityTables` authored **FRESH** (no `Role`
     column, no `Utc` suffix — S10) with RLS FILTER+BLOCK on `TenantUsers` + control-plane `ProducerRegistrationNotices`
     (unique TenantUserId) granted `pol_worker`+`pol_admin` (S5) + least-privilege grants; add **`ProducerArchitectureTests`**
     asserting `Producer.* ⇏ Admin.*` and `Admin.* ⇏ Producer.*` (B2). Done = projects build, migration applies, RLS
     integration test green.
     Satisfies: REQ-1, REQ-2, REQ-19, REQ-23. Verify: `dotnet test tests/Producer.Tests tests/Integration.Tests`
     (TenantUser transitions; Pending NULL-TenantId insert **rejected under pol_app, allowed under pol_admin**;
     ProducerArchitectureTests green).
     Evidence:
       - build: `dotnet build pol-core.slnx` -> 44 projects, 0 errors, 0 warnings (TreatWarningsAsErrors).
       - test: `dotnet test tests/Producer.Tests` -> 31 passed / 0 failed (TenantUser state machine: 4 legal
         transitions + every illegal transition throws + idempotent Approve; RegistrationTicket single-use/expiry).
       - test: `dotnet test tests/Architecture.Tests` -> 48 passed / 0 failed (incl. 5 ProducerArchitectureTests:
         Producer.* ⇏ Admin.* AND Admin.* ⇏ Producer.* both proven (B2), Domain no-EF/no-Infra/no-Host).
       - test: `source .env.integration && dotnet test tests/Integration.Tests --filter ProducerIdentityRls`
         -> 6 passed / 0 failed against live SQL :11434 (Pending NULL-TenantId INSERT rejected under pol_app /
         allowed under pol_admin REQ-19.2; NULL row invisible to every tenant; approved row own-tenant-only;
         pol_app refused on all 4 control-plane child tables REQ-19.4; ProducerRegistrationNotices INSERT/SELECT
         for pol_worker but refused for pol_app S5).
       - viewports: n/a — logic-only (backend slice).
       - deviations: (1) ProducerRegistrationNotices created in raw SQL with no EF entity this slice — its
         consumer lands in Task 4 (ponytail: add the entity when the consumer needs it). (2) Migration
         `20260625050040_AddProducerIdentityTables` was generated AND applied to the :11434 integration DB during
         this task (the worker pre-applied it; `ef database update` reports "already up to date"). (3) The
         3 csproj + 8 domain entities + EF configs + slnx/Api wiring were authored by a fresh-context worker;
         the migration/RLS/tests were reviewed file-by-file and verified green here before marking done.

- [ ] 2. **RBAC catalog + roles** — `ProducerPermissions` (vocab + `AllKeys` frozen), `ProducerRole` (immutable
     `Code` `^[a-z0-9_]+$`, `SetPermissions` catalog-subset), `ProducerRolePermission`, `ProducerRoleAssignment`
     (+`TenantId`); migration `AddProducerRoleRbacTables` seeding the catalog + **two** roles `tenant_owner` (all
     keys, undeletable anchor) and `tenant_member` (`product.*`+`payment.*` only — default approve choice, S7);
     `IProducerRoleRepository.ListEffectivePermissionsAsync` (union over ACTIVE roles); role CRUD. Done = catalog
     seeded, roles enforce subset + anchor rules.
     Satisfies: REQ-15, REQ-16. Depends on: 1. Verify: `dotnet test` (catalog/DB parity == `ProducerPermissions.All`;
     unknown-key grant rejected; `tenant_owner` undeletable; effective-permission union over active roles).

- [ ] 3. **BFF session core** `[DUP→Admin session]` — `ProducerSession` aggregate (owner `TenantUserId`),
     `ProducerSessionDecision` (pure decision table — the heart), `ProducerSessionTokens` (opaque + SHA-256,
     duplicated, Admin untouched), `ProducerSessionStore` (atomic `ExecuteUpdateAsync`, `TrySuperseded`,
     `RevokeFamily`, `RevokeAllForUserAsync`, prune) + `ProducerSessionPorts`; migration `AddProducerSessionTables`
     (control-plane `ProducerSessions`+`ProducerAuthAudits`, `pol_admin` only). Done = decision table + store
     invariants proven.
     Satisfies: REQ-10, REQ-11. Depends on: 1. Verify: `dotnet test` (decision table incl. grace/reuse; rotation
     single-winner `TrySuperseded`; family revoke; prune — unit + integration).

- [ ] 4. **Registration endpoint + photo + outbox event** — `OpaqueTicket` signer (DataProtection, distinct
     purpose); `POST /producer/register` (anonymous, ticket-gated, multipart; **size bound before buffering** N3);
     `SubmitRegistrationCommand` consuming ticket (conditional UPDATE rowcount=1) + creating
     `TenantUser(Pending)`+`ExternalLogin`+`Profile(+photo)` + enqueueing the event in **ONE pol_admin tx**, with
     unique-violation→409 translation (S9); `IPhotoStore`+`LocalPhotoStore`+validation (content-type allowlist +
     magic-byte + cap); **`ProducerOutboxWriter`** on the keyed pol_admin context (B1); `Contracts/TenantUserRegistrationSubmitted`
     + `OutboxDispatcher.EventTypes` registration; Admin consumer → idempotent `ProducerRegistrationNotices` (no
     tenant-scoped table, S5); reject→correction→resubmit path (Rejected→Pending). Done = register + resubmit work
     end-to-end, event published.
     Satisfies: REQ-3, REQ-4, REQ-5, REQ-7, REQ-20, REQ-21. Depends on: 1. Verify: `dotnet test tests/Hosts.Tests`
     (ticket replay/expiry/2-tab → one 201 + one 409, no 500; photo type/magic-byte/size; event enqueued same tx;
     consumer not poison on sentinel tenant).

- [ ] 5. **OIDC login + callback (state machine)** — `ProducerOidcAuthentication` using the framework
     `AddOpenIdConnect` (scheme `ProducerGoogle` + `producer-oidc-noop` sign-in scheme, separate DP app-name +
     callback `/producer/auth/callback`, S2) with `OnTokenValidated` (email_verified/hd), `OnTicketReceived`
     (4-way branch), `OnRemoteFailure`/`OnAccessDenied` (deny→error page); `ProducerLoginService` (none→Registration
     ticket+redirect `/register`; Active→`ProducerSession.Start`+cookie+returnTo; Pending→403; Rejected→Correction
     ticket+redirect) + `ProducerCallbackResolver` (mint ticket, **NO self-provision**); `ReturnUrlPolicy` allowlist;
     `GET /producer/auth/login`; boot guards (secret fail-fast, blank ClientId→skip scheme). Done = login redirects
     to Google; each callback branch behaves.
     Satisfies: REQ-8, REQ-9, REQ-14. Depends on: 1, 3, 4. Verify: `dotnet test tests/Hosts.Tests`
     (`/producer/auth/login` → Google authorize w/ code+PKCE+state+nonce; callback new→ticket / Active→cookie+returnTo
     / Pending→403 / Rejected→correction; OAuth error→deny+audit).

- [ ] 6. **Session scheme + handler + ambient tenant + permission enforcement** — `ProducerSessionAuthenticationHandler`
     (scheme `ProducerSession`; per-request decision+rotate+READ-ONLY re-resolve; bind `IProducerScope` + claims
     `tenant_id`/`tenant_role`/permissions, **`tenant_id` claim = `HttpTenantContext` path**, no `ITenantScope.Begin`
     S4; session only-when-Active); `ProducerSessionCookies` (`__Host-prd_session`/`prd_session`/`prd_csrf`);
     `ProducerCsrfFilter`; `ProducerSessionPruneService`; `ProducerPermissionAuthorization`
     (`RequireProducerPermission` fail-closed + `ProducerPermissionParity.Assert`); `ProducerHostWiring`
     (`AddProducerIdentity`/`AddProducerSessionScheme`); **dual-scheme `producer` policy** (`ProducerSession` OR
     `JwtBearer`, `RequireAuthenticatedUser`, NO `RequireClaim` S3); credentialed producer CORS (REQ-14.5);
     `POST /producer/auth/logout|logout-all`; `GET /producer/me`; `GET /producer/permissions` + role/assignment
     endpoints with the gating matrix (reads authenticated, mutate `roles.manage`, assign `user.roles` S8). Done =
     authenticated producer resolves scope, rotation/revoke/CSRF enforced.
     Satisfies: REQ-12, REQ-13, REQ-14, REQ-15, REQ-17, REQ-21. Depends on: 1, 2, 3. Verify: `dotnet test tests/Hosts.Tests`
     (cookie auth + rotation/grace/reuse; suspend→next request 401; CSRF 403; `/producer/me`; permission gate 403;
     parity boot guard).

- [ ] 7. **Admin approve/reject (cross-plane) + Admin catalog extension** — `ApproveTenantUserCommand(subject,
     validatedTenantId, roleCodes)` / `RejectTenantUserCommand` in `Producer.Application`; host endpoints
     `POST /admin/tenant-users/{subject}/approve|reject` doing `RequirePermission(producer.approve|producer.reject)`
     on `IAdminScope` + accessible-tenant floor via `IAdminQuery` **at the host**, then dispatch (B3); approve
     assigns TenantId+roles (validate exist/Active, target Pending, idempotent), reject→Rejected + `RevokeAllForUser`;
     migration `AddProducerApprovePermissionToAdminCatalog` (new Admin group `producer` + 2 keys + `super_admin`
     grant + `AdminPermissions.cs` consts/`All`/`GroupKeys`); update `AdminRoleTests`/`AdminRoleRbacGrantsTests`
     counts (14→16 perms, 5→6 groups — declared, S1); registration audit on each action. Done = approve/reject
     end-to-end with the Admin gate.
     Satisfies: REQ-6, REQ-18, REQ-21. Depends on: 1, 2, 3. Verify: `dotnet test`
     (approve idempotent + scoped-accessible + role-validate; reject kills live sessions; Admin parity green;
     Admin role tests updated and green).

- [ ] 8. **Enforce the 3 write endpoints + close the seams** — apply the dual-scheme `producer` policy; flip
     `Program.cs` `POST /products` (418) / `POST /payment-sessions` (562) / `POST /payment-sessions/{id}/redirect`
     (583) to `.RequireAuthorization("producer").RequireProducerPermission(product.create|payment.create|payment.redirect)`
     behind `Producer:EnforcePermissionsOnWrites`; remove the three `TODO(producer)` markers + the 346-348 resolver
     TODO. Done = flag off → existing tenant-Bearer flows unchanged; flag on → permission enforced.
     Satisfies: REQ-17, REQ-22, REQ-23. Depends on: 6. Verify: `dotnet test tests/Hosts.Tests`
     (producer+perm pass; producer no-perm 403; **existing tenant-Bearer tests green flag-off**; flag-on Bearer
     fail-closed 403; `ITenantContext.TenantId == producer.tenant`).

- [ ] 9. **Canon reconciliation** [optional] — update `CODING_STANDARDS.md:53` (`ProducerAccount`→`TenantUser`) and
     the `ARCHITECTURE.md` Identity-rebuild note to match the shipped naming; add the new producer auth surface to
     `docs/reference/entity-fields.md` if present (mirrors `admin-oidc-session` REQ-13 canon reconciliation).
     Satisfies: REQ-23 (canon-accuracy). Batch: B-docs. Verify: docs match the shipped entity/module names.

## Suggested execution batches

> COUPLED feature — every task builds on the Producer module primitives from task 1. **Default: run ALL in ONE
> all-in-one session** (`scripts/pane-loop.sh producer-google-sso all-in-one` or `/spec-implement all`); separate
> sessions re-pay cold cache (~30-40% more for coupled work). Coarse order: **1 → (2,3,4) → 5 → 6 → 7 → 8 → 9**.
> 1 is foundational (all depend on it). 2/3/4 are independent of each other (RBAC, session core, registration) once 1
> lands. 5 needs 3+4 (session start + ticket signer). 6 needs 1/2/3. 7 needs 1/2/3. 8 needs 6. 9 is docs-only
> (`Batch: B-docs`, run anytime after naming is final).
