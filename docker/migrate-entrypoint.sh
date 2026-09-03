#!/bin/sh
# One-shot migrate/bootstrap: (0) wait for each DB tier to become reachable (bounded retry — the DB tiers
# are separate hosts now, not same-compose services `depends_on` can gate on), (1) create the DB principal
# (idempotent) as sa, (2) bootstrap hippodb (own instance, own hippo_app LOGIN) and mammothdb (own instance,
# own mammoth_app LOGIN — each sim tier has its own principal and its own password, sim-db-separate-logins),
# (3) apply the committed idempotent schema script docker/migrations/schema.sql (schema + the pol_app grant matrix). Must run to
# completion BEFORE the app hosts start (compose orders this via depends_on:
# service_completed_successfully). Runs from the source tree (/src).
set -eu

: "${DB_SERVER:?set DB_SERVER}"
: "${DB_NAME:?set DB_NAME}"
: "${HIPPO_DB_SERVER:?set HIPPO_DB_SERVER}"
: "${MAMMOTH_DB_SERVER:?set MAMMOTH_DB_SERVER}"
: "${MSSQL_SA_PASSWORD:?set MSSQL_SA_PASSWORD (bootstrap-only)}"
: "${POL_APP_PASSWORD_FILE:?}"
: "${HIPPO_APP_PASSWORD_FILE:?}"
: "${MAMMOTH_APP_PASSWORD_FILE:?}"
: "${DB_PORT:=1433}"
: "${HIPPO_DB_PORT:=1433}"
: "${MAMMOTH_DB_PORT:=1433}"
: "${DB_CONNECT_RETRIES:=30}"
: "${DB_CONNECT_RETRY_DELAY_SECONDS:=5}"

# Fresh baseline is reset-only. Production must arrive through an operator-approved release
# invocation tied to one exact target and one verified backup; this script never drops a database.
if [ "${DEPLOYMENT_ENVIRONMENT:-Development}" = "Production" ]; then
    expected_target="${DB_SERVER}:${DB_PORT}/${DB_NAME}"
    if [ "$DB_NAME" != "VCentralPay" ]; then
        echo "[migrate] refused: production DB_NAME must be VCentralPay" >&2
        exit 1
    fi
    if [ "${RESET_TARGET:-}" != "$expected_target" ]; then
        echo "[migrate] refused: RESET_TARGET must exactly match the production target" >&2
        exit 1
    fi
    if [ "${RESET_APPROVED:-}" != "true" ]; then
        echo "[migrate] refused: RESET_APPROVED=true is required" >&2
        exit 1
    fi
    case "${BACKUP_ARTIFACT_URI:-}" in
        *://?*) ;;
        *) echo "[migrate] refused: BACKUP_ARTIFACT_URI must be a non-empty URI" >&2; exit 1 ;;
    esac
    if ! printf '%s' "${BACKUP_SHA256:-}" | grep -Eq '^[0-9A-Fa-f]{64}$'; then
        echo "[migrate] refused: BACKUP_SHA256 must be a 64-character SHA-256 checksum" >&2
        exit 1
    fi
    : "${RESET_APPROVAL_EVIDENCE:?production reset requires RESET_APPROVAL_EVIDENCE}"
    : "${ROLLBACK_EVIDENCE:?production reset requires ROLLBACK_EVIDENCE}"
    echo "[migrate] production reset evidence validated for exact target"
fi

APP_PW="$(cat "$POL_APP_PASSWORD_FILE")"
# One password per DB tier, never shared: APP_PW is VCentralPay's pol_app and must not reach the sim
# bootstrap scripts below (which take their own sqlcmd variable names for exactly that reason).
HIPPO_PW="$(cat "$HIPPO_APP_PASSWORD_FILE")"
MAMMOTH_PW="$(cat "$MAMMOTH_APP_PASSWORD_FILE")"

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
wait_for_db() { # $1=server $2=port $3=non-sensitive tier label
    wfd_server="$1"; wfd_port="$2"; wfd_label="$3"
    echo "[migrate] waiting for ${wfd_label} DB tier (up to ${DB_CONNECT_RETRIES} attempts, ${DB_CONNECT_RETRY_DELAY_SECONDS}s apart)..."
    wfd_i=1
    while true; do
        if PROBE_OUT="$(sqlcmd -S "${wfd_server},${wfd_port}" -U sa -P "$MSSQL_SA_PASSWORD" -N -Q "SELECT 1" 2>&1)"; then
            echo "[migrate] ${wfd_label} DB tier reachable after ${wfd_i} attempt(s)."
            break
        fi
        if [ "$wfd_i" -ge "$DB_CONNECT_RETRIES" ]; then
            if printf '%s' "$PROBE_OUT" | grep -qi "certificate\|SSL Provider\|TLS"; then
                echo "[migrate] ${wfd_label} DB tier unreachable after ${DB_CONNECT_RETRIES} attempts: TLS validation failure" >&2
            else
                echo "[migrate] ${wfd_label} DB tier unreachable after ${DB_CONNECT_RETRIES} attempts: network unreachable" >&2
            fi
            exit 1
        fi
        wfd_i=$((wfd_i + 1))
        sleep "$DB_CONNECT_RETRY_DELAY_SECONDS"
    done
}

wait_for_db "$DB_SERVER" "$DB_PORT" "core"

echo "[migrate] bootstrapping DB principal (idempotent)..."
sqlcmd -S "${DB_SERVER},${DB_PORT}" -U sa -P "$MSSQL_SA_PASSWORD" -N -b \
  -v DbName="$DB_NAME" \
     POL_APP_PASSWORD="$APP_PW" \
  -i docker/bootstrap/01-principals.sql

# products-sp-gateway REQ-3.2/3.3 + external-sim-separate-containers: hippodb/mammothdb stand in for the
# upstream systems we do not own yet, so GET /products has nothing to read without them. Each now runs on
# its OWN SQL Server instance (own hippo_app/mammoth_app LOGIN — the bootstrap files create it themselves,
# since 01-principals.sql above never touches these instances) and must exist before the API serves traffic
# (compose gates that with depends_on: service_completed_successfully) and before any test host boots
# against either server, which is what keeps parallel hosts from racing on CREATE DATABASE.
echo "[migrate] bootstrapping simulated upstream database hippodb (idempotent)..."
wait_for_db "$HIPPO_DB_SERVER" "$HIPPO_DB_PORT" "motor-source"
sqlcmd -S "${HIPPO_DB_SERVER},${HIPPO_DB_PORT}" -U sa -P "$MSSQL_SA_PASSWORD" -N -b \
  -v HIPPO_APP_PASSWORD="$HIPPO_PW" \
  -i docker/bootstrap/02-hippo-sim.sql

echo "[migrate] bootstrapping simulated upstream database mammothdb (idempotent)..."
wait_for_db "$MAMMOTH_DB_SERVER" "$MAMMOTH_DB_PORT" "non-motor-source"
sqlcmd -S "${MAMMOTH_DB_SERVER},${MAMMOTH_DB_PORT}" -U sa -P "$MSSQL_SA_PASSWORD" -N -b \
  -v MAMMOTH_APP_PASSWORD="$MAMMOTH_PW" \
  -i docker/bootstrap/03-mammoth-sim.sql

echo "[migrate] applying schema script (schema + pol_app grant matrix)..."
# docker/migrations/schema.sql is `dotnet ef migrations script --idempotent` output committed to the
# repo (scripts/check-migration-script.sh keeps it in sync in CI). Every migration is wrapped in
# IF NOT EXISTS (__EFMigrationsHistory) so an existing DB gets only the missing steps — same
# incremental behaviour as `dotnet ef database update`, without the EF tooling in this image.
# -I: QUOTED_IDENTIFIER ON (sqlcmd default is OFF; filtered indexes / persisted computed columns need ON).
sqlcmd -S "${DB_SERVER},${DB_PORT}" -U sa -P "$MSSQL_SA_PASSWORD" -N -b -I \
  -d "$DB_NAME" \
  -i docker/migrations/schema.sql

echo "[migrate] validating and converting workforce identities..."
dotnet run --project src/Tools/WorkforceIdentityMigrator/WorkforceIdentityMigrator.csproj \
  -c Release --no-build

echo "[migrate] done."
