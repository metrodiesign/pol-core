# Plan: Remove SQL Server RLS — migration-owner PolDbContext + 3 cluster-aligned runtime contexts
_Round 0 of the RESET review loop (v7). Prior loop hit MAX_ROUNDS=5 as a deadlock; owner chose to continue design as a large project and reopen a fresh loop. v7 = Codex R5 convergence path, grounded in the full 21-transaction inventory. See PLAN-REVIEW-LOG.md._

> FULL DETAIL: `design.md` (v7) + `requirements.md`. Multi-merchant **payment platform**, shared DB, 1 runtime principal (owner-accepted, flagged).
> Why v7 differs from v6: v6 partitioned runtime contexts by AUDIENCE, which (R5 #3) still cut through real transactions — the inventory found 9+ "cross-context" flows. v7 partitions by the ACTUAL co-commit clusters the inventory proves, so 20 of 21 transactions become single-context and only ProvisionMerchant genuinely crosses. That collapse is what makes v7 tractable where v6 was not.

## Goal
Replace DB RLS with an app-layer isolation floor on one DB / one runtime principal, structured so isolation is compile-time-enforced AND every existing transactional unit-of-work stays atomic, with the cross-context surface reduced to a single, well-understood flow.

## Approach
1. **Migration-owner `PolDbContext` (CLR name KEPT, not registered at runtime)** — maps ALL tables + real cross-context FKs, owns the existing single `__EFMigrationsHistory` chain. Keeping the class name is the R5 #7 fix: the designer/snapshot carry `[DbContext(typeof(PolDbContext))]`, so the lineage stays discoverable; renaming orphans it. Gate: `dotnet ef migrations list --context PolDbContext` must show all existing IDs before the forward migration.
2. **Three runtime contexts aligned to co-commit clusters** (proven by the inventory, not audience):
   - `ControlPlaneDbContext` — admin.* + iam.* + cfg.* (+ DataProtectionKeys). Admin-actor writes; handlers 1-15 all single-context here. Not merchant-filtered (admin scope via `IAdminQuery`).
   - `MerchantUserDbContext` — merch.Users/Sessions/ExternalLogins/RegistrationAudits/RoleAssignments + its OWN outbox. Registration/approval/reject/set-roles (17-20) single-context here. Filter `MerchantId==CurrentMerchant`.
   - `MerchantRuntimeDbContext` — shop.* + txn.* + merch.Merchants/VaultSecrets/ProvisioningAudits + its OWN outbox. Business + webhook + provisioning writes (16,21). THE merchant-isolation query-filter floor.
3. **Sealed write floor on all three** — `internal sealed`, all 4 SaveChanges overloads through one save-core → default-deny `IWriteAuthorizer`; tenant/owner key concurrency-token + immutable; write reaches a context only through a narrow port (assembly-enforced). `ExecuteUpdate`/`ExecuteDelete` banned on ALL runtime entities by default (they bypass the guard) — allowed only via named op ports whose DML WHERE carries the tenant/target/state predicate (authorization is a SEPARATE same-tx lease or request-boundary RBAC, not in the WHERE).
4. **The ONE cross-context write = ProvisionMerchant** — an internal provisioning UoW (NOT a public coordinator): one `SqlConnection` created inside an execution-strategy delegate; both `ControlPlaneDbContext` (Super-tier lock/recheck) and `MerchantRuntimeDbContext` (the writes) built bound to that connection, `UseTransaction` on both; `SaveChanges(acceptAllChangesOnSuccess:false)` → commit → `AcceptAllChanges`; idempotency key for commit-unknown; fresh conn+contexts per retry attempt. Fixes R5 #1, #2, #6.
5. **Merchant-owned role capability** — iam.Roles is mixed-audience; merchant role CRUD goes through `IMerchantRoleWriter` enforcing `Role.MerchantId==CurrentMerchant` (no shared/null/foreign), immutable+concurrency-token key, RolePermission via tracked parent. Fixes R5 #4.
6. **Pre-owner-bind READ ports vs WRITE ports** — reads: `ISessionByTokenHash`, `IResolveMerchantLoginBySubject`, `IResolveAdminLoginBySubject` (projection-limited, `AsNoTracking`, audit). Writes (NOT reads): `IRegistrationWriter`, `IBindInvitedAdminIdentity`, `ISelfProvisionSuperWriter`, `IApproveRegistrationWriter`, `IRejectRegistrationWriter` (pending merch.Users lookup by Subject under suppression, conditional DML `MerchantId IS NULL AND Status=Pending`, one-time NULL→merchant transition, idempotent-replay when already Active) — each verifies its own trust root, exact entity/state allowlist, conditional write, atomic, opaque capability. Fixes R5 #5, R2-v7 #1/#5.
7. **Authorization lease vs business revoke — split** — lease = exactly-one no-op update on the caller's authorization-version row (deny unless exactly-one), held in-tx with the write; business revoke keeps natural 0..N semantics (session-family idempotent). Lease scope = revoke/demote-sensitive admin writes (ControlPlane single-context) + provisioning; approve/reject use request-boundary RBAC (single-context). Fixes R5 #9, R2-v7 #1/#4.
8. **Assembly split + Api host boundary** — persistence split by cluster into separate projects, contexts `internal`; forbidden `ProjectReference` fails the build (custom MSBuild, not just arch-lint). Unified `Api.csproj` = declared trusted composition root + arch test that merchant endpoint adapters can't resolve control-plane ports + privileged ports reauthorize every call. Fixes R5 #8.
9. **Per-write-owner outbox** — MerchantUser + MerchantRuntime each own an outbox so their domain+event commit atomically in one context (registration no longer crosses). Fixes R4 #2.
10. **RLS teardown + 1 principal** — remove RLS SQL, SESSION_CONTEXT interceptor, bypass role, EXECUTE-AS procs. Full 1-principal inventory + CI legacy-object assertion. Big-bang `down -v`.
11. **Observability** — denial/rollback/revoke-deny/recheck-fail taxonomy → durable non-blocking → external tamper-resistant sink + alerts + redaction test.

## Key decisions & tradeoffs
1. **Cluster-aligned contexts** — the inventory proves this drops cross-context transactions from 9+ (v6 audience partition) to exactly 1. Isolation stays compile-time; atomicity stays intact; one migration lineage. Amends the "one DbContext" canon (T1).
2. **1 principal** — owner decision (twice); Codex R1 argued for separation. Mitigated but weaker. **Sign-off item.**
3. **One internal provisioning UoW, no generic coordinator** — the whole cross-context surface is one flow; a general Application-level coordinator (v6) was unnecessary machinery (R5 #1).
4. **Keep CLR name PolDbContext for the migration owner** — lineage adoption with zero history surgery (R5 #7).

## Risks / open questions
- Provisioning UoW correctness under failure injection + transient-fault retry (idempotency) + demote-during-provision.
- Assembly split churn across many `.csproj` (contained by cluster boundaries).
- `dotnet ef migrations list` lineage gate must pass before the forward migration is authored.

## Out of scope
- Multi-merchant model, API routes/auth schemes; user/session isolation MECHANISM (verified + tested);
  reintroducing least-privilege principals (owner chose 1 — flagged, not reversed).
