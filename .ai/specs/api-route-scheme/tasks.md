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
     REQ-1.1, 1.2, 1.3, 1.4, REQ-2 (all), REQ-3.1, 3.2, 3.5, 3.6, 3.7, REQ-4.1, 4.2, 4.4, REQ-5.1, 5.2, 5.4, REQ-6.1, REQ-7.1, 7.2, REQ-9.2.

- [x] 2. OIDC callback path via configuration — move the callback (OIDC middleware, NOT a mapped endpoint) by updating `AdminAuthOptions.CallbackPath` + `ProducerOidcOptions.CallbackPath` defaults and `appsettings.json` / `appsettings.Development.json` `CallbackPath` from `/admin|/producer/auth/callback` to `/api/v1/admins|producers/auth/callback`; preserve cookie/CSRF/rotation/revoke semantics. (`ErrorPath`/`RegisterUrl`/`DefaultReturnPath` are frontend paths — leave them.)
     REQ-6.2, REQ-6.3. Depends on: 1. Batch: B1.

- [x] 3. CORS policy selector — update `PolCorsPolicyProvider.GetPolicyAsync` path checks from `StartsWithSegments("/admin"|"/producer")` to `/api/v1/admins` / `/api/v1/producers` so admin/producer BFF cookie XHR keeps its credentialed policy; preflight still answered before auth.
     REQ-9.1, 9.3. Depends on: 1. Batch: B1.

- [x] 4. Architecture guard + complete legacy-404 — add `tests/Hosts.Tests/RouteSchemeConventionTests.cs` (WebApplicationFactory + `EndpointDataSource`): assert every `RouteEndpoint` matches the LITERAL `^/api/v1/(products|carts|checkouts|orders|payments|admins|producers|webhooks|reports)(/.*)?$` OR the infra allowlist (`/health/live`, `/health/ready`, `/openapi/`, `/scalar`); and assert every old method+path (all 47) plus the two old OIDC callback paths return 404 — generated from the mapping table, not per-area sampling.
     REQ-1.5, REQ-4.3, REQ-5.3. Depends on: 1, 2, 3. Batch: B2.

- [x] 5. Path-coupled + regression test sweep — add targeted integration tests that `EndpointDataSource` can't cover (CORS preflight credentialed-policy at new paths; OIDC challenge `redirect_uri` + callback handling; `Location` headers point to new paths); assert auth preservation at new paths (admins-area rejects tenant/producer session; anon `login`/`register` reachable; products write=producer/read=tenant); confirm infra endpoints still answer at their old paths; and update every existing route-asserting test to new paths (`SfsOpenApiTests`, `ProducerScalarSecurityTests`, `CorsTests`, `Hosts.Tests`, `Integration.Tests`).
     REQ-3.3, 3.4, REQ-4 (infra untouched), REQ-7.3, REQ-9.4. Depends on: 1, 2, 3. Batch: B2.

- [x] 6. Documentation sync — update `CODING_STANDARDS.md` (API conventions line), `ARCHITECTURE.md`, and `docs/reference/platform-modules.md` from `/api/{surface}/v1` to `/api/v1/{area}` (version-first global, audience per-endpoint). Memory already updated.
     REQ-7 (doc consistency), Overview.

- [x] 7. [optional] Cutover coordination (Definition of Done) — out-of-repo checklist: update the admin SPA + producer SPA route calls to the new paths; re-register the PSP dev webhook callback URL (2C2P/Omise) to `/api/v1/webhooks/{pspConnectionId}`; re-register the Google OAuth dev `redirect_uri` for the admin + producer callbacks.
     REQ-8.1, 8.2, 8.3. Depends on: 1, 2.
- Task 7 is out-of-repo coordination — do at cutover, not a code session.
