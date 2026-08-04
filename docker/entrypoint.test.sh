#!/usr/bin/env bash
# entrypoint.test.sh — script-level tests for docker/entrypoint.sh's DB connection-string
# assembly (DB_PORT, Encrypt=Strict pinned-cert branch, Encrypt=True/TrustServerCertificate=False
# fallback branch, and the invariant that the trust flag is never True).
# รัน: bash docker/entrypoint.test.sh   (exit 0 = ผ่านครบ)
#
# Approach: entrypoint.sh ends with `exec dotnet "$HOST_DLL"`, replacing the process before any
# assertion could run. A stub `dotnet` placed first on PATH intercepts that exec and prints
# $ConnectionStrings__App / $SpDocument__MotorConnectionString / $SpDocument__NonMotorConnectionString
# instead of launching the real runtime, so the built connection string(s) are observable without a
# live SQL Server or a real dotnet install.
set -u

SCRIPT="$(cd "$(dirname "$0")" && pwd)/entrypoint.sh"
TMPDIR="$(mktemp -d)"
trap 'rm -rf "$TMPDIR"' EXIT

STUB_BIN="$TMPDIR/bin"
mkdir -p "$STUB_BIN"
cat >"$STUB_BIN/dotnet" <<'EOF'
#!/bin/sh
printf '%s\n' "$ConnectionStrings__App"
printf '%s\n' "$SpDocument__MotorConnectionString"
printf '%s\n' "$SpDocument__NonMotorConnectionString"
EOF
chmod +x "$STUB_BIN/dotnet"

PW_FILE="$TMPDIR/db_password"
echo "s3cret" >"$PW_FILE"

pass=0
fail=0

run_entrypoint() { # extra env assignments as $@, e.g. DB_CA_CERTIFICATE_FILE=/x DB_PORT=14330
    env -i \
        PATH="$STUB_BIN:$PATH" \
        DB_SERVER="dbhost.internal" \
        DB_NAME="AppDb" \
        DB_PRINCIPAL="pol_app" \
        DB_PASSWORD_FILE="$PW_FILE" \
        HOST_DLL="Api.dll" \
        "$@" \
        sh "$SCRIPT"
}

check_contains() { # $1=desc $2=haystack $3=needle
    if printf '%s' "$2" | grep -qF "$3"; then
        pass=$((pass + 1))
    else
        fail=$((fail + 1)); echo "FAIL [$1] expected to contain: $3 :: got: $2"
    fi
}

check_not_contains() { # $1=desc $2=haystack $3=needle
    if printf '%s' "$2" | grep -qF "$3"; then
        fail=$((fail + 1)); echo "FAIL [$1] must NOT contain: $3 :: got: $2"
    else
        pass=$((pass + 1))
    fi
}

check_empty() { # $1=desc $2=value
    if [ -z "$2" ]; then
        pass=$((pass + 1))
    else
        fail=$((fail + 1)); echo "FAIL [$1] expected empty :: got: $2"
    fi
}

check_eq() { # $1=desc $2=actual $3=expected
    if [ "$2" = "$3" ]; then
        pass=$((pass + 1))
    else
        fail=$((fail + 1)); echo "FAIL [$1] expected [$3] got [$2]"
    fi
}

# --- branch: no DB_CA_CERTIFICATE_FILE -> Encrypt=True;TrustServerCertificate=False, default port ---
out_fallback="$(run_entrypoint)"
check_contains "fallback: default port"       "$out_fallback" "Server=dbhost.internal,1433"
check_contains "fallback: encrypt+no-trust"   "$out_fallback" "Encrypt=True;TrustServerCertificate=False"
check_not_contains "fallback: no Strict"      "$out_fallback" "Encrypt=Strict"

# --- branch: DB_CA_CERTIFICATE_FILE set -> Encrypt=Strict pinned-cert path ---
out_strict="$(run_entrypoint DB_CA_CERTIFICATE_FILE=/run/secrets/db_ca_cert DB_PORT=14330)"
check_contains "strict: custom port"          "$out_strict" "Server=dbhost.internal,14330"
check_contains "strict: Encrypt=Strict"       "$out_strict" "Encrypt=Strict"
check_contains "strict: ServerCertificate="   "$out_strict" "ServerCertificate=/run/secrets/db_ca_cert"
check_not_contains "strict: no bare Certificate= keyword" "$out_strict" ";Certificate="
check_contains "strict: HostNameInCertificate" "$out_strict" "HostNameInCertificate=dbhost.internal"
check_not_contains "strict: no plain True/False encrypt fallback" "$out_strict" "Encrypt=True"

# --- branch: DB_CA_CERTIFICATE_FILE set but empty string -> falls back, same as unset ---
out_empty_ca="$(run_entrypoint DB_CA_CERTIFICATE_FILE=)"
check_contains "empty CA var: fallback encrypt" "$out_empty_ca" "Encrypt=True;TrustServerCertificate=False"

# --- regression guard: a password containing backslash-escape sequences must survive byte-for-byte.
# --- dash's builtin `echo` is XSI-compliant and interprets these in its argument (\t -> a real tab,
# --- \c -> stop output right there, silently dropping everything after it including the TLS clause).
# --- build_conn uses `printf '%s\n'` instead, which never interprets escapes in a %s argument.
PW_FILE_ESCAPED="$TMPDIR/db_password_escaped"
printf '%s' 'Pa\tsw0rd\cTAIL' >"$PW_FILE_ESCAPED"

out_escaped_fallback="$(run_entrypoint DB_PASSWORD_FILE="$PW_FILE_ESCAPED")"
check_contains "escape-safe fallback: password literal preserved" "$out_escaped_fallback" 'Password=Pa\tsw0rd\cTAIL;'
check_contains "escape-safe fallback: TLS clause survives"        "$out_escaped_fallback" "Encrypt=True;TrustServerCertificate=False"

out_escaped_strict="$(run_entrypoint DB_PASSWORD_FILE="$PW_FILE_ESCAPED" DB_CA_CERTIFICATE_FILE=/run/secrets/db_ca_cert)"
check_contains "escape-safe strict: password literal preserved" "$out_escaped_strict" 'Password=Pa\tsw0rd\cTAIL;'
check_contains "escape-safe strict: TLS clause survives"        "$out_escaped_strict" "Encrypt=Strict;ServerCertificate=/run/secrets/db_ca_cert;HostNameInCertificate=dbhost.internal"

# --- external-sim-separate-containers: HIPPO_DB_SERVER/MAMMOTH_DB_SERVER assemble
# --- SpDocument__MotorConnectionString/__NonMotorConnectionString the same way (build_conn) and
# --- independently of each other; unset -> both stay empty (REQ-5.7 — host still boots, no
# --- derive/fallback in application code).
out_hippo="$(run_entrypoint HIPPO_DB_SERVER=hippo.internal HIPPO_DB_PORT=11434)"
check_contains "hippo set: motor conn string" "$out_hippo" "Server=hippo.internal,11434;Database=hippodb"

motor_unset="$(printf '%s\n' "$out_fallback" | sed -n '2p')"
nonmotor_unset="$(printf '%s\n' "$out_fallback" | sed -n '3p')"
check_empty "hippo/mammoth unset: motor conn empty"    "$motor_unset"
check_empty "hippo/mammoth unset: nonmotor conn empty" "$nonmotor_unset"

out_mammoth="$(run_entrypoint MAMMOTH_DB_SERVER=mammoth.internal MAMMOTH_DB_PORT=19999)"
check_contains "mammoth set: custom port in nonmotor conn string" "$out_mammoth" "Server=mammoth.internal,19999;Database=mammothdb"

# --- external-sim strict branch: HIPPO_DB_SERVER/MAMMOTH_DB_SERVER paired with DB_CA_CERTIFICATE_FILE
# --- must assemble Encrypt=Strict the same way the App connection string does — build_conn is the same
# --- function for both, but no case had ever exercised this branch for the sim strings before. (F8)
out_sim_strict="$(run_entrypoint HIPPO_DB_SERVER=hippo.internal MAMMOTH_DB_SERVER=mammoth.internal DB_CA_CERTIFICATE_FILE=/run/secrets/db_ca_cert)"
motor_sim_strict="$(printf '%s\n' "$out_sim_strict" | sed -n '2p')"
nonmotor_sim_strict="$(printf '%s\n' "$out_sim_strict" | sed -n '3p')"
check_contains "sim strict: hippo Encrypt=Strict"        "$motor_sim_strict" "Encrypt=Strict"
check_contains "sim strict: hippo ServerCertificate="    "$motor_sim_strict" "ServerCertificate=/run/secrets/db_ca_cert"
check_contains "sim strict: hippo HostNameInCertificate" "$motor_sim_strict" "HostNameInCertificate=hippo.internal"
check_not_contains "sim strict: hippo no plain True/False encrypt fallback" "$motor_sim_strict" "Encrypt=True"
check_contains "sim strict: mammoth Encrypt=Strict"        "$nonmotor_sim_strict" "Encrypt=Strict"
check_contains "sim strict: mammoth ServerCertificate="    "$nonmotor_sim_strict" "ServerCertificate=/run/secrets/db_ca_cert"
check_contains "sim strict: mammoth HostNameInCertificate" "$nonmotor_sim_strict" "HostNameInCertificate=mammoth.internal"
check_not_contains "sim strict: mammoth no plain True/False encrypt fallback" "$nonmotor_sim_strict" "Encrypt=True"

# --- D1: entrypoint must not silently overwrite an operator-set SpDocument__* value with the
# --- sim-assembled one — HIPPO_DB_SERVER is paired with SpDocument__MotorConnectionString,
# --- MAMMOTH_DB_SERVER with SpDocument__NonMotorConnectionString. Both non-empty in the same pair
# --- must refuse (stderr names both vars, no values) and exit non-zero; only one set must let the
# --- pre-set value win (hybrid cutover, one side at a time). Empty string counts as unset on both
# --- sides, same norm as :20's `[ -n "${VAR:-}" ]`. (F1-F5)
motor_preset='Server=motordb.real,1433;Database=motordb;User Id=up;Password=x;Encrypt=True;TrustServerCertificate=False'
nonmotor_preset='Server=centerdb.real,1433;Database=centerdb;User Id=up;Password=y;Encrypt=True;TrustServerCertificate=False'

# F1: Motor pre-set collides with HIPPO_DB_SERVER -> refuse, non-zero exit, stderr names both vars only
out_motor_conflict="$(run_entrypoint HIPPO_DB_SERVER=hippo.internal SpDocument__MotorConnectionString="$motor_preset" 2>&1)"
rc_motor_conflict=$?
check_eq "motor conflict: non-zero exit" "$([ "$rc_motor_conflict" -ne 0 ] && echo yes || echo no)" "yes"
check_contains "motor conflict: stderr names HIPPO_DB_SERVER" "$out_motor_conflict" "HIPPO_DB_SERVER"
check_contains "motor conflict: stderr names SpDocument__MotorConnectionString" "$out_motor_conflict" "SpDocument__MotorConnectionString"
check_not_contains "motor conflict: stderr has no password value (F5)" "$out_motor_conflict" "Password=x"
check_not_contains "motor conflict: stderr has no connection string value (F5)" "$out_motor_conflict" "motordb.real"
check_not_contains "motor conflict: stderr has no real DB_PW secret (F5)" "$out_motor_conflict" "s3cret"

# F2: NonMotor pre-set collides with MAMMOTH_DB_SERVER -> same guard, its own pair
out_nonmotor_conflict="$(run_entrypoint MAMMOTH_DB_SERVER=mammoth.internal SpDocument__NonMotorConnectionString="$nonmotor_preset" 2>&1)"
rc_nonmotor_conflict=$?
check_eq "nonmotor conflict: non-zero exit" "$([ "$rc_nonmotor_conflict" -ne 0 ] && echo yes || echo no)" "yes"
check_contains "nonmotor conflict: stderr names MAMMOTH_DB_SERVER" "$out_nonmotor_conflict" "MAMMOTH_DB_SERVER"
check_contains "nonmotor conflict: stderr names SpDocument__NonMotorConnectionString" "$out_nonmotor_conflict" "SpDocument__NonMotorConnectionString"
check_not_contains "nonmotor conflict: stderr has no password value (F5)" "$out_nonmotor_conflict" "Password=y"
check_not_contains "nonmotor conflict: stderr has no connection string value (F5)" "$out_nonmotor_conflict" "centerdb.real"
check_not_contains "nonmotor conflict: stderr has no real DB_PW secret (F5)" "$out_nonmotor_conflict" "s3cret"

# F3: hybrid cutover — Motor pre-set alone (HIPPO_DB_SERVER unset) must win untouched, NonMotor still
# assembles from sim via MAMMOTH_DB_SERVER since that pair has no conflict
out_hybrid="$(run_entrypoint MAMMOTH_DB_SERVER=mammoth.internal SpDocument__MotorConnectionString="$motor_preset" 2>&1)"
rc_hybrid=$?
check_eq "hybrid: exit 0" "$rc_hybrid" "0"
motor_hybrid="$(printf '%s\n' "$out_hybrid" | sed -n '2p')"
nonmotor_hybrid="$(printf '%s\n' "$out_hybrid" | sed -n '3p')"
check_eq "hybrid: motor keeps operator value untouched" "$motor_hybrid" "$motor_preset"
check_contains "hybrid: nonmotor still assembled from sim" "$nonmotor_hybrid" "Server=mammoth.internal,1433;Database=mammothdb"

# F4: empty string counts as unset — empty SpDocument__MotorConnectionString must not block sim assembly
out_empty_override="$(run_entrypoint HIPPO_DB_SERVER=hippo.internal SpDocument__MotorConnectionString= 2>&1)"
rc_empty_override=$?
check_eq "empty override: exit 0" "$rc_empty_override" "0"
motor_empty_override="$(printf '%s\n' "$out_empty_override" | sed -n '2p')"
check_contains "empty override: motor still assembled from sim" "$motor_empty_override" "Server=hippo.internal,1433;Database=hippodb"

# F4: empty string counts as unset — empty HIPPO_DB_SERVER must not block the pre-set Motor value from winning
out_empty_server="$(run_entrypoint HIPPO_DB_SERVER= SpDocument__MotorConnectionString="$motor_preset" 2>&1)"
rc_empty_server=$?
check_eq "empty HIPPO_DB_SERVER: exit 0" "$rc_empty_server" "0"
motor_empty_server="$(printf '%s\n' "$out_empty_server" | sed -n '2p')"
check_eq "empty HIPPO_DB_SERVER: motor keeps operator value" "$motor_empty_server" "$motor_preset"

# --- invariant: no input combination can make the trust flag be True ---
never_true_needle="TrustServerCertificate="
never_true_needle="${never_true_needle}True"
check_not_contains "invariant: fallback branch"  "$out_fallback"  "$never_true_needle"
check_not_contains "invariant: strict branch"    "$out_strict"    "$never_true_needle"
check_not_contains "invariant: empty-CA branch"  "$out_empty_ca"  "$never_true_needle"
check_not_contains "invariant: hippo branch"     "$out_hippo"     "$never_true_needle"
check_not_contains "invariant: mammoth branch"   "$out_mammoth"   "$never_true_needle"

echo ""
echo "pass=$pass fail=$fail"
[ "$fail" -eq 0 ]
