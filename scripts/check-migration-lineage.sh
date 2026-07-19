#!/usr/bin/env bash
set -euo pipefail

# rls-to-query-filter REQ-8.6 / design.md "Migration (R5 #7)": gate that PolDbContext (the migration owner)
# still discovers the FULL existing migration chain. Run before AND after any assembly-split/class-identity
# change touching PolDbContext — an empty/partial list here means the lineage got orphaned (e.g. class
# renamed, moved namespace/assembly, or migrations-assembly override changed), and the next migration would
# try to re-create tables that already exist instead of extending the chain.

cd "$(dirname "$0")/.."

EXPECTED=(
  "20260712185344_InitialSchema"
  "20260712185646_SecurityObjects"
  "20260712185912_SeedData"
  "20260719081817_RlsTeardownAndOnePrincipal"
)

ACTUAL=$(dotnet ef migrations list --context PolDbContext \
  --project src/BuildingBlocks/BuildingBlocks.Infrastructure --startup-project src/Hosts/Api 2>&1)

STATUS=0
for id in "${EXPECTED[@]}"; do
  if ! grep -qF "$id" <<< "$ACTUAL"; then
    echo "MISSING migration: $id" >&2
    STATUS=1
  fi
done

if [ "$STATUS" -ne 0 ]; then
  echo "--- dotnet ef migrations list output ---" >&2
  echo "$ACTUAL" >&2
  echo "Migration lineage gate FAILED — PolDbContext no longer discovers the existing migration chain." >&2
  exit 1
fi

echo "Migration lineage gate OK — all ${#EXPECTED[@]} existing migration IDs discoverable via PolDbContext."
