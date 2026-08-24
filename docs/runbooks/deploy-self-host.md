# Self-host Deployment Runbook

> Current runbook for the complete self-host release. Production deploy must pass staging, migration, authentication
> and release assembly gates.

## 1. Non-negotiable gates

- Deploy staging before production.
- Release from immutable tag `vX.Y.Z` with `CHANGELOG.md`.
- CI build, tests, integration, secret scan and spec trace must pass.
- SQL Server must be 2025 build `17.0.4045.5` or newer; database compatibility level 170.
- Fresh baseline accepts empty target only. Existing tables, objects or migration history fail before DDL.
- Production reset requires human approval, exact target, verified backup URI/checksum and rollback evidence.
- Stop old API instances before applying a schema that changes identity uniqueness from `Subject` to
  `(Provider, Subject)`; do not run old and new identity code against the upgraded DB together.
- Never deploy production Friday evening or before long holiday except approved emergency hotfix.
- Never run migration `Down` in production. Production rollback restores verified backup.

## 2. Required infrastructure

Three SQL Server tiers:

| Tier | Database | Runtime principal |
|---|---|---|
| Core | `VCentralPay` | `pol_app` |
| Motor source simulation | `hippodb` | `hippo_app` |
| Non-Motor source simulation | `mammothdb` | `mammoth_app` |

Production hosts should use managed/external SQL tiers. Compose DB services are local/integration only. Image reference is
pinned to SQL Server 2025 CU5 plus immutable digest in compose and CI.

Create local secret files under `./secrets/` with mode `0600`. Never commit them. Required production files include
runtime DB passwords, OIDC client secrets, PSP credentials where configured, vault keyring files and
`audit_anchor_signing_key`. Generate that audit key independently with `openssl rand -base64 32`; never reuse a Vault
or OIDC key. `.env` and `.env.*` remain gitignored; only `.env.example` is committed.

Vault uses file-backed keyring in production. Keep active key ID and historical decrypt keys available through rotation.
Do not set legacy `Vault__MasterKeyBase64` together with keyring settings.

Audit high-water checkpoints live in the separate `audit-anchors` volume. Back up and retain that append-only artifact
with database evidence. A database restore whose audit heads fall behind signed checkpoints intentionally leaves
`/health/ready` unhealthy; restore matching database and anchor artifacts instead of deleting or rewriting the anchor.

## 3. Release evidence inputs

Production migrator refuses DB access unless all values pass:

```text
DEPLOYMENT_ENVIRONMENT=Production
RESET_TARGET=<DB_SERVER>:<DB_PORT>/VCentralPay
RESET_APPROVED=true
BACKUP_ARTIFACT_URI=<durable URI>
BACKUP_SHA256=<64 hex characters>
RESET_APPROVAL_EVIDENCE=<approval URI>
ROLLBACK_EVIDENCE=<rollback/rehearsal URI>
```

`RESET_TARGET` must exactly match configured `DB_SERVER`, `DB_PORT` and `DB_NAME=VCentralPay`. Evidence URI points to
immutable ticket/artifact record; do not put secrets in URI. Backup checksum must be calculated after backup completes,
stored beside release evidence and verified before staging rehearsal and production approval.

Run local validation:

```bash
scripts/check-release-evidence.sh
```

GitHub `Release assembly gate` asks for same values. Tagต้องมีหัวข้อ `## vX.Y.Z` ตรงกันใน `CHANGELOG.md`.
`production-assembly` depends on successful `staging` job, exact protected `vars.RESET_TARGET` match และ protected
production environment approval. Workflow validates evidence only; actual deploy remains separate approved action.

## 4. Staging reset rehearsal

Use isolated staging DB. Never point rehearsal at production.

1. Record target identity, owner, release tag and maintenance window.
2. Create backup; record durable URI + SHA-256; verify restore readability.
3. Record explicit reset approval.
4. Stop application traffic and background dispatchers.
5. DBA resets only approved staging `VCentralPay` target using organization procedure.
6. Run all 21 migrations in timestamp order through `20260823132337_Tier0WorkforceEmailIdentity`.
7. Run `WorkforceIdentityMigrator`; require exit `0` before API startup.
8. Run `docker/bootstrap/assert-fresh-db.sql`.
9. Start API and run smoke path below.
10. Stop traffic, restore pre-reset backup, verify health/read contract, then reset/apply again for final staging state.
11. Attach logs, catalog assertion, smoke result and rollback timing to `STAGING_EVIDENCE_URI`.

Required staging smoke:

- `/health/live` and `/health/ready` healthy.
- merchant-user authentication + CSRF works.
- Products source query works.
- Cart create/add/read/update/remove/clear works with `productCode`/`variantCode`.
- `POST /api/v1/orders` returns `201`, `Location`, `Pending` and server-priced amount.
- Cart becomes `CheckedOut`; repeated create is `409`.
- Payment session/redirect/webhook confirms Order `Paid` once.
- Failed/Expired retry and terminal cancel behavior match contract.
- Retired Checkout/policy routes return `404`.
- Cross-merchant read/write remains denied.

Repository rehearsal evidence: `.ai/specs/merchant-commerce-erd-reset/STAGING-EVIDENCE.md`. Production approval needs
fresh environment-specific evidence URI; repository evidence is not substitute.

## 5. Microsoft Entra rollout preflight

Production compose requires Microsoft workforce OIDC for Admin and Google OIDC for Merchant user. Merchant Microsoft
remains opt-in. Each plane uses separate client, secret, callback, scheme and cookie.

### 5.1 Identity and frontend contract

Complete these checks before changing production:

1. Admin SPA approval, rejection and registration-history routes send internal `merchantUserId` GUID.
2. Admin SPA does not send Entra `oid`, Google `sub` or raw `Subject` as `{merchantUserId}`.
3. Existing Google identities remain Google identities; migration backfills `Provider=google`.
4. Tier 0 uses canonical `viriyah.co.th` email as Microsoft subject; it does not read `oid` or `roles`.
5. Active unbound Admin with matching canonical email binds in place; bound-other, Suspended or ambiguous owner fails closed.
6. New Tier 0 identity creates `Active + Scoped` Admin without role or MerchantAccess.
7. Promote corporate Super through Admin management API before production; Microsoft bootstrap allowlist does not exist.
8. Tier 1 has a full test identity that can register, wait for approval, receive approval and login again.

Corporate email can be renamed or reused. Lifecycle owner must suspend former owner and revoke sessions before reuse;
authorization never transfers automatically. Follow `admin-workforce-jit-rollout.md` for cutover and recovery rules.

### 5.2 Runtime configuration

| Tier | Compose variables | Mounted secret file | Public callback |
|---|---|---|---|
| Tier 0 Admin | `ADMIN_ENTRA_CLIENT_ID`, `ADMIN_ENTRA_AUTHORITY` | `secrets/admin_entra_client_secret` | `https://<api-origin>/api/v1/admins/auth/microsoft/callback` |
| Tier 1 Merchant | `MERCHANT_ENTRA_CLIENT_ID`, `MERCHANT_ENTRA_AUTHORITY` | `secrets/merchant_entra_client_secret` | `https://<api-origin>/api/v1/merchants/auth/microsoft/callback` |

Authority rules:

- Admin: `https://login.microsoftonline.com/<tenant-id>/v2.0`.
- Merchant: `https://<tenant>.ciamlogin.com/<tenant-id>/v2.0`.
- Authority must pin one tenant and end with `/v2.0`.
- `/common`, `/organizations` and `/consumers` are forbidden.

Secret rules:

- Store client secret `Value`, never `Secret ID`.
- Create each secret file with one value and no explanatory text.
- Set file mode `0600`; never put secret value in `.env`, compose, image, log, ticket or release evidence.
- Revoke any secret previously shown in chat, terminal transcript, screenshot or source history.
- Admin Microsoft secret must be non-empty in Production. Optional Merchant Microsoft secret may be an empty
  placeholder only while its Client ID is blank.

Verify file presence without printing content:

```bash
test -s secrets/admin_entra_client_secret
test -f secrets/merchant_entra_client_secret
# เมื่อเปิด Merchant Microsoft เท่านั้น:
test -s secrets/merchant_entra_client_secret
```

Azure application configuration:

1. Register each callback as platform type `Web`, not `SPA` or public client.
2. Match scheme, host, port, path and case exactly.
3. Tier 1 sign-up/sign-in user flow must be linked to the Merchant application.
4. Tier 1 must enable the intended identity provider and Email one-time passcode policy.
5. Tier 1 token must contain `oid` and either `email` or email-shaped `preferred_username`; `tid` is required when
   `AllowedTenants` is configured.
6. Test through the application's `/login` endpoint; Portal `Run user flow` alone does not prove backend correlation,
   code redemption or session creation.

### 5.3 Reverse proxy and cookies

OIDC creates callback URLs from the browser-facing request. The reverse proxy must:

- terminate TLS on the public HTTPS origin;
- preserve the public `Host`;
- send `X-Forwarded-Proto=https` and `X-Forwarded-Host`;
- be the exact trusted proxy/network configured by `FORWARDED_HEADERS_KNOWN_NETWORK` or `KnownProxies`;
- allow `POST` to both Microsoft callback paths because OIDC uses `response_mode=form_post`;
- preserve `Set-Cookie` and callback `Cookie` headers without rewriting their security attributes;
- never trust wildcard proxy CIDRs such as `0.0.0.0/0` or `::/0`.

Admin and Merchant SPA origins must match `ADMIN_FRONTEND_ORIGIN` and `MERCHANT_USER_FRONTEND_ORIGIN`. Outside
Development, Data Protection keys must persist in the control-plane DB and be shared by every API instance; otherwise
correlation cookies and sessions fail across restart/instance boundaries.

### 5.4 Database preflight

Run these read-only queries against the exact target before backup and migration:

```sql
SELECT COUNT_BIG(*) AS OrphanTargetAudits
FROM merch.RegistrationAudits ra
LEFT JOIN merch.Users u ON u.Subject = ra.TargetSubject
WHERE u.Id IS NULL;

SELECT COUNT_BIG(*) AS OrphanAdminActors
FROM merch.RegistrationAudits ra
LEFT JOIN admin.Users a ON a.Subject = ra.ActorSubject
WHERE ra.Action IN (N'approved', N'rejected', N'revealed', N'suspended')
  AND a.Id IS NULL;

SELECT COUNT_BIG(*) AS RegistrationAuditRows
FROM merch.RegistrationAudits;
```

Both orphan counts must be `0`. The migration stops before completing if either count is non-zero. Record the audit-row
count to size the migration window; the migration backfills `TargetUserId` and `ActorAdminId`, changes identity indexes
to `(Provider, Subject)`, then adds a foreign key.

Tier 0 canonical-email cutover has an additional fail-closed preflight in `WorkforceIdentityMigrator`: invalid email,
duplicate canonical owner, unknown subject or snapshot drift returns non-zero without partial conversion. Use
`admin-workforce-jit-rollout.md` as the detailed backup, maintenance-window and rollback procedure.

Rollout order:

1. Prove the same migration and OIDC flow on a restored staging copy.
2. Verify frontend contract and both public callback URIs.
3. Close incoming traffic and stop every old API/background dispatcher.
4. Create and verify the production backup and checksum.
5. Apply all migrations once with the one-shot `migrate` service.
6. Start only the new API version and verify health.
7. Open traffic gradually and run the authentication smoke below.

Do not use a rolling mixed-version deployment for this migration. After two providers share the same subject, migration
`Down` intentionally blocks because the old subject-only unique index cannot be restored. Production rollback restores
the verified backup and previous compatible application tag.

### 5.5 Staging authentication smoke

Tier 1 must prove the complete lifecycle, not only the Entra consent page:

1. Start at `/api/v1/merchants/auth/microsoft/login`.
2. Complete email/OTP and consent in one browser session.
3. New identity redirects to Merchant SPA `/register?ticket=<redacted>`.
4. Submit registration and verify `201` with `PendingApproval`.
5. Admin approves using internal `merchantUserId`.
6. Login again and verify dashboard redirect, session cookie and authenticated
   `/api/v1/merchants/users/me` response.

Tier 0 must prove:

1. Start at `/api/v1/admins/auth/microsoft/login?returnTo=/dashboard`.
2. Login with workforce-tenant employee account.
3. Verify tenant/issuer rejection using an account outside the pinned tenant.
4. Verify dashboard redirect, admin session and permission-scoped `/api/v1/admins/me` response.

Capture status, `Location`, correlation ID, Entra error code and timestamp only. Redact query tickets, authorization code,
state, nonce, cookies, ID token, OTP and client secret.

## 6. Deploy

Render config before changing environment:

```bash
docker compose -f docker-compose.prod.yml config
```

Deploy approved immutable release:

```bash
docker compose -f docker-compose.prod.yml up -d --build
```

Order is `migrate` successful completion, then `api`. Migrator bootstraps principals, applies EF migrations, runs
`WorkforceIdentityMigrator`, then exits only after identity verification succeeds. API never auto-migrates outside
Development.

Verify:

```bash
docker compose -f docker-compose.prod.yml logs migrate
docker compose -f docker-compose.prod.yml ps
curl -fsS http://localhost:5100/health/live
curl -fsS http://localhost:5100/health/ready
```

Expected migrator end marker: `[migrate] done.`. Failure blocks API startup. Do not bypass failed migration, catalog
assertion, secret check or health check.

After boot, rerun production-safe smoke reads and one approved synthetic transaction. Do not use real customer PII.
Record deployment SHA/tag, migration history, health output, smoke result and operator in release evidence.

## 7. Rollback

Trigger rollback when migration, health, authorization, payment or smoke gate fails.

1. Stop new traffic and background dispatchers.
2. Preserve logs/evidence; do not retry destructive reset.
3. If DB baseline/reset started, DBA restores exact verified backup identified by `BACKUP_ARTIFACT_URI` and confirms
   checksum `BACKUP_SHA256`.
4. Deploy previous immutable application tag compatible with restored DB.
5. Verify health, auth, merchant isolation and read-only smoke.
6. Record duration/result at `ROLLBACK_EVIDENCE`; keep production closed if verification fails.

Non-production only may prove dependency-safe `Down` with:

```bash
dotnet ef database update 0 --context PolDbContext \
  --project src/BuildingBlocks/BuildingBlocks.Infrastructure \
  --startup-project src/Hosts/Api
```

Production rollback is backup restore, never this command.

## 8. Seq and operational telemetry

Seq is required startup dependency for denial/authz telemetry. Keep it internal, authenticated and retained per policy.
Alert on authorization denials, reconciliation-required payment events, migration refusal, outbox poison and repeated
dependency failure. Logs must not contain token, password, credential, KYC object key or customer PII.

## 9. Credential rotation

Runtime uses `pol_app`; `sa` is bootstrap/migration only. Rotate DB passwords and vault keys through secret manager,
update secret files atomically, restart service and verify health. Keep previous vault decrypt key until all blobs are
re-encrypted and audited. Credential incident requires rotate/revoke; deleting Git history is insufficient.

## 10. Consumer cutover

Frontend changes happen in separate frontend repositories and PRs. Contract package:

- `.ai/specs/merchant-commerce-erd-reset/openapi-cart-order.yaml`
- `.ai/specs/merchant-commerce-erd-reset/FE-MIGRATION.md`

Cutover is big-bang. No Checkout/policy compatibility routes, aliases or overlap window.
