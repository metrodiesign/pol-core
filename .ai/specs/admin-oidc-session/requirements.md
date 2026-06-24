# Requirements: Admin OIDC Server-Side Session (BFF)

> Status: approved 2026-06-23

> **Decisions locked (this spec's `/spec-new`, 2026-06-23):** adopt the **BFF (Backend-for-Frontend)** pattern
> per the IETF *OAuth 2.0 for Browser-Based Apps* BCP. Admin SSO moves from the current client-side
> id-token-as-bearer model to a server-side OIDC Authorization Code flow: the API is a confidential OIDC client,
> the SPA holds NO tokens, and an opaque httpOnly session cookie + a server-side session record (rotation +
> reuse detection) is the credential. The 7 design choices answered up front:
> (1) admin-only — tenant SPA untouched; (2) new confidential "Web application" Google client, secret in Vault,
> PKCE+state+nonce; (3) httpOnly+Secure+SameSite cookie, SPA holds no token, **same-site deploy assumed**
> (cross-site fallback `SameSite=None`+CSRF); (4) opaque server session -> instant revoke (not stateless JWT);
> (5) RBAC resolved per request server-side -> suspend/assignment changes take effect immediately;
> (6) rotate-on-use + reuse->revoke-family, logout local + revoke-all, Google RP-logout out of scope;
> (7) MFA delegated to Workspace (best-effort `amr`/`acr`, unchanged from admin-actor-rename REQ-9).

## Overview

ยกระดับ Admin Console SSO (product canon: "Admin Console — internal-only" + "Identity & RBAC — Google SSO,
hd-gate default-deny") จาก **client-side Google id-token-as-bearer** (ของจริงหลัง admin-actor-rename / PR #18)
เป็น **server-side OIDC Authorization Code flow แบบ BFF** บน pol-core (modular monolith, ASP.NET minimal API,
EF Core 10 + SQL Server 2025, pol_admin control plane, Vault custody). เป้าหมาย: ได้ server-side session control
ที่ของเดิมไม่มี — revocable ทันที, refresh rotation + theft detection, short-lived session, และ SPA ไม่ถือ token
(กัน XSS exfil). งานนี้ **admin-only**: tenant SPA (audience `tenant`) คงเดิมไม่แตะ. resolution/RBAC ของ admin
(bind/self-provision/suspend/accessible-tenant) **reuse** ของ admin-actor-rename — ย้ายจังหวะ resolve จาก
per-request middleware ไปที่ callback + per-request status re-check เท่านั้น. **Out of scope:** tenant-side auth
change, Google API access / offline refresh token, Google RP-initiated (federated) logout, step-up MFA.

## REQ-1: Login initiation (Authorization Code + PKCE)
**User Story:** As an admin, I want clicking "Sign in" to start a backend-handled Google login, so that my
browser never touches an OAuth token or the client secret.
**Reuses:** hosted-domain / verified-email gate concept from the current `GoogleAuthenticationExtensions`.
**Acceptance Criteria (EARS):**
- 1.1 WHEN the browser requests `GET /admin/auth/login` THE SYSTEM SHALL generate a unique `state`, `nonce`, and PKCE `code_verifier` (each >= 128 bits of entropy) and redirect (302) to Google's authorization endpoint with `response_type=code`, the configured confidential `client_id`, the registered `redirect_uri`, `scope=openid email`, an S256 `code_challenge`, `state`, and `nonce`.
- 1.2 THE SYSTEM SHALL persist `state`/`nonce`/`code_verifier` server-side bound to the pre-auth attempt (a short-lived signed+encrypted cookie or server store), single-use, expiring within a configurable TTL (default 10 minutes).
- 1.3 WHERE the request supplies a post-login return target THE SYSTEM SHALL honor it only if it matches a configured allowlist of admin SPA return paths, else use the default admin landing path (open-redirect prevention).
- 1.4 THE SYSTEM SHALL treat `GET /admin/auth/login` and `GET /admin/auth/callback` as top-level browser navigations (not CORS/XHR endpoints) and SHALL NOT require an existing session for them.
- 1.5 THE SYSTEM SHALL request only `openid email` scope and SHALL NOT request offline access or a Google refresh token (no Google API access is performed by this system).
- 1.6 THE SYSTEM SHALL rate-limit the admin auth endpoints (`GET /admin/auth/login`, `GET /admin/auth/callback`) per source IP, reusing the host's rate-limiting middleware. (F10)
- 1.7 WHEN a login is initiated while a valid session already exists THE SYSTEM SHALL start a NEW session (multiple concurrent sessions per admin are allowed — `logout-all`, REQ-6.2, clears them), and the pre-auth state store SHALL support concurrent login attempts (e.g. multiple tabs) without one clobbering another. (F11)

## REQ-2: Callback — code exchange, id_token verification, admin resolution
**User Story:** As the platform, I want the callback to exchange the code and verify the id_token server-side,
so that only a cryptographically verified Google identity can establish a session.
**Reuses:** admin-actor-rename REQ-3.4/3.5 (invite-bind), REQ-5.1 (allowlist self-provision), REQ-5.3/5.6 (deny).
**Acceptance Criteria (EARS):**
- 2.1 WHEN Google redirects to `GET /admin/auth/callback` THE SYSTEM SHALL require the returned `state` to equal the stored single-use value and SHALL reject (400) a missing, unknown, expired, or already-consumed `state`.
- 2.2 WHEN `state` is valid THE SYSTEM SHALL exchange the `code` at Google's token endpoint using the confidential `client_secret` and the stored PKCE `code_verifier`.
- 2.3 THE SYSTEM SHALL verify the returned `id_token`: RS256 signature against Google's JWKS, `iss` in `{accounts.google.com, https://accounts.google.com}`, `aud` == configured client id, unexpired, `nonce` == the stored nonce, `email_verified` == `true`, and (WHERE `HostedDomain` is configured) `hd` == the configured domain.
- 2.4 THE SYSTEM SHALL derive the caller's identity (`sub`, `email`) ONLY from the verified `id_token` claims and SHALL NOT read identity from any client-supplied request parameter.
- 2.5 WHEN the id_token is verified THE SYSTEM SHALL resolve the admin by `sub` using the existing rules — first-login bind of an invited Scoped account by verified email, else allowlist self-provision of a Super — relocated from the per-request resolution middleware to callback time.
- 2.6 IF the resolved admin is unknown/not-allowlisted/uninvited, or has `Status = Suspended` THEN THE SYSTEM SHALL deny (403) and establish no session.
- 2.7 IF code exchange fails OR any id_token check in 2.3 fails THEN THE SYSTEM SHALL establish no session and return an authentication error (no partial session is created).
- 2.8 IF the callback is invoked with an OAuth `error` parameter (e.g. `access_denied`, or a Google-side `hd` rejection) instead of a `code` THEN THE SYSTEM SHALL establish no session, redirect (302) to the admin error page with a non-sensitive reason, and record a denied-auth audit (REQ-12.4). (F1)

## REQ-3: Server-side session + cookie + lifetime
**User Story:** As an admin, I want my session represented by an httpOnly cookie backed by server state, so that
my browser holds no readable token and the server fully controls the session.
**Reuses:** Vault/HKDF primitives available for signing/hashing; control-plane placement (admin-actor-rename REQ-3.2).
**Acceptance Criteria (EARS):**
- 3.1 WHEN admin resolution succeeds at callback THE SYSTEM SHALL create a server-side session record holding an opaque-id **hash**, the `AdminAccount` id, a `FamilyId`, issued-at, idle-expiry, absolute-expiry, and `Status = Active` (the raw session id is never persisted).
- 3.2 THE SYSTEM SHALL set the session cookie with `HttpOnly`, `Secure`, `Path=/`, the `__Host-` prefix, and `SameSite=Lax` for a same-site deploy (`SameSite=None; Secure` when cross-site, per REQ-7.4); the cookie value SHALL be an opaque random token with no identity or claims encoded in it. (F6)
- 3.3 WHERE the environment is Development over plain http THE SYSTEM SHALL be permitted to omit `Secure`/`__Host-` for localhost only, and SHALL NOT relax them outside Development.
- 3.4 THE SYSTEM SHALL expire a session at a configurable idle timeout (default 30 minutes) OR a configurable absolute lifetime (default 8 hours), whichever comes first.
- 3.5 WHEN an authenticated request is served THE SYSTEM SHALL slide the idle expiry forward, bounded by the absolute lifetime.
- 3.6 THE SYSTEM SHALL make session validity independent of the Google id_token lifetime (the id_token is consumed once at callback and not stored).

## REQ-4: Authenticated admin request via session (id-token bearer retired)
**User Story:** As an admin SPA, I want to call admin APIs with my session cookie, so that I no longer attach a
Google token to requests.
**Reuses:** `GET /admin/me` shape (admin-actor-rename REQ-13).
**Acceptance Criteria (EARS):**
- 4.1 WHEN an admin route (`/admin/*`) receives a request THE SYSTEM SHALL authenticate it via the session-cookie scheme: look up the session by the cookie's hashed id and require `Status = Active` and unexpired.
- 4.2 IF no session cookie is present, or the session is unknown/expired/revoked THEN THE SYSTEM SHALL respond 401 and SHALL NOT fall through to any other credential.
- 4.3 THE SYSTEM SHALL serve `GET /admin/me` from the session-resolved admin, returning the unchanged shape `{ adminId, email, tier, accessibleTenants }`.
- 4.4 THE SYSTEM SHALL NOT accept a Google id_token as a Bearer credential on admin routes (the `admin`-audience id-token-as-bearer path is retired).
- 4.5 THE SYSTEM SHALL accept credentialed (cookie) XHR only from the allowlisted admin SPA origin, served by a dedicated admin CORS policy with `AllowCredentials` (REQ-10.5); the tenant CORS policy stays credential-less and unchanged. (F3)

## REQ-5: Session rotation + reuse / theft detection
**User Story:** As a security reviewer, I want rotating session ids with reuse detection, so that a stolen cookie
is detected and the whole session family is killed.
**Reuses:** OAuth 2.0 Security BCP refresh-token-rotation pattern, expressed as cookie-session rotation.
**Acceptance Criteria (EARS):**
- 5.1 WHEN a session id reaches a configurable rotation age (default 15 minutes) THE SYSTEM SHALL transparently issue a new opaque session id within the same `FamilyId` — via `Set-Cookie` on the in-flight admin request, with NO dedicated refresh endpoint — and mark the presented id `Superseded`. (F4)
- 5.2 THE SYSTEM SHALL accept a `Superseded` id within a bounded grace window (default 60 seconds) so legitimate in-flight concurrent requests are not misclassified as theft.
- 5.3 IF a `Superseded` session id is presented after its grace window (reuse) THEN THE SYSTEM SHALL revoke the entire session family and respond 401 (force re-login).
- 5.4 IF a `Revoked` session id is presented THEN THE SYSTEM SHALL respond 401 and take no further action.
- 5.5 THE SYSTEM SHALL serialize rotation per session so that concurrent requests past the rotation age produce exactly one new session id (one rotation wins; other in-flight requests are served and accept the result within the grace window, REQ-5.2). (F5)

## REQ-6: Revocation — logout, logout-all, suspend propagation
**User Story:** As a Super admin, I want to end sessions immediately, so that access is cut without waiting for
any token to expire.
**Reuses:** suspend authority (admin-actor-rename REQ-8.2).
**Acceptance Criteria (EARS):**
- 6.1 WHEN `POST /admin/auth/logout` is called THE SYSTEM SHALL revoke the current session, clear the cookie, and respond 204.
- 6.2 WHEN `POST /admin/auth/logout-all` is called THE SYSTEM SHALL revoke every `Active`/`Superseded` session in that admin's accounts (all devices).
- 6.3 WHEN an admin is suspended THE SYSTEM SHALL cause that admin's next request to be denied without waiting for session expiry (enforced by the per-request status re-check, REQ-9.2).
- 6.4 WHEN a session is revoked THE SYSTEM SHALL deny the next request that presents it; no access persists beyond already in-flight requests.

## REQ-7: CSRF protection
**User Story:** As a security reviewer, I want CSRF protection on cookie-authenticated mutations, so that a
cross-site page cannot drive admin actions with the admin's cookie.
**Acceptance Criteria (EARS):**
- 7.1 THE SYSTEM SHALL issue a CSRF token to the SPA (a JS-readable cookie or a value in the bootstrap response) paired with the session.
- 7.2 IF an unsafe-method request (POST/PUT/DELETE) to an admin route lacks a request-header CSRF token matching the paired value THEN THE SYSTEM SHALL respond 403 (double-submit verification).
- 7.3 THE SYSTEM SHALL set `SameSite=Lax` on the session cookie so a cross-site context cannot carry it to a state-changing endpoint (defense in depth with 7.2).
- 7.4 WHERE the admin SPA and API are deployed cross-site THE SYSTEM SHALL set `SameSite=None; Secure` on the session cookie AND continue to enforce the CSRF token (7.2).

## REQ-8: Confidential client secret custody
**User Story:** As the operator, I want the Google client secret handled as a secret, so that it is never
exposed or committed.
**Reuses:** existing fail-fast-on-unset-config pattern (Program.cs guards) + Vault custody.
**Acceptance Criteria (EARS):**
- 8.1 THE SYSTEM SHALL read the Google `client_secret` only from injected configuration (environment variable / user-secrets / Vault), never from a committed file.
- 8.2 IF the `client_secret` or confidential-client config is unset/placeholder outside Development THEN THE SYSTEM SHALL fail fast at boot (mirroring the existing audience/connection-string guards).
- 8.3 THE SYSTEM SHALL NOT log the `client_secret`, the authorization `code`, the `id_token`, or any raw session id / CSRF token.

## REQ-9: Per-request RBAC resolution (fresh)
**User Story:** As the authorization layer, I want tier and accessible tenants resolved fresh per request, so
that suspension and assignment changes take effect without re-login.
**Reuses:** admin-actor-rename REQ-6 (accessible-tenant resolution) + REQ-7 (scoped floor) + `IAdminScope`.
**Acceptance Criteria (EARS):**
- 9.1 WHEN an authenticated admin request is served THE SYSTEM SHALL resolve the admin's current `Status`, `Tier`, and accessible-tenant set from the control-plane store for that request (not from claims baked into the cookie) and materialize them into `IAdminScope` + an `admin_tier` claim.
- 9.2 IF the resolved admin's `Status` is `Suspended` THEN THE SYSTEM SHALL deny (401/403) regardless of an otherwise-valid session.
- 9.3 THE SYSTEM SHALL reflect an assignment change (assign/unassign tenant) in the accessible set on the admin's next request, with no stale window beyond one request.
- 9.4 THE SYSTEM SHALL perform per-request resolution as a READ-ONLY operation — admin binding and self-provision happen ONLY at callback (REQ-2.5), never in the per-request path. (F2)

## REQ-10: Scope boundary (admin-only; tenant unchanged)
**User Story:** As the maintainer, I want this change limited to the admin surface, so that the tenant SPA and
its audience plumbing are untouched.
**Reuses:** admin-actor-rename REQ-2.3 (retain `tenant` audience), REQ-2.4 (no tenant-SPA change).
**Acceptance Criteria (EARS):**
- 10.1 THE SYSTEM SHALL apply BFF session authentication only to admin routes (`/admin/*`); tenant routes SHALL keep the existing Google id-token-as-bearer `tenant`-audience path unchanged.
- 10.2 THE SYSTEM SHALL retire the `admin` entry from `Google:Audiences` (the id-token-as-bearer admin path) WITHOUT altering the retained `tenant` audience.
- 10.3 THE SYSTEM SHALL require no change to the tenant SPA (audience key and OAuth client id unchanged).
- 10.4 THE SYSTEM SHALL register two authentication schemes — session-cookie (admin) and Google JwtBearer (tenant) — with admin routes gating on the cookie scheme and tenant routes on the bearer scheme.
- 10.5 THE SYSTEM SHALL replace the current single shared CORS policy with two policies — an admin-origin policy with `AllowCredentials` (for cookie XHR) and the existing tenant-origin policy WITHOUT credentials — so enabling admin cookies does not change the tenant CORS posture. (F3)
- 10.6 THE SYSTEM SHALL retain the existing authorization policy named `admin` but re-point it to the session-cookie scheme, so existing `.RequireAuthorization("admin")` call sites need no rename. (F7)

## REQ-11: Session store as control-plane table + migration safety
**User Story:** As the operator, I want sessions persisted as a control-plane table migrated safely, so that
admin sessions live in pol_admin with no RLS/data-plane exposure.
**Reuses:** admin-actor-rename REQ-3.2 (control-plane grant), REQ-11 (migration safety on the RLS schema).
**Acceptance Criteria (EARS):**
- 11.1 THE SYSTEM SHALL store sessions in a new control-plane table (e.g. `AdminSessions`) granted to `pol_admin` only, with NO per-tenant RLS predicate and NO grant to `pol_app`.
- 11.2 THE SYSTEM SHALL persist only the session-id **hash**, the `FamilyId`, `Status`, and the timestamps — never the raw session id and never any Google token.
- 11.3 THE SYSTEM SHALL provide a forward migration that adds the table + indexes and a reversible `Down` that drops it, leaving existing RLS policy/predicates unchanged.
- 11.4 THE SYSTEM SHALL index the store for O(1) lookup by hashed session id and for family-wide revoke by `FamilyId`.
- 11.5 THE SYSTEM SHALL prune sessions past their absolute expiry, or revoked beyond a retention window, via a background sweep so the store does not grow unbounded (audit rows are separate and retained, REQ-12). (F8)

## REQ-12: Auth event audit (append-only)
**User Story:** As a compliance auditor, I want auth lifecycle events recorded immutably, so that login, logout,
revocation, and theft detection are traceable.
**Reuses:** admin-actor-rename REQ-10.2 (append-only audit discipline).
**Acceptance Criteria (EARS):**
- 12.1 WHEN a login succeeds, a session rotates, logout/logout-all runs, or a family is revoked on reuse detection THE SYSTEM SHALL append an audit row with the `AdminAccount` id, an event type, and a correlation id.
- 12.2 THE SYSTEM SHALL treat the auth audit as append-only (insert only; no update/delete).
- 12.3 THE SYSTEM SHALL NOT record secrets, tokens, or raw session ids in the audit (only ids, hashes, and event metadata).
- 12.4 WHEN an auth attempt is denied or fails — `state` mismatch, id_token verification failure, OAuth `error` callback (REQ-2.8), suspended/uninvited denial, or reuse-detected family revoke — THE SYSTEM SHALL append an audit row with the failure reason and a correlation id, containing no secrets or tokens. (F9)

## REQ-13: Canon + frontend contract reconciliation
**User Story:** As a future maintainer and the FE team, I want the docs to match the BFF reality, so that no one
integrates against the retired bearer model.
**Reuses:** canon-reconciliation discipline (admin-actor-rename REQ-12).
**Acceptance Criteria (EARS):**
- 13.1 THE SYSTEM SHALL rewrite `docs/reference/admin-google-sso.md` to describe the BFF flow (login redirect, callback, session cookie, CSRF, logout/logout-all) and remove the GIS / id-token-as-bearer instructions.
- 13.2 THE SYSTEM SHALL add the new session table to `docs/reference/entity-fields.md`.
- 13.3 THE SYSTEM SHALL correct any admin-auth description in `CODING_STANDARDS.md`/`ARCHITECTURE.md` that asserts id-token-as-bearer for admin.

## Edge Cases & Open Questions

- **Deploy topology (Q3, drives cookie attributes)** — defaults assume admin SPA + API **same-site**
  (`SameSite=Lax`, minimal CSRF surface). If prod forces separate registrable domains -> `SameSite=None; Secure`
  + CSRF enforced (REQ-7.4). CONFIRM prod topology before design freezes the cookie attributes.
- **Rotation race** — per-request rotation is rejected in favor of age-based rotation + a grace window
  (REQ-5.1/5.2) so parallel XHR near the rotation boundary is not flagged as theft. Default grace 60s; tune in design.
- **Per-request RBAC DB read (REQ-9.1)** — acceptable: admin traffic is low and the session lookup already hits
  the DB; add a short-TTL cache only if measured hot (do not premature-optimize).
- **Suspend vs logout-all** — suspension relies on the per-request status re-check (REQ-6.3/9.2), not an eager
  session sweep; a Super MAY also call logout-all. Confirm this is sufficient (no eager sweep required).
- **CSRF cookie naming** — the CSRF token cookie must be JS-readable, so it cannot use `__Host-` with HttpOnly;
  design picks a concrete double-submit shape.
- **Google federated logout** — `end_session`/RP-initiated logout is OUT of scope; our logout ends only our
  session (Google SSO may silently re-auth on next login). Accepted.
- **amr/acr reality** — unchanged from admin-actor-rename REQ-9: likely a no-op if Google omits the claim;
  Workspace policy is the enforcing control.

### Findings log (spec-analyze) — anchor: `9f15034` (requirements.md uncommitted at analyze time)

All findings resolved with the recommended option (decision A) and applied as new/edited criteria, REQ IDs stable.

| F | category | REQ | decision | note |
|---|---|---|---|---|
| F1 | gap | 2.8 | **A** | OAuth `error` callback -> no session + 302 error page + denied-auth audit |
| F2 | gap/interaction | 9.4 | **A** | per-request resolution is READ-ONLY; bind/self-provision only at callback (REQ-2.5) |
| F3 | conflict | 4.5 / 10.5 | **A** | split CORS: dedicated admin policy `AllowCredentials`, tenant policy stays credential-less |
| F4 | ambiguity | 5.1 | **A** | transparent age-based rotation via `Set-Cookie`, NO refresh endpoint; "explicit refresh" removed |
| F5 | gap/concurrency | 5.5 | **A** | rotation serialized per session — one rotation wins, others accept within grace window |
| F6 | inconsistency | 3.2 | **A** | `SameSite=Lax` same-site default; `None;Secure` cross-site — reconciled with REQ-7.4 |
| F7 | ambiguity | 10.6 | **A** | retain policy name `admin`, re-point to cookie scheme (no call-site rename) |
| F8 | gap | 11.5 | **A** | prune expired/revoked sessions via background sweep (store not unbounded) |
| F9 | gap | 12.4 | **A** | audit denied/failed auth attempts (state mismatch, id_token fail, OAuth error, suspended/uninvited, reuse) |
| F10 | gap | 1.6 | **A** | rate-limit `/admin/auth/login` + `/callback` per source IP (reuse host limiter) |
| F11 | gap/interaction | 1.7 | **A** | login-while-authed -> new session (multi-session allowed); pre-auth state supports concurrent attempts |
