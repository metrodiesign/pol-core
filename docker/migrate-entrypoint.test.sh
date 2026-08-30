#!/usr/bin/env bash
# migrate-entrypoint.test.sh — script-level tests for docker/migrate-entrypoint.sh's bounded
# DB-reachability retry loop (now run per DB tier: main + hippo + mammoth), network-vs-TLS failure
# classification, and the same DB_PORT/TLS connection-string wiring as entrypoint.sh (POL_DESIGN_SQL)
# with sqlcmd's `-N`/no-`-C` flags.
# รัน: bash docker/migrate-entrypoint.test.sh   (exit 0 = ผ่านครบ)
#
# Approach: stub `sqlcmd` and `dotnet` first on PATH. The sqlcmd stub simulates a configurable
# number of reachability-probe failures (network or TLS flavored, via env) before succeeding,
# and always succeeds for the bootstrap (-i ...) invocation. The dotnet stub intercepts
# both EF update and workforce migration tool, logs their order, and prints $POL_DESIGN_SQL so
# the connection string is observable without a live SQL Server.
set -u

SCRIPT="$(cd "$(dirname "$0")" && pwd)/migrate-entrypoint.sh"
TMPDIR="$(mktemp -d)"
trap 'rm -rf "$TMPDIR"' EXIT

STUB_BIN="$TMPDIR/bin"
mkdir -p "$STUB_BIN"

cat >"$STUB_BIN/sqlcmd" <<'EOF'
#!/bin/sh
# Records every invocation (for flag assertions) then decides pass/fail.
echo "$*" >>"$SQLCMD_LOG"
case "$*" in
    *"-i "*)
        # bootstrap script call — always succeeds
        exit 0
        ;;
esac
# reachability-probe call (-Q "SELECT 1") — counter is PER -S value, not one shared file: migrate-
# entrypoint.sh now waits on 3 servers in sequence (main/hippo/mammoth), and a shared counter would
# make their independent retry/failure schedules bleed into each other. Sanitize logic
# (tr -c 'A-Za-z0-9' '_') is mirrored exactly in this file's own probe_file() helper below — keep
# both in sync if either changes.
server_arg="$(printf '%s' "$*" | sed -n 's/.*-S \([^ ]*\).*/\1/p')"
safe_server="$(printf '%s' "$server_arg" | tr -c 'A-Za-z0-9' '_')"
COUNT_FILE="${SQLCMD_PROBE_COUNT_FILE}.${safe_server}"
n=0
[ -f "$COUNT_FILE" ] && n="$(cat "$COUNT_FILE")"
n=$((n + 1))
echo "$n" >"$COUNT_FILE"
if [ "$n" -le "${SQLCMD_FAIL_TIMES:-0}" ]; then
    if [ "${SQLCMD_FAIL_MODE:-network}" = "tls" ]; then
        echo "Sqlcmd: Error: Microsoft ODBC Driver 18 for SQL Server : SSL Provider: [certificate verify failed]" >&2
    else
        echo "Sqlcmd: Error: Microsoft ODBC Driver 18 for SQL Server : TCP Provider: No connection could be made" >&2
    fi
    exit 1
fi
exit 0
EOF
chmod +x "$STUB_BIN/sqlcmd"

cat >"$STUB_BIN/dotnet" <<'EOF'
#!/bin/sh
echo "$*" >>"$DOTNET_LOG"
echo "$POL_DESIGN_SQL"
case "$*" in
    *"WorkforceIdentityMigrator"*) exit "${WORKFORCE_TOOL_EXIT:-0}" ;;
esac
EOF
chmod +x "$STUB_BIN/dotnet"

# Stub the OS trust-store refresh so the runtime CA-install block is observable without root.
cat >"$STUB_BIN/update-ca-certificates" <<'EOF'
#!/bin/sh
echo "called" >>"$UPDATE_CA_LOG"
EOF
chmod +x "$STUB_BIN/update-ca-certificates"

PW_FILE="$TMPDIR/app_password"
echo "s3cret" >"$PW_FILE"

# sim-db-separate-logins: one password per DB tier, never shared — distinct files so the assertions
# below can prove the core's value never reaches the sim bootstrap calls.
HIPPO_PW_FILE="$TMPDIR/hippo_password"
echo "hippoPw" >"$HIPPO_PW_FILE"
MAMMOTH_PW_FILE="$TMPDIR/mammoth_password"
echo "mammothPw" >"$MAMMOTH_PW_FILE"

CA_FILE="$TMPDIR/db_ca.pem"
echo "FAKE-PEM-CA" >"$CA_FILE"
CA_TRUST_DIR="$TMPDIR/ca-trust"
mkdir -p "$CA_TRUST_DIR"

pass=0
fail=0

run_migrate() { # extra env assignments as $@
    env -i \
        PATH="$STUB_BIN:$PATH" \
        DB_SERVER="dbhost.internal" \
        DB_NAME="AppDb" \
        HIPPO_DB_SERVER="hippohost.internal" \
        MAMMOTH_DB_SERVER="mammothhost.internal" \
        MSSQL_SA_PASSWORD="saPw" \
        POL_APP_PASSWORD_FILE="$PW_FILE" \
        HIPPO_APP_PASSWORD_FILE="$HIPPO_PW_FILE" \
        MAMMOTH_APP_PASSWORD_FILE="$MAMMOTH_PW_FILE" \
        SQLCMD_LOG="$TMPDIR/sqlcmd.log" \
        DOTNET_LOG="$TMPDIR/dotnet.log" \
        SQLCMD_PROBE_COUNT_FILE="$TMPDIR/probe_count" \
        CA_TRUST_DIR="$CA_TRUST_DIR" \
        UPDATE_CA_LOG="$TMPDIR/update_ca.log" \
        "$@" \
        sh "$SCRIPT"
}

# Same sanitize logic as the sqlcmd stub above (tr -c 'A-Za-z0-9' '_') — resolves the per-server counter
# file for a given "server,port" pair so assertions stay deterministic per server instead of reading a
# counter that 3 servers now share attempts across.
probe_file() { # $1=server $2=port
    printf '%s' "$TMPDIR/probe_count.$(printf '%s' "$1,$2" | tr -c 'A-Za-z0-9' '_')"
}

check_contains() { # $1=desc $2=haystack $3=needle
    if printf '%s' "$2" | grep -qF -- "$3"; then
        pass=$((pass + 1))
    else
        fail=$((fail + 1)); echo "FAIL [$1] expected to contain: $3 :: got: $2"
    fi
}

check_not_contains() { # $1=desc $2=haystack $3=needle
    if printf '%s' "$2" | grep -qF -- "$3"; then
        fail=$((fail + 1)); echo "FAIL [$1] must NOT contain: $3 :: got: $2"
    else
        pass=$((pass + 1))
    fi
}

check_eq() { # $1=desc $2=actual $3=expected
    if [ "$2" = "$3" ]; then
        pass=$((pass + 1))
    else
        fail=$((fail + 1)); echo "FAIL [$1] expected [$3] got [$2]"
    fi
}

# --- production fresh-reset gate: fail before DB access unless exact target + backup/approval/rollback evidence ---
rm -f "$TMPDIR"/probe_count* "$TMPDIR/sqlcmd.log"
out_prod_refused="$(run_migrate DEPLOYMENT_ENVIRONMENT=Production DB_NAME=VCentralPay 2>&1)"
rc_prod_refused=$?
check_eq "production gate: missing evidence fails" "$([ "$rc_prod_refused" -ne 0 ] && echo yes || echo no)" "yes"
check_contains "production gate: explicit reset approval required" "$out_prod_refused" "RESET_TARGET must exactly match"
check_eq "production gate: refusal happens before DB access" "$([ -f "$TMPDIR/sqlcmd.log" ] && echo touched || echo untouched)" "untouched"

rm -f "$TMPDIR"/probe_count* "$TMPDIR/sqlcmd.log"
out_prod_wrong_target="$(run_migrate DEPLOYMENT_ENVIRONMENT=Production DB_NAME=VCentralPay \
    RESET_TARGET=other:1433/VCentralPay RESET_APPROVED=true \
    BACKUP_ARTIFACT_URI=s3://backups/vcentralpay.bak \
    BACKUP_SHA256=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef \
    RESET_APPROVAL_EVIDENCE=change-123 ROLLBACK_EVIDENCE=runbook-456 2>&1)"
check_contains "production gate: wrong target refused" "$out_prod_wrong_target" "RESET_TARGET must exactly match"

rm -f "$TMPDIR"/probe_count* "$TMPDIR/sqlcmd.log"
out_prod_ok="$(run_migrate DEPLOYMENT_ENVIRONMENT=Production DB_NAME=VCentralPay \
    RESET_TARGET=dbhost.internal:1433/VCentralPay RESET_APPROVED=true \
    BACKUP_ARTIFACT_URI=s3://backups/vcentralpay.bak \
    BACKUP_SHA256=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef \
    RESET_APPROVAL_EVIDENCE=change-123 ROLLBACK_EVIDENCE=runbook-456 \
    DB_CONNECT_RETRIES=2 DB_CONNECT_RETRY_DELAY_SECONDS=0 2>&1)"
rc_prod_ok=$?
check_eq "production gate: complete evidence proceeds" "$rc_prod_ok" "0"
check_contains "production gate: evidence validation logged" "$out_prod_ok" "production reset evidence validated"

# --- reachable on first attempt: proceeds immediately, exits 0 ---
rm -f "$TMPDIR"/probe_count* "$TMPDIR/sqlcmd.log" "$TMPDIR/dotnet.log"
out_ok="$(run_migrate DB_CONNECT_RETRIES=5 DB_CONNECT_RETRY_DELAY_SECONDS=0 2>&1)"
rc_ok=$?
check_eq "reachable: exit 0" "$rc_ok" "0"
check_contains "reachable: proceeds to migrations" "$out_ok" "[migrate] done."
check_contains "reachable: single probe attempt (DB_SERVER)" "$(cat "$(probe_file dbhost.internal 1433)" 2>/dev/null)" "1"
dotnet_log="$(cat "$TMPDIR/dotnet.log")"
check_contains "workforce tool: invoked" "$dotnet_log" "src/Tools/WorkforceIdentityMigrator/WorkforceIdentityMigrator.csproj"
schema_call="$(grep 'docker/migrations/schema.sql' "$TMPDIR/sqlcmd.log")"
check_contains "schema script: applied via sqlcmd"           "$schema_call" "-i docker/migrations/schema.sql"
check_contains "schema script: targets DB_NAME"              "$schema_call" "-d AppDb"
check_contains "schema script: QUOTED_IDENTIFIER ON (-I)"    "$schema_call" " -I "
check_contains "schema script: exit-on-error (-b)"           "$schema_call" " -b "
check_contains "schema script: encrypted (-N)"               "$schema_call" " -N "
check_not_contains "schema script: no dotnet ef in image path" "$dotnet_log" "ef database update"
check_eq "workforce tool: runs after schema script" \
    "$([ -n "$schema_call" ] && [ -s "$TMPDIR/dotnet.log" ] && echo yes || echo no)" "yes"

# --- workforce conversion failure is a hard deployment gate ---
rm -f "$TMPDIR"/probe_count* "$TMPDIR/sqlcmd.log" "$TMPDIR/dotnet.log"
out_tool_failure="$(run_migrate DB_CONNECT_RETRIES=5 DB_CONNECT_RETRY_DELAY_SECONDS=0 WORKFORCE_TOOL_EXIT=23 2>&1)"
rc_tool_failure=$?
check_eq "workforce tool failure: non-zero exit" "$rc_tool_failure" "23"
check_contains "workforce tool failure: reached conversion" "$out_tool_failure" "validating and converting workforce identities"
check_not_contains "workforce tool failure: deploy not marked done" "$out_tool_failure" "[migrate] done."

# --- unreachable for the whole bounded window: exits non-zero, attempts logged ---
rm -f "$TMPDIR"/probe_count* "$TMPDIR/sqlcmd.log"
out_unreachable="$(run_migrate DB_CONNECT_RETRIES=3 DB_CONNECT_RETRY_DELAY_SECONDS=0 SQLCMD_FAIL_TIMES=999 SQLCMD_FAIL_MODE=network 2>&1)"
rc_unreachable=$?
check_eq "unreachable: non-zero exit" "$([ "$rc_unreachable" -ne 0 ] && echo yes || echo no)" "yes"
check_contains "unreachable: bounded attempts logged" "$out_unreachable" "unreachable after 3 attempts"
check_eq "unreachable: exactly DB_CONNECT_RETRIES probes" "$(cat "$(probe_file dbhost.internal 1433)")" "3"
check_not_contains "unreachable: never reaches migrations" "$out_unreachable" "[migrate] done."

# --- exhaustion log classifies network-unreachable vs TLS-validation failure ---
check_contains "network failure classified" "$out_unreachable" "network unreachable"
check_not_contains "network failure not misclassified as TLS" "$out_unreachable" "TLS validation failure"

rm -f "$TMPDIR"/probe_count* "$TMPDIR/sqlcmd.log"
out_tls="$(run_migrate DB_CONNECT_RETRIES=2 DB_CONNECT_RETRY_DELAY_SECONDS=0 SQLCMD_FAIL_TIMES=999 SQLCMD_FAIL_MODE=tls 2>&1)"
check_contains "TLS failure classified" "$out_tls" "TLS validation failure"
check_not_contains "TLS failure not misclassified as network" "$out_tls" "unreachable after 2 attempts: network"

# --- succeeds after transient failures, within the bounded window ---
rm -f "$TMPDIR"/probe_count* "$TMPDIR/sqlcmd.log"
out_recovers="$(run_migrate DB_CONNECT_RETRIES=5 DB_CONNECT_RETRY_DELAY_SECONDS=0 SQLCMD_FAIL_TIMES=2 2>&1)"
rc_recovers=$?
check_eq "recovers: exit 0 after transient failures" "$rc_recovers" "0"
check_contains "recovers: reaches migrations" "$out_recovers" "[migrate] done."

# --- sqlcmd invoked with -N, never with the -C blind-trust flag ---
sqlcmd_log="$(cat "$TMPDIR/sqlcmd.log")"
check_contains "sqlcmd: uses -N" "$sqlcmd_log" "-N"
check_not_contains "sqlcmd: never -C" "$sqlcmd_log" "-C"

# --- simulated upstream bootstrap (products-sp-gateway REQ-3.2, external-sim-separate-containers): each
# instance's own file, after the principal script, same TLS flags, carries its OWN sqlcmd variable and
# password (HIPPO_APP_PASSWORD / MAMMOTH_APP_PASSWORD — each instance has its own LOGIN, neither has
# 01-principals.sql run on it; sim-db-separate-logins), and targets its own server, with -b so a
# failed self-check inside the SQL stops the deploy ---
hippo_call="$(grep -- '02-hippo-sim.sql' "$TMPDIR/sqlcmd.log" | head -1)"
mammoth_call="$(grep -- '03-mammoth-sim.sql' "$TMPDIR/sqlcmd.log" | head -1)"
principals_line="$(grep -n -- '01-principals.sql' "$TMPDIR/sqlcmd.log" | head -1 | cut -d: -f1)"
hippo_line="$(grep -n -- '02-hippo-sim.sql' "$TMPDIR/sqlcmd.log" | head -1 | cut -d: -f1)"
mammoth_line="$(grep -n -- '03-mammoth-sim.sql' "$TMPDIR/sqlcmd.log" | head -1 | cut -d: -f1)"
check_contains "hippo bootstrap: 02-hippo-sim.sql runs" "$sqlcmd_log" "02-hippo-sim.sql"
check_contains "mammoth bootstrap: 03-mammoth-sim.sql runs" "$sqlcmd_log" "03-mammoth-sim.sql"
check_eq "hippo bootstrap: runs AFTER 01-principals.sql" \
    "$([ -n "$principals_line" ] && [ -n "$hippo_line" ] && [ "$hippo_line" -gt "$principals_line" ] && echo yes || echo no)" "yes"
check_eq "mammoth bootstrap: runs AFTER 01-principals.sql" \
    "$([ -n "$principals_line" ] && [ -n "$mammoth_line" ] && [ "$mammoth_line" -gt "$principals_line" ] && echo yes || echo no)" "yes"
check_contains "hippo bootstrap: uses -N"     "$hippo_call" "-N"
check_not_contains "hippo bootstrap: never -C" "$hippo_call" "-C"
check_contains "hippo bootstrap: uses -b"     "$hippo_call" "-b"
check_contains "hippo bootstrap: carries HIPPO_APP_PASSWORD" "$hippo_call" "HIPPO_APP_PASSWORD=hippoPw"
check_not_contains "hippo bootstrap: never carries the core password" "$hippo_call" "s3cret"
check_contains "hippo bootstrap: targets HIPPO_DB_SERVER" "$hippo_call" "hippohost.internal"
check_contains "mammoth bootstrap: uses -N"     "$mammoth_call" "-N"
check_not_contains "mammoth bootstrap: never -C" "$mammoth_call" "-C"
check_contains "mammoth bootstrap: uses -b"     "$mammoth_call" "-b"
check_contains "mammoth bootstrap: carries MAMMOTH_APP_PASSWORD" "$mammoth_call" "MAMMOTH_APP_PASSWORD=mammothPw"
check_not_contains "mammoth bootstrap: never carries the core password" "$mammoth_call" "s3cret"
check_contains "mammoth bootstrap: targets MAMMOTH_DB_SERVER" "$mammoth_call" "mammothhost.internal"

# --- schema script call shape + strict-mode CA wiring (no POL_DESIGN_SQL: sqlcmd -N trusts the OS store) ---
rm -f "$TMPDIR"/probe_count* "$TMPDIR/sqlcmd.log"
out_fallback="$(run_migrate DB_CONNECT_RETRIES=5 DB_CONNECT_RETRY_DELAY_SECONDS=0 2>&1)"
check_contains "schema script fallback: default port" "$(grep 'schema.sql' "$TMPDIR/sqlcmd.log")" "-S dbhost.internal,1433"

rm -f "$TMPDIR"/probe_count* "$TMPDIR/sqlcmd.log" "$TMPDIR/update_ca.log" "$CA_TRUST_DIR/db-tier-ca.crt"
out_strict="$(run_migrate DB_CONNECT_RETRIES=5 DB_CONNECT_RETRY_DELAY_SECONDS=0 DB_CA_CERTIFICATE_FILE="$CA_FILE" DB_PORT=14330 2>&1)"
check_contains "schema script strict: custom port" "$(grep 'schema.sql' "$TMPDIR/sqlcmd.log")" "-S dbhost.internal,14330"

# --- runtime CA install: strict installs the mounted CA into the trust dir, fallback doesn't ---
check_eq "CA install: cert copied into trust dir" "$(cat "$CA_TRUST_DIR/db-tier-ca.crt" 2>/dev/null)" "FAKE-PEM-CA"
check_contains "CA install: update-ca-certificates ran" "$(cat "$TMPDIR/update_ca.log" 2>/dev/null)" "called"
rm -f "$TMPDIR"/probe_count* "$TMPDIR/sqlcmd.log" "$TMPDIR/update_ca.log" "$CA_TRUST_DIR/db-tier-ca.crt"
out_noca="$(run_migrate DB_CONNECT_RETRIES=5 DB_CONNECT_RETRY_DELAY_SECONDS=0 2>&1)"
check_eq "CA install: skipped when DB_CA_CERTIFICATE_FILE unset" "$([ -f "$TMPDIR/update_ca.log" ] && echo ran || echo skipped)" "skipped"
check_eq "CA install: no cert dropped when unset" "$([ -f "$CA_TRUST_DIR/db-tier-ca.crt" ] && echo present || echo absent)" "absent"

check_not_contains "invariant: fallback never exports POL_DESIGN_SQL" "$out_fallback" "Password=saPw"
check_not_contains "invariant: strict never exports POL_DESIGN_SQL"   "$out_strict"   "Password=saPw"

echo ""
echo "pass=$pass fail=$fail"
[ "$fail" -eq 0 ]
