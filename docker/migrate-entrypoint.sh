#!/bin/sh
# One-shot migrate/bootstrap: (0) wait for the DB tier to become reachable (bounded retry —
# the DB tier is a separate host now, not a same-compose service `depends_on` can gate on),
# (1) create the DB principal (idempotent) as sa, (2) apply the EF migrations
# (schema + the pol_app grant matrix). Must run to completion BEFORE the app hosts
# start (compose orders this via depends_on: service_completed_successfully). Runs from the source tree (/src).
set -eu

: "${DB_SERVER:?set DB_SERVER}"
: "${DB_NAME:?set DB_NAME}"
: "${MSSQL_SA_PASSWORD:?set MSSQL_SA_PASSWORD (bootstrap-only)}"
: "${POL_APP_PASSWORD_FILE:?}"
: "${DB_PORT:=1433}"
: "${DB_CONNECT_RETRIES:=30}"
: "${DB_CONNECT_RETRY_DELAY_SECONDS:=5}"

APP_PW="$(cat "$POL_APP_PASSWORD_FILE")"

echo "[migrate] waiting for DB tier at ${DB_SERVER}:${DB_PORT} (up to ${DB_CONNECT_RETRIES} attempts, ${DB_CONNECT_RETRY_DELAY_SECONDS}s apart)..."
i=1
while true; do
    if PROBE_OUT="$(sqlcmd -S "${DB_SERVER},${DB_PORT}" -U sa -P "$MSSQL_SA_PASSWORD" -N -Q "SELECT 1" 2>&1)"; then
        echo "[migrate] DB tier reachable after ${i} attempt(s)."
        break
    fi
    if [ "$i" -ge "$DB_CONNECT_RETRIES" ]; then
        # sqlcmd doesn't distinguish network vs TLS failure by exit code alone — classify from
        # its error text so operator-only logs carry the real signal (external /health/ready
        # stays generic either way).
        if printf '%s' "$PROBE_OUT" | grep -qi "certificate\|SSL Provider\|TLS"; then
            echo "[migrate] DB tier unreachable after ${DB_CONNECT_RETRIES} attempts: TLS validation failure" >&2
        else
            echo "[migrate] DB tier unreachable after ${DB_CONNECT_RETRIES} attempts: network unreachable" >&2
        fi
        exit 1
    fi
    i=$((i + 1))
    sleep "$DB_CONNECT_RETRY_DELAY_SECONDS"
done

echo "[migrate] bootstrapping DB principal (idempotent)..."
sqlcmd -S "${DB_SERVER},${DB_PORT}" -U sa -P "$MSSQL_SA_PASSWORD" -N -b \
  -v DbName="$DB_NAME" \
     POL_APP_PASSWORD="$APP_PW" \
  -i docker/bootstrap/01-principals.sql

echo "[migrate] applying EF migrations (schema + pol_app grant matrix)..."
# Same DB_PORT/trust wiring as docker/entrypoint.sh: pinned CA cert -> Encrypt=Strict, else
# Encrypt=True;TrustServerCertificate=False (OS trust store) — no input can make
# TrustServerCertificate be True.
if [ -n "${DB_CA_CERTIFICATE_FILE:-}" ]; then
    export POL_DESIGN_SQL="Server=${DB_SERVER},${DB_PORT};Database=${DB_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};Encrypt=Strict;Certificate=${DB_CA_CERTIFICATE_FILE};HostNameInCertificate=${DB_SERVER}"
else
    export POL_DESIGN_SQL="Server=${DB_SERVER},${DB_PORT};Database=${DB_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};Encrypt=True;TrustServerCertificate=False"
fi
dotnet ef database update --context PolDbContext \
  --project src/BuildingBlocks/BuildingBlocks.Infrastructure \
  --startup-project src/Hosts/Api

echo "[migrate] done."
