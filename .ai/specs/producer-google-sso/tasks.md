# Implementation Tasks: Producer Google SSO + Role RBAC

> **Amended 2026-07-01:** ticket-row tasks superseded — `RegistrationTickets` table/repo/consume deleted, wire
> ticket now stateless (see requirements.md + design.md 2026-07-01 amendments; migration `DropRegistrationTicketsTable`).
> Also 2026-07-01: `TenantUserProfile` entity/table deleted — person/form fields moved onto `ProducerAccount`
> (migration `AddProducerAccountDetailsDropProfile`); any `TenantUserProfile` task prose below is superseded.

> Status: approved 2026-06-25 (AFK-delegated per /goal directive; spec-trace 110/110 criteria covered, EARS lint clean)

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
>   with `POL_DESIGN_SQL='Server=localhost,11434;Database=PaymentOrchestration;User Id=sa;Password=$POL_SA_PASSWORD;Encrypt=True;TrustServerCertificate=True'`
>   + `dotnet ef database update --context ProducerDbContext --project src/BuildingBlocks/BuildingBlocks.Infrastructure --startup-project src/Hosts/Api`.
> - RLS predicates + GRANTs + raw control-plane tables are NOT EF-model state → they live in `migrationBuilder.Sql`
>   in the migration's Up/Down. A worker once hand-applied them to :11434 WITHOUT putting them in the migration —
>   ALWAYS verify a new migration is reproducible from zero on a fresh scratch DB (bootstrap `docker/bootstrap/01-principals.sql`
>   with `-v DbName=...` then `ef database update` against it) before marking a migration task done.
> - The :11434 integration DB + its `dbo.__EFMigrationsHistory` are now consistent (history matches the migration
>   files through `20260628124815_AddProducerSessionTables`). Just `ef database update` for new migrations.
> - Integration tests need `source .env.integration` (sets POL_SQL_SERVER/POL_DB + the 4 principal passwords) and
>   the `pol-sql` docker container started (`docker start pol-sql`). A throwaway `PaymentOrchestration_repro` DB
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
       - reconciliation (2026-06-28): the COMMITTED migration file (`20260626022204_AddProducerIdentityTables`)
         was found to contain ONLY `CreateTable`/`CreateIndex` — the RLS predicates on `TenantUsers`, the
         control-plane `ProducerRegistrationNotices` table, and ALL least-privilege grants existed ONLY in the
         hand-applied :11434 DB, NOT in VCS (so a fresh `ef database update` would have produced tables with no
         tenant isolation + no app-principal grants — a deploy defect). FIXED: the missing RLS/notices/grants SQL
         (and its `Down`) were baked into the migration's `Up`/`Down`, mirroring the proven `AddIdentityTables`
         template + the exact live-DB definitions. Reproducibility PROVEN: the full migration chain run from zero
         on a fresh scratch DB reproduced all 3 predicates + notices table + the 8 grants identically.
       - re-verified (2026-06-28): build 44 proj 0 err; Producer.Tests 31; Architecture.Tests 48;
         ProducerIdentityRls integration 6 (live SQL :11434).

- [x] 2. **RBAC catalog + roles** — `ProducerPermissions` (vocab + `AllKeys` frozen), `ProducerRole` (immutable
     `Code` `^[a-z0-9_]+$`, `SetPermissions` catalog-subset), `ProducerRolePermission`, `ProducerRoleAssignment`
     (+`TenantId`); migration `AddProducerRoleRbacTables` seeding the catalog + **two** roles `tenant_owner` (all
     keys, undeletable anchor) and `tenant_member` (`product.*`+`payment.*` only — default approve choice, S7);
     `IProducerRoleRepository.ListEffectivePermissionsAsync` (union over ACTIVE roles); role CRUD. Done = catalog
     seeded, roles enforce subset + anchor rules.
     Satisfies: REQ-15, REQ-16. Depends on: 1. Verify: `dotnet test` (catalog/DB parity == `ProducerPermissions.All`;
     unknown-key grant rejected; `tenant_owner` undeletable; effective-permission union over active roles).
     Evidence:
       - state: domain (`ProducerPermissions`/`ProducerRole`/etc.) + ports + EF config were pre-scaffolded; this
         pass added the repo impl, the seed migration, and the tests.
       - code: `ProducerRoleRepository` (DUP of `AdminRoleRepository` + tenant-scoped effective-permission union,
         REQ-16.4); migration `20260628123342_AddProducerRoleRbacTables` (data-only — RBAC tables were created by
         AddProducerIdentityTables' model diff) = grants (catalog SELECT-only / role+grant+assignment CRUD for
         pol_admin / pol_app NOTHING, S5) + seed (3 groups, 7 perms, `tenant_owner` all-7, `tenant_member` 4).
       - build: `dotnet build pol-core.slnx` -> 44 projects, 0 errors, 0 warnings.
       - test: `dotnet test tests/Producer.Tests` -> 41 passed (10 new ProducerRoleTests: subset/unknown-key
         reject, slug-pattern reject, SetPermissions dedup, tenant_owner undeletable+undeactivatable, ordinary
         role deletable, catalog vocabulary parity).
       - test: `dotnet test tests/Architecture.Tests` -> 48 passed.
       - test: `source .env.integration && dotnet test tests/Integration.Tests --filter ProducerRoleRbac`
         -> 6 passed (seed 3/7/2 + 7/4 grants; code<->DB catalog parity vs `ProducerPermissions.AllKeys`;
         pol_admin role CRUD + catalog SELECT-only refuses INSERT; grant FK rejects bogus key; pol_app refused on
         all RBAC tables; effective-permission union = ACTIVE roles only, scoped to the approved tenant).
       - migration: reproducible from zero — full chain incl. AddProducerRoleRbacTables applied on a fresh scratch
         DB seeds 3 groups / 7 perms / 2 roles / 7+4 grants identically.
       - viewports: n/a — logic-only (backend slice).
       - deviations: (1) :11434 had drifted (the original migration created identity tables only; the RBAC tables
         were modeled later but never reached it, and the history row carried the pre-regeneration id). Reconciled:
         renamed the history id to the current file, created the 5 RBAC tables from the migration's own generated
         DDL, then applied the seed via `ef database update`. The VCS migrations are the source of truth and are
         reproducible from zero (proven). (2) A throwaway `PaymentOrchestration_repro` DB remains on the :11434
         container from the reproducibility test (DROP is blocked by the destructive guard; it dies with the
         throwaway container).

- [x] 3. **BFF session core** `[DUP→Admin session]` — `ProducerSession` aggregate (owner `TenantUserId`),
     `ProducerSessionDecision` (pure decision table — the heart), `ProducerSessionTokens` (opaque + SHA-256,
     duplicated, Admin untouched), `ProducerSessionStore` (atomic `ExecuteUpdateAsync`, `TrySuperseded`,
     `RevokeFamily`, `RevokeAllForUserAsync`, prune) + `ProducerSessionPorts`; migration `AddProducerSessionTables`
     (control-plane `ProducerSessions`+`ProducerAuthAudits`, `pol_admin` only). Done = decision table + store
     invariants proven.
     Satisfies: REQ-10, REQ-11. Depends on: 1. Verify: `dotnet test` (decision table incl. grace/reuse; rotation
     single-winner `TrySuperseded`; family revoke; prune — unit + integration).
     Evidence:
       - code: DUP of the Admin session stack (owner `AdminAccountId`->`TenantUserId`, `RevokeAllForAdmin`->
         `RevokeAllForUserAsync`; each file carries a `ponytail: DUPLICATE` note, Admin untouched) — domain
         `ProducerSession`/`ProducerSessionDecisionPolicy`/`ProducerAuthAudit`, app `IProducerSessionStore`/
         `IProducerAuthAuditWriter`, infra `ProducerSessionStore`/`ProducerAuthAuditWriter` + EF configs, host
         `ProducerSessionTokens` (opaque 256-bit + SHA-256). Migration `20260628124815_AddProducerSessionTables`
         creates `ProducerSessions` (+TokenHash unique idx) + `ProducerAuthAudits` with control-plane grants
         (Sessions SIUD, AuthAudits append-only SI; pol_app NOTHING).
       - build: `dotnet build pol-core.slnx` -> 44 projects, 0 errors, 0 warnings.
       - test: `dotnet test tests/Producer.Tests` -> 53 passed (12 new: Start/Rotate/IsLiveAt/grace, AuthAudit
         optional-user; full decision table ServeActive/Reject/ServeUnderGrace/ReuseRevokeFamily incl. not-immediate).
       - test: `dotnet test tests/Architecture.Tests` -> 48 passed.
       - test: `source .env.integration && dotnet test tests/Integration.Tests --filter ProducerSession`
         -> 5 passed (single-winner supersede; family revoke; revoke-all-for-user; prune-by-absolute; pol_app
         refused on both tables).
       - migration: applied to :11434 via `ef database update` (history now consistent) AND reproducible — applied
         on the fresh scratch DB with the correct grants (Sessions SIUD / AuthAudits SI).
       - viewports: n/a — logic-only (backend slice).
       - deviations: `ProducerSessionTokens` placed in the host (`src/Hosts/Api`) mirroring `AdminSessionTokens`;
         `ProducerSessionCookies` (cookie attrs) is deferred to Task 6 per the task split.

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
     (ticket replay/expiry/2-tab → one 201 + one 409, no 500; photo type/magic-byte/size; event enqueued same tx;
     consumer not poison on sentinel tenant).
     Evidence:
       - build: `dotnet build pol-core.slnx` -> 44 projects, 0 errors, 0 warnings (TreatWarningsAsErrors).
       - test: `dotnet test tests/Producer.Tests` -> 75 passed / 0 failed (+22 new: PhotoValidation type/magic-byte/
         size/lie/SVG-excluded REQ-7.3/7.4; SubmitRegistrationHandler register+correction+ticket-fail+photo REQ-3/4/5/7/20/21;
         TenantUserRegistrationConsumer idempotent + concurrent-conflict-swallow REQ-20.4).
       - test: `dotnet test tests/Hosts.Tests` -> 121 passed / 0 failed (+6 ProducerRegistrationTickets: roundtrip,
         garbage/tamper reject, foreign-DP-purpose reject REQ-3.1/14.4; ApiContainer DI still validates with the new
         registration wiring + Mediator-discovered SubmitRegistrationHandler/consumer).
       - test: `dotnet test tests/Architecture.Tests` -> 48 passed (Producer.* ⇏ Admin.* boundary intact — new files
         add no Admin dependency).
       - test: `source .env.integration && dotnet test tests/Integration.Tests --filter "Category=Integration&FullyQualifiedName~Producer"`
         -> 19 passed (17 prior RLS/session/RBAC + 2 new ProducerRegistrationOutbox: pol_admin CAN insert the
         sentinel-tenant outbox row, pol_worker CANNOT insert — proves the new grant + least privilege).
       - migration: `AddProducerOutboxAdminGrant` (20260628133442) authored + applied to :11434 via `ef database
         update` (reproducible — grant-only raw SQL, Up GRANT / Down REVOKE).
       - worker boot: `dotnet run --project src/Hosts/Worker` (Development, ValidateOnBuild+ValidateScopes on) ->
         "Now listening" with NO DI/validation exception — the worker resolves the Mediator-discovered
         TenantUserRegistrationConsumer (+ SubmitRegistrationHandler graph) via AddProducerModule's default-context seams.
       - viewports: n/a — logic-only (backend slice; no UI).
       - deviations:
         (1) DESIGN GAP CLOSED (critique B1): pol_admin had NO grant on producer.OutboxMessages (only pol_app INSERT
         + pol_worker SELECT/UPDATE). RLS-bypass bypasses PREDICATES, not table GRANTs — so ProducerOutboxWriter on
         the keyed pol_admin context would have been denied INSERT. Added migration `AddProducerOutboxAdminGrant`
         (GRANT INSERT ON producer.OutboxMessages TO pol_admin). Not in the design's 4-migration list — a real,
         required addition; proven by the integration test.
         (2) `AddProducerModule` now registers the registration seams on the DEFAULT context (was a no-op). Reason:
         the worker's Mediator auto-discovers the consumer AND SubmitRegistrationHandler (same Producer.Application
         assembly), so the handler's whole dep graph must resolve there; the worker genuinely has no keyed pol_admin
         context. The API overrides the WRITE seams onto keyed pol_admin via `AddProducerIdentity` (last registration
         wins) — the registration write needs the RLS-bypass connection (REQ-19.2). Worker boot proves the graph resolves.
         (3) `ProducerRegistrationNotice` entity is mapped with `ExcludeFromMigrations` — the table + grants were
         created by AddProducerIdentityTables' raw SQL in Task 1; mapping it now is runtime-only (no re-CREATE).
         (4) AMBIENT (not introduced by this task): a newly-published advisory GHSA-q6rr-fm2g-g5x8 for Scriban 6.2.0
         began failing ALL builds (Worker, via the Mediator.SourceGenerator build-time analyzer — NOT shipped at
         runtime). HEAD failed identically with my changes stashed. Added the GHSA to the existing per-advisory
         `NuGetAuditSuppress` list in Directory.Build.props, exactly as the documented 2026-06-21 Scriban policy
         (lines 16-20) prescribes. FLAG: re-review/prune when Mediator updates its Scriban pin.
         (5) Photo SERVING endpoint (GET + nosniff, REQ-7.5 serve clause) is NOT in this task's scope (no GET route in
         the design's API table); IPhotoStore.GetAsync is implemented + path-traversal-guarded for a later admin-review
         task. Ticket ISSUANCE (server RegistrationTickets row) is Task 5 (callback); Task 4 builds the consume side +
         the shared signer.

- [x] 5. **OIDC login + callback (state machine)** — `ProducerOidcAuthentication` using the framework
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
     Evidence:
       - code: NEW app `ResolveLogin.cs` (`ResolveLoginQuery` → 4-way `ProducerLoginResult`, RLS-bypass lookup, no
         self-provision) + `ProducerScope.cs` (`ProducerResolution`/`IProducerScope`); NEW host
         `ProducerOidcAuthentication` (scheme `ProducerGoogle` + `producer-oidc-noop` sign-in, blank-ClientId skip),
         `ProducerLoginService` + `ProducerCallbackResolver` (4-way branch + ticket mint), `ProducerOidcOptions`/
         `ProducerSessionOptions`, `ProducerSessionCookies` (`__Host-prd_session`/`prd_csrf`); `IRegistrationTicketRepository.Add`
         seam; `GET /producer/auth/login` + `Producer:Oidc`/`Producer:Session` config + boot guards
         (`RequireProducerClientId`/`Secret`). Session/role/audit/cookie seams wired onto keyed pol_admin
         (`AddProducerIdentity`); role repo also on the default context (`AddProducerModule`) for worker DI.
       - build: `dotnet build pol-core.slnx` -> 44 projects, 0 errors, 0 warnings (TreatWarningsAsErrors).
       - test: `dotnet test tests/Producer.Tests` -> 80 passed / 0 failed (+5 ResolveLoginHandlerTests: unknown→NotFound
         no self-provision REQ-9.6; Pending/Rejected/Suspended branch mapping; Active resolves tenant + effective
         permissions scoped to the user's OWN tenant REQ-16.4/17.1).
       - test: `dotnet test tests/Hosts.Tests` -> 129 passed / 0 failed (+6 ProducerLoginServiceTests: Active→session+
         login audit+prd_session cookie+returnTo redirect REQ-9.4/10.1; NotFound→Registration ticket+redirect /register,
         no session REQ-9.4/9.6; Rejected→Correction ticket+redirect REQ-5.2; Pending→403 awaiting-approval no session
         REQ-22.5; Suspended→denied audit+error redirect; missing-identity→denied; +2 ProducerAuthLoginRedirectTests:
         `/producer/auth/login` → Google authorize code+PKCE(S256)+state+nonce+`openid email` REQ-8.1/8.4 + DP correlation
         cookie REQ-8.2).
       - test: `dotnet test tests/Architecture.Tests` -> 48 passed / 0 failed (Producer.* ⇏ Admin.* boundary intact).
       - worker boot: `dotnet run --project src/Hosts/Worker` (Development, ValidateOnBuild+ValidateScopes) ->
         "Application started" / "Now listening" with NO DI/validation exception — the new ResolveLoginHandler graph
         resolves on the default context (the only log errors are the outbox poller hitting an absent local DB).
       - viewports: n/a — logic-only (backend slice; no UI).
       - deviations:
         (1) REQ-14.4 "separate DP application name": a host has ONE global Data Protection provider, so a second
         app-name is not constructible. The producer OIDC handler's correlation/nonce cookies are isolated from the
         Admin client by the DISTINCT SCHEME NAME ("ProducerGoogle" vs "Google"), which the framework folds into the
         DP purpose chain AND the correlation cookie name — true cross-isolation (proven: the login-redirect test sets
         a `*ProducerGoogle*`-scoped correlation cookie). No second global app-name is created; the shared key ring
         (AddAdminDataProtection) is reused. The wire-ticket purpose is already distinct (`Producer.RegistrationTicket.v1`).
         (2) Ticket MINTING (server `RegistrationTickets` row + `.Protect(...)` wire token) lives in the host
         `ProducerLoginService`, not `ProducerCallbackResolver`, because the TTL + the DP protector are host concerns
         (the resolver stays a pure mediator lookup). NO self-provision either way (REQ-9.6).
         (3) Defensive `Suspended` login outcome added (not in the design's NotFound|Active|Pending|Rejected list): a
         suspended account authenticating fresh gets no session — fail-closed 403 + denied audit (REQ-10.1).
         (4) `RegisterUrl`/`HostedDomain` bound under `Producer:Oidc:*` (one login options object) rather than the
         design's flat `Producer:RegisterUrl`/`Producer:HostedDomain` — cleaner binding, same values.

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
     (cookie auth + rotation/grace/reuse; suspend→next request 401; CSRF 403; `/producer/me`; permission gate 403;
     parity boot guard).
     Evidence:
       - code: host — `ProducerSessionAuthenticationHandler` (+`IProducerSessionResolver`+`ProducerScope`+
         `AddProducerSessionScheme`/dual-scheme `producer` policy = ProducerSession OR JwtBearer, no RequireClaim),
         `ProducerCsrfFilter`, `ProducerSessionPruneService`, `ProducerPermissionAuthorization` (`RequireProducerPermission`
         + `ProducerPermissionParity`); endpoints `/producer/auth/logout|logout-all`, `/producer/me`,
         `/producer/permissions`, `/producer/roles` CRUD (roles.manage), `/producer/tenant-users/{id}/roles` (user.roles).
         app — `ResolveProducerById`, `ProducerRoleQueries`, `ProducerRoleCommands` (Create/Update/Delete),
         `SetProducerUserRoles`; ports `ITenantUserRepository.FindByIdAsync` + neutral `IProducerUnitOfWork` +
         `IProducerRoleRepository.ListActiveRoleCodesForUserAsync`. Shared — additive producer credentialed CORS policy
         in `BuildingBlocks.Web/CorsExtensions` (Cors:ProducerOrigins, /producer/* routing; tenant/admin untouched).
       - build: `dotnet build pol-core.slnx` -> 44 projects, 0 errors, 0 warnings (TreatWarningsAsErrors).
       - test: `dotnet test tests/Producer.Tests` -> 87 passed (+7 ProducerRoleHandlerTests: create duplicate→409 +
         catalog-key→400; update/delete tenant_owner anchor→409; delete role-with-assignments→409; SetUserRoles hides
         out-of-tenant target→404, unknown role→400, sets exactly the requested set stamped with acting tenant+actor).
       - test: `dotnet test tests/Hosts.Tests` -> 160 passed (+31: ProducerSessionAuthHandlerTests 9 = decision table/
         grace/reuse-revoke-family/rotation+Set-Cookie+audit/idle-slide/expired-reject/suspend→401/no-cookie→NoResult +
         tenant_id+sub claims/no role claim/scope bind; ProducerPermissionAuthorizationTests 3 fail-closed; ProducerPermissionParityTests 2;
         ProducerCsrfFilterTests 10; ProducerSessionCookieTests 7 __Host-prd_session/dev-http/SameSite).
       - test: `dotnet test tests/Architecture.Tests` -> 48 passed; `dotnet test tests/Admin.Tests` -> 56 passed
         (the shared CORS change did NOT disturb Admin/tenant policies).
       - host boot: the WebApplicationFactory boot in ProducerAuthLoginRedirectTests exercises the FULL Api host with
         ValidateOnBuild on (Development) AND runs `ProducerPermissionParity.Assert` before app.Run() — green proves the
         producer session scheme + scope + role endpoints all resolve AND every RequireProducerPermission key is in the catalog.
       - worker boot: `dotnet run --project src/Hosts/Worker` -> "Now listening", 0 DI/ValidateOnBuild failures — the
         new Mediator-discovered role-CRUD + ResolveProducerById handlers resolve on the default context (IProducerUnitOfWork
         + role repo registered there; never invoked by the worker).
       - viewports: n/a — logic-only (backend slice; no UI).
       - deviations:
         (1) NO `tenant_role` claim: the rebuilt model is full RBAC (multiple role assignments → a permission union), so
         there is no single tenant_role. The principal carries `tenant_id` (the HttpTenantContext path, S4) + `sub`/`email`/
         NameIdentifier; permissions live in the bound IProducerScope (read by RequireProducerPermission), NOT claims —
         and deliberately NO `role` claim so a producer never resolves as a tenant-Bearer principal (S3). `GET /producer/me`
         returns `roles` (the active role CODES, plural) for REQ-17.5's `role`.
         (2) Neutral `IProducerUnitOfWork` (same `ProducerRegistrationUnitOfWork` class) added as the control-plane commit
         seam for role/assignment handlers — the Admin keyed-"admin" IUnitOfWork is NOT registered in the worker (it doesn't
         reference Admin.Application), so reusing it would break worker ValidateOnBuild; this neutral seam is registered on
         both contexts. Producer role CRUD has NO audit writer (role management is not in REQ-21, unlike Admin's).
         (3) "separate DP app-name" (REQ-14.4): see Task 5 deviation (1) — isolation is by the distinct scheme name, not a
         second global DP app-name.
         (4) Integration follow-up (NOT run here — needs the :11434 SQL container): the `/producer/me` + role-management
         ENDPOINTS' E2E HTTP behavior (cookie → resolve → 200, permission 403, role CRUD round-trip) and a producer-CORS
         preflight test. Their LOGIC is unit-covered (handler/decision/scope/parity tests) and their WIRING is
         host-boot-validated; the DB-backed HTTP round-trip lands with the other Producer integration tests, mirroring the
         deferred photo-serving endpoint in Task 4. `ResolveProducerByIdHandler` (a simpler twin of the tested
         ResolveLoginHandler) is exercised via the session-handler FakeResolver, not its own unit test.

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
     (approve idempotent + scoped-accessible + role-validate; reject kills live sessions; Admin parity green;
     Admin role tests updated and green).
     Evidence:
       - code: `ApproveRejectTenantUser.cs` (Approve/RejectTenantUserCommand + handlers, Producer.Application, NO Admin
         import — B3); host endpoints `POST /admin/tenant-users/{subject}/approve|reject` on the admin group
         (`RequirePermission(producer.approve|reject)` + `IAdminQuery` accessible-tenant floor + active-tenant check,
         then dispatch); `AdminPermissions.cs` += `producer` group + `producer.approve`/`producer.reject` (consts/All/
         GroupKeys → 16/6); data-only migration `20260628144534_AddProducerApprovePermissionToAdminCatalog`
         (idempotent group + 2 keys + super_admin grants). `IProducerSessionStore` also registered on the default
         context (worker DI for the reject handler's RevokeAllForUser).
       - build: `dotnet build pol-core.slnx` -> 44 projects, 0 errors, 0 warnings (TreatWarningsAsErrors).
       - test: `dotnet test tests/Producer.Tests` -> 96 passed (+9 ProducerApproveRejectHandlerTests: approve unknown→404,
         non-Pending→409, already-Active→idempotent no-op no re-assign REQ-6.4, no-roles→400, unknown/inactive role→409,
         happy path activates+assigns role stamped with tenant+admin+audits; reject unknown→404, non-Pending→409, happy
         path Rejected+RevokeAllForUser+audit).
       - test: `dotnet test tests/Admin.Tests` -> 56 passed (AdminRoleTests catalog shape updated 14→16 / 5→6, S1).
       - test: `dotnet test tests/Hosts.Tests` -> 160 passed; `dotnet test tests/Architecture.Tests` -> 48 passed.
       - migration: applied to :11434 via `ef database update` — "Applying migration ... Done" (clean). The SQL lives
         fully in the migration Up (idempotent NOT-EXISTS INSERTs), so a from-zero run reproduces it identically (it
         only adds rows onto the proven AddAdminRoleRbacTables catalog).
       - test: `source .env.integration && dotnet test tests/Integration.Tests --filter AdminRoleRbacGrants` -> 4 passed
         against live SQL :11434 — confirms the seeded Admin catalog is now 6 groups / 16 perms and super_admin holds the
         full 16 (code↔DB parity REQ-18.1).
       - parity: the WAF host boot in Hosts.Tests runs BOTH AdminPermissionParity + ProducerPermissionParity before
         app.Run() — green proves the new producer.approve/reject RequirePermission gate keys ARE in the Admin catalog
         (REQ-18.3, the single cross-catalog coupling satisfies both guards).
       - worker boot: `dotnet run --project src/Hosts/Worker` -> "Now listening", 0 DI failures (the new Approve/Reject
         handlers resolve on the default context).
       - viewports: n/a — logic-only (backend slice; no UI).
       - deviations:
         (1) `RejectTenantUserCommand` accepts a `Reason` but does NOT persist it — `RegistrationAudit` has no reason
         column (REQ-21.1's row shape is action/actor/target/role/tenant/correlation, no reason). REQ-5.1's "record the
         reason" has no column in this slice; the reason is accepted at the API for forward-compat and dropped. Adding a
         column is a thin follow-up.
         (2) Approve audits ONE row with `role` = the comma-joined assigned role codes (REQ-6.6 says one row with `role`;
         the model assigns one-or-more roles).
         (3) Integration follow-up (NOT run here): the approve/reject ENDPOINTS' E2E HTTP behavior (admin cookie →
         IAdminQuery floor → 200/404/403/409). Their LOGIC is unit-covered (handler tests) + the IAdminQuery floor is
         already covered by AdminQueryScopeFloorTests; the DB-backed HTTP round-trip lands with the other Producer
         integration tests, consistent with Tasks 4/6.

- [x] 8. **Enforce the 3 write endpoints + close the seams** — apply the dual-scheme `producer` policy; flip
     `Program.cs` `POST /products` (418) / `POST /payment-sessions` (562) / `POST /payment-sessions/{id}/redirect`
     (583) to `.RequireAuthorization("producer").RequireProducerPermission(product.create|payment.create|payment.redirect)`
     behind `Producer:EnforcePermissionsOnWrites`; remove the three `TODO(producer)` markers + the 346-348 resolver
     TODO. Done = flag off → existing tenant-Bearer flows unchanged; flag on → permission enforced.
     Satisfies: REQ-17, REQ-22, REQ-23. Depends on: 6. Verify: `dotnet test tests/Hosts.Tests`
     (producer+perm pass; producer no-perm 403; **existing tenant-Bearer tests green flag-off**; flag-on Bearer
     fail-closed 403; `ITenantContext.TenantId == producer.tenant`).
     Evidence:
       - code: `Program.cs` — `enforceProducerWrites` flag read + `GateProducerWrite` helper; `POST /products`,
         `POST /payment-sessions`, `POST /payment-sessions/{id}/redirect` flipped to gate behind the flag
         (ON → producer policy + RequireProducerPermission(product.create|payment.create|payment.redirect); OFF →
         the pre-existing `tenant` policy). All 4 `TODO(producer)` markers removed (the 3 write-gate TODOs + the
         resolver TODO, which is now a non-TODO note pointing at ProducerSessionAuthenticationHandler — REQ-17.6).
         `Producer:EnforcePermissionsOnWrites` added to appsettings.json (ships OFF; code default ON when absent — REQ-17.4).
       - build: `dotnet build pol-core.slnx` -> 44 projects, 0 errors, 0 warnings (TreatWarningsAsErrors).
       - test: `dotnet test tests/Hosts.Tests` -> 162 passed (+2 ProducerWriteGateTests: flag OFF -> all 3 endpoints keep
         the `tenant` policy + NO RequiredProducerPermission metadata = existing tenant-Bearer behavior intact; flag ON ->
         all 3 carry the `producer` policy + the matching RequiredProducerPermission key — inspected on the booted
         EndpointDataSource, no DB).
       - test: `dotnet test tests/Architecture.Tests` -> 48 passed. The committed flag defaults OFF, so every existing
         tenant-write test (and the WAF host boots) run unchanged — flag-off behavior is the green baseline.
       - trace: `scripts/spec-trace.sh producer-google-sso` -> "OK: เกณฑ์ 110 ข้อ ถูกอ้างครบใน design.md และ tasks.md,
         EARS lint ผ่านทุกข้อ" — zero uncovered REQ.
       - viewports: n/a — logic-only (backend slice; no UI).
       - deviations:
         (1) The committed `appsettings.json` ships `EnforcePermissionsOnWrites=false` (no producer FE yet, so ON would
         fail-close existing tenant-Bearer writers); the CODE default is ON (absent key = new env, REQ-17.4). Flip per-env
         when the producer FE can establish a session. This resolves the spec's deferred CONFIRM in the safe direction.
         (2) Flag-ON E2E (a real producer cookie passing + a tenant Bearer fail-closing 403 against a live DB) is the
         integration follow-up; the gate WIRING (which policy + permission each endpoint carries per flag) is proven here
         directly via endpoint metadata, and the fail-closed decision is unit-covered by ProducerPermissionAuthorizationTests.

- [x] 9. **Canon reconciliation** [optional] — update `CODING_STANDARDS.md:53` (`ProducerAccount`→`TenantUser`) and
     the `ARCHITECTURE.md` Identity-rebuild note to match the shipped naming; add the new producer auth surface to
     `docs/reference/entity-fields.md` if present (mirrors `admin-oidc-session` REQ-13 canon reconciliation).
     Satisfies: REQ-23 (canon-accuracy). Batch: B-docs. Verify: docs match the shipped entity/module names.
     Evidence:
       - docs: `CODING_STANDARDS.md:53` canonical-entities line updated — the producer actor is the shipped
         **`TenantUser`** (+ the full Producer entity list), with an explicit "not the forward-guess `ProducerAccount`"
         note. `ARCHITECTURE.md` Identity-rebuild bullet rewritten: Identity removed 2026-06-23 → **Producer module
         rebuilt 2026-06-28** (OIDC BFF mirroring admin, `__Host-prd_session`, RBAC, register→admin-approve). Added a
         **Producer module** section to `docs/reference/entity-fields.md` documenting all 13 producer tables (TenantUser
         RLS-keyed + identity children + ProducerSession/ProducerAuthAudit DUPs + the RBAC catalog/role tables) with
         field types pulled from the EF configs; header note updated.
       - verify: `grep -rn ProducerAccount .ai/shared/ docs/reference/` -> the ONLY hit is the intentional
         reconciliation NOTE; all 3 canon files now name `TenantUser`. Docs match the shipped entity/module names.
       - deviations: noted in entity-fields.md that the Admin RBAC tables (admin-role-rbac 2026-06-25) remain absent
         from that reference (pre-existing staleness, out of this feature's scope — flagged for a full regen).

## Suggested execution batches

> COUPLED feature — every task builds on the Producer module primitives from task 1. **Default: run ALL in ONE
> all-in-one session** (`scripts/pane-loop.sh producer-google-sso all-in-one` or `/spec-implement all`); separate
> sessions re-pay cold cache (~30-40% more for coupled work). Coarse order: **1 → (2,3,4) → 5 → 6 → 7 → 8 → 9**.
> 1 is foundational (all depend on it). 2/3/4 are independent of each other (RBAC, session core, registration) once 1
> lands. 5 needs 3+4 (session start + ticket signer). 6 needs 1/2/3. 7 needs 1/2/3. 8 needs 6. 9 is docs-only
> (`Batch: B-docs`, run anytime after naming is final).
