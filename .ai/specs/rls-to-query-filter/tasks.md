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
         narrowness goal is met without touching the existing (still-live) `Merchants.Infrastructure` project.
- [x] 2. **Read floor — global query filter + deny-default + CartItems + IDOR closure**
     Per-entity tenant-key descriptor via `FindProperty` (reject shadow/typo/nullable[except pending merch.Users]/
     wrong-type); instance-member query filter `tenantKey==CurrentMerchant` on every `MerchantRuntimeDbContext` entity +
     merch.Users/RoleAssignments in `MerchantUserDbContext`; `Merchant` self-row on `Id`; `shop.CartItems` denormalized
     `MerchantId` + composite FK `(CartId,MerchantId)→Cart(Id,MerchantId)`; unbound actor ⇒ `Guid.Empty` ⇒ 0 rows;
     by-id loads auto-scope (IDOR closed).
     Satisfies: REQ-1.1-1.6 (read filter incl. outbox/vault-audit, fail-closed), REQ-1.2/2.7 (Merchant self-row),
         `UserSessionAuthenticationHandler`/`HttpActorContext`/T11's Bearer-fallback retirement, not by a new test.
- [x] 3. **Write floor — sealed 4-overload guard (all contexts) + concurrency-token + immutable + CHECK + set-DML ban**
     Sealed override of all 4 SaveChanges overloads on every runtime context through one save-core → default-deny
     `IWriteAuthorizer` (operation/owner/tenant-aware); tenant/owner key = concurrency token + immutable after insert
     (one-time NULL→value carve-out for pending merch.Users); DB `CHECK(<>Empty)` + `CHECK(<>sentinel)`; ban
     `ExecuteUpdate`/`ExecuteDelete` on all runtime entities + bypass primitives outside named op ports.
     REQ-2 (2.1-2.11), REQ-3.5, REQ-5.2, REQ-11.3, REQ-11.4. Depends on: 1.
         builds without `TenantKeyDescriptor.Require` throwing.
- [x] 4. **Admin cross-merchant seam — Super/Scoped + authorization lease + invalidation matrix + merchant-role capability**
     `IAdminQuery` accessible-set floor authoritative (Super=all, Scoped=`admin.MerchantAccess`, fail-closed);
     authorization LEASE (exactly-one no-op update on caller version row, scoped to lease-covered ControlPlane flows +
     provisioning; approve/reject linearize at request boundary); `AuthorizationVersion` invalidation matrix (bump the
     affected user in-tx for Status/Tier/Session/MerchantAccess incl Unassign/RoleAssignment/RolePermission);
     `IMerchantRoleWriter` (own-only) vs `IMerchantRoleReader` (shared+own) vs admin (unrestricted), none returning
     `IQueryable`.
     REQ-4. Depends on: 1, 3.
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 5. **User/session isolation — pre-bind read+write ports + per-owner outbox + escape-hatch ports**
     Owner-key isolation for merch (MerchantUser) + admin (ControlPlane) identity tables; pre-bind READ ports
     (`ISessionByTokenHash`/`IResolveMerchantLoginBySubject`/`IResolveAdminLoginBySubject`) vs WRITE ports
     (`IRegistrationWriter`/`IBindInvitedAdminIdentity`/`ISelfProvisionSuperWriter`/`IApproveRegistrationWriter`/
     `IRejectRegistrationWriter`); per-owner outbox CLR (`MerchantUserOutbox`→`merch.UserOutbox`,
     `MerchantRuntimeOutbox`→`txn.OutboxMessages`) + drain per owner; suppressed-op narrow ports
     (`IWebhookMerchantResolver`/`IOrderSummaryReader`/`IOutboxDrain`/`IVaultAuditAppender`/`IRoleAssignmentCounter`) +
     `IgnoreQueryFilters`/raw-SQL allowlist; no `Find`/`FindAsync` on merchant-scoped entities.
     REQ-9, REQ-5 (5.1/5.3/5.4/5.5/5.6), REQ-1.6. Depends on: 1, 2, 3.
         reimplemented here.
         **[Superseded 2026-07-26 — bugfix-merchant-prebind-wiring]** task 8 never actually performed the
         merchant-user DI flip: the handlers stayed on the FILTERED `IUserRepository`, so every pre-bind
         identity flow (login resolve, correction resubmit, admin approve/reject) was runtime-broken on
         production config. The 4 merchant-user scaffolding ports were replaced by two DI-wired
         application-layer seams — `IAccountResolver`/`MerchantAccountResolver` (reads) and
         `IAccountStore`/`MerchantAccountStore` (tracked pre-bind loads; the write floor still authorizes
         every staged change via the new `AdminApprovalWriteAuthorizer`/`HttpMerchantWriteAuthorizer`
         selection) — and the DML writers `MerchantRegistrationSubmitWriter`/`MerchantRegistrationWriter`
         were deleted. See `.ai/specs/bugfix-merchant-prebind-wiring/`.
- [x] 6. **Vault reveal-audit serialization (replace EXECUTE-AS proc)**
     Applock-based serialization (`sp_getapplock` Exclusive, transaction-owned, check return code) inside a single
     transaction via a narrow per-operation port, replacing `usp_vault_audit_head`; keep unique `(MerchantId, Seq)`
     backstop; payment path stays working on SQL Server.
     REQ-7. Depends on: 1, 3.
         narrow port" clause is now fully satisfied (task 5 already satisfied the clause's read-filter half).
- [x] 7. **Provisioning Super-only UoW — the ONE cross-context write**
     `IProvisioningWriter.ProvisionAsync(spec, callerAdminId, expectedAuthorizationVersion, operationKey)` →
     `ProvisionMerchantResult`; dedicated provisioning-integration assembly (narrow `InternalsVisibleTo` from CP+MR +
     type-level gate — only the named coordinator touches the dual-context primitives); shared `SqlConnection` inside an
     execution-strategy delegate (`verifySucceeded` = ledger verifier), authz-FIRST full recheck
     (`Tier=Super AND Status=Active AND AuthorizationVersion=@expected`, `WITH(UPDLOCK,HOLDLOCK)` after the table),
     caller-bound idempotency ledger `admin.ProvisioningOperations` (immediate parameterized INSERT on a named unique
     index; duplicate/commit-unknown match CallerAdminId + canonical hash), `SaveChanges(false)`→commit→AcceptAllChanges.
     REQ-10, REQ-2.12. Depends on: 1, 3, 4.
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
     REQ-8. Depends on: 1, 2, 3, 4, 5, 6, 7 (needs the final model).
         capability over those ports.
- [x] 9. **Observability — denial/rollback/authz taxonomy + tamper-resistant sink**
     Structured taxonomy (guard/`CanWrite`/owner denial, concurrency exception, CHECK/FK violation, provisioning
     rollback, revoke deny, Super-recheck fail, merchant-role denial, applock timeout, sentinel/Empty hit) with
     actor/target/entity/op/reason/correlation (redacted); durable non-blocking transport → external tamper-resistant
     sink + alerts + retention; per-host `Application Name`.
     REQ-13. Depends on: 3, 4, 7.
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 10. **Canon supersede — docs**
     Update `.ai/shared/ARCHITECTURE.md` (amend the "one DbContext" invariant T1 → 1 migration-owner + 3 runtime),
     `SECURITY_RULES.md:178-184`, `CODING_STANDARDS.md`, `PROJECT_CONTEXT.md`; rewrite `docs/reference/db-connection-and-rls.md`;
     record supersede of rf1 REQ-3.2/3.3/3.7/3.8 + admin-actor-rename REQ-7.4.
     REQ-12. Depends on: 8.
      - viewports: n/a — legacy corpus predates viewport protocol (human checkpoint 2026-08-26)
