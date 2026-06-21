# Tasks: production-hardening

> Tracker for the production-grade push. Design + the Codex-reviewed RLS decisions live in design.md.
> No requirements.md here on purpose: the EARS criteria are foundation-scaffold's REQ-3/4/5/8/11/12 +
> tasks G1/G2/F2/Z1/Z2 — this spec implements them. (spec-trace skips dirs without requirements.md.)

## PR1 — DB foundation + RLS security floor  (DONE, branch feat/prod-hardening-db-rls)

Design adversarially reviewed by Codex (4 rounds -> APPROVED) and spike-proven on live SQL Server 2025.

Delivered:
- Model: `TenantId` on OutboxMessage + IdempotencyRecord; idempotency keys gain a PspConnectionId dimension.
- Ambient-tenant binding (`ITenantScope` / `AmbientTenant`) for non-HTTP entry points.
- Webhook system path: `IWebhookTenantResolver` -> `producer.usp_resolve_webhook_tenant` (EXECUTE AS a
  bypass member) -> bind tenant -> RLS-scoped handler. Endpoint orchestrates resolve -> Begin -> Send.
- OutboxDispatcher: per-message fresh scope + `SESSION_CONTEXT` so consumers (OrderPaidConsumer) write
  Orders RLS-scoped. Moved to a dedicated Worker host (pol_worker principal).
- `docker/bootstrap/01-principals.sql`: logins pol_app/pol_admin/pol_worker + pol_webhook_resolver
  (no login) + role pol_rls_bypass. Applied to live pol-db.
- EF migration G1 (producer schema + tables + FK/index) and G2 (predicate functions + resolve proc +
  SECURITY POLICY + object grants), both applied to live DB.
- Connection strings -> SQL auth, single DB `PaymentOrchestration`, one login per host; password via env.
- `Directory.Build.props`: `InvariantGlobalization=false` (SqlClient rejects invariant mode).
- Arch test: ban raw `SqlConnection` in production infrastructure.
- Integration tests (`tests/Integration.Tests`, `[Trait Category=Integration]`).
- CI `dotnet-integration` job (SQL 2025 service -> bootstrap -> migrations -> integration tests);
  docker-compose.yml + .env.example.

Evidence:
- `dotnet build pol-core.slnx -warnaserror` = 30 projects, 0 errors, 0 warnings.
- Unit suite (`--filter Category!=Integration`) = 112 passed (incl. new arch test), 0 failed.
- Integration suite vs live SQL 2025 (`--filter Category=Integration`) = 9 passed, 0 failed; re-run
  green (idempotent). Proves: tenant read-isolation, write-block, admin bypass, sysadmin sees 0,
  outbox forge-block + app-cannot-read-outbox, webhook resolve proc, host principal identity.
- Live DB: `sys.security_policies` is_enabled=1, 28 security predicates, functions + proc present.
- Viewports: n/a (no UI in this PR).
- Deviations: runtime startup principal guard deferred (integration test covers identity — see design.md);
  AdminDbContext admin migrations deferred (no admin entities yet).

## PR2 — HTTP surface + observability hardening  (DONE, branch feat/prod-hardening-http-observability)

Design + adversarial diff review both run as multi-agent workflows; all confirmed in-scope findings fixed.

Delivered:
- `POST /payment-sessions/{id}/redirect` wired to StartRedirect; tenant scoping automatic via the RLS floor.
- Shared host-support library `BuildingBlocks.Web` (FrameworkReference, no app): one place for the
  cross-cutting concerns, referenced by all three hosts.
- Global `IExceptionHandler` -> RFC7807 ProblemDetails: 404 NotFound, 409 Concurrency/illegal-state,
  400 Argument, opaque 500 for TenantBinding + unknown. FIXED generic details — no message/stack/SQL/tenant
  leak. Added typed `NotFoundException` + `TenantBindingException`; StartRedirect not-found now 404.
- Built-in JSON console logging (no Serilog) + correlation-id middleware (X-Correlation-ID, scope, rejects
  malformed ids). Correlation wraps the exception handler so error logs carry the id.
- Split health: `/health/live` (process only) + `/health/ready` (custom DB CanConnect + vault key checks,
  no HealthChecks.EFCore package); minimal body, no topology leak.
- Webhook rate limiter (built-in sliding window), partitioned by SOURCE IP so a rotating-GUID flood shares
  one bounded budget and is 429'd before the tenant-resolve DB lookup; always emits Retry-After.
- Real Google ID-token validation DRY'd into one extension + RequireHttpsMetadata + MapInboundClaims=false
  + fail-fast on a placeholder ClientId outside Development.
- Net new NuGet packages: ZERO (built-in framework features throughout).

Evidence:
- `dotnet build pol-core.slnx -warnaserror` = 31 projects, 0 errors, 0 warnings.
- Unit suite = 139 passed (Hosts.Tests 4 -> 31, incl. exception mapping + no-leak, health live/ready split,
  correlation generate/echo/reject-malformed, redirect auth, webhook 429 + Retry-After, e2e problem+json
  no-leak, Google fail-fast guard in non-Development, vault readiness branches), 0 failed.
- Integration suite (live SQL 2025) = 9 passed — RLS floor unaffected.
- Viewports: n/a (no UI). Accepted review notes: auth fail-fast keys on the environment name (fails closed,
  standard convention); Worker pulls JwtBearer transitively via BuildingBlocks.Web (moving the health checks
  into Infrastructure would drag ASP.NET into the data layer — worse); TenantId is not on the error-log scope
  for StartRedirect (auth runs after correlation) — deferred.

## PR3 — PSP real HTTP adapters (Z1)  (needs 2C2P + Omise sandbox keys)
- [ ] 2C2P + Omise/Opn HTTP via IHttpClientFactory + Polly; mock-tested; sandbox smoke-test on key handoff.

## PR4 — Vault + secret custody hardening (Z2, self-host)
- [ ] Master key from env/secret-file (not appsettings); key id+version rotation; runbook; reveal audit.
