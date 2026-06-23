#!/bin/sh
# Host entrypoint: build the DB connection string(s) from the mounted password secret + this host's principal,
# then launch the app. Each host reads only the keys it needs (Api: Producer; Worker: Worker) — setting both
# to the same principal connection is harmless. The password is read from a file secret and never enters the
# image, the compose file, or `docker inspect`.
# The vault master key is NOT handled here: PR4's keyring reads it directly from Vault__Keys__<id>__KeyFile.
set -eu

: "${DB_SERVER:?set DB_SERVER}"
: "${DB_NAME:?set DB_NAME}"
: "${DB_PRINCIPAL:?set DB_PRINCIPAL}"
: "${DB_PASSWORD_FILE:?set DB_PASSWORD_FILE (mounted secret)}"
: "${HOST_DLL:?set HOST_DLL}"

DB_PW="$(cat "$DB_PASSWORD_FILE")"
# ponytail: TrustServerCertificate=True suits a self-signed SQL cert; for real prod issue a trusted cert and
# set it False (Encrypt stays True). Kept True so the scaffold works out of the box.
CONN="Server=${DB_SERVER};Database=${DB_NAME};User Id=${DB_PRINCIPAL};Password=${DB_PW};Encrypt=True;TrustServerCertificate=True"
export ConnectionStrings__Producer="$CONN"
export ConnectionStrings__Worker="$CONN"
unset DB_PW

# The API also provisions tenants CROSS-TENANT under pol_admin (RLS bypass) on a SEPARATE connection — the
# pol_app principal above is RLS-blocked from writing another tenant's rows. When an admin password file is
# mounted, build that connection too (the API fail-fasts at boot without it); the Worker mounts none and
# skips this. A distinct principal, so it cannot be the same $CONN as Producer.
if [ -n "${ADMIN_DB_PASSWORD_FILE:-}" ]; then
    ADMIN_PW="$(cat "$ADMIN_DB_PASSWORD_FILE")"
    export ConnectionStrings__Admin="Server=${DB_SERVER};Database=${DB_NAME};User Id=${DB_ADMIN_PRINCIPAL:-pol_admin};Password=${ADMIN_PW};Encrypt=True;TrustServerCertificate=True"
    unset ADMIN_PW
fi

# The admin BFF login is a confidential Google OIDC client: export its client secret from the mounted file
# secret so it never enters the image, the compose file, or `docker inspect` (REQ-8.1). The API fail-fasts at
# boot (outside Development) when it is unset.
if [ -n "${ADMIN_OIDC_CLIENT_SECRET_FILE:-}" ]; then
    export Google__Oidc__ClientSecret="$(cat "$ADMIN_OIDC_CLIENT_SECRET_FILE")"
fi

exec dotnet "$HOST_DLL"
