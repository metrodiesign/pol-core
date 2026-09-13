#!/usr/bin/env bash
set -euo pipefail

# Local dev DB bring-up: apply EF migrations, then complete the workforce identity migration.
#
# WHY this exists: the API host deliberately does not construct or register the migration-only
# `PolDbContext`. A fresh dev DB — or any DB after a new identity migration merges — therefore needs
# an explicit operator run before `dotnet watch`; the script applies EF migrations and then runs
# `WorkforceIdentityMigrator` (the API host still deliberately does NOT run that tool).
# Without this step the API can fail startup with:
#   "Admin Microsoft historical identity migration is incomplete."
# Run this once after `docker compose up -d` (and after pulling a new migration) BEFORE `dotnet watch`.
# It mirrors docker/migrate-entrypoint.sh's schema-then-tool order for the local host.
#
# Idempotent: `ef database update` is a no-op when current; the migrator re-verifies an already
# completed migration instead of re-running it.

cd "$(dirname "$0")/.."

# API and dotnet ef do not read .env automatically (runbook Section 5). Source it into THIS shell so
# the Migrator connection and POL_DESIGN_SQL reach both tools. Never print the values.
if [[ -f .env ]]; then
  set -a
  # shellcheck disable=SC1091
  . ./.env
  set +a
else
  echo "[dev-db-migrate] .env not found — copy .env.example and set local values first (runbook Section 3.1)." >&2
  exit 1
fi

echo "[dev-db-migrate] applying EF migrations (PolDbContext)..."
dotnet ef database update --context PolDbContext \
  --project src/Infrastructure \
  --startup-project src/Api

echo "[dev-db-migrate] completing workforce identity migration..."
# Fresh/empty Admin inventory completes with zero counts and needs no manifest. A populated legacy
# inventory needs the six protected WORKFORCE_* first-run inputs + strict manifest — see
# docs/runbooks/admin-workforce-jit-rollout.md; this script does not automate that path.
if ! dotnet run --project src/Infrastructure/Infrastructure.csproj; then
  echo "[dev-db-migrate] migrator did not complete. If this DB has existing Admin rows that need an" >&2
  echo "  authoritative Entra manifest, follow docs/runbooks/admin-workforce-jit-rollout.md." >&2
  echo "  For a stale local demo DB with no real identities, clear the invalid Admin rows first." >&2
  exit 1
fi

echo "[dev-db-migrate] done — API can now boot (dotnet watch)."
