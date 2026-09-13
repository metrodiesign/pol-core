#!/usr/bin/env bash
set -euo pipefail

# Local dev only: grant a Platform role to an Employee account that was JIT-created by the employee login.
#
# WHY: the first employee has no admin session that could call PUT /api/v1/accounts/{id}/platform-access,
# so /me/access answers permissions [] and the admin console shows the 403 card. This applies the same rows
# the API path writes (see scripts/dev-grant-platform-access.sql) against the local VCentralPay catalog.
# Never point it at a shared or production server: the audited path is the API.
#
# Usage: scripts/dev-grant-platform-access.sh <accountId> [roleCode]   (roleCode default: platform_admin)

cd "$(dirname "$0")/.."

account_id="${1:-}"
role_code="${2:-platform_admin}"
if [[ -z "$account_id" ]]; then
  echo "usage: $0 <accountId> [roleCode]" >&2
  exit 2
fi

# sqlcmd does not read .env; source it here (never print the values).
if [[ -f .env ]]; then
  set -a
  # shellcheck disable=SC1091
  . ./.env
  set +a
fi

server="${POL_SQL_SERVER:-localhost,11433}"
database="${POL_DB:-VCentralPay}"
if [[ -z "${POL_SA_PASSWORD:-}" ]]; then
  echo "[dev-grant-platform-access] POL_SA_PASSWORD is not set (.env)." >&2
  exit 1
fi
case "$server" in
  localhost*|127.0.0.1*|host.docker.internal*) ;;
  *) echo "[dev-grant-platform-access] refusing non-local server '$server'." >&2; exit 1 ;;
esac

echo "[dev-grant-platform-access] $database@$server account=$account_id role=$role_code"
sqlcmd -S "$server" -d "$database" -U sa -P "$POL_SA_PASSWORD" -C -b -I \
  -v AccountId="$account_id" RoleCode="$role_code" \
  -i scripts/dev-grant-platform-access.sql
echo "[dev-grant-platform-access] done — the employee must log in again (authorization version bumped)."
