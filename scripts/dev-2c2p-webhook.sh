#!/usr/bin/env bash
# Dev-only: replays a 2C2P backend webhook against the LOCAL API after you have paid (or want to
# poke) a payment session on the 2C2P sandbox. 2C2P's servers cannot reach localhost, so the
# backendReturnUrl handed out in dev never fires — this script delivers the same signed
# notification by hand. It is safe by construction: the webhook handler fetch-to-confirms with the
# real 2C2P paymentInquiry API before any state transition, so a replayed "paid" for an unpaid
# invoice is Ignored, a repeat of a processed one is Duplicate.
#
# Usage:
#   export TWOCTWOP_MERCHANT_ID=<2c2p sandbox merchantID>
#   export TWOCTWOP_SECRET_KEY=<2c2p sandbox secret key>   # never committed, never logged
#   ./scripts/dev-2c2p-webhook.sh <pspConnectionId> <paymentSessionId> [respCode]
#
# <paymentSessionId> is the txn.PaymentSessions.Id GUID; the invoiceNo 2C2P knows is that id
# without dashes (TwoCTwoPAdapter uses session.Id.ToString("N")). respCode defaults to 0000.
set -euo pipefail

API_BASE="${POL_API_BASE:-http://localhost:5100}"

if [[ $# -lt 2 ]]; then
    echo "usage: $0 <pspConnectionId> <paymentSessionId> [respCode]" >&2
    exit 1
fi
if [[ -z "${TWOCTWOP_MERCHANT_ID:-}" || -z "${TWOCTWOP_SECRET_KEY:-}" ]]; then
    echo "dev-2c2p-webhook: set TWOCTWOP_MERCHANT_ID and TWOCTWOP_SECRET_KEY in the environment first." >&2
    exit 1
fi

CONNECTION_ID="$1"
INVOICE_NO="$(echo "$2" | tr -d '-' | tr '[:upper:]' '[:lower:]')"
RESP_CODE="${3:-0000}"
# Fresh tranRef per run so the event-id idempotency key never collides with a previous replay;
# the charge+status key still dedupes a genuinely repeated outcome.
TRAN_REF="dev-replay-$(date +%s)-$RANDOM"

BODY="$(python3 - "$CONNECTION_ID" "$INVOICE_NO" "$RESP_CODE" "$TRAN_REF" <<'PY'
import base64, hashlib, hmac, json, os, sys

def b64url(raw: bytes) -> str:
    return base64.urlsafe_b64encode(raw).rstrip(b"=").decode()

_, invoice_no, resp_code, tran_ref = sys.argv[1:5]
header = b64url(json.dumps({"alg": "HS256", "typ": "JWT"}).encode())
claims = b64url(json.dumps({
    "merchantID": os.environ["TWOCTWOP_MERCHANT_ID"],
    "invoiceNo": invoice_no,
    "respCode": resp_code,
    "tranRef": tran_ref,
}).encode())
signing_input = f"{header}.{claims}".encode()
sig = b64url(hmac.new(os.environ["TWOCTWOP_SECRET_KEY"].encode(), signing_input, hashlib.sha256).digest())
print(json.dumps({"payload": f"{header}.{claims}.{sig}"}))
PY
)"

echo "dev-2c2p-webhook: POST $API_BASE/api/v1/webhooks/$CONNECTION_ID (invoiceNo=$INVOICE_NO respCode=$RESP_CODE tranRef=$TRAN_REF)"
curl -sS -X POST "$API_BASE/api/v1/webhooks/$CONNECTION_ID" \
    -H "Content-Type: application/json" \
    -d "$BODY" \
    -w "\nHTTP %{http_code}\n"
