#!/usr/bin/env bash
# migrate-entrypoint.test.sh — script-level tests for docker/migrate-entrypoint.sh's bounded
# DB-reachability retry loop, network-vs-TLS failure classification, and the same DB_PORT/TLS
# connection-string wiring as entrypoint.sh (POL_DESIGN_SQL) with sqlcmd's `-N`/no-`-C` flags.
# รัน: bash docker/migrate-entrypoint.test.sh   (exit 0 = ผ่านครบ)
#
# Approach: stub `sqlcmd` and `dotnet` first on PATH. The sqlcmd stub simulates a configurable
# number of reachability-probe failures (network or TLS flavored, via env) before succeeding,
# and always succeeds for the bootstrap (-i ...) invocation. The dotnet stub intercepts
# `dotnet ef database update` and prints $POL_DESIGN_SQL so the connection string is observable
# without a live SQL Server.
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
# reachability-probe call (-Q "SELECT 1")
COUNT_FILE="$SQLCMD_PROBE_COUNT_FILE"
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
echo "$POL_DESIGN_SQL"
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
        MSSQL_SA_PASSWORD="saPw" \
        POL_APP_PASSWORD_FILE="$PW_FILE" \
        SQLCMD_LOG="$TMPDIR/sqlcmd.log" \
        SQLCMD_PROBE_COUNT_FILE="$TMPDIR/probe_count" \
        CA_TRUST_DIR="$CA_TRUST_DIR" \
        UPDATE_CA_LOG="$TMPDIR/update_ca.log" \
        "$@" \
        sh "$SCRIPT"
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

# --- reachable on first attempt: proceeds immediately, exits 0 ---
rm -f "$TMPDIR/probe_count" "$TMPDIR/sqlcmd.log"
out_ok="$(run_migrate DB_CONNECT_RETRIES=5 DB_CONNECT_RETRY_DELAY_SECONDS=0 2>&1)"
rc_ok=$?
check_eq "reachable: exit 0" "$rc_ok" "0"
check_contains "reachable: proceeds to migrations" "$out_ok" "[migrate] done."
check_contains "reachable: single probe attempt" "$(cat "$TMPDIR/probe_count")" "1"

# --- unreachable for the whole bounded window: exits non-zero, attempts logged ---
rm -f "$TMPDIR/probe_count" "$TMPDIR/sqlcmd.log"
out_unreachable="$(run_migrate DB_CONNECT_RETRIES=3 DB_CONNECT_RETRY_DELAY_SECONDS=0 SQLCMD_FAIL_TIMES=999 SQLCMD_FAIL_MODE=network 2>&1)"
rc_unreachable=$?
check_eq "unreachable: non-zero exit" "$([ "$rc_unreachable" -ne 0 ] && echo yes || echo no)" "yes"
check_contains "unreachable: bounded attempts logged" "$out_unreachable" "unreachable after 3 attempts"
check_eq "unreachable: exactly DB_CONNECT_RETRIES probes" "$(cat "$TMPDIR/probe_count")" "3"
check_not_contains "unreachable: never reaches migrations" "$out_unreachable" "[migrate] done."

# --- exhaustion log classifies network-unreachable vs TLS-validation failure ---
check_contains "network failure classified" "$out_unreachable" "network unreachable"
check_not_contains "network failure not misclassified as TLS" "$out_unreachable" "TLS validation failure"

rm -f "$TMPDIR/probe_count" "$TMPDIR/sqlcmd.log"
out_tls="$(run_migrate DB_CONNECT_RETRIES=2 DB_CONNECT_RETRY_DELAY_SECONDS=0 SQLCMD_FAIL_TIMES=999 SQLCMD_FAIL_MODE=tls 2>&1)"
check_contains "TLS failure classified" "$out_tls" "TLS validation failure"
check_not_contains "TLS failure not misclassified as network" "$out_tls" "unreachable after 2 attempts: network"

# --- succeeds after transient failures, within the bounded window ---
rm -f "$TMPDIR/probe_count" "$TMPDIR/sqlcmd.log"
out_recovers="$(run_migrate DB_CONNECT_RETRIES=5 DB_CONNECT_RETRY_DELAY_SECONDS=0 SQLCMD_FAIL_TIMES=2 2>&1)"
rc_recovers=$?
check_eq "recovers: exit 0 after transient failures" "$rc_recovers" "0"
check_contains "recovers: reaches migrations" "$out_recovers" "[migrate] done."

# --- sqlcmd invoked with -N, never with the -C blind-trust flag ---
sqlcmd_log="$(cat "$TMPDIR/sqlcmd.log")"
check_contains "sqlcmd: uses -N" "$sqlcmd_log" "-N"
check_not_contains "sqlcmd: never -C" "$sqlcmd_log" "-C"

# --- simulated upstream bootstrap (products-sp-gateway REQ-3.2): after the principal script, same
# TLS flags, and with -b so a failed self-check inside the SQL stops the deploy ---
sim_call="$(grep -- '02-external-sim.sql' "$TMPDIR/sqlcmd.log" | head -1)"
principals_line="$(grep -n -- '01-principals.sql' "$TMPDIR/sqlcmd.log" | head -1 | cut -d: -f1)"
sim_line="$(grep -n -- '02-external-sim.sql' "$TMPDIR/sqlcmd.log" | head -1 | cut -d: -f1)"
check_contains "sim bootstrap: 02-external-sim.sql runs" "$sqlcmd_log" "02-external-sim.sql"
check_eq "sim bootstrap: runs AFTER 01-principals.sql" \
    "$([ -n "$principals_line" ] && [ -n "$sim_line" ] && [ "$sim_line" -gt "$principals_line" ] && echo yes || echo no)" "yes"
check_contains "sim bootstrap: uses -N"     "$sim_call" "-N"
check_not_contains "sim bootstrap: never -C" "$sim_call" "-C"
check_contains "sim bootstrap: uses -b"     "$sim_call" "-b"

# --- POL_DESIGN_SQL wiring: same DB_PORT/TLS shape as entrypoint.sh, trust flag never True ---
rm -f "$TMPDIR/probe_count" "$TMPDIR/sqlcmd.log"
out_fallback="$(run_migrate DB_CONNECT_RETRIES=5 DB_CONNECT_RETRY_DELAY_SECONDS=0 2>&1)"
check_contains "POL_DESIGN_SQL fallback: default port"     "$out_fallback" "Server=dbhost.internal,1433"
check_contains "POL_DESIGN_SQL fallback: encrypt+no-trust" "$out_fallback" "Encrypt=True;TrustServerCertificate=False"
check_not_contains "POL_DESIGN_SQL fallback: no Strict"    "$out_fallback" "Encrypt=Strict"

rm -f "$TMPDIR/probe_count" "$TMPDIR/sqlcmd.log" "$TMPDIR/update_ca.log" "$CA_TRUST_DIR/db-tier-ca.crt"
out_strict="$(run_migrate DB_CONNECT_RETRIES=5 DB_CONNECT_RETRY_DELAY_SECONDS=0 DB_CA_CERTIFICATE_FILE="$CA_FILE" DB_PORT=14330 2>&1)"
check_contains "POL_DESIGN_SQL strict: custom port"           "$out_strict" "Server=dbhost.internal,14330"
check_contains "POL_DESIGN_SQL strict: Encrypt=Strict"        "$out_strict" "Encrypt=Strict"
check_contains "POL_DESIGN_SQL strict: ServerCertificate="    "$out_strict" "ServerCertificate=$CA_FILE"
check_not_contains "POL_DESIGN_SQL strict: no bare Certificate= keyword" "$out_strict" ";Certificate="
check_contains "POL_DESIGN_SQL strict: HostNameInCertificate" "$out_strict" "HostNameInCertificate=dbhost.internal"

# --- runtime CA install: strict installs the mounted CA into the trust dir, fallback doesn't ---
check_eq "CA install: cert copied into trust dir" "$(cat "$CA_TRUST_DIR/db-tier-ca.crt" 2>/dev/null)" "FAKE-PEM-CA"
check_contains "CA install: update-ca-certificates ran" "$(cat "$TMPDIR/update_ca.log" 2>/dev/null)" "called"
rm -f "$TMPDIR/probe_count" "$TMPDIR/sqlcmd.log" "$TMPDIR/update_ca.log" "$CA_TRUST_DIR/db-tier-ca.crt"
out_noca="$(run_migrate DB_CONNECT_RETRIES=5 DB_CONNECT_RETRY_DELAY_SECONDS=0 2>&1)"
check_eq "CA install: skipped when DB_CA_CERTIFICATE_FILE unset" "$([ -f "$TMPDIR/update_ca.log" ] && echo ran || echo skipped)" "skipped"
check_eq "CA install: no cert dropped when unset" "$([ -f "$CA_TRUST_DIR/db-tier-ca.crt" ] && echo present || echo absent)" "absent"

never_true_needle="TrustServerCertificate="
never_true_needle="${never_true_needle}True"
check_not_contains "invariant: fallback never has trust flag True" "$out_fallback" "$never_true_needle"
check_not_contains "invariant: strict never has trust flag True"   "$out_strict"   "$never_true_needle"

echo ""
echo "pass=$pass fail=$fail"
[ "$fail" -eq 0 ]
