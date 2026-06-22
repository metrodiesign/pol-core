#!/bin/sh
# One-shot migrate/bootstrap: (1) create DB principals (idempotent) as sa, (2) apply the EF migrations
# (schema + the RLS security policy + the reveal-audit proc). Must run to completion BEFORE the app hosts
# start (compose orders this via depends_on: service_completed_successfully). Runs from the source tree (/src).
set -eu

: "${DB_SERVER:?set DB_SERVER}"
: "${DB_NAME:?set DB_NAME}"
: "${MSSQL_SA_PASSWORD:?set MSSQL_SA_PASSWORD (bootstrap-only)}"
: "${POL_APP_PASSWORD_FILE:?}"
: "${POL_ADMIN_PASSWORD_FILE:?}"
: "${POL_WORKER_PASSWORD_FILE:?}"

APP_PW="$(cat "$POL_APP_PASSWORD_FILE")"
ADMIN_PW="$(cat "$POL_ADMIN_PASSWORD_FILE")"
WORKER_PW="$(cat "$POL_WORKER_PASSWORD_FILE")"

echo "[migrate] bootstrapping DB principals (idempotent)..."
sqlcmd -S "$DB_SERVER" -U sa -P "$MSSQL_SA_PASSWORD" -C -b \
  -v DbName="$DB_NAME" \
     POL_APP_PASSWORD="$APP_PW" \
     POL_ADMIN_PASSWORD="$ADMIN_PW" \
     POL_WORKER_PASSWORD="$WORKER_PW" \
  -i docker/bootstrap/01-principals.sql

echo "[migrate] applying EF migrations (schema + RLS policy + reveal-audit)..."
export POL_DESIGN_SQL="Server=${DB_SERVER};Database=${DB_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};Encrypt=True;TrustServerCertificate=True"
dotnet ef database update --context ProducerDbContext \
  --project src/BuildingBlocks/BuildingBlocks.Infrastructure \
  --startup-project src/Hosts/Api

echo "[migrate] done."
