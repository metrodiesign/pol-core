# Self-host Deployment Runbook

> Current runbook for merchant-commerce ERD reset. Production deploy must pass staging and release assembly gate.

## 1. Non-negotiable gates

- Deploy staging before production.
- Release from immutable tag `vX.Y.Z` with `CHANGELOG.md`.
- CI build, tests, integration, secret scan and spec trace must pass.
- SQL Server must be 2025 build `17.0.4045.5` or newer; database compatibility level 170.
- Fresh baseline accepts empty target only. Existing tables, objects or migration history fail before DDL.
- Production reset requires human approval, exact target, verified backup URI/checksum and rollback evidence.
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
runtime DB passwords, OIDC client secrets, PSP credentials where configured and vault keyring files. `.env` and
`.env.*` remain gitignored; only `.env.example` is committed.

Vault uses file-backed keyring in production. Keep active key ID and historical decrypt keys available through rotation.
Do not set legacy `Vault__MasterKeyBase64` together with keyring settings.

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
6. Run bootstrap and four migrations: `InitialSchema -> SecurityObjects -> SeedData -> OneBasedPersistedEnumStorage`.
7. Run `docker/bootstrap/assert-fresh-db.sql`.
8. Start API and run smoke path below.
9. Stop traffic, restore pre-reset backup, verify health/read contract, then reset/apply again for final staging state.
10. Attach logs, catalog assertion, smoke result and rollback timing to `STAGING_EVIDENCE_URI`.

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

## 5. Deploy

Render config before changing environment:

```bash
docker compose -f docker-compose.prod.yml config
```

Deploy approved immutable release:

```bash
docker compose -f docker-compose.prod.yml up -d --build
```

Order is `migrate` successful completion, then `api`. Migrator bootstraps principals, checks engine/build/compatibility,
applies baseline and exits. API never auto-migrates outside Development.

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

## 6. Rollback

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

## 7. Seq and operational telemetry

Seq is required startup dependency for denial/authz telemetry. Keep it internal, authenticated and retained per policy.
Alert on authorization denials, reconciliation-required payment events, migration refusal, outbox poison and repeated
dependency failure. Logs must not contain token, password, credential, KYC object key or customer PII.

## 8. Credential rotation

Runtime uses `pol_app`; `sa` is bootstrap/migration only. Rotate DB passwords and vault keys through secret manager,
update secret files atomically, restart service and verify health. Keep previous vault decrypt key until all blobs are
re-encrypted and audited. Credential incident requires rotate/revoke; deleting Git history is insufficient.

## 9. Consumer cutover

Frontend changes happen in separate frontend repositories and PRs. Contract package:

- `.ai/specs/merchant-commerce-erd-reset/openapi-cart-order.yaml`
- `.ai/specs/merchant-commerce-erd-reset/FE-MIGRATION.md`

Cutover is big-bang. No Checkout/policy compatibility routes, aliases or overlap window.
