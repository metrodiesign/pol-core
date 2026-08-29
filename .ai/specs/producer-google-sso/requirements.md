# Requirements: Producer Google SSO + Role RBAC

> Status: unknown

> **Amended 2026-07-01 (registration ticket made stateless):** the server-side `RegistrationTickets` row + its
> single-use conditional-UPDATE consume are REMOVED. The registration/correction wire ticket is now a stateless
> signed+time-limited Data Protection token (no DB row). Duplicate-registration / replay safety is the pre-existing
> UNIQUE index on `ProducerAccount.Subject` (REQ-1.4) + `ProducerAccount.Resubmit()`'s Rejected-only guard, both
> enforced at submit time — REQ-9.6 ("no self-provision at the callback") is UNCHANGED (the account is still created
> only at submission). REQ-3.2–3.6 and REQ-4.6 below are rewritten accordingly; the `registration-pending` reason
> code and the `HasPendingAsync` dedup guard are gone. Also: `DisplayName` is no longer a form field — it is
> server-computed from the now-required `FirstName`+`LastName`.

> **Amended 2026-07-01 (person details moved onto the account, `TenantUserProfile` deleted):** the registration
> form's person fields (`FirstName`, `LastName`, `DisplayName`, `PersonType`, `IdNumber`, `ProducerCode`,
> `LicenseNumber`, `Phone`, photo `PhotoObjectKey`/`PhotoContentType`) now persist DIRECTLY on `ProducerAccount`
> (REQ-7.1). The one-to-one `TenantUserProfile` entity/table is DELETED (migration
> `AddProducerAccountDetailsDropProfile`): a "tenant" is the company/app on a company machine, not a person, so
> person data belongs to the person's own account, never a tenant-scoped profile. `ProducerAccount.SetDetails(...)` /
> `SetPhoto(...)` (lifted from the old profile) apply the form; the duplicate-registration guarantee is now the
> single UNIQUE `Subject` index on `ProducerAccount`.

> **Decisions locked** (from the approved plan `/Users/king_developer/.claude/plans/producer-google-sso-parsed-frost.md`
> + 4 clarifying answers): (D1) Producer login = **OIDC BFF server-side session** mirroring `admin-oidc-session`
> (cookie + rotation + reuse-detection + revoke + CSRF) — NOT the tenant id-token-bearer model. (D2) **Full
> permission catalog** mirroring `admin-role-rbac` (catalog/role/assignment + `RequirePermission` fail-closed +
> boot parity guard) — replaces the fixed `TenantAdmin/Finance/Viewer` enum of the removed `identity-rbac`. (D3)
> **Backend-only** (pol-core); `pol-admin` FE is a follow-up slice. (D4) Profile includes **photo upload** stored
> securely. (D5) The Admin auth/RBAC stacks are **DUPLICATED** (copy-rename into a Producer module), Admin code
> untouched — `Architecture.Tests` forbid a Producer→Admin dependency (control/data-plane separation by design).

## Overview

นี่คือการ **rebuild ของ Identity module ที่ถูกลบ** (PR #18, "remove the Identity module pending a Producer
rebuild") ในรูปแบบที่สมบูรณ์กว่าเดิม. product canon (reference 2.5): "role/tenant ตัดสินที่ platform เสมอ —
Google ทำแค่ authentication". สเปกนี้สร้าง platform-side identity ฝั่ง **Producer** (tenant-facing user ที่
register แล้วรอ admin อนุมัติเพื่อทำงานแทน tenant): ตาราง `TenantUser` (schema `producer`) + `ExternalLogin`
(Google `sub` → user) + registration/correction ticket + register/approve/reject state machine + **full role→
permission RBAC** + **OIDC BFF server-side session** + runtime resolver ที่คืน ambient tenant binding + `tenant_role`
ที่หายไปตอนลบ Identity module. งานนี้ปิดรู `TODO(producer)` ใน `src/Hosts/Api/Program.cs` 3 จุด: tenant-user
resolver (`346-348`), 3 write-endpoint role gates (`418/562/583`, REQ-7.3 เดิม), และ registration/approve
endpoints (`784-786`, scoped-accessible REQ-8.5 เดิม).

**Realms:** *Producer* = ผู้ใช้ฝั่ง tenant (data plane, schema `producer`, RLS ด้วย `TenantId`). *Admin* = ops
(control plane) เป็นผู้ **อนุมัติ** producer — งานนี้ไม่แตะ Admin auth/RBAC ที่เพิ่ง ship เกินกว่าเพิ่ม permission
key `producer.approve`/`producer.reject` เข้า Admin catalog (REQ-18).

**Out of scope:** การแก้ tenant storefront Bearer audience (ลูกค้ายังใช้เดิม, REQ-23); FE (`pol-admin`) wiring;
Google API access / offline refresh token; Google RP-initiated logout; step-up MFA; maker-checker dual approval.

---

## REQ-1: TenantUser master record
**User Story:** As the platform, I want a TenantUser record per person who can act for a tenant, so that role and tenant are decided server-side, never by the token.
**Reuses:** `identity-rbac` REQ-1 (extended with the `Rejected` state).
**Acceptance Criteria (EARS):**
- 1.1 THE SYSTEM SHALL store a TenantUser with: external subject (Google `sub`, the stable id), Email, TenantId (FK to an existing `producer.Tenants` row), Status, CreatedAt. (F1) The user's role(s) are NOT a column on this record — they live in `ProducerRoleAssignments` (REQ-16.3).
- 1.2 THE SYSTEM SHALL constrain Status to one of `PendingApproval`, `Active`, `Rejected`, `Suspended`.
- 1.3 WHILE a TenantUser is `PendingApproval` or `Rejected` THE SYSTEM SHALL allow its TenantId to be unset (NULL) and hold no role assignment until an approval binds them.
- 1.4 THE SYSTEM SHALL enforce that an external subject maps to at most one TenantUser (unique on subject).
- 1.5 THE SYSTEM SHALL expose only the transitions `PendingApproval→Active` (approve), `PendingApproval→Rejected` (reject), `Rejected→PendingApproval` (resubmit), `Active→Suspended` (suspend), and SHALL reject any other transition.
- 1.6 IF a TenantUser is created or transitioned into a Status outside the allowed set THEN THE SYSTEM SHALL reject it with a validation error and make no change.

## REQ-2: External login mapping
**User Story:** As the platform, I want to map a Google identity to a TenantUser, so that returning users resolve to their record.
**Reuses:** `identity-rbac` REQ-2.
**Acceptance Criteria (EARS):**
- 2.1 THE SYSTEM SHALL store an ExternalLogin keyed by (Provider, Subject) that references exactly one TenantUser, with a unique (Provider, Subject) index.
- 2.2 THE SYSTEM SHALL set Provider to `google` for this slice.
- 2.3 WHEN a Google identity authenticates AND an ExternalLogin exists for its (provider, subject) THE SYSTEM SHALL resolve the linked TenantUser.
- 2.4 IF no ExternalLogin exists for an authenticated Google subject THEN THE SYSTEM SHALL treat the caller as an unregistered applicant (REQ-9.4) and establish no session.

## REQ-3: Registration & correction tickets
**User Story:** As an applicant, I want a short-lived handle after Google sign-in, so that I can submit (or correct) my registration form without yet holding a session.
**Reuses:** `identity-rbac` REQ-3 (extended with the `Correction` purpose).
**Acceptance Criteria (EARS):**
- 3.1 THE SYSTEM SHALL issue a ticket as a signed+encrypted self-contained token that the client carries and returns at submission, carrying the verified identity (subject, email, hosted-domain) captured ONLY from the validated Google id_token, plus a Purpose of `Registration` or `Correction`. (F7) *(amended 2026-07-01: no unique id / server row — stateless token.)*
- 3.2 THE SYSTEM SHALL make a ticket short-lived, expiring within a bounded, configurable TTL (default 10 minutes) enforced by the Data Protection time limit. *(amended 2026-07-01)*
- 3.3 THE SYSTEM SHALL be stateless at the callback — it persists no ticket row; replay/duplicate protection is enforced at submit time by the account's UNIQUE (Subject) index and the profile's one-to-one UNIQUE (ProducerAccountId) index. *(amended 2026-07-01, replaces the ticket-consume mark-used guard)*
- 3.4 THE SYSTEM SHALL treat a repeated callback for the same identity as harmless — it simply mints a fresh self-expiring token (no "pending" state, no stuck registration). *(amended 2026-07-01, replaces the server-row replay authority)*
- 3.5 THE SYSTEM SHALL NOT treat a ticket as an authenticated session — it grants only the ability to complete (or correct) a registration.
- 3.6 IF a ticket is tampered, expired, or unknown THEN THE SYSTEM SHALL reject the submission with a 400 and create/modify no TenantUser; a valid token for a subject that already has an account is rejected at submit by the UNIQUE (Subject) index (409). *(amended 2026-07-01)*

## REQ-4: Registration submission
**User Story:** As a new user, I want to register after signing in with Google, so that an admin can approve me onto a tenant.
**Reuses:** `identity-rbac` REQ-4.
**Acceptance Criteria (EARS):**
- 4.1 WHEN a valid `Registration` ticket is submitted with a registration form THE SYSTEM SHALL create a TenantUser (Status `PendingApproval`), an ExternalLogin, and a Profile, in ONE transaction on the control-plane (pol_admin) connection (REQ-19.2).
- 4.2 THE SYSTEM SHALL take the subject/email/hosted-domain from the ticket's verified identity, NEVER from the form body.
- 4.3 THE SYSTEM SHALL NOT set TenantId or role from the registration form (both are decided at approval — REQ-6).
- 4.4 WHEN the registration is persisted THE SYSTEM SHALL enqueue a `TenantUserRegistrationSubmitted` event in the SAME transaction (REQ-20).
- 4.5 THE SYSTEM SHALL treat the registration submission endpoint as anonymous and ticket-gated (no session required, REQ-13.4).
- 4.6 IF a TenantUser/ExternalLogin already exists for the subject THEN THE SYSTEM SHALL reject a second registration (REQ-1.4) with 409; concurrent duplicate submissions, AND replays of a still-valid stateless ticket, for the same subject SHALL be resolved by the UNIQUE (Subject)/(Provider,Subject)/(ProducerAccountId profile) constraints — exactly one commits, the others return 409. This is the sole duplicate-registration guard now that the ticket is stateless. (F9) *(amended 2026-07-01)*
- 4.7 THE SYSTEM SHALL require FirstName and LastName on the form and compute the Profile's DisplayName from them (`"{FirstName} {LastName}"`); DisplayName is NEVER supplied by the client. *(added 2026-07-01)*

## REQ-5: Rejection & correction resubmission
**User Story:** As a rejected applicant, I want to fix and resubmit my registration, so that a correctable mistake does not require starting over.
**Reuses:** new (the goal's reject→correct→resubmit loop).
**Acceptance Criteria (EARS):**
- 5.1 WHEN an admin rejects a `PendingApproval` TenantUser THE SYSTEM SHALL set Status `Rejected` and record the reason (REQ-21).
- 5.2 WHEN a `Rejected` user authenticates via Google THE SYSTEM SHALL issue a `Correction` ticket (REQ-3.1) and redirect to the registration page rather than establishing a session.
- 5.3 WHEN a valid `Correction` ticket is submitted with an updated form THE SYSTEM SHALL update the existing TenantUser's Profile and transition Status `Rejected→PendingApproval`, in ONE transaction, and re-enqueue `TenantUserRegistrationSubmitted` (REQ-20).
- 5.4 THE SYSTEM SHALL NOT create a second TenantUser/ExternalLogin on resubmission (it edits the existing record bound to the subject).
- 5.5 IF a `Correction` ticket targets a subject that is not currently `Rejected` THEN THE SYSTEM SHALL reject the submission and make no change.

## REQ-6: Approval flow (admin)
**User Story:** As a platform admin, I want to approve an applicant onto a specific tenant with a role, so that access is granted deliberately.
**Reuses:** `identity-rbac` REQ-5 + the scoped-accessible check (`admin-oidc-session` REQ-9 / removed REQ-8.5).
**Acceptance Criteria (EARS):**
- 6.1 THE SYSTEM SHALL restrict approval and rejection to an admin holding the `producer.approve` / `producer.reject` permission (REQ-18), rejecting an admin without it with 403 and an unauthenticated caller with 401.
- 6.2 WHEN an admin approves a `PendingApproval` (or resubmitted) TenantUser THE SYSTEM SHALL set its TenantId to a tenant the admin selected, assign one or more ProducerRoles via `ProducerRoleAssignments` (REQ-16.3), and set Status `Active`, in ONE transaction. (F1)
- 6.3 THE SYSTEM SHALL resolve TenantId ONLY from the admin's selection, SHALL validate that the tenant exists and is active, and SHALL require the selected tenant to be within the admin's accessible-tenant set (a Scoped admin cannot approve into a tenant it cannot reach).
- 6.4 IF the target TenantUser is already `Active` THEN THE SYSTEM SHALL treat the approval as an idempotent no-op success (no re-assignment, no duplicate audit, no duplicate event).
- 6.5 IF the selected tenant does not exist, is inactive, or is outside the admin's accessible set; OR the assigned role is unknown or Inactive; OR the target is not `PendingApproval` (e.g. `Rejected`/`Suspended` — the user must resubmit first) THEN THE SYSTEM SHALL reject the approval (409/422/403 as appropriate) and leave the TenantUser unchanged. (F4)
- 6.6 THE SYSTEM SHALL record an audit row for each approval and rejection (acting admin subject, target subject, tenant, role, correlation id) — both are sensitive actions (REQ-21).

## REQ-7: Producer profile + photo upload
**User Story:** As an applicant, I want to submit my personal details and a photo, so that the admin can review a complete profile.
**Reuses:** new (FE `/register` form fields; D4).
**Acceptance Criteria (EARS):**
- 7.1 THE SYSTEM SHALL persist a Profile with the registration form fields (display name + the producer detail fields) linked one-to-one to the TenantUser.
- 7.2 THE SYSTEM SHALL store an uploaded photo's bytes OUTSIDE the database and persist only an opaque object key and the stored content-type on the Profile.
- 7.3 WHEN a photo is uploaded THE SYSTEM SHALL accept only an allowlisted content-type (`image/jpeg`, `image/png`, `image/webp`; SVG excluded) verified by a magic-byte check of the actual bytes, not the declared header.
- 7.4 THE SYSTEM SHALL reject a photo exceeding a configurable size cap (default 2 MB) with 400/413 and store nothing.
- 7.5 THE SYSTEM SHALL generate the storage object key server-side (never from the client filename) so a crafted name cannot traverse the store, and SHALL serve the photo with `X-Content-Type-Options: nosniff` and the stored content-type.

## REQ-8: Login initiation (Authorization Code + PKCE)
**User Story:** As a producer, I want clicking "Sign in" to start a backend-handled Google login, so that my browser never touches an OAuth token or the client secret.
**Reuses:** `admin-oidc-session` REQ-1.
**Acceptance Criteria (EARS):**
- 8.1 WHEN the browser requests `GET /producer/auth/login` THE SYSTEM SHALL generate a unique `state`, `nonce`, and PKCE `code_verifier` (each ≥128 bits entropy) and redirect (302) to Google's authorization endpoint with `response_type=code`, the confidential Producer `client_id`, the registered `redirect_uri`, `scope=openid email`, an S256 `code_challenge`, `state`, and `nonce`.
- 8.2 THE SYSTEM SHALL persist `state`/`nonce`/`code_verifier` server-side (signed+encrypted, single-use, TTL default 10 min) under a Producer-specific Data Protection purpose isolated from the Admin OIDC client (REQ-14.4).
- 8.3 WHERE the request supplies a post-login return target THE SYSTEM SHALL honor it only if it matches a configured allowlist of producer return paths, else use the default landing path (open-redirect prevention).
- 8.4 THE SYSTEM SHALL treat `/producer/auth/login` and `/producer/auth/callback` as top-level browser navigations (not CORS/XHR), require no existing session for them, request only `openid email` scope (no offline access), and rate-limit them per source IP.

## REQ-9: Callback — verification, ExternalLogin lookup, state branch
**User Story:** As the platform, I want the callback to verify the id_token server-side and branch on the applicant's state, so that only a verified identity proceeds and each lifecycle state gets the right outcome.
**Reuses:** `admin-oidc-session` REQ-2 + `identity-rbac` REQ-4.1.
**Acceptance Criteria (EARS):**
- 9.1 WHEN Google redirects to `GET /producer/auth/callback` THE SYSTEM SHALL require the returned `state` to equal the stored single-use value and reject (400) a missing, unknown, expired, or already-consumed `state`.
- 9.2 WHEN `state` is valid THE SYSTEM SHALL exchange the `code` using the confidential `client_secret` + stored PKCE verifier, then verify the `id_token`: RS256 against Google JWKS, `iss ∈ {accounts.google.com, https://accounts.google.com}`, `aud` == Producer client id, unexpired, `nonce` == stored, `email_verified` == `true`, and (WHERE a hosted domain is configured) `hd` == configured.
- 9.3 THE SYSTEM SHALL derive identity (`sub`, `email`, `hd`) ONLY from the verified id_token, never from a request parameter.
- 9.4 WHEN the id_token is verified THE SYSTEM SHALL look up `ExternalLogin(google, sub)` and branch: **none** → issue a `Registration` ticket and redirect (302) to the configured registration page; **TenantUser Active** → establish a session (REQ-10) and redirect to the allowlisted post-login return target (REQ-8.3); **PendingApproval** → respond 403 "awaiting approval" and establish no session; **Rejected** → issue a `Correction` ticket and redirect to the registration page (REQ-5.2). (F8)
- 9.5 IF code exchange fails, any id_token check fails, OR the callback carries an OAuth `error` parameter THEN THE SYSTEM SHALL establish no session, redirect (302) to the error page with a non-sensitive reason, and append a denied-auth audit (REQ-21).
- 9.6 THE SYSTEM SHALL self-provision NO producer at the callback (unlike the Admin allowlist bootstrap) — an unknown subject can only obtain a registration ticket, never an account or a session.

## REQ-10: Server-side session + cookie + lifetime
**User Story:** As a producer, I want my session represented by an httpOnly cookie backed by server state, so that my browser holds no readable token and the server fully controls the session.
**Reuses:** `admin-oidc-session` REQ-3.
**Acceptance Criteria (EARS):**
- 10.1 WHEN — and ONLY when — the resolved TenantUser is `Active` THE SYSTEM SHALL create a server-side session record holding an opaque-id **hash**, the TenantUser id, a `FamilyId`, issued-at, idle-expiry, absolute-expiry, and `Status = Active` (the raw id is never persisted).
- 10.2 THE SYSTEM SHALL set the session cookie `HttpOnly`, `Secure`, `Path=/`, `__Host-` prefix, `SameSite=Lax` same-site (`None; Secure` cross-site, REQ-13.4), value an opaque random token with no identity encoded; the cookie name SHALL be distinct from the Admin session cookie (REQ-14.4).
- 10.3 WHERE the environment is Development over plain http THE SYSTEM SHALL be permitted to omit `Secure`/`__Host-` for localhost only, and SHALL NOT relax them outside Development.
- 10.4 THE SYSTEM SHALL expire a session at a configurable idle timeout (default 24 h) OR absolute lifetime (default 7 days), whichever first, sliding idle forward (bounded by absolute) on each served request, independent of the id_token lifetime. (amended by `extended-login-session-lifetime`, 2026-08-09)

## REQ-11: Session rotation + reuse / theft detection
**User Story:** As a security reviewer, I want rotating session ids with reuse detection, so that a stolen cookie is detected and the whole session family is killed.
**Reuses:** `admin-oidc-session` REQ-5.
**Acceptance Criteria (EARS):**
- 11.1 WHEN a session id reaches a configurable rotation age (default 15 min) THE SYSTEM SHALL transparently issue a new opaque id within the same `FamilyId` via `Set-Cookie` on the in-flight request (no dedicated refresh endpoint) and mark the presented id `Superseded`.
- 11.2 THE SYSTEM SHALL accept a `Superseded` id within a bounded grace window (default 60 s) so legitimate in-flight concurrent requests are not misclassified.
- 11.3 IF a `Superseded` id is presented after its grace window THEN THE SYSTEM SHALL revoke the entire session family and respond 401.
- 11.4 IF a `Revoked` id is presented THEN THE SYSTEM SHALL respond 401 and take no further action.
- 11.5 THE SYSTEM SHALL serialize rotation per session so concurrent requests past the rotation age produce exactly one new id (one wins; others serve within grace).

## REQ-12: Revocation — logout, logout-all, reject/suspend propagation
**User Story:** As the platform, I want to end producer sessions immediately, so that access is cut without waiting for a cookie to expire.
**Reuses:** `admin-oidc-session` REQ-6 + `identity-rbac` REQ-6.4.
**Acceptance Criteria (EARS):**
- 12.1 WHEN `POST /producer/auth/logout` is called THE SYSTEM SHALL revoke the current session family, clear the cookie, and respond 204.
- 12.2 WHEN `POST /producer/auth/logout-all` is called THE SYSTEM SHALL revoke every `Active`/`Superseded` session of that TenantUser (all devices).
- 12.3 WHEN a TenantUser transitions out of `Active` (suspended) THE SYSTEM SHALL revoke all of that user's live sessions so the next request is denied without waiting for expiry; a `PendingApproval`/`Rejected` user has no live session (sessions exist only for `Active`, REQ-10.1), so reject merely sets status. (F5)
- 12.4 WHEN an authenticated request is served THE SYSTEM SHALL re-resolve the TenantUser's current Status READ-ONLY and deny (401/403) if it is not `Active`, so a suspension/rejection takes effect within one request.

## REQ-13: CSRF protection + pre-session ticket capability
**User Story:** As a security reviewer, I want CSRF protection on cookie-authenticated mutations and a sound capability model for the pre-session register endpoint, so that neither cross-site pages nor forged tickets can drive actions.
**Reuses:** `admin-oidc-session` REQ-7 (+ the pre-session clause is new).
**Acceptance Criteria (EARS):**
- 13.1 THE SYSTEM SHALL issue a JS-readable CSRF token cookie (name distinct from Admin's, REQ-14.4) paired with the producer session.
- 13.2 IF an unsafe-method (POST/PUT/PATCH/DELETE) request to a session-authenticated producer route lacks a request-header CSRF token matching the paired cookie THEN THE SYSTEM SHALL respond 403 (double-submit).
- 13.3 THE SYSTEM SHALL set `SameSite=Lax` on the session cookie same-site (`None; Secure` + CSRF enforced cross-site) as defense in depth.
- 13.4 THE SYSTEM SHALL NOT apply the session CSRF filter to the anonymous, pre-session registration submission endpoint — the single-use signed ticket (REQ-3) IS that endpoint's capability barrier — and SHALL rate-limit it instead.

## REQ-14: Confidential secret custody + dual-OIDC-client isolation
**User Story:** As the operator, I want the Producer Google client handled as a secret and fully isolated from the Admin Google client, so that two confidential OIDC clients coexist on one host without collision.
**Reuses:** `admin-oidc-session` REQ-8 (+ the isolation clause is new — two OIDC clients now coexist).
**Acceptance Criteria (EARS):**
- 14.1 THE SYSTEM SHALL read the Producer Google `client_secret` only from injected configuration (env var / user-secrets / Vault), never from a committed file.
- 14.2 IF the Producer `client_secret` or confidential-client config is unset/placeholder outside Development THEN THE SYSTEM SHALL fail fast at boot (mirroring the existing guards); WHERE the `client_id` is blank THE SYSTEM SHALL skip registering the Producer OIDC scheme rather than fault the whole host.
- 14.3 THE SYSTEM SHALL NOT log the `client_secret`, the authorization `code`, the `id_token`, any raw session id, ticket, or CSRF token.
- 14.4 THE SYSTEM SHALL keep the Producer OIDC client fully distinct from the Admin one: a distinct authentication scheme name, a distinct callback path (`/producer/auth/callback`), a distinct Data Protection application name/purpose, and distinct session + CSRF cookie names — no value shared with the Admin `Google` client.
- 14.5 THE SYSTEM SHALL serve credentialed (cookie) producer XHR only from the allowlisted producer SPA origin via a dedicated producer CORS policy with `AllowCredentials`, leaving the tenant (credential-less) and admin CORS policies unchanged. (F6)

## REQ-15: Producer permission catalog (DB, extensible, parity)
**User Story:** As a platform engineer, I want producer permissions stored as a seeded, feature-sourced catalog with a startup parity guard, so that new capabilities add permissions by migration and no gate references an undefined key.
**Reuses:** `admin-role-rbac` REQ-1 + REQ-11.
**Acceptance Criteria (EARS):**
- 15.1 THE SYSTEM SHALL persist the catalog as two control-plane tables `ProducerPermissionGroups` (Key, LabelTh, SortOrder) and `ProducerPermissions` (Key, GroupKey, LabelTh, SortOrder) in schema `producer`, with the GroupKey foreign key enforced.
- 15.2 THE SYSTEM SHALL seed an initial catalog covering at least the groups `catalog`, `payment`, `roles` and the keys `product.create`, `product.update`, `payment.create`, `payment.redirect`, `producer.roles.view`, `producer.roles.manage`, `producer.user.roles`.
- 15.3 WHERE a new feature ships a migration inserting group/permission rows THE SYSTEM SHALL include them with no change to existing code.
- 15.4 WHEN `GET /producer/permissions` is called by an authenticated producer THE SYSTEM SHALL return groups + permissions ordered by SortOrder.
- 15.5 WHEN the API starts THE SYSTEM SHALL assert that every key referenced by a producer permission gate exists in `ProducerPermissions` (in-memory, no DB) and fail fast naming any offending key.

## REQ-16: Producer role aggregate, grants, assignment
**User Story:** As an admin with role-management rights, I want named producer roles that grant only real permissions and are assignable per tenant, so that producer access maps to job functions.
**Reuses:** `admin-role-rbac` REQ-2/3/4/5/8.
**Acceptance Criteria (EARS):**
- 16.1 THE SYSTEM SHALL model a `ProducerRole` with a unique immutable `Code` matching `^[a-z0-9_]+$` (≤64), a Name, optional Description/Color, a Status (Active/Inactive), and a set of granted permission keys; updates may change everything except `Code`.
- 16.2 THE SYSTEM SHALL store grants as `ProducerRolePermissions` (RoleId, PermissionKey) unique per pair, with PermissionKey foreign-keyed to the catalog, and SHALL reject (400) a create/update granting a key absent from the catalog before persisting.
- 16.3 THE SYSTEM SHALL store assignments as `ProducerRoleAssignments` (TenantUserId, RoleId, TenantId, AssignedBy, AssignedAt) unique per (TenantUserId, RoleId); a producer's role is scoped to the tenant it was approved into.
- 16.4 THE SYSTEM SHALL compute a producer's effective permission set as the union of permission keys over assigned roles whose Status = Active (an Inactive role contributes nothing; zero active roles = zero permissions).
- 16.5 THE SYSTEM SHALL seed a stable anchor role `tenant_owner` granting all producer keys (a deliberate grant — undeletable/undeactivatable) AND an ordinary `tenant_member` role (`product.*`+`payment.*` only, no `roles.*`/`user.roles`) as the default approval choice, so approval does not by default confer tenant role-management; and SHALL reject deleting any role that still has ≥1 assignment (409). (S7)

## REQ-17: Per-request resolution, enforcement, ambient tenant, the 3 endpoints
**User Story:** As the authorization layer, I want each authenticated producer request to resolve fresh tenant + role + permissions and bind the ambient tenant, so that the removed resolver returns and the deferred write-endpoint gates are restored.
**Reuses:** `admin-role-rbac` REQ-5/6/9 + `identity-rbac` REQ-6/7 + the `Program.cs` `346-348`/`418`/`562`/`583` seams.
**Acceptance Criteria (EARS):**
- 17.1 WHEN an authenticated producer request is served THE SYSTEM SHALL re-resolve the TenantUser READ-ONLY and materialize, per request, the ambient `TenantId` (for RLS), a `tenant_role`/`tenant_id` claim, and the effective permission set into a producer scope — derived from the record, never from a token claim.
- 17.2 THE SYSTEM SHALL provide a `RequireProducerPermission(key)` gate that admits a request only if the resolved permission set contains `key`, responding 403 otherwise and executing no handler; it SHALL be fail-closed when no producer scope is bound — so a request that authenticates via the tenant Bearer scheme (REQ-17.3) but binds no producer scope is admitted by the policy yet denied 403 by the gate (authentication passes, authorization fails). (F10)
- 17.3 THE SYSTEM SHALL register a `producer` authorization policy admitting EITHER the producer session scheme OR the existing tenant Bearer scheme, so restoring the gates does not break callers that still present a tenant Bearer.
- 17.4 THE SYSTEM SHALL re-gate `POST /products`, `POST /payment-sessions`, and `POST /payment-sessions/{id}/redirect` with the `producer` policy + `RequireProducerPermission(product.create | payment.create | payment.redirect)` respectively, behind a `Producer:EnforcePermissionsOnWrites` flag so the tightening can be enabled per environment; WHILE the flag is off the pre-existing un-gated tenant-Bearer behavior persists as a deliberate, tracked transitional state (REQ-7.3's gap stays open until the producer FE can establish a session), and the flag SHALL default on in new environments. (F10)
- 17.5 WHEN `GET /producer/me` is called by an authenticated producer THE SYSTEM SHALL return `{ tenantUserId, email, tenantId, role, permissions }`.
- 17.6 THE SYSTEM SHALL remove the `TODO(producer)` markers at `Program.cs` 346-348/418/562/583 once their behavior is restored.

## REQ-18: Cross-catalog `producer.approve` (Admin side)
**User Story:** As the platform, I want the producer-approval permission to live in the Admin catalog, so that the Admin who approves a producer is gated by a real Admin permission.
**Reuses:** `admin-role-rbac` REQ-1/6/11 (the approver is an Admin, gated by `IAdminScope`).
**Acceptance Criteria (EARS):**
- 18.1 THE SYSTEM SHALL add `producer.approve` and `producer.reject` permission keys to the **Admin** catalog (`AdminPermissions`) via a migration, and seed-grant them to the `super_admin` role so the bootstrap Super can approve the first producer.
- 18.2 THE SYSTEM SHALL gate the admin approve/reject endpoints with the Admin `RequirePermission(producer.approve | producer.reject)` reading `IAdminScope`, NOT the producer scope.
- 18.3 WHEN the API starts THE SYSTEM SHALL satisfy BOTH the Admin and Producer permission parity guards (the cross-catalog key is the single intentional coupling between the two RBAC systems).

## REQ-19: Row-level isolation & control-plane placement
**User Story:** As the platform, I want producer identity rows isolated correctly, so that one tenant's users are invisible to another and pre-approval rows are control-plane only.
**Reuses:** `identity-rbac` REQ-8 + `admin-oidc-session` REQ-11 + `admin-role-rbac` REQ-12 (corrected: child tables carry no TenantId column, so they are control-plane, not RLS-keyed).
**Acceptance Criteria (EARS):**
- 19.1 THE SYSTEM SHALL place `TenantUsers` under the producer RLS security policy keyed on `TenantId` (FILTER + BLOCK, reusing `fn_tenant_predicate`), granting `pol_app` own-tenant SELECT and `pol_admin` SELECT/INSERT/UPDATE.
- 19.2 THE SYSTEM SHALL perform registration, correction, and approval writes on the `pol_admin` (RLS-bypass) connection, because a `PendingApproval`/`Rejected` row has `TenantId = NULL` which the BLOCK predicate (NULL ⇒ UNKNOWN) would reject under a tenant SESSION_CONTEXT.
- 19.3 WHEN a tenant (pol_app) principal reads `TenantUsers` THE SYSTEM SHALL return only rows for its bound TenantId (the RLS FILTER on `TenantId` hides every other tenant's rows AND every NULL-TenantId pre-approval row); Status-based visibility (e.g. excluding `Suspended`) is applied in application queries, NOT by the RLS predicate, which keys on `TenantId` only. (F2)
- 19.4 THE SYSTEM SHALL place the child identity tables (`ExternalLogins`, `RegistrationTickets`, `TenantUserProfiles`, `RegistrationAudits`), the session tables (`ProducerSessions`, `ProducerAuthAudits`), and the RBAC catalog/role tables in schema `producer` as **control-plane** (no RLS predicate; granted to `pol_admin` only; audits SELECT/INSERT only).
- 19.5 IF a tenant principal attempts to write any producer identity row for another tenant THEN THE SYSTEM SHALL block it at the database (BLOCK predicate), not only in app code.

## REQ-20: Registration event (outbox)
**User Story:** As the platform, I want a reliable cross-module notification when a registration is submitted, so that the admin side learns of a pending producer without a synchronous coupling.
**Reuses:** the existing transactional outbox (`IOutbox`/`EfOutbox`/`OutboxDispatcher`).
**Acceptance Criteria (EARS):**
- 20.1 THE SYSTEM SHALL define `TenantUserRegistrationSubmitted` as an `INotification` in `src/Contracts` carrying TenantUserId, Subject, Email, hosted-domain, display name, OccurredAt, and a SchemaVersion.
- 20.2 THE SYSTEM SHALL enqueue the event in the SAME transaction as the registration/resubmission write (REQ-4.4 / REQ-5.3) so the notification and the row commit atomically; the enqueue SHALL be performed by a Producer outbox writer bound to the SAME keyed pol_admin `ProducerDbContext` as the write (the stock `IOutbox`/`EfOutbox` binds the default pol_app context and throws without a bound tenant, so it cannot share the transaction), stamping a fixed non-empty platform/sentinel tenant id. (F3, B1)
- 20.3 THE SYSTEM SHALL register the event type in the `OutboxDispatcher` type map so the dispatcher can deserialize and publish it (an unregistered type would fault the dispatcher).
- 20.4 THE SYSTEM SHALL provide an Admin-side consumer that records a "registration awaiting approval" notice idempotently to a control-plane table granted INSERT/SELECT to `pol_worker` (the `OutboxDispatcher` principal, which is NOT in `pol_rls_bypass`) and `pol_admin`; the consumer SHALL touch no tenant-scoped table (else the sentinel-tenant SESSION_CONTEXT would FILTER/BLOCK it and poison the message). (F3, B1/S5)

## REQ-21: Audit (append-only)
**User Story:** As a compliance reviewer, I want every sensitive identity and auth event recorded immutably, so that registration, approval, rejection, and the session lifecycle are traceable.
**Reuses:** `identity-rbac` REQ-9 + `admin-oidc-session` REQ-12 + `admin-role-rbac` REQ-10.
**Acceptance Criteria (EARS):**
- 21.1 WHEN a registration is submitted/resubmitted, approved, rejected, or suspended THE SYSTEM SHALL append an audit row (action, acting subject, target subject, tenant, role, correlation id).
- 21.2 WHEN a producer login succeeds, a session rotates, logout/logout-all runs, a family is revoked on reuse, or an auth attempt is denied/fails THE SYSTEM SHALL append an auth-audit row (event type, TenantUser id where known, correlation id, non-sensitive failure reason).
- 21.3 THE SYSTEM SHALL treat all audit tables as append-only (insert only), and SHALL NOT record tokens, tickets, raw session ids, secrets, or PII beyond non-secret identifiers.

## REQ-22: Error handling (HTTP contract)
**User Story:** As an API consumer, I want predictable status codes, so that each failure is distinguishable.
**Reuses:** `identity-rbac` REQ-10.
**Acceptance Criteria (EARS):**
- 22.1 IF a registration submission has no valid ticket THEN THE SYSTEM SHALL respond 400/409 and create nothing.
- 22.2 IF approval/rejection targets an unknown TenantUser THEN THE SYSTEM SHALL respond 404.
- 22.3 IF approval selects an unknown/inactive/out-of-scope tenant THEN THE SYSTEM SHALL respond 409/422 (tenant) or 403 (out of scope), unchanged record.
- 22.4 IF a registration would duplicate an existing subject THEN THE SYSTEM SHALL respond 409.
- 22.5 IF an authenticated producer is `PendingApproval` THEN THE SYSTEM SHALL respond 403 "awaiting approval"; IF the caller is unauthenticated on a session route THEN 401.

## REQ-23: Scope boundary (backend-only; tenant storefront & Admin unchanged)
**User Story:** As the maintainer, I want this change bounded, so that the customer storefront, the Admin auth stack, and the FE are not disturbed beyond what is required.
**Reuses:** `admin-oidc-session` REQ-10 + plan D3/D5.
**Acceptance Criteria (EARS):**
- 23.1 THE SYSTEM SHALL keep the existing tenant Bearer (`tenant` audience) path serving the customer storefront routes unchanged, and SHALL NOT retire that audience.
- 23.2 THE SYSTEM SHALL implement the Producer module by duplication (copy-rename) and SHALL NOT modify shipped Admin auth/RBAC code beyond: the additive Admin-catalog `producer` group + keys of REQ-18.1, the consequent `AdminPermissions.cs`/seed/grant updates and the `AdminRoleTests`/`AdminRoleRbacGrantsTests` count updates they force (14→16 perms, 5→6 groups), and the shared `OutboxDispatcher.EventTypes` + `src/Contracts` additions of REQ-20.3 (BuildingBlocks, not Admin). (S1/S6)
- 23.3 THE SYSTEM SHALL ADD `ProducerArchitectureTests` asserting `Producer.* ⇏ Admin.*` AND `Admin.* ⇏ Producer.*` (the existing suite does NOT yet enforce this — without it REQ-23.3 is vacuous) and keep all `Architecture.Tests` green. (B2)
- 23.4 THE SYSTEM SHALL deliver backend only; `pol-admin` FE wiring is a separate follow-up slice in another repo.

---

## Edge Cases & Open Questions

- **`EnforcePermissionsOnWrites` default (REQ-17.4):** with no producer FE yet, a producer cannot obtain a
  session cookie, so flipping the flag on immediately fail-closes the 3 endpoints for any remaining tenant-Bearer
  writer. Proposal: ship the flag **off** in existing envs, **on** in new ones; flip per-env when the producer FE
  lands. CONFIRM the default.
- **Profile field set (REQ-7.1):** the exact producer detail fields (personType, idNumber, producerCode,
  licenseNumber, phone, …) are taken from the existing `pol-admin /register` form; the design will pin the schema.
  CONFIRM whether any field is required vs optional, and whether idNumber/license need format validation now.
- **Registration page URL (REQ-9.4):** the redirect target (`http://localhost:5200/register` in dev) must be a
  configured absolute URL validated at boot; prod value is the producer SPA origin. CONFIRM prod origin.
- **Deploy topology (cookie attributes, REQ-10.2/13.3):** defaults assume producer SPA + API same-site
  (`SameSite=Lax`). If prod is cross-site → `SameSite=None; Secure` + CSRF enforced. CONFIRM prod topology.
- **Anchor role grant (REQ-16.5):** `tenant_owner` grants ALL producer keys as the recovery anchor; confirm a
  per-tenant owner is intended (vs a platform-wide seed only).
- **`producer.view` (REQ-18):** approval needs `producer.approve`; a read-only admin list of pending producers may
  want a `producer.view` key too. Deferred unless the admin list endpoint is in this slice. CONFIRM.
- **Suspend endpoint deferred (F5):** the `Suspended` status + revoke-live-sessions behavior (REQ-12.3/12.4) ship
  in this slice, but the admin *suspend action/endpoint* (Active→Suspended) is a follow-up — not in the goal. A
  future suspend is a thin add (flip status + RevokeAllForUser).
- **Email refresh:** `Subject` is the stable key; `Email` MAY drift at Google. This slice treats Email as
  informational and MAY refresh it from the verified id_token on login; not a gate. Accepted.
- **One tenant per producer:** a subject maps to exactly one TenantUser (REQ-1.4), so a person acts for at most one
  tenant. Multi-tenant producers are out of scope (future: a join table). Accepted for this slice.
- Findings log anchor: `04dc1a4` (requirements.md uncommitted at analyze time).

### Findings log (spec-analyze) — anchor: `04dc1a4`

All findings resolved autonomously (AFK/goal directive) with the recommended option; REQ IDs stable.

| F | category | REQ | decision | note |
|---|---|---|---|---|
| F1 | inconsistency | 1.1/1.3/6.2 | applied | roles live in `ProducerRoleAssignments` only (zero-or-more); no role column on TenantUser; endpoints gate on permissions |
| F2 | conflict | 19.3 | applied | RLS FILTER keys on TenantId only (hides other-tenant + NULL-TenantId rows); Status filtering is app-layer |
| F3 | gap | 20.2/20.4 | applied | registration outbox enqueue uses a platform/sentinel tenant id on pol_admin (IOutbox needs a bound tenant); consumer touches no tenant-scoped table |
| F4 | gap | 6.5 | applied | approval validates assigned role exists+Active and rejects a non-`PendingApproval` target (must resubmit) |
| F5 | inconsistency | 12.3 | applied | suspend (Active→Suspended) is the session-killer; reject-of-Pending has none; suspend *endpoint* deferred (status+revoke shipped) |
| F6 | gap | 14.5 | applied | dedicated credentialed producer CORS policy for cookie XHR (tenant/admin policies unchanged) |
| F7 | ambiguity | 3.1/3.4 | applied | ticket = signed+encrypted self-contained token the client carries; server row (by ticket id) is the single-use replay guard |
| F8 | ambiguity | 9.4 | applied | Active branch establishes session AND redirects to the allowlisted post-login returnTo; only none/Rejected go to /register |
| F9 | gap | 4.6 | applied | concurrent duplicate registration resolved by unique (subject)/(provider,subject) — one commits, other 409 |
| F10 | clarity | 17.2/17.4 | applied | tenant-Bearer on the 3 endpoints with flag on: authn passes but RequireProducerPermission fail-closes 403; flag defaults on in new envs |
