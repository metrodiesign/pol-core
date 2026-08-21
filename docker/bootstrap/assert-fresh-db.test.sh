#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/../.."

fail() {
  echo "assert-fresh-db.test: $*" >&2
  exit 1
}

migration_dir=src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations
mapfile -t migration_files < <(find "$migration_dir" -maxdepth 1 -type f -name '*.cs' \
  ! -name '*.Designer.cs' ! -name '*ModelSnapshot.cs' -print | sort)
mapfile -t designer_files < <(find "$migration_dir" -maxdepth 1 -type f -name '*.Designer.cs' -print | sort)
mapfile -t snapshot_files < <(find "$migration_dir" -maxdepth 1 -type f -name '*ModelSnapshot.cs' -print | sort)

[ "${#migration_files[@]}" -ge 4 ] || fail "expected at least the four baseline migrations"
[ "${#snapshot_files[@]}" -eq 1 ] || fail "expected exactly one model snapshot"

for designer in "${designer_files[@]}"; do
  migration="${designer%.Designer.cs}.cs"
  [ -f "$migration" ] || fail "orphan migration designer: $designer"
done

for migration in "${migration_files[@]}"; do
  designer="${migration%.cs}.Designer.cs"
  [ -f "$designer" ] || grep -q '\[Migration("' "$migration" \
    || fail "migration lacks designer and inline Migration attribute: $migration"
done

for suffix in InitialSchema SecurityObjects SeedData OneBasedPersistedEnumStorage; do
  [ "$(printf '%s\n' "${migration_files[@]}" | grep -c "_${suffix}\.cs$")" -eq 1 ] \
    || fail "missing or duplicate ${suffix} migration"
done

if grep -rnE 'CheckoutSession|ItemPolicy|product\.create|product\.update|2025-latest' \
  "$migration_dir" \
  docker-compose.yml .github/workflows/ci.yml >/dev/null; then
  fail "retired migration surface or floating SQL image remains"
fi

grep -qE '17\.0\.4045\.5' docker/bootstrap/01-principals.sql \
  || fail "bootstrap engine floor missing"
grep -qE 'COMPATIBILITY_LEVEL = 170' docker/bootstrap/01-principals.sql \
  || fail "bootstrap compatibility assignment missing"
grep -qE 'iam\.PermissionGroups expected 7 rows' docker/bootstrap/assert-fresh-db.sql \
  || fail "fresh assertion IAM group count missing"
grep -qE 'migration history must contain exactly 20 expected migrations' docker/bootstrap/assert-fresh-db.sql \
  || fail "fresh assertion migration set count missing"
grep -qE 'iam\.Permissions expected 26 rows' docker/bootstrap/assert-fresh-db.sql \
  || fail "fresh assertion IAM permission count missing"
grep -qE 'iam\.RolePermissions expected 33 rows' docker/bootstrap/assert-fresh-db.sql \
  || fail "fresh assertion IAM role-permission count missing"
grep -qE '20260819145219_WorkforceTenantBinding' docker/bootstrap/assert-fresh-db.sql \
  || fail "fresh assertion latest migration missing"
grep -qE 'CK_WorkforceTenantBindings_Singleton' docker/bootstrap/assert-fresh-db.sql \
  || fail "fresh assertion workforce tenant singleton missing"
grep -qE 'WorkforceTenantBindings must be empty before runtime tenant pin initialization' \
  docker/bootstrap/assert-fresh-db.sql \
  || fail "fresh assertion workforce tenant empty baseline missing"
grep -qE 'exactly five native json columns required' docker/bootstrap/assert-fresh-db.sql \
  || fail "fresh assertion native JSON check missing"

tmp_script="$(mktemp)"
trap 'rm -f "$tmp_script"' EXIT
dotnet ef migrations script 0 \
  --context PolDbContext \
  --project src/BuildingBlocks/BuildingBlocks.Infrastructure \
  --startup-project src/Hosts/Api \
  --output "$tmp_script" >/dev/null

preflight_line="$(grep -nE 'InitialSchema refused non-empty or legacy target database' "$tmp_script" | head -1 | cut -d: -f1)"
ddl_line="$(grep -nE 'CREATE SCHEMA \[admin\]' "$tmp_script" | head -1 | cut -d: -f1)"
[ -n "$preflight_line" ] && [ -n "$ddl_line" ] && [ "$preflight_line" -lt "$ddl_line" ] \
  || fail "preflight must render before first application DDL"

if [ -n "${POL_SA_PASSWORD:-}" ]; then
  command -v sqlcmd >/dev/null || fail "POL_SA_PASSWORD set but sqlcmd unavailable"
  sqlcmd -S "${POL_SQL_SERVER:-localhost,11433}" -U sa -P "$POL_SA_PASSWORD" -C -b \
    -v DbName="${POL_DB:-VCentralPay}" \
    -i docker/bootstrap/assert-fresh-db.sql
fi

echo "assert-fresh-db.test: OK"
