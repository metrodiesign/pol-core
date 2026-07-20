# Requirements: API Route Scheme (/api/v1/{area})

> Status: approved 2026-07-05, amended 2026-07-05, amended 2026-07-12 (see below)

> **Historical vocabulary notice (2026-07-12, spec `hierarchical-naming` REQ-2.6-2.8).** This file was
> written and shipped 2026-07-05, when the control-plane actors were three separate modules — Tenant,
> Producer, Admin — each with a `tenant`/`producer`/`admin` authorization policy. rf1 (2026-07-12) merged
> Tenant+Producer into one `Merchants` module and retired the `producers` area in favor of
> `merchant-users`; hierarchical-naming (2026-07-12) retires `merchant-users` in favor of `merchants`,
> which now also carries the merchant-provisioning endpoints moved out of `admins` (REQ-6.1/6.2 of
> `hierarchical-naming/requirements.md`). REQ-2.1, REQ-2.3, REQ-2.8, and REQ-2.9 below are amended in
> place to that **target** taxonomy — hierarchical-naming's tasks 3-12 had not yet shipped it as of this
> amendment; `.ai/shared/ARCHITECTURE.md`'s "as-built" API-scheme note is the source of truth for what is
> live right now. Every other `producer`/`tenant` mention in REQ-3, REQ-6, REQ-8, REQ-9,
> and Edge Cases describes the vocabulary and endpoint names **as they stood at the original 2026-07-05
> migration** — read them as history, not current requirements. Current naming law:
> [ARCHITECTURE.md §Naming Conventions](../../shared/ARCHITECTURE.md#namespace--route-naming-law-l1-l8-spec-hierarchical-naming-2026-07-12);
> current module map: [PROJECT_CONTEXT.md](../../shared/PROJECT_CONTEXT.md).

## Overview

pol-core backend วันนี้เสิร์ฟ endpoint แบบ flat ไม่มี `/api` prefix ไม่มี version และ group
ไม่สม่ำเสมอ (มีแค่ `/admin` กับ `/producer` ที่ group ไว้ ส่วน `/products`, `/orders`, `/checkout`,
`/carts`, `/payment-sessions`, `/webhooks/{pspConnectionId}`, `/reports/...` แบนอยู่ระดับ root) รวม
47 route ในไฟล์เดียว `src/Hosts/Api/Program.cs`. spec นี้ migrate **ทุก** endpoint ไปสู่ scheme เดียว
สม่ำเสมอ: **`/api/v1/{area}/{sub}`** — version มาก่อน (version-first), ใช้ version เดียวทั้ง API
(**global v1** ไม่ใช่ per-module), และ segment ที่สองคือ **domain area** (ชื่อ plural สม่ำเสมอ) ไม่ใช่ audience.

การเปลี่ยนนี้ **supersede** มาตรฐานเดิม `/api/{surface}/v1` (surface = audience: producer/admin/webhooks)
ที่ตัดสินไว้ 2026-07-05 — path shape `/api/{x}/v1` ไม่ใช่สิ่งที่เปลี่ยน สิ่งที่เปลี่ยนคือ **แกนของ
segment ที่สอง (audience -> domain area)** และตำแหน่ง version (version-first). audience ยังคง**ไม่อยู่ใน
path** แต่บังคับต่อ endpoint ด้วย `RequireAuthorization` (policy `tenant`/`admin`/`producer`/HMAC-webhook)
เหมือนเดิมทุกประการ ทั้งสองระนาบ (control plane = admin/producer console, data plane = payment flow) ต้อง
แยกขาดเท่าเดิม การ migrate นี้เป็น **big-bang** (pre-prod ยังไม่มีอะไร live) — ลบ legacy path ทิ้ง ไม่ alias.

**ไม่ใช่ "path เท่านั้น":** path ผูกกับ config/response หลายจุดที่**ต้องเปลี่ยนตาม** เพื่อให้ behavior
คงเดิม — CORS policy selection (เลือกด้วย path prefix), OIDC `CallbackPath` (config ของ middleware),
`Location` response header (hardcode legacy path), และ string path ใน OpenAPI description. behavior เชิง
สังเกต (body/status/method/query, ใครเข้าถึงได้) คงเดิม แต่ path-bearing header + path-coupled config
เปลี่ยนไปตาม scheme ใหม่ (ดู REQ-5.4, REQ-9).

## REQ-1: Uniform versioned base path

**User Story:** As an API client, I want every backend endpoint under one predictable `/api/v1/...` base, so that I build URLs the same way everywhere and can read the API version off the path.

**Acceptance Criteria (EARS):**
- 1.1 THE SYSTEM SHALL expose every non-infrastructure HTTP endpoint under a base path of the form `/api/v1/{area}`, where `{area}` is exactly one of the defined API areas.
- 1.2 THE SYSTEM SHALL place the version segment `v1` immediately after `/api` and before `{area}` (version-first) for every area.
- 1.3 THE SYSTEM SHALL place a single shared version segment `v1` on all areas; per-area independent versioning is out of scope for this change, and no per-area versioning machinery (routing/docs/SDK) is provisioned now. (The future `/api/v2/{area}` escape is documented under Edge Cases.)
- 1.4 THE SYSTEM SHALL render each `{area}` segment as the lowercase plural form defined in REQ-2.1, applied uniformly across all nine areas.
- 1.5 IF an endpoint path (that is not an excluded infrastructure endpoint per REQ-4) does not begin with the literal `/api/v1/{area}` for a defined area, THEN THE SYSTEM SHALL fail an architecture test. (Fail-closed on `v1`: a genuine future `v2` is introduced by a dedicated spec that updates this guard — the guard SHALL NOT silently accept an un-specified version.)

## REQ-2: API area taxonomy and endpoint mapping

**User Story:** As a backend maintainer, I want a fixed set of API areas and a defined endpoint-to-area assignment, so that every route has exactly one correct home.

**Acceptance Criteria (EARS):**
- 2.1 THE SYSTEM SHALL define exactly thirteen API areas, each a lowercase plural noun: `products`, `carts`, `checkouts`, `orders`, `payments`, `admins`, `merchants`, `webhooks`, `reports`, `positions`, `offices`, `levels`, `divisions`. *(Amended 2026-07-12, hierarchical-naming REQ-2.6: originally `producers`; rf1 first renamed it `merchant-users`, and hierarchical-naming (tasks 3-12 of that spec, not yet shipped as of this amendment) renames it again to `merchants`, which also absorbs merchant provisioning out of `admins`. `.ai/shared/ARCHITECTURE.md`'s "as-built" API-scheme note still says `merchant-users` until that sweep ships. Further amended 2026-07-20: the four reference master-data lists, previously `/admins` sub-resources per the REQ-2.8 amendment below, are promoted to their own standalone areas — nine areas becomes thirteen — so their full CRUD surface (List/Get/Create/Update/Deactivate) reads as first-class API resources rather than admin internals.)*
- 2.2 THE SYSTEM SHALL assign every existing endpoint to exactly one area, per the complete old→new mapping in `design.md` (Data Models & Interfaces).
- 2.3 THE SYSTEM SHALL surface no area segment for a module with no top-level HTTP surface of its own — its endpoints appear under `admins`, `merchants`, or the relevant data-plane area. *(Amended 2026-07-12: the original `tenant` and `identity` modules named here no longer exist — `identity` was deleted (admin-module-shipped-identity-removed); `tenant` merged into the `Merchants` module at rf1, which inherited the `producers`/`merchant-users` area, itself renamed `merchants` by hierarchical-naming.)*
- 2.4 THE SYSTEM SHALL map the data-plane endpoints as: `/products` -> `/api/v1/products`; `/carts/...` -> `/api/v1/carts/...`; `/checkout/...` -> `/api/v1/checkouts/...`; `/orders/...` -> `/api/v1/orders/...`.
- 2.5 THE SYSTEM SHALL map payment-session endpoints under the `payments` area as `/api/v1/payments/sessions` and `/api/v1/payments/sessions/{paymentSessionId}/redirect`.
- 2.6 THE SYSTEM SHALL route the PSP webhook callback at `/api/v1/webhooks/{pspConnectionId}`.
- 2.7 THE SYSTEM SHALL route the reconciliation report at `/api/v1/reports/reconciliation`.
- 2.8 THE SYSTEM SHALL map the admin console endpoints under `/api/v1/admins/...`, with the admin-account sub-collection at the area root: `POST /api/v1/admins` (create), `/api/v1/admins/{id}/suspend`, `/api/v1/admins/{id}/merchants`, `/api/v1/admins/{id}/merchants/{merchantId}`, `/api/v1/admins/{id}/roles` — no doubled `admins/admins` segment (the guid-constrained `{id}` does not collide with the literal sub-resources `roles`/`permissions`/`merchants`/`merchants/users`/`me`/`auth`). *(Amended 2026-07-12, hierarchical-naming REQ-2.7 — target state, that spec's tasks 3-12 not yet shipped: `tenants`/`tenant-users` sub-resource tokens become `merchants`/`merchants/users` (REQ-6.3 of that spec); the four master-data lists `positions`/`offices`/`levels`/`divisions` were added here, `master-data` wrapper segment dropped (REQ-6.4 of that spec). Further amended 2026-07-20: those four lists moved OUT of `/admins` again — each is now its own standalone area at `/api/v1/{positions|offices|levels|divisions}` per REQ-2.1 above, not a sub-resource of `admins`.)*
- 2.9 THE SYSTEM SHALL map the merchant-user console endpoints under `/api/v1/merchants/users/...`, nested under the `merchants` area alongside merchant provisioning — no separate area of its own. *(Amended 2026-07-12: originally the producer console under `/api/v1/producers/...`; rf1 first moved it to `/api/v1/merchant-users/...`, hierarchical-naming REQ-6.1 nests it under `merchants/users/**`.)*

## REQ-3: Authorization preserved, audience out of the path

**User Story:** As the security owner, I want the URL change to carry zero authorization-behavior change and keep control/data plane separation, so that no endpoint becomes more or less accessible.

**Acceptance Criteria (EARS):**
- 3.1 THE SYSTEM SHALL keep each endpoint's authorization policy identical to its pre-migration policy (`tenant`, `admin`, `producer`, HMAC-webhook, or AllowAnonymous), changing only the path.
- 3.2 THE SYSTEM SHALL NOT encode audience in the path; audience remains enforced per endpoint via `RequireAuthorization`.
- 3.3 IF a request to an `admins`-area endpoint presents a tenant or producer session rather than an admin session, THEN THE SYSTEM SHALL reject it exactly as it did before the migration (plane separation preserved).
- 3.4 WHEN an endpoint was anonymous before migration (for example `/orders/{token}/summary`, the webhook callback, the admin/producer `auth/login`, and `producer/register`), THE SYSTEM SHALL keep it anonymous at its new path.
- 3.5 THE SYSTEM SHALL preserve the `producer` (write) vs `tenant` (read) authorization split within the `products` area unchanged by the path move.
- 3.6 THE SYSTEM SHALL keep the PSP webhook endpoint's current framework-level `AllowAnonymous` plus whatever handler-level signature verification exists today, unchanged; adding or completing HMAC verification is OUT OF SCOPE for this migration (tracked as a separate open item).
- 3.7 THE SYSTEM SHALL preserve each endpoint's EXACT current filter membership; the area-prefix change alone SHALL NOT move any endpoint between filter tiers. Specifically: `producer/auth/login` and `producer/register` remain OUTSIDE `ProducerCsrfFilter`/`ProducerBoundProducerFilter` (unfiltered, exactly as mapped top-level today); and every admin endpoint — including `GET /admins/auth/login` — remains on the `AdminCsrfFilter`'d group exactly as today (the CSRF filter is a no-op on the safe GET method), so admin has no separate unfiltered tier.

## REQ-4: Infrastructure endpoints excluded from the scheme

**User Story:** As an operator, I want health/readiness probes and the API-reference tooling to stay at stable un-versioned paths, so that monitoring and doc tooling are neither versioned nor broken.

**Acceptance Criteria (EARS):**
- 4.1 THE SYSTEM SHALL keep the health and readiness endpoints (`/health/live`, `/health/ready`) at their current stable paths, NOT under `/api/v1`.
- 4.2 THE SYSTEM SHALL keep the `/scalar` API-reference UI at its current path, NOT under `/api/v1`.
- 4.3 THE SYSTEM SHALL exclude the health, readiness, `/scalar`, and OpenAPI-document paths from the area-taxonomy architecture check in REQ-1.5.
- 4.4 THE SYSTEM SHALL keep the OpenAPI document endpoint (`/openapi/v1.json`) at its current path, NOT under `/api/v1`, as part of the infrastructure-exclusion set.

## REQ-5: Big-bang cutover, legacy paths removed

**User Story:** As a maintainer, I want all endpoints moved atomically with the legacy paths deleted, so that there is one canonical URL per endpoint and no lingering dual surface.

**Acceptance Criteria (EARS):**
- 5.1 THE SYSTEM SHALL move all endpoints to the new scheme in a single release (no phased subset).
- 5.2 THE SYSTEM SHALL remove every legacy flat path; a legacy path SHALL NOT be aliased, redirected, or dual-served.
- 5.3 IF a client requests any legacy path (every old method+path from the mapping table, plus the two old OIDC callback paths) after cutover, THEN THE SYSTEM SHALL respond 404 (route not found).
- 5.4 THE SYSTEM SHALL NOT change the response BODY, status code, HTTP method, or query contract of a migrated endpoint. Path-bearing response data — `Location` headers, redirect targets, and self-referential paths — SHALL be updated to the new scheme (this is required to preserve correct behavior; see REQ-9).

## REQ-6: BFF auth endpoints versioned uniformly

**User Story:** As an SSO integrator, I want the admin/producer login-callback-logout endpoints under the same `/api/v1` scheme, so that the URL scheme has no exceptions beyond infrastructure.

**Acceptance Criteria (EARS):**
- 6.1 THE SYSTEM SHALL place admin BFF auth endpoints under `/api/v1/admins/auth/...` and producer BFF auth endpoints under `/api/v1/producers/auth/...`.
- 6.2 THE SYSTEM SHALL move the OIDC callback via configuration (`AdminAuthOptions.CallbackPath` / `ProducerOidcOptions.CallbackPath` + `appsettings*.json`), NOT via endpoint routing, because the callback is handled by the OIDC middleware and is not a mapped endpoint; and THE SYSTEM SHALL require the Google OAuth dev `redirect_uri` updated to the new callback path before cutover.
- 6.3 THE SYSTEM SHALL keep the `__Host-adm_session` / `__Host-prd_session` cookie names, CSRF double-submit, session rotation, reuse-detection, and instant-revoke semantics unchanged by the path move.

## REQ-7: Generated API documentation and security metadata

**User Story:** As an API consumer, I want the OpenAPI document and Scalar UI to reflect the new paths automatically with correct per-operation security, so that the reference is never stale.

**Acceptance Criteria (EARS):**
- 7.1 WHEN routes are migrated, THE SYSTEM SHALL regenerate the OpenAPI document from the routes with the new paths, without manual route-path edits; any hardcoded path STRINGS in OpenAPI descriptions/annotations (for example the security-scheme descriptions naming `/admin/auth/login`) SHALL be updated manually to the new paths.
- 7.2 THE SYSTEM SHALL preserve per-operation security requirements in the OpenAPI document, still derived from each endpoint's authorization policy.
- 7.3 THE SYSTEM SHALL update every route-asserting test (Architecture.Tests and endpoint/integration tests) to the new paths so the full suite passes.

## REQ-8: Consumer and external-registration coordination (Definition of Done)

**User Story:** As the release owner, I want the out-of-repo consumers and third-party registrations updated in lockstep with the big-bang cutover, so that dev flows keep working after the move.

**Acceptance Criteria (EARS):**
- 8.1 THE SYSTEM SHALL treat the cutover as complete only when the admin SPA and producer SPA route calls are updated to the new paths (verified by checklist, not by an automated backend test).
- 8.2 WHEN cutover ships, THE SYSTEM SHALL require the PSP dev webhook callback URL (2C2P / Omise) re-registered to `/api/v1/webhooks/{pspConnectionId}`.
- 8.3 WHEN cutover ships, THE SYSTEM SHALL require the Google OAuth dev `redirect_uri` re-registered for the admin and producer OIDC callbacks.

## REQ-9: Path-coupled configuration and responses

**User Story:** As a maintainer, I want every place the old path is embedded in configuration or responses updated together with the routes, so that the migration preserves runtime behavior rather than silently breaking CORS, SSO, or Location headers.

**Acceptance Criteria (EARS):**
- 9.1 THE SYSTEM SHALL update the CORS policy selector (`PolCorsPolicyProvider`, which selects the credentialed admin/producer policy by `Request.Path.StartsWithSegments("/admin"|"/producer")`) to match the new `/api/v1/admins` and `/api/v1/producers` prefixes, so the admin/producer BFF cookie XHR keeps its credentialed policy.
- 9.2 THE SYSTEM SHALL update every hardcoded `Location` header value in `Results.Created(...)` that names a legacy `/admin/...` or `/producer/...` path to the new scheme.
- 9.3 WHEN a CORS preflight (OPTIONS) is sent to an admin or producer path, THE SYSTEM SHALL answer it with the credentialed policy before any auth challenge, at the new paths.
- 9.4 THE SYSTEM SHALL cover the path-coupled changes that `EndpointDataSource` enumeration cannot see (OIDC middleware callback, CORS policy behavior, `Location` headers) with targeted integration tests.

## Edge Cases & Open Questions

Standing design constraints (not open):

- **Future per-area versioning.** An area that later breaks compatibility MAY be special-cased as `/api/v2/{area}` at that time via a dedicated spec; NO per-area versioning machinery is built now — global `v1` only. The REQ-1.5 arch guard is **fail-closed on the literal `/api/v1`**: introducing a `v2` is a deliberate act that updates the guard (a silent `/api/v2/...` must fail the test).
- **Areas vs module folders.** `webhooks` and `reports` are API areas but NOT `src/Modules/` folders — the area list is an API-surface taxonomy, broader than the module set. Area names are lowercase plural nouns (REQ-2.1).
- **Big-bang vs separate SPA repos.** With no dual-route (REQ-5.2), the admin/producer SPAs break until their route calls are updated (REQ-8.1). Coordinated dev cutover (backend + both SPA repos + Google `redirect_uri` re-registration) is required. Acceptable pre-prod; a release-process concern.
- **Health/readiness paths (REQ-4).** `/health/live` + `/health/ready` via `MapPolHealthChecks` in `BuildingBlocks.Web`; sit outside any group that would receive the `/api/v1` prefix.
- **Frontend paths out of scope.** `ErrorPath` (`/login-error`), `RegisterUrl`, `DefaultReturnPath` are OIDC redirect targets on the SPA (`localhost:5200/...`), not API endpoints — not migrated here.

### Analyze findings log — anchor 762cfb5 (2026-07-05, requirements.md untracked; anchor = repo HEAD at analyze time)

Full audit (no prior anchor). Findings F1-F5 + O1-O3 resolved by user 2026-07-05 (see prior revision). Summary of the substantive ones: F1 (global default + version-agnostic guard), F2 (uniform plural, user override), F3 (OpenAPI doc infra-exclude), F4 (Hosts/Api scope), F5 (webhook HMAC out-of-scope), O1 (payments/sessions), O2 (admins area root, dissolved), O3 (reports=tenant).

### Codex adversarial-review amendments (grill-me-codex, rounds 1-2, 2026-07-05)

- **F1 reversed:** REQ-1.5 guard changed from version-agnostic `v{n}` to **literal `/api/v1` (fail-closed)** — Codex H4: a silent `/api/v2/...` must not pass an un-specified. Future v2 updates the guard.
- **"path-only" thesis dropped (Codex H2/H3/M1/M2):** the path is coupled to CORS policy selection, OIDC `CallbackPath` config, `Location` headers, and OpenAPI description strings — all must change to KEEP behavior. Added **REQ-9** (path-coupled config/responses), **REQ-3.7** (filter-membership preservation), **REQ-6.2** (callback via config, not routing), and the **REQ-5.4** carve-out (path-bearing headers update; body/status/method/query unchanged). REQ-5.3 strengthened to every old method+path + the two callback paths.
- **Full mapping table** now lives in `design.md` (Data Models) — REQ-2.2 cites it.
