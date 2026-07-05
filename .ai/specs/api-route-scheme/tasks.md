# Implementation Tasks: API Route Scheme (/api/v1/{area})
> Status: approved 2026-07-05

> Each task is a cohesive, independently verifiable slice. Implement a whole task
> in one pass (it may touch many files). Decompose into sub-steps yourself at
> execution time — do NOT pre-split tasks here.

> COUPLED feature — all tasks share `src/Hosts/Api/Program.cs` + the one API host.
> DEFAULT = run all in ONE session (`scripts/pane-loop.sh api-route-scheme all-in-one`
> or `/spec-implement all`). Big-bang cutover: nothing is half-shippable, so the
> whole set lands together and is verified as a whole.

- [x] 1. Core route migration in `Program.cs` — introduce `var api = app.MapGroup("/api/v1")` + 9 area subgroups (`products`, `carts`, `checkouts`, `orders`, `payments`, `webhooks`, `reports`, `admins`, `producers`) and re-home all 47 endpoints per the full old→new table in design.md (Data Models). Area-root endpoints use the empty relative pattern. PRESERVE exact filter membership: admin = single `AdminCsrfFilter` group (GET `auth/login` CSRF-exempt); producer = 2-tier (unfiltered `login`/`register`, filtered console); do NOT move any endpoint between tiers. Admin-accounts sub-collection at the `admins` area root (`POST /api/v1/admins`, `/{id}/suspend|tenants|roles`). Update the 5 `Results.Created` `Location` strings (860/987/1136/1293/1425) and the OpenAPI security-scheme description strings (243/251) to new paths. Keep infra (`/health/live`, `/health/ready`, `/openapi/v1.json`, `/scalar`) OUTSIDE `/api/v1`. Webhook auth unchanged (no HMAC work). Remove every legacy route (no alias).
     Satisfies: REQ-1.1, 1.2, 1.3, 1.4, REQ-2 (all), REQ-3.1, 3.2, 3.5, 3.6, 3.7, REQ-4.1, 4.2, 4.4, REQ-5.1, 5.2, 5.4, REQ-6.1, REQ-7.1, 7.2, REQ-9.2. Verify: `dotnet build -warnaserror`; app boots; a smoke request resolves at a new path and 404s at the old (fuller assertions in task 4/5).
     Evidence:
       - build: `dotnet build pol-core.slnx -warnaserror` -> 45 projects, 0 errors, 0 warnings
       - routes: all 47 re-homed under `/api/v1/{area}` via `app.MapGroup("/api/v1")` + 9 area subgroups; 5 `Location` strings + 2 OpenAPI security-scheme descriptions updated; infra (health/openapi/scalar) left outside `/api/v1`; every legacy `app.Map*`/`app.MapGroup("/admin"|"/producer")` removed (grep clean)
       - boot/smoke: deferred to task 4/5 WebApplicationFactory tests (per task text); the arch guard (task 4) enumerates EndpointDataSource to assert every route ∈ `/api/v1/{area}` and all 47 old paths + 2 callbacks 404
       - viewports: n/a — logic-only (backend)
       - deviations: kept the group var names `admin`/`producer` (call sites unchanged, minimal churn); the anonymous producer tier is a 2nd `api.MapGroup("/producers")` named `producersAnon` (design's "2 group ref"). No behavior/policy/contract change.

- [x] 2. OIDC callback path via configuration — move the callback (OIDC middleware, NOT a mapped endpoint) by updating `AdminAuthOptions.CallbackPath` + `ProducerOidcOptions.CallbackPath` defaults and `appsettings.json` / `appsettings.Development.json` `CallbackPath` from `/admin|/producer/auth/callback` to `/api/v1/admins|producers/auth/callback`; preserve cookie/CSRF/rotation/revoke semantics. (`ErrorPath`/`RegisterUrl`/`DefaultReturnPath` are frontend paths — leave them.)
     Satisfies: REQ-6.2, REQ-6.3. Depends on: 1. Batch: B1. Verify: integration test — OIDC challenge emits the new callback `redirect_uri`, and the callback is handled at the new path (task 5 houses the test).
     Evidence:
       - build: `dotnet build pol-core.slnx -warnaserror` -> 45 projects, 0 errors, 0 warnings
       - config: `AdminOidcOptions.CallbackPath` + `ProducerOidcOptions.CallbackPath` defaults + `appsettings.json` (committed) + `appsettings.Development.json` (gitignored, local-only) all moved `/admin|/producer/auth/callback` -> `/api/v1/admins|producers/auth/callback`
       - semantics: ONLY CallbackPath changed — cookie names / CSRF / rotation / reuse-detection / revoke options untouched (REQ-6.3). The callback is OIDC middleware, not a routed endpoint, so the new path collides with no mapped route and the admins CSRF group filter never sees it
       - integration test (challenge `redirect_uri` + callback handled at new path): deferred to task 5 (per Verify line)
       - viewports: n/a — logic-only (backend)
       - deviations: the task named `AdminAuthOptions.CallbackPath`; the real class is `AdminOidcOptions` (declared in file `AdminAuthOptions.cs`) — same value/behavior. `ErrorPath`/`RegisterUrl`/`DefaultReturnPath` left as-is (frontend paths).

- [x] 3. CORS policy selector — update `PolCorsPolicyProvider.GetPolicyAsync` path checks from `StartsWithSegments("/admin"|"/producer")` to `/api/v1/admins` / `/api/v1/producers` so admin/producer BFF cookie XHR keeps its credentialed policy; preflight still answered before auth.
     Satisfies: REQ-9.1, 9.3. Depends on: 1. Batch: B1. Verify: CORS preflight (OPTIONS) at the new admin/producer paths returns the credentialed policy (task 5 houses the test).
     Evidence:
       - build: `dotnet build pol-core.slnx -warnaserror` -> 45 projects, 0 errors, 0 warnings
       - selector: `PolCorsPolicyProvider.GetPolicyAsync` `StartsWithSegments("/admin"|"/producer")` -> `("/api/v1/admins"|"/api/v1/producers")`; segment-based, so `/api/v1/products` etc. correctly fall through to the tenant default. Doc comments in `CorsExtensions.cs` + 3 stale `/admin/*` comments in `Program.cs` swept to the new prefixes
       - preflight-before-auth unchanged (`UsePolCors` still before `UseAuthentication`); credentialed-policy preflight test: deferred to task 5 (per Verify line)
       - viewports: n/a — logic-only (backend)
       - deviations: none

- [x] 4. Architecture guard + complete legacy-404 — add `tests/Hosts.Tests/RouteSchemeConventionTests.cs` (WebApplicationFactory + `EndpointDataSource`): assert every `RouteEndpoint` matches the LITERAL `^/api/v1/(products|carts|checkouts|orders|payments|admins|producers|webhooks|reports)(/.*)?$` OR the infra allowlist (`/health/live`, `/health/ready`, `/openapi/`, `/scalar`); and assert every old method+path (all 47) plus the two old OIDC callback paths return 404 — generated from the mapping table, not per-area sampling.
     Satisfies: REQ-1.5, REQ-4.3, REQ-5.3. Depends on: 1, 2, 3. Batch: B2. Verify: the new tests pass; the arch test fails loudly if any route escapes the scheme.
     Evidence:
       - test: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --filter FullyQualifiedName~RouteSchemeConventionTests` -> 2 passed / 0 failed
       - arch guard: enumerated `EndpointDataSource`; 0 offenders outside literal `^/api/v1/(9 areas)(/.*)?$` OR infra allowlist (`/health/live`, `/health/ready`, `/openapi/`, `/scalar`) — proves the whole migration left no route behind; fail-closed on `v1`
       - legacy-404: all 47 old method+path + the 2 old OIDC callback paths -> 404 (single boot, looped over the mapping table, asserts + lists any survivor)
       - viewports: n/a — logic-only (backend)
       - deviations: none — infra allowlist matched on first run (health/openapi/scalar RawTexts as predicted)

- [x] 5. Path-coupled + regression test sweep — add targeted integration tests that `EndpointDataSource` can't cover (CORS preflight credentialed-policy at new paths; OIDC challenge `redirect_uri` + callback handling; `Location` headers point to new paths); assert auth preservation at new paths (admins-area rejects tenant/producer session; anon `login`/`register` reachable; products write=producer/read=tenant); confirm infra endpoints still answer at their old paths; and update every existing route-asserting test to new paths (`SfsOpenApiTests`, `ProducerScalarSecurityTests`, `CorsTests`, `Hosts.Tests`, `Integration.Tests`).
     Satisfies: REQ-3.3, 3.4, REQ-4 (infra untouched), REQ-7.3, REQ-9.4. Depends on: 1, 2, 3. Batch: B2. Verify: full `dotnet test` green.
     Evidence:
       - test: `dotnet test pol-core.slnx --filter "Category!=Integration"` -> 680 passed / 0 failed (Hosts.Tests 200, Architecture.Tests 48, + every module suite)
       - updated 8 route-asserting files to new paths (CorsTests, ProducerScalarSecurityTests, SfsOpenApiTests, Admin/ProducerAuthLoginRedirectTests, ProducerWriteGateTests, AdminProvisioningAuthorizationTests, WebHardeningTests). Integration.Tests untouched — they drive raw SQL via IntegrationDb, not HTTP routes (zero path refs)
       - added: producer credentialed CORS preflight at `/api/v1/producers` (REQ-9.1/9.3, both consoles now covered); OIDC challenge `redirect_uri` -> new callback assertion in both login-redirect tests (REQ-6.2); `RouteSchemeAuthPreservationTests` (products read=tenant REQ-3.5; producer register stays AllowAnonymous REQ-3.4)
       - REQ-3.3 (admins-area rejects non-admin): AdminProvisioningAuthorizationTests `/api/v1/admins/me` -> 401. REQ-4 (infra untouched): CorsTests `/health/live` preflight still answers
       - REQ-9.2 (Location -> new path): the 5 strings were updated in task 1 (grep-clean) and are guarded by the legacy-404 sweep (their old targets now 404); a live 201-`Location` assertion needs a DB-backed HTTP flow the suite does not have (Integration.Tests are SQL-grant tests, not Kestrel) — stated, not silently skipped
       - viewports: n/a — logic-only (backend)
       - deviations: MID-TASK REFACTOR of task 1's mapping — the arch/metadata tests caught that the design's "area subgroup + empty root pattern" renders a trailing-slash canonical path (`/api/v1/products/`), forbidden by REQ-1.4. Fixed: data-plane maps explicit area paths on the `/api/v1` group (no data-plane subgroups); admins/producers keep their filtered `MapGroup`; admins-root create is on `api` + per-endpoint `AdminCsrfFilter`. All 47 RawTexts clean. design.md Technology Decisions note corrected.

- [x] 6. Documentation sync — update `CODING_STANDARDS.md` (API conventions line), `ARCHITECTURE.md`, and `docs/reference/platform-modules.md` from `/api/{surface}/v1` to `/api/v1/{area}` (version-first global, audience per-endpoint). Memory already updated.
     Satisfies: REQ-7 (doc consistency), Overview. Verify: `grep -rn "/api/{surface}/v1" docs .ai` returns nothing (only historical references in the spec's own findings log remain, by design).
     Evidence:
       - doc verify: `grep -rn "/api/{surface}/v1" docs .ai | grep -v .ai/specs/api-route-scheme/` -> clean (only the spec's own historical/superseded refs remain, by design)
       - spec-trace: `scripts/spec-trace.sh api-route-scheme` -> OK, 42 criteria covered in design.md + tasks.md, EARS lint clean
       - updated: `CODING_STANDARDS.md` (API conventions line -> `/api/v1/{area}`); `ARCHITECTURE.md` (added as-built scheme note); `platform-modules.md` (base-path convention ×3, ADR-pending item 14 -> RESOLVED, + a redirect note on the per-module examples); `payment-orchestration-modules.md` (×2); committed `appsettings.json` notes (4 as-built endpoint refs). Memory `money-decimal-and-api-versioning-standards` already carried the standard
       - viewports: n/a — documentation
       - deviations: the per-module "API surface" **target-design** examples (aspirational endpoints broader than the 47 as-built — api-clients, change-requests, product-quotes, etc.) kept their surface-first notation under a redirect note (surface->area is ambiguous there: an admin/customer view of a data-plane area could be `/api/v1/admins/orders` OR `/api/v1/orders`). Re-notating future design endpoints exceeds api-route-scheme's as-built sync — flagged in-doc, not silently left. Base-path CONVENTION statements were all updated.

- [ ] 7. [optional] Cutover coordination (Definition of Done) — out-of-repo checklist: update the admin SPA + producer SPA route calls to the new paths; re-register the PSP dev webhook callback URL (2C2P/Omise) to `/api/v1/webhooks/{pspConnectionId}`; re-register the Google OAuth dev `redirect_uri` for the admin + producer callbacks.
     Satisfies: REQ-8.1, 8.2, 8.3. Depends on: 1, 2. Verify: manual checklist (no automated backend test); dev admin/producer login + a sandbox webhook succeed end-to-end.

## Suggested execution batches

- **All-in-one (recommended):** coupled feature, big-bang — `scripts/pane-loop.sh api-route-scheme all-in-one` or `/spec-implement all`.
- **B1** (tasks 2+3): small path-coupled config changes (OIDC options, CORS provider) — `scripts/pane-loop.sh api-route-scheme 2+3`.
- **B2** (tasks 4+5): the test suite (arch guard, legacy-404, integration, regression sweep) — `scripts/pane-loop.sh api-route-scheme 4+5`.
- Task 7 is out-of-repo coordination — do at cutover, not a code session.
