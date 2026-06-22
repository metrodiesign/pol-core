# Implementation Tasks: Identity & RBAC — TenantUser realm
> Status: approved 2026-06-23 (autonomous, AFK). Branch feat/identity-rbac (stacks on feat/tenant-provisioning).
> Cohesive slices; TDD (pure-domain first); commit per task; PR not merged.

- [x] 1. Identity.Domain — `TenantUser` aggregate (`AggregateRoot<Guid>`; `Create` for a pending applicant: subject/email required, Status=PendingApproval, TenantId null, Role unset/default; `Approve(tenantId, role, now)` -> Active guarding non-pending; `Suspend()`), `TenantUserRole` {TenantAdmin,Finance,Viewer}, `TenantUserStatus` {PendingApproval,Active,Suspended}, `ExternalLogin` (Provider+Subject->TenantUserId), `TenantUserProfile`, `RegistrationTicket` (`Issue` + `Consume(now)` single-use/expiry guard). Done = pure-domain unit tests green.
     Satisfies: REQ-1, 2.1/2.2, 3.2/3.3, 5.5, 7.1. Verify: `dotnet test tests/Identity.Tests` (domain).
     Evidence: `dotnet test tests/Identity.Tests` -> Passed 15, Failed 0 (TenantUser register/approve/suspend + guards, RegistrationTicket single-use/expiry, ExternalLogin). Viewports: n/a (no UI). Deviations: none. Scaffolding (4 csproj + slnx + test csproj) included.

- [x] 2. Identity.Application — ports (`ITenantUserRepository`, `IRegistrationTicketStore`, `IRegistrationAuditWriter`, `ITenantDirectory`), `IssueRegistrationTicket` + `CompleteRegistration` + `ApproveTenantUser` commands/handlers (idempotent-under-retry; subject from ticket not form; TenantId from admin not form; validate tenant via ITenantDirectory), `ResolveTenantUser` query (sub -> active user -> TenantId+Role; deny pending/suspended). Done = handler unit tests (fakes) green.
     Satisfies: REQ-3, 4, 5, 6.1/6.2/6.3/6.4, 10. Depends on: 1. Verify: `dotnet test tests/Identity.Tests`.
     Evidence: `dotnet test tests/Identity.Tests` -> Passed 25 (15 domain + 10 handler), Failed 0. Covers: issue dup-reject, complete creates-pending+consumes-ticket+rejects-replay/unknown, approve activates+audits / rejects inactive-tenant+unknown-user, resolve active vs pending/suspended/unknown. Viewports: n/a. Deviations: ITenantDirectory port keeps Identity.Application free of a Tenant dependency (host wires impl over Tenant repo) — improves on design's "Identity.Application -> Tenant.Application" note. Repos plain admin-bound; only IUnitOfWork keyed "admin". RegistrationAudit entity added to Domain.

- [ ] 3. Identity.Infrastructure — EF configs (TenantUsers unique(Subject), ExternalLogins unique(Provider,Subject), Profiles, RegistrationTickets, RegistrationAudits), repos + ticket store + audit writer, `AddIdentityModule`. Done = build + EF model OK.
     Satisfies: REQ-1.5, 2.1, 8 (persistence). Depends on: 2. Verify: `dotnet build`.

- [ ] 4. Central migration AddIdentityTables + RLS + grants — assembly into `HostModuleAssemblies.All` + `RawConnectionTests`; migration (timestamp newest): tables + indexes + `Sql()` ALTER SECURITY POLICY (FILTER+BLOCK `fn_tenant_predicate(TenantId)` on TenantUsers + child tables) + grants (pol_app SELECT own identity rows; pol_admin SELECT/INSERT/UPDATE identity + SELECT/INSERT RegistrationTickets/RegistrationAudits) + Down complete. Done = `ef migrations add` + script OK.
     Satisfies: REQ-8.1/8.4/8.5. Depends on: 3. Verify: `ef migrations script` + Integration (task 6).

- [ ] 5. Host wiring + endpoints + runtime resolver — admin-scoped Identity repos via the keyed "admin" scope; `TenantUserContext` (sub->active TenantUser->bind TenantId via AmbientTenant, replaces the prod claim shim; Development claim fallback only) + `RequireTenantRole`; `ITenantDirectory` impl over Tenant's admin `ITenantRepository`; endpoints: `GET /me/registration` (issue ticket), `POST /registrations/complete`, `POST /admin/tenant-users/{subject}/approve` (RequireAuthorization("admin")); `AddIdentityModule`. Done = build + container boot + authz status correct.
     Satisfies: REQ-4, 5.1, 6.5, 7.2/7.3/7.4, 9.1. Depends on: 3,4. Verify: `dotnet test tests/Hosts.Tests`.

- [ ] 6. Verification suites — Integration (RLS: tenant sees only own users; pending NULL-tenant invisible to pol_app; admin cross-tenant; app cannot write identity; tickets admin-only; duplicate subject rejected). Hosts (approve authz 401/403; registration). Architecture (Identity.Domain no EF/no Infra; Identity.* no Host; composition module). Done = unit/arch/hosts green; integration authored ([Trait Integration]).
     Satisfies: REQ-5.1, 6.3, 8.2/8.3/8.5, 9.3, 10. Depends on: 5. Verify: `dotnet test pol-core.slnx --filter Category!=Integration`.

## Notes
- Identity is a composition module (refs Tenant.Application), outside Architecture peer-ban (like Tenant).
- Reuses Tenant's pol_admin keyed scope, AmbientTenant/ITenantScope, fn_tenant_predicate, ProblemDetails, Google authn.
- Deferred (Open Questions in requirements): AdminUser persistent realm + admin sub-RBAC; dual maker-checker; platform-issued sessions.
