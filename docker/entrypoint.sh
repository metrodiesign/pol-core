#!/bin/sh
# Host entrypoint: build the DB connection string(s) from the mounted password secret + this host's
# principal, then launch the app. The password is read from a file secret and never enters the image, the
# compose file, or `docker inspect`.
# The vault master key is NOT handled here: PR4's keyring reads it directly from Vault__Keys__<id>__KeyFile.
set -eu

: "${DB_SERVER:?set DB_SERVER}"
: "${DB_NAME:?set DB_NAME}"
: "${DB_PRINCIPAL:?set DB_PRINCIPAL}"
: "${DB_PASSWORD_FILE:?set DB_PASSWORD_FILE (mounted secret)}"
: "${HOST_DLL:?set HOST_DLL}"

# Assembles one SQL Server connection string. Trust mode is not operator-configurable: a pinned CA cert
# (DB_CA_CERTIFICATE_FILE, read from the environment directly — the same CA pins every DB tier, there is
# only one) gets Encrypt=Strict validation against it; otherwise Encrypt=True;TrustServerCertificate=False
# (OS trust store). No env var can make the trust flag be True.
build_conn() { # $1=server $2=port $3=db $4=user $5=password
    conn_server="$1"; conn_port="$2"; conn_db="$3"; conn_user="$4"; conn_pw="$5"
    if [ -n "${DB_CA_CERTIFICATE_FILE:-}" ]; then
        printf '%s\n' "Server=${conn_server},${conn_port};Database=${conn_db};User Id=${conn_user};Password=${conn_pw};Encrypt=Strict;ServerCertificate=${DB_CA_CERTIFICATE_FILE};HostNameInCertificate=${conn_server}"
    else
        printf '%s\n' "Server=${conn_server},${conn_port};Database=${conn_db};User Id=${conn_user};Password=${conn_pw};Encrypt=True;TrustServerCertificate=False"
    fi
}

DB_PW="$(cat "$DB_PASSWORD_FILE")"
: "${DB_PORT:=1433}"
export ConnectionStrings__App="$(build_conn "$DB_SERVER" "$DB_PORT" "$DB_NAME" "$DB_PRINCIPAL" "$DB_PW")"

# external-sim-separate-containers: hippodb/mammothdb each run on their own SQL Server instance now, so
# their connection strings are assembled here too — no derive/fallback in application code anymore
# (supersedes products-sp-gateway REQ-3.4). Each side uses its OWN principal and its OWN mounted password
# secret (hippo_app / mammoth_app, sim-db-separate-logins) — never DB_PRINCIPAL/DB_PW, which belong to
# VCentralPay alone. A missing secret file fails the container before the app starts, exactly like
# DB_PASSWORD_FILE above; the `:?` sits inside each branch so an image run standalone (no sim server
# configured) needs no sim secret at all.
# HIPPO_DB_SERVER/MAMMOTH_DB_SERVER are required by docker-compose.prod.yml (`:?`, fails fast at render
# time), but this script still tolerates them being unset — e.g. this image run standalone, outside that
# compose file: the host still boots (products-sp-gateway REQ-5.7, no .ValidateOnStart()) and a products
# search request gets 503 instead of failing boot.
#
# Precedence when SpDocument__MotorConnectionString/__NonMotorConnectionString are ALSO pre-set (e.g. an
# operator pointing at the real motordb/centerdb upstream): this script no longer silently overwrites them.
# Both set in the same pair (non-empty) -> stderr names both env vars and the container exits non-zero
# without exec'ing the app. Only one side of a pair set -> the pre-set value wins, the sim side is left
# alone (hybrid cutover, one side at a time, works). An empty string counts as unset on both sides, same
# as the rest of this file. This guard alone does not make cutover a two-value config change — see
# SpDocumentOptions.cs for the other three layers (compose plumbing, `:?`, migrate bootstrap) that still
# gate it.
if [ -n "${HIPPO_DB_SERVER:-}" ]; then
    if [ -n "${SpDocument__MotorConnectionString:-}" ]; then
        printf '%s\n' "entrypoint: both HIPPO_DB_SERVER and SpDocument__MotorConnectionString are set — unset one, the entrypoint cannot tell which should win" >&2
        exit 1
    fi
    : "${HIPPO_DB_PORT:=1433}"
    : "${HIPPO_APP_PASSWORD_FILE:?set HIPPO_APP_PASSWORD_FILE (mounted secret) when HIPPO_DB_SERVER is set}"
    HIPPO_PW="$(cat "$HIPPO_APP_PASSWORD_FILE")"
    export SpDocument__MotorConnectionString="$(build_conn "$HIPPO_DB_SERVER" "$HIPPO_DB_PORT" "hippodb" "hippo_app" "$HIPPO_PW")"
    unset HIPPO_PW
fi
if [ -n "${MAMMOTH_DB_SERVER:-}" ]; then
    if [ -n "${SpDocument__NonMotorConnectionString:-}" ]; then
        printf '%s\n' "entrypoint: both MAMMOTH_DB_SERVER and SpDocument__NonMotorConnectionString are set — unset one, the entrypoint cannot tell which should win" >&2
        exit 1
    fi
    : "${MAMMOTH_DB_PORT:=1433}"
    : "${MAMMOTH_APP_PASSWORD_FILE:?set MAMMOTH_APP_PASSWORD_FILE (mounted secret) when MAMMOTH_DB_SERVER is set}"
    MAMMOTH_PW="$(cat "$MAMMOTH_APP_PASSWORD_FILE")"
    export SpDocument__NonMotorConnectionString="$(build_conn "$MAMMOTH_DB_SERVER" "$MAMMOTH_DB_PORT" "mammothdb" "mammoth_app" "$MAMMOTH_PW")"
    unset MAMMOTH_PW
fi
unset DB_PW

# Microsoft Entra clients use one mounted secret per plane; Admin is required in Production, Merchant is optional.
if [ -n "${ADMIN_ENTRA_CLIENT_SECRET_FILE:-}" ]; then
    export AdminAuth__Providers__Microsoft__ClientSecret="$(cat "$ADMIN_ENTRA_CLIENT_SECRET_FILE")"
fi
if [ -n "${MERCHANT_ENTRA_CLIENT_SECRET_FILE:-}" ]; then
    export MerchantAuth__Providers__Microsoft__ClientSecret="$(cat "$MERCHANT_ENTRA_CLIENT_SECRET_FILE")"
fi

exec dotnet "$HOST_DLL"
