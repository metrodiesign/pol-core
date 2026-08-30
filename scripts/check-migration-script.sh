#!/usr/bin/env bash
set -euo pipefail

# Schema script drift gate: docker/migrations/schema.sql must match what the committed EF migrations
# generate. Regenerate with `./scripts/check-migration-script.sh --write` after `dotnet ef migrations add`.
cd "$(dirname "$0")/.."

TARGET=docker/migrations/schema.sql
TMP="$(mktemp)"
RAW="$(mktemp)"
trap 'rm -f "$TMP" "$RAW"' EXIT

dotnet ef migrations script --idempotent --context PolDbContext \
  --project src/BuildingBlocks/BuildingBlocks.Infrastructure --startup-project src/Hosts/Api \
  -o "$RAW" >/dev/null

# EF's script generator emits ONE batch per migration, while `ef database update` executes one
# command at a time. A raw Sql() that references a column added earlier in the same migration then
# fails at batch compile time under sqlcmd ("Invalid column name", seen on
# 20260810112718_AdminTenantPspRoutingControlPlane from an empty DB). Split into one batch per
# command by inserting GO between EF's per-command IF NOT EXISTS wrappers; the BEGIN TRANSACTION
# opened before the first wrapper still spans those batches on the same sqlcmd connection.
awk 'last == "END;" && $0 == "IF NOT EXISTS (" { print "GO"; print "" } { print; if ($0 != "") last = $0 }' "$RAW" > "$TMP"

if [ "${1:-}" = "--write" ]; then
  cp "$TMP" "$TARGET"
  echo "Schema script written — $TARGET"
  exit 0
fi

if ! diff -q "$TMP" "$TARGET" >/dev/null; then
  echo "Schema script drift gate FAILED — $TARGET is stale. Run: ./scripts/check-migration-script.sh --write" >&2
  diff "$TARGET" "$TMP" | head -40 >&2
  exit 1
fi
echo "Schema script drift gate OK — $TARGET matches the EF migrations."
