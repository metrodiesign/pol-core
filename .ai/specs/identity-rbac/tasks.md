# Implementation Tasks: Identity & RBAC — TenantUser realm
> Status: unknown
> Cohesive slices; TDD (pure-domain first); commit per task; PR not merged.

- [x] 1. Identity.Domain — `TenantUser` aggregate (`AggregateRoot<Guid>`; `Create` for a pending applicant: subject/email required, Status=PendingApproval, TenantId null, Role unset/default; `Approve(tenantId, role, now)` -> Active guarding non-pending; `Suspend()`), `TenantUserRole` {TenantAdmin,Finance,Viewer}, `TenantUserStatus` {PendingApproval,Active,Suspended}, `ExternalLogin` (Provider+Subject->TenantUserId), `TenantUserProfile`, `RegistrationTicket` (`Issue` + `Consume(now)` single-use/expiry guard). Done = pure-domain unit tests green.
     REQ-1, 2.1/2.2, 3.2/3.3, 5.5, 7.1.

- [x] 2. Identity.Application — ports (`ITenantUserRepository`, `IRegistrationTicketStore`, `IRegistrationAuditWriter`, `ITenantDirectory`), `IssueRegistrationTicket` + `CompleteRegistration` + `ApproveTenantUser` commands/handlers (idempotent-under-retry; subject from ticket not form; TenantId from admin not form; validate tenant via ITenantDirectory), `ResolveTenantUser` query (sub -> active user -> TenantId+Role; deny pending/suspended). Done = handler unit tests (fakes) green.
     REQ-3, 2.3/2.4, 5.2/5.3/5.4/5.6, 6.1/6.2/6.3/6.4, 10.1/10.2/10.3/10.4. Depends on: 1.

- [x] 3. Identity.Infrastructure — EF configs (TenantUsers unique(Subject), ExternalLogins unique(Provider,Subject), Profiles, RegistrationTickets, RegistrationAudits), repos + ticket store + audit writer, `AddIdentityModule`. Done = build + EF model OK.
     REQ-1.5, 2.1, 8 (persistence). Depends on: 2.

- [x] 4. Central migration AddIdentityTables + RLS + grants — assembly into `HostModuleAssemblies.All` + `RawConnectionTests`; migration (timestamp newest): tables + indexes + `Sql()` ALTER SECURITY POLICY (FILTER+BLOCK `fn_tenant_predicate(TenantId)` on TenantUsers + child tables) + grants (pol_app SELECT own identity rows; pol_admin SELECT/INSERT/UPDATE identity + SELECT/INSERT RegistrationTickets/RegistrationAudits) + Down complete. Done = `ef migrations add` + script OK.
     REQ-8.1/8.4/8.5. Depends on: 3.

- [x] 5. Host wiring + endpoints + runtime resolver — admin-scoped Identity repos via the keyed "admin" scope; `TenantUserContext` (sub->active TenantUser->bind TenantId via AmbientTenant, replaces the prod claim shim; Development claim fallback only) + `RequireTenantRole`; `ITenantDirectory` impl over Tenant's admin `ITenantRepository`; endpoints: `GET /me/registration` (issue ticket), `POST /registrations/complete`, `POST /admin/tenant-users/{subject}/approve` (RequireAuthorization("admin")); `AddIdentityModule`. Done = build + container boot + authz status correct.
     REQ-4, 5.1, 6.5, 7.2/7.3/7.4, 9.1, 9.2. Depends on: 3,4.

- [x] 6. Verification suites — Integration (RLS: tenant sees only own users; pending NULL-tenant invisible to pol_app; admin cross-tenant; app cannot write identity; tickets admin-only; duplicate subject rejected). Hosts (approve authz 401/403; registration). Architecture (Identity.Domain no EF/no Infra; Identity.* no Host; composition module). Done = unit/arch/hosts green; integration authored ([Trait Integration]).
     REQ-5.1, 6.3, 8.2/8.3/8.5, 9.3, 10.1/10.2/10.3/10.4. Depends on: 5.
- Deferred (Open Questions in requirements): AdminUser persistent realm + admin sub-RBAC; dual maker-checker; platform-issued sessions.
