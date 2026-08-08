#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "$0")/.." && pwd)
test_dir=$(mktemp -d)
cleanup() {
  rm -f "$test_dir/CHANGELOG.md"
  rmdir "$test_dir"
}
trap cleanup EXIT
printf '## v1.0.0\n' > "$test_dir/CHANGELOG.md"
cd "$test_dir"

valid_env=(
  RELEASE_TAG=v1.0.0
  STAGING_STATUS=passed
  STAGING_EVIDENCE_URI=https://evidence.example/staging/123
  RESET_TARGET=db.internal:1433/VCentralPay
  RESET_APPROVED=true
  BACKUP_ARTIFACT_URI=s3://release-backups/v1.0.0.bak
  BACKUP_SHA256=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
  RESET_APPROVAL_EVIDENCE=https://evidence.example/approvals/456
  ROLLBACK_EVIDENCE=https://evidence.example/rollback/789
)

env "${valid_env[@]}" bash "$repo_root/scripts/check-release-evidence.sh" >/dev/null

assert_refused() {
  local key=$1 value=$2
  local -a changed=("${valid_env[@]}")
  changed+=("$key=$value")
  if env "${changed[@]}" bash "$repo_root/scripts/check-release-evidence.sh" >/dev/null 2>&1; then
    printf 'expected refusal for %s=%s\n' "$key" "$value" >&2
    exit 1
  fi
}

assert_refused RELEASE_TAG latest
assert_refused STAGING_STATUS failed
assert_refused STAGING_EVIDENCE_URI ''
assert_refused RESET_TARGET db.internal:1433/OtherDb
assert_refused RESET_APPROVED false
assert_refused BACKUP_ARTIFACT_URI /tmp/local.bak
assert_refused BACKUP_SHA256 abc
assert_refused RESET_APPROVAL_EVIDENCE ''
assert_refused ROLLBACK_EVIDENCE ''

printf '## v2.0.0\n' > CHANGELOG.md
assert_refused RELEASE_TAG v1.0.0
printf '## v1.0.0\n' > CHANGELOG.md

env "${valid_env[@]}" REQUIRE_EXPECTED_RESET_TARGET=true \
  EXPECTED_RESET_TARGET=db.internal:1433/VCentralPay \
  bash "$repo_root/scripts/check-release-evidence.sh" >/dev/null
assert_refused REQUIRE_EXPECTED_RESET_TARGET true

printf 'check-release-evidence tests: OK\n'
