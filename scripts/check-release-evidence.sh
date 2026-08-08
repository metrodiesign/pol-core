#!/usr/bin/env bash
set -euo pipefail

fail() {
  printf 'release gate refused: %s\n' "$1" >&2
  exit 1
}

[[ "${RELEASE_TAG:-}" =~ ^v[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.-]+)?$ ]] \
  || fail "RELEASE_TAG must be semantic version tag vX.Y.Z"
[[ "${STAGING_STATUS:-}" == "passed" ]] || fail "STAGING_STATUS must be passed"
[[ "${STAGING_EVIDENCE_URI:-}" =~ ^(https?|s3|gs|artifact)://[^[:space:]]+$ ]] \
  || fail "STAGING_EVIDENCE_URI must be non-empty evidence URI"
[[ "${RESET_TARGET:-}" =~ ^[^/:[:space:]]+:[0-9]+/VCentralPay$ ]] \
  || fail "RESET_TARGET must exactly identify host:port/VCentralPay"
if [[ "${REQUIRE_EXPECTED_RESET_TARGET:-false}" == "true" ]]; then
  [[ -n "${EXPECTED_RESET_TARGET:-}" ]] || fail "EXPECTED_RESET_TARGET is required by protected environment"
  [[ "$RESET_TARGET" == "$EXPECTED_RESET_TARGET" ]] \
    || fail "RESET_TARGET does not match protected environment target"
fi
[[ "${RESET_APPROVED:-}" == "true" ]] || fail "RESET_APPROVED must be true"
[[ "${BACKUP_ARTIFACT_URI:-}" =~ ^(https?|s3|gs|artifact)://[^[:space:]]+$ ]] \
  || fail "BACKUP_ARTIFACT_URI must be non-empty URI"
[[ "${BACKUP_SHA256:-}" =~ ^[0-9A-Fa-f]{64}$ ]] \
  || fail "BACKUP_SHA256 must be 64 hexadecimal characters"
[[ "${RESET_APPROVAL_EVIDENCE:-}" =~ ^(https?|s3|gs|artifact)://[^[:space:]]+$ ]] \
  || fail "RESET_APPROVAL_EVIDENCE must be non-empty evidence URI"
[[ "${ROLLBACK_EVIDENCE:-}" =~ ^(https?|s3|gs|artifact)://[^[:space:]]+$ ]] \
  || fail "ROLLBACK_EVIDENCE must be non-empty evidence URI"
[[ -f CHANGELOG.md ]] || fail "CHANGELOG.md is required"
grep -Fqx "## ${RELEASE_TAG}" CHANGELOG.md \
  || fail "CHANGELOG.md must contain an exact ## ${RELEASE_TAG} release heading"

printf 'release gate accepted: %s, staging evidence present, reset/backup/rollback evidence complete\n' "$RELEASE_TAG"
