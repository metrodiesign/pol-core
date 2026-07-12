# Requirements: Hierarchical Naming (namespace + route)

> Status: approved 2026-07-12, amended 2026-07-12
> Derived FROM `design.md` (approved 2026-07-12, rev 2). Design is upstream: if a requirement below
> conflicts with the design, the requirement is wrong. Each REQ cites its design section.

## Overview

`pol-core` is a modular monolith whose naming drifted into three shapes at once — singular module
projects beside plural ones, parent-prefixed flat types (`MerchantUserRoleDefinition`,
`PlatformUserSessionDecision`), and one compound route area (`/api/v1/merchant-users`) beside eight
single-noun ones. The `admin` module carries two parallel families (`Admin*`, `Platform*`) that agree
with neither its route (`/admins`) nor its schema (`admin`). The root cause is that
`.ai/shared/ARCHITECTURE.md` §Naming Conventions never stated a rule beyond "PascalCase".

This spec renames the repo onto one hierarchical law and writes that law into the canon so the drift
cannot recur. It is **behavior-preserving**: no endpoint gains or loses a capability, no authorization
decision changes, no row changes meaning. The requirements below therefore spend most of their weight
not on the rename but on the four ways a rename can **remove a security control while every existing
test stays green** — the group move (CSRF/CORS/tier), a fail-open architecture guard, a config section
that starts binding when its name changes, and a missed GRANT in raw SQL.

## REQ-1: Behavior preservation

**User Story:** As the maintainer, I want the rename to change names and nothing else, so that a green
suite is real evidence rather than a false one.

**Acceptance Criteria (EARS):**

- 1.1 THE SYSTEM SHALL expose, after the rename, exactly the same set of HTTP capabilities it exposed before — every endpoint that existed still exists (at its new path per REQ-6) and no endpoint is added. (design: Architecture Overview)
- 1.2 THE SYSTEM SHALL reach the same authorization decision for every (principal, endpoint) pair as it did before the rename. (design: Architecture Overview, §5)
- 1.3 THE SYSTEM SHALL persist and read back the same data for every entity, with only table and column *names* changed per REQ-10. (design: §6)
- 1.4 IF a pre-existing test's ASSERTION must change for the suite to pass, AND the new asserted value is NOT one this spec explicitly mandates (a route path per REQ-6, a permission key or scheme id per REQ-11, a table name per REQ-10, or a type/namespace identifier per REQ-3/4/16), THEN THE SYSTEM SHALL treat that as a behavior change and escalate it for review rather than absorb it into the rename. (design: Testing Strategy)
- 1.5 WHERE the design records a deliberate exception to a locked decision (L8 exceptions in §8 and §10), THE SYSTEM SHALL keep the pre-existing name unchanged. (design: §8, §10)

## REQ-2: The naming law is codified, not just applied

**User Story:** As a future contributor, I want the naming rule written in the canon, so that the repo
does not drift back within three months.

**Acceptance Criteria (EARS):**

- 2.1 THE SYSTEM SHALL record laws L1 through L8 in `.ai/shared/ARCHITECTURE.md` §Naming Conventions. (design: Architecture Overview, Technology Decisions #7)
- 2.2 THE SYSTEM SHALL state in that canon that module projects and sub-namespaces are plural while type names are singular. (design: L3)
- 2.3 THE SYSTEM SHALL state in that canon that a module's root aggregate stays at the module-root namespace and is never given a sub-namespace of its own. (design: L2)
- 2.4 THE SYSTEM SHALL state in that canon that configuration keys, OpenAPI security-scheme ids, and integration-event type names are flat external contracts to which the prefix-drop rule does not apply. (design: L8)
- 2.5 THE SYSTEM SHALL amend `.ai/specs/rf1-schema-reset/design.md` §149, whose `Producer -> MerchantUser` sweep rule this spec supersedes, so that the repo does not hold two contradictory naming canons. (design: Non-Functional Considerations)
- 2.6 THE SYSTEM SHALL amend REQ-2.1 of `.ai/specs/api-route-scheme/requirements.md` so that its area taxonomy lists `merchants` and no longer lists the compound area this spec removes. (design: §4, Non-Functional Considerations)
- 2.7 THE SYSTEM SHALL amend REQ-2.8 of that same file, which enumerates the literal sub-resources of the `admins` area, to include the sub-resources this spec adds — `merchants/users` (REQ-6.3) and the four master-data collections (REQ-6.4). (design: §4, Non-Functional Considerations)
- 2.8 WHERE `.ai/specs/api-route-scheme/requirements.md` still uses pre-rf1 vocabulary (`producers`, `tenant`) that no longer names anything in the codebase, THE SYSTEM SHALL bring it to current vocabulary as part of the same amendment, so the amended file is not internally inconsistent. (design: Non-Functional Considerations)

## REQ-3: Module projects are pluralised

**User Story:** As a maintainer, I want every module project to follow one pluralisation rule, so that
`Admin` beside `Merchants` stops being a coin flip.

**Acceptance Criteria (EARS):**

- 3.1 THE SYSTEM SHALL rename the module projects `Admin.*` to `Admins.*`, `Cart.*` to `Carts.*`, and `Checkout.*` to `Checkouts.*` across their Domain, Application, and Infrastructure layers. (design: §1)
- 3.2 THE SYSTEM SHALL rename the test projects `Admin.Tests`, `Cart.Tests`, and `Checkout.Tests` to `Admins.Tests`, `Carts.Tests`, and `Checkouts.Tests`. (design: §1)
- 3.3 THE SYSTEM SHALL update all twelve affected entries in `pol-core.slnx`, and `dotnet build` SHALL resolve all 40 projects afterwards. (design: §1, Testing Strategy)
- 3.4 THE SYSTEM SHALL perform every folder move with `git mv`, preserving per-file history. (design: Technology Decisions #1)
- 3.5 THE SYSTEM SHALL leave `SchemaNames.Admin = "admin"` singular and unchanged, and SHALL record in the design why the project name and the schema name deliberately differ. (design: §1)
- 3.6 THE SYSTEM SHALL delete the `src/Modules/Identity/`, `src/Modules/Producer/`, and `src/Modules/Tenant/` directories, which are absent from `pol-core.slnx` and hold only stale `obj/` build output, in a commit separate from the rename sweep. (design: §1)

## REQ-4: Domain types are nested and de-prefixed

**User Story:** As a developer reading `Merchants.Domain`, I want the namespace to carry the parent and
the type to carry only itself, so that names stop repeating their own context.

**Acceptance Criteria (EARS):**

- 4.1 THE SYSTEM SHALL move each domain type into the sub-namespace of the sub-domain it belongs to, per the mapping tables in design §2. (design: §2)
- 4.2 THE SYSTEM SHALL drop from each moved type's name every token its new namespace already carries. (design: L4, §2)
- 4.3 WHERE dropping a token would leave a name that is ambiguous inside its own module (a bare verb or a framework word), THE SYSTEM SHALL stop shortening and keep the qualifying token. (design: L4, §3)
- 4.4 THE SYSTEM SHALL dissolve the `Platform*` family in the admin module, mapping `PlatformUser` to `Admins.Domain.Users.User` and its satellites accordingly. (design: §2)
- 4.5 THE SYSTEM SHALL keep each module's root aggregate at the module-root namespace, creating no sub-namespace that would hold only the root — specifically `Checkouts.Domain.Session` and `Payments.Domain.Session`, not `...Sessions.Session`. (design: L2, §2)
- 4.6 THE SYSTEM SHALL rename the `MasterData` abstract base to `MasterDataItem`, because a type may not share the name of its own namespace. (design: §2)
- 4.7 THE SYSTEM SHALL limit sub-namespace nesting to two levels below a layer. (design: L5)

## REQ-5: Cross-module ambiguity is resolved by alias, never by re-prefixing

**User Story:** As a reviewer, I want one fixed way to disambiguate the four `Session` types, so that
each file does not invent its own.

**Acceptance Criteria (EARS):**

- 5.1 WHERE one file consumes colliding type names from two modules, THE SYSTEM SHALL disambiguate with a file-level `using` alias of the fixed form `<ModuleSingular><Type>`. (design: L6)
- 5.2 THE SYSTEM SHALL NOT declare these aliases in `GlobalUsings`, because a global alias would restore the flat names repo-wide and cancel the change. (design: L6, Technology Decisions #5)
- 5.3 THE SYSTEM SHALL NOT resolve any collision by re-adding a parent prefix to one of the colliding types. (design: L6)
- 5.4 THE SYSTEM SHALL NOT use partial qualification (e.g. `Users.Session`) to disambiguate. (design: L6)
- 5.5 THE SYSTEM SHALL apply the alias discipline in `tests/` as well as `src/`, since the cross-plane tests consume both planes. (design: L6)

## REQ-6: Routes move to the hierarchical scheme

**User Story:** As an API consumer, I want the merchant-user surface to sit under the merchant it
belongs to, so that the URL reflects the resource hierarchy.

**Acceptance Criteria (EARS):**

- 6.1 THE SYSTEM SHALL serve the merchant-user surface under `/api/v1/merchants/users/**` instead of `/api/v1/merchant-users/**`. (design: §4)
- 6.2 THE SYSTEM SHALL serve merchant provisioning at `POST /api/v1/merchants` and merchant read at `GET /api/v1/merchants/{code}`, moved out of the `admins` area. (design: §4)
- 6.3 THE SYSTEM SHALL serve merchant-user approval and rejection at `POST /api/v1/admins/merchants/users/{subject}/approve` and `.../reject`. (design: §4)
- 6.4 THE SYSTEM SHALL serve the four master-data collections directly at `/api/v1/admins/{positions|offices|levels|divisions}`, dropping the `master-data` wrapper segment entirely. (design: §4)
- 6.5 THE SYSTEM SHALL leave `{code}` unconstrained on `GET /api/v1/merchants/{code}`, because adding a route constraint would itself be a behavior change and the templates cannot collide with `/merchants/users/**` in any case. (design: §4)
- 6.6 THE SYSTEM SHALL update every `Location` response header that embeds a moved path. (design: §4, §9)
- 6.7 THE SYSTEM SHALL leave every other route unchanged. (design: §4)
- 6.8 IF an endpoint path does not begin with `/api/v1/{area}` for one of the nine areas — with `merchants` replacing `merchant-users` — THEN THE SYSTEM SHALL fail the architecture guard, which SHALL remain fail-closed on the literal `v1`. (design: §4)

## REQ-7: The moved endpoints keep every control they had

**User Story:** As the platform owner, I want the two endpoints leaving the `admins` group to arrive
with all four of their controls intact, so that a path change does not become a privilege change.

**Acceptance Criteria (EARS):**

- 7.1 WHEN merchant provisioning and merchant read move out of the `admins` route group, THE SYSTEM SHALL re-attach `AdminCsrfFilter` to them. (design: §5)
- 7.2 THE SYSTEM SHALL continue to apply the credentialed admin CORS policy to both moved endpoints. (design: §5)
- 7.3 THE SYSTEM SHALL continue to require the `"admin"` authorization policy on both moved endpoints. (design: §5)
- 7.4 THE SYSTEM SHALL continue to require the Super tier on `POST /api/v1/merchants`. (design: §5)
- 7.5 THE SYSTEM SHALL have tests asserting all four controls on the moved endpoints **written and passing against the pre-move code** before the endpoints are moved. (design: §5, Error Handling Strategy)
- 7.6 IF a request presents a merchant-user session to an admin-plane endpoint, THEN THE SYSTEM SHALL reject it exactly as it did before the move. (design: §5)

## REQ-8: CORS policy selection stays path-based and is guarded

**User Story:** As the platform owner, I want the CORS mechanism left alone and its rot risk covered by
a test, so that we do not trade a working mechanism for a broken one.

**Acceptance Criteria (EARS):**

- 8.1 THE SYSTEM SHALL continue to select the CORS policy in `PolCorsPolicyProvider` from the request path, and SHALL NOT switch to endpoint metadata. (design: §5, Technology Decisions #3)
- 8.2 THE SYSTEM SHALL extend the admin-plane path table to cover `/api/v1/merchants` and `/api/v1/merchants/{code}` while excluding `/api/v1/merchants/users/**`, which is the merchant-user plane. (design: §5)
- 8.3 THE SYSTEM SHALL enumerate `EndpointDataSource` in an architecture test and, for every endpoint carrying the `"admin"` authorization policy or `AdminCsrfFilter`, assert that `PolCorsPolicyProvider` returns the admin policy for its route template. (design: §5)
- 8.4 IF an admin-plane endpoint's template is not covered by the admin CORS path table, THEN THE SYSTEM SHALL fail that test — the guard SHALL be fail-closed. (design: §5)
- 8.5 THE SYSTEM SHALL preserve the existing preflight behavior exercised by `CorsTests`, since a CORS preflight is an `OPTIONS` that minimal-API endpoints do not accept and therefore carries no endpoint metadata. (design: §5)
- 8.6 THE SYSTEM SHALL have the guard of REQ-8.3 written and passing against the pre-move code before any endpoint moves, on the same grounds as REQ-7.5: a detector authored after the code it guards proves only that the two agree, not that the control survived. (design: §5, Error Handling Strategy)

## REQ-9: Configuration keys are frozen; the section-name defect is fixed separately

**User Story:** As the platform owner, I want the rename to be incapable of widening the admin
open-redirect allowlist, so that a naming change cannot become an authorization change.

**Acceptance Criteria (EARS):**

- 9.1 THE SYSTEM SHALL leave every configuration section key unchanged by this rename, including `Google:Oidc`, `MerchantUser:*`, `Cors:*`, `ConnectionStrings:*`, and `AdminAllowlist:Subjects`. (design: §5b, L8)
- 9.2 THE SYSTEM SHALL NOT change `PlatformUserSessionOptions.SectionName` as part of the rename sweep. (design: §5b)
- 9.3 THE SYSTEM SHALL fix the section-name mismatch — `SectionName` reads `"PlatformUserSession"` while `appsettings.json` defines `"AdminSession"`, so admin session options bind to nothing and `ReturnUrlAllowlist` is empty — as **task 0 of this spec, shipped on its own branch and PR, merged before any rename task starts**. (design: §5b)
- 9.4 WHEN that fix is prepared, THE SYSTEM SHALL have the configured `ReturnUrlAllowlist` value audited in staging and production first, because the fix makes a previously dead allowlist start binding, widening the admin open-redirect surface from deny-everything to whatever is configured. (design: §5b)
- 9.5 IF task 0 has not merged, THEN THE SYSTEM SHALL NOT begin the rename sweep, since the sweep's safety depends on that token already being consistent. (design: §5b)
- 9.6 WHEN task 0 has merged, THE SYSTEM SHALL leave the now-consistent `AdminSession` section name untouched by the sweep, so that the sweep is a genuine no-op on it. (design: §5b)

## REQ-10: Database objects are renamed, including every raw-SQL surface

**User Story:** As an operator, I want a fresh database to come up green, so that a table renamed in C#
but missed in raw SQL does not surface as a runtime permission error.

**Acceptance Criteria (EARS):**

- 10.1 THE SYSTEM SHALL rename the `admin` and `merch` tables per design §6, and SHALL leave the `shop` and `txn` tables unchanged. (design: §6, L7)
- 10.2 WHERE a table name would become ambiguous or unreadable within its schema after the prefix drop, THE SYSTEM SHALL keep the qualifying token — specifically `admin.UserAudits` and `shop.CartItems`. (design: L7, §6)
- 10.3 THE SYSTEM SHALL update the RLS predicate `sec.fn_merchant_predicate` and the security policies in `20260711142515_SecurityObjects.cs` to the new table names. (design: §6)
- 10.4 THE SYSTEM SHALL update every line of the per-table GRANT matrix in that same migration. (design: §6)
- 10.5 THE SYSTEM SHALL update the table names in `docker/bootstrap/assert-fresh-db.sql`, which is a required CI check. (design: §6)
- 10.6 THE SYSTEM SHALL update the table reference in `docker/entrypoint.sh`. (design: §6)
- 10.7 IF any renamed table is missing a GRANT after the sweep, THEN THE SYSTEM SHALL fail on a fresh database rather than at runtime — the RLS matrix test and `assert-fresh-db.sql` SHALL both run against a freshly migrated database. (design: §6, Error Handling Strategy)

## REQ-11: Permission keys and auth schemes

**User Story:** As an FE developer, I want the permission keys to mirror the routes, so that the gate I
check matches the URL I call.

**Acceptance Criteria (EARS):**

- 11.1 THE SYSTEM SHALL rename the admin catalog's `merchant_user.approve` and `merchant_user.reject` to `merchants.users.approve` and `merchants.users.reject`, with the group renamed to match. (design: §7)
- 11.2 THE SYSTEM SHALL drop the redundant self-prefix from the merchant-user catalog, renaming `merchant_user.roles.view|manage` to `roles.view|manage` and `merchant_user.user.roles` to `users.roles`. (design: §7)
- 11.3 THE SYSTEM SHALL leave every other permission key unchanged, since none carried a parent prefix. (design: §7)
- 11.4 THE SYSTEM SHALL rename the auth scheme `PlatformUserSession` to `AdminSession`, which is the core of dissolving the `Platform*` family. (design: §8)
- 11.5 THE SYSTEM SHALL leave the auth scheme `MerchantUserSession` unchanged, because the principal genuinely is a user *of* a merchant and the scheme id is a flat OpenAPI contract. (design: §8, L8)
- 11.6 THE SYSTEM SHALL leave the rate-limit policy names unchanged. (design: §8, L8)
- 11.7 THE SYSTEM SHALL move the merchant-user OIDC callback path to `/api/v1/merchants/users/auth/callback`. (design: §8)
- 11.8 WHEN the new callback path is deployed, THE SYSTEM SHALL have the corresponding authorized redirect URI updated in Google Console first, since that contract lives outside the repository. (design: §8)
- 11.9 IF the Google Console redirect URI is not updated, THEN login SHALL break in that environment while CI stays green — therefore the update SHALL be an explicit operator step, staged before production. (design: Error Handling Strategy)

## REQ-12: The FE-facing contract change is published

**User Story:** As an FE developer, I want one document listing everything that breaks, so that I am not
discovering it endpoint by endpoint.

**Acceptance Criteria (EARS):**

- 12.1 THE SYSTEM SHALL record every route change from REQ-6 in `.ai/specs/hierarchical-naming/FE-MIGRATION.md` — this spec's own document, not `rf1-schema-reset`'s, so the two migrations do not braid their histories. (design: §9)
- 12.2 THE SYSTEM SHALL record every changed `Location` response header. (design: §9)
- 12.3 THE SYSTEM SHALL record every changed permission-key string. (design: §9)
- 12.4 THE SYSTEM SHALL record the OpenAPI security-scheme id change from `PlatformUserSession` to `AdminSession`, on which generated clients key. (design: §9)
- 12.5 THE SYSTEM SHALL record the changed master-data operation ids. (design: §9)
- 12.6 THE SYSTEM SHALL add a pointer from `.ai/specs/rf1-schema-reset/FE-MIGRATION.md` to this spec's document, so an FE reader following the older trail is not left on a stale page. (design: §9)
- 12.7 THE SYSTEM SHALL update the operator-facing documentation that names the old routes — `docs/runbooks/local-dev-run.md` and `docs/reference/producer-module.md`. (design: §9)

## REQ-13: Integration events are out of scope

**User Story:** As the worker's maintainer, I want the outbox vocabulary left alone, so that a rename in
the modules does not reach into a flat cross-module registry.

**Acceptance Criteria (EARS):**

- 13.1 THE SYSTEM SHALL leave `MerchantUserRegistrationSubmitted` unchanged in `src/Contracts/`, since `namespace Contracts` is flat and the prefix-drop rule has no namespace to lean on. (design: §10, L8)
- 13.2 THE SYSTEM SHALL leave the `OutboxDispatcher` event-type registry keys unchanged. (design: §10)
- 13.3 WHEN the rename is deployed, THE SYSTEM SHALL still round-trip an outbox message from publish to worker consumption. (design: Testing Strategy)

## REQ-14: The cutover is reset-only

**User Story:** As an operator, I want the migration history rewritten and the database reset, so that
EF's stored CLR type names cannot disagree with the code.

**Acceptance Criteria (EARS):**

- 14.0 THE SYSTEM SHALL confirm, before any migration is rewritten, that no production deployment holds real data — REQ-14.2 destroys the database volume, so if that assumption is false this spec SHALL stop and be redesigned around a transfer migration. This is stated as a checkable precondition rather than left as knowledge in someone's head. (design: Technology Decisions #2)
- 14.1 THE SYSTEM SHALL rewrite the three existing migrations, their designers, and `PolDbContextModelSnapshot` in place, rather than adding a transfer migration. (design: Technology Decisions #2, Sequence Diagrams)
- 14.2 WHEN the rename is deployed, THE SYSTEM SHALL be brought up on a freshly created database (`docker compose down -v`, then `dotnet ef database update`). (design: Sequence Diagrams)
- 14.3 THE SYSTEM SHALL report no pending model changes against that fresh database. (design: Error Handling Strategy)
- 14.4 THE SYSTEM SHALL NOT be deployed as a rolling upgrade over a populated database. (design: Sequence Diagrams)

## REQ-15: No guard may fail open, and the sweep gate has an explicit exception list

**User Story:** As a reviewer, I want the guards to fail loudly after the rename, so that a dead guard
does not read as a passing one.

**Acceptance Criteria (EARS):**

- 15.1 THE SYSTEM SHALL update the hardcoded namespace literals in `AdminArchitectureTests` and `MerchantsArchitectureTests`, which after the rename would match nothing and pass vacuously. (design: Error Handling Strategy)
- 15.2 THE SYSTEM SHALL add a positive assertion to those tests requiring every forbidden namespace to resolve to at least one real assembly, so that a typo fails instead of passing. (design: Error Handling Strategy)
- 15.3 THE SYSTEM SHALL verify that none of the identifiers `MerchantUser`, `PlatformUser`, `AdminRole`, `AdminPermission`, `PaymentSession`, `CartItem`, `CheckoutSession`, or `PspConnection` appears in `src/` or `tests/`, other than the exceptions in REQ-15.4. (design: Testing Strategy)
- 15.4 WHERE the design deliberately retains an old name, THE SYSTEM SHALL list it as an explicit exception to that check — namely `MerchantUserRegistrationSubmitted`, the `MerchantUser:*` configuration keys, and comments citing history. (design: Testing Strategy)
- 15.5 IF the exception list is absent, THEN an implementer SHALL be forced to either rename a retained contract or weaken the check — therefore the list SHALL ship with the check. (design: Testing Strategy)

## REQ-16: The API host is organised by area

**User Story:** As a developer opening `src/Hosts/Api`, I want the twelve `MerchantUser*.cs` files to sit
under the area they serve, so that the composition root is navigable rather than a flat pile.

**Acceptance Criteria (EARS):**

- 16.1 THE SYSTEM SHALL group the `src/Hosts/Api` files by API area into `Api/Admins/`, `Api/Merchants/`, `Api/Payments/`, and `Api/Webhooks/`, leaving files that belong to no single area at the host root. (design: Component map, D7)
- 16.2 THE SYSTEM SHALL place each grouped file in the namespace `Api.<Area>` matching its folder. (design: Component map, D7)
- 16.3 THE SYSTEM SHALL apply the REQ-4 prefix-drop rule to these files' type names, since their namespace now carries the area token. (design: L4, Component map)
- 16.4 THE SYSTEM SHALL keep the endpoint route mappings themselves in `Program.cs` unless a task explicitly splits them, so that this reorganisation does not silently rewrite the route table. (design: Architecture Overview)
- 16.5 THE SYSTEM SHALL move these files with `git mv`. (design: Technology Decisions #1)

## Edge Cases & Open Questions

- **Accepted throwaway.** rf2 replaces both RBAC catalogs with `iam.*`, rf3 replaces `PspConnection`,
  rf6 replaces `PaymentSession`. Roughly half the renamed files are scheduled for deletion within five
  specs. The user accepted this cost on 2026-07-12 (D13); it is recorded so it is not re-discovered.
- **`api-route-scheme` is bigger than a one-line amendment.** That requirements file is still written in
  **pre-rf1 vocabulary** — `requirements.md:43` says `producers`, and several REQs still say `tenant` /
  `producer`. REQ-2.8 also enumerates the literal admin sub-resources, which REQ-6.3 and REQ-6.4 extend.
  The amendment is part of this spec's work; its full extent is confirmed during `/spec-tasks`.
- **Review order is load-bearing.** REQ-7.5, REQ-8.6 and REQ-15.1/15.2 require the detectors to exist
  *before* the code they detect against moves. A tasks breakdown that ships the sweep first and the
  guards second satisfies the letter of these requirements and defeats their purpose.

### Analysis findings log (`/spec-analyze`, anchor `a71dd1d`, 2026-07-12)

All ten findings were raised as questions and resolved by the user in one pass; every one was accepted
as recommended. Recorded so a re-run skips them and so no finding code dangles into a lost conversation.

| # | category | finding | decision |
|---|----------|---------|----------|
| F1 | logical inconsistency | REQ-1.4 escalated *every* assertion change, but REQ-6 (routes) and REQ-11 (permission keys) mandate assertion changes by construction — the rule cancelled itself. | **Fixed.** REQ-1.4 now exempts assertions whose new value this spec explicitly mandates. |
| F2 | gap | D7 (nest `src/Hosts/Api` into `Api/<Area>/`) had **no requirement at all** — 12 files would have been untraceable and unimplemented. | **Fixed.** New REQ-16. |
| F3 | gap / process | REQ-9.3-9.5 described a bugfix PR declared out of scope, but `spec-trace.sh` requires every criterion to be cited by a task — no task could honestly cite them. | **Fixed.** The bugfix is **task 0 of this spec** (own branch and PR, gated to merge first). REQ-9.6 added so the sweep is a proven no-op on that token afterwards. |
| F4 | gap | No criterion amended `api-route-scheme`, whose taxonomy REQ-6.8 depends on. | **Fixed.** REQ-2.6, 2.7, 2.8. |
| F5 | gap | `docs/runbooks/local-dev-run.md` and `docs/reference/producer-module.md` name the old routes; nothing required updating them. | **Fixed.** REQ-12.7. |
| F6 | ambiguity | REQ-15.3 said "the old compound identifiers" without naming them — not testable as written. | **Fixed.** The eight identifiers are now enumerated. |
| F7 | gap | REQ-7.5 required its detector to pass against pre-move code; the CORS guard (REQ-8.3) — same trap, same risk — carried no such requirement. | **Fixed.** REQ-8.6. |
| F8 | gap | T11 (delete the dead `Identity/`, `Producer/`, `Tenant/` folders) had no criterion. | **Fixed.** REQ-3.6, in a commit separate from the sweep. |
| F9 | unstated assumption | REQ-14.2 destroys the database volume, yet the spec never stated the assumption that no production data exists. If that assumption is wrong, REQ-14 is not a rename — it is data loss. | **Fixed.** REQ-14.0 makes it a checkable precondition with an explicit stop condition. |
| F10 | ambiguity | REQ-12.1 wrote this spec's FE migration into **`rf1-schema-reset`'s** document. | **Fixed.** This spec owns `FE-MIGRATION.md`; REQ-12.6 leaves a pointer on the rf1 one. |

**Downstream sync:** `design.md`'s `## Requirement Traceability` table was updated in the same pass to
cover REQ-2.6-2.8, 3.6, 8.6, 9.6, 12.6-12.7, 14.0, and REQ-16. `tasks.md` does not exist yet.
