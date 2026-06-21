#!/usr/bin/env bash
# destructive-guard.sh — thin Claude adapter: jq stdin payload -> .ai/bin/check-destructive.sh (exit 2 = block)
C=$(jq -r '.tool_input.command // empty')
[ -n "$C" ] || exit 0
exec "$(dirname "$0")/../../.ai/bin/check-destructive.sh" "$C"
