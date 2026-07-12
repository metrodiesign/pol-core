#!/bin/sh
# Host entrypoint: build the DB connection string(s) from the mounted password secret + this host's principal,
# then launch the app. Each host reads only the keys it needs (Api: App; Worker: Worker) — setting both
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
export ConnectionStrings__App="$CONN"
export ConnectionStrings__Worker="$CONN"
unset DB_PW

# The API also provisions merchants CROSS-MERCHANT under pol_admin on a SEPARATE connection — pol_admin is NOT
# an RLS-bypass role member (T5): it reaches other merchants' rows via the Super/Scoped branches of
# sec.fn_merchant_predicate (a Scoped admin only as far as admin.MerchantAccess allows), not a blanket
# bypass. The pol_app principal above stays RLS-filtered to its own merchant. When an admin password file is
# mounted, build that connection too (the API fail-fasts at boot without it); the Worker mounts none and
# skips this. A distinct principal, so it cannot be the same $CONN as App.
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

# Same for the merchant-user BFF login (its own isolated confidential OIDC client, distinct scheme/cookie names
# from admin — see MerchantUserOidcOptions). A blank ClientId skips the scheme rather than failing boot.
if [ -n "${MERCHANT_USER_OIDC_CLIENT_SECRET_FILE:-}" ]; then
    export MerchantUser__Oidc__ClientSecret="$(cat "$MERCHANT_USER_OIDC_CLIENT_SECRET_FILE")"
fi

exec dotnet "$HOST_DLL"
