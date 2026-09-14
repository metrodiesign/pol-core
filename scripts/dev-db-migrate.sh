#!/usr/bin/env bash
set -euo pipefail

# Local dev DB bring-up: apply EF migrations.
#
# WHY this exists: the API host deliberately does not construct or register the migration-only
# `PolDbContext`. A fresh dev DB — or any DB after a new migration merges — therefore needs an
# explicit operator run before `dotnet watch`. Run this once after `docker compose up -d` (and after
# pulling a new migration) BEFORE `dotnet watch`. Idempotent: `ef database update` is a no-op when current.

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

echo "[dev-db-migrate] done — API can now boot (dotnet watch)."
