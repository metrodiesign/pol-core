#!/bin/sh
# One-shot migrate/bootstrap: (0) wait for each DB tier to become reachable (bounded retry — the DB tiers
# are separate hosts now, not same-compose services `depends_on` can gate on), (1) create the DB principal
# (idempotent) as sa, (2) bootstrap hippodb (own instance, own pol_app LOGIN) and mammothdb (own instance,
# own pol_app LOGIN), (3) apply the EF migrations (schema + the pol_app grant matrix). Must run to
# completion BEFORE the app hosts start (compose orders this via depends_on:
# service_completed_successfully). Runs from the source tree (/src).
set -eu

: "${DB_SERVER:?set DB_SERVER}"
: "${DB_NAME:?set DB_NAME}"
: "${HIPPO_DB_SERVER:?set HIPPO_DB_SERVER}"
: "${MAMMOTH_DB_SERVER:?set MAMMOTH_DB_SERVER}"
: "${MSSQL_SA_PASSWORD:?set MSSQL_SA_PASSWORD (bootstrap-only)}"
: "${POL_APP_PASSWORD_FILE:?}"
: "${DB_PORT:=1433}"
: "${HIPPO_DB_PORT:=1433}"
: "${MAMMOTH_DB_PORT:=1433}"
: "${DB_CONNECT_RETRIES:=30}"
: "${DB_CONNECT_RETRY_DELAY_SECONDS:=5}"

APP_PW="$(cat "$POL_APP_PASSWORD_FILE")"

# sqlcmd has no ServerCertificate= pin (unlike Microsoft.Data.SqlClient) — install the mounted
# CA (PEM) into the OS trust store at RUNTIME so `sqlcmd -N` validates against it. Build-time
# install can't work: images are built in CI where the operator's CA secret doesn't exist, and
# deploy pulls with `--no-build`. This container runs as root. No-op when unset (publicly-trusted CA).
CA_TRUST_DIR="${CA_TRUST_DIR:-/usr/local/share/ca-certificates}"
if [ -n "${DB_CA_CERTIFICATE_FILE:-}" ]; then
    cp "$DB_CA_CERTIFICATE_FILE" "${CA_TRUST_DIR}/db-tier-ca.crt"
    update-ca-certificates >/dev/null
fi

# Bounded-retry reachability probe against one server:port. sqlcmd doesn't distinguish network vs TLS
# failure by exit code alone — classify exhaustion from its error text so operator-only logs carry the
# real signal (external /health/ready stays generic either way). Called once per DB tier below.
wait_for_db() { # $1=server $2=port
    wfd_server="$1"; wfd_port="$2"
    echo "[migrate] waiting for DB tier at ${wfd_server}:${wfd_port} (up to ${DB_CONNECT_RETRIES} attempts, ${DB_CONNECT_RETRY_DELAY_SECONDS}s apart)..."
    wfd_i=1
    while true; do
        if PROBE_OUT="$(sqlcmd -S "${wfd_server},${wfd_port}" -U sa -P "$MSSQL_SA_PASSWORD" -N -Q "SELECT 1" 2>&1)"; then
            echo "[migrate] DB tier reachable after ${wfd_i} attempt(s)."
            break
        fi
        if [ "$wfd_i" -ge "$DB_CONNECT_RETRIES" ]; then
            if printf '%s' "$PROBE_OUT" | grep -qi "certificate\|SSL Provider\|TLS"; then
                echo "[migrate] DB tier unreachable after ${DB_CONNECT_RETRIES} attempts: TLS validation failure" >&2
            else
                echo "[migrate] DB tier unreachable after ${DB_CONNECT_RETRIES} attempts: network unreachable" >&2
            fi
            exit 1
        fi
        wfd_i=$((wfd_i + 1))
        sleep "$DB_CONNECT_RETRY_DELAY_SECONDS"
    done
}

wait_for_db "$DB_SERVER" "$DB_PORT"

echo "[migrate] bootstrapping DB principal (idempotent)..."
sqlcmd -S "${DB_SERVER},${DB_PORT}" -U sa -P "$MSSQL_SA_PASSWORD" -N -b \
  -v DbName="$DB_NAME" \
     POL_APP_PASSWORD="$APP_PW" \
  -i docker/bootstrap/01-principals.sql

# products-sp-gateway REQ-3.2/3.3 + external-sim-separate-containers: hippodb/mammothdb stand in for the
# upstream systems we do not own yet, so GET /products has nothing to read without them. Each now runs on
# its OWN SQL Server instance (own pol_app LOGIN — the bootstrap files create it themselves, since
# 01-principals.sql above never touches these instances) and must exist before the API serves traffic
# (compose gates that with depends_on: service_completed_successfully) and before any test host boots
# against either server, which is what keeps parallel hosts from racing on CREATE DATABASE.
echo "[migrate] bootstrapping simulated upstream database hippodb (idempotent)..."
wait_for_db "$HIPPO_DB_SERVER" "$HIPPO_DB_PORT"
sqlcmd -S "${HIPPO_DB_SERVER},${HIPPO_DB_PORT}" -U sa -P "$MSSQL_SA_PASSWORD" -N -b \
  -v POL_APP_PASSWORD="$APP_PW" \
  -i docker/bootstrap/02-hippo-sim.sql

echo "[migrate] bootstrapping simulated upstream database mammothdb (idempotent)..."
wait_for_db "$MAMMOTH_DB_SERVER" "$MAMMOTH_DB_PORT"
sqlcmd -S "${MAMMOTH_DB_SERVER},${MAMMOTH_DB_PORT}" -U sa -P "$MSSQL_SA_PASSWORD" -N -b \
  -v POL_APP_PASSWORD="$APP_PW" \
  -i docker/bootstrap/03-mammoth-sim.sql

echo "[migrate] applying EF migrations (schema + pol_app grant matrix)..."
# Same DB_PORT/trust wiring as docker/entrypoint.sh: pinned CA cert -> Encrypt=Strict, else
# Encrypt=True;TrustServerCertificate=False (OS trust store) — no input can make
# TrustServerCertificate be True.
if [ -n "${DB_CA_CERTIFICATE_FILE:-}" ]; then
    export POL_DESIGN_SQL="Server=${DB_SERVER},${DB_PORT};Database=${DB_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};Encrypt=Strict;ServerCertificate=${DB_CA_CERTIFICATE_FILE};HostNameInCertificate=${DB_SERVER}"
else
    export POL_DESIGN_SQL="Server=${DB_SERVER},${DB_PORT};Database=${DB_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};Encrypt=True;TrustServerCertificate=False"
fi
dotnet ef database update --context PolDbContext \
  --project src/BuildingBlocks/BuildingBlocks.Infrastructure \
  --startup-project src/Hosts/Api

echo "[migrate] done."
