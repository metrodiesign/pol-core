# Implementation Tasks: Hierarchical Naming (namespace + route)

> Status: approved 2026-07-12

> Each task is a cohesive, independently verifiable slice. Implement a whole task
> in one pass (it may touch many files). Decompose into sub-steps yourself at
> execution time — do NOT pre-split tasks here.

> **Order is a control, not a preference.** Tasks 0 and 1 exist so that the sweep
> cannot silently remove a security control. A breakdown that ships the rename first
> and the detectors second satisfies every REQ on paper and defeats all of them in
> practice (requirements.md, Edge Cases). Do not reorder 0 or 1.

- [x] 0. **Preconditions — the admin session-config bugfix, and proof there is no production data.**
     Two gates that must both be true before a single file is renamed.
     (a) Fix the section-name mismatch: `PlatformUserSessionOptions.SectionName` reads
     `"PlatformUserSession"` while `appsettings.json` defines `"AdminSession"`, so admin session options
     bind to nothing, `ReturnUrlAllowlist` is `[]`, and every admin `returnTo` is silently discarded.
     Ship on its own branch and PR. **Before merging, audit the configured `ReturnUrlAllowlist` in
     staging and production** — the fix makes a dead allowlist start binding, widening the admin
     open-redirect surface from deny-everything to whatever is set. Touches `AdminAuthOptions.cs:28`,
     `appsettings.json:25`, `AdminAuthLoginRedirectTests.cs:41`, `AdminLoginServiceTests.cs:105`,
     `docs/runbooks/deploy-self-host.md:76-77`.
     (b) Confirm no production deployment holds real data. Task 9 destroys the database volume; if this
     is false, STOP — the spec needs a transfer migration instead of a reset.
     Satisfies: REQ-9.3, 9.4, 9.5, 14.0. Verify: bugfix PR merged to develop; a test asserts which
     section the options bind from; the no-production-data answer is recorded in this file before task 1.

     Progress (2026-07-12): (a) fixed on branch `fix/admin-session-config-binding`, PR
     [#95](https://github.com/metrodiesign/pol-core/pull/95) open against `develop` — SectionName now
     `"AdminSession"`; `AdminAuthLoginRedirectTests.cs` gained a test resolving
     `IOptions<PlatformUserSessionOptions>` from real app config and asserting the allowlist actually
     binds. Confirmed with repo owner: no staging/production deployment exists yet, so the
     pre-merge allowlist audit has nothing to audit — gate satisfied by there being no live config to
     widen. Awaiting PR #95 review/merge before checking this task done.
     (b) Confirmed with repo owner (2026-07-12): **no production deployment holds real data** — the
     big-bang reset in task 9 is safe to proceed as specced; no transfer migration needed.

     Evidence:
       - test: `dotnet test tests/Hosts.Tests` -> 194 passed / 0 failed (post-merge, on this branch after
         merging develop@a0f9825, which carries PR #95)
       - test: `dotnet test tests/Hosts.Tests --filter FullyQualifiedName~AdminAuthLoginRedirectTests` ->
         3 passed / 0 failed, including the new
         `PlatformUserSessionOptions_bind_from_the_appsettings_AdminSession_section` test; re-verified green
         with `appsettings.Development.json` temporarily removed (CI-equivalent, no gitignored dev config)
       - viewports: n/a — logic-only (config binding + backend test)
       - deviations: none. PR #95 merged to develop (a0f9825); Codex P2 review flagged the binding test
         depending on a gitignored local dev config to hit its asserted values — fixed (1585d66) before
         merge to assert NotEmpty + Contains instead of a full-list Equal, verified self-contained.
         Repo owner confirmed (this session, 2026-07-12): no staging/production deployment exists, so the
         pre-merge `ReturnUrlAllowlist` audit had nothing live to widen; no production data exists, so the
         big-bang reset in task 9 stays valid without a transfer migration.

- [x] 1. **Detectors, written against the code as it stands today.**
     Every one of these must be **green on the pre-rename, pre-move code** — a detector authored after
     the code it guards proves only that the two agree, not that the control survived.
     Add Hosts.Tests asserting all four controls on `POST /api/v1/admins/merchants` and
     `GET /api/v1/admins/merchants/{code}`: `AdminCsrfFilter`, the admin CORS policy, the `"admin"`
     authorization policy, and the Super tier on the POST. Add the fail-closed CORS guard: enumerate
     `EndpointDataSource`, and for every endpoint carrying the `"admin"` policy or `AdminCsrfFilter`,
     assert `PolCorsPolicyProvider` returns `AdminPolicyName` for its template. Harden
     `AdminArchitectureTests` and `MerchantsArchitectureTests`: their namespace literals
     (`"Admin.Domain"`, `"Cart.Infrastructure"`, …) will match nothing after task 3 and pass vacuously,
     so add a positive assertion that every forbidden namespace resolves to at least one real assembly.
     Satisfies: REQ-7.5, 8.3, 8.4, 8.6, 15.1, 15.2. Depends on: 0.
     Verify: `dotnet test` green **before** any rename; deliberately break one control locally and watch
     each new test fail.

     Evidence:
       - test: `dotnet test` (full solution) -> Cart 15, BuildingBlocks 65, Products 25, Orders 25,
         Payments 59, Checkout 2, Admin 129, Merchants 128, Architecture 50, Hosts.Tests 201 — all
         passed / 0 failed. `Integration.Tests` (86 tests) not run — needs a live SQL Server container +
         `.env.integration` env vars not sourced in this session; out of this task's scope (Hosts.Tests +
         Architecture.Tests only) and untouched by this change.
       - test: deliberately broke each control on the pre-move code and watched its own new test fail,
         then restored and re-verified green — `AdminCsrfFilter` removed from the `/admins` group ->
         `POST_without_a_CSRF_token_is_rejected_even_with_a_Super_session` failed; `RequirePlatformUserTier`
         removed from the POST -> `POST_from_a_Scoped_admin_is_rejected_even_with_a_valid_CSRF_token`
         failed; `RequireAuthorization("admin")` swapped for `AllowAnonymous()` on the GET ->
         `Without_a_session_the_admin_policy_rejects_the_request(GET)` failed; `PolCorsPolicyProvider`'s
         `/api/v1/admins` prefix literal broken -> `AdminCorsGuardTests` failed; a forbidden assembly-name
         literal typo'd in `AdminArchitectureTests` -> the new resolve-assertion failed.
       - viewports: n/a — backend-only (endpoint filters, CORS, architecture guards)
       - deviations: (1) `AdminCsrfFilter` and the Super-tier gate are plain endpoint filters with zero
         queryable `Endpoint.Metadata` (verified empirically by dumping metadata for
         `POST /api/v1/admins/merchants` — only `IAuthorizeData`/`HttpMethodMetadata`/etc show up), so the
         CORS guard's "carrying `AdminCsrfFilter`" clause is approximated as "requires the `admin`
         authorization policy" — today the two sets coincide exactly (one call site, `Program.cs`).
         (2) proving CSRF/tier attachment requires an authenticated request past `RequireAuthorization`,
         but there is no DB-backed session in Hosts.Tests, so the new `AdminMerchantsEndpointControlsTests`
         re-points the `"admin"` policy at a fake always-present test scheme in its own factory only — the
         real scheme pinning is already covered by `AdminProvisioningAuthorizationTests`. (3) CSRF is not
         independently asserted on the GET endpoint — it is a safe method the filter exempts by design
         (already covered by `AdminCsrfFilterTests`), so there is no observable behavior difference to
         assert there; REQ-7.1's "re-attach to them" is satisfied structurally (same route group) and
         verified for the POST, where it is observable.

- [x] 2. **The naming law, written into the canon and reconciled with the specs it contradicts.**
     Record L1-L8 in `.ai/shared/ARCHITECTURE.md` §Naming Conventions (which today says only
     "type/interface: PascalCase" — the absence of this rule is the root cause of the drift). Amend
     `.ai/specs/rf1-schema-reset/design.md` §149, whose `Producer -> MerchantUser` sweep rule this spec
     supersedes. Amend `.ai/specs/api-route-scheme/requirements.md`: REQ-2.1's area taxonomy
     (`merchants` in, the compound area out), REQ-2.8's enumeration of literal `admins` sub-resources
     (add `merchants/users` and the four master lists), and its pre-rf1 vocabulary (`producers`,
     `tenant`) which no longer names anything in the codebase.
     Satisfies: REQ-2 (all criteria). Verify: no two canon files state contradictory naming rules;
     `grep -rn 'producer\|tenant' .ai/specs/api-route-scheme/requirements.md` returns only history notes.

     Evidence:
       - test: n/a — docs-only task, no code touched; `dotnet build` unaffected (not re-run, nothing to
         invalidate it)
       - viewports: n/a — logic-only (canon documentation)
       - deviations: `.ai/specs/api-route-scheme/requirements.md` predates rf1 and carries `producer`/
         `tenant` vocabulary across REQ-2, REQ-3, REQ-6, REQ-8, REQ-9, and Edge Cases (~25 occurrences),
         not just REQ-2.1/2.8. Amended REQ-2.1, REQ-2.3, REQ-2.8, REQ-2.9 (the area-taxonomy group) in
         place to the target post-sweep vocabulary, each inline-noting it as a 2026-07-12 amendment and
         that hierarchical-naming's tasks 3-12 had not yet shipped it as of this amendment (so it isn't
         misread as already-live). Added one top-of-file historical-vocabulary banner covering every
         remaining producer/tenant mention in REQ-3/6/8/9/Edge Cases, which describe the vocabulary as it
         stood at the ORIGINAL 2026-07-05 migration and are left as-is — rewriting ~20 more lines of a
         shipped migration's behavior-preservation requirements was judged out of this task's scope
         (docs-canon reconciliation, not a rewrite of an unrelated shipped spec). `grep -rn
         'producer\|tenant' .ai/specs/api-route-scheme/requirements.md` re-run after the edit: every hit
         is either inside the new banner, inside an inline amendment note, or inside REQ-3/6/8/9/Edge
         Cases text the banner frames as history — none reads as a live, current requirement anymore.
         `.ai/shared/CODING_STANDARDS.md`'s own entity-naming bullet still names pre-hierarchical-naming
         types (`PlatformUser` etc.) — left untouched; it reflects current as-built code and is this
         spec's own tasks 4/5/9/10 to update once the rename actually ships, not task 2's docs-law scope.

- [x] 3. **Module projects pluralised; solution and dead folders cleaned.**
     `Admin.*` -> `Admins.*`, `Cart.*` -> `Carts.*`, `Checkout.*` -> `Checkouts.*` across Domain,
     Application and Infrastructure, plus the three test projects. Update all twelve `pol-core.slnx`
     entries. Every folder move is `git mv` — a delete+create loses per-file history across a 262-file
     diff. Leave `SchemaNames.Admin = "admin"` singular and record why the project and schema names now
     differ on purpose. Delete `src/Modules/{Identity,Producer,Tenant}/`, absent from the solution and
     holding only stale `obj/` output, **in a separate commit** from the renames.
     Satisfies: REQ-3 (all criteria). Depends on: 1.
     Verify: `dotnet build` resolves all 40 projects; `dotnet test` green; `git log --follow` still
     traces a moved file's history.

     Evidence:
       - test: `dotnet build pol-core.slnx` -> Build succeeded, 0 Warning(s), 0 Error(s), all 40
         projects resolved
       - test: `dotnet test pol-core.slnx --no-build` -> every project green except `Integration.Tests`
         (86 failed, pre-existing: needs a live SQL Server container + `.env.integration`, same
         out-of-scope condition recorded in task 1's evidence, untouched by this change) — Carts.Tests
         15, Checkouts.Tests 2, Admins.Tests 129, Merchants.Tests 128, Orders.Tests 25, Products.Tests
         25, Payments.Tests 59, BuildingBlocks.Tests 65, SharedKernel.Tests 46, Architecture.Tests 50,
         Hosts.Tests 201 — all passed / 0 failed
       - test: `git log --follow --oneline -3 -- src/Modules/Admins/Admins.Domain/PlatformUser.cs` (run
         after committing the sweep, commit 580d95d) -> traces through the rename into the file's prior
         history; `git diff --cached --stat` before commit showed 103 renames (git similarity-detected,
         >84% each) + 45 modifies, 0 delete+create pairs
       - viewports: n/a — backend-only (project/namespace rename, no UI)
       - deviations: (1) the namespace/using-alias sweep and the `pol-core.slnx` path fixes had to be
         done as two passes — `rg --type cs --type xml` for `.cs`/`.slnx` files, then a second pass
         for `*.csproj` (ProjectReference paths use the same `Admin.Domain`-style tokens but aren't
         matched by a `cs`/`xml` type filter) — first `dotnet build` attempt failed with 408 errors from
         the missed csproj references; fixed and re-verified green. (2) Docs (`docs/reference/
         admin-module.md`) and `retrospectives/**` still say `Modules/Admin/` — left as-is, out of REQ-3
         scope (no build/test dependency on them; historical record for retrospectives). (3) REQ-3.6's
         dead-folder deletion produced **no git diff and no separate commit**: `git ls-files` on
         `src/Modules/{Identity,Producer,Tenant}/` returned 0 tracked files before deletion (only
         gitignored `bin`/`obj`), so there was nothing for git to record — deleted directly from disk
         (confirmed with repo owner first, since it required `rm -r` past the destructive-ops hook) after
         the rename commit, verified `git status --short` shows nothing.

- [ ] 4. **Admins module: dissolve `Platform*`, nest, de-prefix.**
     The largest single naming change: `PlatformUser` -> `Admins.Domain.Users.User` and its whole
     satellite family (sessions, audits, tier, merchant access), `AdminRole*` -> `Admins.Domain.Roles.*`,
     `AdminPermission*` -> `Admins.Domain.Permissions.*` with the const catalog becoming
     `Permissions.Keys`, and the `MasterData` abstract base renamed `MasterDataItem` (a type may not
     share its namespace's name). Application and Infrastructure follow the same law. Apply the L6 alias
     discipline in every consuming file — file-level aliases only, never `GlobalUsings`, never partial
     qualification, never a re-added prefix.
     Satisfies: REQ-4.1-4.4, 4.6, 4.7, REQ-5.1-5.4. Depends on: 3.
     Verify: `dotnet build` + `dotnet test` green; Architecture.Tests (hardened in task 1) still green.

- [ ] 5. **Merchants module: nest and de-prefix; `Merchant` stays at the root.**
     `MerchantUser*` -> `Merchants.Domain.Users.*` (user, session, external login, auth audit,
     registration), `MerchantUserRole*` -> `Users.Roles.*`, `MerchantUserPermission*` ->
     `Users.Permissions.*`. `Merchant`, `MerchantCode`, `MerchantStatus` and `ProvisioningAudit` stay at
     the module root — L2 forbids nesting a module inside itself. Application and Infrastructure follow.
     Satisfies: REQ-4.1-4.3, 4.7, REQ-5.1-5.4. Depends on: 3.
     Verify: `dotnet build` + `dotnet test` green.

- [ ] 6. **Data-plane modules: `Carts`, `Checkouts`, `Payments`, `Orders`, `Products`.**
     `CartItem` -> `Carts.Domain.Items.Item`. `PspConnection`/`PspCode` -> `Payments.Domain.Psp.*`.
     `CheckoutSession` and `PaymentSession` become `Checkouts.Domain.Session` and
     `Payments.Domain.Session` — **at the module root, with no `Sessions/` folder**: each is its own
     module's root aggregate, and a sub-namespace holding only the root is exactly what L2 forbids.
     `Orders` and `Products` gain no sub-folder: one aggregate each, nothing to cluster.
     Satisfies: REQ-4.1-4.3, 4.5, 4.7. Depends on: 3.
     Verify: `dotnet build` + `dotnet test` green.

- [ ] 7. **API host organised by area.**
     Group the twelve flat `MerchantUser*.cs` and their admin counterparts under `Api/Admins/`,
     `Api/Merchants/`, `Api/Payments/`, `Api/Webhooks/`, namespace `Api.<Area>`, prefix dropped, moved
     with `git mv`. Files belonging to no single area stay at the host root. **Leave the route mappings
     in `Program.cs` alone** — this task reorganises files, it does not rewrite the route table (that is
     task 8), and conflating the two would make both unreviewable. This is where the L6 alias block
     lands, including in `tests/Hosts.Tests/*`, which consumes both planes.
     Satisfies: REQ-16 (all criteria), REQ-5.5. Depends on: 4, 5, 6.
     Verify: `dotnet build` + `dotnet test` green; `git diff --stat` shows no change to any route string.

- [ ] 8. **Routes moved; the four controls re-attached; the CORS path table extended and guarded.**
     `/api/v1/merchant-users/**` -> `/api/v1/merchants/users/**`. Provision and read merchant leave the
     `admins` group for `/api/v1/merchants` and `/api/v1/merchants/{code}` — and **arrive with all four
     controls explicitly re-attached** (CSRF filter, admin CORS policy, `"admin"` policy, Super tier on
     the POST); the group move is the single most dangerous edit in this spec. Approve/reject move to
     `/api/v1/admins/merchants/users/{subject}/…`. The `master-data` wrapper segment is **dropped, not
     renamed** — `/api/v1/admins/{positions|offices|levels|divisions}`. `{code}` stays **unconstrained**:
     adding a constraint would itself be a behavior change, and the templates cannot collide anyway.
     Update the area regex in `RouteSchemeConventionTests` (it changes because the spec changed, never to
     make a red test green) and every `Location` header that embeds a moved path. Extend the CORS
     admin-plane path table to the moved endpoints while excluding `/api/v1/merchants/users/**`; the
     provider stays **path-based** — the endpoint-metadata alternative is broken on preflight.
     Satisfies: REQ-6 (all criteria), REQ-7.1-7.4, 7.6, REQ-8.1, 8.2, 8.5. Depends on: 1, 7.
     Verify: task 1's four-control tests and CORS guard still green **after** the move; `CorsTests`
     unchanged and green; `RouteSchemeConventionTests` green with `merchants` and without the old area.

- [ ] 9. **Database renamed everywhere it is named — including the raw SQL the compiler cannot see.**
     Rename the `admin` and `merch` tables per design §6; `shop` and `txn` are untouched. Rewrite the
     three migrations, their designers, and `PolDbContextModelSnapshot` **in place** — they store CLR
     type names as strings, so a missed one means EF reports pending model changes. Then follow the
     names into the places a compiler never looks: `sec.fn_merchant_predicate` and the security policies,
     **every line of the per-table GRANT matrix** in `20260711142515_SecurityObjects.cs:232-238` (miss one
     and that table has no grant — permission-denied at runtime, not at build),
     `docker/bootstrap/assert-fresh-db.sql` (a required CI check), and `docker/entrypoint.sh`.
     Satisfies: REQ-10 (all criteria), REQ-14.1-14.4, REQ-1.3. Depends on: 4, 5, 6.
     Verify: `docker compose down -v` then `dotnet ef database update` on a fresh DB → **no pending model
     changes**; `assert-fresh-db.sql` passes; the RLS matrix test is green.

- [ ] 10. **Wire strings: permission keys, auth schemes, the OIDC callback — and the three things that
     deliberately do not move.**
     Admin catalog: `merchant_user.approve|reject` -> `merchants.users.approve|reject`, group renamed to
     match. Merchant-user catalog: drop the redundant self-prefix — `merchant_user.roles.view|manage` ->
     `roles.view|manage`, `merchant_user.user.roles` -> `users.roles`. Auth scheme `PlatformUserSession`
     -> `AdminSession` (this is D5's core). OIDC callback -> `/api/v1/merchants/users/auth/callback`,
     **which requires the authorized redirect URI to be updated in Google Console first** — that contract
     lives outside the repo, so login breaks in the environment while CI stays green. Deliberately
     unchanged: `MerchantUserSession` (the principal really is a user *of* a merchant, and the scheme id
     is a flat OpenAPI contract), the rate-limit policy names, `MerchantUserRegistrationSubmitted` and the
     outbox registry keys, and every configuration section key.
     Satisfies: REQ-11 (all criteria), REQ-9.1, 9.2, 9.6, REQ-13 (all criteria). Depends on: 8, 9.
     Verify: permission-key unit tests green; outbox publish → worker consume round-trips; admin and
     merchant-user Google login both succeed on dev against the new callback path (restart the API — a
     stale dev binary will lie to you).

- [ ] 11. **The FE-facing contract, published where FE will find it.**
     Write `.ai/specs/hierarchical-naming/FE-MIGRATION.md` — this spec's own document, not
     `rf1-schema-reset`'s — covering every route change, every changed `Location` header, every changed
     permission-key string, the OpenAPI security-scheme id change (`PlatformUserSession` -> `AdminSession`,
     which generated clients key on), and the changed master-data operation ids. Leave a pointer on
     rf1's `FE-MIGRATION.md` so an FE reader on the old trail is not stranded. Update
     `docs/runbooks/local-dev-run.md` and `docs/reference/producer-module.md`, which still name the old
     routes.
     Satisfies: REQ-12 (all criteria). Depends on: 8, 10. Batch: B1.

- [ ] 12. **Final gate: prove it was a rename.**
     Run the identifier check — `MerchantUser`, `PlatformUser`, `AdminRole`, `AdminPermission`,
     `PaymentSession`, `CartItem`, `CheckoutSession`, `PspConnection` must not appear in `src/` or
     `tests/` — and ship it **together with its exception list** (`MerchantUserRegistrationSubmitted`,
     the `MerchantUser:*` config keys, history comments). Without the list an implementer must either
     rename a retained contract or weaken the gate. Then the review that actually matters: walk the full
     diff and confirm no test's **assertion** changed except where this spec mandates the new value (a
     route path, a permission key or scheme id, a table name, a type or namespace identifier). Any other
     changed assertion is a behavior change wearing a rename's clothes — escalate it.
     Satisfies: REQ-15.3, 15.4, 15.5, REQ-1.1, 1.2, 1.4, 1.5. Depends on: 2, 3, 4, 5, 6, 7, 8, 9, 10, 11.
     Verify: full `dotnet test` green; the identifier check passes with its exception list; the
     assertion-diff review is recorded.

## Suggested execution batches

This feature is **coupled** — tasks 3-10 share the naming law, the alias discipline, and the same type
inventory. Run them in ONE session (`/spec-implement all`, or
`scripts/pane-loop.sh hierarchical-naming all-in-one`). Separate sessions do not share cache and would
re-pay the cold cache-write to re-acquire the same context for each module.

Two exceptions, both deliberate:

- **Task 0 is its own PR and merges before anything else** (REQ-9.5). It is not part of the sweep.
- **Task 1 must be green before task 3 starts.** If the detectors are written after the code they guard,
  they prove agreement, not survival.

`Batch: B1` (task 11) is documentation and can share a session with task 12 if you prefer.

**PR shape:** one branch, a PR per module (tasks 4, 5, 6) plus one for each of 7, 8, 9, 10. A single
262-file PR gets rubber-stamped, and rubber-stamping is how a dropped CSRF filter ships.
