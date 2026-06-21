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

## PR3 — PSP real HTTP adapters (Z1)  (IN PROGRESS, branch feat/prod-hardening-psp-adapters)

Design + adversarial payments-security critique run as a multi-agent workflow (w5rckuawo, 7 agents).
Critique verdict NEEDS_WORK -> 8 must-fix resolved below before any code. Locked design:

- Correlation key (critical fix): 2C2P ExternalChargeId = a STABLE invoiceNo (session.Id "N") used
  identically by create + ParseWebhook + FetchChargeAsync (paymentInquiry) so GetByExternalChargeAsync
  always matches; never paymentToken (per-attempt). Omise uses the charge/link id consistently.
- 2C2P contract: JWT-wrapped JSON (body {"payload": HS256-JWT}). paymentToken (create) -> webPaymentUrl
  (hosted, SAQ A); paymentInquiry (fetch) -> respCode authoritative ("0000"=Paid, "0001"/"2001"/"4009"=
  Pending, else Failed). Webhook = body JWT; VerifyWebhook validates HS256 + pins alg (reject none/conf)
  + checks merchantID. Secret = JSON envelope {merchantId, secretKey}. Base host by UseSandbox
  (sandbox-pgw.2c2p.com / pgw.2c2p.com). amount = major-unit decimal via Iso4217.MinorUnitDigits;
  currencyCode alpha. invoiceNo + idempotencyID stable -> retry-safe.
- Omise contract: Basic auth (secretKey:""). card -> POST /charges (NO card data; authorize_uri hosted
  3DS redirect) + deterministic Idempotency-Key (session.Id) so a retried POST returns the same charge.
  promptpay -> Payment Links+ (linksplus-api.omise.co/external/links) hosted transaction_url; NEVER
  source+charge (offline QR = forbidden). fetch GET /charges/{id}: successful=Paid, pending=Pending
  (NEVER Failed), failed/expired/reversed=Failed. Secret = JSON envelope {secretKey, promptPayTemplateId,
  promptPayTeamId}. Host constant; KEY prefix decides test/live -> guard skey_test_ XOR UseSandbox.
- Omise webhook HMAC: DEFERRED honestly. The signing secret differs from the API secret and the timestamp
  is not threaded through the (rawPayload,signature,secret) seam. VerifyWebhook does a well-formedness
  check only; the mandatory server-side fetch-to-confirm (handler runs it before every MarkPaid) is the
  sole authority; PR2 webhook rate limiter bounds forged-id probe exposure. Real Omise HMAC = follow-up
  that custodies a second signing secret. (2C2P HMAC IS real — same secret, signature in the body JWT.)
- Resilience: keep adapters DI SINGLETON + inject IHttpClientFactory (named clients "2c2p"/"omise",
  per-call CreateClient). charge-create POST = single-shot (NO retry, code path) so a timeout never
  double-charges; fetch GET = bounded retry (2x, expo backoff + jitter, transient only) via a hand-rolled
  helper. No circuit breaker (captive 2-PSP platform earns nothing).
- Deps: ONE new first-party pin Microsoft.Extensions.Http (platform HttpClientFactory, MIT, ships w/ SDK).
  JWT HS256 + the retry are hand-rolled (no third-party Polly / IdentityModel / WireMock) — lazy-correct,
  keeps the zero-third-party streak and lets us pin the JWT alg.
- Seam preserved: IPspAdapter/PspCharge/WebhookEvent/PspChargeStatus and PspAdapterFactory UNCHANGED;
  swap only the two adapter bodies + PaymentsModuleRegistration + one host line (Configure<PspOptions>).
  StubPspAdapter + PspWebhookPayload deleted (per-adapter parsing replaces the shared stub status map);
  Architecture.Tests anchor re-pointed to PspAdapterFactory. RLS (PR1) + PR2 floors untouched.

Implemented + adversarially reviewed (workflow w3hr2many, 21 agents, 17 findings). Remediated all confirmed
in-scope findings:
- CRITICAL: Omise PromptPay correlation key was a Links+ transaction id while the webhook/fetch key off the
  charge id -> customer charged, order never fulfils. Resolved by DEFERRING PromptPay (throws NotSupported)
  rather than shipping a known-broken path that cannot be verified without a sandbox. CARD stays (charge id
  is correlation-consistent across create/webhook/fetch).
- MED: the hand-rolled JWT verifier threw (uncaught) on a non-string alg/payload from an untrusted webhook
  -> 500 instead of clean Rejected. Guarded alg/payload ValueKind == String; added a non-string-alg test.
- MED/LOW (tests): added the create->fetch invoiceNo correlation assertion, forged-response-JWT negatives
  (create + fetch), a ParseWebhook respCode->status theory; aligned 2C2P ParseWebhook to the fetch mapping
  (pending codes -> Pending, not Failed); collapsed the dead VerifyWebhook signature-arm; deduped GetString
  into the base; added the Omise fetch-path env guard + a minor-units-verbatim amount theory.

Deferred (documented, need the sandbox key handoff): Omise card-create exact field set (real Omise may need
a token/source) and Omise webhook HMAC (separate signing-secret custody) and PromptPay (Links+ link->charge
correlation). AdminConsole/Worker intentionally do not bind PspOptions (adapter HTTP surface is
TenantConsole-only).

Evidence: dotnet build 31 projects 0/0; unit 173 passed (Payments.Tests 14 -> 48, incl. the adapter suite);
integration unaffected (PR3 touches no data-layer/RLS/migration file). ZERO third-party deps (1 first-party
Microsoft.Extensions.Http); JWT HS256 + retry hand-rolled.

## PR4 — Vault + secret custody hardening (Z2, self-host)  (SCOUTED)

Current: LocalEnvelopeVaultStore = AES-256-GCM envelope (per-secret random DEK, per-tenant KEK via
HKDF-SHA256 from one master key). Master key from VaultOptions.MasterKeyBase64 (IOptions/config); KeyId
hardcoded "local-envelope-v1"; VaultSecretBlob already PERSISTS KeyId but RevealAsync ignores it (single
key). Gaps to close in PR4:
- Master key custody: support a secret-FILE source (Docker/K8s mounted path) + env, forbid a literal key
  in committed appsettings, fail-fast when unset/short. (env already works via Vault__ override.)
- Key rotation: introduce a versioned keyring (active key id + id->key map). New/rotated secrets encrypt
  with the ACTIVE key; RevealAsync decrypts with the key named by blob.KeyId (already stored) so old blobs
  keep working across a master-key roll; add a re-wrap path. This is the load-bearing change.
- Reveal audit: tamper-evident (hash-chained) record on RevealAsync — tenant/name/when, NEVER the secret.
- Rotation runbook in docs/.
- Confirm .gitignore covers the secret-file pattern; no key ever committed.

- [ ] Implement per the above via design-workflow -> implement -> review-workflow -> PR (after PR3 lands).
