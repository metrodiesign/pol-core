#!/usr/bin/env bash
# check-secrets.sh — harness-agnostic secret scanner (.ai/bin engine)
# Ported from ~/.claude/hooks/secret-guard.sh; detection patterns kept verbatim.
#
# Default: scan STAGED changes (git diff --cached) — for pre-commit hooks / agents.
#   --all : scan the whole tree (tracked files) — for CI.
#
# Blocks (exit 2) on:
#   1. Known key patterns (Omise, Stripe, AWS, GitHub, generic high-entropy assignment)
#   2. Forbidden config files (.env/.env.*/*.pem/*.key/... and appsettings.*.json without .example)
# Prints which file/pattern matched to stderr.
#
# Exit 0 = OK, exit 2 = block. (SECRET_GUARD_SKIP=1 overrides ONLY on the staged path — the
# in-session human escape hatch; it is IGNORED in --all/CI mode, which is a hard gate.)

set -euo pipefail

RED=$'\e[31m'
YEL=$'\e[33m'
RST=$'\e[0m'

MODE=staged
if [ "${1:-}" = "--all" ]; then
  MODE=all
fi

fail=0
reasons=()

# Exclude the guard's OWN adversarial test fixtures. .claude/hooks/tests/ holds the secret-guard
# test corpus — intentional Stripe/GitHub/AWS-shaped sample tokens, PEM blocks, conn strings —
# that exist precisely to prove the patterns BLOCK. A scanner that flags its own fixtures cannot
# commit its own tests. Standard test-path allowlist; real secrets must never live under it.
TESTS_EXCLUDE=":(exclude).claude/hooks/tests/"

# --- gather file list + content depending on mode ---
if [ "$MODE" = "all" ]; then
  # whole tree (tracked files only)
  FILES=$(git ls-files -- . "$TESTS_EXCLUDE" 2>/dev/null || true)
  if [ -z "$FILES" ]; then
    exit 0
  fi
  # content = '+'-prefixed lines emulated by prefixing every line (so the '^\+' generic
  # pattern below works identically in both modes)
  CONTENT=$(
    while IFS= read -r f; do
      [ -z "$f" ] && continue
      [ -f "$f" ] || continue
      sed 's/^/+/' "$f" 2>/dev/null || true
    done <<< "$FILES"
  )
else
  # staged (Added/Modified/Renamed/Copied)
  FILES=$(git diff --cached --name-only --diff-filter=ACMR -- . "$TESTS_EXCLUDE" 2>/dev/null || true)
  if [ -z "$FILES" ]; then
    exit 0
  fi
  CONTENT=$(git diff --cached --unified=0 -- . "$TESTS_EXCLUDE" 2>/dev/null || true)
fi

# 1. Forbidden filename patterns
while IFS= read -r f; do
  [ -z "$f" ] && continue
  base="${f##*/}"   # basename only — rules below judge the file's own name, not its dir
  case "$f" in
    *appsettings.Development.json|*appsettings.Production.json|*appsettings.Local.json)
      if [ -z "$(echo "$f" | grep -i 'example\|template\|sample')" ]; then
        fail=1
        reasons+=("forbidden file: $f (should be .example only, real config in user-secrets)")
      fi
      ;;
    *.pem|*.key|*.pfx|*.p12|secrets.json|id_rsa|id_ed25519)
      if [ -z "$(echo "$f" | grep -i 'example\|template\|sample')" ]; then
        fail=1
        reasons+=("forbidden file: $f")
      fi
      ;;
  esac
  # Real dotenv files ONLY: basename is exactly '.env', starts with '.env.' (leading dot),
  # or ENDS in '.env' (prod.env / config.env / staging.env are common real dotenv names).
  # Review #21: the old '*.env.*' glob false-positived on benign names (en.env.json,
  # theme.env.css) because '.env.' could sit MID-name. Anchor on the basename so a dotenv file
  # ('.env.production', 'prod.env') blocks while a mid-name '.env.' ('en.env.json') does not.
  case "$base" in
    .env|.env.*|*.env)
      if [ -z "$(echo "$f" | grep -i 'example\|template\|sample')" ]; then
        fail=1
        reasons+=("forbidden file: $f")
      fi
      ;;
  esac
done <<< "$FILES"

# 2. Content patterns

# Omise keys
if printf '%s' "$CONTENT" | grep -qE '(skey_|pkey_)(test|live)_[a-zA-Z0-9]{15,}'; then
  fail=1
  reasons+=("Omise key pattern (skey_/pkey_) detected in $MODE content")
fi

# Stripe keys
if printf '%s' "$CONTENT" | grep -qE 'sk_(test|live)_[a-zA-Z0-9]{24,}|pk_(test|live)_[a-zA-Z0-9]{24,}|rk_(test|live)_[a-zA-Z0-9]{24,}'; then
  fail=1
  reasons+=("Stripe key pattern detected in $MODE content")
fi

# AWS
if printf '%s' "$CONTENT" | grep -qE 'AKIA[0-9A-Z]{16}|aws_secret_access_key\s*=\s*[A-Za-z0-9/+=]{40}'; then
  fail=1
  reasons+=("AWS credential pattern detected")
fi

# GitHub tokens
if printf '%s' "$CONTENT" | grep -qE 'ghp_[a-zA-Z0-9]{36}|github_pat_[a-zA-Z0-9_]{82}'; then
  fail=1
  reasons+=("GitHub token pattern detected")
fi

# Private key PEM blocks pasted into any (even innocuously-named) file
if printf '%s' "$CONTENT" | grep -qE -- '-----BEGIN ([A-Z ]+ )?PRIVATE KEY-----'; then
  fail=1
  reasons+=("private key PEM block detected in $MODE content")
fi

# Generic: key/secret/password assigned to long value (naive entropy check)
# Catches: "SecretKey": "long-real-value", MinIO.SecretKey=..., JWT_SECRET=..., etc
#
# Review #12/#22: the value class was [A-Za-z0-9_+/=-], so real passwords/secrets containing
# @ ! $ # % slipped through, and *_SECRET names (JWT_SECRET) were not in the key list. The
# class is broadened and the key pattern now also matches '<word>_secret'.
# NOTE: '.' is deliberately EXCLUDED from the value class — including it made dotted
# member-access (token = process.env.API_TOKEN, os.environ.get) look like a 20+ char secret
# and blocked everyday code that READS secrets from env (the pattern we want to encourage).
# Dotted secrets are still covered by the connection-string and named-provider rules.
#
# Review #3: the placeholder exclusion (grep -ivE) used to run on the WHOLE LINE, so any
# placeholder word ANYWHERE on the line — e.g. a trailing '# TODO' comment — unblocked a real
# secret. We now extract ONLY the matched key=value with `grep -ioE` (which stops at the first
# char outside the value class, i.e. before a space/comment) and test the placeholder rule
# against that EXTRACTED VALUE alone. A trailing comment can no longer whitelist a real secret;
# a value that is itself a placeholder (your-key-here, example-...) still passes.
SECRET_KEYPAT='([a-z]*[_-]?secret([_-]?key)?|api[_-]?key|password|passwd|pwd|access[_-]?token|auth[_-]?token|token|private[_-]?key)[[:space:]]*[:=][[:space:]]*["'"'"']?'
SECRET_VALCLASS='A-Za-z0-9_+/=@!$#%-'
SECRET_PLACEHOLDER='(XXXX|\*\*\*\*|your-|example|placeholder|changeme|<.*>|REPLACE|TODO|YOUR_|CHANGE[_-]?ME|dummy|sample|FIXME)'
SECRET_HITS=$(
  printf '%s' "$CONTENT" | grep -E '^\+' | \
    grep -ioE "${SECRET_KEYPAT}[${SECRET_VALCLASS}]{20,}" 2>/dev/null || true
)
if [ -n "$SECRET_HITS" ] && \
   printf '%s' "$SECRET_HITS" | grep -ivE "$SECRET_PLACEHOLDER" >/dev/null 2>&1; then
  fail=1
  reasons+=("possible hardcoded secret assignment in $MODE content (matches key/password/token with long value)")
fi

# Connection strings with embedded credentials of the form  scheme :// user : PASS @ host
# Review #12/#22: CLAUDE.md lists 'connection string' as a forbidden secret. The password sits
# between the ':' and the '@', so the generic key=value rule above never sees it. Require a
# non-trivial password (>=6 chars, and no '/' so a bare host-only URL cannot match) and skip
# placeholder passwords so documented examples stay allowed.
CONN_PAT='[a-zA-Z][a-zA-Z0-9+.-]*://[^:/@[:space:]]+:[^@/[:space:]]{6,}@'
CONN_HITS=$(
  printf '%s' "$CONTENT" | grep -E '^\+' | \
    grep -oE "$CONN_PAT" 2>/dev/null || true
)
if [ -n "$CONN_HITS" ] && \
   printf '%s' "$CONN_HITS" | grep -ivE "$SECRET_PLACEHOLDER" >/dev/null 2>&1; then
  fail=1
  reasons+=("connection string with embedded credentials detected in $MODE content")
fi

if [ "$fail" -ne 0 ]; then
  printf '%s\n' "${RED}✗ Secret Guard — BLOCKED${RST}" >&2
  echo >&2
  for r in "${reasons[@]}"; do
    printf '  %s %s\n' "${YEL}•${RST}" "$r" >&2
  done
  echo >&2
  cat >&2 <<EOF
${YEL}Remediation:${RST}
  1. Remove the secret from staged files
  2. Move real values to environment (.env.local / user-secrets / K8s Secret)
  3. Commit only placeholder .example files
  4. If secret already pushed: rotate it + scrub history (BFG / git-filter-repo)

Bypass (NOT recommended): SECRET_GUARD_SKIP=1 ...
EOF
  # Review #14: SECRET_GUARD_SKIP is an escape hatch for the in-session human on the STAGED
  # path only. In --all (CI) mode it MUST be ignored — CI is a hard gate, never bypassable by
  # an env var an agent or a workflow could set.
  if [ "$MODE" != "all" ] && [ "${SECRET_GUARD_SKIP:-0}" = "1" ]; then
    echo >&2
    printf '%s\n' "${YEL}⚠ SECRET_GUARD_SKIP=1 set — overriding block${RST}" >&2
    exit 0
  fi
  exit 2
fi

exit 0
