# Implementation Tasks: Producer Google SSO + Role RBAC

> **Amended 2026-07-01:** ticket-row tasks superseded — `RegistrationTickets` table/repo/consume deleted, wire
> ticket now stateless (see requirements.md + design.md 2026-07-01 amendments; migration `DropRegistrationTicketsTable`).
> Also 2026-07-01: `TenantUserProfile` entity/table deleted — person/form fields moved onto `ProducerAccount`
> (migration `AddProducerAccountDetailsDropProfile`); any `TenantUserProfile` task prose below is superseded.

> Status: unknown

> Each task is a cohesive, independently verifiable slice. Implement a whole task in one pass (it may touch many
> files). Decompose into sub-steps yourself at execution time — do NOT pre-split tasks here. Logic-first: pure-function
> unit tests green BEFORE wiring. Each `[DUP]` file copied from Admin carries a `// ponytail: DUPLICATE of Admin.<X>`
> comment. This feature is COUPLED (every task shares the Producer module) → default to ONE all-in-one session.

> ## RESUME STATE (2026-06-28) — ALL TASKS 1-9 DONE (feature implementation complete)
> Done + committed: **Task 1** (35cabd0 + fix 3463ca3), **Task 2** (0003f4a), **Task 3** (8bbd887), **Task 4** (e90783c).
> **Tasks 5-9** IMPLEMENTED (NOT YET COMMITTED — one working tree, ready for review/commit + PR): OIDC login/callback,
> session scheme + RBAC enforcement, admin approve/reject + Admin catalog migration, write-endpoint flag, docs canon.
> All green: build 44/0/0; Producer.Tests 96; Hosts.Tests 162; Architecture.Tests 48; Admin.Tests 56; Integration
> AdminRoleRbacGrants 4 (live :11434); migration 20260628144534 applied to :11434; worker boots clean;
> `spec-trace.sh producer-google-sso` = OK 110/110 + EARS clean. See each task's Evidence block.
> NEXT = review the uncommitted diff, commit Tasks 5-9, open the PR to `develop`. Integration follow-ups (deferred,
> consistent with Tasks 4/6): DB-backed E2E HTTP tests for the producer session/role/me + approve/reject endpoints +
> a producer-CORS preflight test (run against :11434, like the existing Producer integration tests).
>
> **Task 4 carryover for Task 5 (read before the callback):**
> - The shared `ProducerRegistrationTickets` signer (host, `src/Hosts/Api/ProducerRegistration.cs`) is BUILT
>   (Protect + TryUnprotect, DataProtection time-limited, purpose `Producer.RegistrationTicket.v1`). Task 5's callback
>   ISSUES the wire ticket via `.Protect(...)` AND inserts the server `RegistrationTickets` row (the single-use
>   authority) — Task 4 only built the CONSUME side + the signer. `ProducerRegistrationOptions.TicketTtlMinutes` (10).
> - New migration `AddProducerOutboxAdminGrant` (20260628133442) applied to :11434; chain reproducible.
> - `AddProducerModule` registers default-context registration seams; the API's `AddProducerIdentity` overrides the
>   write seams onto keyed pol_admin. Task 5 host wiring (OIDC scheme) adds a SEPARATE Producer DP app-name for the
>   OIDC client (REQ-14.4) — the ticket purpose-isolation is already distinct.
> - Sentinel tenant = `Producer.Infrastructure.Persistence.ProducerOutbox.SentinelTenantId`.
>
> **Migration / integration-DB gotchas (learned the hard way — read before touching migrations):**
> - Migrations live in `src/BuildingBlocks/.../Persistence/Migrations` under context `ProducerDbContext`. Apply
>   with `POL_DESIGN_SQL='Server=localhost,11434;Database=VCentralPay;User Id=sa;Password=$POL_SA_PASSWORD;Encrypt=True;TrustServerCertificate=True'`
>   + `dotnet ef database update --context ProducerDbContext --project src/BuildingBlocks/BuildingBlocks.Infrastructure --startup-project src/Hosts/Api`.
> - RLS predicates + GRANTs + raw control-plane tables are NOT EF-model state → they live in `migrationBuilder.Sql`
>   in the migration's Up/Down. A worker once hand-applied them to :11434 WITHOUT putting them in the migration —
>   ALWAYS verify a new migration is reproducible from zero on a fresh scratch DB (bootstrap `docker/bootstrap/01-principals.sql`
>   with `-v DbName=...` then `ef database update` against it) before marking a migration task done.
> - The :11434 integration DB + its `dbo.__EFMigrationsHistory` are now consistent (history matches the migration
>   files through `20260628124815_AddProducerSessionTables`). Just `ef database update` for new migrations.
> - Integration tests need `source .env.integration` (sets POL_SQL_SERVER/POL_DB + the 4 principal passwords) and
>   the `pol-sql` docker container started (`docker start pol-sql`). A throwaway `VCentralPay_repro` DB
>   from a repro test may still exist on the container (DROP is blocked by the destructive guard; harmless).
> - **Task 4 outbox subtlety:** registration runs on the pol_admin control plane with NO tenant (Pending user has
>   TenantId NULL). The default `EfOutbox` REQUIRES a bound tenant + uses the default context → you need a
>   `ProducerOutboxWriter` on the KEYED pol_admin `ProducerDbContext` writing with a SENTINEL TenantId, and the
>   Admin consumer must be idempotent + not poison on that sentinel (Task 4 verify line). Register the event in
>   `OutboxDispatcher.EventTypes`. Surface map from the Explore pass is in the prior session's transcript.

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

- [x] 2. **RBAC catalog + roles** — `ProducerPermissions` (vocab + `AllKeys` frozen), `ProducerRole` (immutable
     `Code` `^[a-z0-9_]+$`, `SetPermissions` catalog-subset), `ProducerRolePermission`, `ProducerRoleAssignment`
     (+`TenantId`); migration `AddProducerRoleRbacTables` seeding the catalog + **two** roles `tenant_owner` (all
     keys, undeletable anchor) and `tenant_member` (`product.*`+`payment.*` only — default approve choice, S7);
     `IProducerRoleRepository.ListEffectivePermissionsAsync` (union over ACTIVE roles); role CRUD. Done = catalog
     seeded, roles enforce subset + anchor rules.
     Satisfies: REQ-15, REQ-16. Depends on: 1. Verify: `dotnet test` (catalog/DB parity == `ProducerPermissions.All`;

- [x] 3. **BFF session core** `[DUP→Admin session]` — `ProducerSession` aggregate (owner `TenantUserId`),
     `ProducerSessionDecision` (pure decision table — the heart), `ProducerSessionTokens` (opaque + SHA-256,
     duplicated, Admin untouched), `ProducerSessionStore` (atomic `ExecuteUpdateAsync`, `TrySuperseded`,
     `RevokeFamily`, `RevokeAllForUserAsync`, prune) + `ProducerSessionPorts`; migration `AddProducerSessionTables`
     (control-plane `ProducerSessions`+`ProducerAuthAudits`, `pol_admin` only). Done = decision table + store
     invariants proven.
     Satisfies: REQ-10, REQ-11. Depends on: 1. Verify: `dotnet test` (decision table incl. grace/reuse; rotation

- [x] 4. **Registration endpoint + photo + outbox event** — `OpaqueTicket` signer (DataProtection, distinct
     purpose); `POST /producer/register` (anonymous, ticket-gated, multipart; **size bound before buffering** N3);
     `SubmitRegistrationCommand` consuming ticket (conditional UPDATE rowcount=1) + creating
     `TenantUser(Pending)`+`ExternalLogin`+`Profile(+photo)` + enqueueing the event in **ONE pol_admin tx**, with
     unique-violation→409 translation (S9); `IPhotoStore`+`LocalPhotoStore`+validation (content-type allowlist +
     magic-byte + cap); **`ProducerOutboxWriter`** on the keyed pol_admin context (B1); `Contracts/TenantUserRegistrationSubmitted`
     + `OutboxDispatcher.EventTypes` registration; Admin consumer → idempotent `ProducerRegistrationNotices` (no
     tenant-scoped table, S5); reject→correction→resubmit path (Rejected→Pending). Done = register + resubmit work
     end-to-end, event published.
     Satisfies: REQ-3, REQ-4, REQ-5, REQ-7, REQ-20, REQ-21. Depends on: 1. Verify: `dotnet test tests/Hosts.Tests`
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 5. **OIDC login + callback (state machine)** — `ProducerOidcAuthentication` using the framework
     `AddOpenIdConnect` (scheme `ProducerGoogle` + `producer-oidc-noop` sign-in scheme, separate DP app-name +
     callback `/producer/auth/callback`, S2) with `OnTokenValidated` (email_verified/hd), `OnTicketReceived`
     (4-way branch), `OnRemoteFailure`/`OnAccessDenied` (deny→error page); `ProducerLoginService` (none→Registration
     ticket+redirect `/register`; Active→`ProducerSession.Start`+cookie+returnTo; Pending→403; Rejected→Correction
     ticket+redirect) + `ProducerCallbackResolver` (mint ticket, **NO self-provision**); `ReturnUrlPolicy` allowlist;
     `GET /producer/auth/login`; boot guards (secret fail-fast, blank ClientId→skip scheme). Done = login redirects
     to Google; each callback branch behaves.
     Satisfies: REQ-8, REQ-9, REQ-14. Depends on: 1, 3, 4. Verify: `dotnet test tests/Hosts.Tests`
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 6. **Session scheme + handler + ambient tenant + permission enforcement** — `ProducerSessionAuthenticationHandler`
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
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 7. **Admin approve/reject (cross-plane) + Admin catalog extension** — `ApproveTenantUserCommand(subject,
     validatedTenantId, roleCodes)` / `RejectTenantUserCommand` in `Producer.Application`; host endpoints
     `POST /admin/tenant-users/{subject}/approve|reject` doing `RequirePermission(producer.approve|producer.reject)`
     on `IAdminScope` + accessible-tenant floor via `IAdminQuery` **at the host**, then dispatch (B3); approve
     assigns TenantId+roles (validate exist/Active, target Pending, idempotent), reject→Rejected + `RevokeAllForUser`;
     migration `AddProducerApprovePermissionToAdminCatalog` (new Admin group `producer` + 2 keys + `super_admin`
     grant + `AdminPermissions.cs` consts/`All`/`GroupKeys`); update `AdminRoleTests`/`AdminRoleRbacGrantsTests`
     counts (14→16 perms, 5→6 groups — declared, S1); registration audit on each action. Done = approve/reject
     end-to-end with the Admin gate.
     Satisfies: REQ-6, REQ-18, REQ-21. Depends on: 1, 2, 3. Verify: `dotnet test`
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 8. **Enforce the 3 write endpoints + close the seams** — apply the dual-scheme `producer` policy; flip
     `Program.cs` `POST /products` (418) / `POST /payment-sessions` (562) / `POST /payment-sessions/{id}/redirect`
     (583) to `.RequireAuthorization("producer").RequireProducerPermission(product.create|payment.create|payment.redirect)`
     behind `Producer:EnforcePermissionsOnWrites`; remove the three `TODO(producer)` markers + the 346-348 resolver
     TODO. Done = flag off → existing tenant-Bearer flows unchanged; flag on → permission enforced.
     Satisfies: REQ-17, REQ-22, REQ-23. Depends on: 6. Verify: `dotnet test tests/Hosts.Tests`
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 9. **Canon reconciliation** [optional] — update `CODING_STANDARDS.md:53` (`ProducerAccount`→`TenantUser`) and
     the `ARCHITECTURE.md` Identity-rebuild note to match the shipped naming; add the new producer auth surface to
     `docs/reference/entity-fields.md` if present (mirrors `admin-oidc-session` REQ-13 canon reconciliation).
     Satisfies: REQ-23 (canon-accuracy). Batch: B-docs. Verify: docs match the shipped entity/module names.
      - viewports: n/a — legacy corpus predates viewport protocol (human checkpoint 2026-08-26)
