> Status: unknown
# Handoff: Merchant-Commerce ERD Reset

> From: Codex `/root`   To: human review   Date: 2026-08-07

## Task Summary

Implemented approved spec `.ai/specs/merchant-commerce-erd-reset` tasks 1-9 covering ERD model reset, KYC lifecycle,
typed native JSON, generic Cart, direct Cart-to-Order, serialized payment lifecycle, Checkout/policy retirement, guarded
SQL Server 2025 fresh baseline and consumer/release gates. Scope: REQ-1 through REQ-13.

## Current Status

Implementation complete. All task checkboxes and Evidence blocks filled. Build, offline tests, isolated fresh SQL
Integration suite, secret scan and spec trace pass. No commit, push, PR or production deployment performed.

## Files Changed

- `.ai/specs/merchant-commerce-erd-reset/*` — created — approved requirements/design/tasks, FE/OpenAPI contract,
  staging evidence and handoff
- `src/Modules/*`, `src/Persistence/*`, `src/Hosts/Api/*`, `src/Contracts/*`, `src/SharedKernel/*` — edited/created/deleted — ERD/domain/API/persistence reset
- `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/*` — replaced — exactly three fresh migrations + snapshot
- `tests/*` — edited/created/deleted — unit, architecture, Host/OpenAPI and live SQL coverage
- `docker/*`, `docker-compose*.yml` — edited/created/deleted — SQL 2025 pin, bootstrap/assertion, production reset guard
- `.github/workflows/ci.yml`, `.github/workflows/release-gate.yml` — edited/created — CI and staging/production assembly gates
- `scripts/*` — edited/created/deleted — migration lineage and release evidence guards
- `.ai/shared/*`, `docs/reference/*`, `docs/runbooks/*`, `README.md`, `.env.example`, `CHANGELOG.md` — edited/created — current architecture/consumer/operations docs
- `.codex/config.toml` — pre-existing user modification; intentionally untouched

## Important Decisions

- Big-bang reset: no compatibility alias, legacy migration path or frontend overlap window.
- Direct Cart-to-Order uses source revalidation before DB transaction and owner ports/shared UoW for atomic commit.
- Exactly five typed native JSON columns; scalar searchable/sensitive fields remain outside JSON.
- Payment lifecycle serialized on tenant-scoped Order row lock; conflicting terminal Paid creates reconciliation evidence.
- Production fresh reset remains human-operated: exact target + backup/checksum + approval + rollback evidence required before DB access.
- Release workflow validates immutable tag/evidence only; deployment is separate protected action.

## Constraints

- Do not reset existing `VCentralPay`; baseline accepts empty target only.
- Do not commit secrets or `.env*`; do not log credentials, KYC keys or PII.
- Do not push direct to `main`/`develop`, force push or commit without review.
- Production must pass staging, protected environment approval and backup-restore plan.
- Frontend repositories were out of scope and remain unchanged.

## Tests Run

- `dotnet build pol-core.slnx --no-restore -warnaserror` -> 0 warnings / 0 errors
- `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"` -> 1,562 passed / 0 failed
- isolated fresh DB: `dotnet test tests/Integration.Tests/Integration.Tests.csproj --no-build --filter "Category=Integration"` -> 144 passed / 0 failed
- `docker/bootstrap/assert-fresh-db.sql` -> OK on fresh scratch DB
- `bash docker/bootstrap/assert-fresh-db.test.sh` -> OK
- `bash docker/migrate-entrypoint.test.sh` -> 51 passed / 0 failed
- `bash scripts/check-release-evidence.test.sh` -> OK
- `scripts/check-migration-lineage.sh` -> OK
- `scripts/spec-trace.sh merchant-commerce-erd-reset` -> 264/264 covered, EARS lint passed
- `.ai/bin/check-secrets.sh --all` -> passed
- `git diff --check`, shell syntax and YAML parse -> passed

## Known Issues

- Existing local `.env` points to legacy `VCentralPay`; Integration tests fail there because it intentionally was not reset.
  Use isolated fresh baseline target. Fresh scratch run passed and scratch target was removed.
- `CHANGELOG.md` remains `Unreleased`; release owner must add exact `## vX.Y.Z` heading before release gate.
- External staging/production actions were not authorized or performed. Repository rehearsal is mechanism evidence only.

## Next Recommended Agent

Human review, then code/security/architecture sign-off before commit and PR.

## Next Steps

1. Read this handoff plus `requirements.md`, `design.md`, `tasks.md`; inspect full working-tree diff.
2. Run gates from Evidence on reviewer-owned fresh SQL target.
3. Add versioned changelog, create `codex/` branch if needed, commit only after review, then open PR; never push main/develop.
