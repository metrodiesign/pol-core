# Task 3 handoff — rf1-schema-reset (big-bang cutover)

> Written 2026-07-11 mid-execution. Task 3 is NOT done — this is a checkpoint so a fresh session
> (new context) can resume without re-deriving the hundreds of small rename decisions already made.
> Read this file FIRST, then `.ai/specs/rf1-schema-reset/{design,requirements,tasks}.md` and the
> approved plan at `~/.claude/plans/proud-splashing-marshmallow.md`.
>
> Filesystem is ground truth. Run `scripts/spec-state.sh rf1-schema-reset` and
> `find src/Modules/Merchants -name "*.cs"` before trusting this doc's file lists — they may have
> drifted if work continued after this was written.

## How to resume

1. Read this file + design.md + requirements.md + the plan file above.
2. Run `dotnet build pol-core.slnx` — it will NOT succeed yet (Merchants.Application/Infrastructure
   don't exist, old Tenant/Producer modules still referenced elsewhere, Program.cs half old/half
   untouched). The compiler errors are your task list — work through them file by file using the
   rename rules below, they are the authoritative source of truth for "what does X rename to."
3. Do NOT re-litigate the rename rules below — they were derived carefully from design.md +
   requirements.md and cross-checked against the actual current-state code. If one looks wrong,
   re-verify against design.md's tables before changing it.
4. Continue down the phase list in "Remaining work" in order — each phase depends on the previous.
5. When genuinely done (build green, tests green, fresh-DB migrate verified), flip task 3 `[x]` in
   tasks.md with a real Evidence block (commands actually run + actual output), per
   `.claude/skills/spec-implement/SKILL.md`. Do NOT mark it done on partial verification.
6. If context is getting deep again before task 3 is fully green, update this file (not just
   tasks.md) with the new state and hand off again the same way.

## Done so far (verified self-consistent, NOT yet build-verified against the rest of the tree)

- `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/SchemaNames.cs` — new. Constants
  `Shop/Txn/Admin/Merch/Sec/Dbo`.
- `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/PolDbContext.cs` — new, replaces
  `ProducerDbContext.cs` (NOT YET DELETED — still on disk, will collide/dup with PolDbContext's
  DbSets once other files stop referencing it; delete it once nothing references `ProducerDbContext`
  anymore, confirm via `grep -rl ProducerDbContext src tests`).
- `ModuleAssemblies.cs` — NOT YET EDITED (still has `.Producer` property, needs rename to `.Modules`,
  see rule table below). `DesignTimeDbContextFactories.cs` — NOT YET EDITED (still references
  `ProducerDbContext`/`Tenant.Infrastructure.TenantModuleRegistration`/
  `Producer.Infrastructure.ProducerModuleRegistration`; must reference `PolDbContext` +
  `Merchants.Infrastructure.MerchantsModuleRegistration` once that exists).
- `DataProtectionKey.cs` — schema=Dbo added.
- `OutboxMessage.cs`/`OutboxMessageConfiguration.cs` — `TenantId`→`MerchantId`, schema=Txn.
- `IdempotencyRecord.cs`/`IdempotencyRecordConfiguration.cs` — `TenantId`→`MerchantId`, schema=Txn.
- `VaultSecretBlob.cs` — `TenantId`→`MerchantId` (config file `VaultSecretBlobConfiguration.cs` NOT
  YET EDITED — still says `builder.ToTable("VaultSecrets")` with no schema and `x.TenantId` in the
  composite key; needs schema=Merch + `x.MerchantId`).
- `VaultRevealAudit.cs`/`VaultRevealAuditConfiguration.cs` — **NOT YET EDITED AT ALL** (still full
  `TenantId` throughout entity + config; needs schema=Merch).
- `src/Modules/Merchants/Merchants.Domain/` — COMPLETE, 21 `.cs` files + csproj. Merged from
  `Tenant.Domain` (4 files) + `Producer.Domain` (17 files, `ProducerTenantAssignment.cs` dropped —
  absorbed into `MerchantUser.MerchantId` nullable + `MerchantUser.Approve(merchantId, now)`).
- `src/Modules/Merchants/Merchants.Application/Merchants.Application.csproj` — csproj only, NO `.cs`
  files written yet.
- `src/Modules/Merchants/Merchants.Infrastructure/Merchants.Infrastructure.csproj` — csproj only, NO
  `.cs` files written yet.
- Old `src/Modules/Tenant/` and `src/Modules/Producer/` — UNTOUCHED, still fully present (source of
  truth for the Application/Infrastructure content still to port — read them before deleting).
- `src/Modules/Identity/` — NOT YET DELETED (confirmed shell-only, no real `.cs`, safe to
  `rm -rf` whenever convenient).
- `pol-core.slnx` — NOT YET EDITED.
- Nothing in Admin module touched. Nothing in BuildingBlocks.Application actor-context touched.
  Interceptor not rewritten. No migrations touched (still 26 old files + old snapshot). Bootstrap
  script not touched. Program.cs not touched. Docker/CI/.env.example not touched. No tests touched.

## Rename rule table (the hard-won part — apply exactly, do not re-derive)

Applied via ordered substring/prefix rules. Order matters where patterns overlap (longer/more
specific first). General rule: `Tenant`/`tenant` → `Merchant`/`merchant` substring, `Producer` →
`MerchantUser` substring, EXCEPT the rows below which override that default.

### Admin module (module itself is NOT renamed — only these specific entities move to "Platform"; T6
locks the RBAC catalog names — `AdminRole`/`AdminRolePermission`/`AdminPermission`/
`AdminPermissionGroup`/`AdminRoleStatus` etc. — schema moves, NAME DOES NOT CHANGE)

| Find (prefix, cascades to compounds) | Replace |
|---|---|
| `AdminTenantAssignments` (plural, do this FIRST) | `PlatformMerchantAccess` (no plural form — deliberate, matches design's literal table name) |
| `AdminTenantAssignment` | `PlatformMerchantAccess` |
| `AdminAccounts` (plural, FIRST) | `PlatformUsers` |
| `AdminAccount` (cascades: `AdminAccountId`→`PlatformUserId`, `AdminAccountAudit`→`PlatformUserAudit`, `AdminAccountRepository`→`PlatformUserRepository`, `AdminAccountQueries`→`PlatformUserQueries`, etc.) | `PlatformUser` |
| `AdminAuthAudit` (cascades to `IAdminAuthAuditWriter`→`IPlatformAuthAuditWriter`, `AdminAuthEventType`→`PlatformAuthEventType`) | `PlatformAuthAudit` |
| `AdminSessions` (plural, FIRST) | `PlatformUserSessions` |
| `AdminSession` (cascades broadly: Decision/Status/Store/Options/Cookies/Tokens/PruneService/AuthenticationHandler/SchemeRegistration/Policy/Resolver — all of it) | `PlatformUserSession` |
| `AdminTier` (cascades to `AdminTierAuthorization`) | `PlatformUserTier` |

Everything else in Admin.* (AdminScope, IAdminScope, AdminQuery, IAdminQuery, AdminModuleRegistration,
AdminConfigurations, AdminRepositories, AdminRole*, AdminPermission*, AdminStatus,
AdminAccountAudit — wait, this IS covered by the `AdminAccount` prefix rule above, so it becomes
`PlatformUserAudit` automatically) stays "Admin"-prefixed UNCHANGED. `AdminTenantDirectory` /
`IAdminTenantDirectory` (contains "Tenant", not "AdminAccount"/"AdminTenantAssignment") falls through
to the GENERAL `Tenant`→`Merchant` rule below → becomes `AdminMerchantDirectory` /
`IAdminMerchantDirectory` (correct, no special rule needed). `AccessibleTenants` → falls through to
general rule → `AccessibleMerchants` (correct, no special rule needed).

### Producer module (module IS dissolving into Merchants — full rename, no T6-style protection)

| Find | Replace | Why (not the naive substring result) |
|---|---|---|
| `ProducerAccounts` (plural, FIRST) | `MerchantUsers` | matches schema map table name |
| `ProducerAccount` (cascades to Id/Status/Repository/etc.) | `MerchantUser` | drops "Account" suffix, design's own C# map row says so |
| `ProducerAuthAudit` | `MerchantAuthAudit` | drops "User" too — design schema map: `MerchantAuthAudits \| ProducerAuthAudits` |
| `ProducerRegistrationNotices` (plural, FIRST) | `RegistrationNotices` | drops "Producer"/"Merchant" entirely — design: `RegistrationNotices \| ProducerRegistrationNotices` |
| `ProducerRegistrationNotice` | `RegistrationNotice` | same |
| `\bProducerRoles\b` (exact word, plural, FIRST — do NOT let this match inside `ProducerRoleAssignment`/`ProducerRolePermission`) | `MerchantUserRoleDefinitions` | design: table `MerchantUserRoleDefinitions \| ProducerRoles` |
| `\bProducerRole\b` (exact word, singular — same non-match guard) | `MerchantUserRoleDefinition` | same, disambiguates from the Assignment/Permission compounds which cascade fine via the general rule below |
| `ProducerBoundProducerFilter` | `MerchantBoundFilter` | design states this exact name — drops "User" too |
| `IProducerUnitOfWork` | `IMerchantsUnitOfWork` | design states this exact name — "Merchants" (module-plural), NOT "MerchantUser" |
| `IProducerRegistrationUnitOfWork` | `IMerchantsRegistrationUnitOfWork` | consistent with the row above (own inference, not explicit in design, but same module-level naming logic) |
| `ProducerRegistrationUnitOfWork` (concrete class) | `MerchantsRegistrationUnitOfWork` | same |
| `ProducerHostWiring` | `MerchantsHostWiring` | own inference — module-level wiring class, mirrors `AdminHostWiring` |
| `ProducerModuleRegistration` + `TenantModuleRegistration` | merge into ONE `MerchantsModuleRegistration` | structural merge, not a rename — hand-write |
| role code `tenant_owner` | `merchant_owner` | already applied in Merchants.Domain |
| role code `tenant_member` | `merchant_member` | already applied in Merchants.Domain |
| permission key `producer.roles.view`/`.manage`, `producer.user.roles` | `merchant_user.roles.view`/`.manage`, `merchant_user.user.roles` | already applied in Merchants.Domain (`MerchantUserPermissions.cs`) |
| permission key `producer.approve`/`producer.reject` (REQ-2.6, explicit) | `merchant_user.approve`/`merchant_user.reject` | REQ-2.6 literal |
| Admin permission GROUP key `'producer'` (label "ผู้ผลิต") | `'merchant_user'` | own inference per REQ-2.7 zero-token sweep (not yet applied anywhere — still pending in `AdminPermissions.cs` + the future SeedData migration) |

Everything else `Producer*`/`producer*` NOT in this table cascades correctly through the plain
`Producer`→`MerchantUser` / `producer`→`merchant-user` (kebab, routes/policy/scheme names) or
`merchantUser` (camelCase locals) substring rule — e.g. `ProducerSession`→`MerchantUserSession`
(confirmed matches design's own sequence-diagram naming), `ProducerRoleAssignment`→
`MerchantUserRoleAssignment`, `ProducerScope`/`IProducerScope`→`MerchantUserScope`/
`IMerchantUserScope` (own inference, reasonable, no design conflict).

### BuildingBlocks actor-context (Tenant-related, target = "Actor" not "Merchant" — the true exceptions)

| Find (exact identifier) | Replace |
|---|---|
| `ITenantContext` | `IActorContext` — new shape `{ Guid MerchantId, Guid? UserId, bool HasActor }` (adds `UserId`, genuinely new, not just renamed) |
| `HttpTenantContext` | `HttpActorContext` |
| `WorkerTenantContext` | `WorkerActorContext` |
| `AmbientTenant` | `AmbientActor` |
| `ITenantScope` (exact — NOT `ITenantScoped`, that one cascades fine via the general rule to `IMerchantScoped`) | `IActorScope` — `Begin` takes `(Guid merchantId, Guid? userId = null)` now |
| `HasTenant` (property, wherever it appears — on the interface + all 3 implementations + any caller) | `HasActor` |

New file needed: `AdminActorContext.cs` (scoped, holds `{ Guid.Empty, PlatformUserId }` for the keyed
"admin" registration — see design.md "Interceptor contract" table, "Actor source" table). Not started.

### Naming collision to remember, NOT a bug, no action needed

`ProvisionPspConnectionRequest.MerchantId` / `PspConnectionSpec.MerchantId` (already exists today) is
the PSP gateway's OWN merchant id (2C2P/Omise), unrelated to the actor `MerchantId` this rf1 adds.
PSP vocabulary is explicitly frozen by design — do not touch this field. They never collide in the
same class.

## Remaining work (in order — each phase blocked on the previous)

1. **Finish Merchants.Application** (~13-15 files after merge). Source content for ALL 21 old files
   (8 Tenant.Application + 13 Producer.Application) has already been read this session (see the
   conversation, or re-read the old `src/Modules/Tenant/Tenant.Application/**` +
   `src/Modules/Producer/Producer.Application/**` files — they are still on disk, untouched).
   Key STRUCTURAL change (not just rename): `ApproveRejectTenantUser.cs` (→
   `ApproveRejectMerchantUser.cs`) currently creates a separate `ProducerTenantAssignment` row via
   `IProducerTenantAssignmentRepository` — DELETE that repository interface entirely and instead
   call `account.Approve(merchantId, now)` directly (the entity now holds `MerchantId` itself). Same
   simplification applies to `ResolveLogin.cs` and `ResolveProducerById.cs` (`ResolveMerchantUserById.cs`) —
   they currently look up `_assignments.FindByAccountIdAsync(...)` for the tenant id; now just read
   `account.MerchantId` directly (throw/deny if null, mirroring the old "assignment is null → deny"
   invariant-violation branch). `SetProducerUserRoles.cs`→`SetMerchantUserRoles.cs` similarly drops
   the assignment lookup, reads `target.MerchantId` directly.
2. **Merchants.Infrastructure** (~13 files: merge `Tenant.Infrastructure` 5 + `Producer.Infrastructure`
   8). EF configs need `ToTable(name, SchemaNames.Merch)` (or `.Admin` for nothing here — all
   Merchants module tables are schema `merch`). One new `MerchantsModuleRegistration.cs` replacing
   both `TenantModuleRegistration.cs` + `ProducerModuleRegistration.cs`. `ProducerRepositories.cs`
   drops the `ProducerTenantAssignmentRepository` implementation (interface deleted per step 1).
3. **Delete** `src/Modules/Tenant/`, `src/Modules/Producer/`, `src/Modules/Identity/`. Update
   `pol-core.slnx` (remove those 3 folders' entries, add one `/src/Modules/Merchants/` folder with 3
   projects; same for `tests/` section once Merchants.Tests exists in phase 8 below).
4. **Admin module**: apply the Admin rename table above across `src/Modules/Admin/**` (Domain first,
   then Application, then Infrastructure). Also add `SchemaNames.Admin` to every `ToTable` call.
   `AdminPermissions.cs`: rename `GroupProducer`/`ProducerApprove`/`ProducerReject` constants values
   per the table above (constant NAMES can stay `GroupProducer` etc. in C# if you prefer, or rename
   to `GroupMerchantUser`/`MerchantUserApprove`/`MerchantUserReject` for consistency — VALUES must
   change to `merchant_user`/`merchant_user.approve`/`merchant_user.reject`).
5. **BuildingBlocks actor-context**: apply the exact-identifier table above
   (`src/BuildingBlocks/BuildingBlocks.Application/{ITenantContext,ITenantScoped,ITenantScope,TenantGuardBehavior}.cs`
   → rename per table, `TenantBindingException`→`MerchantBindingException` via general rule;
   `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/AmbientTenant.cs`→`AmbientActor.cs`;
   `src/Hosts/Api/HttpTenantContext.cs`→`HttpActorContext.cs`;
   `src/Hosts/Worker/WorkerTenantContext.cs`→`WorkerActorContext.cs`). Write new
   `AdminActorContext.cs`. Update `BuildingBlocksInfrastructureRegistration.cs` (registers
   `AmbientTenant`/`ITenantScope` — rename to `AmbientActor`/`IActorScope`).
6. **SessionContextConnectionInterceptor.cs rewrite** per design.md's exact contract (already quoted
   in design.md "SessionContextConnectionInterceptor (contract ใหม่)" section — stamp both keys
   always when bound, sentinel guard throw, unbound = no stamp no throw). Constructor now takes
   `IActorContext`. Keyed "admin" DbContext registration (currently inline in `Program.cs`, search
   `AddTenantAdminScope` — this method's actual definition wasn't read yet this session, find it via
   `grep -rl AddTenantAdminScope src`) must resolve `AdminActorContext` via DI callback per REQ-4.5
   (no hand-built `DbContextOptions` per request — preserve EF model cache).
7. **ToTable(schema) sweep for the 5 untouched modules**: Products, Cart, Checkout, Orders, Payments —
   every `*Configuration.cs` needs `SchemaNames.Shop` (Products/Carts/CartItems/CheckoutSessions/
   Orders) or `SchemaNames.Txn` (PaymentSessions/PspConnections) added to its `ToTable` call, plus
   `TenantId`→`MerchantId` column/property rename throughout (entity + config + every handler/query
   that references `.TenantId`). NOT started — none of these 5 modules' `*Configuration.cs` files
   have been read or touched yet (only their handler/command `.cs` files show as modified in git
   status from task 2's Money work, unrelated to this rename).
8. **Migration reset**: delete all 26 files in
   `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/` + `ProducerDbContextModelSnapshot.cs`.
   Run `dotnet ef migrations add InitialSchema --context PolDbContext --project src/BuildingBlocks/BuildingBlocks.Infrastructure --startup-project src/Hosts/Api`
   ONLY after steps 1-7 compile clean (the model must build first). Then hand-write `SecurityObjects`
   migration — full raw SQL content (fn/procs/policy/grants/RegistrationNotices table) is already
   fully derived from the OLD migrations + design.md's GRANT matrix; see the conversation transcript
   around message timestamps where `AddRlsSecurityPolicy.cs`, `AddProducerIdentityTables.cs`,
   `AddVaultRevealAudit.cs`, `AddOrderSummaryToken.cs` + its two later patches, and
   `AddProducerAccountAdminParity.cs` were read in full — port their raw SQL forward with schema/name
   substitutions per the tables above (e.g. `usp_resolve_order_summary` final body selects
   `Id, TenantId, AmountAmount, AmountCurrency, Status, PaymentSessionId, SummaryTokenExpiresAt FROM
   VCentralPay.Orders` → becomes `Id, MerchantId, AmountAmount, AmountCurrency, Status,
   PaymentSessionId, SummaryTokenExpiresAt FROM shop.Orders`, proc renamed `sec.usp_resolve_order_summary`,
   `WITH EXECUTE AS 'pol_resolver'`). Then hand-write `SeedData` migration (RBAC seed content already
   fully known from `AddAdminRoleRbacTables.cs` + `AddProducerRoleRbacTables.cs` +
   `AddProducerApprovePermissionToAdminCatalog.cs` + `AddAdminMasterDataSeed.cs` — port forward with
   the rename table above; master data GUIDs/values are UNCHANGED, only schema qualifier changes).
9. **Bootstrap**: `docker/bootstrap/01-principals.sql` — remove
   `ALTER ROLE pol_rls_bypass ADD MEMBER pol_admin` line, add guarded `IF ... DROP MEMBER` before it
   (mirrors the existing `IF NOT EXISTS ... ADD MEMBER` guard pattern already in the file); rename
   `pol_webhook_resolver`→`pol_resolver` (2 occurrences: `CREATE USER` + the `ALTER ROLE ADD MEMBER`
   line for it — `pol_vault_auditor` stays unchanged).
10. **Program.cs** (1976 lines, already read in full this session — see transcript). Needs: remove
    `AddGoogleIdTokenAuthentication` call + the whole `GoogleAuthenticationExtensions.cs` file;
    explicit `AddAuthentication(MerchantUserSessionDefaults.AuthenticationScheme)` as default (need to
    define `MerchantUserSessionDefaults` — doesn't exist today, `ProducerSessionAuthenticationHandler`
    hardcodes `SchemeName = "ProducerSession"` as a const, not a `Defaults` class — add one, or just
    reference `MerchantUserSessionAuthenticationHandler.SchemeName` directly for the default-scheme
    call, simpler); every `ITenantContext tenant` parameter → `IActorContext actor` +
    `tenant.TenantId`→`actor.MerchantId` (~30+ call sites, mostly funnel endpoints); policy `"tenant"`
    and the dual-scheme `"producer"` policy both COLLAPSE into ONE `"merchant-user"` policy
    (single-scheme, session-cookie only — the `GateProducerWrite` helper + the
    `Producer:EnforcePermissionsOnWrites` toggle mechanism gets DELETED entirely, since there's no
    more Bearer fallback to gate against — ALL funnel endpoints just
    `.RequireAuthorization("merchant-user")` unconditionally); route renames `/producers`→
    `/merchant-users` (both the anon group and the authenticated group), `/admins/tenants`→
    `/admins/merchants`, `/admins/tenant-users/{subject}/approve|reject`→
    `/admins/merchant-users/{subject}/approve|reject`; every wire DTO/record with `Tenant` in the name
    or a `tenantId`/`TenantId` field renamed per the tables above (e.g. `ProvisionTenantRequest`→
    `ProvisionMerchantRequest`, body key `"tenant"`→`"merchant"`, `AdminAccessibleResponse.Tenants`→
    `.Merchants`, `AssignTenantRequest`→`AssignMerchantRequest`); the webhook endpoint's
    `ITenantScope tenantScope` / `tenantScope.Begin(tenantId.Value)` → `IActorScope actorScope` /
    `actorScope.Begin(merchantId.Value)` (no `UserId` for webhook — worker-style anonymous bind).
    `SecuritySchemeForEndpoint` switch: `"tenant"`/`"producer"` cases collapse to one
    `"merchant-user"` → `"MerchantUserSession"` Scalar security scheme. `HostModuleAssemblies.All`
    array: replace the `Tenant.Infrastructure.TenantModuleRegistration` +
    `Producer.Infrastructure.ProducerModuleRegistration` lines with one
    `Merchants.Infrastructure.MerchantsModuleRegistration` line.
11. **Auth handler files**: `GoogleAuthenticationExtensions.cs` — DELETE (whole file, no longer
    used). `ProducerSessionAuthenticationHandler.cs`→`MerchantUserSessionAuthenticationHandler.cs`
    (rename per table, drop the "no fallback to Bearer" comments since Bearer is gone entirely now,
    simplify `AuthenticateResult.NoResult()` comment). `ProducerPermissionAuthorization.cs`→
    `MerchantUserPermissionAuthorization.cs` (drop the "DUPLICATE-shaped" ponytail comment framing
    since there's no more tenant-Bearer fallback path to reason about — `RequireProducerPermission`→
    `RequireMerchantUserPermission`, `ProducerBoundProducerFilter`→`MerchantBoundFilter` per table,
    `ProducerPermissionParity`→`MerchantUserPermissionParity`). `ProducerHostWiring.cs`→
    `MerchantsHostWiring.cs` per table. `AdminHostWiring.cs` — apply Admin rename table + rename
    `AdminTenantDirectory`/`IAdminTenantDirectory`→`AdminMerchantDirectory`/`IAdminMerchantDirectory`
    (falls out of general Tenant sweep), `GetTenantByCodeAsync`→`GetMerchantByCodeAsync`. There is
    also a `ProducerOidcAuthentication.cs` file (referenced by Program.cs, NOT YET individually read
    this session — read it before touching; it should be structurally identical to
    `AdminOidcAuthentication.cs` which WAS read in full, just for the merchant-user BFF instead of
    admin) → rename to `MerchantUserOidcAuthentication.cs`, scheme name `"ProducerGoogle"`→
    `"MerchantUserGoogle"` (or similar — check the actual current scheme name string first).
12. **Config + wire**: `docker-compose.yml` (`ConnectionStrings__Producer` env doesn't appear
    directly there actually — check again, it builds via `.env`), `docker/entrypoint.sh` line
    `export ConnectionStrings__Producer="$CONN"` → `ConnectionStrings__App`, `docker/migrate-entrypoint.sh`
    `--context ProducerDbContext` → `--context PolDbContext`, `.github/workflows/ci.yml` same
    `--context ProducerDbContext` → `--context PolDbContext` (2 occurrences: the `dotnet ef database
    update` line), `.env.example` `ConnectionStrings__Producer`→`ConnectionStrings__App` (this file IS
    readable/editable). **`.env.prod.example` is permission-denied for Read/Edit in this sandbox** —
    could not touch it; leave as an explicit operator note in the final Evidence/FE-MIGRATION doc.
    `src/Contracts/*.cs` — `CheckoutConfirmed.cs` (already modified by task 2, re-check for leftover
    `TenantId`), `PaymentPaid.cs` (not yet located/read this session — find via
    `grep -rl PaymentPaid src/Contracts`), event `TenantUserRegistrationSubmitted`→
    `MerchantUserRegistrationSubmitted` (defined somewhere in `src/Contracts/` — not yet located,
    search `grep -rl TenantUserRegistrationSubmitted src`).
13. **Architecture.Tests**: schema guard test (allowlist `{shop,txn,admin,merch}`, exception
    `DataProtectionKey`→`dbo`), module list assertion (Merchants replaces Tenant+Producer, Identity
    removed). Not yet located/read this session — find via `grep -rl "HasDefaultSchema\|module list"
    tests/Architecture.Tests`.
14. **Test suite**: merge `tests/Tenant.Tests` + `tests/Producer.Tests` → `tests/Merchants.Tests`
    (mirror the src merge — csproj + rename tests by the same rules). Update `tests/Admin.Tests` per
    Admin rename table. Rewrite `tests/Hosts.Tests` funnel auth tests (currently likely use a Bearer
    JWT test shim — need to switch to session-cookie test auth; NOT yet read this session, find via
    `grep -rl "tenant\|Bearer" tests/Hosts.Tests`). Update `tests/Integration.Tests/IntegrationDb.cs`
    (schema/column/SESSION_CONTEXT key names, `PriceMinorUnits`→already gone per task 2 — verify no
    leftover). None of `tests/` has been touched this session.
15. **Build + test iterate**: `dotnet build pol-core.slnx -warnaserror` until 0 errors/warnings,
    `dotnet test pol-core.slnx --filter "Category!=Integration"` until green.
16. **Fresh-DB verify**: `docker compose down -v && docker compose up -d`, wait for `pol-db-init` to
    finish, `dotnet ef database update --context PolDbContext --project
    src/BuildingBlocks/BuildingBlocks.Infrastructure --startup-project src/Hosts/Api` against
    `:11433`, then `dotnet test tests/Integration.Tests` (needs `.env.integration` sourced — see
    memory `integration-test-local-sql-11434.md`, port 11434 is the isolated integration container,
    not 11433). Docker + dotnet ARE available in this sandbox (confirmed:
    `docker version` → 29.4.0 Server, `dotnet --version` → 10.0.300).
17. **Close task 3**: flip `[x]` in `tasks.md`, write a real Evidence block (actual commands +
    actual output, not projected), note deviations (the `.env.prod.example` sandbox limitation, the
    `MerchantId` naming-collision note, any other judgment calls made along the way that weren't in
    this handoff).

## Do NOT re-litigate (locked, already reasoned through carefully this session)

- Every rename rule in the tables above.
- `MerchantUser.MerchantId` absorption approach (nullable Guid on the entity + guard logic in
  `Approve`) — replaces the dropped `ProducerTenantAssignment` table/repository entirely.
- Admin module keeps its RBAC catalog class names (`AdminRole` etc.) per T6 — only
  AdminAccount/AdminTenantAssignment/AdminAuthAudit/AdminSession/AdminTier move to Platform*.
- `.env.prod.example` cannot be touched in this sandbox (permission deny on the path, confirmed via
  both Read tool and Bash `cat`/`wc` — not a transient error, do not keep retrying it).
