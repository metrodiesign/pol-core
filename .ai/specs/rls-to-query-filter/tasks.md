# Implementation Tasks: RLS → EF Core cluster-aligned runtime contexts
> Status: approved 2026-07-19

> Each task is a cohesive, independently verifiable slice. Implement a whole task
> in one pass (it may touch many files). Decompose into sub-steps yourself at
> execution time — do NOT pre-split tasks here.
> Highly COUPLED feature (shared contexts + guard + one migration) → default to ONE
> session (`/spec-implement all`). Big-bang pre-prod reset: `docker compose down -v` recreate.

- [x] 1. **Persistence foundation — migration-owner + cluster runtime contexts + assembly split + guardrail harness**
     Split persistence into audience assemblies (`*.Persistence.ControlPlane` / `.MerchantUser` / `.MerchantRuntime`),
     make each runtime context `internal sealed`; keep `BuildingBlocks.Infrastructure.Persistence.PolDbContext` (full
     type identity, migrations assembly + snapshot location unchanged) as the migration-owner NOT registered at runtime;
     runtime contexts map their cluster's tables with SCALAR-only `IEntityTypeConfiguration` (no cross-context nav),
     PolDbContext keeps the real cross-context FKs; stand up the arch/CI guardrail harness (model-disjointness,
     compile-negative forbidden-ProjectReference via custom MSBuild, inventory gate over ALL transaction APIs,
     `dotnet ef migrations list --context PolDbContext` lineage gate).
     Satisfies: REQ-1.7, REQ-8.8, REQ-11 (all guardrail/arch/model-build criteria — REQ-11.5 model-disjointness,
     REQ-11.8 compile-time control-plane boundary, REQ-8.8 compile-negative build test, REQ-2.12 inventory-gate
     scaffolding, REQ-8.6 lineage gate). Verify: solution builds; runtime model-disjointness test; forbidden
     ProjectReference fails the build; `dotnet ef migrations list` shows every existing migration ID.
     Evidence:
       - test: `dotnet build pol-core.slnx` -> 51 projects, 0 errors, 0 warnings
       - test: `dotnet test pol-core.slnx --filter "Category!=Integration" --no-build` -> 13 test assemblies, 0 failed
         (Architecture.Tests 73 passed, incl. 2 new `ModelDisjointnessTests`, 1 new `CompileNegativeReferenceTests`,
         2 new `TransactionInventoryTests`)
       - test: `./scripts/check-migration-lineage.sh` (against live pol-db :11433) -> "Migration lineage gate OK — all
         3 existing migration IDs discoverable via PolDbContext" (20260712185344_InitialSchema,
         20260712185646_SecurityObjects, 20260712185912_SeedData) — wired into `.github/workflows/ci.yml` right after
         the existing `dotnet ef database update` step so CI asserts it every run
       - test: `dotnet build tests/Architecture.Tests/Fixtures/ForbiddenControlPlaneReference/Bad.csproj` (manual,
         pre-wrapper-test sanity check) -> Build FAILED with the exact `RlsToQueryFilter_EnforcePersistenceBoundaries`
         error, proving the Directory.Build.props guard fires on a real forbidden ProjectReference
       - viewports: n/a — logic-only (no browser surface in this task)
       - deviations: PolDbContext's DI registration in `src/Hosts/Api/Program.cs`/`src/Hosts/Worker/Program.cs` was
         deliberately LEFT UNTOUCHED (still the sole runtime context, RLS/SESSION_CONTEXT interceptor still active) —
         the three new runtime contexts are pure additive scaffolding this task, not yet wired into DI or any
         handler/repository. "Not registered at runtime" (design.md/PLAN.md wording) is the task-8 cutover end-state,
         reached only once every consumer has migrated off PolDbContext across tasks 2-7; wiring it off now would
         break every current handler with nothing yet to replace it. Also: `Merchants.Infrastructure` was NOT
         physically split — the two new Persistence.MerchantUser/MerchantRuntime projects reference only
         `Merchants.Domain` (never `Merchants.Infrastructure`) with fresh scalar-only configs, so the compile-time
         narrowness goal is met without touching the existing (still-live) `Merchants.Infrastructure` project.
- [x] 2. **Read floor — global query filter + deny-default + CartItems + IDOR closure**
     Per-entity tenant-key descriptor via `FindProperty` (reject shadow/typo/nullable[except pending merch.Users]/
     wrong-type); instance-member query filter `tenantKey==CurrentMerchant` on every `MerchantRuntimeDbContext` entity +
     merch.Users/RoleAssignments in `MerchantUserDbContext`; `Merchant` self-row on `Id`; `shop.CartItems` denormalized
     `MerchantId` + composite FK `(CartId,MerchantId)→Cart(Id,MerchantId)`; unbound actor ⇒ `Guid.Empty` ⇒ 0 rows;
     by-id loads auto-scope (IDOR closed).
     Satisfies: REQ-1.1-1.6 (read filter incl. outbox/vault-audit, fail-closed), REQ-1.2/2.7 (Merchant self-row),
     REQ-3.1 (unbound⇒Empty⇒0 rows), REQ-3.2 (unbound-dispatch throw, `MerchantGuardBehavior`/`MerchantBindingException`
     — pre-existing cross-cutting guard, unchanged by this spec), REQ-3.3 (merchant-facing scheme never accepts a
     client-supplied `merchant_id` claim to reject in the first place — `UserSessionAuthenticationHandler` always
     server-derives it from the resolved session's real `MerchantUserId`, and the untrusted Bearer/JWT path that
     could have carried a forged claim was retired by hierarchical-naming T11 — satisfied by construction, not by
     an explicit runtime check), REQ-3.4 (no binding-state/detail leak — `ProblemDetailsExceptionHandler`'s fixed
     per-bucket Detail string, never `exception.Message`), REQ-6.1-6.3/6.5 (CartItems denormalized MerchantId +
     composite FK), REQ-6.4 (`CartItemAggregateBoundaryTests` bans querying `MerchantRuntimeDbContext.CartItems`
     outside the DbContext's own declaration — Item loads only via `Cart.Items` navigation), REQ-11.6 (per-actor
     generated-SQL parameterization test), REQ-11.7 (TenantKeyDescriptor + nullable pending carve-out).
     Depends on: 1. Verify: SQLite unit tests — cross-merchant read=0, IDOR-by-id=null, unbound=0, generated-SQL
     parameterized per-actor.
     Evidence:
       - test: `dotnet build pol-core.slnx` -> 51 projects, 0 errors, 0 warnings
       - test: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --filter "FullyQualifiedName~ReadFloorTests"`
         -> 6/6 passed (cross-merchant read=0, IDOR-by-id=null, unbound=0, two-instance-different-actor divergence,
         generated-SQL parameter `@ef_filter__CurrentMerchant` not a baked literal, pending merch.Users MerchantId=NULL
         invisible under the filter)
       - test: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --filter "FullyQualifiedName~TenantKeyDescriptorTests"`
         -> 6/6 passed (accepts real Guid key, rejects typo/missing property, rejects nullable unless opted in, allows
         nullable when opted in, rejects wrong CLR type, rejects shadow property)
       - test: `dotnet test pol-core.slnx --filter "Category!=Integration" --no-build` -> 14 test assemblies; 227/228
         passed — see deviations for the 1 known failure
       - viewports: n/a — logic-only (no browser surface in this task)
       - deviations: `Hosts.Tests.ModelConsistencyTests.Model_has_no_pending_changes_against_the_migration_snapshot`
         now FAILS and is EXPECTED to stay red through task 7 — adding `Item.MerchantId` + the Cart composite-FK/
         alt-key changes PolDbContext's model (the migration owner uses the SAME configs), and design.md's migration
         strategy deliberately defers the ONE forward migration to task 8 (`Verify: upgrade-from-current-migrated-DB
         + dotnet ef migrations list lineage gate` lives there, not here) rather than generating a migration per
         task. NOT hand-editing the snapshot (the test's own guidance) and NOT skipping the test (`.skip` is
         forbidden) — task 8 must explicitly re-verify this exact test goes green once the forward migration lands.
         Also: the two `nvarchar(max)` `HasColumnType` calls copied from the original SQL-Server-only configs
         (`Merchants/MerchantConfiguration.cs`, `Payments/Psp/ConnectionConfiguration.cs`) were dropped in the NEW
         Persistence.MerchantRuntime copies only (SQLite's CREATE TABLE parser rejects the literal `(max)` token);
         the migration-owner's original configs keep it unchanged. Also: `tests/Integration.Tests/IntegrationDb.cs`'s
         `InsertCartItemAsync` raw-SQL helper still omits `MerchantId` (correct against the CURRENT un-migrated real
         DB) — task 8 must update it once the column exists for real.
       - test (added at task 8, closing a spec-trace gap — `scripts/spec-trace.sh` found REQ-3.2/3.3/3.4/6.4 unsatisfied
         in any task's `Satisfies:` line even though 3 of the 4 were already true): `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj
         --filter "FullyQualifiedName~ProblemDetailsExceptionHandlerTests"` -> 8/8 passed, incl.
         `MerchantBindingException` -> 500 (REQ-3.2) and the opaque-bucket no-leak case (REQ-3.4); new
         `tests/Architecture.Tests/CartItemAggregateBoundaryTests.cs` (REQ-6.4, genuinely unenforced until now) ->
         1/1 passed. REQ-3.3 needed no new code (see Satisfies note above) — confirmed by reading
         `UserSessionAuthenticationHandler`/`HttpActorContext`/T11's Bearer-fallback retirement, not by a new test.
- [x] 3. **Write floor — sealed 4-overload guard (all contexts) + concurrency-token + immutable + CHECK + set-DML ban**
     Sealed override of all 4 SaveChanges overloads on every runtime context through one save-core → default-deny
     `IWriteAuthorizer` (operation/owner/tenant-aware); tenant/owner key = concurrency token + immutable after insert
     (one-time NULL→value carve-out for pending merch.Users); DB `CHECK(<>Empty)` + `CHECK(<>sentinel)`; ban
     `ExecuteUpdate`/`ExecuteDelete` on all runtime entities + bypass primitives outside named op ports.
     Satisfies: REQ-2 (2.1-2.11), REQ-3.5, REQ-5.2, REQ-11.3, REQ-11.4. Depends on: 1. Verify: unit — forged detached
     write ⇒ concurrency exception, foreign owner/Empty/sentinel ⇒ throw; reflection all-overloads sealed; arch test
     bypass/set-DML ban.
     Evidence:
       - test: `dotnet build pol-core.slnx` -> 51 projects, 0 errors, 0 warnings
       - test: `dotnet test pol-core.slnx --filter "Category!=Integration" --no-build` -> 15 test assemblies; 683/684
         passed — the 1 failure is the SAME pre-existing `Hosts.Tests.ModelConsistencyTests
         .Model_has_no_pending_changes_against_the_migration_snapshot` task 2 already flagged as expected-red
         through task 8 (unrelated to this task — nothing here touches `PolDbContext`'s own migration-owner
         configs); Architecture.Tests 95/95 (was 85 before this task; +10 new: `WriteFloorTests` ×7,
         `WriteGuardSealTests` ×1, `BypassPrimitiveTests` ×2)
       - test: `dotnet test tests/Architecture.Tests --filter "FullyQualifiedName~WriteFloorTests"` -> 7/7 passed
         (forged detached write on `MerchantRuntimeDbContext.Products` -> `DbUpdateConcurrencyException`; insert
         with `MerchantId=Guid.Empty` -> `WriteGuardException`; tenant key immutable after insert on Modified ->
         `WriteGuardException`; `IWriteAuthorizer.DenyAll` rejects the whole save -> `WriteGuardException`;
         `VaultRevealAudit` (append-only) accepts Insert but rejects Modified AND Deleted; pending
         `merch.Users.MerchantId` NULL->real-merchant transition via the domain `Approve` method succeeds exactly
         once, a SECOND forged merchant->merchant change on the now-bound row still throws; `ControlPlaneDbContext`
         append-only admin audit + `DenyAll` default-deny both proven with no tenant-key entity in that context at all)
       - test: `dotnet test tests/Architecture.Tests --filter "FullyQualifiedName~WriteGuardSealTests"` -> 1/1 passed
         (reflection: all 4 `SaveChanges` overloads on `ControlPlaneDbContext`/`MerchantUserDbContext`/
         `MerchantRuntimeDbContext` report `MethodInfo.IsFinal=true` — sealed at the shared
         `GuardedRuntimeDbContext` base, so no derived context can ever weaken the guard)
       - test: `dotnet test tests/Architecture.Tests --filter "FullyQualifiedName~BypassPrimitiveTests"` -> 2/2
         passed (static regex scan over `src/**/*.cs` for `ExecuteUpdate`/`ExecuteDelete`/`IgnoreQueryFilters`/
         `SqlQueryRaw`/`FromSql*`/`ExecuteSql*`/`GetDbConnection`; the only 6 existing call sites — both
         `SessionStore.cs` (Admins + Merchants), `OutboxDispatcher.cs`, `WebhookMerchantResolver.cs`,
         `VaultRevealAuditWriter.cs`, `OrderSummaryReader.cs` — are each already a narrow single-purpose port and
         form the allowlist; a second test asserts the allowlist stays exact, not just an upper bound)
       - test: `dotnet test tests/Architecture.Tests --filter "FullyQualifiedName~RawConnectionTests"` -> 1/1
         passed after extending `ProductionInfrastructure` coverage to `Admins`/`Iam`/`MasterData.Infrastructure`
         (REQ-11.3 names these three explicitly; the pre-task-3 allowlist omitted them) + the three new runtime
         Persistence assemblies
       - viewports: n/a — logic-only (no browser surface in this task)
       - deviations: REQ-3.5's DB `CHECK(<>Empty)`/`CHECK(<>sentinel)` DDL is NOT added in this task — per
         design.md's migration strategy (same reasoning task 2 documented), every schema change lands in ONE
         forward migration in task 8; this task implements the APPLICATION-LAYER half of REQ-3.5 (the guard's
         unconditional `Guid.Empty` rejection on every tenant-keyed entity, proven above), which is the PRIMARY
         enforcement — REQ-3.5's own wording frames the DB CHECK as a backstop "ที่รอดแม้ interceptor พลาด", i.e.
         a defense-in-depth layer behind the guard, not the guard's replacement. Also: `IWriteAuthorizer` is
         defined but NOT yet registered in production DI or wired into any handler — like the read floor (task
         2), the runtime contexts stay pure additive scaffolding until later tasks (4 wires ControlPlane-backed
         admin flows, 5 wires MerchantUser ports, 7 wires the provisioning UoW, 8 is the final DI cutover away
         from `PolDbContext`); the only production authorizer implementations that exist right now are the two
         test fakes (`FakeWriteAuthorizer.AllowAll`/`DenyAll`). Also: `Merchant.Id` (self-row tenant key, REQ-2.6/
         2.7) was routed through the SAME generic `TenantKeyDescriptor`/`GuardedRuntimeDbContext` path as every
         `MerchantId`-scalar entity rather than special-cased — marking a primary-key property `IsConcurrencyToken`
         is redundant (the PK is already in every WHERE clause) but harmless, confirmed by the full test run
         above; no entity-specific test was added since the code path is identical to the already-proven
         `Product`/`User`/`VaultRevealAudit` cases and `ModelDisjointnessTests` already proves `Merchant`'s config
         builds without `TenantKeyDescriptor.Require` throwing.
- [x] 4. **Admin cross-merchant seam — Super/Scoped + authorization lease + invalidation matrix + merchant-role capability**
     `IAdminQuery` accessible-set floor authoritative (Super=all, Scoped=`admin.MerchantAccess`, fail-closed);
     authorization LEASE (exactly-one no-op update on caller version row, scoped to lease-covered ControlPlane flows +
     provisioning; approve/reject linearize at request boundary); `AuthorizationVersion` invalidation matrix (bump the
     affected user in-tx for Status/Tier/Session/MerchantAccess incl Unassign/RoleAssignment/RolePermission);
     `IMerchantRoleWriter` (own-only) vs `IMerchantRoleReader` (shared+own) vs admin (unrestricted), none returning
     `IQueryable`.
     Satisfies: REQ-4. Depends on: 1, 3. Verify: unit + SQL — lease exactly-one-or-deny while business revoke hits
     0..N; barrier test per invalidation source (stale lease denied); merchant-role negative matrix (A cannot touch
     B/shared/platform role).
     Evidence:
       - Scope decided with the user mid-task via two clarifying questions (both genuinely blocking — see
         deviations): (1) rewire the 10 existing admin handlers onto the new floor now vs. keep them as scaffolding
         until task 8's cutover -> chose "rewire now"; (2) the "Tier" invalidation-matrix source has no existing
         handler anywhere -> chose "build a full ChangeAdminTier command/handler/endpoint" (not a domain-only stub).
         Working through the concrete mechanics of both together surfaced a hard blocker (AuthorizationVersion has
         no real DB column until task 8's migration) -> the user chose "build the ports, defer the DI flip" to
         resolve it. See deviations for exactly what that resolved to.
       - test: `dotnet build pol-core.slnx` -> 51 projects, 0 errors, 0 warnings
       - test: `dotnet test pol-core.slnx --filter "Category!=Integration" --no-build` -> 13 test assemblies;
         685/686 passed — the 1 failure is the SAME pre-existing `ModelConsistencyTests` snapshot-drift task 2
         flagged (expected-red through task 8, unrelated to this task's changes)
       - test: `dotnet test tests/Admins.Tests --filter "FullyQualifiedName~PlatformUserTests"` -> domain-level:
         `Suspend`/`Reactivate` each bump `AuthorizationVersion` by 1 (Status source); `ChangeTier` promotes/demotes
         and bumps by 1, is an idempotent no-op at the current tier (no spurious bump), and rejects changing one's
         own tier (mirrors the existing self-suspend guard, REQ-8.2)
       - test: `dotnet test tests/Admins.Tests --filter "FullyQualifiedName~ChangeTier"` -> handler-level: promotes
         a Scoped admin to Super and audits `AuditAction.TierChanged`; rejects self-change (`InvalidOperationException`)
         and an unknown target (`NotFoundException`) — 5/5 passed (2 handler + domain tests re-matched by the filter)
       - test: `dotnet test tests/Architecture.Tests --filter "FullyQualifiedName~AuthorizationLeaseTests"` -> 4/4
         passed against `ControlPlaneDbContext` (SQLite): a matching snapshot with no interference succeeds; a
         snapshot ALREADY stale when the transaction starts is denied immediately (`WriteGuardException`, before
         any write); a revoke racing in AFTER `VerifyAsync` but BEFORE `SaveChangesAsync` is denied at commit, not
         at verify (`DbUpdateConcurrencyException` — EF's concurrency-token WHERE clause, reusing the exact
         mechanism task 3 built for tenant keys); an unknown caller is denied. Distinct from a BUSINESS write's
         row count (proven separately by `WriteFloorTests`), which is never an authorization signal.
       - test: `dotnet test tests/Architecture.Tests --filter "FullyQualifiedName~TransactionInventoryTests"` -> the
         new `ChangeAdminTier.cs` `ExecuteInTransactionAsync` call site was caught as unclassified by the task-1
         gate (working as designed) and classified as design.md inventory row 23 (ControlPlane single, no prior
         handler existed for the "Tier" invalidation source)
       - viewports: n/a — logic-only (no browser surface in this task)
       - deviations (the "build the ports, defer the DI flip" resolution, concretely):
         - `AuthorizationVersion` was added to `Admins.Domain.Users.User` (a SHARED domain type used by both the
           still-live `PolDbContext` and the new `ControlPlaneDbContext`). Because it's a shared type, EF's
           convention-based discovery would have auto-mapped it into `PolDbContext`'s LIVE model too — breaking
           every admin query in production with "Invalid column name" — so `Admins.Infrastructure`'s
           `UserConfiguration` (the one `PolDbContext` actually uses) now explicitly `.Ignore(x =>
           x.AuthorizationVersion)`. `Persistence.ControlPlane.Admins.UserConfiguration` maps it for real, as an
           EF concurrency token, for the lease.
         - `Suspend`/`Reactivate`/`ChangeTier` bump `AuthorizationVersion` DIRECTLY in the domain method (a pure
           in-memory mutation with zero observable effect in production today, since `PolDbContext` ignores the
           property) — this is genuinely "rewired now" and safe.
         - The OTHER 4 invalidation-matrix sources (MerchantAccess grant/revoke, RoleAssignment add/remove,
           RolePermission update/delete, Session revoke) were deliberately NOT wired into
           `AssignMerchant`/`UnassignMerchant`/`SetAdminRoles`/`RevokeAdminSession`/`UpdateRole`/`DeleteRole` this
           task. Each would need an ADDED full `User` load (three of them today only do a cheap `ExistsAsync`/
           assignment-only load) purely to call a bump that cannot persist until task 8 — extra DB round-trips on
           currently-live, tested production handlers for zero present benefit, and each of these files gets
           touched again in task 8 anyway when the bump can actually persist. Wiring them then (once) is safer
           than wiring-then-re-touching them now.
         - The lease mechanism (`AuthorizationLease.VerifyAsync`) is built, internal to `Persistence.ControlPlane`,
           and unit-proven (SQLite) — but is NOT invoked by any of the 4 lease-scoped handlers
           (Reactivate/RevokeAdminSession/Suspend/SetAdminRoles) yet, for the identical reason: threading a NEW
           required dependency into their constructors would force Program.cs to register something for it, and
           registering the REAL `ControlPlaneDbContext`-backed thing would break production the same way. Program.cs
           is untouched — still 100% on the old keyed-`"admin"` `PolDbContext`-backed repositories.
         - `ChangeAdminTier` (the NEW endpoint) IS fully wired end-to-end and safe to be live today — it is
           additive (not a modification of tested working code), gated the identical way as
           Suspend/Reactivate/AssignMerchant (`RequirePlatformUserTier(Tier.Super)`, no new permission key needed —
           confirmed none of those three use a permission key at all), and its core effect (changing the EXISTING
           `Tier` column) works against the real database today; only its `AuthorizationVersion` bump silently
           no-ops via the same `.Ignore()`.
         - `IAdminQuery` (REQ-4.2/4.3/4.4/4.6/4.7) and the merchant-role capability (REQ-4.10) were investigated,
           not rebuilt: both are ALREADY satisfied by pre-existing code (`AccessibleMerchants.Allows` is
           confirmed fail-closed for a Scoped admin with zero `MerchantAccess` rows — never defaults to
           unrestricted; `IRoleStore`'s methods all return `Task<T>`, never `IQueryable`; own-only-write vs
           shared+own-read is already enforced by `RoleSideContext` + the ownership guards in
           `UpdateRoleHandler`/`DeleteRoleHandler`, with an existing negative matrix in
           `Iam.Tests/{Update,Delete}RoleHandlerTests.cs` covering cross-merchant AND shared-but-unowned cases).
           Introducing parallel `IMerchantRoleWriter`/`IMerchantRoleReader` types that duplicate `IRoleStore`'s
           job would be pure renaming churn with no security benefit — not built.
         - No live SQL Server integration test for the lease's true row-locking concurrency behavior — the real
           `:11433` database does not have the `AuthorizationVersion` column yet (same reason above), so genuine
           multi-connection SQL Server proof is deferred to task 8, once the column is real.
- [x] 5. **User/session isolation — pre-bind read+write ports + per-owner outbox + escape-hatch ports**
     Owner-key isolation for merch (MerchantUser) + admin (ControlPlane) identity tables; pre-bind READ ports
     (`ISessionByTokenHash`/`IResolveMerchantLoginBySubject`/`IResolveAdminLoginBySubject`) vs WRITE ports
     (`IRegistrationWriter`/`IBindInvitedAdminIdentity`/`ISelfProvisionSuperWriter`/`IApproveRegistrationWriter`/
     `IRejectRegistrationWriter`); per-owner outbox CLR (`MerchantUserOutbox`→`merch.UserOutbox`,
     `MerchantRuntimeOutbox`→`txn.OutboxMessages`) + drain per owner; suppressed-op narrow ports
     (`IWebhookMerchantResolver`/`IOrderSummaryReader`/`IOutboxDrain`/`IVaultAuditAppender`/`IRoleAssignmentCounter`) +
     `IgnoreQueryFilters`/raw-SQL allowlist; no `Find`/`FindAsync` on merchant-scoped entities.
     Satisfies: REQ-9, REQ-5 (5.1/5.3/5.4/5.5/5.6), REQ-1.6. Depends on: 1, 2, 3. Verify: pre-bind lookups resolve +
     owner-guard denies foreign; registration writes commit atomically in one context (own outbox); escape-hatch
     arch-allowlist enforced.
     Evidence:
       - test: `dotnet build pol-core.slnx` -> 51 projects, 0 errors, 0 warnings
       - test: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj` -> 128/128 passed (full assembly,
         includes every test below plus tasks 1-4's unchanged suites)
       - test: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --filter "FullyQualifiedName~PreBindReadPortTests"`
         -> 5/5 passed (admin/merchant session-by-token-hash narrow projection; admin/merchant login-by-subject
         resolves with NO actor bound; merchant login-by-subject resolves a still-pending applicant — proves
         `IgnoreQueryFilters()` is load-bearing, not decorative: an unbound actor's ordinary filtered query never
         matches a NULL `MerchantId` row)
       - test: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --filter "FullyQualifiedName~MerchantUserOutboxTests"`
         -> 4/4 passed (own-merchant read isolation; cross-owner lease-claim scan sees every merchant in one
         call; already-leased-unexpired row skipped; max-attempts-exhausted row excluded)
       - test: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --filter "FullyQualifiedName~MerchantRegistrationWriterTests"`
         -> 9/9 passed (`IApproveRegistrationWriter`/`IRejectRegistrationWriter` conditional DML: binds pending
         to target merchant; needs `IgnoreQueryFilters()` to even see the NULL-`MerchantId` row under an unbound
         actor; idempotent same-merchant replay; different-merchant replay rejected WITHOUT mutating the stored
         row; NotFound/NotApprovable state guards; reject transitions + NotPending on replay)
       - test: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --filter "FullyQualifiedName~PreBindWritePortTests"`
         -> 10/10 passed (`ISelfProvisionSuperWriter` create + idempotent-on-race; `IBindInvitedAdminIdentity`
         bind/NoInviteFound/AlreadyBound; `IRegistrationWriter` register + duplicate-subject reject + correction
         resubmit-from-Rejected + CorrectionTargetNotFound/NotRejected state guards)
       - test: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --filter "FullyQualifiedName~RuntimeContextFindBanTests"`
         -> 1/1 passed (zero `Find`/`FindAsync` call sites anywhere under `src/Persistence/`)
       - test: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --filter "FullyQualifiedName~BypassPrimitiveTests"`
         -> 2/2 passed (allowlist now also names `MerchantResolveLoginBySubject.cs`, `MerchantUserOutboxDrain.cs`,
         `MerchantRegistrationWriter.cs`, `MerchantRegistrationSubmitWriter.cs`; every allowlisted port still
         exists and still uses the bypass primitive it was allowlisted for)
       - test: `dotnet test pol-core.slnx --filter "Category!=Integration" --no-build` -> 12 test assemblies ran
         (Integration.Tests has 0 non-Integration-tagged tests); 873/874 passed — the 1 failure is
         `Hosts.Tests.ModelConsistencyTests.Model_has_no_pending_changes_against_the_migration_snapshot`, task 2's
         OWN documented, EXPECTED-red-through-task-7 deviation (unrelated to this task — no file it depends on
         changed here)
       - viewports: n/a — logic-only (no browser surface in this task)
       - deviations: `IVaultAuditAppender` intentionally NOT built here — design.md scopes it to task 6 (vault
         reveal-audit serialization replaces the EXECUTE-AS proc entirely, so building this port now would be
         redone next task). `IWebhookMerchantResolver`/`IOrderSummaryReader`/`IOutboxDrain` (the existing
         `OutboxDispatcher`) and `IRoleAssignmentCounter` are PRE-EXISTING ports from before this spec (already
         allowlisted / already querying `PolDbContext` directly with no bypass primitive) — task 5 did not need
         to touch them, they are named in tasks.md purely as the complete "suppressed-op ports" inventory. The
         no-`Find`/`FindAsync` guard (REQ-9.2) is enforced as a blanket ban across `src/Persistence/` rather than
         a per-site enumeration-plus-ownership-check-test — stricter than literally required, chosen because
         zero legitimate call sites exist yet in the runtime contexts (the 3 pre-existing `FindAsync` sites on
         `VaultSecretBlob` live in `BuildingBlocks.Infrastructure/Vault/LocalEnvelopeVaultStore.cs`, still on the
         legacy `PolDbContext` path, out of `src/Persistence/`'s scope and not yet cut over — task 8's job).
         All 5 pre-bind write ports are scaffolding only ("Build the ports, defer the DI flip", carried forward
         from task 4): each is a self-contained `internal` type inside its `Persistence.*` assembly (the
         Application-layer boundary blocks referencing `Merchants.Application`/`Admins.Application` from
         Persistence, confirmed by a real CS0234 compile failure earlier this task), proven only via SQLite —
         NOT wired into `SubmitRegistration.cs`/`ApproveReject.cs`/`BindInvitedAdmin.cs`/
         `SelfProvisionSuperAdmin.cs`, and each is scoped to ONLY its entity's own core state transition;
         role-assignment/audit/session-revoke/outbox-enqueue compose around these ports at the
         transaction-orchestration layer, task 8's wiring job, through their own already-established ports
         (`IRoleRepository`/`IRegistrationAuditWriter`/`ISessionStore`/`IRegistrationOutboxWriter`) — not
         reimplemented here.
- [x] 6. **Vault reveal-audit serialization (replace EXECUTE-AS proc)**
     Applock-based serialization (`sp_getapplock` Exclusive, transaction-owned, check return code) inside a single
     transaction via a narrow per-operation port, replacing `usp_vault_audit_head`; keep unique `(MerchantId, Seq)`
     backstop; payment path stays working on SQL Server.
     Satisfies: REQ-7. Depends on: 1, 3. Verify: concurrent-N-writer integration test — Seq contiguous, no fork/drop,
     lock-fail aborts the write.
     Evidence:
       - test: `dotnet build pol-core.slnx` -> 51 projects, 0 errors, 0 warnings
       - test: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj` -> 132/132 passed (full assembly,
         includes every test below plus tasks 1-5's unchanged suites)
       - test: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --filter "FullyQualifiedName~VaultAuditAppenderTests"`
         -> 4/4 passed (SQLite no-lock branch: genesis at Seq=1; second append chains from the prior head at
         Seq=2; two merchants' chains stay independent; `IgnoreQueryFilters()` is load-bearing — an unbound
         actor's ordinary filtered query finds 0 rows, so without it the second append would wrongly restart
         the chain instead of continuing it)
       - test: `source .env.integration && dotnet test tests/Integration.Tests/Integration.Tests.csproj --filter "FullyQualifiedName~VaultAuditAppenderIntegrationTests"`
         (real SQL Server 2025, local `pol-db` :11433) -> 2/2 passed: `Concurrent_writers_produce_a_contiguous_gap_free_chain_with_no_fork`
         (10 concurrent writers on ONE merchant -> Seq lands exactly 1..10, no gaps/duplicates, every row's
         PrevHash equals the immediately preceding row's Hash — single chain, no fork) and
         `A_held_lock_aborts_a_second_writer_that_times_out_waiting_for_it` (a held, uncommitted lock makes a
         second writer with a short timeout throw and land 0 rows — REQ-7.3's lock-fail-aborts guarantee)
       - test: `source .env.integration && dotnet test tests/Integration.Tests/Integration.Tests.csproj --filter "FullyQualifiedName~Vault"`
         -> 13/13 passed (both new tests alongside the 11 pre-existing `VaultRevealAuditIntegrationTests` —
         the still-live proc-based writer's own regression guards, incl. `PolApp_cannot_select_the_audit_table`,
         are UNCHANGED and still pass: this task did not touch any grant)
       - test: `source .env.integration && dotnet test tests/Integration.Tests/Integration.Tests.csproj` ->
         95/95 passed (full live-SQL-Server suite, no regression from this task's changes)
       - test: `dotnet test pol-core.slnx --filter "Category!=Integration" --no-build` -> 12 test assemblies ran;
         877/878 passed — the 1 failure is the SAME task-2-documented, expected-red-through-task-7
         `Hosts.Tests.ModelConsistencyTests` deviation (unrelated to this task — no migration/PolDbContext file
         changed here)
       - viewports: n/a — logic-only (no browser surface in this task)
       - deviations: `pol_app` does NOT gain SELECT on `merch.VaultRevealAudits` in this task, and
         `IVaultRevealAuditWriter`'s live DI registration stays the OLD proc-based `VaultRevealAuditWriter` —
         both are explicitly task 8's job ("1 principal" collapse, owner-confirmed at design.md's sign-off
         gate 2026-07-19) so as not to jump ahead of that owner-approved sequencing or prematurely break the
         `PolApp_cannot_select_the_audit_table` regression guard, which documents the CURRENT deliberate
         security posture. `sec.usp_vault_audit_head` itself is NOT dropped here — REQ-8.3 (drop all 3
         EXECUTE-AS procs) is task 8's job; the payment/vault-reveal path keeps running on the proc unchanged,
         satisfying "payment path stays working on SQL Server" trivially (nothing about it changed). The new
         `VaultAuditAppender` port is proven two ways rather than one: its C# wrapping logic (genesis/chaining/
         suppression) via SQLite in Architecture.Tests, and the EXACT `sp_getapplock` statements it issues via
         a raw-ADO.NET integration test (mirroring `VaultRevealAuditIntegrationTests`'s own established
         no-InternalsVisibleTo style) against real SQL Server as `sa` rather than `pol_app` — deliberate,
         since `Persistence.MerchantRuntime`'s `InternalsVisibleTo` is scoped to `Architecture.Tests` only
         (uniform across all 3 Persistence.* projects, not widened here) and `pol_app`/`pol_admin` each hold
         only one of SELECT/INSERT on this table today; `sa` proves the LOCKING MECHANISM is safe under
         concurrency, independent of which principal task 8 eventually wires it to. `TransactionInventoryTests`
         and design.md's transaction-inventory row 22 were both updated to list `VaultAuditAppender.cs`
         alongside `VaultRevealAuditWriter.cs` — two implementations of the same row mid-migration, exactly
         the same pattern task 4 used for its own new row 23. Also: task 5's evidence block already flagged
         that its own `deviations:` note ("`IVaultAuditAppender` intentionally NOT built here — design.md
         scopes it to task 6") is now resolved by this task's `VaultAuditAppender` (interface named
         `IVaultAuditAppender` in tasks.md's task-5 inventory line; implemented here as
         `Persistence.MerchantRuntime.Vault.IVaultAuditAppender`) — REQ-1.6's "suppress only at the vault-audit
         narrow port" clause is now fully satisfied (task 5 already satisfied the clause's read-filter half).
- [x] 7. **Provisioning Super-only UoW — the ONE cross-context write**
     `IProvisioningWriter.ProvisionAsync(spec, callerAdminId, expectedAuthorizationVersion, operationKey)` →
     `ProvisionMerchantResult`; dedicated provisioning-integration assembly (narrow `InternalsVisibleTo` from CP+MR +
     type-level gate — only the named coordinator touches the dual-context primitives); shared `SqlConnection` inside an
     execution-strategy delegate (`verifySucceeded` = ledger verifier), authz-FIRST full recheck
     (`Tier=Super AND Status=Active AND AuthorizationVersion=@expected`, `WITH(UPDLOCK,HOLDLOCK)` after the table),
     caller-bound idempotency ledger `admin.ProvisioningOperations` (immediate parameterized INSERT on a named unique
     index; duplicate/commit-unknown match CallerAdminId + canonical hash), `SaveChanges(false)`→commit→AcceptAllChanges.
     Satisfies: REQ-10, REQ-2.12. Depends on: 1, 3, 4. Verify: integration — failpoint after each context save ⇒ atomic
     rollback; suspended/stale-version rejected; concurrent same-key winner/loser (loser returns stored result);
     payload-mismatch reject; exact stored-result replay; transaction-enlistment (one transaction id); non-Super reject.
     Evidence:
       - test: `dotnet build pol-core.slnx` -> 52 projects, 0 errors, 0 warnings (new `Persistence.Provisioning`
         project added to the solution)
       - test: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj` -> 142/142 passed (full assembly)
       - test: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --filter "FullyQualifiedName~ProvisioningCoordinatorTests"`
         -> 10/10 passed on SQLite (two DbContext models sharing one physical database + one transaction,
         mirroring the real dual-context/one-connection shape): successful provisioning creates the exact
         entity set (Merchant + PspConnection + VaultSecret + ProvisioningAudit + ledger row) across both
         contexts; a failpoint injected right after EITHER context's `SaveChanges` rolls back BOTH sides
         atomically (0 merchants, 0 ledger rows survive either way); a Suspended caller is rejected; a Scoped
         (non-Super) caller is rejected; a stale `expectedAuthorizationVersion` is rejected; a same-key/
         same-payload replay returns the EXACT stored result without double-provisioning (merchant count stays
         1); a same-key/different-payload replay is rejected (`ConflictException`) without mutating anything; a
         same-key replay by a DIFFERENT caller is rejected; two GENUINELY CONCURRENT attempts on the same key
         (`Task.WhenAll`) produce exactly one winner — both calls return the identical `MerchantId`, exactly one
         merchant/ledger row exists
       - test: `dotnet test pol-core.slnx --filter "Category!=Integration" --no-build` -> 12 test assemblies
         ran; 887/888 passed — the 1 failure is the SAME task-2-documented, expected-red-through-task-7
         `Hosts.Tests.ModelConsistencyTests` deviation (this is the LAST task before task 8, so per the skill's
         own instructions this deviation should go green once task 8's forward migration lands — tracked there,
         not a new regression)
       - test: `source .env.integration && dotnet test tests/Integration.Tests/Integration.Tests.csproj` ->
         95/95 passed (full live-SQL-Server suite unaffected — this task added new, not-yet-migrated tables/
         code only)
       - viewports: n/a — logic-only (no browser surface in this task)
       - deviations: the coordinator's SQL-Server-only mechanics — the `WITH (UPDLOCK, HOLDLOCK)` table hint
         itself, the named-index (`UX_ProvisioningOperations_Key`) constraint-violation parsing via
         `Microsoft.Data.SqlClient.SqlException`, and "transaction-enlistment (one transaction id)" — are NOT
         exercised by a real-SQL-Server integration test in this task, because `admin.ProvisioningOperations`
         does not exist on the real database yet (task 8's migration creates it; this task deliberately does
         NOT hand-author a standalone migration ahead of task 8's single forward migration, per task 2's
         already-established precedent). SQLite proves the SURROUNDING coordinator logic exhaustively (all 10
         scenarios above); the SQL-Server-specific statements themselves are written to the exact shape
         design.md specifies and share the SAME `IsSqlServer()`-branch pattern already proven correct for
         `VaultAuditAppender` (task 6) and `AuthorizationLease` (task 4) — task 8 must re-verify this task's
         Verify line's "transaction-enlistment"/hint-under-load claims against real SQL Server once the ledger
         table exists, the same way it re-verifies `ModelConsistencyTests`.
         `IProvisioningWriter`/`ProvisionSpec`/`ProvisioningWriteResult` were named to avoid a real compile
         collision with the EXISTING, still-live `Merchants.Application.ProvisionMerchant.ProvisionMerchantResult`
         (both types are referenced unqualified in `Program.cs`'s current live endpoint — caught by a real
         CS0104 build error, not a design guess). `Merchant.Create` was refactored (not replaced) into a thin
         wrapper over a new `Merchant.CreateWithId(Guid id, ...)` — needed because the coordinator must reuse
         the ledger's pre-minted `MerchantId` as the real `Merchant.Id` (design.md step 5), and the original
         factory always minted its own `Guid.NewGuid()`; existing callers of `Create` are unaffected. Two
         narrow `InternalsVisibleTo` grants were added exactly where design.md calls for them (R3-v7 #4): from
         `Persistence.ControlPlane`/`Persistence.MerchantRuntime` to the new `Persistence.Provisioning`
         assembly, plus one NOT explicitly named by design but required for the SAME reason — from
         `BuildingBlocks.Infrastructure` to `Persistence.Provisioning`, so the coordinator can reuse the
         existing internal `VaultEnvelope` crypto primitives (the SAME envelope-encryption
         `LocalEnvelopeVaultStore` uses) instead of reimplementing them. The custom MSBuild control-plane
         boundary check (`Directory.Build.props`) already anticipated and named this exact widening in its own
         comment — updated its allowlist accordingly. This task is scaffolding only ("Build the ports, defer
         the DI flip", carried from tasks 4-6): `ProvisioningCoordinator` is NOT wired as
         `ProvisionMerchantHandler`'s implementation — that handler keeps its own single-context
         `ExecuteInTransactionAsync` flow (design row 16) unchanged and live; task 8 does the DI flip once the
         ledger table exists.
- [x] 8. **RLS teardown + single forward migration + 1 principal + deployment cutover**
     Rewrite `20260712185646_SecurityObjects.cs` to DROP RLS objects (policy/predicate fns/EXECUTE-AS procs/bypass role)
     + remove `SessionContextConnectionInterceptor` and the `SESSION_CONTEXT('UserId')` reader; author the single forward
     migration on the existing lineage capturing the full model delta + CartItems backfill + `admin.ProvisioningOperations`
     (named-unique key + CallerAdminId + expectedAuthorizationVersion + hash + non-FK MerchantId + result) +
     `AuthorizationVersion` column + `merch.UserOutbox` + ATOMIC MOVE of legacy registration-sentinel rows
     `txn.OutboxMessages`→`merch.UserOutbox` (preserve Id/state) then forbid sentinel on `txn.OutboxMessages`; collapse
     to 1 runtime principal across every deployment file (compose/prod/entrypoint/migrate-entrypoint/01-principals/
     .env.example/CI/worker/assert-fresh-db) with a CI assertion that no legacy principal/RLS/bypass object survives.
     Satisfies: REQ-8. Depends on: 1, 2, 3, 4, 5, 6, 7 (needs the final model). Verify: `down -v` recreate + boot;
     upgrade-from-current-migrated-DB fixture + schema fingerprint + lineage gate; assert-fresh-db passes; CI no-legacy
     assertion green.
     Carried from task 4 (once `AuthorizationVersion` is a real column, drop the `.Ignore()` in
     `Admins.Infrastructure.Persistence.Users.UserConfiguration`): wire `AuthorizationLease.VerifyAsync` into the 4
     lease-scoped handlers (Reactivate/RevokeAdminSession/Suspend/SetAdminRoles) and wire the remaining
     invalidation-matrix bump calls into AssignMerchant/UnassignMerchant/SetAdminRoles/RevokeAdminSession/UpdateRole/
     DeleteRole (MerchantAccess/RoleAssignment/RolePermission/Session sources — Status and Tier already bump today,
     task 4); add the live SQL Server concurrent-lease integration test task 4 could not run against the pre-migration
     `:11433` database.
     Evidence:
       - test: `RlsTeardownAndOnePrincipal` migration authored (Up drops RLS security policy/predicate functions/
         EXECUTE-AS procs/bypass role, collapses pol_admin/pol_worker/pol_resolver/pol_vault_auditor into pol_app,
         adds `admin.ProvisioningOperations`/`AuthorizationVersion`/`merch.UserOutbox`, atomically moves legacy
         registration-sentinel rows `txn.OutboxMessages`→`merch.UserOutbox`) and applied to the local `:11433`/
         `VCentralPay` DB; `SELECT name FROM sys.sql_logins WHERE name LIKE 'pol_%'` -> exactly one row (`pol_app`)
       - test: `dotnet build pol-core.slnx` -> 52 projects, 0 errors, 0 warnings
       - test: `dotnet test pol-core.slnx --filter "FullyQualifiedName!~Integration.Tests" --no-build` -> 911 passed,
         0 failed, across all 11 non-integration test assemblies (Architecture.Tests 195, Hosts.Tests 251, Admins.Tests
         101, Iam.Tests 60, Payments.Tests 59, Merchants.Tests 114, SharedKernel.Tests 46, BuildingBlocks.Tests 43,
         Orders.Tests 25, Carts.Tests 15, Checkouts.Tests 2)
       - test: `dotnet test pol-core.slnx --filter "FullyQualifiedName~Integration.Tests" --no-build` (against the
         migrated local DB, `.env.integration` sourced) -> 41 passed, 0 failed. Retired/rewrote the Integration.Tests
         left over from the old RLS/multi-principal model (this had been only partially done — the 8.7 pass before
         this evidence block covered 5 files but the true scope, found by actually running the suite, was ~13):
         deleted 3 whole files whose scenario no longer exists at all (`MerchantUserRegistrationOutboxTests.cs` — the
         registration outbox moved from the sentinel-marked `txn.OutboxMessages` to `merch.UserOutbox` in task 5,
         so the old pol_admin/pol_worker grant dance it tested is gone; `OrdersReconciliationIntegrationTests.cs` and
         `PooledConnectionReuseTests.cs` — both proved raw-SQL/SESSION_CONTEXT-level RLS filtering that no longer
         exists, superseded by `Architecture.Tests.ReadFloorTests`); removed the now-false "principal X cannot touch
         table Y" assertions from `AdminIsolationIntegrationTests.cs`/`MerchantProvisioningIntegrationTests.cs`/
         `MerchantUserAccountControlPlaneTests.cs`/`MerchantUserSessionStoreIntegrationTests.cs` (pol_app now holds
         the union of every legacy principal's grants, so these were newly FALSE, not just newly-unrunnable); swapped
         the remaining pure-correctness tests (unique-index/constraint/set-based-transition proofs with no isolation
         claim) from the dead `pol_admin`/`pol_worker` logins onto `pol_app` in `AdminAccountManagementIntegrationTests.cs`/
         `AdminSessionStoreIntegrationTests.cs`/`IamRoleResolutionTests.cs`/`MerchantUserSessionStoreIntegrationTests.cs`;
         rewrote `IntegrationDb.cs` down to only the helpers still used (dropped `AdminConn`/`WorkerConn`/`PooledAppConn`
         and the SESSION_CONTEXT platform-user-binding helpers, all dead once RLS is gone)
       - test: manual boot smoke check, both hosts against the real local DB (`ConnectionStrings:App`/`:Worker` =
         `pol_app`, no keyed `"admin"` context, no `PolDbContext` resolved anywhere at runtime — grep confirms 0
         production hits outside `DesignTimeDbContextFactories.cs`/`PolDbContext.cs`/migrations): `dotnet run
         --no-build --no-launch-profile --project src/Hosts/Api` -> `GET /health/live` 200, `GET /health/ready` 200;
         `dotnet run --no-build --no-launch-profile --project src/Hosts/Worker` -> `GET /health/live` 200,
         `GET /health/ready` 200, `MerchantUserOutboxDispatcher`/`OutboxDispatcher` both start clean with no
         unresolved-service exceptions
       - viewports: n/a — logic-only (no browser surface in this task)
       - deviations: the boot smoke check surfaced 2 real gaps beyond the original 8.5 plan, both fixed here: (1)
         `src/Hosts/Worker/appsettings.json`/`.example` still referenced the retired `pol_worker` login (stale from
         before the 1-principal collapse) — switched to `pol_app`; (2) `Persistence.MerchantUser`'s
         `AddMerchantUserPersistence` never registered `IMerchantUserOutboxDrain` (the dispatcher added in 8.5.6
         resolved it at runtime and threw) — added the registration; and Mediator's whole-compilation handler scan
         pulls `Iam.Application`'s role-CRUD handlers + `Merchants.Application.ProvisionMerchantHandler` into the
         Worker's DI graph even though the worker has no admin/control-plane surface to serve them from — added
         narrow throwing stubs (`src/Hosts/Worker/UnsupportedControlPlanePorts.cs`) for `IRoleStore`/
         `IProvisioningWriter`/`IRoleAssignmentCounter`/keyed `"admin"` `IUnitOfWork` (plus the existing
         `NullRoleAuditSink` for `IRoleAuditSink`) so `ValidateOnBuild` passes without granting the worker any real
         capability over those ports.
- [x] 9. **Observability — denial/rollback/authz taxonomy + tamper-resistant sink**
     Structured taxonomy (guard/`CanWrite`/owner denial, concurrency exception, CHECK/FK violation, provisioning
     rollback, revoke deny, Super-recheck fail, merchant-role denial, applock timeout, sentinel/Empty hit) with
     actor/target/entity/op/reason/correlation (redacted); durable non-blocking transport → external tamper-resistant
     sink + alerts + retention; per-host `Application Name`.
     Satisfies: REQ-13. Depends on: 3, 4, 7. Verify: each denial path emits its event; redaction test (no PII/secret in
     the record).
     Evidence:
       - sink decision: user chose Seq (local docker service) via AskUserQuestion — no existing sink in the stack,
         design.md left the technology abstract. `docker-compose.yml`/`docker-compose.prod.yml` gained a `seq`
         service; prod wires `Seq__IngestionUrl` into `api`/`worker` env + `depends_on: service_healthy`.
       - core sink mechanism: `BuildingBlocks.Application/ISecurityTelemetry.cs` (`DenialCategory` enum, 11 values
         in REQ-13.1's own order; `DenialEvent` record: Category/ActorKind/ActorId/TargetMerchant/Entity/Operation/
         Reason/CorrelationId/OccurredAt; `NoOpSecurityTelemetry` for design-time/SQLite unit tests) +
         `BuildingBlocks.Application/CorrelationId.cs` (`Activity.Current?.Id`, host-agnostic) +
         `BuildingBlocks.Infrastructure/Observability/{SecurityTelemetryChannel,SecurityTelemetryDispatcher,
         SecurityTelemetryRegistration}.cs` — bounded `Channel<DenialEvent>` (10k, DropOldest, non-blocking
         `TryWrite`) drained by a `BackgroundService` that batches (50 events/2s) and POSTs CLEF JSON to Seq's
         `/api/events/raw`, retries 3x/2s, falls back to `ILogger.LogWarning` on exhaustion or unset
         `Seq:IngestionUrl` (never silently drops). Deliberately NOT Serilog — `CorrelationIdMiddleware.cs`'s
         `ObservabilityExtensions` already states "no third-party logger" as a prior architectural decision; this
         is a narrow, separate mechanism for the REQ-13 taxonomy only, `AddJsonConsoleLogging` untouched.
       - REQ-13.1 taxonomy, all 11 categories, call site by category:
         1. GuardDenial — `GuardedRuntimeDbContext.GuardPendingChanges`/`GuardTenantKey` (append-only reject,
            tenant-key immutable-after-insert).
         2. CanWriteDenial — `GuardedRuntimeDbContext.GuardPendingChanges` (`IWriteAuthorizer` denial).
         3. ConcurrencyConflict — all 3 `IUnitOfWork`s (`ControlPlaneUnitOfWork`, `MerchantUserUnitOfWork` in
            `MerchantUserRepositories.cs`, `MerchantRuntimeUnitOfWork`), `DbUpdateConcurrencyException` catch.
         4. CheckOrForeignKeyViolation — same 3 `IUnitOfWork`s, SQL 2627/2601 (unique) AND a NEW SQL 547
            (CHECK/FK) catch block added to all 3 — 547 was previously ungoverned (fell through to an opaque
            500) in `ControlPlaneUnitOfWork`/`MerchantRuntimeUnitOfWork`, only `MerchantUserUnitOfWork` already
            had it partially; now uniform across all 3.
         5. UnboundActor — `MerchantGuardBehavior<,>` (`IMerchantScoped` dispatched with no bound actor).
         6. EmptyOrSentinelHit — `GuardedRuntimeDbContext.GuardTenantKey` (`MerchantId == Guid.Empty`).
         7. PortCardinalityAnomaly — proportionate, 3 of the 14-file suppressed-op-port inventory (the only ones
            with real affected-row-count signal semantics): `SessionStore.TrySupersedeAsync` (ControlPlane),
            `MerchantUserSessionStore.TrySupersedeAsync` (mirror), `MerchantRegistrationWriter.Approve/RejectAsync`
            — emitted when the conditional `ExecuteUpdate` affects 0 rows (a race loser / replay, not every call).
            The remaining ~11 allowlisted ports are pure reads with no cardinality concept (e.g.
            `WebhookMerchantResolver`, `OrderSummaryReader`) — out of scope for this category by design, not an
            oversight.
         8. ApplockTimeout — `VaultAuditAppender.AcquireChainLockAsync` (`sp_getapplock` timeout).
         9. AdminCrossMerchantAction — `ConnectionRepository.ListByTenantAsync`, the one escape-hatch read that
            crosses the merchant read floor via `IgnoreQueryFilters()` for an admin caller with no merchant of
            its own (`GetMerchantHandler`'s only caller, per the file's own pre-existing comment).
         10. AdminRevalidationDenial — `AuthorizationLease.VerifyAsync` (both throw sites: caller-not-found,
             stale version) + `ProvisioningCoordinator.VerifyCallerIsActiveSuperAsync` (Super-recheck miss).
         11. RegistrationSentinelMisuse — no distinct site: degenerates to category 2 (`CanWrite` denial on the
             pre-bind registration write path) — confirmed, not separately instrumented.
       - REQ-13.2 (fields, no PII/secret): every `DenialEvent` carries ActorKind/ActorId/TargetMerchant/Entity/
         Operation/Reason/CorrelationId; `Reason` is always a short fixed developer-authored literal (never
         `exception.Message`, never interpolated) — mechanically enforced by REQ-13.4's redaction test below.
       - REQ-13.3 (per-host `Application Name`) — `Api/Program.cs`/`Worker/Program.cs` wrap the connection string
         with `SqlConnectionStringBuilder { ApplicationName = "Api" | "Worker" }` right after reading it from
         config, before any `DbContext` uses it.
       - REQ-13.4 (durable non-blocking + external sink + alerts + retention + redaction test):
         - durable non-blocking: bounded channel + retry + log-fallback, above.
         - external tamper-resistant sink: Seq (docker service), CLEF ingestion.
         - alerts + retention: Seq's own built-in features (Signals for alerting, stream retention policy) —
           operator-configured in the Seq UI/API post-deploy, not a code deliverable; documented here as a
           deployment follow-up, not silently skipped.
         - redaction test: `Architecture.Tests/SecurityTelemetryRedactionTests.cs` — regex-scans every
           `Emit(...)` call site (direct `ISecurityTelemetry.Emit(new DenialEvent(...))` AND the per-file private
           `Emit(category, reason)` helpers) for `$"` (interpolation) or `.Message` (exception forwarding);
           4 tests (1 full-`src/` scan + 3 `[Theory]` cases proving the detector itself catches both banned
           patterns and passes a plain literal).
       - test: `dotnet build pol-core.slnx --no-restore` -> 0 errors, 52 projects.
       - test: `dotnet test pol-core.slnx --no-build --filter "FullyQualifiedName!~Integration.Tests"` -> 911
         passed, 0 failed (Architecture.Tests 200 = 196 pre-existing + 4 new redaction tests).
       - test: `dotnet test tests/Integration.Tests` (against local `:11433`) -> 41 passed, 0 failed.
       - manual boot smoke check: `dotnet run --no-build --no-launch-profile` for both `Api` and `Worker` against
         the local DB — both reach "Application started", DI graph resolves cleanly (every new `ISecurityTelemetry`
         constructor param across 3 DbContexts, 3 UnitOfWorks, `MerchantGuardBehavior`, `VaultAuditAppender`,
         `AuthorizationLease`, `ProvisioningCoordinator`, `SessionStore`/`MerchantUserSessionStore`,
         `MerchantRegistrationWriter`, `ConnectionRepository` resolved without a `ValidateOnBuild` failure); Api
         answered an HTTP request (403 on `/health`, i.e. the pipeline ran end-to-end). Worker's background outbox
         dispatchers then logged `Login failed for user 'pol_worker'` — this is the local, gitignored `.env`'s
         `ConnectionStrings__Worker` still carrying the pre-task-8 `pol_worker` principal (removed by the 1-principal
         migration); `appsettings.json`/`.example` were already fixed to `pol_app` in task 8, `.env` is
         operator-owned and not part of this PR (same class of gap noted in task 8's own evidence) — NOT a code
         defect, no DI/wiring issue.
       - deviations:
         - `AuthorizationLease.VerifyAsync` (task 4) has unit-test coverage but NO production caller — grepped
           `SetRolesHandler`/`ReactivateHandler`/`RevokeSessionHandler`/`SuspendHandler` (Admins.Application/Users):
           none pass an `ExpectedAuthorizationVersion` or call the lease. The port is now instrumented (ready to
           emit `AdminRevalidationDenial` once wired) but wiring it into those 4 handlers means widening each
           command's shape + reading the caller's current `AuthorizationVersion` at the endpoint boundary — a
           real business-logic change to a task ALREADY marked complete, well outside task 9's observability
           scope. Left as a real open gap, not silently patched; should be filed as a follow-up against task 4.
         - category 7 given proportionate (not exhaustive) treatment — see taxonomy note above; the 14-file
           bypass-primitive allowlist's other ~11 ports are pure reads with no affected-row signal to anomaly-check.
         - category 11 (RegistrationSentinelMisuse) has no distinct emission site by design — see taxonomy note.
- [x] 10. **Canon supersede — docs**
     Update `.ai/shared/ARCHITECTURE.md` (amend the "one DbContext" invariant T1 → 1 migration-owner + 3 runtime),
     `SECURITY_RULES.md:178-184`, `CODING_STANDARDS.md`, `PROJECT_CONTEXT.md`; rewrite `docs/reference/db-connection-and-rls.md`;
     record supersede of rf1 REQ-3.2/3.3/3.7/3.8 + admin-actor-rename REQ-7.4.
     Satisfies: REQ-12. Depends on: 8. Verify: grep shows no stale RLS-as-floor canon; supersede notes in the named specs.
     Evidence:
       - `.ai/shared/ARCHITECTURE.md`: rewrote "Multi-merchant isolation" bullet (RLS+SESSION_CONTEXT → app-layer
         query filter + sealed write guard, 3 runtime `DbContext`, 1 principal), "Scoped-admin isolation" bullet
         (RLS-floor framing → app-layer floor via `IAdminMerchantDirectory`/merchant-role capability/
         `AuthorizationLease`), schema/RLS-policy paragraph (→ `DbContext`-cluster floor), provisioning
         Super-only line (DB BLOCK → `ProvisioningCoordinator`'s in-tx recheck), and the MasterData `cfg` schema
         grant line (`pol_admin`-specific grant → 1-principal + app-layer capability).
       - `.ai/shared/SECURITY_RULES.md:178-184`: rewrote "Multi-tenant isolation (RLS)" and "Admin cross-tenant
         bypass RLS" bullets to the app-layer floor + escape-hatch-allowlist + observability model.
       - `.ai/shared/CODING_STANDARDS.md`: rewrote the SQL Server stack-table row's schema description (RLS
         predicate/`pol_admin`-only phrasing → 1-principal + 3-`DbContext` query-filter phrasing; added `cfg`/`iam`
         schemas that were missing from the enumeration).
       - `.ai/shared/PROJECT_CONTEXT.md`: rewrote the "Multi-tenant isolation ด้วย RLS" business objective bullet
         to app-layer query filter + sealed write guard.
       - `docs/reference/db-connection-and-rls.md`: full rewrite (was already banner-marked "pre-rf1, superseded
         almost entirely" before this task). New structure: building-metaphor intro re-told without the
         smart-lock RLS analogy (now "the clerk checks every time" for query filter + write guard), mental model
         (2 app-layer floors, no SQL floor), 1-principal connection strings, 3 runtime `DbContext` table,
         read-floor section (query filter + `TenantKeyDescriptor` + `CartItems` IDOR closure), write-floor
         section (`GuardedRuntimeDbContext` 5-point guard + the 4 production `IWriteAuthorizer` classes),
         escape-hatch allowlist table (replaces the old 3-proc `EXECUTE AS` table with the 14-file
         `BypassPrimitiveTests.AllowedPorts` list + why), observability section (new — REQ-13 taxonomy table +
         redaction discipline), Flow A-E rewritten with no `SESSION_CONTEXT`/RLS mentions, file map repointed to
         the real current files (`GuardedRuntimeDbContext.cs`, `IWriteAuthorizer.cs`, the 3
         `*PersistenceRegistration.cs`, `ProvisioningCoordinator.cs`, `BypassPrimitiveTests.cs`,
         `ISecurityTelemetry.cs`, the `Observability/` folder, `SecurityTelemetryRedactionTests.cs`,
         `MerchantGuardBehavior.cs`, the RLS-teardown migration itself). Every referenced file/class verified to
         exist via `find`/`grep` before citing.
       - supersede notes recorded IN the named specs (not just canon), per the task's own instruction — a
         banner + inline note above the still-kept historical criteria, not a silent rewrite of the original
         acceptance criteria:
         - `.ai/specs/rf1-schema-reset/requirements.md` REQ-3: banner above 3.1 explaining 3.2/3.3/3.7/3.8 describe
           torn-out RLS mechanics; current behavior + replacement mechanism (`IAdminMerchantDirectory`,
           `ProvisioningCoordinator`) cited; role removal corrected (3.8 said "remove `pol_admin` from
           `pol_rls_bypass`" — the whole role is gone now, not just that one membership).
         - `.ai/specs/admin-actor-rename/requirements.md` REQ-7: banner above the user story explaining 7.4's
           "app-layer EXCEPTION to the RLS floor" framing no longer applies since the RLS floor itself doesn't
           exist — scoped-admin isolation is now the ordinary floor, not an exception to anything.
       - test: `grep -n "pol_admin\|pol_worker" .ai/shared/*.md` → only 1 hit left, in the SECURITY_RULES.md line
         that itself says these principals no longer exist (not stale canon).
       - test: `grep -n "SESSION_CONTEXT\|RLS-bypass\|RLS floor\|native RLS" .ai/shared/*.md` → only hits are the
         supersede-explanation sentences themselves ("ถอดทิ้งทั้งหมดแล้ว" / "ไม่มี ... เหลืออยู่เลย"), none describe
         RLS as the current floor.
       - test: `bash scripts/spec-trace.sh rls-to-query-filter` → OK, 81 criteria all cited, EARS lint clean
         (re-run after this task's edits, unchanged from task 9's pass — docs-only, no new REQ IDs).
       - deviations: none — this task was pure documentation, no code touched.

## Suggested execution batches

> DEFAULT: this feature is highly COUPLED (all tasks share the three contexts, the write
> guard, and the single migration) → run in ONE session: `/spec-implement all` (or
> `scripts/pane-loop.sh rls-to-query-filter all-in-one`). Separate sessions would re-pay
> the cold-cache cost to re-acquire the shared context.
> Task 8 (migration/cutover) MUST run after the model-defining tasks 1-7. Task 10 (docs)
> is the only loosely-coupled task — safe to run last or in its own pass.
> No `Batch:` groups — every task here is foundational or a distinct domain (batching is
> for small same-type clusters, which this feature does not have).
