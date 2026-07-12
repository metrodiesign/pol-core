#!/usr/bin/env bash
# Loads/reloads the demo dataset (dev only). Idempotent — safe to run repeatedly.
# Reads connection/credential from the environment (same vars docker-compose/.env uses);
# no secret lives in this script or in docker/bootstrap/seed-demo.sql.
#   set -a && source .env && set +a
#   ./scripts/seed-demo.sh
set -euo pipefail

SQL_SERVER="${POL_SQL_SERVER:-localhost,11433}"
DB_NAME="${POL_DB:-VCentralPay}"
PASSWORD="${POL_SA_PASSWORD:-${MSSQL_SA_PASSWORD:-}}"

if [[ -z "$PASSWORD" ]]; then
    echo "seed-demo: set POL_SA_PASSWORD or MSSQL_SA_PASSWORD (sa password) in the environment first." >&2
    exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

sqlcmd -S "$SQL_SERVER" -U sa -P "$PASSWORD" -C -b \
    -v DbName="$DB_NAME" \
    -i "$REPO_ROOT/docker/bootstrap/seed-demo.sql"
