#!/usr/bin/env bash
# Loads/reloads the demo dataset (dev only). Idempotent — safe to run repeatedly.
# Reads connection/credential from the environment (same vars docker-compose/.env uses);
# no secret lives in this script or in docker/bootstrap/seed-demo.sql.
#   set -a && source .env && set +a
#   ./scripts/seed-demo.sh
#
# The seed deletes and re-inserts rows as `sa`. Pointing it at a non-local server (a copied prod
# .env, a tunnel, a shared staging box) would silently plant demo merchants/users/orders there, so
# a non-local target is refused unless POL_ALLOW_DEMO_SEED=1 is set deliberately.
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
SQL_FILE="$REPO_ROOT/docker/bootstrap/seed-demo.sql"

echo "seed-demo: target server=$SQL_SERVER db=$DB_NAME"

case "$SQL_SERVER" in
    localhost|localhost,*|127.0.0.1|127.0.0.1,*|'[::1]'|'[::1],'*) IS_LOCAL=1 ;;
    *)                                                             IS_LOCAL=0 ;;
esac

if [[ "$IS_LOCAL" -eq 0 && "${POL_ALLOW_DEMO_SEED:-}" != "1" ]]; then
    cat >&2 <<EOF
seed-demo: refusing to seed a non-local target ($SQL_SERVER).

This script deletes and re-inserts demo merchants, users, products, orders and payments as \`sa\`.
It is meant for the local docker-compose DB only. If you are certain this target is a throwaway
dev/test database, re-run with:

    POL_ALLOW_DEMO_SEED=1 ./scripts/seed-demo.sh
EOF
    exit 1
fi

# sqlcmd on the host is optional — the compose SQL Server image already ships one. Fall back to it
# (feeding the script over stdin, since docker/bootstrap is not mounted into pol-db) so a fresh
# machine following the README prerequisites needs no extra host tool. The fallback can only reach
# the compose DB, so a custom non-local server without host sqlcmd is an error, not a silent
# redirect to the local container.
if command -v sqlcmd >/dev/null 2>&1; then
    sqlcmd -S "$SQL_SERVER" -U sa -P "$PASSWORD" -C -b \
        -v DbName="$DB_NAME" \
        -i "$SQL_FILE"
elif [[ "$IS_LOCAL" -eq 1 ]]; then
    echo "seed-demo: no host sqlcmd — using the one inside the pol-db container."
    docker compose -f "$REPO_ROOT/docker-compose.yml" exec -T pol-db /opt/mssql-tools18/bin/sqlcmd \
        -S localhost -U sa -P "$PASSWORD" -C -b \
        -v DbName="$DB_NAME" \
        < "$SQL_FILE"
else
    echo "seed-demo: sqlcmd is not on PATH and the target ($SQL_SERVER) is not the compose DB," >&2
    echo "           so the container's sqlcmd cannot reach it. Install sqlcmd on the host." >&2
    exit 1
fi
