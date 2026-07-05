# Plan: API Route Scheme migration (/api/v1/{area})
_Locked via grill — by Claude + metrodiesign; revised after Codex round 1_

## Goal
Migrate all 47 HTTP endpoints in `src/Hosts/Api/Program.cs` from legacy flat routes to one uniform scheme `/api/v1/{area}/{sub}` — version-first, single GLOBAL `v1`, audience enforced per-endpoint, big-bang cutover, pre-prod. NOT "path-only": the path is coupled to CORS policy selection, OIDC callback config, `Location` response headers, and hardcoded doc strings — all of these MUST change **so that runtime behavior stays identical**. 9 uniform-plural areas.

## Approach
1. `var api = app.MapGroup("/api/v1")` versioned root; 9 area subgroups: `products`, `carts`, `checkouts`, `orders`, `payments`, `webhooks`, `reports`, `admins`, `producers`.
2. **Preserve each endpoint's EXACT current filter membership** — the area-prefix change must NOT move any endpoint between tiers. Producer `login`/`register` stay OUTSIDE `ProducerCsrfFilter`/`ProducerBoundProducerFilter` (unfiltered `producers` ref, as mapped top-level today). Admin keeps its SINGLE `AdminCsrfFilter`'d group for everything incl `GET /auth/login` (CSRF no-ops on GET — that is how login works today); admin has NO separate unfiltered tier. So only the producer console uses two group references; admin uses one.
3. Re-home all 47 mapped endpoints per the complete old→new table (full 47-row + 2 callback table in `design.md` → Data Models & Interfaces). The admin-accounts sub-collection collapses onto the `admins` **area root** (no `admins/admins` doubling): `POST /api/v1/admins` (create), `/api/v1/admins/{id}/suspend`, `/api/v1/admins/{id}/tenants`, `/api/v1/admins/{id}/tenants/{tenantId}`, `/api/v1/admins/{id}/roles`. The guid-constrained `{id}` does not collide with the literal sub-resources (`roles`, `permissions`, `tenants`, `tenant-users`, `me`, `auth`).
4. **OIDC callback moves via CONFIG, not routing** (it is OIDC middleware, not a mapped endpoint → the arch test cannot see it): update `AdminAuthOptions.CallbackPath` + `ProducerOidcOptions.CallbackPath` defaults and `appsettings.json` / `appsettings.Development.json` `CallbackPath` from `/admin|/producer/auth/callback` → `/api/v1/admins|producers/auth/callback`; re-register the Google dev `redirect_uri`. `login` redirect + `logout` are mapped endpoints (move via group). `ErrorPath` (`/login-error`), `RegisterUrl`, `DefaultReturnPath` are FRONTEND SPA paths (`localhost:5200/...`) → OUT OF SCOPE.
5. **Update CORS policy selection** — `PolCorsPolicyProvider.GetPolicyAsync` selects the credentialed admin/producer policy via `Request.Path.StartsWithSegments("/admin"|"/producer")`. Change to `/api/v1/admins` / `/api/v1/producers`, else the admin/producer BFF cookie XHR falls to the credential-less tenant default policy and breaks. Update `CorsTests`.
6. **Update path-bearing RESPONSE data:** every `Results.Created($"/admin|/producer/…")` `Location` header (Program.cs lines 860, 987, 1136, 1293, 1425) → new paths, asserted in tests; and the hardcoded legacy path STRINGS in the OpenAPI security-scheme `Description`s (lines 243, 251, "GET /admin/auth/login" etc.) → new paths.
7. Keep infra OUTSIDE `/api/v1` at current paths: `/health/live`, `/health/ready`, `/openapi/v1.json`, `/scalar`.
8. **Arch test** (`tests/Hosts.Tests`, WebApplicationFactory + `EndpointDataSource`): assert every `RouteEndpoint.RoutePattern.RawText` matches the LITERAL `^/api/v1/(products|carts|checkouts|orders|payments|admins|producers|webhooks|reports)(/.*)?$` OR the infra allowlist. **Fail-closed on `v1`** (no `v\d+`) — a real v2 spec updates this guard. Because `EndpointDataSource` cannot see middleware/CORS, ADD targeted integration tests: CORS preflight returns the credentialed policy on the new admin/producer paths; OIDC challenge emits the new callback `redirect_uri` and the callback is handled; `Location` headers point to new paths.
9. Remove every legacy route (no alias/redirect/dual-serve) → 404. Assert legacy→404 for EVERY old method+path (all 47) PLUS the two old callback paths — generated from the mapping table, not per-area sampling.
10. Doc-sync (task-time): `CODING_STANDARDS.md`, `ARCHITECTURE.md`, `docs/reference/platform-modules.md` `/api/{surface}/v1` → `/api/v1/{area}`. Memory already updated.
11. Cutover DoD (out-of-repo): admin SPA + producer SPA route calls; PSP dev webhook URL; Google dev `redirect_uri`.

## Key decisions & tradeoffs
- **NOT purely path-only** (core insight from Codex round 1): CORS selector, OIDC `CallbackPath`, `Location` headers, and OpenAPI description strings are path-coupled and MUST change to KEEP behavior identical. Synced into `requirements.md` (new **REQ-9** path-coupled config/responses, **REQ-5.4** carve-out for path-bearing headers, **REQ-3.7** filter-membership, **REQ-6.2** callback-via-config) and `design.md` (full route table + CORS/OIDC/Location/OpenAPI sections + literal-`v1` guard).
- **version-first + GLOBAL v1**; literal `/api/v1` segment; REJECT `Asp.Versioning.*` (YAGNI).
- **Arch guard is LITERAL `/api/v1` (fail-closed)** — revised per Codex H4 (was version-agnostic `v\d+`). A future `/api/v2/{area}` is a deliberate, spec-worthy act that updates the guard; the guard should not silently accept an un-speced version.
- **2nd segment = domain area, not audience.** Supersedes the earlier `/api/{surface}/v1` standard.
- **Uniform PLURAL** area names incl `admins`/`producers`/`checkouts`.
- **Admin-accounts collection = `admins` area root** (no doubling); guid `{id}` vs literal sub-resources disambiguates routing.
- **`/payment-sessions` → `/api/v1/payments/sessions`**; **big-bang, no dual-route**; **webhook HMAC hardening out of scope**.

## Risks / open questions
- **Anon-login filter regression** — if the restructure lets an anon auth-entry endpoint inherit the CSRF/bound filter, login breaks. Mitigation = preserve exact filter membership (two group refs) + anon-login integration test at the new path.
- **CORS prefix matching** — `StartsWithSegments("/api/v1/admins")` must select the credentialed policy AND the OPTIONS preflight must still be answered before auth. Verify with a preflight test.
- **Google redirect_uri re-registration** must land BEFORE cutover, or dev OIDC login breaks (external, manual).
- **`ErrorPath`/`RegisterUrl`/`DefaultReturnPath`** assumed frontend SPA paths — confirm once (out of scope if so).
- **Filter/auth ordering** under nested/dual groups — confirm group filters still run before per-endpoint auth as today.

## Out of scope
- HMAC / webhook signature hardening (separate open item).
- Money / minor-units migration.
- Any handler, business-logic, contract, status-code, or query-shape change (path-bearing `Location`/redirect headers DO change — see decisions).
- Per-area independent versioning machinery.
- Frontend SPA source, and the frontend paths `ErrorPath`/`RegisterUrl`/`DefaultReturnPath`.
- Renaming entities or the `tenant-users` wire resource.
