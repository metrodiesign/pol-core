# Design: RLS → EF Core cluster-aligned runtime contexts (migration-owner + minimal cross-context)
> Status: approved 2026-07-18, amended 2026-07-19

> Design-First. v2 spec-architect · v3/v4 Codex R1/R2 (single-context) · v5 split-context (R3) · v6 hybrid (R4).
> **v7 = Codex R5 convergence: runtime contexts partitioned along the ACTUAL co-commit clusters proven by the full
> 21-transaction inventory (not audience). Result: 20/21 transactions are single-context; exactly ONE flow
> (ProvisionMerchant) genuinely crosses contexts. Migration-owner keeps CLR name `PolDbContext` (EF binds lineage by
> type). Coordinator retired in favour of ONE internal provisioning UoW.** Context7 EF Core 10 verified.
> Scope acknowledged: LARGE re-architecture (not a simplification) — owner accepted after the round-4 finding.

## Architecture Overview

- **`PolDbContext` (migration-owner, CLR name KEPT, NOT registered at runtime)** — maps ALL tables + real
  cross-context FKs; owns the **single existing `__EFMigrationsHistory` chain**. Keeping the CLR type name is the fix
  for R5 #7: the designer/snapshot already carry `[DbContext(typeof(PolDbContext))]`, so the existing migration IDs
  stay discoverable. Renaming the class would orphan the chain. Forward migration continues it (drop RLS objects, add
  CHECK/composite-FK/CartItems column + per-owner outbox tables, preserve `merch.RegistrationNotices`).
- **Runtime contexts partitioned by CO-COMMIT CLUSTER** (proven by the inventory below, not by audience). Each maps a
  table subset with scalar FKs only (no cross-context navigation), owns its own query filter (where scoped) + a
  **sealed write guard (all 4 SaveChanges overloads)** + its own outbox where it emits events.
- **Exactly ONE cross-context write flow** — `ProvisionMerchant` (locks a Super-tier row in ControlPlane, writes the
  new merchant's rows in MerchantRuntime). It runs through ONE internal provisioning UoW (shared connection +
  transaction), NOT a general Application-level coordinator. All other 20 transactions are single-context.
- **Real assembly split** — merchant-facing persistence in separate projects from control-plane, contexts `internal`,
  so merchant code cannot even name `ControlPlaneDbContext`. Api composition root is a declared trusted exception
  (R5 #8).

### Context topology (partition by co-commit cluster; scalar FK only at runtime)
| Runtime context | Tables (write-owner cluster) | Own outbox | Filter | Actor |
|---|---|---|---|---|
| **ControlPlaneDbContext** | admin.Users/Sessions/UserAudits/AuthAudits/RoleAssignments/MerchantAccess/ProvisioningOperations, iam.Roles/RolePermissions/Permissions/PermissionGroups, cfg.Positions/Offices/Levels/Divisions, dbo.DataProtectionKeys | **none** (no ControlPlane event producer) | admin accessible-set (`IAdminQuery`) / shared read | admin |
| **MerchantUserDbContext** | merch.Users/Sessions/ExternalLogins/AuthAudits/RegistrationAudits/RegistrationNotices/RoleAssignments; `MerchantUserOutbox` (own CLR) | merchant-user (own CLR) | `MerchantId==CurrentMerchant` (merch.Users + RoleAssignments via parent) | merchant + admin-on-behalf |
| **MerchantRuntimeDbContext** | shop.Products/Carts/CartItems/CheckoutSessions/Orders, txn.PaymentSessions/PspConnections/IdempotencyRecords, `MerchantRuntimeOutbox` (own CLR), merch.Merchants(key=Id)/VaultSecrets/VaultRevealAudits/ProvisioningAudits | merchant-runtime (own CLR) | `tenantKey==CurrentMerchant` uniform (isolation floor) | merchant (+ provisioning writer) |
| **PolDbContext** (migration only) | ALL tables + real cross-context FKs | — | none (never opened at runtime) | — |

> **Outbox is per-owner with an owner-specific CLR type** (R1-v7 #8) — `MerchantUserOutbox` (→ new `merch.UserOutbox`)
> and `MerchantRuntimeOutbox` (→ reuses existing `txn.OutboxMessages`) are distinct entities/tables/dispatchers,
> honouring "one entity → one runtime context". ControlPlane has NO outbox (no handler 1-15 enqueues an event). A shared
> `OutboxMessage` CLR reused across contexts is forbidden.
>
> **ControlPlane manifest is exhaustive** (R2 #8): also includes `admin.AuthAudits` (append-only), `iam.Permissions` +
> `iam.PermissionGroups` (read-only except sanctioned seed/migration). The model-disjointness test fails if ANY mapped
> entity is unassigned to a runtime context.

> **Why these three:** the 21-transaction inventory (below) shows admin.* + iam.* + cfg.* always co-commit under an
> admin actor (handlers 1-15); merch user/role/registration always co-commit (17-20); shop/txn/provisioning always
> co-commit (16, 21). Drawing the boundary on those clusters makes every transaction except ProvisionMerchant land in
> exactly one context — this is the direct answer to R5 #3 (audience boundaries cut through transactions; cluster
> boundaries do not).

### Transaction inventory (R5 #3) — every `ExecuteInTransactionAsync`, classified
| # | Handler | Write cluster(s) | Cross-context? | Mechanism |
|---|---|---|---|---|
| 1 | AssignMerchant | admin.MerchantAccess + admin.UserAudits | no | ControlPlane single |
| 2 | BindInvitedAdmin | admin.Users (bind Subject) | no | ControlPlane single, **write port** (§pre-bind) |
| 3 | CreateScopedAdmin | admin.Users + admin.UserAudits | no | ControlPlane single |
| 4 | ReactivateAdmin | admin.Users + admin.Sessions + admin.UserAudits | no | ControlPlane single |
| 5 | RevokeAdminSession | admin.Sessions + admin.UserAudits | no | ControlPlane single |
| 6 | SelfProvisionSuperAdmin | admin.Users + admin.RoleAssignments + admin.UserAudits | no | ControlPlane single, **write port** (allowlist-gated) |
| 7 | SetAdminRoles | admin.RoleAssignments + admin.UserAudits (reads iam.Roles) | no | ControlPlane single |
| 8 | SuspendAdmin | admin.Users + admin.UserAudits | no | ControlPlane single |
| 9 | UnassignMerchant | admin.MerchantAccess + admin.UserAudits | no | ControlPlane single |
| 10 | UpdateAdminProfile | admin.Users + admin.UserAudits (reads cfg.*) | no | ControlPlane single |
| 11 | CreateRole (Iam) | iam.Roles/RolePermissions + admin.UserAudits[if admin] | no | ControlPlane single; **merchant actor → `IMerchantRoleWriter`** (§role cap) |
| 12 | DeleteRole (Iam) | iam.* + admin.UserAudits[if admin] | no | ControlPlane single (reads assignment counts via port) |
| 13 | UpdateRole (Iam) | iam.* + admin.UserAudits[if admin] | no | ControlPlane single; merchant actor → `IMerchantRoleWriter` |
| 14 | Reference-list Create (Division/Level/Office/Position stores — masterdata-split: typed x4, 2 tx sites/store) | cfg.* | no | ControlPlane single |
| 15 | Reference-list Update (Division/Level/Office/Position stores — masterdata-split) | cfg.* | no | ControlPlane single |
| 16 | **ProvisionMerchant** | merch.Merchants + txn.PspConnections + merch.VaultSecrets + merch.ProvisioningAudits | **YES** (locks ControlPlane admin.Users.Tier) | **provisioning UoW** (§provisioning); task 7 adds `ProvisioningCoordinator` (`Persistence.Provisioning`, internal, behind `IProvisioningWriter`) implementing the FULL mechanism (execution-strategy retry + `admin.ProvisioningOperations` idempotency ledger + UPDLOCK/HOLDLOCK authz recheck) — proven standalone via SQLite, not yet the live `ProvisionMerchantHandler` implementation (task 8: the ledger table doesn't exist on the real DB until its migration lands) |
| 17 | Approve (Merchants) | merch.Users (**NULL→merchant** bind) + merch.RoleAssignments + merch.RegistrationAudits (reads iam.Roles) | no | MerchantUser single, **approve write port** (§pre-bind, one-time tenant-key transition) |
| 18 | Reject (Merchants) | merch.Users (pending) + merch.Sessions + merch.RegistrationAudits | no | MerchantUser single, **reject write port** (§pre-bind, suppressed pending lookup) |
| 19 | SetUserRoles (Merchants) | merch.RoleAssignments (reads merch.Users + iam.Roles) | no | MerchantUser single; iam read via port |
| 20 | SubmitRegistration | merch.Users + merch.ExternalLogins + merch.RegistrationAudits + `MerchantUserOutbox` | no | MerchantUser single (own outbox = R4 #2 fix) |
| 21 | HandlePspWebhook | txn.IdempotencyRecords + txn.PaymentSessions + `MerchantRuntimeOutbox` | no | MerchantRuntime single |
| 22 | VaultRevealAuditWriter (`BeginTransactionAsync`) | merch.VaultRevealAudits (applock + append) | no | MerchantRuntime single (R1-v7 #6 — not an `ExecuteInTransactionAsync`); task 6 adds `VaultAuditAppender` (`Persistence.MerchantRuntime/Vault`) as the app-layer `sp_getapplock` replacement for `sec.usp_vault_audit_head` — proven standalone (SQLite unit + real-SQL-Server concurrent-N-writer integration test), not yet the live `IVaultRevealAuditWriter` implementation (task 8 grants `pol_app` SELECT on the table + flips the DI registration as part of the 1-principal collapse) |
| 23 | ChangeAdminTier (task 4, new REQ-4.11 "Tier" invalidation-matrix source — no prior handler existed) | admin.Users (Tier + AuthorizationVersion) + admin.UserAudits | no | ControlPlane single |
| 24 | Reference-list Deactivate (Division/Level/Office/Position stores — masterdata-full-crud follow-up: typed x4, 1 tx site/store, soft-deactivate only via `IsActive`) | cfg.* | no | ControlPlane single |
| 25 | CreateMerchantUserInvitation | merch.UserInvitations + merch.UserManagementAudits + `MerchantUserOutbox` | no | MerchantUser single; revoke prior pending invitation + create + delivery event commit together |
| 26 | RevokeMerchantUserInvitation | merch.UserInvitations + merch.UserManagementAudits | no | MerchantUser single |
| 27 | UpdateMerchantUser | merch.Users + merch.UserManagementAudits | no | MerchantUser single |
| 28 | ChangeMerchantUserLifecycle | merch.Users + merch.RoleAssignments + merch.Sessions + merch.UserManagementAudits | no | MerchantUser single; serializable last-manager guard |

> **Inventory gate covers ALL transaction APIs** (R1-v7 #6), not only `ExecuteInTransactionAsync`: also
> `BeginTransaction(Async)`, `UseTransaction`, `TransactionScope`, raw-connection transactions — a CI scan fails on any
> new transaction site not classified here.
>
> **Fan-out writes (R4-v7 #5)**: rows 1/5/7/9/13 ALSO write `admin.Users.AuthorizationVersion` (invalidation matrix
> §) and row 16 ALSO writes `admin.ProvisioningOperations` — all ControlPlane-single (row 16's ledger row is in the
> same ControlPlane tx as the cross-context provisioning). The inventory gate + failpoint tests cover these fan-out
> writes. "Only ProvisionMerchant crosses contexts" still holds.

> Reads that cross a context are plain validation reads via narrow read ports (`IMerchantRoleReader`,
> `IRoleAssignmentCounter`) — NOT locked authorization gates, so they need no shared transaction. The only
> lock-sensitive cross-context read is the Super-tier recheck in ProvisionMerchant, which the provisioning UoW holds
> under lock to commit (R5 #6).

## Sequence Diagrams

```mermaid
sequenceDiagram
  participant H as ProvisionMerchant handler
  participant P as IProvisioningWriter (internal impl)
  participant C as SqlConnection (one)
  participant CP as ControlPlaneDbContext
  participant MR as MerchantRuntimeDbContext
  H->>P: ProvisionAsync(spec, callerAdminId, expectedAuthzVersion, operationKey)
  P->>C: open; strategy.ExecuteAsync(attempt)
  Note over P,C: each attempt builds a FRESH conn + both contexts
  P->>C: BeginTransaction; UseTransaction(tx) on CP and MR
  P->>CP: SELECT 1 FROM admin.Users WITH (UPDLOCK,HOLDLOCK) WHERE Id=caller AND Tier=Super AND Status=Active AND AuthorizationVersion=expected
  alt zero rows (not Super / suspended / stale version)
    P->>C: rollback → throw NotAuthorized
  else authorized FIRST (lock held to commit)
    P->>CP: immediate parameterized INSERT admin.ProvisioningOperations(operationKey,CallerAdminId,snapshot,hash,MerchantId) [named unique index]
    alt named-index violation (duplicate key)
      P->>C: rollback; CallerAdminId==stored AND request-hash matches → return stored result; else reject
    else new operation
      P->>MR: Add Merchant/PspConnection/VaultSecret/ProvisioningAudit (exact set)
      P->>CP: write serialized result onto the operation row
      P->>CP: SaveChanges(acceptAllChangesOnSuccess:false)
      P->>MR: SaveChanges(acceptAllChangesOnSuccess:false)
      P->>C: Commit
      P->>CP: AcceptAllChanges
      P->>MR: AcceptAllChanges
    end
  end
```

## Data Models & Interfaces

### Write floor — sealed guard on EVERY runtime context (R4 #1, still)
ทุก runtime context (`ControlPlaneDbContext`/`MerchantUserDbContext`/`MerchantRuntimeDbContext`) เป็น `internal sealed`,
override **ทั้ง 4 SaveChanges overload** (`SaveChanges()`, `SaveChanges(bool)`, `SaveChangesAsync(ct)`,
`SaveChangesAsync(bool,ct)`) ผ่าน save-core เดียว → `IWriteAuthorizer` (default-deny, operation/owner/tenant-aware ต่อ
context). reflection test: ทุก virtual save entrypoint ของทุก context ถูก seal. write เข้า context เหล่านี้เกิดได้เฉพาะผ่าน
narrow ports (host inject ได้เท่านั้น; merchant code inject ไม่ได้ — assembly boundary).
- **MerchantRuntimeDbContext guard**: `tenantKey==CurrentMerchant` concurrency token + immutable + reject Empty/sentinel + append-only VaultRevealAudit/ProvisioningAudit + `CanWrite(entityType,state,merchant)` default-deny
- **MerchantUserDbContext guard**: `MerchantId==CurrentMerchant` (merch.Users/RoleAssignments), owner-key per entity, append-only audits, immutable+concurrency-token tenant key — **with ONE carve-out**: a pending `merch.Users` row may transition `MerchantId` NULL→value exactly once at approval (§pre-bind approve port); merchant→merchant is still forbidden
- **ControlPlaneDbContext guard**: admin capability default-deny + authorization lease (§lease) + append-only admin audits
- **Set-based DML floor (R1-v7 #3, reworded R4-v7 #6)**: `ExecuteUpdate`/`ExecuteDelete` bypass the change tracker + sealed guard → **banned by default on EVERY runtime entity** (not just `IMerchantFiltered`); allowed only inside a named operation port whose DML `WHERE` carries the **tenant/target/state** predicate. AUTHORIZATION is a SEPARATE concern — a same-transaction lease (lease-covered flows) or request-boundary RBAC (approve/reject) — NOT required to sit inside the business DML `WHERE`. Static scan + a SQL/ordering test per port type

### Merchant-owned role capability (R5 #4) — iam.Roles is mixed-audience
iam.Roles holds platform roles, shared roles, AND merchant-owned roles; a merchant actor may CRUD its OWN roles
(CreateRole/UpdateRole/DeleteRole with a merchant actor). To keep compile-isolation, merchant code does NOT inject
`ControlPlaneDbContext`; it injects a narrow `IMerchantRoleWriter` (arch-allowlisted) whose impl (control-plane
assembly) enforces:
- `Role.MerchantId == CurrentMerchant` on every create/update/delete; reject shared/null/foreign `MerchantId`
- `MerchantId` immutable + concurrency token on iam.Roles
- `RolePermission` rows scoped through the tracked parent Role (never a free `Set<RolePermission>()` write)
- negative SQL Server matrix: merchant A cannot create/edit/delete a role of merchant B / a shared / a platform role
Admin role CRUD keeps its admin capability. Both write `ControlPlaneDbContext` but through distinct guarded ports; the
context's `IWriteAuthorizer` validates which capability is present. **Three distinct predicates, none returning
`IQueryable`** (R1-v7 #5): (a) merchant WRITER = own-only (`MerchantId==CurrentMerchant`) — cannot even load a
shared/foreign role to mutate; (b) merchant AUTHORIZATION READER (`IMerchantRoleReader`) = shared (`MerchantId IS
NULL`) + own; (c) platform admin = unrestricted. Writer predicate ≠ reader predicate → a merchant can USE a shared
role but never EDIT it (resolves the topology-vs-requirement contradiction).

### Pre-owner-bind READS vs WRITES (R5 #5) — split ports
Pre-bind **reads** (projection-limited + `AsNoTracking` + audit + arch-allowlist): `ISessionByTokenHash` (token hash),
`IResolveMerchantLoginBySubject` / `IResolveAdminLoginBySubject` (verified OIDC Subject).
Pre-bind **writes** are NOT reads and get their own command ports (each verifies its own trust root, writes an exact
entity/state allowlist, conditional write, atomic in one context, opaque capability):
- `IRegistrationWriter` — SubmitRegistration (anonymous, gated by a verified registration ticket) → MerchantUser
- `IBindInvitedAdminIdentity` — BindInvitedAdmin (gated by invited-admin email allowlist + verified Subject) → ControlPlane
- `ISelfProvisionSuperWriter` — SelfProvisionSuperAdmin (gated by the bootstrap Subject allowlist) → ControlPlane
- `IApproveRegistrationWriter` / `IRejectRegistrationWriter` (R1-v7 #1) — Approve/Reject load a PENDING `merch.Users`
  row (`MerchantId IS NULL`) by verified `Subject` under filter suppression and mutate via conditional DML
  `WHERE Subject=@s AND MerchantId IS NULL AND Status=Pending`. Approve performs the **one-time tenant-key transition
  NULL → verified target merchant** (the sole immutability carve-out, §guard); Reject sets status + revokes sessions.
  **These stay SINGLE-context (MerchantUser)** — admin authorization is the request-boundary RBAC permission check
  (read-only in ControlPlane), NOT an in-tx lease: approve/reject grant a merchant USER (not admin privilege), are
  fully auditable + reversible, so the ms-scale stale-admin race is accepted rather than paid for with a cross-context
  lease on every admin-on-merchant write (R2 #1 — deliberate decision, see §lease scope).
  **Idempotent replay (R2 #5)**: conditional update affects 0 rows → a suppressed projection checks the row's current
  state: Active + same target merchant → return stored success (idempotent 200, matching the current API); Active +
  other merchant → 409; still Pending → normal path.
No generic unfiltered Identity repository; the owner guard would otherwise deny these, and a blanket bypass would be a
privilege-escalation hole.

### Provisioning UoW — the ONE cross-context write (R5 #1, #2, #6; R4 #5)
```csharp
public interface IProvisioningWriter {          // Application port — the only thing handlers see
    // operationKey REQUIRED (idempotency); expectedAuthorizationVersion pins the caller's authz snapshot into the tx
    Task<ProvisionMerchantResult> ProvisionAsync(
        ProvisionSpec spec, Guid callerAdminId, long expectedAuthorizationVersion, string operationKey, CancellationToken ct);
}
// returns the FULL result (merchant id + connection ids + ...) so an idempotent replay returns the same body, not a bare Guid (R3-v7 #1)
// impl lives in a dedicated provisioning-integration assembly (narrow InternalsVisibleTo from BOTH CP + MR persistence, R3-v7 #4); NOT a public coordinator
```
The impl is a self-contained internal unit of work — the ONLY place two contexts share a transaction:
1. **One connection per attempt, inside an execution-strategy delegate** — `strategy.ExecuteAsync(action,
   verifySucceeded, ...)`; every retry attempt builds a FRESH `SqlConnection` + both contexts + re-reads state, so a
   transient-fault retry never reuses a poisoned change-tracker (R5 #2). The `verifySucceeded` callback is the ledger
   verifier (step 4 commit-unknown) — it runs BEFORE any retry so a committed-but-lost-ack operation is recognised, not
   re-attempted and wrongly denied (R5-v7 #2).
2. Open connection → `BeginTransaction`. Create `ControlPlaneDbContext` + `MerchantRuntimeDbContext` **bound to that
   one connection via `DbContextOptions` using the open connection** (not `AddDbContext` instances — each of those has
   its own connection, R5 #1), then `UseTransaction(tx)` on both.
3. **Lock+recheck the FULL authorization in-tx** (R2 #2; R3-v7 #2 fixes hint placement): `SELECT 1 FROM admin.Users
   WITH (UPDLOCK,HOLDLOCK) WHERE Id=@caller AND Tier=Super AND Status=Active AND AuthorizationVersion=@expected` — the
   table hint goes AFTER the table, not after WHERE. `@expected` is the port's `expectedAuthorizationVersion` arg
   (pinned at the request boundary), NOT re-read inside the tx — re-reading would make the lease a no-op. A bare `Tier`
   read would pass for a SUSPENDED Super; suspension/demotion/relevant revoke bumps `AuthorizationVersion`. Zero rows →
   rollback → throw. Lock held to commit (Tier/Status live in ControlPlane, enlisted — this IS the one cross-context flow).
4. **Idempotency ledger `admin.ProvisioningOperations` in ControlPlane** (R1-v7 #2, R2 #3, R4-v7 #1/#2/#4). Row =
   **`operationKey` (UNIQUE named index, e.g. `UX_ProvisioningOperations_Key`) + `CallerAdminId` +
   `expectedAuthorizationVersion` + request hash + pre-minted `MerchantId` (a NON-FK ledger value — explicit exception
   to the real-FK rule, since the row is written before `merch.Merchants` exists) + serialized result**. Authorization
   (step 3) runs FIRST on EVERY attempt — new or replay — so a result is never returned before authz (R4-v7 #1). Then
   INSERT the key via an **immediate parameterized SQL INSERT** in a try/catch scoped to that one statement, matched on
   the SPECIFIC named index (parse the constraint name — 2601/2627 alone is ambiguous), NOT a deferred `DbSet.Add`
   (R4-v7 #2):
   - INSERT succeeds → new operation → step 5.
   - named-index violation → duplicate: roll back; verify the requesting `CallerAdminId` == stored **AND the canonical
     request hash matches** (a DIFFERENT caller OR a different payload for the same key → reject BEFORE deserializing the
     result — R5-v7 #1), then return the stored result.
   - commit-unknown → the execution strategy does NOT blindly re-run the delegate: it FIRST calls the internal
     fresh-context verifier (an EF Core 10 `verifySucceeded`-style hook, R5-v7 #2) matching key + `CallerAdminId` +
     snapshot + request hash + result. A match ⇒ the operation already committed → return the stored result WITHOUT
     re-running the auth-first check (which could wrongly deny on a since-bumped `AuthorizationVersion`); only a genuine
     absence retries.
5. New operation: use the pre-minted merchant Id from the ledger row; add the EXACT entity set (Merchant +
   PspConnection(s) + VaultSecret(s) + ProvisioningAudit) — guard rejects any other Added type on this capability —
   then write the serialized result back onto the ledger row.
6. `SaveChanges(acceptAllChangesOnSuccess: false)` on each context → `Commit` → `ChangeTracker.AcceptAllChanges()` on
   each (R5 #2 ordering: never accept before the joint commit).
7. Capability is an opaque one-shot the application layer cannot resolve from DI.

> No general `ITransactionCoordinator` in the Application contract (R5 #1) — a single internal provisioning UoW is the
> whole cross-context surface. If a future flow needs a second one, it gets its own internal UoW the same way.

### Migration (R5 #7) — keep the CLR name, verify lineage before adding
- **Migration-owner keeps its FULL type identity** (R1-v7 #10): `BuildingBlocks.Infrastructure.Persistence.PolDbContext`
  — same class name, namespace, migrations assembly, AND snapshot location (only its runtime registration is removed)
  so `[DbContext(typeof(PolDbContext))]` in every existing designer + the snapshot still resolves the current chain.
  Moving the namespace/assembly would change discovery even with the class name kept.
- **Gate `dotnet ef migrations list --context PolDbContext` BEFORE and AFTER the assembly split** MUST list every
  existing migration ID (no empty history, no re-create-existing-table baseline). CI asserts this.
- forward migration ต่อ chain เดิม: drop security objects (policy/predicate/proc/bypass role), preserve
  `merch.RegistrationNotices`, add CartItems `MerchantId` + backfill, add CHECK/composite-FK/alt-key;
  **add `admin.ProvisioningOperations`** (named-unique `operationKey`, `CallerAdminId`, `expectedAuthorizationVersion`,
  request-hash, pre-minted `MerchantId` as a NON-FK ledger value, serialized result) + **`AuthorizationVersion` column
  on `admin.Users`** (default 0, bumped on suspend/demote/revoke) (R2 #4, R4-v7 #1/#4);
  **outbox physical mapping (R2 #7, R4-v7 #3)**: `MerchantRuntimeOutbox` **reuses the existing `txn.OutboxMessages`**
  (no rename) for in-flight PAYMENT events; `MerchantUserOutbox` = NEW `merch.UserOutbox`; **atomic move of the legacy
  REGISTRATION sentinel rows** `txn.OutboxMessages` → `merch.UserOutbox` preserving `Id` + processing/lease/attempt
  state under deployment quiescence, then delete source; **after the move the sentinel CHECK forbids sentinel on
  `txn.OutboxMessages`** and allows it ONLY on `merch.UserOutbox` (resolves the "no-migration vs migrate" contradiction).
- **upgrade test from the CURRENT migrated DB** (not just fresh) + schema fingerprint assert.
- full 1-principal inventory (compose/entrypoint/migrate-entrypoint/01-principals/.env.example/CI/worker/assert-fresh-db)
  + CI assertion: no legacy principal/RLS/bypass object post-migration.

### Assembly split + Api host boundary (R5 #8)
- persistence split by cluster into separate projects (e.g. `*.Persistence.ControlPlane` / `.MerchantUser` /
  `.MerchantRuntime`), contexts `internal` → merchant module code cannot name `ControlPlaneDbContext`. Split the
  current `Merchants.Infrastructure` (holds both merchant + identity persistence today).
- **compile-negative build test**: a forbidden `ProjectReference` (merchant persistence → control-plane persistence)
  MUST fail the build — enforced by a custom MSBuild check, not only an arch-lint test.
- **unified `Api.csproj` references every module** (unavoidable for one host) → declare the Api composition root a
  **trusted exception**; an arch test asserts merchant route/endpoint adapters cannot resolve control-plane
  read/write ports, and every privileged port **reauthorizes on each call** (does not trust that only trusted code
  can reach it).
- **Data Protection** (R1-v7 #9): the `IXmlRepository` that today resolves `PolDbContext` + calls `SaveChanges` in the
  Api host moves INTO the ControlPlane persistence assembly (DataProtectionKeys lives there), exposed only via a DI
  registration extension over an opaque interface. **No `InternalsVisibleTo(Api)`** — that would hand the host the
  whole internal context and defeat the boundary.
- **Runtime EF config is scalar-only, separate from the migration-owner's relationship config** (R1-v7 #7): the current
  `merch.RoleAssignments` config uses `HasOne<Role>()` → importing it into `MerchantUserDbContext` would pull `iam.Role`
  into that model and break disjointness. Runtime contexts get their OWN `IEntityTypeConfiguration` (scalar `RoleId`
  only); `PolDbContext` keeps the full FK relationships. Test: runtime model disjointness AND a schema FK fingerprint.
- **Provisioning-integration assembly** (R3-v7 #4): the provisioning UoW needs BOTH `internal ControlPlaneDbContext`
  and `internal MerchantRuntimeDbContext`, so it cannot live in either single-cluster assembly. It lives in ONE
  dedicated privileged assembly that both CP and MR persistence grant narrow `InternalsVisibleTo`; a compile-negative
  test asserts no OTHER assembly receives that friendship. This is the single sanctioned dual-reference.
  **Type-level gate (R4-v7 #7)**: since `InternalsVisibleTo` scopes to the whole assembly, an arch test ADDITIONALLY
  asserts that only ONE named coordinator type in that assembly may reference the context constructors / `UseTransaction`
  / provisioning capability / raw connection — no other type added there can widen the privileged surface outside
  `IProvisioningWriter`.

### Authorization lease vs business revoke — TWO distinct things (R5 #9, R1-v7 #4)
The prior 'rows affected == 1' rule wrongly conflated the authorization GATE with the business DML (session-family
revoke is idempotent, legitimately 0..N rows, and the target row carries no caller version). Split them:
- **Authorization lease** = a single conditional no-op update on the CALLER's own authorization row:
  `UPDATE admin.Users SET AuthorizationVersion=AuthorizationVersion WHERE Id=@caller AND AuthorizationVersion=@expected`
  (or `Tier=@expected`), READ COMMITTED, PK-covered; **exactly-one row or deny+rollback**. Held in the same
  transaction as the business write → closes the stale-authorization race.
- **Business revoke** keeps its natural **0..N row-count semantics** per operation (revoke session family may hit 0..N
  and is idempotent) — its row count is NOT an authorization signal.
- demote/revoke bumps the caller/target `AuthorizationVersion`; timeout/deadlock → bounded retry then deny. One lease
  algorithm across flows. (The provisioning UoW's Super-tier UPDLOCK is the single scoped exception.)
- **Lease SCOPE (R2 #1, accepted R3-v7)**: the in-tx lease applies to revoke/demote-sensitive **admin writes that
  already live in ControlPlane** (Reactivate/RevokeAdminSession/Suspend/SetAdminRoles — lease + business write both
  single-context in ControlPlane) and to **provisioning** (the one cross-context flow). It does NOT extend to
  approve/reject: **admin authorization LINEARIZES at the request boundary** (RBAC check), the SQL conditional DML
  controls only target/state (`Subject`/`Status=Pending`/owner). Consequence (accepted): a request authorized BEFORE a
  concurrent revoke may still commit (the granted merchant USER is auditable + reversible); a request that STARTS after
  the revoke is denied at the boundary. This is a deliberate linearization-point choice, not "reversible ⇒ ignore" —
  race test asserts exactly that ordering. Keeps "only ProvisionMerchant crosses contexts".

### AuthorizationVersion invalidation matrix (R3-v7 #3)
`AuthorizationVersion` on `admin.Users` is bumped IN THE SAME TRANSACTION as every write that changes a user's
effective authorization — else a caller holds a stale lease. Complete source list (barrier test per source):
| Source change | Bump whose version |
|---|---|
| Status (suspend/reactivate) | that user |
| Tier (promote/demote) | that user |
| Session revoke (RevokeAdminSession / family) | that user |
| MerchantAccess grant/revoke (AssignMerchant / **UnassignMerchant**) | the scoped admin |
| RoleAssignment add/remove (SetAdminRoles / SelfProvision) | the assigned user |
| RolePermission update/delete (UpdateRole / DeleteRole) | every admin holding that role |
Missing any source = a revoked admin keeps a valid lease. Bump + business change commit atomically (all
ControlPlane-single except provisioning).

### Cross-cutting (kept from v4-v6)
tenant-key descriptor (resolve `FindProperty`, reject shadow/typo/nullable/wrong-type); concurrency token + immutable
(after-save Throw); DB `CHECK(<>Empty)` + `CHECK(<>sentinel except Outbox)`; CartItems composite FK; suppressed-op
read ports (`IWebhookMerchantResolver`/`IOrderSummaryReader`/`IOutboxDrain` per owner/`IVaultAuditAppender`/
`IRoleAssignmentCounter`/`IMerchantRoleReader`) + ban bypass primitives outside port impls in every assembly;
deny-by-default `Guid.Empty`; zero-claim reject 401/403.

## Technology Decisions
- **Cluster-aligned runtime contexts** = every real unit-of-work stays in one context (20/21); only ProvisionMerchant
  crosses, via one internal UoW. This is what makes v7 converge where v6's audience partition did not.
- migration-owner class name kept `PolDbContext` = lineage stays discoverable (R5 #7).
- sealed 4-overload guard every runtime context + write via port only (assembly-enforced).
- `AddDbContext` (scoped) for the three runtime contexts; the provisioning UoW builds its own connection-bound
  contexts by hand (shared-transaction requirement) — ban pooling APIs.
- 1 principal (owner decision — Codex R1 flag, sign-off item); big-bang `down -v` local/CI.

## Error Handling Strategy
| กรณี | พฤติกรรม |
|---|---|
| forged detached write | concurrency token → 0 rows → `DbUpdateConcurrencyException` |
| write Empty/sentinel/foreign owner/no-capability | sealed guard (ทุก context) → throw |
| merchant edits foreign/shared/platform role | `IMerchantRoleWriter` MerchantId guard → reject |
| provisioning partial failure / transient fault | provisioning UoW rollback; execution-strategy retry with fresh conn+contexts |
| provisioning commit-unknown retry | `admin.ProvisioningOperations` lookup → matching hash returns stored result; different payload rejects key reuse |
| Super demoted mid-provision | in-tx UPDLOCK,HOLDLOCK Tier recheck → rollback |
| stale-authorization race | in-tx authorization LEASE = exactly-one-or-deny; business revoke keeps 0..N |
| approve pending registration | one-time `MerchantId` NULL→verified-merchant transition via approve port (guard carve-out) |
| set-based DML on a runtime entity | banned outside a named op port (static scan) |
| provision by non-Super | recheck Super in-tx → reject |
| merchant read foreign id | filter → null → 404 |

## Testing Strategy (R5 #10, R4 #10)
- **Unit (SQLite)** per context: filter/guard/owner isolation; merchant-role capability negative matrix (A cannot touch B/shared/platform role); writer own-only vs reader shared+own predicates; approve one-time NULL→merchant transition allowed once + merchant→merchant rejected; per-owner outbox commits atomically with its domain write.
- **Integration (SQL Server)**: **upgrade-from-current-migrated-DB** fixture + `dotnet ef migrations list` lineage gate;
  **provisioning UoW**: failpoint after ControlPlane save and after MerchantRuntime save → assert atomic rollback;
  transient-fault retry does not double-provision; **commit-unknown returns stored result / rejects payload mismatch**
  (`admin.ProvisioningOperations`); concurrent demote-during-provision; **authorization LEASE exactly-one-or-deny while
  business session-revoke hits 0..N**; **set-based DML banned outside op ports** (SQL predicate test per allowed port);
  vault concurrent-N applock; transaction-enlistment assert (provisioning writes share one transaction id);
  forged/Empty/sentinel/CartItems-FK/owner-change; **provisioning: suspended-Super + stale-version rejected; concurrent
  same-key winner/loser (loser returns the winner's stored result); payload-mismatch reject; exact stored-result
  replay**; **approve/reject race: a request authorized BEFORE a concurrent revoke may commit, a request STARTING after
  it is denied**; **AuthorizationVersion invalidation barrier per source** (Status/Tier/Session/MerchantAccess/
  RoleAssignment/RolePermission → stale lease denied); legacy registration-sentinel rows drained
  `txn.OutboxMessages`→`merch.UserOutbox` on upgrade.
- **Compile/arch**: forbidden `ProjectReference` → **build fails** (compile-negative, MSBuild); merchant endpoint
  adapter cannot resolve control-plane ports; no `InternalsVisibleTo(Api)` on runtime contexts; **only the
  provisioning-integration assembly holds `InternalsVisibleTo` from CP+MR** (any other assembly touching an internal
  context fails to build); bypass primitive only in port impls; sealed all-overload every context (reflection);
  `RawConnectionTests`(+assembly, provisioning-UoW allowlisted); `AdminSeamArchitectureTests`; **inventory gate scans
  ALL transaction APIs** (BeginTransaction/UseTransaction/TransactionScope/raw), not only `ExecuteInTransactionAsync`.
- **CI**: no legacy principal/RLS/bypass post-migration; lineage gate.

## Observability [REQ-13]
taxonomy: guard/`CanWrite`/owner denial, concurrency exception, CHECK/FK violation, provisioning rollback, revoke deny
(`rows affected != 1`), provisioning Super-recheck fail, merchant-role capability denial, port use/anomaly, applock
timeout, sentinel/Empty hit — fields actor/target/entity/op/reason/correlation (redacted); durable non-blocking →
external tamper-resistant sink + alerts + retention + redaction test; per-host `Application Name`.

## Requirement Traceability
| Design element | REQ |
|---|---|
| Cluster-aligned runtime contexts + filters | REQ-1, REQ-11.5 |
| Sealed guard ALL contexts + token/immutable | REQ-2 |
| Transaction inventory + provisioning UoW (only cross-context) | REQ-2.12 |
| Empty/sentinel CHECK + zero-claim | REQ-3 |
| Admin capability + revoke conditional-DML | REQ-4 |
| Merchant-owned role capability | REQ-4, REQ-5 |
| Suppressed-op read ports + bypass ban | REQ-5, REQ-11.4 |
| CartItems composite FK | REQ-6 |
| Vault applock port | REQ-7 |
| RLS removal + 1 principal + migration-owner (CLR kept) + inventory | REQ-8 |
| MerchantUser owner-scoped + pre-bind read ports + write ports + own outbox | REQ-9 |
| Provisioning command port (Super, atomic, idempotent) | REQ-10 |
| Testing (upgrade/failpoint/idempotency/compile-negative/tx-enlist) | REQ-11 |
| Canon supersede | REQ-12 |
| Observability | REQ-13 |

## Non-Functional Considerations
- **Security**: isolation = assembly boundary (compile) + per-context sealed guard + DB invariants; the single
  cross-context flow (provisioning) is atomic + Super-locked + idempotent. **1 principal (Codex R1 ค้าน)** — owner รับ,
  sign-off item.
- **Complexity (honest)**: v7 = re-architecture ใหญ่ (migration-owner + 3 runtime context + assembly split + per-owner
  outbox + 1 provisioning UoW). **ไม่ใช่ net simplification เทียบ RLS** — owner รับ trade เพื่อถอด RLS (unit-testable, no
  SESSION_CONTEXT/bypass/proc). v7 ต่างจาก v6 ตรงที่ inventory พิสูจน์ว่า cross-context เหลือ flow เดียว → ตัด generic
  coordinator ทิ้ง = เล็กลงกว่าที่ R4 กลัวมาก.
- **Canon supersede**: `ARCHITECTURE.md:99-108,128-130` + **amend T1 "one DbContext"** (→ 1 migration-owner + 3
  runtime), `SECURITY_RULES.md:178-184`, rf1 REQ-3.2/3.3/3.7/3.8, admin-actor-rename REQ-7.4, rewrite
  `docs/reference/db-connection-and-rls.md`.

## Human sign-off item — RESOLVED
1-principal blast radius (Codex R1). **Owner CONFIRMED 1 principal at gate #2 (2026-07-19)** after seeing the full v7
design — accepts the wider blast radius (app compromise ⇒ DB-level read of vault plaintext / audit) in exchange for the
simpler operational surface; the isolation floor is app-layer (query filter + sealed guard + DB invariants) + arch
guardrails, with NO least-privilege DB belt. Decision not reversed; recorded in PLAN-REVIEW-LOG.md.
