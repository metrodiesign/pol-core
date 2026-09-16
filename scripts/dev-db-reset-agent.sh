#!/usr/bin/env bash
set -euo pipefail

# Local dev only: reset one agent registration so the same Microsoft identity can run the canonical
# registration e2e again (login -> draft -> photos -> submit -> approve -> login).
#
# WHY: Approve creates the agent account graph and Submit then answers 409 account_already_approved;
# there is no product path that removes an agent, and hand-written DELETEs across seven tables broke
# the e2e reruns. This applies scripts/dev-db-reset-agent.sql (FK order, one transaction) against the
# local VCentralPay catalog. Never point it at a shared or production server.
#
# Usage: scripts/dev-db-reset-agent.sh <registrationId>
#   registrationId = acct.AgentRegistrations.Id (the `registrationId` the SPA shows / GET /api/v1/agent-registration returns)

cd "$(dirname "$0")/.."

registration_id="${1:-}"
if [[ -z "$registration_id" ]]; then
  echo "usage: $0 <registrationId>" >&2
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
  echo "[dev-db-reset-agent] POL_SA_PASSWORD is not set (.env)." >&2
  exit 1
fi
case "$server" in
  localhost*|127.0.0.1*|host.docker.internal*) ;;
  *) echo "[dev-db-reset-agent] refusing non-local server '$server'." >&2; exit 1 ;;
esac

echo "[dev-db-reset-agent] $database@$server registration=$registration_id"
sqlcmd -S "$server" -d "$database" -U sa -P "$POL_SA_PASSWORD" -C -b -I \
  -v RegistrationId="$registration_id" \
  -i scripts/dev-db-reset-agent.sql
echo "[dev-db-reset-agent] done — the identity can register again (API restart not needed)."
