#!/usr/bin/env bash
set -euo pipefail

# Fresh-baseline lineage gate: exactly four migrations, in fixed dependency order.
cd "$(dirname "$0")/.."

ACTUAL=$(dotnet ef migrations list --context PolDbContext \
  --project src/BuildingBlocks/BuildingBlocks.Infrastructure --startup-project src/Hosts/Api 2>&1)

mapfile -t IDS < <(printf '%s\n' "$ACTUAL" | sed -nE 's/^([0-9]{14}_(InitialSchema|SecurityObjects|SeedData|OneBasedPersistedEnumStorage)).*/\1/p')
EXPECTED_SUFFIXES=("_InitialSchema" "_SecurityObjects" "_SeedData" "_OneBasedPersistedEnumStorage")

if [ "${#IDS[@]}" -ne 4 ]; then
  echo "Migration lineage gate FAILED — expected exactly 4 baseline migrations, got ${#IDS[@]}." >&2
  printf '%s\n' "$ACTUAL" >&2
  exit 1
fi

for i in 0 1 2; do
  if [[ "${IDS[$i]}" != *"${EXPECTED_SUFFIXES[$i]}" ]]; then
    echo "Migration lineage gate FAILED — expected order InitialSchema -> SecurityObjects -> SeedData -> OneBasedPersistedEnumStorage." >&2
    printf '%s\n' "$ACTUAL" >&2
    exit 1
  fi
done

echo "Migration lineage gate OK — InitialSchema -> SecurityObjects -> SeedData -> OneBasedPersistedEnumStorage."
