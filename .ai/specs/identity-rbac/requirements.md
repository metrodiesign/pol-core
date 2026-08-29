# Requirements: Identity & RBAC — TenantUser realm

> Status: unknown
> review at the PR gate). Source: docs/reference/payment-orchestration-modules.md section 2.5.

## Overview

ทุกวันนี้ TenantId มาจาก `tenant_id` claim บน Google token (dev shim ใน `HttpTenantContext`) — แต่ reference
2.5 บังคับว่า "role/tenant ตัดสินที่ platform เสมอ" (Google ทำแค่ authentication). สเปกนี้สร้าง platform-side
identity ฝั่ง **Merchant Console realm**: ตาราง `TenantUser` (ผูก [[Tenant]] ที่ provisioning สร้าง) + ExternalLogin
(Google `sub` -> user) + registration ticket + register/approve flow + RBAC (Tenant Admin/Finance/Viewer) +
runtime resolution ที่ replace claim shim. authentication ใช้ Google SSO ที่มีอยู่ (`GoogleAuthenticationExtensions`).

**Scope (slice นี้):** เฉพาะ **TenantUser** (schema producer, RLS ด้วย TenantId). การ approve ใช้ Google role
`admin` ที่มีอยู่ (เหมือน Tenant provisioning) — **ยังไม่สร้างตาราง `AdminUser` + admin sub-RBAC** (Platform
Owner/Operator/Risk/Support) และ **ยังไม่ทำ dual maker-checker** (single-admin approve + audit ก่อน). ทั้งสองข้อ
อยู่ใน Open Questions เป็น deferred follow-up.

## REQ-1: TenantUser master record
**User Story:** As the platform, I want a TenantUser record per person who can act for a tenant, so that role and tenant are decided server-side, never by the token.
**Acceptance Criteria (EARS):**
- 1.1 THE SYSTEM SHALL store a TenantUser with: external subject (Google `sub`, the stable id), Email, TenantId (FK to an existing producer.Tenants row), Role, Status, CreatedAtUtc.
- 1.2 THE SYSTEM SHALL constrain Status to one of `PendingApproval`, `Active`, `Suspended`.
- 1.3 THE SYSTEM SHALL constrain Role to one of `TenantAdmin`, `Finance`, `Viewer` (reference 2.3 Merchant Console roles).
- 1.4 WHILE a TenantUser is `PendingApproval` THE SYSTEM SHALL allow its TenantId to be unset (NULL) until approval binds it.
- 1.5 THE SYSTEM SHALL enforce that an external subject maps to at most one TenantUser (unique on subject).
- 1.6 IF a TenantUser is created with a Role or Status outside the allowed sets THEN THE SYSTEM SHALL reject it with a validation error.

## REQ-2: External login mapping
**User Story:** As the platform, I want to map a Google identity to a TenantUser, so that returning users resolve to their record.
**Acceptance Criteria (EARS):**
- 2.1 THE SYSTEM SHALL store an ExternalLogin keyed by (Provider, Subject) that references exactly one TenantUser.
- 2.2 THE SYSTEM SHALL set Provider to `google` for this slice.
- 2.3 WHEN a Google identity authenticates AND an ExternalLogin exists for its (provider, subject) THE SYSTEM SHALL resolve the linked TenantUser.
- 2.4 IF no ExternalLogin exists for an authenticated Google subject THEN THE SYSTEM SHALL treat the caller as an unregistered applicant (REQ-4).

## REQ-3: Registration ticket
**User Story:** As an applicant, I want a short-lived registration handle after Google sign-in, so that I can submit my registration form without yet holding a session.
**Acceptance Criteria (EARS):**
- 3.1 THE SYSTEM SHALL issue a RegistrationTicket that carries the verified identity (subject, email, hosted-domain) captured from the validated Google token.
- 3.2 THE SYSTEM SHALL make a RegistrationTicket single-use and short-lived (expires within a bounded TTL).
- 3.3 WHEN a RegistrationTicket is consumed THE SYSTEM SHALL mark it used so it cannot be replayed.
- 3.4 IF a RegistrationTicket is expired, already used, or unknown THEN THE SYSTEM SHALL reject the completion with an error and create no TenantUser.
- 3.5 THE SYSTEM SHALL NOT treat a RegistrationTicket as an authenticated session (it grants only the ability to complete registration).

## REQ-4: Registration flow
**User Story:** As a new user, I want to register after signing in with Google, so that an admin can approve me onto a tenant.
**Acceptance Criteria (EARS):**
- 4.1 WHEN an authenticated Google caller has a verified email AND no ExternalLogin THE SYSTEM SHALL issue a RegistrationTicket (REQ-3.1).
- 4.2 WHEN a valid RegistrationTicket is completed with a registration form THE SYSTEM SHALL create a TenantUser with Status `PendingApproval`, an ExternalLogin, and a Profile, in ONE transaction.
- 4.3 THE SYSTEM SHALL take the subject/email from the ticket's verified identity, NEVER from the form body.
- 4.4 THE SYSTEM SHALL NOT set TenantId or Role from the registration form (both are decided at approval — REQ-5).
- 4.5 IF a TenantUser/ExternalLogin already exists for the subject THEN THE SYSTEM SHALL reject a second registration (REQ-1.5).

## REQ-5: Approval flow (admin)
**User Story:** As a platform admin, I want to approve an applicant onto a specific tenant with a role, so that access is granted deliberately.
**Acceptance Criteria (EARS):**
- 5.1 THE SYSTEM SHALL restrict approval to the `admin` authorization role (the admin-SPA audience), rejecting the tenant role with 403 and anonymous with 401.
- 5.2 WHEN an admin approves a `PendingApproval` TenantUser THE SYSTEM SHALL set its TenantId to a tenant the admin selected, assign a Role, and set Status `Active`, in ONE transaction.
- 5.3 THE SYSTEM SHALL resolve TenantId ONLY from the admin's selection and SHALL validate that the selected tenant exists and is active — never trusting any value the applicant supplied.
- 5.4 IF the selected tenant does not exist or is not active THEN THE SYSTEM SHALL reject the approval and leave the TenantUser unchanged.
- 5.5 IF the target TenantUser is not `PendingApproval` THEN THE SYSTEM SHALL reject the approval (no re-approval / no overwrite of an Active user).
- 5.6 THE SYSTEM SHALL record an audit row for each approval (acting admin subject, target subject, tenant, role, correlation id) — approval is a sensitive action.

## REQ-6: Runtime tenant + role resolution
**User Story:** As the platform, I want each authenticated tenant request to resolve its tenant and role from the TenantUser record, so that the token cannot assert its own tenant or role.
**Acceptance Criteria (EARS):**
- 6.1 WHEN an authenticated Google caller hits a tenant route THE SYSTEM SHALL resolve the active TenantUser by (provider, subject) and bind its TenantId as the ambient tenant for RLS.
- 6.2 THE SYSTEM SHALL derive the caller's tenant Role from the TenantUser record, not from a token claim.
- 6.3 IF no active TenantUser exists for the subject (unregistered, pending, or suspended) THEN THE SYSTEM SHALL deny the tenant request (403) and bind no tenant.
- 6.4 WHILE a TenantUser is `Suspended` THE SYSTEM SHALL deny all tenant requests for that subject.
- 6.5 THE SYSTEM SHALL NOT fall back to a `tenant_id` token claim for production resolution (the dev claim shim is replaced by this lookup; any remaining dev fallback is Development-only and off in production).

## REQ-7: RBAC enforcement (Merchant Console roles)
**User Story:** As the platform, I want tenant actions gated by the user's role, so that a Viewer cannot perform write actions.
**Acceptance Criteria (EARS):**
- 7.1 THE SYSTEM SHALL admit only `TenantAdmin`, `Finance`, or `Viewer` as the resolved tenant role.
- 7.2 WHERE an action is read-only THE SYSTEM SHALL admit any active tenant role.
- 7.3 WHERE an action is a write/financial action THE SYSTEM SHALL admit only the roles permitted for it (at minimum: `Viewer` is denied write actions).
- 7.4 IF a resolved role is not permitted for the requested action THEN THE SYSTEM SHALL respond 403 and perform no state change.

## REQ-8: Row-level isolation of identity tables
**User Story:** As the platform, I want identity rows isolated like every other tenant row, so that one tenant's users are invisible to another.
**Acceptance Criteria (EARS):**
- 8.1 THE SYSTEM SHALL place `TenantUser` and its child identity tables under the producer RLS security policy keyed on TenantId, FILTER + BLOCK, reusing `fn_tenant_predicate`.
- 8.2 WHEN a tenant principal reads identity rows THE SYSTEM SHALL return only rows for its bound TenantId.
- 8.3 THE SYSTEM SHALL allow the pol_admin (bypass) connection to read/write identity rows cross-tenant for registration and approval (which run before a TenantId is bound).
- 8.4 THE SYSTEM SHALL grant the least privilege per principal (pol_app: own-tenant read of its user; pol_admin: the registration/approval writes), mirroring the Tenant grants.
- 8.5 IF a tenant principal attempts to write an identity row for another tenant THEN THE SYSTEM SHALL block it at the database (BLOCK predicate), not only in app code.

## REQ-9: Trust boundary & confidentiality
**Acceptance Criteria (EARS):**
- 9.1 THE SYSTEM SHALL require a verified email (`email_verified`) and (where configured) the hosted-domain guard for any identity action, reusing the existing Google validation.
- 9.2 THE SYSTEM SHALL NOT log tokens, registration tickets, or PII beyond non-secret identifiers.
- 9.3 THE SYSTEM SHALL treat registration/approval under pol_admin bypass as auditable control-plane actions (REQ-5.6) with a correlation id.

## REQ-10: Error handling
**Acceptance Criteria (EARS):**
- 10.1 IF an applicant submits a registration form without a valid ticket THEN THE SYSTEM SHALL respond 400/409 and create nothing.
- 10.2 IF approval targets an unknown TenantUser THEN THE SYSTEM SHALL respond 404.
- 10.3 IF approval selects an unknown/inactive tenant THEN THE SYSTEM SHALL respond 409/422 (REQ-5.4).
- 10.4 IF a registration would duplicate an existing subject THEN THE SYSTEM SHALL respond 409.

## Edge Cases & Open Questions

- **Scope cut — AdminUser realm:** this slice does NOT create the `admin`-schema `AdminUser` table or the admin sub-RBAC (Platform Owner/Operator/Risk/Support). Approval is gated by the existing Google `admin` role. CONFIRM whether AdminUser persistence + admin sub-roles are needed now or as a follow-up.
- **Scope cut — maker-checker:** approval is single-admin + audited here; reference 2.5/2.3 call for maker-checker on sensitive actions. CONFIRM whether dual-control approval is required in this slice.
- **Session model:** this slice keeps Google ID-token bearer auth (no platform-issued session/refresh tokens); the platform adds tenant/role resolution on top. CONFIRM if a platform session is wanted.
- **Role→action matrix (REQ-7.3):** the precise per-endpoint role matrix is minimal here (Viewer = read-only). CONFIRM the full matrix (e.g. who may create payment sessions vs. only view).
- **Registration ticket transport:** ticket as an opaque server-stored token returned to the SPA; CONFIRM transport (header vs body) and TTL.
- Findings log anchor: (none yet — first authoring).
